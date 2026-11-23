using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MacDeskNative {
 // A single-line text field that we draw ourselves. The folder UI and the popups never take
 // focus (see docs/ARCHITECTURE.md, "焦点与键盘"), so keys arrive routed through
 // AppController.RouteKey / RouteChar; the owner forwards them here and decides what Enter and
 // Escape mean. Plain typing keys are NOT consumed by HandleKey, so WM_CHAR (including IME
 // text) still arrives at HandleChar.
 internal sealed class TextField {
  string text = "";
  int caret;
  bool allSelected;
  float scrollX;
  PointF caretPoint;

  public int MaxLength = 200;
  /// Raised after any edit or caret move (owners redraw).
  public event EventHandler Changed;

  public string Text { get { return text; } }
  public int Caret { get { return caret; } }
  public bool AllSelected { get { return allSelected && text.Length > 0; } }
  /// Bottom-left of the caret in the coordinates of the last Draw (for IME placement).
  public PointF CaretPoint { get { return caretPoint; } }

  public void Reset(string value, bool selectAll) {
   text = Clean(value ?? "");
   caret = text.Length;
   allSelected = selectAll;
   scrollX = 0;
   OnChanged();
  }

  static string Clean(string s) {
   return s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Replace("\0", "");
  }

  void OnChanged() { var h = Changed; if (h != null) h(this, EventArgs.Empty); }

  /// Inserts at the caret (replacing the text if it is all selected).
  public void Insert(string s) {
   s = Clean(s ?? "");
   if (allSelected) { text = ""; caret = 0; allSelected = false; }
   int room = Math.Max(0, MaxLength - text.Length);
   if (s.Length > room) s = s.Substring(0, room);
   text = text.Substring(0, caret) + s + text.Substring(caret);
   caret += s.Length;
   OnChanged();
  }

  public void HandleChar(char c) {
   if (char.IsControl(c)) return;
   Insert(c.ToString());
  }

  // Moves by one user-visible character (a surrogate pair counts as one).
  int Prev(int i) { return i <= 0 ? 0 : (i >= 2 && char.IsLowSurrogate(text[i - 1]) && char.IsHighSurrogate(text[i - 2]) ? i - 2 : i - 1); }
  int Next(int i) { return i >= text.Length ? text.Length : (i + 1 < text.Length && char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]) ? i + 2 : i + 1); }

  /// Editing / caret keys. Returns true when consumed. Enter and Escape are left to the owner.
  public bool HandleKey(Keys keyData) {
   Keys code = keyData & Keys.KeyCode;
   bool ctrl = (keyData & Keys.Control) != 0;
   if (code == Keys.Enter || code == Keys.Escape) return false;
   if (code == Keys.Back) {
    if (AllSelected) { text = ""; caret = 0; }
    else if (ctrl) { text = text.Substring(caret); caret = 0; }
    else if (caret > 0) { int p = Prev(caret); text = text.Remove(p, caret - p); caret = p; }
    allSelected = false;
    OnChanged();
    return true;
   }
   if (code == Keys.Delete) {
    if (AllSelected) { text = ""; caret = 0; }
    else if (caret < text.Length) { int n = Next(caret); text = text.Remove(caret, n - caret); }
    allSelected = false;
    OnChanged();
    return true;
   }
   if (code == Keys.Left || code == Keys.Right || code == Keys.Home || code == Keys.End) {
    if (AllSelected) caret = code == Keys.Left || code == Keys.Home ? 0 : text.Length;
    else if (code == Keys.Left) caret = Prev(caret);
    else if (code == Keys.Right) caret = Next(caret);
    else caret = code == Keys.Home ? 0 : text.Length;
    allSelected = false;
    OnChanged();
    return true;
   }
   if (ctrl && code == Keys.A) { allSelected = true; OnChanged(); return true; }
   if (ctrl && code == Keys.V) {
    string clip = "";
    try { clip = Clipboard.GetText() ?? ""; } catch { }
    Insert(clip);
    return true;
   }
   if (ctrl && (code == Keys.C || code == Keys.X)) {
    try { if (text.Length > 0) Clipboard.SetText(text); } catch { }
    if (code == Keys.X) { text = ""; caret = 0; allSelected = false; OnChanged(); }
    return true;
   }
   // Swallow other shortcuts and navigation so they don't act on the view behind the field.
   bool modifier = (keyData & (Keys.Control | Keys.Alt)) != 0;
   return modifier || code == Keys.Up || code == Keys.Down || code == Keys.Tab || code == Keys.PageUp || code == Keys.PageDown || (code >= Keys.F1 && code <= Keys.F24);
  }

  // ---- Drawing ------------------------------------------------------------------------------

  static readonly StringFormat Measure = CreateMeasure();
  static StringFormat CreateMeasure() {
   var f = (StringFormat)StringFormat.GenericTypographic.Clone();
   f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
   return f;
  }

  static float Width(Graphics g, string s, Font font) {
   if (s.Length == 0) return 0;
   return g.MeasureString(s, font, PointF.Empty, Measure).Width;
  }

  /// The shared field shape: a dark inset pill; focused fields get a blue ring.
  public static void DrawPill(Graphics g, RectangleF r, float radius, float alpha, bool focused) {
   if (alpha <= 0.01f) return;
   using (var path = Ui.Squircle(r, radius))
   using (var shade = new SolidBrush(Ui.WithAlpha(Theme.FieldShade, alpha)))
   using (var light = new SolidBrush(Ui.WithAlpha(Theme.FieldLight, alpha))) {
    g.FillPath(shade, path);
    g.FillPath(light, path);
    if (focused) using (var ring = new Pen(Ui.WithAlpha(Theme.Blue, alpha), 1.6f)) g.DrawPath(ring, path);
   }
  }

  /// Draws the text, selection and caret inside `inner` (the pill is drawn separately).
  /// centered: short text is centred (labels, titles); long text scrolls to keep the caret visible.
  public void Draw(Graphics g, RectangleF inner, Font font, bool centered, string placeholder, Color ink) {
   float full = Width(g, text, font);
   float caretX = Width(g, text.Substring(0, caret), font);
   float origin;
   if (full <= inner.Width) {
    scrollX = 0;
    origin = centered ? inner.X + (inner.Width - full) / 2f : inner.X;
   } else {
    if (caretX - scrollX > inner.Width - 2) scrollX = caretX - inner.Width + 2;
    if (caretX - scrollX < 0) scrollX = caretX;
    scrollX = Math.Max(0, Math.Min(full - inner.Width + 2, scrollX));
    origin = inner.X - scrollX;
   }
   float lineH = font.GetHeight(g);
   float top = inner.Y + (inner.Height - lineH) / 2f;
   var clip = g.Clip;
   g.SetClip(RectangleF.Inflate(inner, 2, 0), CombineMode.Intersect);
   try {
    if (text.Length == 0) {
     if (!String.IsNullOrEmpty(placeholder))
      using (var b = new SolidBrush(Theme.TertiaryLabel)) g.DrawString(placeholder, font, b, new PointF(centered ? inner.X + (inner.Width - Width(g, placeholder, font)) / 2f : inner.X, top), Measure);
    } else {
     if (AllSelected)
      using (var b = new SolidBrush(Theme.Selection)) g.FillRectangle(b, origin - 2, inner.Y + inner.Height / 2f - lineH / 2f - 1, full + 4, lineH + 2);
     using (var b = new SolidBrush(ink)) g.DrawString(text, font, b, new PointF(origin, top), Measure);
    }
    float cx = (text.Length == 0 && centered ? inner.X + inner.Width / 2f : origin + caretX) + 0.5f;
    if (!AllSelected)
     using (var pen = new Pen(Theme.Blue, 2f)) g.DrawLine(pen, cx, top + 1, cx, top + lineH - 1);
    caretPoint = new PointF(cx, top + lineH);
   } finally {
    g.Clip = clip;
    clip.Dispose();
   }
  }
 }
}
