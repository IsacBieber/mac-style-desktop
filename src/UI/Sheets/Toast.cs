using System;
using System.Drawing;
using System.Windows.Forms;

namespace MacDeskNative {
 // A glass banner at the top of the screen for "that didn't work" messages (replaces the
 // Win32 message box). It never takes the keyboard, ignores clicks elsewhere, disappears after
 // a few seconds, and a click dismisses it early.
 internal sealed class Toast : GlassPopup {
  const int Height1 = 46, Height2 = 64, MinWidth = 240, MaxWidth = 560, PadX = 18, IconSize = 22, Gap = 10;
  readonly Timer life = new Timer { Interval = 3800 };
  readonly Font font = Ui.MakeFont(Ui.Cjk, Theme.Callout, FontStyle.Regular);
  string message = "";
  bool twoLines;

  public Toast(AppController app) : base(app) {
   Text = "MacDesk 提示";
   life.Tick += delegate { life.Stop(); Dismiss(); };
  }

  protected override bool TakesKeyboard { get { return false; } }
  protected override bool FollowsForeground { get { return false; } }
  protected override float StartScale { get { return 0.92f; } }
  protected override float Radius { get { return (twoLines ? Height2 : Height1) / 2f; } }
  protected override Color Tint { get { return Material.WidgetTint; } }
  protected override void OnOutsidePress(Point screen) { }
  protected override void OnCardMouseUp(Point p, MouseButtons button) { Dismiss(); }
  protected override void OnDismissed() { life.Stop(); }

  internal string Message { get { return message; } }

  /// Shows `text` at the top centre of `work` (or replaces the message if already showing).
  public void ShowMessage(string text, Rectangle work) {
   Size size = Measure(text, work.Width);
   var card = new Rectangle(work.X + (work.Width - size.Width) / 2, work.Top + 14, size.Width, size.Height);
   if (!Visible) CaptureGlass(new Rectangle(work.X + 20 - ShadowPad, work.Top, work.Width - 40 + ShadowPad * 2, Height2 + 14 + ShadowPad * 2));
   PresentCard(card, new RectangleF(card.X + card.Width / 2f, work.Top - 24, 1, 1));
   RedrawCard();
   life.Stop();
   life.Start();
  }

  /// Sets the message and returns the card size it needs on a screen `screenWidth` wide.
  internal Size Measure(string text, int screenWidth) {
   message = (text ?? "").Trim();
   if (message.Length == 0) message = "操作未完成。";
   float textMax = Math.Min(MaxWidth, screenWidth - 40) - PadX * 2 - IconSize - Gap;
   SizeF one = MeasureText(message, font, 10000, StringFormat.GenericTypographic);
   twoLines = one.Width > textMax;
   int width = (int)Math.Ceiling(Math.Min(textMax, one.Width)) + PadX * 2 + IconSize + Gap + 10;
   width = Math.Max(MinWidth, Math.Min(Math.Min(MaxWidth, screenWidth - 40), width));
   return new Size(width, twoLines ? Height2 : Height1);
  }

  protected override void DrawCard(Graphics g) {
   float cy = CardSize.Height / 2f;
   var icon = new RectangleF(PadX, cy - IconSize / 2f, IconSize, IconSize);
   using (var b = new SolidBrush(Theme.Red)) g.FillEllipse(b, icon);
   using (var pen = new Pen(Color.White, 2.2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) {
    g.DrawLine(pen, icon.X + IconSize / 2f, icon.Y + 5.5f, icon.X + IconSize / 2f, icon.Y + 12.5f);
   }
   using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, icon.X + IconSize / 2f - 1.4f, icon.Y + 15.2f, 2.8f, 2.8f);
   var textRect = new RectangleF(PadX + IconSize + Gap, 6, CardSize.Width - PadX * 2 - IconSize - Gap, CardSize.Height - 12);
   using (var f = (StringFormat)StringFormat.GenericTypographic.Clone())
   using (var b = new SolidBrush(Theme.Label)) {
    f.LineAlignment = StringAlignment.Center;
    f.Trimming = StringTrimming.EllipsisCharacter;
    if (!twoLines) f.FormatFlags |= StringFormatFlags.NoWrap;
    g.DrawString(message, font, b, textRect, f);
   }
  }

  protected override void Dispose(bool disposing) {
   if (disposing) { life.Dispose(); font.Dispose(); }
   base.Dispose(disposing);
  }
 }
}
