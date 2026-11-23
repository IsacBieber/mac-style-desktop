using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

using MacDesk.Core;

namespace MacDeskNative {
 // The day card (replaces the old one-line memo dialog). Tapping a calendar day shows that day's
 // reminders and its note; the reminders widget opens it in Agenda mode (overdue / today /
 // undated). iOS Reminders behaviour: tap the ring to complete, tap a title to rename, Enter in
 // "新提醒事项" adds and keeps adding, typing anywhere starts a new reminder, right-click to
 // reschedule or delete. Never activated: keys are routed in (see GlassPopup).
 internal sealed class DaySheet : GlassPopup {
  public enum SheetMode { Day, Agenda }
  enum RowKind { Section, Todo, Add, Note }
  enum EditTarget { None, NewTodo, RenameTodo, Note }

  public const int CardWidth = 352;
  const int PadX = 16, HeaderH = 64, RowH = 40, SectionH = 30, NoteH = 46, BottomPad = 8, CheckD = 20;
  const int TextX = PadX + 34;
  const double LingerSeconds = 0.9;

  sealed class RowState {
   public readonly Spring Y = new Spring(0, 0.3, 0.9);
   public readonly Spring Check = new Spring(0, 0.28, 0.62);
   public readonly Spring Fade = new Spring(1, 0.2, 1.0);
   public bool Placed;
   public bool Step(double dt) { return Y.Step(dt) | Check.Step(dt) | Fade.Step(dt); }
  }
  sealed class Row {
   public RowKind Kind;
   public DeskTodo Todo;
   public string Key, Text, Trailing;
   public Color TextColor, TrailingColor;
   public float Target, Height;
   public RowState State;
  }

  SheetMode mode = SheetMode.Day;
  string dateKey = "", todayKey = "";
  Rectangle beside;
  RectangleF anchor;
  int cardX, cardTop, maxCardHeight;
  readonly List<Row> rows = new List<Row>();
  readonly Dictionary<string, RowState> states = new Dictionary<string, RowState>();
  // Agenda / reorder: reminders ticked in this card stay where they were for a moment
  // (value > 0: seconds left in place; <= 0: fading out of the agenda).
  readonly Dictionary<string, double> lingering = new Dictionary<string, double>();
  readonly TextField field = new TextField { MaxLength = DeskService.MaxTodoTitle };
  EditTarget edit = EditTarget.None;
  string editTodoId;
  readonly Spring scroll = new Spring(0, 0.11, 1.0);
  float contentHeight;
  readonly TextSprites sprites = new TextSprites();
  readonly Font fieldFont = Ui.MakeFont(Ui.Cjk, Theme.Body, FontStyle.Regular);
  readonly Font noteFont = Ui.MakeFont(Ui.Cjk, Theme.Callout, FontStyle.Regular);
  int hoverRow = -1;
  ContextMenuStrip menu;

  public DaySheet(AppController app) : base(app) {
   Text = "MacDesk 日程";
   AccessibleName = "日程卡片：当天的提醒事项和备注";
   field.Changed += delegate { RedrawCard(); };
  }

  public SheetMode Mode { get { return mode; } }
  public string DateKey { get { return dateKey; } }
  internal bool Editing { get { return edit != EditTarget.None; } }
  internal string EditText { get { return field.Text; } }

  // ---- Opening ----------------------------------------------------------------------------

  /// Shows `day` beside `besideScreen` (the calendar card), growing out of `anchorScreen`.
  public void ShowDay(string day, Rectangle besideScreen, RectangleF anchorScreen) { Show(SheetMode.Day, day, besideScreen, anchorScreen, false); }

  /// Shows today's agenda beside `besideScreen` (the reminders widget); optionally starts adding.
  public void ShowAgenda(Rectangle besideScreen, RectangleF anchorScreen, bool startAdding) { Show(SheetMode.Agenda, Reminders.TodayKey(), besideScreen, anchorScreen, startAdding); }

  void Show(SheetMode nextMode, string day, Rectangle besideScreen, RectangleF anchorScreen, bool startAdding) {
   bool switching = IsOpen && (nextMode != mode || day != dateKey);
   if (switching) CommitEdit(true, false);
   if (!IsOpen || switching) { lingering.Clear(); scroll.Snap(0); hoverRow = -1; }
   mode = nextMode;
   dateKey = day;
   todayKey = Reminders.TodayKey();
   beside = besideScreen;
   anchor = anchorScreen;
   Rectangle work = Screen.FromRectangle(besideScreen).WorkingArea;
   int x = besideScreen.Right + 14;
   if (x + CardWidth > work.Right - 12) x = besideScreen.Left - 14 - CardWidth;
   x = Math.Max(work.Left + 12, Math.Min(work.Right - 12 - CardWidth, x));
   if (Visible && x != cardX) DismissNow(); // the glass was captured for the other side
   cardX = x;
   cardTop = Math.Max(work.Top + 12, besideScreen.Top);
   maxCardHeight = Math.Min(work.Bottom - 12 - cardTop, (int)(work.Height * 0.78));
   if (maxCardHeight < 280) { cardTop = Math.Max(work.Top + 12, work.Bottom - 12 - 280); maxCardHeight = work.Bottom - 12 - cardTop; }
   if (!Visible) {
    foreach (var s in states.Values) s.Placed = false;
    CaptureGlass(new Rectangle(cardX, work.Top, CardWidth, work.Height));
   }
   Rebuild(!switching && Visible);
   Relayout();
   if (startAdding) BeginNew();
  }

  /// The data changed (save from here, the widget, or elsewhere).
  internal void RefreshData() {
   if (!IsOpen) return;
   todayKey = Reminders.TodayKey();
   Rebuild(true);
   Relayout();
  }

  void Relayout() {
   int height = (int)Math.Ceiling(Math.Min(maxCardHeight, HeaderH + contentHeight + BottomPad));
   PresentCard(new Rectangle(cardX, cardTop, CardWidth, height), anchor);
   ClampScroll();
   RedrawCard();
  }

  float ViewHeight { get { return Math.Max(1, CardSize.Height - HeaderH - BottomPad); } }
  void ClampScroll() {
   float max = Math.Max(0, contentHeight - ViewHeight);
   if (scroll.Target > max) scroll.Target = max;
   if (scroll.Target < 0) scroll.Target = 0;
  }

  // ---- Rows -------------------------------------------------------------------------------

  bool ShownDone(DeskTodo t) { return app.IsTodoDone(t); }

  RowState StateFor(string key) {
   RowState s;
   if (!states.TryGetValue(key, out s)) { s = new RowState(); states[key] = s; }
   return s;
  }

  void AddRow(RowKind kind, string key, DeskTodo todo, string text, Color color, string trailing, Color trailingColor, float height, ref float y) {
   var row = new Row { Kind = kind, Key = key, Todo = todo, Text = text, TextColor = color, Trailing = trailing, TrailingColor = trailingColor, Target = y, Height = height, State = StateFor(key) };
   rows.Add(row);
   y += height;
  }

  // animate: rows that already exist glide to their new places; new ones fade in.
  void Rebuild(bool animate) {
   rows.Clear();
   var data = app.Data;
   var todos = data.todos ?? new List<DeskTodo>();
   float y = 0;
   if (mode == SheetMode.Day) {
    // Open first, then done; a reminder ticked a moment ago keeps its old place until it settles.
    var open = new List<DeskTodo>();
    var done = new List<DeskTodo>();
    foreach (var t in todos) {
     if (t.due != dateKey) continue;
     double left;
     bool holding = lingering.TryGetValue(t.id, out left) && left > 0;
     bool shownDone = ShownDone(t) && !holding;
     (shownDone ? done : open).Add(t);
    }
    foreach (var t in open) AddTodoRow(t, ref y);
    foreach (var t in done) AddTodoRow(t, ref y);
    AddRow(RowKind.Add, "add", null, "新提醒事项", Theme.BlueOnGlass, null, Color.Empty, RowH, ref y);
    string note = app.Service.GetNote(dateKey);
    AddRow(RowKind.Note, "note", null, note, Theme.Orange, null, Color.Empty, NoteH, ref y);
   } else {
    var shown = new HashSet<string>();
    var sections = new[] { TodoQueries.AgendaSection.Overdue, TodoQueries.AgendaSection.Today, TodoQueries.AgendaSection.Undated };
    var agenda = TodoQueries.Agenda(data, todayKey);
    foreach (var section in sections) {
     var list = new List<DeskTodo>();
     foreach (var entry in agenda) if (entry.Section == section) list.Add(entry.Todo);
     // Reminders completed here linger in their section briefly before fading out.
     foreach (var t in todos) if (lingering.ContainsKey(t.id) && SectionOf(t) == section && !list.Contains(t)) InsertInOrder(list, t, todos);
     if (list.Count == 0) continue;
     string title = section == TodoQueries.AgendaSection.Overdue ? "已逾期" : section == TodoQueries.AgendaSection.Today ? "今天" : "未定日期";
     Color color = section == TodoQueries.AgendaSection.Overdue ? Theme.Red : section == TodoQueries.AgendaSection.Today ? Theme.Label : Theme.SecondaryLabel;
     AddRow(RowKind.Section, "section:" + section, null, title, color, null, Color.Empty, SectionH, ref y);
     foreach (var t in list) { AddTodoRow(t, ref y); shown.Add(t.id); }
    }
    AddRow(RowKind.Add, "add", null, "新提醒事项", Theme.BlueOnGlass, null, Color.Empty, RowH, ref y);
   }
   contentHeight = y;
   var alive = new HashSet<string>();
   foreach (var row in rows) {
    alive.Add(row.Key);
    var s = row.State;
    if (!s.Placed) {
     s.Y.Snap(row.Target);
     s.Fade.Snap(animate && row.Kind == RowKind.Todo ? 0 : 1);
     s.Fade.Target = 1;
     if (row.Todo != null) s.Check.Snap(ShownDone(row.Todo) ? 1 : 0);
     s.Placed = true;
    } else {
     s.Y.Target = row.Target;
     if (!animate) s.Y.Snap(row.Target);
    }
    if (row.Todo != null) {
     s.Check.Target = ShownDone(row.Todo) ? 1 : 0;
     double left;
     if (mode == SheetMode.Agenda && ShownDone(row.Todo) && lingering.TryGetValue(row.Todo.id, out left) && left <= 0) s.Fade.Target = 0;
    }
   }
   foreach (var key in new List<string>(states.Keys)) if (!alive.Contains(key)) states.Remove(key);
   if (editTodoId != null && edit == EditTarget.RenameTodo && rows.Find(r => r.Todo != null && r.Todo.id == editTodoId) == null) { edit = EditTarget.None; editTodoId = null; }
  }

  TodoQueries.AgendaSection SectionOf(DeskTodo t) {
   if (String.IsNullOrEmpty(t.due)) return TodoQueries.AgendaSection.Undated;
   return String.CompareOrdinal(t.due, todayKey) < 0 ? TodoQueries.AgendaSection.Overdue : TodoQueries.AgendaSection.Today;
  }
  static void InsertInOrder(List<DeskTodo> list, DeskTodo t, List<DeskTodo> all) {
   int index = all.IndexOf(t), at = list.Count;
   for (int i = 0; i < list.Count; i++) if (all.IndexOf(list[i]) > index && list[i].due == t.due) { at = i; break; }
   list.Insert(at, t);
  }

  void AddTodoRow(DeskTodo t, ref float y) {
   bool done = ShownDone(t);
   string trailing = null;
   Color trailingColor = Theme.SecondaryLabel;
   if (mode == SheetMode.Agenda && !String.IsNullOrEmpty(t.due) && t.due != todayKey) { trailing = Reminders.RelativeDay(t.due, todayKey); trailingColor = Theme.Red; }
   AddRow(RowKind.Todo, "todo:" + t.id, t, t.title, done ? Theme.SecondaryLabel : Theme.Label, trailing, trailingColor, RowH, ref y);
  }

  float RowTop(Row row) { return HeaderH + row.State.Y.F - scroll.F; }

  int RowAt(Point p) {
   if (p.Y < HeaderH || p.Y > CardSize.Height - BottomPad + 2 || p.X < 0 || p.X > CardSize.Width) return -1;
   for (int i = 0; i < rows.Count; i++) {
    float top = RowTop(rows[i]);
    if (p.Y >= top && p.Y < top + rows[i].Height) return i;
   }
   return -1;
  }

  // ---- Editing ----------------------------------------------------------------------------

  string NewTodoDue { get { return mode == SheetMode.Day ? dateKey : todayKey; } }

  void BeginNew() {
   CommitEdit(true, false);
   edit = EditTarget.NewTodo;
   editTodoId = null;
   field.MaxLength = DeskService.MaxTodoTitle;
   field.Reset("", false);
   Row add = rows.Find(r => r.Kind == RowKind.Add);
   if (add != null) ScrollIntoView(add);
   RedrawCard();
  }

  void BeginRename(DeskTodo t) {
   CommitEdit(true, false);
   edit = EditTarget.RenameTodo;
   editTodoId = t.id;
   field.MaxLength = DeskService.MaxTodoTitle;
   field.Reset(t.title, false);
   RedrawCard();
  }

  void BeginNote() {
   CommitEdit(true, false);
   edit = EditTarget.Note;
   editTodoId = null;
   field.MaxLength = 200;
   field.Reset(app.Service.GetNote(dateKey) ?? "", false);
   Row note = rows.Find(r => r.Kind == RowKind.Note);
   if (note != null) ScrollIntoView(note);
   RedrawCard();
  }

  // save: keep the text; keepAdding: after adding, open another empty "new reminder" line.
  void CommitEdit(bool save, bool keepAdding) {
   if (edit == EditTarget.None) return;
   EditTarget target = edit;
   string id = editTodoId, text = field.Text.Trim();
   edit = EditTarget.None;
   editTodoId = null;
   if (save) {
    if (target == EditTarget.NewTodo && text.Length > 0) app.AddTodo(text, NewTodoDue);
    else if (target == EditTarget.RenameTodo && text.Length > 0 && id != null) {
     var t = app.Service.FindTodo(id);
     if (t != null && t.title != text) app.RenameTodo(id, text);
    } else if (target == EditTarget.Note) app.SetCalendarNote(dateKey, text);
   }
   if (keepAdding && target == EditTarget.NewTodo && text.Length > 0 && IsOpen) { edit = EditTarget.NewTodo; field.Reset("", false); }
   RedrawCard();
  }

  void ScrollIntoView(Row row) {
   float top = row.Target, bottom = row.Target + row.Height;
   if (top < scroll.Target) scroll.Target = top;
   else if (bottom > scroll.Target + ViewHeight) scroll.Target = bottom - ViewHeight;
   ClampScroll();
  }

  void Toggle(DeskTodo t) {
   bool next = !ShownDone(t);
   // Stay in place for a moment (and in the agenda, fade out afterwards) before reordering.
   lingering[t.id] = LingerSeconds;
   app.ToggleTodoSoon(t.id, next);
   Rebuild(true);
   RedrawCard();
  }

  // ---- Input ------------------------------------------------------------------------------

  public override bool HandleKey(Keys keyData) {
   if (edit != EditTarget.None) {
    if (keyData == Keys.Enter) { CommitEdit(true, true); return true; }
    if (keyData == Keys.Escape) { CommitEdit(false, false); return true; }
    if (keyData == Keys.Tab && edit == EditTarget.NewTodo) { CommitEdit(true, true); return true; }
    return field.HandleKey(keyData);
   }
   Keys code = keyData & Keys.KeyCode;
   if (keyData == Keys.Escape) { Dismiss(); return true; }
   if (keyData == Keys.Enter || keyData == (Keys.Control | Keys.N)) { BeginNew(); return true; }
   if (code == Keys.Up || code == Keys.PageUp) { scroll.Target -= code == Keys.Up ? RowH : ViewHeight * 0.8f; ClampScroll(); RedrawCard(); return true; }
   if (code == Keys.Down || code == Keys.PageDown) { scroll.Target += code == Keys.Down ? RowH : ViewHeight * 0.8f; ClampScroll(); RedrawCard(); return true; }
   // Plain typing keys fall through so WM_CHAR arrives (type to add); swallow shortcuts.
   bool modifier = (keyData & (Keys.Control | Keys.Alt)) != 0;
   return modifier || code == Keys.Tab || code == Keys.Left || code == Keys.Right || code == Keys.Home || code == Keys.End || code == Keys.Delete || (code >= Keys.F1 && code <= Keys.F24);
  }

  public override bool HandleChar(char c) {
   if (edit != EditTarget.None) { field.HandleChar(c); return true; }
   if (char.IsControl(c)) return true;
   BeginNew();
   field.HandleChar(c);
   return true;
  }

  protected override bool OutsidePaused() { return menu != null && !menu.IsDisposed && menu.Visible; }

  // Presses on the calendar / reminders widget are theirs: they switch this card instead.
  protected override void OnOutsidePress(Point screen) {
   if (app.Home != null && !app.Home.IsDisposed && app.Home.OwnsSheetPress(screen)) return;
   Dismiss();
  }

  protected override void OnDismissing() {
   CommitEdit(true, false);
   if (menu != null && !menu.IsDisposed) menu.Close();
  }

  protected override void OnDismissed() {
   lingering.Clear();
   states.Clear();
   rows.Clear();
   sprites.Clear();
   hoverRow = -1;
  }

  protected override void OnCardMouseMove(Point p) {
   int next = RowAt(p);
   if (next != hoverRow) { hoverRow = next; RedrawCard(); }
   Cursor = next >= 0 && rows[next].Kind != RowKind.Section ? Cursors.Hand : Cursors.Default;
  }
  protected override void OnCardMouseLeave() { if (hoverRow != -1) { hoverRow = -1; RedrawCard(); } }

  protected override void OnCardMouseWheel(Point p, int delta) {
   scroll.Target -= delta / 120f * RowH * 1.5f;
   ClampScroll();
   RedrawCard();
  }

  protected override void OnCardMouseUp(Point p, MouseButtons button) {
   int i = RowAt(p);
   if (i < 0) { if (button == MouseButtons.Left) CommitEdit(true, false); return; }
   Row row = rows[i];
   if (button == MouseButtons.Right) {
    if (row.Kind == RowKind.Todo) ShowTodoMenu(row.Todo, p);
    return;
   }
   if (button != MouseButtons.Left) return;
   switch (row.Kind) {
    case RowKind.Todo:
     if (p.X < TextX - 4) { CommitEdit(true, false); Toggle(row.Todo); }
     else if (!(edit == EditTarget.RenameTodo && editTodoId == row.Todo.id)) BeginRename(row.Todo);
     break;
    case RowKind.Add:
     if (edit != EditTarget.NewTodo) BeginNew();
     break;
    case RowKind.Note:
     if (edit != EditTarget.Note) BeginNote();
     break;
    default:
     CommitEdit(true, false);
     break;
   }
  }

  void ShowTodoMenu(DeskTodo t, Point cardPoint) {
   CommitEdit(true, false);
   if (menu != null && !menu.IsDisposed) menu.Dispose();
   menu = new ContextMenuStrip();
   PanelDrawing.StyleMenu(menu);
   string id = t.id, tomorrow = Reminders.AddDays(todayKey, 1), nextWeek = Reminders.AddDays(todayKey, 7);
   if (t.due != todayKey) AddMenuItem("改到今天", delegate { app.SetTodoDue(id, todayKey); });
   if (t.due != tomorrow) AddMenuItem("改到明天", delegate { app.SetTodoDue(id, tomorrow); });
   AddMenuItem("推迟一周", delegate { app.SetTodoDue(id, nextWeek); });
   if (!String.IsNullOrEmpty(t.due)) AddMenuItem("移除日期", delegate { app.SetTodoDue(id, ""); });
   menu.Items.Add(new ToolStripSeparator());
   AddMenuItem("重命名", delegate { var current = app.Service.FindTodo(id); if (current != null) BeginRename(current); });
   menu.Items.Add(new ToolStripSeparator());
   ToolStripItem remove = AddMenuItem("删除", delegate { app.DeleteTodo(id); });
   remove.Tag = PanelDrawing.Destructive;
   var shown = menu;
   shown.Closed += delegate { BeginInvoke((MethodInvoker)delegate { if (!shown.IsDisposed) shown.Dispose(); }); };
   shown.Show(this, new Point(cardPoint.X + ShadowPad, cardPoint.Y + ShadowPad));
  }

  ToolStripItem AddMenuItem(string text, Action action) {
   return menu.Items.Add(text, null, delegate { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) action(); }); });
  }

  // ---- Animation and drawing ------------------------------------------------------------

  protected override bool StepCard(double dt) {
   bool moving = scroll.Step(dt);
   foreach (var row in rows) moving |= row.State.Step(dt);
   if (lingering.Count > 0) {
    bool rebuild = false;
    foreach (var id in new List<string>(lingering.Keys)) {
     double before = lingering[id], after = before - dt;
     lingering[id] = after;
     if (before > 0 && after <= 0) rebuild = true;           // stop holding its place
     if (after <= -0.25) { lingering.Remove(id); rebuild = true; } // faded out of the agenda
    }
    if (rebuild) { Rebuild(true); Relayout(); }
    moving = true;
   }
   return moving;
  }

  protected override void DrawCard(Graphics g) {
   float w = CardSize.Width;
   DrawHeader(g, w);
   var clip = new RectangleF(0, HeaderH - 2, w, CardSize.Height - HeaderH - BottomPad + 4);
   var old = g.Clip;
   g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
   for (int i = 0; i < rows.Count; i++) {
    Row row = rows[i];
    float top = RowTop(row);
    if (top > CardSize.Height || top + row.Height < HeaderH - 2) continue;
    DrawRow(g, row, i, top, w, i + 1 < rows.Count ? rows[i + 1] : null);
   }
   g.Clip = old;
   old.Dispose();
   if (contentHeight > ViewHeight + 1) DrawScrollHint(g, w);
  }

  void DrawHeader(Graphics g, float w) {
   if (mode == SheetMode.Day) {
    DateTime day = Reminders.Parse(dateKey);
    int titleW = sprites.Draw(g, Reminders.MonthDay(day), Ui.Cjk, FontStyle.Bold, Theme.LargeTitle, Theme.Label, 0.15f, PadX, 34, 220);
    sprites.Draw(g, Reminders.Weekday(day), Ui.Cjk, FontStyle.Regular, Theme.Footnote, Theme.SecondaryLabel, 0.2f, PadX + titleW + 8, 37, 90);
    if (dateKey == todayKey) DrawChip(g, "今天", Theme.Red, w - PadX, 34);
    else if (day.Year != Reminders.Today().Year) sprites.Draw(g, day.Year + "年", Ui.Cjk, FontStyle.Regular, Theme.Footnote, Theme.SecondaryLabel, 0.2f, w - PadX - 44, 37, 60);
   } else {
    int titleW = sprites.Draw(g, "提醒事项", Ui.Cjk, FontStyle.Bold, Theme.LargeTitle, Theme.BlueOnGlass, 0.15f, PadX, 34, 220);
    int open = 0;
    foreach (var row in rows) if (row.Todo != null && !ShownDone(row.Todo)) open++;
    sprites.Draw(g, open > 0 ? open + " 项" : "全部完成", Ui.Cjk, FontStyle.Regular, Theme.Footnote, Theme.SecondaryLabel, 0.2f, PadX + titleW + 8, 37, 90);
    DrawChip(g, Reminders.MonthDay(Reminders.Parse(todayKey)), Theme.Red, w - PadX, 34);
   }
  }

  void DrawChip(Graphics g, string text, Color color, float right, float cy) {
   var bmp = sprites.Get(text, Ui.Cjk, FontStyle.Bold, Theme.Caption, color, 0.1f, 120);
   if (bmp == null) return;
   float tw = bmp.Width - 2, h = 22, width = tw + 18;
   var r = new RectangleF(right - width, cy - h / 2f, width, h);
   using (var path = Ui.Squircle(r, h / 2f))
   using (var b = new SolidBrush(Color.FromArgb(48, color))) g.FillPath(b, path);
   Ui.Blit(g, bmp, (int)Math.Round(r.X + 9) - 1, (int)Math.Round(cy - bmp.Height / 2f));
  }

  void DrawRow(Graphics g, Row row, int index, float top, float w, Row next) {
   float alpha = Math.Max(0, Math.Min(1, row.State.Fade.F));
   if (alpha <= 0.01f) return;
   float cy = top + row.Height / 2f;
   bool editingThis = (row.Kind == RowKind.Todo && edit == EditTarget.RenameTodo && editTodoId == row.Todo.id) ||
    (row.Kind == RowKind.Add && edit == EditTarget.NewTodo) || (row.Kind == RowKind.Note && edit == EditTarget.Note);
   if (index == hoverRow && row.Kind != RowKind.Section && !editingThis) {
    using (var path = Ui.Squircle(new RectangleF(6, top + 2, w - 12, row.Height - 4), Theme.RowRadius))
    using (var b = new SolidBrush(Ui.WithAlpha(Theme.ControlFill, 0.7 * alpha))) g.FillPath(b, path);
   }
   int textMax = (int)(w - TextX - PadX);
   switch (row.Kind) {
    case RowKind.Section:
     sprites.Draw(g, row.Text, Ui.Cjk, FontStyle.Bold, Theme.Footnote, row.TextColor, 0.1f, PadX + 2, top + row.Height / 2f + 3, (int)(w - PadX * 2));
     break;
    case RowKind.Todo: {
     Reminders.DrawCheck(g, new PointF(PadX + 12, cy), CheckD, row.State.Check.F, Theme.TertiaryLabel, alpha);
     int trailingW = 0;
     if (!editingThis && row.Trailing != null) {
      var bmp = sprites.Get(row.Trailing, Ui.Cjk, FontStyle.Regular, Theme.Footnote, row.TrailingColor, 0.15f, 120);
      if (bmp != null) { trailingW = bmp.Width + 8; Ui.Blit(g, bmp, (int)Math.Round(w - PadX - bmp.Width + 2), (int)Math.Round(cy - bmp.Height / 2f)); }
     }
     if (editingThis) DrawField(g, new RectangleF(TextX, top + 6, textMax, row.Height - 12), null);
     else sprites.Draw(g, row.Text, Ui.Cjk, FontStyle.Regular, Theme.Body, row.TextColor, 0.15f, TextX, cy, Math.Max(20, textMax - trailingW), alpha);
     if (next != null && next.Kind == RowKind.Todo)
      using (var pen = new Pen(Ui.WithAlpha(Theme.Separator, alpha), 1f)) g.DrawLine(pen, TextX, top + row.Height - 0.5f, w - PadX, top + row.Height - 0.5f);
     break;
    }
    case RowKind.Add:
     if (editingThis) {
      Reminders.DrawCheck(g, new PointF(PadX + 12, cy), CheckD, 0, Theme.TertiaryLabel, 1f);
      DrawField(g, new RectangleF(TextX, top + 6, textMax, row.Height - 12), "新提醒事项");
     } else {
      Reminders.DrawPlusDisc(g, new PointF(PadX + 12, cy), CheckD, 1f);
      sprites.Draw(g, row.Text, Ui.Cjk, FontStyle.Regular, Theme.Body, Theme.BlueOnGlass, 0.15f, TextX, cy, textMax);
     }
     break;
    case RowKind.Note: {
     using (var pen = new Pen(Theme.Separator, 1f)) g.DrawLine(pen, PadX, top + 6.5f, w - PadX, top + 6.5f);
     float ncy = cy + 3;
     sprites.Draw(g, "备注", Ui.Cjk, FontStyle.Regular, Theme.Footnote, Theme.SecondaryLabel, 0.15f, PadX + 2, ncy, 60);
     if (editingThis) DrawField(g, new RectangleF(TextX, ncy - 14, textMax, 28), "添加备注");
     else if (!String.IsNullOrEmpty(row.Text)) sprites.Draw(g, row.Text, Ui.Cjk, FontStyle.Regular, Theme.Callout, Theme.Orange, 0.15f, TextX, ncy, textMax);
     else sprites.Draw(g, "添加备注", Ui.Cjk, FontStyle.Regular, Theme.Callout, Theme.TertiaryLabel, 0.15f, TextX, ncy, textMax);
     break;
    }
   }
  }

  void DrawField(Graphics g, RectangleF r, string placeholder) {
   field.Draw(g, r, edit == EditTarget.Note ? noteFont : fieldFont, false, placeholder, edit == EditTarget.Note ? Theme.Orange : Theme.Label);
   // Let the IME composition / candidate window follow the caret.
   if (app.DesktopFolders != null && !app.DesktopFolders.IsDisposed) app.DesktopFolders.PlaceImeWindow(CardToScreen(new PointF(field.CaretPoint.X - 2, field.CaretPoint.Y + 2)));
  }

  void DrawScrollHint(Graphics g, float w) {
   float view = ViewHeight, total = contentHeight;
   float h = Math.Max(24, view * view / total);
   float y = HeaderH + (view - h) * Math.Max(0, Math.Min(1, scroll.F / Math.Max(1, total - view)));
   using (var path = Ui.Squircle(new RectangleF(w - 6, y, 3, h), 1.5f))
   using (var b = new SolidBrush(Theme.TertiaryLabel)) g.FillPath(b, path);
  }

  // ---- Layout facts for the self-test -----------------------------------------------------

  /// Lays the card out for `day` (or today's agenda) without showing it.
  internal void PrepareForTest(SheetMode testMode, string day, Rectangle work) {
   mode = testMode;
   todayKey = Reminders.TodayKey();
   dateKey = testMode == SheetMode.Day ? day : todayKey;
   cardX = work.Left + 400;
   cardTop = work.Top + 226;
   maxCardHeight = Math.Min(work.Bottom - 12 - cardTop, (int)(work.Height * 0.78));
   foreach (var s in states.Values) s.Placed = false;
   Rebuild(false);
   SizeCardForTest(new Size(CardWidth, (int)Math.Ceiling(Math.Min(maxCardHeight, HeaderH + contentHeight + BottomPad))));
  }
  internal void BeginNewForTest() { BeginNew(); }
  /// Ticks the first open reminder shown (bench: the check animation).
  internal bool TickFirstForTest() {
   foreach (var row in rows) if (row.Todo != null && !ShownDone(row.Todo)) { Toggle(row.Todo); return true; }
   return false;
  }

  /// Row rectangles in card coordinates, as drawn when landed and not scrolled.
  internal List<RectangleF> RowRects() {
   var list = new List<RectangleF>();
   foreach (var row in rows) list.Add(new RectangleF(0, HeaderH + row.Target, CardSize.Width, row.Height));
   return list;
  }
  internal int RowCount { get { return rows.Count; } }

  protected override void Dispose(bool disposing) {
   if (disposing) {
    sprites.Dispose();
    fieldFont.Dispose();
    noteFont.Dispose();
    if (menu != null) menu.Dispose();
   }
   base.Dispose(disposing);
  }
 }
}
