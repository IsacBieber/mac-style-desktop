using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MacDeskNative {
 // The one animation clock of the app. A background thread waits for each display refresh
 // (DwmFlush) and posts a single "frame" to the UI thread; every running animation is ticked
 // in that same frame with the same dt. All drawing therefore happens on the UI thread, in
 // step, at the monitor's refresh rate (240 Hz here) - nothing animates on its own timer.
 internal static class AnimationClock {
  [DllImport("dwmapi.dll")] static extern int DwmFlush();
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
  static double last;

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

  static void Loop(int gen) {
   try {
    while (gen == generation) {
     bool waited = false;
     try { waited = DwmFlush() == 0; } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
     if (!waited) Thread.Sleep(4);
     if (gen != generation) return;
     if (Interlocked.CompareExchange(ref pending, 1, 0) == 0) {
      try { marshal.BeginInvoke(frame); } catch { pending = 0; return; }
     }
    }
   } catch { }
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
   pending = 0;
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
}
