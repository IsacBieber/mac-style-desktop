using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

using MacDesk.Core;

namespace MacDeskNative {
 // Shared look & feel: one palette, one corner shape, one spring model, one material.
 internal static class Ui {
  // Typography: Segoe UI Variable for Latin/digits, YaHei UI for CJK (both ship with Windows 11).
  internal const string Cjk = "Microsoft YaHei UI";
  internal const string DisplayLight = "Segoe UI Variable Display Light";
  internal const string DisplayRegular = "Segoe UI Variable Display";
  internal const string DisplaySemibold = "Segoe UI Variable Display Semib";
  internal const string TextRegular = "Segoe UI Variable Text";
  internal const string TextSemibold = "Segoe UI Variable Text Semibold";

  internal static Font MakeFont(string family, float px, FontStyle style) {
   try {
    var f = new Font(family, px, style, GraphicsUnit.Pixel);
    if (String.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase)) return f;
    f.Dispose();
   } catch (ArgumentException) { }
   return new Font("Segoe UI", px, style, GraphicsUnit.Pixel);
  }

  // Apple system colors (dark appearance).
  internal static readonly Color Red = Color.FromArgb(255, 69, 58);
  internal static readonly Color Blue = Color.FromArgb(10, 132, 255);
  internal static readonly Color Orange = Color.FromArgb(255, 159, 10);
  internal static readonly Color Label = Color.FromArgb(255, 255, 255);
  internal static readonly Color SecondaryLabel = Color.FromArgb(190, 235, 235, 245);
  internal static readonly Color TertiaryLabel = Color.FromArgb(120, 235, 235, 245);

  // Continuous ("squircle") corners: the curve starts earlier than a circular arc and
  // eases into the straight edge, which is what makes Apple shapes look soft.
  internal static GraphicsPath Squircle(RectangleF r, float radius) {
   var p = new GraphicsPath();
   if (r.Width < 2 || r.Height < 2) { p.AddRectangle(r); return p; }
   float a = Math.Min(radius * 1.3f, Math.Min(r.Width, r.Height) * 0.5f);
   float c = a * (0.348f / 1.3f);
   float x0 = r.Left, y0 = r.Top, x1 = r.Right, y1 = r.Bottom;
   p.StartFigure();
   p.AddLine(x0 + a, y0, x1 - a, y0);
   p.AddBezier(x1 - a, y0, x1 - c, y0, x1, y0 + c, x1, y0 + a);
   p.AddLine(x1, y0 + a, x1, y1 - a);
   p.AddBezier(x1, y1 - a, x1, y1 - c, x1 - c, y1, x1 - a, y1);
   p.AddLine(x1 - a, y1, x0 + a, y1);
   p.AddBezier(x0 + a, y1, x0 + c, y1, x0, y1 - c, x0, y1 - a);
   p.AddLine(x0, y1 - a, x0, y0 + a);
   p.AddBezier(x0, y0 + a, x0, y0 + c, x0 + c, y0, x0 + a, y0);
   p.CloseFigure();
   return p;
  }

  internal static Color WithAlpha(Color c, double alpha) {
   return Color.FromArgb(Math.Max(0, Math.Min(255, (int)Math.Round(c.A * alpha))), c.R, c.G, c.B);
  }

  // Soft text shadow (two offset passes) so white labels stay readable on any wallpaper.
  // Cheap variant for text on our own glass (already dimmed): a single soft drop.
  internal static void SoftText(Graphics g, string text, Font font, Color color, RectangleF bounds, StringFormat format, float alpha) {
   if (String.IsNullOrEmpty(text) || alpha <= 0.01f) return;
   using (var drop = new SolidBrush(Color.FromArgb((int)(90 * alpha), 0, 0, 0)))
   using (var ink = new SolidBrush(WithAlpha(color, alpha))) {
    var b = bounds; b.Offset(0, 1f); g.DrawString(text, font, drop, b, format);
    g.DrawString(text, font, ink, bounds, format);
   }
  }

  // A soft dark halo (ring of faint copies) plus a drop, like iOS home-screen labels.
  static readonly PointF[] Halo = { new PointF(-1, 0), new PointF(1, 0), new PointF(0, -1), new PointF(0, 1), new PointF(-0.7f, -0.7f), new PointF(0.7f, -0.7f), new PointF(-0.7f, 0.7f), new PointF(0.7f, 0.7f), new PointF(0, 2f), new PointF(-1.2f, 1.6f), new PointF(1.2f, 1.6f) };
  internal static void ShadowText(Graphics g, string text, Font font, Color color, RectangleF bounds, StringFormat format, float alpha) {
   if (String.IsNullOrEmpty(text) || alpha <= 0.01f) return;
   using (var halo = new SolidBrush(Color.FromArgb((int)(46 * alpha), 0, 0, 0)))
   using (var drop = new SolidBrush(Color.FromArgb((int)(120 * alpha), 0, 0, 0)))
   using (var ink = new SolidBrush(WithAlpha(color, alpha))) {
    foreach (var o in Halo) { var h = bounds; h.Offset(o.X * 1.4f, o.Y * 1.4f); g.DrawString(text, font, halo, h, format); }
    var b = bounds; b.Offset(0, 1f); g.DrawString(text, font, drop, b, format);
    g.DrawString(text, font, ink, bounds, format);
   }
  }

  internal static StringFormat Centered(bool ellipsis) {
   var f = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
   if (ellipsis) f.Trimming = StringTrimming.EllipsisCharacter;
   return f;
  }
  // Horizontally centred, top-aligned: every label shares one baseline whether it is Latin
  // or CJK (vertical centring shifts CJK lines by a few pixels in GDI+).
  internal static StringFormat TopCentered() {
   return new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoFontFallback) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter };
  }
  internal static StringFormat Leading() {
   return new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
  }

  // Anti-aliased shapes/text and good resampling, but plain (non gamma-corrected) blending:
  // CompositingQuality.HighQuality makes every GDI+ draw several times slower for no visible gain.
  internal static void PrepareQuality(Graphics g) {
   g.SmoothingMode = SmoothingMode.AntiAlias;
   g.InterpolationMode = InterpolationMode.HighQualityBicubic;
   g.PixelOffsetMode = PixelOffsetMode.Half;
   g.CompositingQuality = CompositingQuality.HighSpeed;
   g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
  }

  // 1:1 copy of a pre-rendered bitmap. Nearest-neighbour + exact source rect lets GDI+ take its
  // fast path instead of running the bicubic filter over pixels that do not move.
  internal static void Blit(Graphics g, Image image, int x, int y) {
   var im = g.InterpolationMode;
   var pm = g.PixelOffsetMode;
   g.InterpolationMode = InterpolationMode.NearestNeighbor;
   g.PixelOffsetMode = PixelOffsetMode.Half;
   g.DrawImage(image, new Rectangle(x, y, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
   g.InterpolationMode = im;
   g.PixelOffsetMode = pm;
  }

  // Draws a bitmap with a constant opacity (fast path when fully opaque).
  internal static void Blit(Graphics g, Image image, int x, int y, float alpha, System.Drawing.Imaging.ImageAttributes scratch, System.Drawing.Imaging.ColorMatrix matrix) {
   if (alpha >= 0.995f) { Blit(g, image, x, y); return; }
   if (alpha <= 0.005f) return;
   matrix.Matrix33 = alpha;
   scratch.SetColorMatrix(matrix);
   var im = g.InterpolationMode;
   g.InterpolationMode = InterpolationMode.NearestNeighbor;
   g.DrawImage(image, new Rectangle(x, y, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, scratch);
   g.InterpolationMode = im;
  }
 }

 // Damped harmonic spring, parameterised like SwiftUI (response seconds, damping fraction).
 // Integrated with fixed substeps so it behaves identically at 60, 120 or 240 Hz.
 internal sealed class Spring {
  public double Value, Velocity, Target;
  readonly double response, damping;
  public Spring(double value, double response, double damping) { Value = Target = value; this.response = response; this.damping = damping; }
  public bool Settled { get { return Math.Abs(Value - Target) < 0.0005 && Math.Abs(Velocity) < 0.005; } }
  public void Snap(double value) { Value = Target = value; Velocity = 0; }
  // Returns true while still moving.
  public bool Step(double dt) {
   if (Settled) { Value = Target; Velocity = 0; return false; }
   double omega = 2 * Math.PI / response, k = omega * omega, c = 2 * damping * omega;
   double remaining = Math.Min(dt, 0.1);
   while (remaining > 1e-6) {
    double h = Math.Min(remaining, 1.0 / 480.0);
    double accel = -k * (Value - Target) - c * Velocity;
    Velocity += accel * h;
    Value += Velocity * h;
    remaining -= h;
   }
   if (Settled) { Value = Target; Velocity = 0; return false; }
   return true;
  }
  public float F { get { return (float)Value; } }

  // Closed-form step response (0 -> 1) for time-based motions.
  public static double StepResponse(double seconds, double response, double damping) {
   if (seconds <= 0) return 0;
   double omega = 2 * Math.PI / response;
   if (damping >= 0.999) return 1 - (1 + omega * seconds) * Math.Exp(-omega * seconds);
   double wd = omega * Math.Sqrt(1 - damping * damping);
   double decay = Math.Exp(-damping * omega * seconds);
   return 1 - decay * (Math.Cos(wd * seconds) + (damping * omega / wd) * Math.Sin(wd * seconds));
  }
 }

 // Vsync-paced frame callbacks on the UI thread. DwmFlush returns once per composition,
 // so frames follow the real refresh rate (240 Hz here) instead of the ~15.6 ms timer grid.
 internal sealed class FramePump : IDisposable {
  [DllImport("dwmapi.dll")] static extern int DwmFlush();
  readonly Control owner;
  readonly Func<double, bool> tick;
  readonly Stopwatch clock = Stopwatch.StartNew();
  readonly MethodInvoker frame;
  int generation, pending;
  bool running;
  double last;

  public FramePump(Control owner, Func<double, bool> tick) {
   this.owner = owner;
   this.tick = tick;
   frame = OnFrame;
  }
  public bool Running { get { return running; } }

  public void Start() {
   if (running) return;
   if (owner.IsDisposed || !owner.IsHandleCreated) return;
   running = true;
   last = clock.Elapsed.TotalSeconds;
   int gen = ++generation;
   var t = new Thread(() => Loop(gen)) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "MacDesk frame pump" };
   t.Start();
  }
  public void Stop() { running = false; generation++; }

  void Loop(int gen) {
   try {
    while (gen == generation) {
     bool waited = false;
     try { waited = DwmFlush() == 0; } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
     if (!waited) Thread.Sleep(4);
     if (gen != generation) return;
     if (Interlocked.CompareExchange(ref pending, 1, 0) == 0) {
      try { owner.BeginInvoke(frame); } catch { pending = 0; return; }
     }
    }
   } catch { }
  }

  void OnFrame() {
   pending = 0;
   if (!running || owner.IsDisposed) return;
   double now = clock.Elapsed.TotalSeconds;
   double dt = Math.Max(0.0, Math.Min(0.05, now - last));
   last = now;
   bool more;
   try { more = tick(dt); } catch { Stop(); throw; }
   if (!more) Stop();
  }

  public void Dispose() { Stop(); }
 }

 // Persistent 32bpp premultiplied DIB for UpdateLayeredWindow: no per-frame allocation and
 // optional dirty-rect presentation (only the changed pixels are sent to DWM).
 internal sealed class LayeredSurface : IDisposable {
  IntPtr screen, memory, dib, previous, bits, scratch;
  Bitmap bitmap;
  Graphics graphics;
  Size size, capacity;

  public Size Size { get { return size; } }

  // Keeps a DIB at least this large, so a window that grows every frame (the folder morph)
  // never reallocates mid-animation.
  public void Reserve(Size wanted) {
   if (bitmap == null || wanted.Width > capacity.Width || wanted.Height > capacity.Height)
    Allocate(new Size(Math.Max(wanted.Width, capacity.Width), Math.Max(wanted.Height, capacity.Height)));
  }

  public Graphics Begin(Size wanted) {
   if (wanted.Width < 1 || wanted.Height < 1) wanted = new Size(Math.Max(1, wanted.Width), Math.Max(1, wanted.Height));
   if (bitmap == null || wanted.Width > capacity.Width || wanted.Height > capacity.Height)
    Allocate(new Size(Math.Max(wanted.Width, capacity.Width * 5 / 4), Math.Max(wanted.Height, capacity.Height * 5 / 4)));
   size = wanted;
   graphics.ResetTransform();
   graphics.SetClip(new Rectangle(Point.Empty, size));
   return graphics;
  }

  void Allocate(Size wanted) {
   Release();
   capacity = wanted;
   screen = GetDC(IntPtr.Zero);
   memory = CreateCompatibleDC(screen);
   if (screen == IntPtr.Zero || memory == IntPtr.Zero) throw new Win32Exception();
   var info = new INFO { Size = Marshal.SizeOf(typeof(INFO)), W = wanted.Width, H = -wanted.Height, Planes = 1, BitCount = 32 };
   dib = CreateDIBSection(screen, ref info, 0, out bits, IntPtr.Zero, 0);
   if (dib == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
   previous = SelectObject(memory, dib);
   bitmap = new Bitmap(wanted.Width, wanted.Height, wanted.Width * 4, PixelFormat.Format32bppPArgb, bits);
   graphics = Graphics.FromImage(bitmap);
   Ui.PrepareQuality(graphics);
   scratch = Marshal.AllocHGlobal(128);
  }

  // Clears a region to fully transparent (alpha 0) without touching anything else.
  public void Clear(Rectangle region) {
   var g = graphics;
   var old = g.CompositingMode;
   g.CompositingMode = CompositingMode.SourceCopy;
   using (var b = new SolidBrush(Color.FromArgb(0, 0, 0, 0))) g.FillRectangle(b, region);
   g.CompositingMode = old;
  }

  // position == null keeps the window where it is (required for WS_CHILD layered windows).
  public void Present(IntPtr hwnd, Point? position, byte alpha, Rectangle? dirty) {
   if (bitmap == null || hwnd == IntPtr.Zero) return;
   graphics.Flush(FlushIntention.Sync);
   // scratch layout: [0] POINT dst, [8] SIZE, [16] POINT src, [24] BLEND, [32] RECT dirty
   Marshal.WriteInt32(scratch, 0, position.HasValue ? position.Value.X : 0);
   Marshal.WriteInt32(scratch, 4, position.HasValue ? position.Value.Y : 0);
   Marshal.WriteInt32(scratch, 8, size.Width);
   Marshal.WriteInt32(scratch, 12, size.Height);
   Marshal.WriteInt32(scratch, 16, 0);
   Marshal.WriteInt32(scratch, 20, 0);
   Marshal.WriteInt32(scratch, 24, 0 | (0 << 8) | (alpha << 16) | (1 << 24));
   IntPtr dirtyPtr = IntPtr.Zero;
   if (dirty.HasValue) {
    Rectangle d = Rectangle.Intersect(dirty.Value, new Rectangle(Point.Empty, size));
    if (d.Width <= 0 || d.Height <= 0) return;
    Marshal.WriteInt32(scratch, 32, d.Left);
    Marshal.WriteInt32(scratch, 36, d.Top);
    Marshal.WriteInt32(scratch, 40, d.Right);
    Marshal.WriteInt32(scratch, 44, d.Bottom);
    dirtyPtr = new IntPtr(scratch.ToInt64() + 32);
   }
   var info = new ULWINFO {
    cbSize = Marshal.SizeOf(typeof(ULWINFO)),
    hdcDst = screen,
    pptDst = position.HasValue ? scratch : IntPtr.Zero,
    psize = new IntPtr(scratch.ToInt64() + 8),
    hdcSrc = memory,
    pptSrc = new IntPtr(scratch.ToInt64() + 16),
    crKey = 0,
    pblend = new IntPtr(scratch.ToInt64() + 24),
    dwFlags = 2,
    prcDirty = dirtyPtr
   };
   if (!UpdateLayeredWindowIndirect(hwnd, ref info)) {
    // Some hosts refuse partial updates; fall back to a full present once.
    if (dirtyPtr != IntPtr.Zero) { info.prcDirty = IntPtr.Zero; if (UpdateLayeredWindowIndirect(hwnd, ref info)) return; }
    throw new Win32Exception(Marshal.GetLastWin32Error());
   }
  }

  // Changes only the constant alpha of an already-presented layered window (very cheap).
  public static bool SetAlpha(IntPtr hwnd, double alpha) {
   if (hwnd == IntPtr.Zero) return false;
   var blend = new BLEND { Operation = 0, Flags = 0, Alpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(alpha * 255))), Format = 1 };
   return UpdateLayeredWindowAlpha(hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2);
  }

  void Release() {
   if (graphics != null) { graphics.Dispose(); graphics = null; }
   if (bitmap != null) { bitmap.Dispose(); bitmap = null; }
   if (dib != IntPtr.Zero) { SelectObject(memory, previous); DeleteObject(dib); dib = IntPtr.Zero; }
   if (memory != IntPtr.Zero) { DeleteDC(memory); memory = IntPtr.Zero; }
   if (screen != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, screen); screen = IntPtr.Zero; }
   if (scratch != IntPtr.Zero) { Marshal.FreeHGlobal(scratch); scratch = IntPtr.Zero; }
   size = capacity = Size.Empty;
  }
  public void Dispose() { Release(); }

  [StructLayout(LayoutKind.Sequential)] struct INFO { public int Size, W, H; public short Planes, BitCount; public int Compression, ImageSize, XPels, YPels, Used, Important; }
  [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Operation, Flags, Alpha, Format; }
  [StructLayout(LayoutKind.Sequential)] struct ULWINFO { public int cbSize; public IntPtr hdcDst, pptDst, psize, hdcSrc, pptSrc; public int crKey; public IntPtr pblend; public int dwFlags; public IntPtr prcDirty; }
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
  [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
  [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
  [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateDIBSection(IntPtr dc, ref INFO info, int usage, out IntPtr bits, IntPtr section, int offset);
  [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref ULWINFO info);
  [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)] static extern bool UpdateLayeredWindowAlpha(IntPtr hwnd, IntPtr dst, IntPtr pos, IntPtr size, IntPtr src, IntPtr srcPos, int key, ref BLEND blend, int flags);
 }

 // Frosted material source: a heavily blurred, slightly saturated copy of some screen area.
 internal sealed class Backdrop : IDisposable {
  public Bitmap Image { get; private set; }
  public Bitmap Dimmed { get; private set; }  // Image with the folder-backdrop dim baked in
  public Rectangle Area { get; private set; } // screen coordinates covered by Image

  Backdrop(Bitmap image, Rectangle area) { Image = image; Area = area; }

  public const int BackdropDim = 112;

  // Safe on any thread: pure GDI/GDI+ on objects owned by this call.
  public static Backdrop FromScreen(Rectangle area) {
   using (var shot = new Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height), PixelFormat.Format32bppRgb)) {
    using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
    var result = new Backdrop(Blur(shot, area.Size, 6, 1.35f), area);
    var dimmed = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
    using (var g = Graphics.FromImage(dimmed)) {
     g.CompositingMode = CompositingMode.SourceCopy;
     g.DrawImageUnscaled(result.Image, 0, 0);
     g.CompositingMode = CompositingMode.SourceOver;
     using (var b = new SolidBrush(Color.FromArgb(BackdropDim, 0, 0, 0))) g.FillRectangle(b, 0, 0, area.Width, area.Height);
    }
    result.Dimmed = dimmed;
    return result;
   }
  }

  public static Backdrop FromImage(Image source, Rectangle area) { return new Backdrop(Blur(source, area.Size, 8, 1.25f), area); }

  // Downscale -> 3-pass box blur -> saturate -> upscale. ~20 ms for a 1080p screen.
  static Bitmap Blur(Image source, Size full, int factor, float saturation) {
   int sw = Math.Max(4, full.Width / factor), sh = Math.Max(4, full.Height / factor);
   using (var small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb)) {
    using (var g = Graphics.FromImage(small)) {
     // Plain bilinear is ~5x cheaper than the high-quality prefilter; the heavy blur that
     // follows hides any aliasing from the decimation.
     g.InterpolationMode = InterpolationMode.Bilinear;
     g.PixelOffsetMode = PixelOffsetMode.Half;
     g.CompositingMode = CompositingMode.SourceCopy;
     g.DrawImage(source, new Rectangle(0, 0, sw, sh));
    }
    var data = small.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    int[] px = new int[sw * sh];
    Marshal.Copy(data.Scan0, px, 0, px.Length);
    int radius = Math.Max(2, 26 / factor + 2);
    for (int pass = 0; pass < 3; pass++) { BoxH(px, sw, sh, radius); BoxV(px, sw, sh, radius); }
    Saturate(px, saturation);
    Marshal.Copy(px, 0, data.Scan0, px.Length);
    small.UnlockBits(data);
    var result = new Bitmap(full.Width, full.Height, PixelFormat.Format32bppPArgb);
    using (var g = Graphics.FromImage(result))
    using (var attributes = new ImageAttributes()) {
     attributes.SetWrapMode(WrapMode.TileFlipXY);
     g.InterpolationMode = InterpolationMode.Bilinear;
     g.PixelOffsetMode = PixelOffsetMode.Half;
     g.CompositingMode = CompositingMode.SourceCopy;
     g.DrawImage(small, new Rectangle(0, 0, full.Width, full.Height), 0, 0, sw, sh, GraphicsUnit.Pixel, attributes);
    }
    return result;
   }
  }

  static void BoxH(int[] px, int w, int h, int r) {
   int[] row = new int[w];
   for (int y = 0; y < h; y++) {
    int o = y * w;
    Array.Copy(px, o, row, 0, w);
    int sr = 0, sg = 0, sb = 0, n = 2 * r + 1;
    for (int i = -r; i <= r; i++) { int c = row[Math.Max(0, Math.Min(w - 1, i))]; sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255; }
    for (int x = 0; x < w; x++) {
     px[o + x] = unchecked((int)0xFF000000) | ((sr / n) << 16) | ((sg / n) << 8) | (sb / n);
     int add = row[Math.Min(w - 1, x + r + 1)], sub = row[Math.Max(0, x - r)];
     sr += ((add >> 16) & 255) - ((sub >> 16) & 255); sg += ((add >> 8) & 255) - ((sub >> 8) & 255); sb += (add & 255) - (sub & 255);
    }
   }
  }
  static void BoxV(int[] px, int w, int h, int r) {
   int[] col = new int[h];
   for (int x = 0; x < w; x++) {
    for (int y = 0; y < h; y++) col[y] = px[y * w + x];
    int sr = 0, sg = 0, sb = 0, n = 2 * r + 1;
    for (int i = -r; i <= r; i++) { int c = col[Math.Max(0, Math.Min(h - 1, i))]; sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255; }
    for (int y = 0; y < h; y++) {
     px[y * w + x] = unchecked((int)0xFF000000) | ((sr / n) << 16) | ((sg / n) << 8) | (sb / n);
     int add = col[Math.Min(h - 1, y + r + 1)], sub = col[Math.Max(0, y - r)];
     sr += ((add >> 16) & 255) - ((sub >> 16) & 255); sg += ((add >> 8) & 255) - ((sub >> 8) & 255); sb += (add & 255) - (sub & 255);
    }
   }
  }
  static void Saturate(int[] px, float s) {
   for (int i = 0; i < px.Length; i++) {
    int c = px[i];
    float r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
    float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
    int nr = Clamp(l + (r - l) * s), ng = Clamp(l + (g - l) * s), nb = Clamp(l + (b - l) * s);
    px[i] = unchecked((int)0xFF000000) | (nr << 16) | (ng << 8) | nb;
   }
  }
  static int Clamp(float v) { return v < 0 ? 0 : v > 255 ? 255 : (int)v; }

  // Brush that paints this backdrop aligned to the screen, for a surface whose top-left is at surfaceOrigin (screen coords).
  public TextureBrush CreateBrush(Point surfaceOrigin) {
   var brush = new TextureBrush(Image, WrapMode.Clamp);
   brush.TranslateTransform(Area.X - surfaceOrigin.X, Area.Y - surfaceOrigin.Y);
   return brush;
  }

  public void Dispose() {
   if (Image != null) { Image.Dispose(); Image = null; }
   if (Dimmed != null) { Dimmed.Dispose(); Dimmed = null; }
  }
 }

 // The desktop wallpaper as it is laid out on the primary screen, pre-blurred, so desktop
 // widgets and folder icons can be real frosted glass (we cannot sample pixels behind a
 // WS_CHILD of Explorer's desktop view, but we can reproduce exactly what is there).
 internal static class Wallpaper {
  static Backdrop current;
  static string signature;
  static DateTime lastCheck = DateTime.MinValue;
  public static event EventHandler Changed;

  public static Backdrop Current {
   get {
    if (current == null || (DateTime.UtcNow - lastCheck).TotalSeconds > 3) Refresh(false);
    return current;
   }
  }

  public static void Refresh(bool force) {
   lastCheck = DateTime.UtcNow;
   string path; int style; bool tile; Color background;
   ReadSettings(out path, out style, out tile, out background);
   Rectangle screen = Screen.PrimaryScreen.Bounds;
   string sig = path + "|" + style + "|" + tile + "|" + background.ToArgb() + "|" + screen;
   try { if (path != null && File.Exists(path)) sig += "|" + File.GetLastWriteTimeUtc(path).Ticks; } catch { }
   if (!force && current != null && sig == signature) return;
   Backdrop next = null;
   try {
    using (var composed = Compose(path, style, tile, background, screen.Size)) next = Backdrop.FromImage(composed, screen);
   } catch { next = null; }
   if (next == null) return;
   Backdrop old = current;
   current = next;
   signature = sig;
   if (old != null) old.Dispose();
   var handler = Changed;
   if (handler != null) handler(null, EventArgs.Empty);
  }

  static void ReadSettings(out string path, out int style, out bool tile, out Color background) {
   path = null; style = 10; tile = false; background = Color.Black;
   try {
    using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop")) {
     if (key != null) {
      path = key.GetValue("WallPaper") as string;
      int.TryParse(Convert.ToString(key.GetValue("WallpaperStyle")), out style);
      tile = Convert.ToString(key.GetValue("TileWallpaper")) == "1";
     }
    }
    using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors")) {
     string bg = key == null ? null : key.GetValue("Background") as string;
     if (bg != null) { var parts = bg.Split(' '); if (parts.Length == 3) background = Color.FromArgb(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2])); }
    }
   } catch { }
   if (String.IsNullOrEmpty(path) || !File.Exists(path)) {
    string transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
    path = File.Exists(transcoded) ? transcoded : null;
   }
  }

  static Bitmap Compose(string path, int style, bool tile, Color background, Size screen) {
   var result = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppRgb);
   using (var g = Graphics.FromImage(result)) {
    g.Clear(background);
    if (path == null) return result;
    Image image;
    using (var stream = new MemoryStream(File.ReadAllBytes(path))) image = Image.FromStream(stream);
    using (image) {
     g.InterpolationMode = InterpolationMode.HighQualityBilinear;
     float iw = image.Width, ih = image.Height, sw = screen.Width, sh = screen.Height;
     if (tile && style == 0) {
      using (var brush = new TextureBrush(image)) g.FillRectangle(brush, 0, 0, sw, sh);
     } else if (style == 2) {
      g.DrawImage(image, 0, 0, sw, sh);
     } else if (style == 6 || style == 0) {
      float scale = style == 6 ? Math.Min(sw / iw, sh / ih) : 1f;
      float w = iw * scale, h = ih * scale;
      g.DrawImage(image, (sw - w) / 2, (sh - h) / 2, w, h);
     } else {
      float scale = Math.Max(sw / iw, sh / ih); // Fill / Span
      float w = iw * scale, h = ih * scale;
      g.DrawImage(image, (sw - w) / 2, (sh - h) / 2, w, h);
     }
    }
   }
   return result;
  }
 }

 // Frosted glass painting shared by every surface, so the folder morph, the open folder,
 // and the desktop widgets are literally the same material.
 internal static class Material {
  // Open folder sheet: lighter than the dimmed backdrop around it.
  public static readonly Color SheetTint = Color.FromArgb(64, 34, 34, 40);
  public static readonly Color SheetFallback = Color.FromArgb(225, 58, 60, 66);
  // Desktop folder icon: light frosted glass.
  public static readonly Color FolderTint = Color.FromArgb(64, 255, 255, 255);
  public static readonly Color FolderFallback = Color.FromArgb(150, 120, 126, 136);
  // Widgets: darker glass so white text stays readable on bright skies.
  public static readonly Color WidgetTint = Color.FromArgb(138, 16, 18, 24);
  public static readonly Color WidgetFallback = Color.FromArgb(215, 28, 30, 36);

  // tint: overlay color drawn on top of the blurred backdrop (light for folders, dark for widgets).
  public static void Fill(Graphics g, GraphicsPath shape, TextureBrush backdrop, Color tint, Color fallback) {
   if (backdrop != null) g.FillPath(backdrop, shape);
   else using (var b = new SolidBrush(fallback)) g.FillPath(b, shape);
   if (tint.A > 0) using (var b = new SolidBrush(tint)) g.FillPath(b, shape);
  }

  // Hairline rim: brighter at the top like light catching the edge of glass.
  public static void Rim(Graphics g, RectangleF r, float radius, float strength) {
   if (strength <= 0.01f) return;
   var inner = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
   using (var path = Ui.Squircle(inner, Math.Max(1, radius - 0.5f)))
   using (var brush = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb((int)(120 * strength), 255, 255, 255), Color.FromArgb((int)(28 * strength), 255, 255, 255), LinearGradientMode.Vertical))
   using (var pen = new Pen(brush, 1f))
    g.DrawPath(pen, path);
  }
 }
}
