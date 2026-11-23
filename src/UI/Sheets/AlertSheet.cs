using System;
using System.Drawing;
using System.Windows.Forms;

namespace MacDeskNative {
 // iOS-style alert (replaces the Win32 confirmation boxes): a title, a short explanation, and
 // "取消 | <action>" side by side under a hairline. Enter confirms, Escape cancels; clicks
 // elsewhere are ignored (choose a button), another app taking the foreground cancels.
 internal sealed class AlertSheet : GlassPopup {
  const int CardWidth = 290, PadX = 18, PadTop = 20, ButtonH = 46, TitleGap = 6, BodyGap = 18;
  readonly Font titleFont = Ui.MakeFont(Ui.Cjk, Theme.Title, FontStyle.Bold);
  readonly Font bodyFont = Ui.MakeFont(Ui.Cjk, Theme.Footnote, FontStyle.Regular);
  readonly Font buttonFont = Ui.MakeFont(Ui.Cjk, Theme.Title - 1, FontStyle.Regular);
  readonly Font buttonBoldFont = Ui.MakeFont(Ui.Cjk, Theme.Title - 1, FontStyle.Bold);
  string title = "", body = "", action = "";
  Action confirmed;
  float titleH, bodyH;
  int hovered = -1, pressed = -1; // 0 = cancel, 1 = action

  public AlertSheet(AppController app) : base(app) { Text = "MacDesk 确认"; }

  protected override float Radius { get { return Theme.AlertRadius; } }
  protected override float StartScale { get { return 1.08f; } }
  protected override void OnOutsidePress(Point screen) { }

  internal string TitleText { get { return title; } }

  static StringFormat Wrapped() { return new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisWord }; }

  /// Shows the alert centred on `over` (screen rectangle); `onConfirm` runs if the action is chosen.
  public void Ask(string titleText, string bodyText, string actionText, Rectangle over, Action onConfirm) {
   confirmed = onConfirm;
   Size size = Measure(titleText, bodyText, actionText);
   int height = size.Height;
   var card = new Rectangle(over.X + (over.Width - CardWidth) / 2, over.Y + (over.Height - height) / 2, CardWidth, height);
   Rectangle work = Screen.FromRectangle(card).WorkingArea;
   card.X = Math.Max(work.Left + 12, Math.Min(work.Right - 12 - card.Width, card.X));
   card.Y = Math.Max(work.Top + 12, Math.Min(work.Bottom - 12 - card.Height, card.Y));
   if (!Visible) CaptureGlass(card);
   PresentCard(card, new RectangleF(card.X + card.Width / 2f, card.Y + card.Height / 2f, 1, 1));
   RedrawCard();
  }

  /// Sets the texts and returns the card size they need.
  internal Size Measure(string titleText, string bodyText, string actionText) {
   title = titleText ?? "";
   body = bodyText ?? "";
   action = String.IsNullOrEmpty(actionText) ? "确定" : actionText;
   hovered = pressed = -1;
   using (var f = Wrapped()) {
    titleH = Math.Min(48, MeasureText(title, titleFont, CardWidth - PadX * 2, f).Height);
    bodyH = body.Length == 0 ? 0 : Math.Min(60, MeasureText(body, bodyFont, CardWidth - PadX * 2, f).Height);
   }
   return new Size(CardWidth, (int)Math.Ceiling(PadTop + titleH + (bodyH > 0 ? TitleGap + bodyH : 0) + BodyGap) + ButtonH);
  }

  void Choose(bool confirm) {
   Action run = confirm ? confirmed : null;
   confirmed = null;
   Dismiss();
   if (run != null) run();
  }

  int ButtonAt(Point p) {
   int top = CardSize.Height - ButtonH;
   if (p.Y < top || p.Y >= CardSize.Height || p.X < 0 || p.X >= CardSize.Width) return -1;
   return p.X < CardSize.Width / 2 ? 0 : 1;
  }

  public override bool HandleKey(Keys keyData) {
   if (keyData == Keys.Escape) { Choose(false); return true; }
   if (keyData == Keys.Enter) { Choose(true); return true; }
   return true; // modal: nothing reaches the panel behind
  }
  public override bool HandleChar(char c) { return true; }

  protected override void OnDismissing() { confirmed = null; }

  protected override void OnCardMouseMove(Point p) {
   int next = ButtonAt(p);
   if (next != hovered) { hovered = next; RedrawCard(); }
  }
  protected override void OnCardMouseLeave() { if (hovered != -1 || pressed != -1) { hovered = pressed = -1; RedrawCard(); } }
  protected override void OnCardMouseDown(Point p, MouseButtons button) {
   if (button != MouseButtons.Left) return;
   pressed = ButtonAt(p);
   RedrawCard();
  }
  protected override void OnCardMouseUp(Point p, MouseButtons button) {
   int was = pressed;
   pressed = -1;
   if (button != MouseButtons.Left) return;
   int at = ButtonAt(p);
   if (at >= 0 && at == was) { Choose(at == 1); return; }
   RedrawCard();
  }

  protected override void DrawCard(Graphics g) {
   float w = CardSize.Width;
   using (var f = Wrapped()) {
    using (var b = new SolidBrush(Theme.Label)) g.DrawString(title, titleFont, b, new RectangleF(PadX, PadTop, w - PadX * 2, titleH + 2), f);
    if (bodyH > 0)
     using (var b = new SolidBrush(Theme.SecondaryLabel)) g.DrawString(body, bodyFont, b, new RectangleF(PadX, PadTop + titleH + TitleGap, w - PadX * 2, bodyH + 2), f);
   }
   float top = CardSize.Height - ButtonH;
   int highlight = pressed >= 0 ? pressed : hovered;
   if (highlight >= 0)
    using (var b = new SolidBrush(pressed >= 0 ? Theme.ControlFillHover : Theme.ControlFill))
     g.FillRectangle(b, highlight == 0 ? 0 : w / 2f, top, w / 2f, ButtonH);
   using (var pen = new Pen(Theme.Separator, 1f)) {
    g.DrawLine(pen, 0, top + 0.5f, w, top + 0.5f);
    g.DrawLine(pen, w / 2f, top, w / 2f, CardSize.Height);
   }
   using (var f = Ui.Centered(true)) {
    using (var b = new SolidBrush(Theme.BlueOnGlass)) g.DrawString("取消", buttonFont, b, new RectangleF(0, top, w / 2f, ButtonH), f);
    using (var b = new SolidBrush(Theme.Red)) g.DrawString(action, buttonBoldFont, b, new RectangleF(w / 2f, top, w / 2f, ButtonH), f);
   }
  }

  protected override void Dispose(bool disposing) {
   if (disposing) { titleFont.Dispose(); bodyFont.Dispose(); buttonFont.Dispose(); buttonBoldFont.Dispose(); }
   base.Dispose(disposing);
  }
 }
}
