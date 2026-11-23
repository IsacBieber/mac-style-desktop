using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MacDeskNative {
 // The one animation clock of the app. A background thread waits for each vertical blank of
 // the display and posts a single "frame" to the UI thread; every running animation is ticked
 // in that same frame with the same dt. All drawing therefore happens on the UI thread, in
 // step, at the monitor's refresh rate (240 Hz here) - nothing animates on its own timer.
 //
 // The wait is the display's vblank (D3DKMTWaitForVerticalBlankEvent), never DwmFlush: DwmFlush
 // waits for DWM's next composition, and DWM composes nothing while nothing else on screen
 // changes - on a still desktop an animation could stop half-way and stay there (the day card
 // froze half-open, blurry and ignoring input).
 internal static class AnimationClock {
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
  [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);
  static double period;
  static readonly List<FramePump> active = new List<FramePump>();
  static readonly Stopwatch clock = Stopwatch.StartNew();
  static readonly MethodInvoker frame = OnFrame;
  static Control marshal;
  static int generation, pending;
  static bool running;
  static double last, postedAt;

  /// Where stalls are reported (set by the host; null = not logged).
  internal static string LogPath;
  static double lastLog = -100;
  static void Log(string text) {
   string path = LogPath;
   if (path == null || Now - lastLog < 5) return; // at most one line per 5 s
   lastLog = Now;
   try { System.IO.File.AppendAllText(path, DateTime.Now.ToString("o") + " " + text + Environment.NewLine); } catch { }
  }

  public static double Now { get { return clock.Elapsed.TotalSeconds; } }
  public static bool IsActive(FramePump pump) { return active.Contains(pump); }

  // UI thread only.
  internal static void Add(FramePump pump) {
   if (marshal == null) { marshal = new Control(); marshal.CreateControl(); IntPtr h = marshal.Handle; }
   if (!active.Contains(pump)) active.Add(pump);
   if (!running) {
    running = true;
    period = RefreshPeriod();
    last = Now;
    int gen = ++generation;
    new Thread(() => Loop(gen)) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "MacDesk animation clock" }.Start();
   }
  }
  internal static void Remove(FramePump pump) { active.Remove(pump); }

  static VBlank vblank;       // opened once, shared by successive clock threads
  static bool vblankFailed;
  static int instantWaits;

  static void Loop(int gen) {
   try {
    while (gen == generation) {
     double before = Now;
     VBlank v = vblank;
     if (v == null && !vblankFailed) { v = vblank = VBlank.Open(); if (v == null) vblankFailed = true; }
     if (v != null && !v.Wait()) { vblank = null; v.Dispose(); v = null; } // display changed: reopen next time
     if (v == null) SleepOneFrame();
     double waited = Now - before;
     // A vblank wait that keeps returning at once would spin the clock: use timed frames.
     if (v != null) { instantWaits = waited < 0.0003 ? instantWaits + 1 : 0; if (instantWaits > 8) { vblankFailed = true; vblank = null; v.Dispose(); } }
     if (waited > 0.25) Log(String.Format("animation clock: waited {0:0} ms for one frame", waited * 1000));
     if (gen != generation) return;
     if (Interlocked.CompareExchange(ref pending, 1, 0) == 0) {
      postedAt = Now;
      try { marshal.BeginInvoke(frame); } catch { pending = 0; return; }
     }
    }
   } catch { }
  }

  static bool fineTimer;
  [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
  static void SleepOneFrame() {
   if (!fineTimer) { fineTimer = true; try { timeBeginPeriod(1); } catch { } }
   Thread.Sleep(Math.Max(1, (int)Math.Round(FramePeriod * 1000)));
  }

  // One display refresh, in seconds (240 Hz here); 60 Hz if the driver doesn't say.
  public static double FramePeriod { get { return period > 0 ? period : RefreshPeriod(); } }
  static double RefreshPeriod() {
   int hz = 0;
   IntPtr dc = GetDC(IntPtr.Zero);
   if (dc != IntPtr.Zero) { try { hz = GetDeviceCaps(dc, 116 /* VREFRESH */); } finally { ReleaseDC(IntPtr.Zero, dc); } }
   if (hz < 24 || hz > 1000) hz = 60;
   return 1.0 / hz;
  }

  static void OnFrame() {
   // While a frame is drawing, drop display ticks. Another BeginInvoke could otherwise
   // keep WinForms draining frame callbacks without returning to mouse and keyboard input.
   try { RunFrame(); }
   finally { Interlocked.Exchange(ref pending, 0); }
  }

  static void RunFrame() {
   double late = Now - postedAt;
   if (late > 0.25) Log(String.Format("UI thread was busy for {0:0} ms before drawing a frame", late * 1000));
   // Animations advance in whole display refreshes. The UI thread reaches this a variable
   // moment after the vsync, so raw "now - last" wobbles by a fraction of a frame; feeding
   // that into the springs makes equal-looking frames move unequal distances (judder).
   double now = Now;
   int n = (int)Math.Round((now - last) / period);
   if (n < 1) n = 1;
   if (n > 12) n = 12;
   double dt = n * period;
   last += dt;
   if (Math.Abs(now - last) > period) last = now; // fell far behind or ahead: resync
   foreach (var pump in active.ToArray()) {
    bool more;
    try { more = pump.Tick(dt); } catch { active.Remove(pump); throw; }
    if (!more) active.Remove(pump);
   }
   if (active.Count == 0) { running = false; generation++; }
  }
 }

 // A subscription to the AnimationClock (kept under its old name: every view owns one and
 // calls Start() when something starts moving; its tick returns false when it has settled).
 internal sealed class FramePump : IDisposable {
  readonly Control owner;
  readonly Func<double, bool> tick;
  public FramePump(Control owner, Func<double, bool> tick) { this.owner = owner; this.tick = tick; }
  public bool Running { get { return AnimationClock.IsActive(this); } }
  public void Start() {
   if (owner.IsDisposed || !owner.IsHandleCreated) return;
   AnimationClock.Add(this);
  }
  public void Stop() { AnimationClock.Remove(this); }
  internal bool Tick(double dt) { return !owner.IsDisposed && tick(dt); }
  public void Dispose() { Stop(); }
 }

 // The vertical blank of the primary display, through the kernel display driver interface
 // (what browsers use for their frame clocks). Returns at every refresh whether or not
 // anything on screen is being redrawn.
 internal sealed class VBlank : IDisposable {
  [StructLayout(LayoutKind.Sequential)] struct OpenFromHdc { public IntPtr hDc; public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint VidPnSourceId; }
  [StructLayout(LayoutKind.Sequential)] struct WaitEvent { public uint hAdapter; public uint hDevice; public uint VidPnSourceId; }
  [StructLayout(LayoutKind.Sequential)] struct CloseAdapter { public uint hAdapter; }
  [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromHdc(ref OpenFromHdc p);
  [DllImport("gdi32.dll")] static extern int D3DKMTWaitForVerticalBlankEvent(ref WaitEvent p);
  [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter p);
  [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, string output, IntPtr mode);
  [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

  WaitEvent wait;
  bool open;

  public static VBlank Open() {
   try {
    IntPtr dc = CreateDC(Screen.PrimaryScreen.DeviceName, null, null, IntPtr.Zero);
    if (dc == IntPtr.Zero) return null;
    var o = new OpenFromHdc { hDc = dc };
    int status;
    try { status = D3DKMTOpenAdapterFromHdc(ref o); } finally { DeleteDC(dc); }
    if (status != 0) return null;
    var v = new VBlank { open = true };
    v.wait = new WaitEvent { hAdapter = o.hAdapter, VidPnSourceId = o.VidPnSourceId };
    return v;
   } catch { return null; }
  }

  public bool Wait() { return open && D3DKMTWaitForVerticalBlankEvent(ref wait) == 0; }

  public void Dispose() {
   if (!open) return;
   open = false;
   var c = new CloseAdapter { hAdapter = wait.hAdapter };
   try { D3DKMTCloseAdapter(ref c); } catch { }
  }
 }
}
