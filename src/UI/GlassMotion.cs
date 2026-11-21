using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

using MacDesk.Core;

namespace MacDeskNative {
 // iOS-style backdrop while a folder is open: the whole screen, blurred and dimmed.
 // Presented once; only its constant alpha changes during the morph (cheap, thread-safe).
 // Clicking it closes the folder and the click is consumed, exactly like tapping outside on iOS.
 internal sealed class BackdropOverlay : Form {
  readonly LayeredSurface surface = new LayeredSurface();
  readonly Action outsideClick;
  IntPtr cachedHandle;

  internal BackdropOverlay(Rectangle bounds, Form owner, Backdrop backdrop, Action outsideClick) {
   this.outsideClick = outsideClick;
   Owner = owner;
   ShowInTaskbar = false;
   FormBorderStyle = FormBorderStyle.None;
   StartPosition = FormStartPosition.Manual;
   AutoScaleMode = AutoScaleMode.None;
   Text = "MacDesk Backdrop";
   Bounds = bounds;
   cachedHandle = Handle;
   var g = surface.Begin(bounds.Size);
   g.CompositingMode = CompositingMode.SourceCopy;
   if (backdrop != null && backdrop.Dimmed != null) {
    // Pre-composed off the UI thread while the mouse button was still down: just a copy.
    g.DrawImageUnscaled(backdrop.Dimmed, backdrop.Area.X - bounds.X, backdrop.Area.Y - bounds.Y);
    g.CompositingMode = CompositingMode.SourceOver;
   } else if (backdrop != null && backdrop.Image != null) {
    g.DrawImageUnscaled(backdrop.Image, backdrop.Area.X - bounds.X, backdrop.Area.Y - bounds.Y);
    g.CompositingMode = CompositingMode.SourceOver;
    using (var dim = new SolidBrush(Color.FromArgb(Backdrop.BackdropDim, 0, 0, 0))) g.FillRectangle(dim, 0, 0, bounds.Width, bounds.Height);
   } else {
    using (var dim = new SolidBrush(Color.FromArgb(150, 8, 10, 14))) g.FillRectangle(dim, 0, 0, bounds.Width, bounds.Height);
    g.CompositingMode = CompositingMode.SourceOver;
   }
   surface.Present(cachedHandle, bounds.Location, 0, null);
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

  internal void SetAlphaThreadSafe(double alpha) { LayeredSurface.SetAlpha(cachedHandle, Math.Max(0.0, Math.Min(1.0, alpha))); }

  protected override void Dispose(bool disposing) {
   if (disposing) surface.Dispose();
   base.Dispose(disposing);
  }
 }

 // The folder morph. Frame 0 is pixel-identical to the desktop folder icon; the last frame is
 // pixel-identical to the open panel (same material, same snapshot), so neither hand-off pops.
 // The window never moves or resizes during the animation (resizing a layered window every
 // frame makes DWM reallocate its surface and pace frames unevenly): it covers the whole path
 // from icon to sheet once, and each frame only re-sends the pixels that changed.
 // Present() runs on the render thread; everything it touches is owned by this instance.
 internal sealed class GlassMotion : Form {
  Bitmap content;          // panel sheet contents (icons + labels), sheet-sized, transparent
  Bitmap originPreview;    // the desktop folder icon face, incl. its own material
  Bitmap materialImage;    // blurred backdrop with the sheet tint baked in, canvas-sized
  TextureBrush material;
  readonly Rectangle canvas; // screen rectangle the motion window occupies (fixed)
  readonly LayeredSurface surface = new LayeredSurface();
  readonly bool activate;
  readonly float originRadius, sheetRadius;
  internal Func<Keys, bool> KeyHandler;
  internal Action<Point> Clicked;
  IntPtr layered;
  Rectangle drawn = Rectangle.Empty;
  bool presentedOnce;
  readonly ImageAttributes fade = new ImageAttributes();
  readonly ColorMatrix fadeMatrix = new ColorMatrix();

  // Per-phase timing (ms, summed over frames) for MACDESK_MOTION_LOG.
  internal double TimeMaterial, TimeContent, TimeOrigin, TimePresent;

  internal GlassMotion(Bitmap content, Bitmap originPreview, Backdrop backdrop, Rectangle canvas, float originRadius, float sheetRadius, Form owner, bool activate) {
   this.content = content;
   this.originPreview = originPreview;
   this.activate = activate;
   this.originRadius = originRadius;
   this.sheetRadius = sheetRadius;
   this.canvas = canvas;
   // Crop the backdrop to just the canvas and bake the sheet tint in: one fill per frame.
   materialImage = new Bitmap(Math.Max(1, canvas.Width), Math.Max(1, canvas.Height), PixelFormat.Format32bppPArgb);
   using (var g = Graphics.FromImage(materialImage)) {
    if (backdrop != null && backdrop.Image != null) {
     g.CompositingMode = CompositingMode.SourceCopy;
     g.DrawImage(backdrop.Image, new Rectangle(0, 0, canvas.Width, canvas.Height), new Rectangle(canvas.X - backdrop.Area.X, canvas.Y - backdrop.Area.Y, canvas.Width, canvas.Height), GraphicsUnit.Pixel);
     g.CompositingMode = CompositingMode.SourceOver;
     using (var tint = new SolidBrush(Material.SheetTint)) g.FillRectangle(tint, 0, 0, canvas.Width, canvas.Height);
    } else {
     using (var fill = new SolidBrush(Material.SheetFallback)) g.FillRectangle(fill, 0, 0, canvas.Width, canvas.Height);
    }
   }
   material = new TextureBrush(materialImage, WrapMode.Clamp);
   Owner = owner;
   ShowInTaskbar = false;
   FormBorderStyle = FormBorderStyle.None;
   StartPosition = FormStartPosition.Manual;
   AutoScaleMode = AutoScaleMode.None;
   KeyPreview = true;
   Text = "MacDesk Folder Motion";
   Bounds = canvas;
   surface.Reserve(canvas.Size);
  }

  internal Rectangle Canvas { get { return canvas; } }

  protected override bool ShowWithoutActivation { get { return !activate; } }
  protected override bool ProcessCmdKey(ref Message message, Keys key) {
   if (KeyHandler != null && KeyHandler(key)) return true;
   return base.ProcessCmdKey(ref message, key);
  }
  protected override CreateParams CreateParams {
   get {
    var p = base.CreateParams;
    p.ExStyle = (p.ExStyle | 0x80000 | 0x80 | (activate ? 0 : 0x8000000)) & ~0x40000;
    return p;
   }
  }
  protected override void WndProc(ref Message m) {
   if (m.Msg == 0x21 && !activate) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
   base.WndProc(ref m);
  }
  protected override void OnMouseUp(MouseEventArgs e) {
   base.OnMouseUp(e);
   if (e.Button == MouseButtons.Left && Clicked != null) Clicked(PointToScreen(e.Location));
  }

  // bounds: the sheet rectangle on screen. progress: 0 = folder icon, 1 = open sheet.
  internal void Present(Rectangle bounds, double progress, double opacity) {
   if (bounds.Width < 2 || bounds.Height < 2) return;
   long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
   var g = surface.Begin(canvas.Size);
   var local = new Rectangle(bounds.X - canvas.X, bounds.Y - canvas.Y, bounds.Width, bounds.Height);
   Rectangle now = Rectangle.Inflate(local, 2, 2);
   if (!drawn.IsEmpty) surface.Clear(drawn);
   if (!presentedOnce) surface.Clear(new Rectangle(Point.Empty, canvas.Size));
   g.SmoothingMode = SmoothingMode.AntiAlias;
   g.InterpolationMode = InterpolationMode.Bilinear;
   g.PixelOffsetMode = PixelOffsetMode.Half;
   g.CompositingQuality = CompositingQuality.HighSpeed;

   float p = (float)Math.Max(0.0, Math.Min(1.0, progress));
   var rect = new RectangleF(local.X, local.Y, local.Width, local.Height);
   float radius = Math.Min(originRadius + (sheetRadius - originRadius) * Math.Min(1f, p * 1.15f), Math.Min(local.Width, local.Height) * 0.5f);
   using (var shape = Ui.Squircle(rect, radius)) {
    g.FillPath(material, shape);
    long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
    TimeMaterial += Ms(t0, t1);
    // Exact shape clipping is only needed while the sheet is small; later the content's own
    // transparent margins keep it inside, and a rectangle clip is far cheaper per frame.
    if (p < 0.45f) g.SetClip(shape); else g.SetClip(rect);

    // Panel contents grow in place (uniform scale, never squashed) and fade in.
    float contentWeight = Smooth((p - 0.12f) / 0.6f);
    if (content != null && contentWeight > 0.01f) {
     float scale = 0.55f + 0.45f * p;
     float cx = rect.X + rect.Width * 0.5f, cy = rect.Y + rect.Height * 0.5f;
     if (scale > 0.993f && contentWeight >= 0.99f) {
      // Settling frames: a plain unscaled blit is several times cheaper than a resample.
      g.DrawImageUnscaled(content, (int)Math.Round(cx - content.Width * 0.5f), (int)Math.Round(cy - content.Height * 0.5f));
     } else {
      float w = content.Width * scale, h = content.Height * scale;
      DrawFaded(g, content, new RectangleF(cx - w * 0.5f, cy - h * 0.5f, w, h), contentWeight);
     }
    }
    long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
    TimeContent += Ms(t1, t2);

    // The folder icon face dissolves as it stretches into the sheet (and reappears on close).
    float originWeight = 1f - Smooth(p / 0.38f);
    if (originPreview != null && originWeight > 0.01f) {
     float side = Math.Min(rect.Width, rect.Height);
     var dest = new RectangleF(rect.X + (rect.Width - side) * 0.5f, rect.Y + (rect.Height - side) * 0.5f, side, side);
     if (p < 0.02f) dest = rect;
     DrawFaded(g, originPreview, dest, originWeight);
    }
    g.SetClip(new Rectangle(Point.Empty, canvas.Size));
    Material.Rim(g, rect, radius, 0.55f + 0.45f * p);
    TimeOrigin += Ms(t2, System.Diagnostics.Stopwatch.GetTimestamp());
   }

   long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
   IntPtr hwnd = layered;
   if (hwnd == IntPtr.Zero) { hwnd = Handle; layered = hwnd; }
   Rectangle dirty = drawn.IsEmpty ? now : Rectangle.Union(drawn, now);
   surface.Present(hwnd, canvas.Location, (byte)Math.Max(0, Math.Min(255, (int)Math.Round(opacity * 255.0))), presentedOnce ? (Rectangle?)dirty : null);
   presentedOnce = true;
   drawn = now;
   TimePresent += Ms(t3, System.Diagnostics.Stopwatch.GetTimestamp());
  }

  static double Ms(long a, long b) { return (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
  static float Smooth(float x) { x = Math.Max(0f, Math.Min(1f, x)); return x * x * (3f - 2f * x); }

  void DrawFaded(Graphics g, Bitmap image, RectangleF dest, float alpha) {
   if (alpha >= 0.99f) { g.DrawImage(image, dest); return; }
   fadeMatrix.Matrix33 = alpha;
   fade.SetColorMatrix(fadeMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
   var pts = new[] { new PointF(dest.Left, dest.Top), new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) };
   g.DrawImage(image, pts, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, fade);
  }

  protected override void OnPaint(PaintEventArgs e) { }
  protected override void OnPaintBackground(PaintEventArgs e) { }
  protected override void Dispose(bool disposing) {
   if (disposing) {
    surface.Dispose();
    fade.Dispose();
    if (material != null) { material.Dispose(); material = null; }
    if (materialImage != null) { materialImage.Dispose(); materialImage = null; }
    if (content != null) { content.Dispose(); content = null; }
    if (originPreview != null) { originPreview.Dispose(); originPreview = null; }
   }
   base.Dispose(disposing);
  }
 }
}
