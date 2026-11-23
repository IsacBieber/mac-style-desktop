using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace MacDeskNative {
 // Shared bits of the reminders UI (widget and day sheet): date keys and wording in Beijing
 // time, and the round check that fills when a reminder is done.
 internal static class Reminders {
  static readonly string[] WeekNames = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };

  public static string Key(DateTime day) { return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
  public static DateTime Parse(string key) { return DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture); }
  public static DateTime Today() { return HomeForm.BeijingFromUtc(DateTime.UtcNow).Date; }
  public static string TodayKey() { return Key(Today()); }
  public static string AddDays(string key, int days) { return Key(Parse(key).AddDays(days)); }
  public static string Weekday(DateTime day) { return WeekNames[(int)day.DayOfWeek]; }
  public static string MonthDay(DateTime day) { return day.Month + "月" + day.Day + "日"; }

  /// "今天", "昨天", "前天", "明天", "11月20日" (or "2025年11月20日" in another year).
  public static string RelativeDay(string key, string todayKey) {
   if (String.IsNullOrEmpty(key)) return "";
   DateTime day = Parse(key), today = Parse(todayKey);
   int delta = (int)(day - today).TotalDays;
   if (delta == 0) return "今天";
   if (delta == -1) return "昨天";
   if (delta == -2) return "前天";
   if (delta == 1) return "明天";
   return day.Year == today.Year ? MonthDay(day) : day.Year + "年" + MonthDay(day);
  }

  /// The reminder check: an empty ring while open; when done (progress -> 1) a blue disc grows
  /// in from the centre and the tick is drawn on top. progress may overshoot (springy).
  public static void DrawCheck(Graphics g, PointF center, float diameter, float progress, Color ring, float alpha) {
   if (alpha <= 0.01f) return;
   float r = diameter / 2f;
   float fill = Math.Max(0f, Math.Min(1.12f, progress));
   if (fill < 0.98f)
    using (var pen = new Pen(Ui.WithAlpha(ring, alpha * (1 - Math.Min(1f, fill))), 1.6f))
     g.DrawEllipse(pen, center.X - r + 0.8f, center.Y - r + 0.8f, diameter - 1.6f, diameter - 1.6f);
   if (fill <= 0.01f) return;
   float d = diameter * fill;
   using (var b = new SolidBrush(Ui.WithAlpha(Theme.Blue, alpha))) g.FillEllipse(b, center.X - d / 2f, center.Y - d / 2f, d, d);
   float tick = Math.Max(0f, Math.Min(1f, (progress - 0.35f) / 0.5f));
   if (tick <= 0.01f) return;
   var a = new PointF(center.X - r * 0.42f, center.Y + r * 0.02f);
   var m = new PointF(center.X - r * 0.1f, center.Y + r * 0.34f);
   var e = new PointF(center.X + r * 0.45f, center.Y - r * 0.3f);
   using (var pen = new Pen(Ui.WithAlpha(Color.White, alpha * tick), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
    g.DrawLines(pen, new[] { a, m, e });
  }

  /// The "add" glyph: a filled blue disc with a white plus.
  public static void DrawPlusDisc(Graphics g, PointF center, float diameter, float alpha) {
   using (var b = new SolidBrush(Ui.WithAlpha(Theme.Blue, alpha))) g.FillEllipse(b, center.X - diameter / 2f, center.Y - diameter / 2f, diameter, diameter);
   float arm = diameter * 0.26f;
   using (var pen = new Pen(Ui.WithAlpha(Color.White, alpha), 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
    g.DrawLine(pen, center.X - arm, center.Y, center.X + arm, center.Y);
    g.DrawLine(pen, center.X, center.Y - arm, center.X, center.Y + arm);
   }
  }
 }
}
