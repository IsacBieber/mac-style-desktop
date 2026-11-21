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

  readonly Font chevronFont = Ui.MakeFont(Ui.TextSemibold, 17f, FontStyle.Regular);

  static readonly Rectangle ClockCard = new Rectangle(4, 4, 344, 206);
  const int MonthTop = 226, CardWidth = 344, WidgetWidth = 356;
  const int CalendarBottomPadding = 11, NoteHeight = 24;
  static readonly Rectangle PrevMonth = new Rectangle(258, 240, 34, 30);
  static readonly Rectangle NextMonth = new Rectangle(296, 240, 34, 30);
  static readonly Rectangle TodayButton = new Rectangle(196, 241, 58, 28);
  const float CardRadius = 26f;
  const int GridLeft = 22, GridTop = 306, CellW = 44, CellH = 34;

  TextureBrush wallpaperBrush;
  Backdrop wallpaperSource;
  Point wallpaperOrigin;
  Rectangle wallpaperRegion;

  bool rendering, draggingCard;
  Point mouseClient = new Point(-999, -999);
  Point dragDownScreen, dragOrigLoc, dragTargetLocation;
  enum CalendarTarget { None, PreviousMonth, NextMonth, Today, Day }
  int monthOffset, hoveredDay = -1;
  CalendarTarget pressedTarget;
  readonly Spring monthHover = new Spring(0, 0.2, 1.0);
  readonly Spring monthSlide = new Spring(0, 0.34, 0.86);
  readonly Spring dayHover = new Spring(0, 0.16, 1.0);
  readonly Spring navPress = new Spring(1, 0.22, 0.55);
  int dayHoverTarget = -1;
  long renderedMinute = -1;
  Rectangle MonthCard, GridRegion, FooterRegion;
  DateTime calendarMonth;
  DesktopData calendarData;
  readonly string[] monthNotes = new string[32];
  bool gridDirty = true;

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
   UpdateCalendarLayout(DisplayedMonth(BeijingFromUtc(DateTime.UtcNow)));
   SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
   AccessibleName = "桌面小组件：北京时间和月历";
   AccessibleDescription = "点击日期写备忘，滚轮切换月份。";
   pump = new FramePump(this, Tick);
   Wallpaper.Changed += OnWallpaperChanged;

   var menu = new ContextMenuStrip();
   PanelDrawing.StyleMenu(menu);
   menu.Items.Add("新建桌面文件夹", null, delegate {
    BeginInvoke((Action)delegate { if (!IsDisposed) app.CreateAndOpenNewGroup(PointToScreen(new Point(480, 120))); });
   });
   menu.Items.Add("回到本月", null, delegate { ChangeMonth(-monthOffset); });
   menu.Items.Add(new ToolStripSeparator());
   menu.Items.Add("退出 MacDesk", null, delegate { BeginInvoke((Action)delegate { if (!IsDisposed) app.Quit(); }); });
   ContextMenuStrip = menu;

   timer.Interval = 1000;
   timer.Tick += delegate {
    EnsureZOrder();
    if (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute != renderedMinute) Render();
    else Wallpaper.RefreshIfNeeded();
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

  // Offscreen benchmark: hover across calendar days, flip months, then drag the widgets.
  internal Bench.Stat[] Bench() {
   IntPtr h = Handle;
   string dir = Environment.GetEnvironmentVariable("MACDESK_BENCH_FRAMES");
   Render();
   if (dir != null) surface.SaveSnapshot(System.IO.Path.Combine(dir, "home.png"));
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
   var drag = BenchDrag();
   pump.Stop();
   return new[] { hover, flip, drag };
  }

  private Bench.Stat BenchDrag() {
   Point original = Location;
   dragOrigLoc = dragTargetLocation = original;
   dragDownScreen = PointToScreen(new Point(12, 22));
   draggingCard = true;
   try {
    int sample = 0;
    return MacDeskNative.Bench.Time("clock and calendar drag", 240, delegate {
     for (int i = 0; i < 4; i++) {
      int index = sample++;
      Point client = PointToClient(new Point(dragDownScreen.X + index % 340, dragDownScreen.Y + (index % 128) / 2));
      OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, client.X, client.Y, 0));
     }
     Tick(1 / 240.0);
    });
   } finally {
    Location = original;
    draggingCard = false;
   }
  }

  bool Tick(double dt) {
   if (draggingCard) {
    ApplyDragTarget();
    return true;
   }
   bool active = false;
   monthHover.Target = MonthCard.Contains(mouseClient) ? 1 : 0;
   if (dayHoverTarget != hoveredDay) { dayHoverTarget = hoveredDay; dayHover.Snap(0); }
   dayHover.Target = hoveredDay > 0 ? 1 : 0;
   navPress.Target = pressedTarget == CalendarTarget.PreviousMonth || pressedTarget == CalendarTarget.NextMonth ? 0.88 : 1;
   active |= monthHover.Step(dt);
   active |= monthSlide.Step(dt);
   active |= dayHover.Step(dt);
   active |= navPress.Step(dt);
   Render();
   return active;
  }

  TextureBrush WallpaperBrush() {
   Backdrop bd = Wallpaper.Current;
   if (bd == null) return null;
   Point origin = IsHandleCreated ? PointToScreen(Point.Empty) : Location;
   Rectangle region = Rectangle.Inflate(Rectangle.Union(ClockCard, MonthCard), 4, 4);
   region.Offset(origin);
   region.Intersect(bd.Area);
   if (region.Width <= 0 || region.Height <= 0) return null;
   if (wallpaperBrush == null || wallpaperSource != bd || !wallpaperRegion.Contains(region)) {
    if (wallpaperBrush != null) wallpaperBrush.Dispose();
    wallpaperRegion = Rectangle.Intersect(Rectangle.Inflate(region, 96, 96), bd.Area);
    wallpaperBrush = bd.CreateBrush(wallpaperRegion, origin);
    wallpaperSource = bd;
    wallpaperOrigin = new Point(int.MinValue, 0);
   }
   if (wallpaperOrigin != origin) {
    wallpaperBrush.ResetTransform();
    wallpaperBrush.TranslateTransform(wallpaperRegion.X - origin.X, wallpaperRegion.Y - origin.Y);
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
  string footerKey;
  Point cardsOrigin;
  Backdrop cardsSource;
  DateTime headerMonth, gridToday;
  readonly System.Drawing.Imaging.ImageAttributes blitAttributes = new System.Drawing.Imaging.ImageAttributes();
  readonly System.Drawing.Imaging.ColorMatrix blitMatrix = new System.Drawing.Imaging.ColorMatrix();
  static readonly Rectangle HeaderRegion = new Rectangle(16, 236, 250, 38);

  static Bitmap Layer(Rectangle region, out Graphics g) {
   var bmp = new Bitmap(Math.Max(1, region.Width), Math.Max(1, region.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
   g = Graphics.FromImage(bmp);
   g.Clear(Color.Transparent);
   Ui.PrepareQuality(g);
   g.TranslateTransform(-region.X, -region.Y);
   return bmp;
  }
  static void Replace(ref Bitmap cache, Bitmap next) { if (cache != null) cache.Dispose(); cache = next; }

  void DrawCard(Graphics g, Rectangle bounds) {
   var r = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
   for (int i = 0; i < 3; i++) {
    float spread = 2f + i * 3f;
    var s = new RectangleF(r.X - spread * 0.5f, r.Y + 3f, r.Width + spread, r.Height + spread * 0.6f);
    using (var path = Ui.Squircle(s, CardRadius + spread * 0.5f))
    using (var b = new SolidBrush(Color.FromArgb(14 - i * 3, 0, 0, 0))) g.FillPath(b, path);
   }
   using (var shape = Ui.Squircle(r, CardRadius)) Material.Fill(g, shape, WallpaperBrush(), Material.WidgetTint, Material.WidgetFallback);
   Material.Rim(g, r, CardRadius, 0.7f);
  }

  static readonly string[] WeekLetters = { "日", "一", "二", "三", "四", "五", "六" };
  void DrawStaticCards(Graphics g) {
   DrawCard(g, ClockCard);
   DrawCard(g, MonthCard);
   using (var f = Ui.Centered(false))
    for (int c = 0; c < 7; c++)
     using (var b = new SolidBrush(c == 0 || c == 6 ? Ui.TertiaryLabel : Ui.SecondaryLabel))
      TextStyle.Draw(g, WeekLetters[c], Ui.Cjk, FontStyle.Bold, 12f, b.Color, new RectangleF(GridLeft + c * CellW, 280, CellW, 22), f, 0f);
  }

  void DrawCards(Graphics g) {
   Backdrop bd = Wallpaper.Current;
   Point origin = PointToScreen(Point.Empty);
   if (cardsCache == null || cardsCache.Size != Size || cardsOrigin != origin || cardsSource != bd) {
    Graphics cg;
    Replace(ref cardsCache, Layer(new Rectangle(Point.Empty, Size), out cg));
    using (cg) DrawStaticCards(cg);
    cardsOrigin = origin;
    cardsSource = bd;
   }
   var mode = g.CompositingMode;
   g.CompositingMode = CompositingMode.SourceCopy;
   Ui.Blit(g, cardsCache, 0, 0);
   g.CompositingMode = mode;
  }

  void Render() {
   if (IsDisposed || Disposing || rendering || draggingCard || !IsHandleCreated || Width < 1 || Height < 1) return;
   rendering = true;
   try {
    DateTime utc = DateTime.UtcNow, now = BeijingFromUtc(utc);
    long minute = utc.Ticks / TimeSpan.TicksPerMinute;
    DateTime month = DisplayedMonth(now);
    UpdateCalendarLayout(month);
    var g = surface.Begin(Size);
    Ui.PrepareQuality(g);
    DrawCards(g);
    DrawClock(g, now, minute != renderedMinute);
    DrawCalendar(g, now.Date, month);
    surface.Present(Handle, DesktopChildMode ? (Point?)null : Location, 255, null);
    renderedMinute = minute;
   } finally { rendering = false; }
  }

  static readonly string[] WeekNames = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
  static readonly string[] MonthNames = { "一月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "十一月", "十二月" };

  void DrawClock(Graphics g, DateTime now, bool minuteChanged) {
   if (clockCache == null || minuteChanged) {
    string time = now.ToString("HH:mm");
    string date = now.Month + "月" + now.Day + "日 " + WeekNames[(int)now.DayOfWeek];
    Graphics cg;
    Replace(ref clockCache, Layer(ClockCard, out cg));
    using (cg) using (var lead = Ui.Leading()) {
     TextStyle.Draw(cg, "北京时间", Ui.Cjk, FontStyle.Regular, 13.5f, Ui.SecondaryLabel, new RectangleF(24, 22, 150, 28), lead, 0.35f);
     TextStyle.Draw(cg, time, Ui.DisplaySemibold, FontStyle.Regular, 74f, Ui.Label, new RectangleF(18, 52, 320, 100), lead, 0f);
     TextStyle.Draw(cg, date, Ui.Cjk, FontStyle.Regular, 16f, Ui.SecondaryLabel, new RectangleF(24, 156, 300, 28), lead, 0.45f);
    }
   }
   Ui.Blit(g, clockCache, ClockCard.X, ClockCard.Y);
  }

  static RectangleF Scale(Rectangle r, float s) {
   float w = r.Width * s, h = r.Height * s;
   return new RectangleF(r.X + (r.Width - w) / 2f, r.Y + (r.Height - h) / 2f, w, h);
  }

  DateTime DisplayedMonth(DateTime today) { return new DateTime(today.Year, today.Month, 1).AddMonths(monthOffset); }

  static int CalendarRows(DateTime month) {
   return ((int)month.DayOfWeek + DateTime.DaysInMonth(month.Year, month.Month) + 6) / 7;
  }

  internal static Rectangle CalendarCardBounds(DateTime month, bool hasNotes) {
   int bottom = GridTop + CalendarRows(month) * CellH + CalendarBottomPadding + (hasNotes ? NoteHeight : 0);
   return Rectangle.FromLTRB(4, MonthTop, 4 + CardWidth, bottom);
  }

  private void UpdateCalendarLayout(DateTime month) {
   if (month == calendarMonth && app.Data == calendarData) return;
   gridDirty |= month != calendarMonth;
   calendarMonth = month;
   calendarData = app.Data;
   bool hasNotes = false;
   int days = DateTime.DaysInMonth(month.Year, month.Month);
   bool lookupNotes = calendarData.notes != null && calendarData.notes.Count > 0;
   for (int day = 1; day <= days; day++) {
    string note = null;
    if (lookupNotes) calendarData.notes.TryGetValue(month.AddDays(day - 1).ToString("yyyy-MM-dd"), out note);
    if (String.IsNullOrEmpty(note)) note = null;
    gridDirty |= (monthNotes[day] != null) != (note != null);
    monthNotes[day] = note;
    hasNotes |= note != null;
   }
   Array.Clear(monthNotes, days + 1, monthNotes.Length - days - 1);
   int gridHeight = CalendarRows(month) * CellH;
   MonthCard = CalendarCardBounds(month, hasNotes);
   GridRegion = new Rectangle(GridLeft - 4, GridTop - 4, CellW * 7 + 8, gridHeight + 6);
   FooterRegion = new Rectangle(20, GridTop + gridHeight + 1, 312, NoteHeight);
   int height = MonthCard.Bottom + 12;
   if (Size != new Size(WidgetWidth, height)) {
    bool grew = height > Height;
    Size = new Size(WidgetWidth, height);
    if (grew && Visible) FitCalendarOnScreen();
   }
  }

  void FitCalendarOnScreen() {
   Rectangle work = Screen.FromControl(this).WorkingArea;
   int top = PointToScreen(Point.Empty).Y;
   int limit = Math.Max(work.Top, work.Bottom - Height);
   if (top > limit) Top -= top - limit;
  }

  internal static int HitCalendarDay(Point pt, DateTime month) {
   int x = pt.X - GridLeft, y = pt.Y - GridTop;
   if (x < 0 || x >= 7 * CellW || y < 0 || y >= CalendarRows(month) * CellH) return -1;
   int day = (y / CellH) * 7 + x / CellW - (int)month.DayOfWeek + 1;
   return day >= 1 && day <= DateTime.DaysInMonth(month.Year, month.Month) ? day : -1;
  }

  internal static RectangleF DayCircle(DateTime month, int day) {
   int cell = (int)month.DayOfWeek + day - 1;
   return new RectangleF(GridLeft + (cell % 7) * CellW + (CellW - 30) / 2f, GridTop + (cell / 7) * CellH + 1, 30, 30);
  }

  void DrawCalendar(Graphics g, DateTime today, DateTime month) {
   float slide = monthSlide.F;
   float hover = monthHover.F;
   float headerAlpha = 0.35f + 0.65f * (1 - Math.Min(1f, Math.Abs(slide)));
   float gridAlpha = 1 - Math.Min(1f, Math.Abs(slide)) * 0.8f;

   // Header (month name in iOS red + year), cached per month.
   if (headerCache == null || headerMonth != month) {
    Graphics cg;
    Replace(ref headerCache, Layer(HeaderRegion, out cg));
    using (cg) using (var lead = Ui.Leading()) {
     string name = MonthNames[month.Month - 1];
     var nameRect = new RectangleF(24, 238, 160, 34);
     TextStyle.Draw(cg, name, Ui.Cjk, FontStyle.Bold, 16.5f, Ui.Red, nameRect, lead, 0f);
     float nameW = TextStyle.Width(name, Ui.Cjk, FontStyle.Bold, 16.5f);
     TextStyle.Draw(cg, month.Year.ToString(), Ui.TextRegular, FontStyle.Regular, 16f, Ui.SecondaryLabel, new RectangleF(nameRect.X + nameW + 6, 238, 80, 34), lead, 0.3f);
    }
    headerMonth = month;
   }
   Ui.Blit(g, headerCache, HeaderRegion.X + (int)Math.Round(slide * 24), HeaderRegion.Y, headerAlpha, blitAttributes, blitMatrix);

   if (hover > 0.01f || monthOffset != 0) {
    float vis = Math.Max(hover, monthOffset != 0 ? 1f : 0f);
    DrawChevron(g, PrevMonth, "‹", vis, pressedTarget == CalendarTarget.PreviousMonth ? navPress.F : 1f);
    DrawChevron(g, NextMonth, "›", vis, pressedTarget == CalendarTarget.NextMonth ? navPress.F : 1f);
    if (monthOffset != 0) {
     bool over = TodayButton.Contains(mouseClient);
     using (var path = Ui.Squircle(TodayButton, TodayButton.Height / 2f))
     using (var b = new SolidBrush(Color.FromArgb(over ? 70 : 44, 255, 255, 255))) g.FillPath(b, path);
     using (var f = Ui.Centered(false)) TextStyle.Draw(g, "今天", Ui.Cjk, FontStyle.Bold, 12.5f, Ui.Red, TodayButton, f, 0f);
    }
   }

   // Day grid (numbers, today's red disc, note dots), cached per month + notes.
   int days = DateTime.DaysInMonth(month.Year, month.Month);
   if (gridCache == null || gridDirty || gridToday != today) {
    Graphics cg;
    Replace(ref gridCache, Layer(GridRegion, out cg));
    using (cg) using (var f = Ui.Centered(false)) {
     for (int d = 1; d <= days; d++) {
      int col = ((int)month.DayOfWeek + d - 1) % 7;
      RectangleF circle = DayCircle(month, d);
      bool isToday = month.Year == today.Year && month.Month == today.Month && d == today.Day;
      if (isToday) using (var b = new SolidBrush(Ui.Red)) cg.FillEllipse(b, circle);
      Color ink = isToday ? Color.White : (col == 0 || col == 6 ? Ui.SecondaryLabel : Ui.Label);
      TextStyle.Draw(cg, d.ToString(), isToday ? Ui.TextSemibold : Ui.TextRegular, FontStyle.Regular, 15.5f, ink, new RectangleF(circle.X - 4, circle.Y, circle.Width + 8, circle.Height), f, isToday ? 0f : 0.35f);
      if (monthNotes[d] != null)
       using (var b = new SolidBrush(isToday ? Ui.Red : Ui.Orange)) cg.FillEllipse(b, circle.X + 15 - 2.5f, circle.Y + CellH - 5.5f, 5, 5);
     }
    }
    gridDirty = false;
    gridToday = today;
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

   string activeNote = hoveredDay > 0 && hoveredDay <= days ? monthNotes[hoveredDay] : null;
   string todayNote = month.Year == today.Year && month.Month == today.Month ? monthNotes[today.Day] : null;
   string footer = activeNote != null ? hoveredDay + "日 · " + activeNote : todayNote != null ? "今天 · " + todayNote : "";
   if (!String.IsNullOrEmpty(footer)) {
    if (footerCache == null || footerKey != footer) {
     Graphics cg;
     Replace(ref footerCache, Layer(FooterRegion, out cg));
     using (cg) using (var lead = Ui.Leading()) TextStyle.Draw(cg, footer, Ui.Cjk, FontStyle.Regular, 13f, Ui.Orange, new RectangleF(24, FooterRegion.Y, 300, 22), lead, 0.3f);
     footerKey = footer;
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
  protected override void OnShown(EventArgs e) { base.OnShown(e); FitCalendarOnScreen(); }
  protected override void OnPaint(PaintEventArgs e) { }
  protected override void OnPaintBackground(PaintEventArgs e) { }

  // --- Input ----------------------------------------------------------------------------

  CalendarTarget TargetAt(Point pt, out int day) {
   day = -1;
   if (!MonthCard.Contains(pt)) return CalendarTarget.None;
   DateTime month = DisplayedMonth(BeijingFromUtc(DateTime.UtcNow).Date);
   day = HitCalendarDay(pt, month);
   bool navVisible = monthHover.Value > 0.3 || monthOffset != 0;
   if (navVisible && PrevMonth.Contains(pt)) return CalendarTarget.PreviousMonth;
   if (navVisible && NextMonth.Contains(pt)) return CalendarTarget.NextMonth;
   if (monthOffset != 0 && TodayButton.Contains(pt)) return CalendarTarget.Today;
   return day > 0 ? CalendarTarget.Day : CalendarTarget.None;
  }

  protected override void OnMouseDown(MouseEventArgs e) {
   base.OnMouseDown(e);
   if (e.Button != MouseButtons.Left) return;
   int day;
   pressedTarget = TargetAt(e.Location, out day);
   if (pressedTarget == CalendarTarget.None && HitsCard(e.Location)) {
    draggingCard = true;
    dragDownScreen = PointToScreen(e.Location);
    dragOrigLoc = dragTargetLocation = Location;
    Capture = true;
    Cursor = Cursors.SizeAll;
   }
   Wake();
  }

  protected override void OnMouseMove(MouseEventArgs e) {
   base.OnMouseMove(e);
   if (draggingCard && e.Button == MouseButtons.Left) {
    UpdateDragTarget(PointToScreen(e.Location));
    Wake();
    return;
   }
   Point previous = mouseClient;
   mouseClient = e.Location;
   int day;
   CalendarTarget target = TargetAt(e.Location, out day);
   bool changed = hoveredDay != day || MonthCard.Contains(previous) != MonthCard.Contains(mouseClient) ||
    PrevMonth.Contains(previous) != PrevMonth.Contains(mouseClient) || NextMonth.Contains(previous) != NextMonth.Contains(mouseClient) ||
    TodayButton.Contains(previous) != TodayButton.Contains(mouseClient);
   hoveredDay = day;
   Cursor = target != CalendarTarget.None ? Cursors.Hand : Cursors.Default;
   if (changed) Wake();
  }

  void UpdateDragTarget(Point screen) {
   dragTargetLocation = new Point(dragOrigLoc.X + screen.X - dragDownScreen.X, dragOrigLoc.Y + screen.Y - dragDownScreen.Y);
  }

  void ApplyDragTarget() {
   // DWM can move the existing layered bitmap without asking us to paint it again.
   if (Location != dragTargetLocation) Location = dragTargetLocation;
  }

  void FinishDrag(Point? releaseScreen) {
   ApplyDragTarget();
   draggingCard = false;
   Capture = false;
   Cursor = Cursors.Default;
   if (Location != dragOrigLoc) app.SetHomePosition(Left, Top);
   mouseClient = releaseScreen.HasValue ? PointToClient(releaseScreen.Value) : new Point(-999, -999);
   TargetAt(mouseClient, out hoveredDay);
   Wake();
  }

  protected override void OnMouseCaptureChanged(EventArgs e) {
   base.OnMouseCaptureChanged(e);
   if (draggingCard && !Capture && !Disposing && !IsDisposed) FinishDrag(null);
  }

  protected override void OnMouseLeave(EventArgs e) {
   base.OnMouseLeave(e);
   if (draggingCard) return;
   mouseClient = new Point(-999, -999);
   hoveredDay = -1;
   pressedTarget = CalendarTarget.None;
   Cursor = Cursors.Default;
   Wake();
  }

  protected override void OnMouseWheel(MouseEventArgs e) {
   base.OnMouseWheel(e);
   if (MonthCard.Contains(e.Location)) ChangeMonth(e.Delta > 0 ? -1 : 1);
  }

  protected override void OnMouseUp(MouseEventArgs e) {
   base.OnMouseUp(e);
   CalendarTarget pressed = pressedTarget;
   pressedTarget = CalendarTarget.None;
   if (draggingCard && e.Button == MouseButtons.Left) {
    Point release = PointToScreen(e.Location);
    UpdateDragTarget(release);
    FinishDrag(release);
    return;
   }
   Wake();
   if (e.Button != MouseButtons.Left) return;
   int day;
   CalendarTarget target = TargetAt(e.Location, out day);
   if (target != pressed) return;
   if (target == CalendarTarget.PreviousMonth) { ChangeMonth(-1); return; }
   if (target == CalendarTarget.NextMonth) { ChangeMonth(1); return; }
   if (target == CalendarTarget.Today) { ChangeMonth(-monthOffset); return; }
   if (target == CalendarTarget.Day) {
    DateTime month = DisplayedMonth(BeijingFromUtc(DateTime.UtcNow).Date);
    string dateKey = new DateTime(month.Year, month.Month, day).ToString("yyyy-MM-dd");
    Point screenPos = PointToScreen(new Point(MonthCard.Left, MonthCard.Bottom - 80));
    BeginInvoke((Action)delegate { if (!IsDisposed) app.EditCalendarNote(dateKey, screenPos); });
   }
  }

  protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); if (IsHandleCreated && !draggingCard) Render(); }

  protected override void Dispose(bool disposing) {
   if (disposing) {
    Wallpaper.Changed -= OnWallpaperChanged;
    pump.Dispose();
    timer.Dispose();
    surface.Dispose();
    if (wallpaperBrush != null) { wallpaperBrush.Dispose(); wallpaperBrush = null; }
    chevronFont.Dispose();
    blitAttributes.Dispose();
    foreach (var c in new[] { cardsCache, clockCache, headerCache, gridCache, footerCache }) if (c != null) c.Dispose();
    if (ContextMenuStrip != null) ContextMenuStrip.Dispose();
   }
   base.Dispose(disposing);
  }
 }
}
