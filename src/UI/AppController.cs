using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using MacDesk.Core;

namespace MacDeskNative {
 public class AppController : ApplicationContext {
  // The backend. All data changes go through it; its Changed event refreshes every view.
  public DeskService Service { get; private set; }
  public DesktopData Data { get { return Service.Data; } }
  public HomeForm Home { get; private set; }
  public DesktopFolderForm DesktopFolders { get; private set; }
  public bool ModalInProgress { get { return modalDepth > 0; } }
  int modalDepth;
  public FolderLayer Panel { get; private set; }

  NotifyIcon tray;
  bool closing, refreshQueued, testMode;
  BackdropOverlay dim;
  Backdrop backdrop;

  // Copy the desktop before the panel exists. A worker prepares its blur independently of the
  // click; opening can present immediately even if the worker is still running.
  Task<Backdrop> prefetch;
  Rectangle prefetchArea;
  DateTime prefetchedAt;
  internal void PrefetchBackdrop(Point screenPoint) {
   if (closing || testMode || (dim != null && !dim.IsDisposed)) return;
   Rectangle work = Screen.FromPoint(screenPoint).WorkingArea;
   DiscardPrefetch();
   prefetchArea = work;
   prefetchedAt = DateTime.UtcNow;
   prefetch = StartBackdrop(work);
  }
  static Task<Backdrop> StartBackdrop(Rectangle area) {
   Bitmap shot;
   try { shot = Backdrop.CaptureScreen(area); } catch { return null; }
   try { return Task.Factory.StartNew(() => { using (shot) return Backdrop.FromCapture(shot, area); }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default); }
   catch { shot.Dispose(); return null; }
  }
  Task<Backdrop> TakePrefetch(Rectangle area) {
   var task = prefetch;
   prefetch = null;
   if (task == null) return null;
   if (prefetchArea == area && (DateTime.UtcNow - prefetchedAt).TotalMilliseconds < 2500) return task;
   DisposeWhenDone(task);
   return null;
  }
  internal void DiscardPrefetch() { var task = prefetch; prefetch = null; if (task != null) DisposeWhenDone(task); }
  static Backdrop CompletedBackdrop(Task<Backdrop> task) {
   if (task.Status == TaskStatus.RanToCompletion) return task.Result;
   if (task.IsFaulted) task.Exception.Handle(delegate { return true; });
   return null;
  }
  static void DisposeWhenDone(Task<Backdrop> task) {
   task.ContinueWith(t => { var image = CompletedBackdrop(t); if (image != null) image.Dispose(); }, TaskScheduler.Default);
  }

  // Blurred, dimmed copy of the screen behind an open folder. Captured once per open, before
  // any of our overlay windows exist, and reused while switching between folders.
  void ShowDim(Rectangle bounds) {
   if (dim != null && !dim.IsDisposed && dim.Bounds != bounds) HideDim();
   if (dim == null || dim.IsDisposed) {
    var task = TakePrefetch(bounds) ?? StartBackdrop(bounds);
    bool ready = task == null || task.IsCompleted;
    backdrop = task != null && ready ? CompletedBackdrop(task) : null;
    try { dim = new BackdropOverlay(bounds, OwnDialogWindow(), backdrop, !ready, delegate { ClosePanel(); }); }
    catch { if (!ready) DisposeWhenDone(task); throw; }
    if (!ready) FinishBackdrop(task, dim);
   }
   if (!dim.Visible) dim.Show();
   dim.SetAlpha(0);
  }
  void FinishBackdrop(Task<Backdrop> task, BackdropOverlay overlay) {
   task.ContinueWith(t => {
    var image = CompletedBackdrop(t);
    try {
     if (closing || dim != overlay || overlay.IsDisposed) return;
     overlay.FinishPreparing(image);
     // The desktop can fade in the blur at any time. Keep the pane's own glass stable once
     // the morph is underway.
     if (image != null && Panel != null && Panel.CanSetOpeningBackdrop) Panel.SetBackdrop(image);
     backdrop = image;
    } catch {
     if (Panel != null && !Panel.IsDisposed) Panel.SetBackdrop(null);
     if (dim == overlay && !overlay.IsDisposed) overlay.FinishPreparing(null);
    } finally { if (image != null && backdrop != image) image.Dispose(); }
   }, TaskScheduler.FromCurrentSynchronizationContext());
  }
  void HideDim() {
   BackdropOverlay old = dim;
   dim = null;
   if (old != null) { old.Hide(); old.Dispose(); }
   if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
   if (Panel != null && !Panel.IsDisposed) Panel.SetBackdrop(null);
   if (DesktopFolders != null && !DesktopFolders.IsDisposed) { DesktopFolders.SetOpenFolder(null); DesktopFolders.ReleaseKeyboardFocus(); }
  }

  // The folder UI never takes activation (that would kick Windows out of "show desktop" and
  // restore every window over it). Keyboard input is received by the desktop-layer window,
  // which lives inside the desktop's own focus, and routed here.
  internal bool RouteKey(Keys key) {
   Keys code = key & Keys.KeyCode;
   bool modifierOnly = code == Keys.ShiftKey || code == Keys.ControlKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin;
   if (Panel == null || Panel.IsDisposed || !Panel.Visible) return false;
   if (Panel.IsClosing) return key == Keys.Escape;
   if (key == Keys.Escape && Panel.IsOpening) { ClosePanel(); return true; }
   if (!modifierOnly) Panel.CompleteOpening(); // typing during the open: land it, then deliver
   return Panel.HandleKey(key);
  }
  internal bool RouteChar(char c) {
   if (Panel == null || Panel.IsDisposed || !Panel.Visible || Panel.IsClosing) return false;
   Panel.CompleteOpening();
   return Panel.HandleChar(c);
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  static void BringToTop(Form f) {
   if (f != null && !f.IsDisposed && f.IsHandleCreated) SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOP, NOSIZE|NOMOVE|NOACTIVATE
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
  public AppController(string root) : this(root, true, true) { }
  public AppController(string root, bool show) : this(root, show, false) { }
  internal AppController(string root, bool show, bool seedIfEmpty) {
   testMode = !show;
   try {
    Service = new DeskService(root);
    Service.Changed += delegate { QueueRefresh(); };
    if (seedIfEmpty) Service.SeedStarterGroupsIfNew();
    // Desktop folders are laid out by order on a fixed grid (like a phone home screen);
    // turn any free positions saved by older versions into that order, once.
    try { Service.MigratePinnedPositionsToOrder(); } catch { }
    Home = new HomeForm(this);
    Home.Text = "MacDesk Desktop";
    Home.FormClosed += delegate { Quit(); };
    Home.HandleCreated += delegate { if (!testMode) NativeDesktop.Lower(Home.Handle); };
    Home.Shown += delegate { NativeDesktop.Lower(Home.Handle); };
    if (show) {
     DesktopFolders = new DesktopFolderForm(this);
     DesktopFolders.FormClosed += delegate { Quit(); };
     DesktopFolders.HandleCreated += delegate { if (!testMode) NativeDesktop.SetCompanion(DesktopFolders.Handle); };
     DesktopFolders.Show();
     NativeDesktop.SetCompanion(DesktopFolders.Handle);
     Home.Show();
     NativeDesktop.Lower(Home.Handle);
     NativeDesktopHost.EnsureChildTopOrder(Home.Handle, DesktopFolders.Handle);
     CreateTray();
     ThreadPool.QueueUserWorkItem(delegate { try { EntryServices.DiscoverSystemApps(); } catch { } });
    }
   } catch { Quit(); throw; }
  }

  protected override void Dispose(bool disposing) { if (disposing) Quit(); base.Dispose(disposing); }
  public void ReportError(Exception ex) { Fail(ex); }
  void Fail(Exception ex) {
   if (testMode) throw ex;
   Modal(owner => MessageBox.Show(owner, ex.Message, "MacDesk · 操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning));
  }

  Form dialogOwner;
  bool HostedHome { get { return Home != null && Home.DesktopChildMode; } }
  sealed class DialogOwnerForm : Form {
   public DialogOwnerForm() { ShowInTaskbar = true; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; TopMost = false; }
   protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle = (p.ExStyle | 0x80) & ~0x40000; return p; } }
  }
  Form OwnDialogWindow() {
   if (dialogOwner == null || dialogOwner.IsDisposed) dialogOwner = new DialogOwnerForm();
   dialogOwner.Bounds = (Home != null && !Home.IsDisposed) ? Screen.FromControl(Home).WorkingArea : Screen.PrimaryScreen.WorkingArea;
   IntPtr ready = dialogOwner.Handle;
   return dialogOwner;
  }
  IWin32Window Owner() {
   if (Panel != null && !Panel.IsDisposed && Panel.Visible && !Panel.IsClosing) return Panel;
   if (Home == null || Home.IsDisposed || HostedHome) return OwnDialogWindow();
   return Home;
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
  [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
  [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
  [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
  [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr hwnd, int index);

  static void OwnPopup(IntPtr popup, IntPtr owner) {
   if ((GetWindowLong32(popup, -16) & 0x40000000) != 0) throw new InvalidOperationException("菜单必须是本程序的独立弹出窗口。");
   if (IntPtr.Size == 8) SetWindowLongPtr64(popup, -8, owner);
   else SetWindowLong32(popup, -8, owner.ToInt32());
   IntPtr actual = IntPtr.Size == 8 ? GetWindowLongPtr64(popup, -8) : new IntPtr(GetWindowLong32(popup, -8));
   if (actual != owner) throw new InvalidOperationException("无法设置本程序菜单的所有者。");
  }
  public void ShowDesktopMenu(ContextMenuStrip menu, Point screenPoint) {
   var owner = OwnDialogWindow();
   OwnPopup(menu.Handle, owner.Handle);
   try {
    menu.Show(owner, owner.PointToClient(screenPoint));
    if (menu.IsDisposed || !menu.Visible || !menu.IsHandleCreated) return;
    IntPtr popup = menu.Handle;
    OwnPopup(popup, owner.Handle);
    SetForegroundWindow(popup);
    if (!menu.IsDisposed && menu.Visible && menu.IsHandleCreated) OwnPopup(popup, owner.Handle);
   } catch { if (!menu.IsDisposed) menu.Close(); throw; }
  }

  T Modal<T>(Func<IWin32Window, T> action) {
   if (Panel != null && !Panel.IsDisposed) Panel.CompleteOpening();
   bool hasHome = Home != null && !Home.IsDisposed;
   bool hosted = hasHome && Home.DesktopChildMode, enabled = hasHome && Home.Enabled, foldersEnabled = DesktopFolders != null && !DesktopFolders.IsDisposed && DesktopFolders.Enabled;
   modalDepth++;
   if (hosted && hasHome) Home.Enabled = false;
   if (DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.Enabled = false;
   try { return action(Owner()); } finally {
    if (hosted && Home != null && !Home.IsDisposed) Home.Enabled = enabled;
    if (DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.Enabled = foldersEnabled;
    modalDepth--;
   }
  }

  DeskGroup Group(string id) {
   var g = Data.groups.Find(v => v.id == id);
   if (g == null) throw new InvalidDataException("分类已变化，请重试。");
   return g;
  }

  void QueueRefresh() {
   if (refreshQueued || closing || Home == null || Home.IsDisposed) return;
   refreshQueued = true;
   Action action = delegate {
    refreshQueued = false;
    if (closing || Home == null || Home.IsDisposed) return;
    Home.RefreshData();
    if (DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.RefreshData();
    if (Panel != null && Panel.Visible) {
     if (Data.groups.Any(g => g.id == Panel.GroupId)) Panel.RefreshData();
     else { Panel.Hide(); HideDim(); }
    }
   };
   if (Home.IsHandleCreated) Home.BeginInvoke(action); else action();
  }

  // Opens folder `id`: the folder layer grows it out of its own desktop icon (sourceCenter is
  // only a fallback when the folder has no desktop tile).
  public void OpenGroup(string id, Point sourceCenter) {
   try {
    Group(id);
    if (Panel != null && Panel.GroupId == id && Panel.Visible && !Panel.IsClosing) { ClosePanel(); return; }
    if (Panel != null && !Panel.IsDisposed && Panel.Visible) Panel.Hide(); // switching folders
    if (Panel == null || Panel.IsDisposed) Panel = new FolderLayer(this, id);
    Rectangle tile = Rectangle.Empty;
    if (DesktopFolders != null && !DesktopFolders.IsDisposed) {
     Rectangle? own = DesktopFolders.IconRectOnScreen(id);
     if (own.HasValue) tile = own.Value;
    }
    if (tile.IsEmpty) tile = new Rectangle(sourceCenter.X - Metrics.DesktopIcon / 2, sourceCenter.Y - Metrics.DesktopIcon / 2, Metrics.DesktopIcon, Metrics.DesktopIcon);
    Rectangle work = Screen.FromPoint(new Point(tile.X + tile.Width / 2, tile.Y + tile.Height / 2)).WorkingArea;
    ShowDim(work); // captures the desktop before showing the folder
    var layer = Panel;
    layer.BackdropAlpha = a => { if (dim != null && !dim.IsDisposed) dim.SetAlpha(a); };
    layer.Collapsed = delegate { if (Panel == layer) HideDim(); };
    layer.Landing = delegate { if (DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.SetOpenFolder(null); };
    Bitmap body = DesktopFolders != null ? DesktopFolders.RenderTileBody(id) : null;
    layer.Open(id, work, tile, backdrop, body);
    if (!layer.Visible) layer.Show(OwnDialogWindow());
    BringToTop(dim);
    BringToTop(layer);
    if (DesktopFolders != null) DesktopFolders.SetOpenFolder(id); // the layer now covers it
    if (DesktopFolders != null) DesktopFolders.TakeKeyboardFocus();
   } catch (Exception ex) { DiscardPrefetch(); if (Panel != null && !Panel.IsDisposed) Panel.Hide(); HideDim(); Fail(ex); }
  }

  public void ClosePanel() {
   if (Panel == null || Panel.IsDisposed || !Panel.Visible || Panel.IsClosing) return;
   Rectangle? tile = DesktopFolders != null && !DesktopFolders.IsDisposed ? DesktopFolders.IconRectOnScreen(Panel.GroupId) : null;
   Panel.CloseFolder(tile);
  }

  // ---- Data actions: thin UI wrappers over the backend (DeskService) ------------------------
  // Each wrapper only adds UI concerns (dialogs, error display, focus/animation follow-up);
  // the rule for what is valid and how it is stored lives in Core/DeskService.cs.

  bool Try(Action action) {
   try { action(); return true; } catch (Exception ex) { Fail(ex); return false; }
  }

  public void SetHomePosition(int x, int y) {
   Rectangle work = Screen.FromControl(Home).WorkingArea;
   int clampedX = Math.Max(0, Math.Min(Math.Max(0, work.Width - Home.Width), x));
   int clampedY = Math.Max(0, Math.Min(Math.Max(0, work.Height - Home.Height), y));
   // Keep the live card where it is saved, so it can't be stranded off-screen.
   if (Home.Location != new Point(clampedX, clampedY)) Home.Location = new Point(clampedX, clampedY);
   if (Data.homeX != clampedX || Data.homeY != clampedY) Try(delegate { Service.SetHomePosition(clampedX, clampedY); });
  }


  public void AddGroup() { var g = Modal(owner => EditDialogs.Group(owner, null)); if (g != null) Try(delegate { Service.AddGroup(g); }); }
  public void CreateAndOpenNewGroup(Point sourceCenter) {
   DeskGroup g = null;
   if (Try(delegate { g = Service.CreateGroupWithUniqueTitle("新文件夹"); })) {
    if (DesktopFolders != null) DesktopFolders.RefreshData();
    OpenGroup(g.id, sourceCenter);
   }
  }
  public bool RenameGroupInline(string groupId, string newTitle) {
   if ((newTitle ?? "").Trim().Length == 0) return false;
   return Try(delegate { Service.RenameGroup(groupId, newTitle); });
  }
  public bool ToggleCandidateInGroup(string groupId, DeskItem candidate) {
   if (candidate == null) return false;
   return Try(delegate { Service.ToggleCandidate(groupId, candidate); });
  }
  public void PickFilesToAdd(string groupId) {
   string[] files = Modal(owner => {
    using (var f = new OpenFileDialog { Title = "选择要添加到文件夹的文件或程序", Multiselect = true, CheckFileExists = true, DereferenceLinks = false }) {
     return f.ShowDialog(owner) == DialogResult.OK ? f.FileNames : null;
    }
   });
   if (files != null && files.Length > 0) TryAddPaths(groupId, files, -1);
  }
  public void PickFolderToAdd(string groupId) {
   string folder = Modal(owner => {
    using (var f = new FolderBrowserDialog { Description = "选择要添加到文件夹的目录" }) {
     return f.ShowDialog(owner) == DialogResult.OK ? f.SelectedPath : null;
    }
   });
   if (!String.IsNullOrEmpty(folder)) TryAddPaths(groupId, new[] { folder }, -1);
  }
  public void EditGroup(string id) {
   try {
    var original = Group(id);
    string title = original.title, subtitle = original.subtitle;
    var g = Modal(owner => EditDialogs.Group(owner, original));
    if (g != null) Service.UpdateGroup(id, title, subtitle, g.title, g.subtitle);
   } catch (Exception ex) { Fail(ex); }
  }
  public void DeleteGroup(string id) {
   try {
    var g = Group(id);
    if (Modal(owner => MessageBox.Show(owner, "删除文件夹“" + g.title + "”及其中的入口？\n不会删除或移动任何原文件。", "删除文件夹", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)) == DialogResult.OK)
     Service.DeleteGroup(id);
   } catch (Exception ex) { Fail(ex); }
  }
  public void AddItem(string groupId) { var item = Modal(owner => EditDialogs.Item(owner, null)); if (item != null) Try(delegate { Service.AddItem(groupId, item); }); }
  public void EditItem(string groupId, string itemId) {
   try {
    var old = Service.FindItem(groupId, itemId);
    if (old == null) return;
    var edited = Modal(owner => EditDialogs.Item(owner, old));
    if (edited != null) Service.UpdateItem(groupId, itemId, old, edited);
   } catch (Exception ex) { Fail(ex); }
  }
  public bool RenameItemInline(string groupId, string itemId, string newTitle) {
   if ((newTitle ?? "").Trim().Length == 0) return false;
   return Try(delegate { Service.RenameItem(groupId, itemId, newTitle); });
  }
  public bool SetCalendarNote(string dateKey, string noteText) { return Try(delegate { Service.SetNote(dateKey, noteText); }); }
  public void EditCalendarNote(string dateKey, Point screenLocation) {
   try {
    string existing = Service.GetNote(dateKey);
    string updated = Modal(owner => EditDialogs.CalendarNote(owner, dateKey, existing, screenLocation));
    if (updated != null) SetCalendarNote(dateKey, updated);
   } catch (Exception ex) { Fail(ex); }
  }
  public void ToggleShowLabels() { Try(delegate { Service.ToggleShowLabels(); }); }
  public void DeleteItem(string groupId, string itemId) {
   if (Modal(owner => MessageBox.Show(owner, "只删除此入口，不会删除原文件。", "移除入口", MessageBoxButtons.OKCancel, MessageBoxIcon.Question)) == DialogResult.OK)
    Try(delegate { Service.DeleteItem(groupId, itemId); });
  }
  public bool TryMoveItem(string sourceGroup, string itemId, string targetGroup, int insertIndex) { return Try(delegate { Service.MoveItem(sourceGroup, itemId, targetGroup, insertIndex); }); }
  public bool TryMoveGroup(string groupId, int targetIndex) { return Try(delegate { Service.MoveGroup(groupId, targetIndex); }); }
  public bool TryAddPaths(string groupId, string[] paths, int insertIndex) { return Try(delegate { Service.AddPaths(groupId, paths, insertIndex); }); }
  public bool TryAddUrl(string groupId, string url, int insertIndex) { return Try(delegate { Service.AddUrl(groupId, url, insertIndex); }); }
  public void MoveItem(string sourceGroup, string itemId, string targetGroup, int index) { TryMoveItem(sourceGroup, itemId, targetGroup, index); }
  public void MoveGroup(string id, int index) { TryMoveGroup(id, index); }
  public void Launch(string groupId, string itemId) {
   if (Try(delegate { Service.Launch(groupId, itemId); })) ClosePanel();
  }
  public void RunEntryAction(Action action, bool closeAfter) {
   if (Try(action) && closeAfter) ClosePanel();
  }
  void CreateTray() {
   Icon appIcon = SystemIcons.Application;
   try {
    Icon extracted = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
    if (extracted != null) appIcon = extracted;
   } catch { }
   tray = new NotifyIcon { Icon = appIcon, Text = "MacDesk · 拖入添加，右键编辑，自动保存", Visible = true };
   var menu = new ContextMenuStrip();
   PanelDrawing.StyleMenu(menu);
   menu.Items.Add("显示桌面小组件", null, delegate {
    Home.WindowState = FormWindowState.Normal;
    Home.Show();
    if (DesktopFolders != null) { DesktopFolders.RefreshData(); DesktopFolders.Show(); }
    NativeDesktop.Lower(Home.Handle);
   });
   menu.Items.Add("新建文件夹…", null, delegate { AddGroup(); });
   menu.Items.Add("重新读取配置", null, delegate { try { Service.Reload(); Wallpaper.Refresh(true); } catch (Exception ex) { Fail(ex); } });
   menu.Items.Add("打开备份目录", null, delegate {
    try {
     string dir = Path.Combine(Service.Root, "Backups");
     Directory.CreateDirectory(dir);
     EntryServices.Open(new DeskItem { type = "folder", target = dir });
    } catch (Exception ex) { Fail(ex); }
   });
   menu.Items.Add(new ToolStripSeparator());
   menu.Items.Add("退出 MacDesk", null, delegate { Quit(); });
   tray.ContextMenuStrip = menu;
   tray.DoubleClick += delegate { Home.WindowState = FormWindowState.Normal; Home.Show(); NativeDesktop.Lower(Home.Handle); };
  }
  public void Quit() {
   if (closing) return;
   closing = true;
   HideDim();
   DiscardPrefetch();
   if (Panel != null) Panel.Dispose();
   if (tray != null) { tray.Visible = false; tray.Dispose(); }
   if (DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.Dispose();
   if (Home != null && !Home.IsDisposed) Home.Dispose();
   if (dialogOwner != null && !dialogOwner.IsDisposed) dialogOwner.Dispose();
   ExitThread();
  }
 }
}
