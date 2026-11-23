using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;

using MacDesk.Core;

namespace MacDeskNative {
 // `MacDesk.exe --bench [out.txt]`: renders the real UI code offscreen (windows are created
 // but never shown) against a throw-away data folder seeded from this PC's Start menu, and
 // reports milliseconds per frame. Touches neither the screen nor the user's desktop.json.
 internal static class Bench {
  internal sealed class Stat {
   public readonly string Name; double sum, max; int n;
   public Stat(string name) { Name = name; }
   public void Add(double ms) { sum += ms; n++; if (ms > max) max = ms; }
   public double Max { get { return max; } }
   public override string ToString() { return String.Format("{0,-28} avg {1,6:0.00} ms   max {2,6:0.00} ms   frames {3}", Name, n == 0 ? 0 : sum / n, max, n); }
  }

  internal static Stat Time(string name, int frames, Action frame) {
   var stat = new Stat(name);
   for (int i = 0; i < frames; i++) {
    long t0 = Stopwatch.GetTimestamp();
    frame();
    stat.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
   }
   return stat;
  }

  // The open/close transition of a real folder, offscreen: structural invariants (icons stay
  // inside the glass, move monotonically, land exactly on the open panel's pixels, reversal is
  // continuous) and the cost of a transition frame.
  internal static Stat[] Transition(FolderLayer layer) {
   Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
   var tile = new Rectangle(work.Left + 676, work.Top + 30, Metrics.DesktopIcon, Metrics.DesktopIcon);
   layer.PrepareTransitionForTest(work, tile);
   string problem = layer.CheckTransitionInvariants();
   var invariants = new Stat("transition invariants: " + (problem ?? "OK (inside frame, monotonic, lands on layout, reversible)"));
   invariants.Add(problem == null ? 0 : 1);
   // The first frame of an open clears and presents the whole layer once; report it apart.
   var first = new Stat("open/close first frame");
   first.Add(layer.TransitionFrameMs(0));
   var frames = new Stat("open/close transition frame");
   double worst = 0, worstP = 0;
   for (int k = 1; k <= 240; k++) {
    double p = (k <= 120 ? k : 240 - k) / 120.0; // open, then close
    double ms = layer.TransitionFrameMs(p);
    frames.Add(ms);
    if (ms > worst) { worst = ms; worstP = p; }
   }
   layer.TransitionFrameMs(1); // leave it open for the panel benches
   var slowest = new Stat("slowest transition frame at p=" + worstP.ToString("0.000"));
   slowest.Add(worst);
   return new[] { invariants, first, frames, slowest };
  }

  static void SeedReminders(AppController app) {
   string today = Reminders.TodayKey();
   string[] titles = { "交季度报告", "回复设计稿意见", "MacDesk 发布检查", "给妈妈打电话", "买咖啡豆", "整理下载文件夹", "预约体检" };
   for (int i = 0; i < titles.Length; i++) app.AddTodo(titles[i], i == 0 ? Reminders.AddDays(today, -1) : i == 4 ? "" : today);
   app.SetCalendarNote(today, "下午三点评审");
  }

  // The glass popups: open/close frames and the tick animation of the day card, plus PNG
  // snapshots (MACDESK_BENCH_FRAMES) of the day card, agenda, alert and toast.
  static Stat[] Popups(AppController app) {
   string dir = Environment.GetEnvironmentVariable("MACDESK_BENCH_FRAMES");
   Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
   string today = Reminders.TodayKey();
   var stats = new System.Collections.Generic.List<Stat>();
   using (var sheet = new DaySheet(app)) {
    sheet.PrepareForTest(DaySheet.SheetMode.Day, today, work);
    if (dir != null) using (var shot = sheet.RenderSnapshot()) Save(shot, Path.Combine(dir, "day-sheet.png"));
    int k = 0;
    stats.Add(Time("day card open/close frame", 240, delegate { int i = k++; sheet.FrameMsForTest((i < 120 ? i : 240 - i) / 120.0, false); }));
    sheet.TickFirstForTest();
    stats.Add(Time("day card tick animation", 240, delegate { sheet.StepForTest(1 / 240.0); sheet.FrameMsForTest(1, true); }));
    sheet.BeginNewForTest();
    stats.Add(Time("day card typing redraw", 120, delegate { sheet.HandleChar('字'); sheet.FrameMsForTest(1, true); }));
    sheet.PrepareForTest(DaySheet.SheetMode.Agenda, today, work);
    if (dir != null) using (var shot = sheet.RenderSnapshot()) Save(shot, Path.Combine(dir, "agenda-sheet.png"));
   }
   if (dir != null) {
    using (var alert = new AlertSheet(app)) {
     alert.SizeCardForTest(alert.Measure("移除“开发空间”？", "文件夹里的快捷入口会一起移除，原有应用和文件会保留。", "移除"));
     using (var shot = alert.RenderSnapshot()) Save(shot, Path.Combine(dir, "alert.png"));
    }
    using (var toast = new Toast(app)) {
     toast.SizeCardForTest(toast.Measure("目标已不存在，请编辑入口更新路径。", work.Width));
     using (var shot = toast.RenderSnapshot()) Save(shot, Path.Combine(dir, "toast.png"));
    }
   }
   return stats.ToArray();
  }

  // Premultiplied snapshot over a neutral backdrop so translucency is visible.
  static void Save(Bitmap shot, string path) {
   using (var copy = new Bitmap(shot.Width, shot.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
   using (var g = Graphics.FromImage(copy)) {
    g.Clear(Color.FromArgb(255, 40, 44, 52));
    g.DrawImage(shot, 0, 0, shot.Width, shot.Height);
    copy.Save(path, System.Drawing.Imaging.ImageFormat.Png);
   }
  }

  internal static int Run(string output) {
   string root = Path.Combine(Path.GetTempPath(), "MacDeskBench-" + Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(root);
   var report = new StringBuilder();
   report.AppendLine("MacDesk offscreen benchmark  " + DateTime.Now.ToString("s"));
   try {
    using (var app = new AppController(root, false, true)) {
     int items = 0; foreach (var g in app.Data.groups) items += g.items.Count;
     report.AppendLine("groups " + app.Data.groups.Count + ", items " + items);
     SeedReminders(app);
     var desk = new DesktopFolderForm(app);
     foreach (Stat s in desk.Bench()) report.AppendLine(s.ToString());
     foreach (Stat s in app.Home.Bench()) report.AppendLine(s.ToString());
     foreach (Stat s in Popups(app)) report.AppendLine(s.ToString());
     DeskGroup biggest = null;
     foreach (var g in app.Data.groups) if (biggest == null || g.items.Count > biggest.items.Count) biggest = g;
     if (biggest != null) {
      var panel = new FolderLayer(app, biggest.id);
      panel.WarmCaches(true); // measure steady rendering with already-loaded icons and labels
      foreach (Stat s in Transition(panel)) report.AppendLine(s.ToString());
      foreach (Stat s in panel.Bench()) report.AppendLine(s.ToString());
      panel.Dispose();
     }
     desk.Dispose();
     app.Quit();
    }
   } catch (Exception ex) {
    report.AppendLine("FAILED: " + ex);
    File.WriteAllText(output, report.ToString(), Encoding.UTF8);
    return 1;
   } finally {
    try { Directory.Delete(root, true); } catch { }
   }
   File.WriteAllText(output, report.ToString(), Encoding.UTF8);
   return 0;
  }
 }
}
