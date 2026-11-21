using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

using MacDesk.Core;

namespace MacDeskNative {
 // The left column is widgets only (iOS style). Folders live on DesktopFolderForm, never here.
 // Frosted-wallpaper glass, rendered into a persistent layered surface, animated with springs.
 public class HomeForm : Form {
  readonly AppController app;
  readonly Timer timer = new Timer();
  readonly LayeredSurface surface = new LayeredSurface();
  readonly FramePump pump;

  readonly Font captionFont = Ui.MakeFont(Ui.Cjk, 13f, FontStyle.Regular);
  readonly Font clockFont = Ui.MakeFont(Ui.DisplayRegular, 76f, FontStyle.Regular);
  readonly Font dateFont = Ui.MakeFont(Ui.Cjk, 15f, FontStyle.Regular);
  readonly Font pillFont = Ui.MakeFont(Ui.Cjk, 12.5f, FontStyle.Bold);
  readonly Font ringFont = Ui.MakeFont(Ui.TextSemibold, 11f, FontStyle.Regular);
  readonly Font monthFont = Ui.MakeFont(Ui.Cjk, 15f, FontStyle.Bold);
  readonly Font yearFont = Ui.MakeFont(Ui.TextRegular, 15f, FontStyle.Regular);
  readonly Font weekdayFont = Ui.MakeFont(Ui.Cjk, 11.5f, FontStyle.Bold);
  readonly Font dayFont = Ui.MakeFont(Ui.TextRegular, 15f, FontStyle.Regular);
  readonly Font dayBoldFont = Ui.MakeFont(Ui.TextSemibold, 15f, FontStyle.Regular);
  readonly Font noteFont = Ui.MakeFont(Ui.Cjk, 12.5f, FontStyle.Regular);
  readonly Font chevronFont = Ui.MakeFont(Ui.TextSemibold, 17f, FontStyle.Regular);

  static readonly Rectangle ClockCard = new Rectangle(4, 4, 344, 206);
  static readonly Rectangle MonthCard = new Rectangle(4, 226, 344, 322);
  static readonly Rectangle FocusButton = new Rectangle(250, 22, 80, 28);
  static readonly Rectangle PrevMonth = new Rectangle(258, 240, 34, 30);
  static readonly Rectangle NextMonth = new Rectangle(296, 240, 34, 30);
  static readonly Rectangle TodayButton = new Rectangle(196, 241, 58, 28);
  const float CardRadius = 26f;
  const int GridLeft = 22, GridTop = 306, CellW = 44, CellH = 34;

  TextureBrush wallpaperBrush;
  Backdrop wallpaperSource;
  Point wallpaperOrigin;

  bool rendering, draggingCard;
  Point mouseClient = new Point(-999, -999);
  Point dragDownScreen, dragOrigLoc;
  int monthOffset, hoveredDay = -1, pressedTarget;
  readonly Spring cardHover = new Spring(0, 0.2, 1.0);
  readonly Spring monthHover = new Spring(0, 0.2, 1.0);
  readonly Spring monthSlide = new Spring(0, 0.34, 0.86);
  readonly Spring dayHover = new Spring(0, 0.16, 1.0);
  readonly Spring focusPress = new Spring(1, 0.22, 0.55);
  readonly Spring navPress = new Spring(1, 0.22, 0.55);
  readonly Spring dragLift = new Spring(0, 0.24, 0.9);
  int dayHoverTarget = -1;

  const int FocusDurationSeconds = 25 * 60;
  bool focusRunning;
  DateTime focusEndUtc;

  public bool DesktopChildMode { get; private set; }

  internal static DateTime BeijingFromUtc(DateTime utc) {
   if (utc.Kind != DateTimeKind.Utc) throw new ArgumentException("必须传入 UTC 时间。", "utc");
   return DateTime.SpecifyKind(utc.AddHours(8), DateTimeKind.Unspecified);
  }

  public HomeForm(AppController controller) {
   DesktopChildMode = NativeDesktopHost.Parent != IntPtr.Zero;
   if (DesktopChildMode) TopLevel = false;
   app = controller;
   AutoScaleMode = AutoScaleMode.None;
   FormBorderStyle = FormBorderStyle.None;
   ShowInTaskbar = true;
   TopMost = false;
   StartPosition = FormStartPosition.Manual;
   int initX = (app.Data.homeX >= 0 && app.Data.homeY >= 0) ? app.Data.homeX : 276;
   int initY = (app.Data.homeX >= 0 && app.Data.homeY >= 0) ? app.Data.homeY : 56;
   Location = new Point(initX, initY);
   Size = new Size(356, 554);
   SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
   AccessibleName = "桌面小组件：北京时间和月历";
   pump = new FramePump(this, Tick);
   Wallpaper.Changed += OnWallpaperChanged;

   var menu = new ContextMenuStrip();
   PanelDrawing.StyleMenu(menu);
   menu.Items.Add("新建桌面文件夹", null, delegate {
    BeginInvoke((Action)delegate { if (!IsDisposed) app.CreateAndOpenNewGroup(PointToScreen(new Point(480, 120))); });
   });
   menu.Items.Add("开始 / 结束 25 分钟专注", null, delegate { ToggleFocusTimer(); });
   menu.Items.Add("回到本月", null, delegate { ChangeMonth(-monthOffset); });
   menu.Items.Add(new ToolStripSeparator());
   menu.Items.Add("退出 MacDesk", null, delegate { BeginInvoke((Action)delegate { if (!IsDisposed) app.Quit(); }); });
   ContextMenuStrip = menu;

   timer.Interval = 1000;
   timer.Tick += delegate {
    if (focusRunning && DateTime.UtcNow >= focusEndUtc) {
     focusRunning = false;
     app.NotifyFocusCompleted();
    }
    EnsureZOrder();
    Render();
   };
   timer.Start();
   RefreshData();
  }

  void EnsureZOrder() {
   if (!DesktopChildMode || !IsHandleCreated) return;
   IntPtr folders = app != null && app.DesktopFolders != null && !app.DesktopFolders.IsDisposed && app.DesktopFolders.IsHandleCreated
    ? app.DesktopFolders.Handle : IntPtr.Zero;
   NativeDesktopHost.EnsureChildTopOrder(Handle, folders);
  }

  protected override bool ShowWithoutActivation { get { return true; } }

  protected override CreateParams CreateParams {
   get {
    var p = base.CreateParams;
    if (DesktopChildMode && NativeDesktopHost.Parent != IntPtr.Zero) {
     p.Parent = NativeDesktopHost.Parent;
     p.Style = (p.Style | 0x40000000) & ~unchecked((int)0x80000000);
    } else {
     p.Parent = IntPtr.Zero;
     p.Style = (p.Style | unchecked((int)0x80000000)) & ~0x40000000;
    }
    p.ExStyle = (p.ExStyle | 0x80000 | 0x80) & ~0x40000;
    return p;
   }
  }

  protected override void CreateHandle() {
   if (DesktopChildMode && NativeDesktopHost.Parent != IntPtr.Zero) {
    try {
     base.CreateHandle();
     return;
    } catch (Exception ex) {
     NativeDesktopHost.ResetStaleHandle(this);
     DesktopChildMode = false;
     NativeDesktopHost.DisableChildHosting("HomeForm child hosting fallback: " + ex.Message);
     TopLevel = true;
    }
   } else if (DesktopChildMode) {
    DesktopChildMode = false;
    TopLevel = true;
   }
   base.CreateHandle();
  }

  bool HitsCard(Point pt) { return ClockCard.Contains(pt) || MonthCard.Contains(pt); }

  protected override void WndProc(ref Message m) {
   if (m.Msg == 0x7B && ContextMenuStrip != null) {
    long p = m.LParam.ToInt64();
    Point screen = p == -1 ? Cursor.Position : new Point((short)(p & 65535), (short)((p >> 16) & 65535));
    app.ShowDesktopMenu(ContextMenuStrip, screen);
    m.Result = IntPtr.Zero;
    return;
   }
   if (m.Msg == 0x84) {
    long packed = m.LParam.ToInt64();
    var point = PointToClient(new Point((short)(packed & 65535), (short)((packed >> 16) & 65535)));
    if (!HitsCard(point)) { m.Result = new IntPtr(-1); return; }
   }
   if (m.Msg == 0x14) { m.Result = new IntPtr(1); return; }
   base.WndProc(ref m);
  }

  public void RefreshData() {
   if (IsDisposed || Disposing) return;
   Render();
  }

  void ToggleFocusTimer() {
   if (focusRunning) focusRunning = false;
   else { focusRunning = true; focusEndUtc = DateTime.UtcNow.AddSeconds(FocusDurationSeconds); }
   Render();
  }

  void ChangeMonth(int delta) {
   if (delta == 0) return;
   monthOffset += delta;
   // Slide the new month in from the side it came from.
   monthSlide.Value = delta > 0 ? 1 : -1;
   monthSlide.Velocity = 0;
   monthSlide.Target = 0;
   hoveredDay = -1;
   Wake();
  }

  // --- Animation & rendering ------------------------------------------------------------

  void Wake() { if (!IsDisposed && IsHandleCreated) pump.Start(); }

  // Offscreen benchmark: hover across calendar days, then flip months.
  internal Bench.Stat[] Bench() {
   IntPtr h = Handle;
   mouseClient = new Point(MonthCard.X + 60, MonthCard.Y + 120);
   int k = 0;
   var hover = MacDeskNative.Bench.Time("calendar day hover", 20 * 12, delegate {
    hoveredDay = 1 + (k++ / 12);
    Tick(1 / 240.0);
   });
   hoveredDay = -1;
   k = 0;
   var flip = MacDeskNative.Bench.Time("calendar month slide", 6 * 60, delegate {
    if (k++ % 60 == 0) ChangeMonth(1);
    Tick(1 / 240.0);
   });
   pump.Stop();
   return new[] { hover, flip };
  }

  bool Tick(double dt) {
   bool active = false;
   cardHover.Target = ClockCard.Contains(mouseClient) ? 1 : 0;
   monthHover.Target = MonthCard.Contains(mouseClient) ? 1 : 0;
   if (dayHoverTarget != hoveredDay) { dayHoverTarget = hoveredDay; dayHover.Snap(0); }
   dayHover.Target = hoveredDay > 0 ? 1 : 0;
   focusPress.Target = pressedTarget == 1 ? 0.92 : 1;
   navPress.Target = pressedTarget == 2 || pressedTarget == 3 ? 0.88 : 1;
   dragLift.Target = draggingCard ? 1 : 0;
   active |= cardHover.Step(dt);
   active |= monthHover.Step(dt);
   active |= monthSlide.Step(dt);
   active |= dayHover.Step(dt);
   active |= focusPress.Step(dt);
   active |= navPress.Step(dt);
   active |= dragLift.Step(dt);
   Render();
   return active;
  }

  TextureBrush WallpaperBrush() {
   Backdrop bd = Wallpaper.Current;
   if (bd == null) return null;
   Point origin = IsHandleCreated ? PointToScreen(Point.Empty) : Location;
   if (wallpaperBrush == null || wallpaperSource != bd) {
    if (wallpaperBrush != null) wallpaperBrush.Dispose();
    wallpaperBrush = new TextureBrush(bd.Image, WrapMode.Clamp);
    wallpaperSource = bd;
    wallpaperOrigin = new Point(int.MinValue, 0);
   }
   if (wallpaperOrigin != origin) {
    wallpaperBrush.ResetTransform();
    wallpaperBrush.TranslateTransform(bd.Area.X - origin.X, bd.Area.Y - origin.Y);
    wallpaperOrigin = origin;
   }
   return wallpaperBrush;
  }

  void OnWallpaperChanged(object sender, EventArgs e) {
   if (IsDisposed || !IsHandleCreated) return;
   BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) Render(); });
  }

  // --- Render caches -----------------------------------------------------------------------
  // Everything that does not change between animation frames is rendered once into a layer;
  // a hover / month-slide frame only copies layers and draws the one moving element.
  Bitmap cardsCache, clockCache, headerCache, gridCache, footerCache;
  string cardsKey, clockKey, headerKey, gridKey, footerKey;
  readonly System.Drawing.Imaging.ImageAttributes blitAttributes = new System.Drawing.Imaging.ImageAttributes();
  readonly System.Drawing.Imaging.ColorMatrix blitMatrix = new System.Drawing.Imaging.ColorMatrix();
  static readonly Rectangle HeaderRegion = new Rectangle(16, 236, 250, 38);
  static readonly Rectangle GridRegion = new Rectangle(GridLeft - 4, GridTop - 4, CellW * 7 + 8, CellH * 6 + 8);
  static readonly Rectangle FooterRegion = new Rectangle(20, MonthCard.Bottom - 34, 312, 26);

  static Bitmap Layer(Rectangle region, out Graphics g) {
   var bmp = new Bitmap(Math.Max(1, region.Width), Math.Max(1, region.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
   g = Graphics.FromImage(bmp);
   g.Clear(Color.Transparent);
   Ui.PrepareQuality(g);
   g.TranslateTransform(-region.X, -region.Y);
   return bmp;
  }
  static void Replace(ref Bitmap cache, Bitmap next) { if (cache != null) cache.Dispose(); cache = next; }

  void DrawCard(Graphics g, Rectangle bounds, float lift) {
   var r = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
   for (int i = 0; i < 3; i++) {
    float spread = 2f + i * 3f + lift * 4f;
    var s = new RectangleF(r.X - spread * 0.5f, r.Y + 3f + lift * 5f, r.Width + spread, r.Height + spread * 0.6f);
    using (var path = Ui.Squircle(s, CardRadius + spread * 0.5f))
    using (var b = new SolidBrush(Color.FromArgb((int)(14 - i * 3 + lift * 8), 0, 0, 0))) g.FillPath(b, path);
   }
   using (var shape = Ui.Squircle(r, CardRadius)) Material.Fill(g, shape, WallpaperBrush(), Material.WidgetTint, Material.WidgetFallback);
   Material.Rim(g, r, CardRadius, 0.7f);
  }

  static readonly string[] WeekLetters = { "日", "一", "二", "三", "四", "五", "六" };
  void DrawStaticCards(Graphics g) {
   DrawCard(g, ClockCard, dragLift.F);
   DrawCard(g, MonthCard, dragLift.F);
   using (var f = Ui.Centered(false))
    for (int c = 0; c < 7; c++)
     using (var b = new SolidBrush(c == 0 || c == 6 ? Ui.TertiaryLabel : Ui.SecondaryLabel))
      g.DrawString(WeekLetters[c], weekdayFont, b, new RectangleF(GridLeft + c * CellW, 280, CellW, 22), f);
  }

  void DrawCards(Graphics g) {
   if (dragLift.Value > 0.01) { DrawStaticCards(g); return; } // live only while the widget is carried
   Backdrop bd = Wallpaper.Current;
   string key = (IsHandleCreated ? PointToScreen(Point.Empty) : Location) + "|" + (bd == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bd));
   if (cardsCache == null || cardsKey != key) {
    Graphics cg;
    Replace(ref cardsCache, Layer(new Rectangle(Point.Empty, Size), out cg));
    using (cg) DrawStaticCards(cg);
    cardsKey = key;
   }
   var mode = g.CompositingMode;
   g.CompositingMode = CompositingMode.SourceCopy;
   Ui.Blit(g, cardsCache, 0, 0);
   g.CompositingMode = mode;
  }

  void Render() {
   if (IsDisposed || Disposing || rendering || !IsHandleCreated || Width < 1 || Height < 1) return;
   rendering = true;
   try {
    var g = surface.Begin(Size);
    surface.Clear(new Rectangle(Point.Empty, Size));
    Ui.PrepareQuality(g);
    DateTime now = BeijingFromUtc(DateTime.UtcNow);
    DrawCards(g);
    DrawClock(g, now);
    DrawCalendar(g, now);
    surface.Present(Handle, DesktopChildMode ? (Point?)null : Location, 255, null);
   } finally { rendering = false; }
   EnsureZOrder();
  }

  static readonly string[] WeekNames = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
  static readonly string[] MonthNames = { "一月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "十一月", "十二月" };

  void DrawClock(Graphics g, DateTime now) {
   string time = now.ToString("HH:mm"), sub;
   Color subColor;
   if (focusRunning) {
    TimeSpan rem = focusEndUtc - DateTime.UtcNow;
    if (rem.TotalSeconds < 0) rem = TimeSpan.Zero;
    sub = String.Format("专注中 · 剩余 {0:00}:{1:00}", (int)rem.TotalMinutes, rem.Seconds);
    subColor = Ui.Orange;
   } else {
    sub = now.Month + "月" + now.Day + "日 " + WeekNames[(int)now.DayOfWeek];
    subColor = Ui.SecondaryLabel;
   }
   string key = time + "|" + sub;
   if (clockCache == null || clockKey != key) {
    Graphics cg;
    Replace(ref clockCache, Layer(ClockCard, out cg));
    using (cg) using (var lead = Ui.Leading()) {
     using (var b = new SolidBrush(Ui.SecondaryLabel)) cg.DrawString("北京时间", captionFont, b, new RectangleF(24, 22, 150, 28), lead);
     using (var b = new SolidBrush(Ui.Label)) cg.DrawString(time, clockFont, b, new RectangleF(16, 50, 320, 100), lead);
     using (var b = new SolidBrush(subColor)) cg.DrawString(sub, dateFont, b, new RectangleF(24, 156, 300, 28), lead);
    }
    clockKey = key;
   }
   Ui.Blit(g, clockCache, ClockCard.X, ClockCard.Y);
   DrawFocusControl(g);
  }

  void DrawFocusControl(Graphics g) {
   float hover = cardHover.F;
   if (focusRunning) {
    // Activity-ring style countdown in the top-right corner.
    TimeSpan rem = focusEndUtc - DateTime.UtcNow;
    float progress = (float)Math.Max(0.0, Math.Min(1.0, 1.0 - rem.TotalSeconds / FocusDurationSeconds));
    var ring = new RectangleF(ClockCard.Right - 62, ClockCard.Y + 18, 42, 42);
    using (var track = new Pen(Color.FromArgb(60, 255, 159, 10), 5f)) g.DrawEllipse(track, ring);
    using (var arc = new Pen(Ui.Orange, 5f)) { arc.StartCap = arc.EndCap = LineCap.Round; g.DrawArc(arc, ring, -90, Math.Max(2f, 360f * progress)); }
    using (var f = Ui.Centered(false)) using (var b = new SolidBrush(Ui.Label)) g.DrawString(Math.Max(0, (int)Math.Ceiling(rem.TotalMinutes)).ToString(), ringFont, b, ring, f);
    return;
   }
   if (hover <= 0.01f) return;
   float s = focusPress.F;
   var r = Scale(FocusButton, s);
   bool over = FocusButton.Contains(mouseClient);
   using (var path = Ui.Squircle(r, r.Height / 2f))
   using (var b = new SolidBrush(Color.FromArgb((int)((over ? 255 : 220) * hover), 255, 159, 10))) g.FillPath(b, path);
   using (var f = Ui.Centered(false)) using (var b = new SolidBrush(Color.FromArgb((int)(255 * hover), 255, 255, 255))) g.DrawString("专注 25", pillFont, b, r, f);
  }

  static RectangleF Scale(Rectangle r, float s) {
   float w = r.Width * s, h = r.Height * s;
   return new RectangleF(r.X + (r.Width - w) / 2f, r.Y + (r.Height - h) / 2f, w, h);
  }

  DateTime DisplayedMonth(DateTime today) { return new DateTime(today.Year, today.Month, 1).AddMonths(monthOffset); }

  int HitCalendarDay(Point pt, DateTime month) {
   if (!MonthCard.Contains(pt)) return -1;
   int offset = (int)month.DayOfWeek, days = DateTime.DaysInMonth(month.Year, month.Month);
   for (int d = 1; d <= days; d++) {
    int cell = offset + d - 1;
    if (new Rectangle(GridLeft + (cell % 7) * CellW, GridTop + (cell / 7) * CellH, CellW, CellH).Contains(pt)) return d;
   }
   return -1;
  }

  string NoteFor(DateTime month, int day) {
   string note = null;
   if (app != null && app.Data != null && app.Data.notes != null) app.Data.notes.TryGetValue(new DateTime(month.Year, month.Month, day).ToString("yyyy-MM-dd"), out note);
   return String.IsNullOrEmpty(note) ? null : note;
  }

  RectangleF DayCircle(DateTime month, int day) {
   int cell = (int)month.DayOfWeek + day - 1;
   return new RectangleF(GridLeft + (cell % 7) * CellW + (CellW - 30) / 2f, GridTop + (cell / 7) * CellH + 1, 30, 30);
  }

  void DrawCalendar(Graphics g, DateTime now) {
   DateTime today = now.Date;
   DateTime month = DisplayedMonth(today);
   float slide = monthSlide.F;
   float hover = monthHover.F;
   float headerAlpha = 0.35f + 0.65f * (1 - Math.Min(1f, Math.Abs(slide)));
   float gridAlpha = 1 - Math.Min(1f, Math.Abs(slide)) * 0.8f;

   // Header (month name in iOS red + year), cached per month.
   string hk = month.ToString("yyyy-MM");
   if (headerCache == null || headerKey != hk) {
    Graphics cg;
    Replace(ref headerCache, Layer(HeaderRegion, out cg));
    using (cg) using (var lead = Ui.Leading()) {
     string name = MonthNames[month.Month - 1];
     var nameRect = new RectangleF(24, 238, 160, 34);
     using (var b = new SolidBrush(Ui.Red)) cg.DrawString(name, monthFont, b, nameRect, lead);
     float nameW = cg.MeasureString(name, monthFont).Width;
     using (var b = new SolidBrush(Ui.SecondaryLabel)) cg.DrawString(month.Year.ToString(), yearFont, b, new RectangleF(nameRect.X + nameW - 2, 238, 80, 34), lead);
    }
    headerKey = hk;
   }
   Ui.Blit(g, headerCache, HeaderRegion.X + (int)Math.Round(slide * 24), HeaderRegion.Y, headerAlpha, blitAttributes, blitMatrix);

   if (hover > 0.01f || monthOffset != 0) {
    float vis = Math.Max(hover, monthOffset != 0 ? 1f : 0f);
    DrawChevron(g, PrevMonth, "‹", vis, pressedTarget == 2 ? navPress.F : 1f);
    DrawChevron(g, NextMonth, "›", vis, pressedTarget == 3 ? navPress.F : 1f);
    if (monthOffset != 0) {
     bool over = TodayButton.Contains(mouseClient);
     using (var path = Ui.Squircle(TodayButton, TodayButton.Height / 2f))
     using (var b = new SolidBrush(Color.FromArgb(over ? 70 : 44, 255, 255, 255))) g.FillPath(b, path);
     using (var f = Ui.Centered(false)) using (var b = new SolidBrush(Ui.Red)) g.DrawString("今天", pillFont, b, TodayButton, f);
    }
   }

   // Day grid (numbers, today's red disc, note dots), cached per month + notes.
   int days = DateTime.DaysInMonth(month.Year, month.Month);
   var noteSig = new System.Text.StringBuilder(hk).Append('|').Append(today.ToString("yyyy-MM-dd"));
   for (int d = 1; d <= days; d++) if (NoteFor(month, d) != null) noteSig.Append(',').Append(d);
   string gk = noteSig.ToString();
   if (gridCache == null || gridKey != gk) {
    Graphics cg;
    Replace(ref gridCache, Layer(GridRegion, out cg));
    using (cg) using (var f = Ui.Centered(false)) {
     for (int d = 1; d <= days; d++) {
      int col = ((int)month.DayOfWeek + d - 1) % 7;
      RectangleF circle = DayCircle(month, d);
      bool isToday = month.Year == today.Year && month.Month == today.Month && d == today.Day;
      if (isToday) using (var b = new SolidBrush(Ui.Red)) cg.FillEllipse(b, circle);
      Color ink = isToday ? Color.White : (col == 0 || col == 6 ? Ui.SecondaryLabel : Ui.Label);
      using (var b = new SolidBrush(ink)) cg.DrawString(d.ToString(), isToday ? dayBoldFont : dayFont, b, new RectangleF(circle.X - 4, circle.Y, circle.Width + 8, circle.Height), f);
      if (NoteFor(month, d) != null)
       using (var b = new SolidBrush(isToday ? Ui.Red : Ui.Orange)) cg.FillEllipse(b, circle.X + 15 - 2.5f, circle.Y + CellH - 5.5f, 5, 5);
     }
    }
    gridKey = gk;
   }

   var oldClip = g.Clip;
   g.SetClip(new RectangleF(MonthCard.X + 6, GridRegion.Y, MonthCard.Width - 12, GridRegion.Height));
   int dx = (int)Math.Round(slide * 60f);
   bool hoveredIsToday = month.Year == today.Year && month.Month == today.Month && hoveredDay == today.Day;
   if (hoveredDay > 0 && hoveredDay <= days && !hoveredIsToday) {
    // The one live element: the hover disc grows under the number (drawn before the grid).
    float h = dayHover.F;
    RectangleF c = DayCircle(month, hoveredDay);
    var hc = new RectangleF(c.X + dx + 15 * (1 - h), c.Y + 15 * (1 - h), 30 * h, 30 * h);
    using (var b = new SolidBrush(Color.FromArgb((int)(52 * gridAlpha), 255, 255, 255))) g.FillEllipse(b, hc);
   }
   Ui.Blit(g, gridCache, GridRegion.X + dx, GridRegion.Y, gridAlpha, blitAttributes, blitMatrix);
   g.Clip = oldClip;
   oldClip.Dispose();

   string activeNote = hoveredDay > 0 && hoveredDay <= days ? NoteFor(month, hoveredDay) : null;
   string todayNote = month.Year == today.Year && month.Month == today.Month ? NoteFor(month, today.Day) : null;
   string footer = activeNote != null ? hoveredDay + "日 · " + activeNote : todayNote != null ? "今天 · " + todayNote : (hover > 0.5f ? "点日期写备忘 · 滚轮切换月份" : "");
   if (!String.IsNullOrEmpty(footer)) {
    bool isNote = activeNote != null || todayNote != null;
    string fk = (isNote ? "n" : "h") + footer;
    if (footerCache == null || footerKey != fk) {
     Graphics cg;
     Replace(ref footerCache, Layer(FooterRegion, out cg));
     using (cg) using (var lead = Ui.Leading()) using (var b = new SolidBrush(isNote ? Ui.Orange : Ui.TertiaryLabel)) cg.DrawString(footer, noteFont, b, new RectangleF(24, MonthCard.Bottom - 32, 300, 22), lead);
     footerKey = fk;
    }
    Ui.Blit(g, footerCache, FooterRegion.X, FooterRegion.Y);
   }
  }

  void DrawChevron(Graphics g, Rectangle r, string glyph, float alpha, float scale) {
   bool over = r.Contains(mouseClient);
   var rr = Scale(r, scale);
   using (var path = Ui.Squircle(rr, rr.Height / 2f))
   using (var b = new SolidBrush(Color.FromArgb((int)((over ? 64 : 34) * alpha), 255, 255, 255))) g.FillPath(b, path);
   using (var f = Ui.Centered(false)) using (var b = new SolidBrush(Ui.WithAlpha(Ui.Red, alpha))) g.DrawString(glyph, chevronFont, b, new RectangleF(rr.X, rr.Y - 2, rr.Width, rr.Height), f);
  }

  protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Render(); }
  protected override void OnPaint(PaintEventArgs e) { }
  protected override void OnPaintBackground(PaintEventArgs e) { }

  // --- Input ----------------------------------------------------------------------------

  int TargetAt(Point pt, out int day) {
   DateTime month = DisplayedMonth(BeijingFromUtc(DateTime.UtcNow).Date);
   day = HitCalendarDay(pt, month);
   if (FocusButton.Contains(pt) && (cardHover.Value > 0.3 || focusRunning)) return 1;
   if (focusRunning && new Rectangle(ClockCard.Right - 66, ClockCard.Y + 14, 50, 50).Contains(pt)) return 1;
   bool navVisible = monthHover.Value > 0.3 || monthOffset != 0;
   if (navVisible && PrevMonth.Contains(pt)) return 2;
   if (navVisible && NextMonth.Contains(pt)) return 3;
   if (monthOffset != 0 && TodayButton.Contains(pt)) return 4;
   if (day > 0) return 5;
   return 0;
  }

  protected override void OnMouseDown(MouseEventArgs e) {
   base.OnMouseDown(e);
   if (e.Button != MouseButtons.Left) return;
   int day;
   pressedTarget = TargetAt(e.Location, out day);
   if (pressedTarget == 0 && HitsCard(e.Location)) {
    draggingCard = true;
    dragDownScreen = PointToScreen(e.Location);
    dragOrigLoc = Location;
    Capture = true;
    Cursor = Cursors.SizeAll;
   }
   Wake();
  }

  protected override void OnMouseMove(MouseEventArgs e) {
   base.OnMouseMove(e);
   if (draggingCard && e.Button == MouseButtons.Left) {
    Point cur = PointToScreen(e.Location);
    Location = new Point(dragOrigLoc.X + cur.X - dragDownScreen.X, dragOrigLoc.Y + cur.Y - dragDownScreen.Y);
    Wake();
    return;
   }
   mouseClient = e.Location;
   int day;
   int target = TargetAt(e.Location, out day);
   hoveredDay = day;
   Cursor = target != 0 ? Cursors.Hand : Cursors.Default;
   Wake();
  }

  protected override void OnMouseLeave(EventArgs e) {
   base.OnMouseLeave(e);
   mouseClient = new Point(-999, -999);
   hoveredDay = -1;
   pressedTarget = 0;
   Cursor = Cursors.Default;
   Wake();
  }

  protected override void OnMouseWheel(MouseEventArgs e) {
   base.OnMouseWheel(e);
   if (MonthCard.Contains(e.Location)) ChangeMonth(e.Delta > 0 ? -1 : 1);
  }

  protected override void OnMouseUp(MouseEventArgs e) {
   base.OnMouseUp(e);
   int pressed = pressedTarget;
   pressedTarget = 0;
   Wake();
   if (draggingCard) {
    draggingCard = false;
    Capture = false;
    Cursor = Cursors.Default;
    app.SetHomePosition(Left, Top);
    if (app.DesktopFolders != null) app.DesktopFolders.RefreshData();
    return;
   }
   if (e.Button != MouseButtons.Left) return;
   int day;
   int target = TargetAt(e.Location, out day);
   if (target != pressed) return;
   if (target == 1) { ToggleFocusTimer(); return; }
   if (target == 2) { ChangeMonth(-1); return; }
   if (target == 3) { ChangeMonth(1); return; }
   if (target == 4) { ChangeMonth(-monthOffset); return; }
   if (target == 5 && day > 0) {
    DateTime month = DisplayedMonth(BeijingFromUtc(DateTime.UtcNow).Date);
    string dateKey = new DateTime(month.Year, month.Month, day).ToString("yyyy-MM-dd");
    Point screenPos = PointToScreen(new Point(MonthCard.Left, MonthCard.Bottom - 80));
    BeginInvoke((Action)delegate { if (!IsDisposed) app.EditCalendarNote(dateKey, screenPos); });
   }
  }

  protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); if (IsHandleCreated && !rendering) Render(); }

  protected override void Dispose(bool disposing) {
   if (disposing) {
    Wallpaper.Changed -= OnWallpaperChanged;
    pump.Dispose();
    timer.Dispose();
    surface.Dispose();
    if (wallpaperBrush != null) { wallpaperBrush.Dispose(); wallpaperBrush = null; }
    captionFont.Dispose(); clockFont.Dispose(); dateFont.Dispose(); pillFont.Dispose(); ringFont.Dispose();
    monthFont.Dispose(); yearFont.Dispose(); weekdayFont.Dispose(); dayFont.Dispose(); dayBoldFont.Dispose();
    noteFont.Dispose(); chevronFont.Dispose();
    blitAttributes.Dispose();
    foreach (var c in new[] { cardsCache, clockCache, headerCache, gridCache, footerCache }) if (c != null) c.Dispose();
    if (ContextMenuStrip != null) ContextMenuStrip.Dispose();
   }
   base.Dispose(disposing);
  }
 }
}
