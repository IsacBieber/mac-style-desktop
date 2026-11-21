using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
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
  public PanelForm Panel { get; private set; }
  readonly Dictionary<string, PanelForm> panels = new Dictionary<string, PanelForm>();

  // Windows 11 coalesces every timer to ~15.6 ms; the folder morph is rendered by its own
  // thread paced by DwmFlush, i.e. one frame per display refresh (240 Hz on this machine).
  Thread renderThread;
  volatile bool motionRunning, reverseRequested;
  // Each motion gets a generation so a completion queued by an older render thread can
  // never finish (or tear down) a newer animation that started in the meantime.
  int motionGeneration;
  void StartMotion() {
   StopMotion();
   motionRunning = true;
   reverseRequested = false;
   clock.Restart();
   MotionLogReset();
   int generation = ++motionGeneration;
   renderThread = new Thread(() => RenderLoop(generation));
   renderThread.IsBackground = true;
   renderThread.Priority = ThreadPriority.AboveNormal;
   renderThread.Start();
  }
  void StopMotion() {
   motionRunning = false;
   Thread t = renderThread;
   // The render thread only ever blocks for one frame; wait for it so the motion window is
   // never disposed while a frame is still being presented on it.
   if (t != null && t != Thread.CurrentThread && t.IsAlive) t.Join(2000);
   renderThread = null;
  }

  readonly Stopwatch clock = new Stopwatch();
  NotifyIcon tray;
  bool closing, refreshQueued, testMode;
  Rectangle originBounds, fromBounds, toBounds, expandedBounds, motionBounds;
  double progressFrom, progressTo, motionProgress;
  bool closeMotion, motionMouse, handingOff;
  GlassMotion motion;
  BackdropOverlay dim;
  Backdrop backdrop;
  const int OpenMs = 380, CloseMs = 300;
  const double DimAlpha = 1.0;
  // iOS-like springs: opening has a hint of overshoot, closing is critically damped.
  const double OpenResponse = 0.36, OpenDamping = 0.8, CloseResponse = 0.30, CloseDamping = 1.0;

  public static double EvaluateSpring(double t, bool closingMotion) {
   if (t <= 0.0) return 0.0;
   if (t >= 1.0) return 1.0;
   double duration = (closingMotion ? CloseMs : OpenMs) / 1000.0;
   double response = closingMotion ? CloseResponse : OpenResponse, damping = closingMotion ? CloseDamping : OpenDamping;
   // Normalised so the curve lands exactly on 1 at the end of the motion (no visible snap).
   return Spring.StepResponse(t * duration, response, damping) / Spring.StepResponse(duration, response, damping);
  }

  // Optional motion diagnostics (off unless MACDESK_MOTION_LOG is set): proves real frame pacing.
  static readonly string MotionLog = Environment.GetEnvironmentVariable("MACDESK_MOTION_LOG");
  long motionStartTicks, firstFrameTicks;
  int motionFrames;
  double motionMaxGapMs, lastFrameMs, workMax, workSum, setupMs;
  void MotionLogReset() { motionStartTicks = Stopwatch.GetTimestamp(); firstFrameTicks = 0; motionFrames = 0; motionMaxGapMs = 0; lastFrameMs = 0; workMax = 0; workSum = 0; }
  void MotionLogFrame(double workMs) {
   if (workMs > workMax) workMax = workMs;
   workSum += workMs;
   long ms = (long)((Stopwatch.GetTimestamp() - motionStartTicks) * 1000.0 / Stopwatch.Frequency);
   if (firstFrameTicks == 0) firstFrameTicks = ms;
   if (lastFrameMs > 0) trackGap(ms - lastFrameMs);
   lastFrameMs = ms;
   motionFrames++;
  }
  void trackGap(double gap) { if (gap > motionMaxGapMs) motionMaxGapMs = gap; }
  void MotionLogDone(bool isClosing) {
   if (String.IsNullOrEmpty(MotionLog)) return;
   try {
    string phases = "";
    GlassMotion m = motion;
    if (m != null && motionFrames > 0) {
     int n = motionFrames;
     phases = ",\"materialMs\":" + Math.Round(m.TimeMaterial / n, 2) + ",\"contentMs\":" + Math.Round(m.TimeContent / n, 2) + ",\"originRimMs\":" + Math.Round(m.TimeOrigin / n, 2) + ",\"presentMs\":" + Math.Round(m.TimePresent / n, 2);
    }
    string line = "{\"closing\":" + (isClosing ? "true" : "false") + ",\"frames\":" + motionFrames + ",\"firstFrameMs\":" + firstFrameTicks + ",\"maxGapMs\":" + Math.Round(motionMaxGapMs, 1) + ",\"workAvgMs\":" + Math.Round(workSum / Math.Max(1, motionFrames), 2) + ",\"workMaxMs\":" + Math.Round(workMax, 2) + ",\"setupMs\":" + Math.Round(setupMs, 1) + phases + "}\r\n";
    File.AppendAllText(MotionLog, line);
   } catch { }
  }

  // Capturing + blurring the screen costs a few dozen ms; doing it while the mouse button is
  // still down (a click lasts ~100 ms) means the folder starts moving the instant it's released.
  // The work runs on a pool thread so the press animation itself never stalls.
  System.Threading.Tasks.Task<Backdrop> prefetch;
  Rectangle prefetchArea;
  DateTime prefetchedAt;
  internal void PrefetchBackdrop(Point screenPoint) {
   if (closing || testMode || (dim != null && !dim.IsDisposed)) return;
   Rectangle work = Screen.FromPoint(screenPoint).WorkingArea;
   DiscardPrefetch();
   prefetchArea = work;
   prefetchedAt = DateTime.UtcNow;
   prefetch = System.Threading.Tasks.Task.Factory.StartNew(() => Backdrop.FromScreen(work));
  }
  Backdrop TakePrefetch(Rectangle area) {
   var task = prefetch;
   prefetch = null;
   if (task == null) return null;
   if (prefetchArea == area && (DateTime.UtcNow - prefetchedAt).TotalMilliseconds < 2500) {
    try { if (task.Wait(400)) return task.Result; } catch { }
   }
   DisposeWhenDone(task);
   return null;
  }
  void DiscardPrefetch() { var task = prefetch; prefetch = null; if (task != null) DisposeWhenDone(task); }
  static void DisposeWhenDone(System.Threading.Tasks.Task<Backdrop> task) {
   task.ContinueWith(t => { if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result != null) t.Result.Dispose(); });
  }

  // Blurred, dimmed copy of the screen behind an open folder. Captured once per open, before
  // any of our overlay windows exist, and reused while switching between folders.
  void ShowDim(Rectangle bounds, double alpha) {
   if (dim == null || dim.IsDisposed) {
    if (backdrop == null || backdrop.Area != bounds) {
     if (backdrop != null) backdrop.Dispose();
     backdrop = null;
     backdrop = TakePrefetch(bounds);
     if (backdrop == null) {
      try { backdrop = Backdrop.FromScreen(bounds); } catch { backdrop = null; }
     }
    }
    dim = new BackdropOverlay(bounds, OwnDialogWindow(), backdrop, delegate { ClosePanel(); });
   }
   if (!dim.Visible) dim.Show();
   dim.SetAlphaThreadSafe(alpha);
  }
  void HideDim() {
   BackdropOverlay old = dim;
   dim = null;
   if (old != null) { old.Hide(); old.Dispose(); }
   if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
   if (Panel != null && !Panel.IsDisposed) Panel.SetBackdrop(null);
   if (DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.ReleaseKeyboardFocus();
  }

  // The folder UI never takes activation (that would kick Windows out of "show desktop" and
  // restore every window over it). Keyboard input is received by the desktop-layer window,
  // which lives inside the desktop's own focus, and routed here.
  internal bool RouteKey(Keys key) {
   Keys code = key & Keys.KeyCode;
   bool modifierOnly = code == Keys.ShiftKey || code == Keys.ControlKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin;
   if (motion != null) {
    if (closeMotion) return key == Keys.Escape;
    if (MotionKey(key)) return true;
    if (modifierOnly) return false;
    FinishMotion(); // typing during the open: land the panel, then deliver the key
   }
   if (Panel != null && !Panel.IsDisposed && Panel.Visible) return Panel.HandleKey(key);
   return false;
  }
  internal bool RouteChar(char c) {
   if (motion != null) {
    if (closeMotion) return false;
    FinishMotion();
   }
   if (Panel != null && !Panel.IsDisposed && Panel.Visible) return Panel.HandleChar(c);
   return false;
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  static void BringToTop(Form f) {
   if (f != null && !f.IsDisposed && f.IsHandleCreated) SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOP, NOSIZE|NOMOVE|NOACTIVATE
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
  void DisposeMotion() { GlassMotion old = motion; motion = null; if (old != null) old.Dispose(); }
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

  bool MotionKey(Keys key) {
   if (key == Keys.Escape) { ClosePanel(); return true; }
   if (key == (Keys.Control | Keys.F)) {
    FinishMotion();
    if (Panel != null && !Panel.IsDisposed && Panel.Visible) Panel.FocusSearch();
    return true;
   }
   return false;
  }
  internal bool SourceContains(Point point) { Rectangle r = originBounds; r.Inflate(16, 16); r.Height += 24; return r.Contains(point); }

  void MotionDeactivated(object sender, EventArgs args) {
   if (handingOff || motion == null || sender != motion || closing) return;
   if ((GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0) return;
   StopMotion();
   if (Panel != null && !Panel.IsDisposed) Panel.Hide();
   HideDim();
   closeMotion = true;
   DisposeMotion();
  }
  void PrepareMotion() {
   motion.KeyHandler = MotionKey;
   motion.Deactivate += MotionDeactivated;
   // A click that lands while the folder is still opening is not lost: land the panel at
   // once and deliver the click to whatever icon is under the pointer.
   motion.Clicked = delegate(Point screen) {
    if (closeMotion || motion == null) return;
    FinishMotion();
    if (Panel != null && !Panel.IsDisposed && Panel.Visible) Panel.ClickAt(Panel.PointToClient(screen));
   };
  }
  void FinishMotion() {
   StopMotion();
   if (motion == null) return;
   MotionLogDone(closeMotion);
   handingOff = true;
   try {
    if (closeMotion) HideDim();
    else if (dim != null && !dim.IsDisposed) dim.SetAlphaThreadSafe(DimAlpha);
    if (Panel != null && !Panel.IsDisposed) {
     if (closeMotion) Panel.Hide();
     else {
      // Present the real panel first, then drop the motion layer on top of it: the two
      // frames are identical, so the hand-off is invisible.
      Panel.Bounds = expandedBounds;
      Panel.SetBackdrop(backdrop);
      Panel.PresentNow();
      Panel.Show(OwnDialogWindow());
      BringToTop(Panel);
      Panel.BeginReveal();
     }
    }
    DisposeMotion();
   } finally {
    handingOff = false;
   }
  }

  public AppController(string root) : this(root, true, true) { }
  public AppController(string root, bool show) : this(root, show, false) { }
  internal AppController(string root, bool show, bool seedIfEmpty) {
   testMode = !show;
   try {
    Service = new DeskService(root);
    Service.Changed += delegate { QueueRefresh(); };
    if (seedIfEmpty) Service.SeedStarterGroupsIfNew();
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
     EventHandler warm = null;
     warm = delegate { Application.Idle -= warm; WarmPanels(); };
     Application.Idle += warm;
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
   if (Panel != null && !Panel.IsDisposed && Panel.Visible && !closeMotion) return Panel;
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
   FinishMotion();
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
    foreach (var pair in panels.ToArray()) {
     if (!Data.groups.Any(g => g.id == pair.Key)) {
      if (Panel == pair.Value) { StopMotion(); DisposeMotion(); Panel = null; }
      pair.Value.Dispose();
      panels.Remove(pair.Key);
     } else pair.Value.RefreshData();
    }
   };
   if (Home.IsHandleCreated) Home.BeginInvoke(action); else action();
  }

  // Build every folder's panel (window, icons, layout) once the desktop is idle, so the first
  // open of any folder does not pay for window creation and icon extraction.
  void WarmPanels() {
   if (closing) return;
   foreach (var g in Data.groups) {
    PanelForm existing;
    if (panels.TryGetValue(g.id, out existing) && !existing.IsDisposed) continue;
    var p = new PanelForm(this, g.id);
    IntPtr h = p.Handle;
    p.WarmCaches(panels.Count == 0);
    panels[g.id] = p;
   }
  }

  // The morph window covers the whole path once (icon -> sheet, plus room for the spring's
  // overshoot) so it never has to be moved or resized while animating.
  static Rectangle MotionCanvas(Rectangle a, Rectangle b) {
   Rectangle u = Rectangle.Union(a, b);
   int pad = Math.Max(24, (int)(Math.Max(b.Width, b.Height) * 0.05));
   u.Inflate(pad, pad);
   return u;
  }

  public void OpenGroup(string id, Point sourceCenter) {
   long openStarted = Stopwatch.GetTimestamp();
   try {
    Group(id);
    if (Panel != null && Panel.GroupId == id && (Panel.Visible || motion != null) && !closeMotion) { ClosePanel(); return; }
    StopMotion();
    DisposeMotion();
    if (Panel != null) Panel.Hide();
    if (!panels.ContainsKey(id) || panels[id].IsDisposed) panels[id] = new PanelForm(this, id);
    Panel = panels[id];
    Panel.Text = "MacDesk · " + Group(id).title;
    Panel.ResetTransientState();
    Panel.RefreshData();
    Rectangle work = Screen.FromPoint(sourceCenter).WorkingArea;
    Size size = Panel.GetExpandedSize(sourceCenter);
    int icon = DesktopFolderForm.IconSize;
    originBounds = new Rectangle(sourceCenter.X - icon / 2, sourceCenter.Y - icon / 2, icon, icon);
    int centerX = work.Left + (work.Width - size.Width) / 2, centerY = work.Top + (work.Height - size.Height) / 2 - 18;
    expandedBounds = new Rectangle(Math.Max(work.Left + 12, Math.Min(work.Right - size.Width - 12, centerX)), Math.Max(work.Top + 12, Math.Min(work.Bottom - size.Height - 12, centerY)), size.Width, size.Height);
    Panel.WindowState = FormWindowState.Normal;
    Panel.Bounds = expandedBounds;
    if (Panel.Handle == IntPtr.Zero) throw new InvalidOperationException("无法准备文件夹窗口。");
    // Capture the blurred screen before any overlay of ours is on it.
    ShowDim(work, 0.0);
    Bitmap originBmp = DesktopFolders != null ? DesktopFolders.RenderTilePreview(id) : null;
    toBounds = PanelForm.SheetBounds(expandedBounds);
    motion = new GlassMotion(Panel.MotionSnapshot(), originBmp, backdrop, MotionCanvas(originBounds, toBounds), DesktopFolderForm.IconRadius, PanelForm.SheetRadius, OwnDialogWindow(), false);
    fromBounds = originBounds;
    motionBounds = fromBounds;
    progressFrom = motionProgress = 0.0;
    progressTo = 1.0;
    closeMotion = false;
    PrepareMotion();
    motion.Present(fromBounds, 0.0, 1.0);
    motion.Show();
    BringToTop(dim);
    BringToTop(motion);
    if (DesktopFolders != null) DesktopFolders.TakeKeyboardFocus();
    motionMouse = (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0;
    setupMs = (Stopwatch.GetTimestamp() - openStarted) * 1000.0 / Stopwatch.Frequency;
    StartMotion();
   } catch (Exception ex) { StopMotion(); DisposeMotion(); HideDim(); Fail(ex); }
  }

  public void ClosePanel() {
   if (Panel == null || Panel.IsDisposed || closeMotion || (!Panel.Visible && motion == null)) return;
   try {
    if (motion != null) { reverseRequested = true; return; }
    StopMotion();
    if (dim == null || dim.IsDisposed) ShowDim(Screen.FromControl(Panel).WorkingArea, DimAlpha);
    Bitmap originBmp = DesktopFolders != null ? DesktopFolders.RenderTilePreview(Panel.GroupId) : null;
    motionBounds = PanelForm.SheetBounds(Panel.Bounds);
    motion = new GlassMotion(Panel.MotionSnapshot(), originBmp, backdrop, MotionCanvas(originBounds, motionBounds), DesktopFolderForm.IconRadius, PanelForm.SheetRadius, OwnDialogWindow(), false);
    motionProgress = 1.0;
    PrepareMotion();
    motion.Present(motionBounds, 1.0, 1.0);
    motion.Show();
    BringToTop(motion);
    Panel.Hide();
    fromBounds = motionBounds;
    toBounds = originBounds;
    progressFrom = 1.0;
    progressTo = 0.0;
    closeMotion = true;
    setupMs = 0;
    StartMotion();
   } catch (Exception ex) { DisposeMotion(); HideDim(); Panel.Hide(); Fail(ex); }
  }

  [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmFlush();

  // Runs on its own thread: one UpdateLayeredWindow per display refresh, no message-loop jitter.
  void RenderLoop(int generation) {
   bool completed = false;
   try {
    while (motionRunning && !closing) {
     if (Panel == null || Panel.IsDisposed || motion == null) return;
     bool down = (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0;
     if (!closeMotion && (reverseRequested || (down && !motionMouse && !expandedBounds.Contains(Cursor.Position) && !SourceContains(Cursor.Position)))) {
      // Interrupted while opening: fold back from wherever the sheet is right now.
      reverseRequested = false;
      fromBounds = motionBounds;
      toBounds = originBounds;
      progressFrom = motionProgress;
      progressTo = 0.0;
      closeMotion = true;
      clock.Restart();
      motionMouse = down;
      continue;
     }
     motionMouse = down;
     double t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / (closeMotion ? CloseMs : OpenMs));
     double e = EvaluateSpring(t, closeMotion);
     Rectangle b = new Rectangle(
      (int)Math.Round(fromBounds.X + (toBounds.X - fromBounds.X) * e),
      (int)Math.Round(fromBounds.Y + (toBounds.Y - fromBounds.Y) * e),
      Math.Max(2, (int)Math.Round(fromBounds.Width + (toBounds.Width - fromBounds.Width) * e)),
      Math.Max(2, (int)Math.Round(fromBounds.Height + (toBounds.Height - fromBounds.Height) * e)));
     double prog = Math.Max(0.0, Math.Min(1.0, progressFrom + (progressTo - progressFrom) * e));
     motionBounds = b;
     motionProgress = prog;
     long t0 = Stopwatch.GetTimestamp();
     motion.Present(b, prog, 1.0);
     BackdropOverlay d = dim;
     if (d != null && !d.IsDisposed) d.SetAlphaThreadSafe(DimAlpha * Math.Min(1.0, prog * 1.25));
     MotionLogFrame((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
     if (t >= 1.0) { completed = true; break; }
     bool paced = false;
     try { paced = DwmFlush() == 0; } catch { }
     if (!paced) Thread.Sleep(4);
    }
   } catch (Exception ex) {
    // Never leave a frozen motion layer on screen: tear it down, then report.
    try {
     Home.BeginInvoke((Action)delegate {
      if (generation != motionGeneration) return;
      StopMotion();
      DisposeMotion();
      HideDim();
      if (Panel != null && !Panel.IsDisposed) Panel.Hide();
      closeMotion = true;
      Fail(ex);
     });
    } catch { }
    return;
   }
   if (completed) {
    try { Home.BeginInvoke((Action)delegate { if (generation == motionGeneration) FinishMotion(); }); } catch { }
   }
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
   Try(delegate { Service.SetHomePosition(clampedX, clampedY); });
  }

  public void SetFolderPosition(string id, int x, int y) {
   var positions = DesktopFolders == null ? null : DesktopFolders.SnapshotPositions();
   var area = DesktopFolders == null ? new Size(Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.WorkingArea.Height) : DesktopFolders.ClientSize;
   Try(delegate { Service.SetGroupPosition(id, x, y, positions, area); });
  }
  public void ResetFolderPositions() { Try(delegate { Service.ResetGroupPositions(); }); }

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
  public void NotifyFocusCompleted() {
   try {
    if (tray != null) tray.ShowBalloonTip(4000, "MacDesk · 专注完成", "25 分钟番茄专注已结束，起来活动一下吧！", ToolTipIcon.Info);
   } catch { }
  }
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
   StopMotion();
   DisposeMotion();
   HideDim();
   DiscardPrefetch();
   foreach (var p in panels.Values) p.Dispose();
   panels.Clear();
   if (tray != null) { tray.Visible = false; tray.Dispose(); }
   if (DesktopFolders != null && !DesktopFolders.IsDisposed) DesktopFolders.Dispose();
   if (Home != null && !Home.IsDisposed) Home.Dispose();
   if (dialogOwner != null && !dialogOwner.IsDisposed) dialogOwner.Dispose();
   ExitThread();
  }
 }
}
