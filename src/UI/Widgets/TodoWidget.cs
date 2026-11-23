using System;
using System.Collections.Generic;
using System.Drawing;

using MacDesk.Core;

namespace MacDeskNative {
 // The "提醒事项" widget under the calendar (iOS Reminders widget). A fixed-size card - like an
 // iOS widget it never resizes, so the desktop folders do not reflow when reminders change.
 // Lists what needs attention: overdue, today, then undated. Tapping a ring completes the
 // reminder (it stays a moment, fades, and the rows below slide up); tapping elsewhere opens the
 // agenda card, "+" opens it ready to type. HomeForm owns the window, layout and glass card.
 internal sealed class TodoWidget : IDisposable {
  public const int CardWidth = 344, CardHeight = 236;
  const int PadX = 20, HeaderH = 48, RowH = 34, MaxRows = 5, CheckD = 18, TextX = PadX + 28, PlusD = 26;
  const double LingerSeconds = 0.9, FadeSeconds = 0.22;

  sealed class Item {
   public DeskTodo Todo;
   public string Trailing;
   public readonly Spring Y = new Spring(0, 0.3, 0.9);
   public readonly Spring Check = new Spring(0, 0.28, 0.62);
   public readonly Spring Fade = new Spring(1, 0.2, 1.0);
   // Ticked here: keeps its slot for LingerSeconds, then fades out over FadeSeconds.
   public bool Lingering;
   public double Left;
   public bool Placed;
   public bool Step(double dt) { return Y.StepChanged(dt) | Check.StepChanged(dt) | Fade.StepChanged(dt); }
  }

  readonly AppController app;
  readonly TextSprites sprites = new TextSprites();
  readonly Dictionary<string, Item> items = new Dictionary<string, Item>();
  readonly List<Item> rows = new List<Item>();
  readonly Spring plusPress = new Spring(1, 0.2, 0.6);
  int overflow, openCount;
  string todayKey = "";
  Point mouse = new Point(-999, -999);
  bool pressingPlus;

  public TodoWidget(AppController app) { this.app = app; }

  /// Card rectangle in the host's client coordinates (set by HomeForm's layout).
  public Rectangle Bounds { get; set; }
  public int OpenCount { get { return openCount; } }
  public int VisibleRows { get { return rows.Count; } }
  public string TodayKey { get { return todayKey; } }

  Rectangle PlusRect { get { return new Rectangle(Bounds.Right - PadX - PlusD + 4, Bounds.Y + (HeaderH - PlusD) / 2 + 2, PlusD, PlusD); } }
  Rectangle HeaderRect { get { return new Rectangle(Bounds.X, Bounds.Y, Bounds.Width, HeaderH); } }
  float ListTop { get { return Bounds.Y + HeaderH; } }
  Rectangle FooterRect { get { return new Rectangle(Bounds.X + PadX, Bounds.Y + HeaderH + MaxRows * RowH, Bounds.Width - PadX * 2, Bounds.Bottom - (Bounds.Y + HeaderH + MaxRows * RowH)); } }

  /// Rebuilds the list from the data (animate: existing rows glide, new ones fade in).
  public void Refresh(bool animate) {
   todayKey = Reminders.TodayKey();
   var data = app.Data;
   var agenda = TodoQueries.AgendaPreview(data, todayKey, t => !app.IsTodoDone(t), MaxRows, out openCount);
   var keep = new List<Item>(MaxRows * 2);
   // Everything follows the agenda, except reminders ticked here: they hold their old slot
   // until they have faded out.
   foreach (var entry in agenda) {
    Item item;
    if (!items.TryGetValue(entry.Todo.id, out item)) item = new Item();
    item.Todo = entry.Todo;
    if (item.Lingering) { item.Lingering = false; item.Fade.Target = 1; }
    item.Trailing = entry.Section == TodoQueries.AgendaSection.Overdue ? Reminders.RelativeDay(entry.Todo.due, todayKey) : null;
    keep.Add(item);
   }
   int lingeringCount = 0;
   for (int i = 0; i < rows.Count; i++) {
    var r = rows[i];
    if (!r.Lingering) continue;
    var current = data.todos.Find(t => t.id == r.Todo.id);
    if (current == null || !app.IsTodoDone(current)) continue;
    r.Todo = current;
    keep.Insert(Math.Min(i, keep.Count), r);
    lingeringCount++;
   }
   rows.Clear();
   items.Clear();
   for (int i = 0; i < keep.Count && i < MaxRows; i++) rows.Add(keep[i]);
   overflow = Math.Max(0, openCount + lingeringCount - rows.Count);
   for (int i = 0; i < rows.Count; i++) {
    var it = rows[i];
    items.Add(it.Todo.id, it);
    float target = i * RowH;
    if (!it.Placed) {
     it.Y.Snap(target);
     it.Fade.Snap(animate ? 0 : 1);
     it.Fade.Target = 1;
     it.Check.Snap(app.IsTodoDone(it.Todo) ? 1 : 0);
     it.Placed = true;
    } else {
     it.Y.Target = target;
     if (!animate) it.Y.Snap(target);
    }
    it.Check.Target = app.IsTodoDone(it.Todo) ? 1 : 0;
   }
  }

  /// True when the Beijing date has rolled over since the last Refresh.
  public bool DayChanged { get { return todayKey != Reminders.TodayKey(); } }

  public bool Step(double dt, out bool waiting) {
   bool moving = plusPress.StepChanged(dt);
   waiting = false;
   bool rebuild = false;
   foreach (var it in rows) {
    moving |= it.Step(dt);
    if (!it.Lingering) continue;
    double before = it.Left;
    it.Left -= dt;
    if (before > 0 && it.Left <= 0) it.Fade.Target = 0;                    // start fading out
    if (it.Left <= -FadeSeconds) { it.Lingering = false; rebuild = true; } // gone: close the gap
    else waiting = true;
   }
   if (rebuild) Refresh(true);
   return moving || rebuild;
  }

  // ---- Input (host client coordinates) ----------------------------------------------------

  public enum HitKind { None, Plus, Check, Row, Header, Footer }

  public HitKind HitTest(Point p, out DeskTodo todo, out RectangleF rowRect) {
   todo = null;
   rowRect = RectangleF.Empty;
   if (!Bounds.Contains(p)) return HitKind.None;
   if (Rectangle.Inflate(PlusRect, 6, 6).Contains(p)) return HitKind.Plus;
   if (HeaderRect.Contains(p)) return HitKind.Header;
   foreach (var it in rows) {
    float top = ListTop + it.Y.F;
    if (p.Y >= top && p.Y < top + RowH && it.Fade.Target > 0) {
     todo = it.Todo;
     rowRect = new RectangleF(Bounds.X, top, Bounds.Width, RowH);
     return p.X < Bounds.X + TextX - 4 ? HitKind.Check : HitKind.Row;
    }
   }
   if (overflow > 0 && FooterRect.Contains(p)) return HitKind.Footer;
   return HitKind.None;
  }

  /// Where row i is drawn when settled (host client coordinates).
  internal RectangleF RowRect(int i) { return new RectangleF(Bounds.X, ListTop + i * RowH, Bounds.Width, RowH); }
  internal Rectangle PlusBounds { get { return PlusRect; } }
  internal int CheckColumnRight { get { return Bounds.X + TextX - 4; } }

  /// Whether a press at p belongs to a control (otherwise the host may drag the widgets).
  public bool IsInteractive(Point p) {
   DeskTodo t; RectangleF r;
   return HitTest(p, out t, out r) != HitKind.None;
  }

  public bool MouseMove(Point p) {
   Point before = mouse;
   mouse = p;
   DeskTodo a, b; RectangleF ra, rb;
   HitKind was = HitTest(before, out a, out ra), now = HitTest(p, out b, out rb);
   return was != now || a != b;
  }
  public void MouseLeave() { mouse = new Point(-999, -999); pressingPlus = false; plusPress.Target = 1; }
  public void MouseDown(Point p) {
   DeskTodo t; RectangleF r;
   pressingPlus = HitTest(p, out t, out r) == HitKind.Plus;
   plusPress.Target = pressingPlus ? 0.86 : 1;
  }

  /// A click released at p (pressed at the same target). Returns true if it did something.
  public bool Click(Point p, Func<Rectangle, Rectangle> toScreen) {
   pressingPlus = false;
   plusPress.Target = 1;
   DeskTodo todo; RectangleF rowRect;
   HitKind hit = HitTest(p, out todo, out rowRect);
   Rectangle cardScreen = toScreen(Bounds);
   switch (hit) {
    case HitKind.Check: {
     Item it;
     if (todo == null || !items.TryGetValue(todo.id, out it)) return false;
     bool next = !app.IsTodoDone(todo);
     it.Lingering = next;
     it.Left = LingerSeconds;
     it.Fade.Target = 1;
     app.ToggleTodoSoon(todo.id, next);
     it.Check.Target = next ? 1 : 0;
     return true;
    }
    case HitKind.Plus:
     app.ShowAgenda(cardScreen, toScreen(PlusRect), true);
     return true;
    case HitKind.Row:
     app.ShowAgenda(cardScreen, toScreen(Rectangle.Round(rowRect)), false);
     return true;
    case HitKind.Header:
    case HitKind.Footer:
     app.ShowAgenda(cardScreen, toScreen(HeaderRect), false);
     return true;
   }
   return false;
  }

  // ---- Drawing (the glass card itself is drawn by the host) ------------------------------

  public void Draw(Graphics g) {
   Rectangle b = Bounds;
   float headerCy = b.Y + HeaderH / 2f + 2;
   int titleW = sprites.Draw(g, "提醒事项", Ui.Cjk, FontStyle.Bold, 16.5f, Theme.BlueOnGlass, 0f, b.X + PadX + 4, headerCy, 160);
   if (openCount > 0) sprites.Draw(g, openCount.ToString(), Ui.TextSemibold, FontStyle.Regular, 15f, Theme.SecondaryLabel, 0.2f, b.X + PadX + 4 + titleW + 8, headerCy, 60);

   DeskTodo hoverTodo; RectangleF hoverRect;
   HitKind hover = HitTest(mouse, out hoverTodo, out hoverRect);
   RectangleF plus = PlusRect;
   float s = plusPress.F;
   var pr = new RectangleF(plus.X + plus.Width * (1 - s) / 2f, plus.Y + plus.Height * (1 - s) / 2f, plus.Width * s, plus.Height * s);
   using (var fill = new SolidBrush(Color.FromArgb(hover == HitKind.Plus ? 72 : 46, Theme.Blue))) g.FillEllipse(fill, pr);
   using (var pen = new Pen(Theme.BlueOnGlass, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) {
    float cx = pr.X + pr.Width / 2f, cy = pr.Y + pr.Height / 2f, arm = pr.Width * 0.24f;
    g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
    g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
   }

   var oldClip = g.Clip;
   g.SetClip(new RectangleF(b.X, ListTop - 2, b.Width, MaxRows * RowH + 4), System.Drawing.Drawing2D.CombineMode.Intersect);
   int textMax = b.Width - TextX - PadX;
   for (int i = 0; i < rows.Count; i++) {
    var it = rows[i];
    float alpha = Math.Max(0, Math.Min(1, it.Fade.F));
    if (alpha <= 0.01f) continue;
    float top = ListTop + it.Y.F, cy = top + RowH / 2f;
    if (hover == HitKind.Row && hoverTodo == it.Todo)
     using (var path = Ui.Squircle(new RectangleF(b.X + 10, top + 1, b.Width - 20, RowH - 2), Theme.RowRadius))
     using (var fill = new SolidBrush(Ui.WithAlpha(Theme.ControlFill, 0.6 * alpha))) g.FillPath(fill, path);
    Reminders.DrawCheck(g, new PointF(b.X + PadX + 9, cy), CheckD, it.Check.F, hover == HitKind.Check && hoverTodo == it.Todo ? Theme.SecondaryLabel : Theme.TertiaryLabel, alpha);
    int trailingW = 0;
    if (it.Trailing != null) {
     var tb = sprites.Get(it.Trailing, Ui.Cjk, FontStyle.Regular, 12.5f, Theme.Red, 0.15f, 110);
     if (tb != null) { trailingW = tb.Width + 8; Ui.Blit(g, tb, b.Right - PadX - tb.Width + 2, (int)Math.Round(cy - tb.Height / 2f)); }
    }
    bool done = app.IsTodoDone(it.Todo);
    sprites.Draw(g, it.Todo.title, Ui.Cjk, FontStyle.Regular, 14.5f, done ? Theme.SecondaryLabel : Theme.Label, 0.15f, b.X + TextX, cy, Math.Max(20, textMax - trailingW), alpha);
   }
   g.Clip = oldClip;
   oldClip.Dispose();

   if (rows.Count == 0) {
    using (var f = Ui.Centered(false))
     TextStyle.Draw(g, "全部完成", Ui.Cjk, FontStyle.Regular, 14f, Theme.TertiaryLabel, new RectangleF(b.X, ListTop + 40, b.Width, 40), f, 0.15f);
   } else if (overflow > 0) {
    Rectangle fr = FooterRect;
    sprites.Draw(g, "还有 " + overflow + " 项", Ui.Cjk, FontStyle.Regular, 12.5f, hover == HitKind.Footer ? Theme.Label : Theme.SecondaryLabel, 0.15f, b.X + TextX, fr.Y + fr.Height / 2f - 1, 160);
   }
  }

  public void Dispose() { sprites.Dispose(); }
 }
}
