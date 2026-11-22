using System;
using System.Drawing;
using System.Windows.Forms;

namespace MacDeskNative {
 // iOS-style backdrop while a folder is open: the whole screen, blurred and dimmed.
 // Presented once; only its constant alpha changes during the morph (cheap, thread-safe).
 // Clicking it closes the folder and the click is consumed, exactly like tapping outside on iOS.
 internal sealed class BackdropOverlay : Form {
  readonly LayeredSurface surface = new LayeredSurface();
  readonly Action outsideClick;
  readonly FramePump pump;
  readonly Spring reveal = new Spring(1, 0.16, 1.0);
  IntPtr cachedHandle;
  byte alpha;
  double requestedAlpha;
  bool preparing, hasFrame;

  internal BackdropOverlay(Rectangle bounds, Form owner, Backdrop backdrop, bool preparing, Action outsideClick) {
   this.outsideClick = outsideClick;
   this.preparing = preparing;
   pump = new FramePump(this, Tick);
   Owner = owner;
   ShowInTaskbar = false;
   FormBorderStyle = FormBorderStyle.None;
   StartPosition = FormStartPosition.Manual;
   AutoScaleMode = AutoScaleMode.None;
   Text = "MacDesk Backdrop";
   Bounds = bounds;
   cachedHandle = Handle;
   if (!preparing) SetImage(backdrop);
  }

  void SetImage(Backdrop backdrop) {
   var frame = backdrop == null ? null : backdrop.TakeDimmed();
   if (frame != null) surface.UseFrame(frame);
   else surface.Begin(ClientSize).Clear(Color.FromArgb(150, 8, 10, 14));
   surface.Present(cachedHandle, Location, alpha, null);
   hasFrame = true;
  }

  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams {
   get {
    CreateParams p = base.CreateParams;
    p.ExStyle |= 0x80000 | 0x80 | 0x8000000; // LAYERED | TOOLWINDOW | NOACTIVATE
    p.ExStyle &= ~0x40000;
    return p;
   }
  }
  protected override void WndProc(ref Message m) {
   if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // WM_MOUSEACTIVATE -> MA_NOACTIVATE
   base.WndProc(ref m);
  }
  protected override void OnMouseDown(MouseEventArgs e) {
   base.OnMouseDown(e);
   if (outsideClick != null) outsideClick();
  }
  protected override void OnPaint(PaintEventArgs e) { }
  protected override void OnPaintBackground(PaintEventArgs e) { }

  internal void SetAlpha(double value) {
   requestedAlpha = value;
   PresentAlpha();
  }

  internal void FinishPreparing(Backdrop backdrop) {
   if (backdrop != null || !hasFrame) SetImage(backdrop);
   preparing = false;
   reveal.Snap(0);
   reveal.Target = 1;
   pump.Start();
  }

  bool Tick(double dt) {
   bool moving = reveal.Step(dt);
   PresentAlpha();
   return moving;
  }

  void PresentAlpha() {
   if (!hasFrame) return;
   // Fade in a late result without flashing the desktop.
   double opacity = preparing ? 0 : requestedAlpha * reveal.Value;
   byte next = requestedAlpha <= 0 ? (byte)0 : (byte)Math.Max(2, Math.Min(255, (int)Math.Round(opacity * 255)));
   if (next != alpha && LayeredSurface.SetAlpha(cachedHandle, next / 255.0)) alpha = next;
  }

  protected override void Dispose(bool disposing) {
   if (disposing) { pump.Dispose(); surface.Dispose(); }
   base.Dispose(disposing);
  }
 }
}
