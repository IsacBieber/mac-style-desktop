using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDeskNative {
 /// Receives keys routed from the desktop layer. The folder panel and the popups never take
 /// focus (activating a window would end Win+D "show desktop"), so AppController.RouteKey /
 /// RouteChar forward keys to the top of a target stack instead.
 internal interface IKeyTarget {
  bool HandleKey(Keys keyData);
  bool HandleChar(char c);
 }

 /// Watches the mouse buttons while one of our non-activating surfaces is up. The desktop stays
 /// the active window, so Windows never tells us about clicks elsewhere; this reports the first
 /// press (left or right) and, optionally, another app taking the foreground.
 internal sealed class OutsideClickWatcher : IDisposable {
  [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
  static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

  readonly Timer timer;
  bool leftWas, rightWas;
  /// While true, presses are swallowed (not reported later).
  public Func<bool> Paused;
  /// A button went down; argument is the cursor in screen coordinates.
  public Action<Point> Pressed;
  /// Another program became the foreground window (null = don't check).
  public Action ForegroundLost;

  public OutsideClickWatcher(int intervalMs) {
   timer = new Timer { Interval = intervalMs };
   timer.Tick += delegate { Poll(); };
  }
  public bool Running { get { return timer.Enabled; } }
  public void Start() { leftWas = Down(0x01); rightWas = Down(0x02); timer.Start(); }
  public void Stop() { timer.Stop(); }

  void Poll() {
   bool left = Down(0x01), right = Down(0x02);
   bool pressed = (left && !leftWas) || (right && !rightWas);
   leftWas = left;
   rightWas = right;
   if (Paused != null && Paused()) return;
   if (ForegroundLost != null && !FolderLayer.ForegroundAllowsPanel()) { ForegroundLost(); return; }
   if (pressed && Pressed != null) Pressed(Control.MousePosition);
  }

  public void Dispose() { timer.Stop(); timer.Dispose(); }
 }

 /// A floating card of frosted glass: never activated, keys routed in, dismissed by a press
 /// outside or by Escape. It grows out of an anchor with a spring (scale + fade about the point
 /// of the card nearest the anchor) and lands on whole pixels. Subclasses draw the content in
 /// card coordinates (0,0 = top-left of the glass) and handle card-relative mouse input.
 internal abstract class GlassPopup : Form, IKeyTarget {
  /// Transparent band around the card for its soft shadow (and the slight spring overshoot).
  protected const int ShadowPad = 18;

  protected readonly AppController app;
  readonly LayeredSurface surface = new LayeredSurface();
  readonly FramePump pump;
  readonly OutsideClickWatcher outside;
  readonly Spring openSpring = new Spring(0, Theme.PopupOpenResponse, Theme.PopupOpenDamping);
  readonly Spring closeSpring = new Spring(0, Theme.PopupCloseResponse, Theme.PopupCloseDamping);
  bool closing, contentDirty = true;
  float presentedScale;
  byte presentedAlpha;
  Size cardSize;
  PointF pivot;
  Bitmap chrome, content;
  Size chromeSize;
  Point chromeOrigin;
  Backdrop glass;
  TextureBrush glassBrush;
  Point glassBrushOrigin;
  Rectangle glassBrushRegion;
  Point mouseCard = new Point(-999, -999);

  protected GlassPopup(AppController app) {
   this.app = app;
   AutoScaleMode = AutoScaleMode.None;
   FormBorderStyle = FormBorderStyle.None;
   ShowInTaskbar = false;
   StartPosition = FormStartPosition.Manual;
   SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
   pump = new FramePump(this, Tick);
   outside = new OutsideClickWatcher(30) {
    Paused = () => IsDisposed || !Visible || closing || OutsidePaused(),
    ForegroundLost = () => { if (FollowsForeground) Dismiss(); },
    Pressed = OnPress
   };
  }

  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams {
   get {
    var p = base.CreateParams;
    p.ExStyle = (p.ExStyle | 0x80000 | 0x80 | 0x8000000) & ~0x40000; // LAYERED | TOOLWINDOW | NOACTIVATE
    return p;
   }
  }
  protected override void WndProc(ref Message m) {
   if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // WM_MOUSEACTIVATE -> MA_NOACTIVATE
   base.WndProc(ref m);
  }
  protected override void OnPaint(PaintEventArgs e) { }
  protected override void OnPaintBackground(PaintEventArgs e) { }

  // ---- What subclasses provide ----------------------------------------------------------

  protected virtual float Radius { get { return Theme.PopoverRadius; } }
  protected virtual Color Tint { get { return Material.SheetTint; } }
  protected virtual Color Fallback { get { return Material.SheetFallback; } }
  /// Popups that take the keyboard go on AppController's key-target stack while open.
  protected virtual bool TakesKeyboard { get { return true; } }
  /// Scale at p = 0 (the card grows from this size to 1).
  protected virtual float StartScale { get { return 0.86f; } }
  /// Draws the content over the glass, in card coordinates; clipped to the card shape.
  protected abstract void DrawCard(Graphics g);
  /// Per-frame content animation; return true while something still moves.
  protected virtual bool StepCard(double dt) { return false; }
  protected virtual bool CardHasPendingWork { get { return false; } }
  protected virtual void OnOutsidePress(Point screen) { Dismiss(); }
  /// Close when another program takes the foreground (toasts stay for their few seconds).
  protected virtual bool FollowsForeground { get { return true; } }
  /// Called as the close begins (commit a pending edit here).
  protected virtual void OnDismissing() { }
  protected virtual bool OutsidePaused() { return false; }
  protected virtual void OnDismissed() { }
  protected virtual void OnCardMouseMove(Point p) { }
  protected virtual void OnCardMouseDown(Point p, MouseButtons button) { }
  protected virtual void OnCardMouseUp(Point p, MouseButtons button) { }
  protected virtual void OnCardMouseWheel(Point p, int delta) { }
  protected virtual void OnCardMouseLeave() { }
  public virtual bool HandleKey(Keys keyData) { if (keyData == Keys.Escape) { Dismiss(); return true; } return false; }
  public virtual bool HandleChar(char c) { return false; }

  // ---- State ----------------------------------------------------------------------------

  public bool IsOpen { get { return !IsDisposed && Visible && !closing; } }
  protected Size CardSize { get { return cardSize; } }
  protected Point MouseCard { get { return mouseCard; } }
  /// Screen rectangle of the glass card (without the shadow band).
  public Rectangle CardScreenRect { get { return new Rectangle(Left + ShadowPad, Top + ShadowPad, cardSize.Width, cardSize.Height); } }
  /// Open progress: 0 = at the anchor, 1 = landed (springs may overshoot slightly).
  protected double Progress { get { return closing ? closeSpring.Value : openSpring.Value; } }
  // Input is taken while the card is still growing (the spring is only motion).
  bool Interactive { get { return IsOpen; } }

  /// Card coordinates -> screen.
  protected Point CardToScreen(PointF p) { return new Point(Left + ShadowPad + (int)Math.Round(p.X), Top + ShadowPad + (int)Math.Round(p.Y)); }

  /// Asks for the content to be drawn again on the next frame.
  protected void RedrawCard() { contentDirty = true; if (IsHandleCreated && Visible) pump.Start(); }

  // ---- Glass ----------------------------------------------------------------------------

  /// Captures (and blurs) what is on screen in `area` - call before the card is visible, with
  /// an area covering every place the card may move or grow to while it stays open.
  protected void CaptureGlass(Rectangle area) {
   ReleaseGlass();
   chromeSize = Size.Empty;
   if (area.Width < 1 || area.Height < 1) return;
   try {
    using (Bitmap shot = Backdrop.CaptureScreen(area)) glass = Backdrop.FromImage(shot, area);
   } catch { glass = null; }
  }

  void ReleaseGlass() {
   if (glassBrush != null) { glassBrush.Dispose(); glassBrush = null; }
   if (glass != null) { glass.Dispose(); glass = null; }
  }

  TextureBrush GlassBrush(Point cardOrigin) {
   if (glass == null) return null;
   var region = Rectangle.Intersect(new Rectangle(cardOrigin, cardSize), glass.Area);
   if (region.Width <= 0 || region.Height <= 0) return null;
   if (glassBrush == null || !glassBrushRegion.Contains(region)) {
    if (glassBrush != null) glassBrush.Dispose();
    glassBrushRegion = region;
    glassBrush = glass.CreateBrush(region, cardOrigin);
    glassBrushOrigin = cardOrigin;
   } else if (glassBrushOrigin != cardOrigin) {
    glassBrush.ResetTransform();
    glassBrush.TranslateTransform(glassBrushRegion.X - cardOrigin.X, glassBrushRegion.Y - cardOrigin.Y);
    glassBrushOrigin = cardOrigin;
   }
   return glassBrush;
  }

  // ---- Showing and dismissing ------------------------------------------------------------

  /// Shows the card at `card` (screen rectangle of the glass), growing out of `anchor`, or moves
  /// / resizes it if it is already open. Call CaptureGlass first when it is not yet visible.
  protected void PresentCard(Rectangle card, RectangleF anchor, bool drawNow = true) {
   if (IsDisposed) return;
   cardSize = card.Size;
   var window = Rectangle.Inflate(card, ShadowPad, ShadowPad);
   float ax = anchor.X + anchor.Width / 2f - card.X, ay = anchor.Y + anchor.Height / 2f - card.Y;
   pivot = new PointF(Math.Max(0, Math.Min(card.Width, ax)), Math.Max(0, Math.Min(card.Height, ay)));
   contentDirty = true;
   if (!Visible) {
    closing = false;
    openSpring.Snap(0);
    openSpring.Target = 1;
    Bounds = window;
    Show(app.PopupOwner);
    app.RegisterPopup(this);
    if (TakesKeyboard) app.PushKeyTarget(this);
    outside.Start();
   } else {
    if (closing) {
     closing = false;
     openSpring.Value = closeSpring.Value;
     openSpring.Velocity = closeSpring.Velocity;
     openSpring.Target = 1;
     if (TakesKeyboard) app.PushKeyTarget(this);
     outside.Start();
    }
    if (Bounds != window) Bounds = window;
   }
   app.BringPopupToTop(this);
   if (drawNow) Render();
   pump.Start();
  }

  /// Starts the close animation (Escape, outside press, or the owner asking).
  public void Dismiss() {
   if (IsDisposed || !Visible || closing) return;
   OnDismissing();
   closing = true;
   closeSpring.Value = Math.Min(1, openSpring.Value);
   closeSpring.Velocity = Math.Min(0, openSpring.Velocity);
   closeSpring.Target = 0;
   outside.Stop();
   app.PopKeyTarget(this);
   pump.Start();
  }

  /// Hides at once without animating (app exit, tests).
  public void DismissNow() {
   if (IsDisposed || !Visible) return;
   closing = true;
   FinishClose();
  }

  void FinishClose() {
   pump.Stop();
   outside.Stop();
   app.PopKeyTarget(this);
   Hide();
   app.UnregisterPopup(this);
   ReleaseGlass();
   presentedScale = 0;
   closing = false;
   mouseCard = new Point(-999, -999);
   OnDismissed();
  }

  void OnPress(Point screen) {
   if (CardScreenRect.Contains(screen) || app.PopupContains(screen, this)) return;
   OnOutsidePress(screen);
  }

  // ---- Frames ---------------------------------------------------------------------------

  bool Tick(double dt) {
   double previousProgress = Progress;
   bool moving = closing ? closeSpring.Step(dt) : openSpring.Step(dt);
   bool content = StepCard(dt);
   if (content) contentDirty = true;
   if (closing && (closeSpring.Settled || closeSpring.Value < 0.015)) { FinishClose(); return false; }
   if (contentDirty || Progress != previousProgress) Render();
   return moving || content || CardHasPendingWork;
  }

  void Render() {
   if (IsDisposed || !IsHandleCreated || cardSize.Width < 1 || cardSize.Height < 1) return;
   Size window = new Size(cardSize.Width + ShadowPad * 2, cardSize.Height + ShadowPad * 2);
   bool redraw = contentDirty || content == null || content.Size != window;
   if (redraw) RenderContent(window);
   double p = Math.Max(0, Math.Min(1.04, Progress));
   float scale = (float)(StartScale + (1 - StartScale) * p);
   if (Math.Abs(scale - 1) < 0.0015f) scale = 1;
   byte alpha = (byte)Math.Round(Math.Min(1, p * 1.7) * 255);
   if (!redraw && scale == presentedScale) {
    if (alpha != presentedAlpha) {
     surface.Present(Handle, null, alpha, null);
     presentedAlpha = alpha;
    }
    return;
   }
   var g = surface.Begin(window);
   if (scale == 1) {
    // Landed: a 1:1 copy (whole pixels, no resampling).
    var mode = g.CompositingMode;
    g.CompositingMode = CompositingMode.SourceCopy;
    Ui.Blit(g, content, 0, 0);
    g.CompositingMode = mode;
   } else {
    float px = ShadowPad + pivot.X, py = ShadowPad + pivot.Y;
    var dest = new RectangleF(px - px * scale, py - py * scale, window.Width * scale, window.Height * scale);
    // Copy the scaled frame over the old one and clear only what it no longer covers.
    var inner = Rectangle.FromLTRB((int)Math.Ceiling(dest.Left), (int)Math.Ceiling(dest.Top), (int)Math.Floor(dest.Right), (int)Math.Floor(dest.Bottom));
    inner.Intersect(new Rectangle(Point.Empty, window));
    if (inner.Width <= 0 || inner.Height <= 0) surface.Clear(new Rectangle(Point.Empty, window));
    else {
     if (inner.Top > 0) surface.Clear(new Rectangle(0, 0, window.Width, inner.Top));
     if (inner.Bottom < window.Height) surface.Clear(new Rectangle(0, inner.Bottom, window.Width, window.Height - inner.Bottom));
     if (inner.Left > 0) surface.Clear(new Rectangle(0, inner.Top, inner.Left, inner.Height));
     if (inner.Right < window.Width) surface.Clear(new Rectangle(inner.Right, inner.Top, window.Width - inner.Right, inner.Height));
    }
    var im = g.InterpolationMode;
    var cm = g.CompositingMode;
    g.InterpolationMode = InterpolationMode.Bilinear;
    g.CompositingMode = CompositingMode.SourceCopy;
    g.DrawImage(content, dest, new RectangleF(0, 0, window.Width, window.Height), GraphicsUnit.Pixel);
    g.CompositingMode = cm;
    g.InterpolationMode = im;
   }
   // The fade is the layered window's constant alpha: free, no per-pixel work.
   surface.Present(Handle, Location, alpha, null);
   presentedScale = scale;
   presentedAlpha = alpha;
  }

  void RenderContent(Size window) {
   Point cardOrigin = new Point(Left + ShadowPad, Top + ShadowPad);
   if (chrome == null || chromeSize != cardSize || chromeOrigin != cardOrigin) {
    if (chrome != null) chrome.Dispose();
    chrome = new Bitmap(window.Width, window.Height, PixelFormat.Format32bppPArgb);
    using (var g = Graphics.FromImage(chrome)) {
     g.Clear(Color.Transparent);
     Ui.PrepareQuality(g);
     g.TranslateTransform(ShadowPad, ShadowPad);
     var r = new RectangleF(0, 0, cardSize.Width, cardSize.Height);
     for (int i = 0; i < 4; i++) {
      float spread = 3f + i * 4f;
      var s = new RectangleF(r.X - spread * 0.5f, r.Y + 4f + i, r.Width + spread, r.Height + spread * 0.5f);
      using (var path = Ui.Squircle(s, Radius + spread * 0.5f))
      using (var b = new SolidBrush(Color.FromArgb(18 - i * 4, 0, 0, 0))) g.FillPath(b, path);
     }
     using (var shape = Ui.Squircle(r, Radius)) {
      var mode = g.CompositingMode;
      g.CompositingMode = CompositingMode.SourceCopy;
      using (var clear = new SolidBrush(Color.FromArgb(0, 0, 0, 0))) g.FillPath(clear, shape);
      g.CompositingMode = mode;
      Material.Fill(g, shape, GlassBrush(cardOrigin), Tint, Fallback);
     }
    }
    chromeSize = cardSize;
    chromeOrigin = cardOrigin;
   }
   if (content == null || content.Size != window) {
    if (content != null) content.Dispose();
    content = new Bitmap(window.Width, window.Height, PixelFormat.Format32bppPArgb);
   }
   using (var g = Graphics.FromImage(content)) {
    g.CompositingMode = CompositingMode.SourceCopy;
    Ui.Blit(g, chrome, 0, 0);
    g.CompositingMode = CompositingMode.SourceOver;
    Ui.PrepareQuality(g);
    g.TranslateTransform(ShadowPad, ShadowPad);
    var r = new RectangleF(0, 0, cardSize.Width, cardSize.Height);
    using (var shape = Ui.Squircle(r, Radius)) {
     g.SetClip(shape);
     DrawCard(g);
     g.ResetClip();
    }
    Material.Rim(g, r, Radius, 0.8f);
   }
   contentDirty = false;
  }

  // GDI+ measuring outside a paint (layout happens before anything is drawn). UI thread only.
  static Graphics measuring;
  protected static SizeF MeasureText(string text, Font font, float maxWidth, StringFormat format) {
   if (measuring == null) { measuring = Graphics.FromImage(new Bitmap(1, 1)); Ui.PrepareQuality(measuring); }
   if (String.IsNullOrEmpty(text)) return new SizeF(0, font.GetHeight(measuring));
   return measuring.MeasureString(text, font, new SizeF(maxWidth, 10000), format);
  }

  // ---- Offscreen hooks (self-test / bench): never shown -------------------------------------

  /// Sizes the card without showing the window.
  internal void SizeCardForTest(Size size) { cardSize = size; contentDirty = true; }

  /// Renders one frame at open progress p into the (hidden) layered window; returns ms.
  internal double FrameMsForTest(double p, bool contentChanged) {
   IntPtr h = Handle;
   closing = false;
   openSpring.Snap(p);
   if (contentChanged) contentDirty = true;
   long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
   Render();
   return (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
  }

  /// Advances the content animation by dt without the clock (bench).
  internal bool StepForTest(double dt) { bool more = StepCard(dt); if (more) contentDirty = true; return more; }

  /// Draws the landed card offscreen (self-test / bench snapshots); caller disposes.
  internal Bitmap RenderSnapshot() {
   Size window = new Size(cardSize.Width + ShadowPad * 2, cardSize.Height + ShadowPad * 2);
   RenderContent(window);
   return (Bitmap)content.Clone();
  }

  // ---- Mouse (window -> card coordinates) ------------------------------------------------

  Point ToCard(Point window) { return new Point(window.X - ShadowPad, window.Y - ShadowPad); }

  protected override void OnMouseMove(MouseEventArgs e) {
   base.OnMouseMove(e);
   mouseCard = ToCard(e.Location);
   if (Interactive) OnCardMouseMove(mouseCard);
  }
  protected override void OnMouseDown(MouseEventArgs e) {
   base.OnMouseDown(e);
   Point p = ToCard(e.Location);
   if (!new Rectangle(Point.Empty, cardSize).Contains(p)) { OnOutsidePress(PointToScreen(e.Location)); return; }
   if (Interactive) OnCardMouseDown(p, e.Button);
  }
  protected override void OnMouseUp(MouseEventArgs e) {
   base.OnMouseUp(e);
   if (Interactive) OnCardMouseUp(ToCard(e.Location), e.Button);
  }
  protected override void OnMouseWheel(MouseEventArgs e) {
   base.OnMouseWheel(e);
   if (Interactive) OnCardMouseWheel(ToCard(e.Location), e.Delta);
  }
  protected override void OnMouseLeave(EventArgs e) {
   base.OnMouseLeave(e);
   mouseCard = new Point(-999, -999);
   OnCardMouseLeave();
  }

  protected override void Dispose(bool disposing) {
   if (disposing) {
    if (app != null) { app.PopKeyTarget(this); app.UnregisterPopup(this); }
    pump.Dispose();
    outside.Dispose();
    surface.Dispose();
    ReleaseGlass();
    if (chrome != null) { chrome.Dispose(); chrome = null; }
    if (content != null) { content.Dispose(); content = null; }
   }
   base.Dispose(disposing);
  }
 }
}
