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
  internal AppCatalog Catalog { get { return Service.Catalog; } }

  NotifyIcon tray;
  bool closing, refreshQueued, testMode;
  readonly HashSet<string> launchingApps = new HashSet<string>();
  int panelSession;
  BackdropOverlay dim;
  Backdrop backdrop;
  IconLoader icons;

  internal void EnableBackgroundIcons() {
   if (icons != null) return;
   icons = new IconLoader(
    () => Panel == null || !Panel.Visible || (!Panel.IsOpening && !Panel.IsClosing),
    ready => {
     if (DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.RefreshIcons(ready);
     if (Panel != null && Panel.Visible) Panel.RefreshIcons();
    });
  }

  internal Bitmap GetIcon(DeskItem item, bool priority) {
   return icons == null ? FolderLayer.CachedIcon(item) : icons.Get(item, priority);
  }

  internal void RefreshAppCatalog() {
   if (closing || !Home.IsHandleCreated) return;
   Catalog.Refresh(action => Home.BeginInvoke(action), delegate { if (!closing && Panel != null) Panel.UpdateAppCatalog(); });
  }

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
   if (DesktopFolders != null && !DesktopFolders.IsDisposed) {
    DesktopFolders.SetOpenFolder(null);
    if (keyTargets.Count == 0) DesktopFolders.ReleaseKeyboardFocus();
   }
  }

  // ---- Popups and keyboard routing ----------------------------------------------------------
  // Glass popups (day sheet, alerts, toasts) are top-level, non-activating windows owned by
  // the hidden dialog owner. Those that take the keyboard sit on a stack above the folder panel:
  // keys go to the top of the stack, then to the panel.

  readonly List<IKeyTarget> keyTargets = new List<IKeyTarget>();
  readonly List<GlassPopup> popups = new List<GlassPopup>();

  internal Form PopupOwner { get { return OwnDialogWindow(); } }
  /// True while a popup owns the keyboard (the folder panel then ignores outside presses).
  internal bool KeyboardPopupOpen { get { return keyTargets.Count > 0; } }

  internal void PushKeyTarget(IKeyTarget target) {
   keyTargets.Remove(target);
   keyTargets.Add(target);
   if (DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.TakeKeyboardFocus();
  }
  internal void PopKeyTarget(IKeyTarget target) {
   if (!keyTargets.Remove(target) || keyTargets.Count > 0) return;
   bool panelOpen = Panel != null && !Panel.IsDisposed && Panel.Visible;
   if (!panelOpen && DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.ReleaseKeyboardFocus();
  }
  internal void RegisterPopup(GlassPopup popup) { if (!popups.Contains(popup)) popups.Add(popup); }
  internal void UnregisterPopup(GlassPopup popup) { popups.Remove(popup); }
  /// Whether a screen point lies on one of our visible popups (other than `except`).
  internal bool PopupContains(Point screen, GlassPopup except) {
   foreach (var popup in popups)
    if (popup != except && !popup.IsDisposed && popup.Visible && popup.CardScreenRect.Contains(screen)) return true;
   return false;
  }
  internal void BringPopupToTop(GlassPopup popup) {
   BringToTop(popup);
   // Toasts stay above everything else of ours.
   foreach (var other in popups) if (other != popup && other is Toast) BringToTop(other);
  }

  // The folder UI never takes activation (that would kick Windows out of "show desktop" and
  // restore every window over it). Keyboard input is received by the desktop-layer window,
  // which lives inside the desktop's own focus, and routed here.
  internal bool RouteKey(Keys key) {
   if (keyTargets.Count > 0) return keyTargets[keyTargets.Count - 1].HandleKey(key);
   Keys code = key & Keys.KeyCode;
   bool modifierOnly = code == Keys.ShiftKey || code == Keys.ControlKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin;
   if (Panel == null || Panel.IsDisposed || !Panel.Visible) return false;
   if (Panel.IsClosing) return key == Keys.Escape;
   if (key == Keys.Escape && Panel.IsOpening) { ClosePanel(); return true; }
   if (!modifierOnly) Panel.CompleteOpening(); // typing during the open: land it, then deliver
   return Panel.HandleKey(key);
  }
  internal bool RouteChar(char c) {
   if (keyTargets.Count > 0) return keyTargets[keyTargets.Count - 1].HandleChar(c);
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
    Service.Changed += delegate(object sender, EventArgs e) { QueueRefresh(DeskChangedEventArgs.KindsOf(e)); };
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
      EnableBackgroundIcons();
     DesktopFolders = new DesktopFolderForm(this);
     DesktopFolders.FormClosed += delegate { Quit(); };
     DesktopFolders.HandleCreated += delegate { if (!testMode) NativeDesktop.SetCompanion(DesktopFolders.Handle); };
     DesktopFolders.Show();
     NativeDesktop.SetCompanion(DesktopFolders.Handle);
     Home.Show();
     NativeDesktop.Lower(Home.Handle);
     NativeDesktopHost.EnsureChildTopOrder(Home.Handle, DesktopFolders.Handle);
     Home.LocationChanged += ReflowDesktopWidgets;
     Home.SizeChanged += ReflowDesktopWidgets;
     DesktopFolders.ReflowWidgets();
     CreateTray();
     NativeDesktopHost.HideDesktopIcons(Home.Handle, DesktopFolders.Handle);
     RefreshAppCatalog();
    }
   } catch { Quit(); throw; }
  }

  protected override void Dispose(bool disposing) { if (disposing) Quit(); base.Dispose(disposing); }
  void ReflowDesktopWidgets(object sender, EventArgs e) {
   if (!closing && DesktopFolders != null) DesktopFolders.ReflowWidgets();
  }
  public void ReportError(Exception ex) { Fail(ex); }
  void Fail(Exception ex) {
   if (testMode) throw ex;
   ShowToast(ex.Message);
  }

  // ---- Glass popups: toast, alert, day sheet -------------------------------------------------

  Toast toast;
  AlertSheet alert;
  DaySheet daySheet;

  /// Shows a short message at the top of the screen (errors; replaces the Win32 message box).
  public void ShowToast(string message) {
   if (closing) return;
   if (toast == null || toast.IsDisposed) toast = new Toast(this);
   Point at = Panel != null && !Panel.IsDisposed && Panel.Visible ? Panel.ScreenRect.Location : Cursor.Position;
   toast.ShowMessage(message, Screen.FromPoint(at).WorkingArea);
  }
  internal Toast CurrentToast { get { return toast; } }
  internal AlertSheet CurrentAlert { get { return alert; } }
  internal DaySheet CurrentDaySheet { get { return daySheet; } }

  /// iOS-style confirmation over the open folder (or the screen centre); runs onConfirm on OK.
  public void Confirm(string title, string message, string actionLabel, Point near, Action onConfirm) {
   if (testMode) { onConfirm(); return; }
   if (closing) return;
   if (alert == null || alert.IsDisposed) alert = new AlertSheet(this);
   Rectangle over = Panel != null && !Panel.IsDisposed && Panel.Visible && !Panel.IsClosing ? Panel.ScreenRect : Screen.FromPoint(near).WorkingArea;
   alert.Ask(title, message, actionLabel, over, onConfirm);
  }

  /// The day card for a calendar day, beside the calendar (besideScreen), grown from the day.
  public void ShowDay(string dateKey, Rectangle besideScreen, RectangleF dayScreen) {
   if (closing) return;
   if (daySheet == null || daySheet.IsDisposed) daySheet = new DaySheet(this);
   try { daySheet.ShowDay(dateKey, besideScreen, dayScreen); } catch (Exception ex) { Fail(ex); }
  }
  /// The agenda card (overdue / today / undated) beside the reminders widget.
  public void ShowAgenda(Rectangle besideScreen, RectangleF fromScreen, bool startAdding) {
   if (closing) return;
   if (daySheet == null || daySheet.IsDisposed) daySheet = new DaySheet(this);
   try { daySheet.ShowAgenda(besideScreen, fromScreen, startAdding); } catch (Exception ex) { Fail(ex); }
  }

  // ---- Reminders -----------------------------------------------------------------------------
  // Ticking a reminder animates at once; the save waits until the views have settled (a disk
  // write mid-animation would drop frames), and quick successive ticks are saved together.

  readonly Dictionary<string, bool> pendingDone = new Dictionary<string, bool>();
  System.Windows.Forms.Timer doneFlush;

  /// What a view should show for `todo` (a pending tick wins over the saved state).
  internal bool IsTodoDone(DeskTodo todo) {
   bool done;
   return todo != null && (pendingDone.TryGetValue(todo.id, out done) ? done : todo.done);
  }
  internal bool HasPendingTicks { get { return pendingDone.Count > 0; } }

  internal void ToggleTodoSoon(string todoId, bool done) {
   pendingDone[todoId] = done;
   if (testMode) { FlushTodoTicks(); return; }
   if (doneFlush == null) {
    doneFlush = new System.Windows.Forms.Timer { Interval = 520 };
    doneFlush.Tick += delegate { FlushTodoTicks(); };
   }
   doneFlush.Stop();
   doneFlush.Start();
   if (Home != null && !Home.IsDisposed) Home.RefreshData();
  }

  internal void FlushTodoTicks() {
   if (doneFlush != null) doneFlush.Stop();
   if (pendingDone.Count == 0) return;
   var states = new Dictionary<string, bool>(pendingDone);
   pendingDone.Clear();
   Try(delegate { Service.SetTodosDone(states); });
  }

  public DeskTodo AddTodo(string title, string dueDateKey) {
   DeskTodo added = null;
   Try(delegate { added = Service.AddTodo(title, dueDateKey); });
   return added;
  }
  public bool RenameTodo(string todoId, string title) { return Try(delegate { Service.RenameTodo(todoId, title); }); }
  public bool SetTodoDue(string todoId, string dueDateKey) { return Try(delegate { Service.SetTodoDue(todoId, dueDateKey); }); }
  public bool DeleteTodo(string todoId) {
   pendingDone.Remove(todoId);
   return Try(delegate { Service.DeleteTodo(todoId); });
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

  // Changes are coalesced into one refresh per message-loop turn, and each view only redraws
  // for the kinds of data it shows (ticking a reminder does not touch the desktop folders).
  DeskChange pendingChanges;
  void QueueRefresh(DeskChange kinds) {
   pendingChanges |= kinds;
   if (refreshQueued || closing || Home == null || Home.IsDisposed) return;
   refreshQueued = true;
   Action action = delegate {
    refreshQueued = false;
    DeskChange changed = pendingChanges;
    pendingChanges = DeskChange.None;
    if (closing || Home == null || Home.IsDisposed) return;
    if ((changed & (DeskChange.Notes | DeskChange.Todos | DeskChange.Prefs)) != 0) {
     Home.RefreshData();
     if (daySheet != null && !daySheet.IsDisposed) daySheet.RefreshData();
    }
    if ((changed & (DeskChange.Groups | DeskChange.Prefs)) == 0) return;
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
     panelSession++;
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
   Point position = Home.ClampLocation(new Point(x, y));
   if (Home.Location != position) Home.Location = position;
   if (Data.homeX != position.X || Data.homeY != position.Y) Try(delegate { Service.SetHomePosition(position.X, position.Y); });
  }


  // New folders open at once with the title ready to type over (iOS); the tray, Ctrl+N and
  // the widget menu all take this one path.
  public void AddGroup() {
   Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
   CreateAndOpenNewGroup(new Point(work.X + work.Width / 2, work.Y + work.Height / 3));
  }
  public void CreateAndOpenNewGroup(Point sourceCenter) {
   DeskGroup g = null;
   if (Try(delegate { g = Service.CreateGroupWithUniqueTitle("新文件夹"); })) {
    if (DesktopFolders != null) DesktopFolders.RefreshData();
    OpenGroup(g.id, sourceCenter);
    if (Panel != null && !Panel.IsDisposed && Panel.Visible && Panel.GroupId == g.id) Panel.RenameTitleOnLand();
   }
  }
  /// Desktop folder menu "重命名": open the folder and edit its title in place.
  public void RenameGroupFromDesktop(string id, Point sourceCenter) {
   if (Panel == null || Panel.IsDisposed || !Panel.Visible || Panel.GroupId != id || Panel.IsClosing) OpenGroup(id, sourceCenter);
   if (Panel != null && !Panel.IsDisposed && Panel.Visible && Panel.GroupId == id) Panel.RenameTitleOnLand();
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
     using (var f = new OpenFileDialog { Title = "选择文件。普通文件可在托盘打开", Multiselect = true, CheckFileExists = true, DereferenceLinks = false }) {
     return f.ShowDialog(owner) == DialogResult.OK ? f.FileNames : null;
    }
   });
   if (files != null && files.Length > 0) TryAddPaths(groupId, files, -1);
  }
  public void PickFolderToAdd(string groupId) {
   string folder = Modal(owner => FolderPicker.Pick(owner, "选择目录，添加到托盘的「其他快捷入口」"));
   if (!String.IsNullOrEmpty(folder)) TryAddPaths(groupId, new[] { folder }, -1);
  }
  public void DeleteGroup(string id) {
   try {
    var g = Group(id);
    Rectangle? tile = DesktopFolders != null && !DesktopFolders.IsDisposed ? DesktopFolders.IconRectOnScreen(id) : null;
    Point near = tile.HasValue ? new Point(tile.Value.X + tile.Value.Width / 2, tile.Value.Y) : Cursor.Position;
    Confirm("移除“" + g.title + "”？", "文件夹里的快捷入口会一起移除，原有应用和文件会保留。", "移除", near, delegate { Try(delegate { Service.DeleteGroup(id); }); });
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
  public void ToggleShowLabels() { Try(delegate { Service.ToggleShowLabels(); }); }
  public void DeleteItem(string groupId, string itemId) {
   DeskItem item = Service.FindItem(groupId, itemId);
   if (item == null) return;
   Confirm("移除“" + item.title + "”？", "只从 MacDesk 移除这个图标，原有应用和文件会保留。", "移除", Cursor.Position, delegate { Try(delegate { Service.DeleteItem(groupId, itemId); }); });
  }
  public bool TryMoveItem(string sourceGroup, string itemId, string targetGroup, int insertIndex) { return Try(delegate { Service.MoveItem(sourceGroup, itemId, targetGroup, insertIndex); }); }
  public bool TryMoveGroup(string groupId, int targetIndex) { return Try(delegate { Service.MoveGroup(groupId, targetIndex); }); }
  public bool TryAddPaths(string groupId, string[] paths, int insertIndex) { return Try(delegate { Service.AddPaths(groupId, paths, insertIndex); }); }
  public bool TryAddUrl(string groupId, string url, int insertIndex) { return Try(delegate { Service.AddUrl(groupId, url, insertIndex); }); }
  public void MoveItem(string sourceGroup, string itemId, string targetGroup, int index) { TryMoveItem(sourceGroup, itemId, targetGroup, index); }
  public void MoveGroup(string id, int index) { TryMoveGroup(id, index); }
  public void Launch(string groupId, string itemId) {
   DeskItem item = Service.FindItem(groupId, itemId);
   if (item == null) { Fail(new InvalidDataException("条目已删除。")); return; }
   if (item.type != "app") {
    if (Try(delegate { Service.Launch(groupId, itemId); })) ClosePanel();
    return;
   }
   if (launchingApps.Contains(itemId)) return;
   int session = panelSession;
   // Run ShellExecute away from the animation loop. Windows can take time to
   // resolve the shortcut or prompt for elevation even with a responsive panel.
   var ui = TaskScheduler.FromCurrentSynchronizationContext();
   launchingApps.Add(itemId);
   Task.Factory.StartNew(() => DeskService.OpenAppOnWorker(item), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default)
    .ContinueWith(task => {
     launchingApps.Remove(itemId);
     Exception error = task.Exception;
     if (closing) return;
     if (error != null) { Fail(error.GetBaseException()); return; }
     if (session != panelSession || Panel == null || Panel.IsDisposed || !Panel.Visible) return;
     if (DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.SetOpenFolder(null);
     Panel.Hide();
     HideDim();
    }, ui);
  }
  public void RunEntryAction(Action action, bool closeAfter) {
   if (Try(action) && closeAfter) ClosePanel();
  }
  public void OpenEntryWith(string groupId, string itemId, EntryAction action) {
   RunEntryAction(delegate { Service.OpenWith(groupId, itemId, action); }, true);
  }
  internal void PopulateOtherShortcuts(ToolStripMenuItem menu) {
   while (menu.DropDownItems.Count > 0) {
    ToolStripItem previous = menu.DropDownItems[0];
    menu.DropDownItems.RemoveAt(0);
    previous.Dispose();
   }
   foreach (DeskGroup group in Data.groups) {
    ToolStripMenuItem category = null;
    foreach (DeskItem item in group.items) {
     if (item.type == "app") continue;
     if (category == null) category = new ToolStripMenuItem(group.title.Replace("&", "&&"));
     string groupId = group.id, itemId = item.id;
     category.DropDownItems.Add(item.title.Replace("&", "&&"), null, delegate { Launch(groupId, itemId); });
    }
    if (category != null) {
     PanelDrawing.StyleDropDown(category);
     menu.DropDownItems.Add(category);
    }
   }
   menu.Visible = menu.DropDownItems.Count > 0;
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
    var shortcuts = new ToolStripMenuItem("其他快捷入口");
    menu.Items.Add(shortcuts);
    PanelDrawing.StyleDropDown(shortcuts);
    DesktopData menuData = null;
    menu.Opening += delegate {
     if (menuData == Data) return;
     PopulateOtherShortcuts(shortcuts);
     menuData = Data;
    };
   menu.Items.Add("在资源管理器打开桌面目录", null, delegate { Try(delegate { Service.OpenFolder(DataFolder.Desktop); }); });
   menu.Items.Add("重新读取配置", null, delegate { try { Service.Reload(); Wallpaper.Refresh(true); } catch (Exception ex) { Fail(ex); } });
   menu.Items.Add("打开备份目录", null, delegate { Try(delegate { Service.OpenFolder(DataFolder.Backups); }); });
   menu.Items.Add(new ToolStripSeparator());
   menu.Items.Add("退出 MacDesk", null, delegate { Quit(); });
   tray.ContextMenuStrip = menu;
   tray.DoubleClick += delegate { Home.WindowState = FormWindowState.Normal; Home.Show(); NativeDesktop.Lower(Home.Handle); };
  }
  public void Quit() {
   if (closing) return;
   closing = true;
   try {
    FlushTodoTicks();
    if (doneFlush != null) doneFlush.Dispose();
    foreach (var popup in new List<GlassPopup>(popups)) popup.DismissNow();
    if (daySheet != null) daySheet.Dispose();
    if (alert != null) alert.Dispose();
    if (toast != null) toast.Dispose();
    HideDim();
    DiscardPrefetch();
    if (icons != null) { icons.Dispose(); icons = null; }
    if (Panel != null) Panel.Dispose();
    if (tray != null) { tray.Visible = false; tray.Dispose(); }
    if (DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.Dispose();
    if (Home != null && !Home.IsDisposed) Home.Dispose();
    if (dialogOwner != null && !dialogOwner.IsDisposed) dialogOwner.Dispose();
   } finally {
    if (!testMode) NativeDesktopHost.RestoreDesktopIcons();
    ExitThread();
   }
  }
 }
}
