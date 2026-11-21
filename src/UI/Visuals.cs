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

  // Folder icons use the same pixel-aligned bitmap draw from preview size through panel size.
  internal static void DrawIcon(Graphics g, Image image, Rectangle dest, float alpha, System.Drawing.Imaging.ImageAttributes scratch, System.Drawing.Imaging.ColorMatrix matrix) {
   if (image == null || alpha <= 0.005f) return;
   if (dest.Size == image.Size) { Blit(g, image, dest.X, dest.Y, alpha, scratch, matrix); return; }
   matrix.Matrix33 = alpha;
   scratch.SetColorMatrix(matrix);
   g.DrawImage(image, dest, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, scratch);
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

 // Persistent 32bpp premultiplied DIB for UpdateLayeredWindow: no per-frame allocation and
 // optional dirty-rect presentation (only the changed pixels are sent to DWM).
 internal sealed class LayeredSurface : IDisposable {
  IntPtr screen, memory, dib, previous, bits, scratch;
  Bitmap bitmap;
  Graphics graphics;
  Size size, capacity;

  public Size Size { get { return size; } }

  // Diagnostics: save what is currently presented (premultiplied) as a PNG.
  public void SaveSnapshot(string path) {
   if (bitmap == null) return;
   graphics.Flush(FlushIntention.Sync);
   using (var copy = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
   using (var g = Graphics.FromImage(copy)) {
    g.Clear(Color.FromArgb(255, 40, 44, 52)); // neutral backdrop so translucency is visible
    g.DrawImage(bitmap, new Rectangle(0, 0, size.Width, size.Height), new Rectangle(0, 0, size.Width, size.Height), GraphicsUnit.Pixel);
    copy.Save(path, ImageFormat.Png);
   }
  }

  // Alpha of a presented pixel (diagnostics: the OS hit-tests layered windows by alpha).
  public int AlphaAt(int x, int y) {
   if (bitmap == null || x < 0 || y < 0 || x >= size.Width || y >= size.Height) return 0;
   graphics.Flush(FlushIntention.Sync);
   return (Marshal.ReadInt32(bits, (y * capacity.Width + x) * 4) >> 24) & 255;
  }

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

  // Capture before the folder window appears, then process these pixels on a worker.
  public static Bitmap CaptureScreen(Rectangle area) {
   var shot = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
   try {
    using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
    return shot;
   } catch { shot.Dispose(); throw; }
  }

  public static Backdrop FromCapture(Image source, Rectangle area) {
   Bitmap small = BlurSmall(source, area.Size, 6, 1.35f);
   try {
    using (var dark = small.Clone(new Rectangle(Point.Empty, small.Size), PixelFormat.Format32bppPArgb)) {
     using (var g = Graphics.FromImage(dark))
     using (var b = new SolidBrush(Color.FromArgb(BackdropDim, 0, 0, 0))) g.FillRectangle(b, 0, 0, dark.Width, dark.Height);
     return new Backdrop(small, area) { Dimmed = ExpandBlur(dark, area.Size, new Rectangle(Point.Empty, area.Size)) };
    }
   } catch { small.Dispose(); throw; }
  }

  public static Backdrop FromImage(Image source, Rectangle area) {
   return new Backdrop(BlurSmall(source, area.Size, 8, 1.25f), area);
  }

  public Bitmap TakeDimmed() { Bitmap image = Dimmed; Dimmed = null; return image; }

  public Bitmap CopyRegion(Rectangle region) {
   region.Offset(-Area.X, -Area.Y);
   return ExpandBlur(Image, Area.Size, region);
  }

  // Downscale -> three separable box passes -> saturate. Only the pixels needed for display are expanded.
  static Bitmap BlurSmall(Image source, Size full, int factor, float saturation) {
   int sw = Math.Max(4, full.Width / factor), sh = Math.Max(4, full.Height / factor);
   var small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
   try {
    using (var g = Graphics.FromImage(small)) {
     // Plain bilinear is ~5x cheaper than the high-quality prefilter; the heavy blur that
     // follows hides any aliasing from the decimation.
     g.InterpolationMode = InterpolationMode.Bilinear;
     g.PixelOffsetMode = PixelOffsetMode.Half;
     g.CompositingMode = CompositingMode.SourceCopy;
     g.DrawImage(source, new Rectangle(0, 0, sw, sh));
    }
    var data = small.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    try {
     int[] px = new int[sw * sh];
     Marshal.Copy(data.Scan0, px, 0, px.Length);
     int radius = Math.Max(2, 26 / factor + 2);
     for (int pass = 0; pass < 3; pass++) { BoxH(px, sw, sh, radius); BoxV(px, sw, sh, radius); }
     Saturate(px, saturation);
     Marshal.Copy(px, 0, data.Scan0, px.Length);
    } finally { small.UnlockBits(data); }
    return small;
   } catch { small.Dispose(); throw; }
  }

  // Bilinear sampling of opaque blurred pixels. Each source row is expanded at most once,
  // then reused for every output row that interpolates between that pair.
  static Bitmap ExpandBlur(Bitmap small, Size full, Rectangle region) {
   var pixels = new int[small.Width * small.Height];
   var source = small.LockBits(new Rectangle(Point.Empty, small.Size), ImageLockMode.ReadOnly, small.PixelFormat);
   try {
    for (int y = 0; y < small.Height; y++)
     Marshal.Copy(IntPtr.Add(source.Scan0, y * source.Stride), pixels, y * small.Width, small.Width);
   } finally { small.UnlockBits(source); }

   int[] columns = new int[region.Width];
   for (int x = 0; x < columns.Length; x++) columns[x] = SamplePosition(region.X + x, small.Width, full.Width);
   int[] upper = new int[region.Width], lower = new int[region.Width], row = new int[region.Width];
   int upperIndex = -1, lowerIndex = -1;
   var result = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);
   try {
    var output = result.LockBits(new Rectangle(Point.Empty, result.Size), ImageLockMode.WriteOnly, result.PixelFormat);
    try {
     for (int y = 0; y < region.Height; y++) {
      int sample = SamplePosition(region.Y + y, small.Height, full.Height);
      int y0 = sample >> 8, y1 = Math.Min(small.Height - 1, y0 + 1);
      if (upperIndex != y0) {
       if (lowerIndex == y0) {
        int[] previousUpper = upper; upper = lower; lower = previousUpper;
        lowerIndex = upperIndex;
       } else ExpandRow(pixels, small.Width, y0, columns, upper);
       upperIndex = y0;
      }
      if ((sample & 255) == 0) {
       Marshal.Copy(upper, 0, IntPtr.Add(output.Scan0, y * output.Stride), region.Width);
       continue;
      }
      if (lowerIndex != y1) {
       ExpandRow(pixels, small.Width, y1, columns, lower);
       lowerIndex = y1;
      }
      uint fraction = (uint)(sample & 255);
      for (int x = 0; x < row.Length; x++) row[x] = MixOpaque(upper[x], lower[x], fraction);
      Marshal.Copy(row, 0, IntPtr.Add(output.Scan0, y * output.Stride), region.Width);
     }
    } finally { result.UnlockBits(output); }
    return result;
   } catch { result.Dispose(); throw; }
  }

  static int SamplePosition(int pixel, int sourceLength, int destinationLength) {
   int sample = (int)(((2L * pixel + 1) * sourceLength * 128) / destinationLength) - 128;
   return Math.Max(0, Math.Min((sourceLength - 1) * 256, sample));
  }

  static void ExpandRow(int[] source, int width, int y, int[] columns, int[] destination) {
   int offset = y * width;
   for (int x = 0; x < columns.Length; x++) {
    int column = columns[x], x0 = column >> 8;
    destination[x] = MixOpaque(source[offset + x0], source[offset + Math.Min(width - 1, x0 + 1)], (uint)(column & 255));
   }
  }

  static int MixOpaque(int a, int b, uint fraction) {
   const uint rb = 0x00ff00ff, green = 0x0000ff00;
   uint left = unchecked((uint)a), right = unchecked((uint)b), inverse = 256 - fraction;
   uint redBlue = (((left & rb) * inverse + (right & rb) * fraction + 0x00800080u) >> 8) & rb;
   uint g = (((left & green) * inverse + (right & green) * fraction + 0x00008000u) >> 8) & green;
   return unchecked((int)(0xff000000u | redBlue | g));
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

  // Expand just the requested rectangle while preserving its alignment to the full image.
  public TextureBrush CreateBrush(Rectangle region, Point surfaceOrigin) {
   region.Intersect(Area);
   if (region.Width <= 0 || region.Height <= 0) return null;
   using (var pixels = CopyRegion(region)) {
    var brush = new TextureBrush(pixels, WrapMode.Clamp);
    brush.TranslateTransform(region.X - surfaceOrigin.X, region.Y - surfaceOrigin.Y);
    return brush;
   }
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
    RefreshIfNeeded();
    return current;
   }
  }

  public static void RefreshIfNeeded() {
   if (current == null || (DateTime.UtcNow - lastCheck).TotalSeconds > 3) Refresh(false);
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

namespace MacDeskNative {
 // Per-frame timing on the real desktop, enabled by MACDESK_FRAME_LOG=<file>.
 // Columns: ms since start, surface, dt between frames (ms), render+present work (ms), detail.
 internal static class FrameLog {
  static readonly string path = Environment.GetEnvironmentVariable("MACDESK_FRAME_LOG");
  static readonly System.Text.StringBuilder buffer = new System.Text.StringBuilder();
  static readonly Stopwatch clock = Stopwatch.StartNew();
  static double lastFlush;
  public static bool On { get { return !String.IsNullOrEmpty(path); } }
  public static double Now { get { return clock.Elapsed.TotalMilliseconds; } }
  public static void Add(string surface, double dtMs, double workMs, string detail) {
   if (!On) return;
   buffer.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0}\t{1}\t{2:0.00}\t{3:0.00}\t{4}\r\n", Now, surface, dtMs, workMs, detail);
   if (Now - lastFlush > 1000) Flush();
  }
  public static void Flush() {
   if (!On || buffer.Length == 0) return;
   try { File.AppendAllText(path, buffer.ToString()); } catch { }
   buffer.Length = 0;
   lastFlush = Now;
  }
 }
}

namespace MacDeskNative {
 // Text drawn as filled glyph outlines. GDI+ DrawString on a transparent layered surface renders
 // thin and soft; outlines are crisp and let us add a little weight (a hairline stroke around
 // the glyphs), which is how the labels get a "medium" weight Windows' CJK fonts don't ship.
 internal static class TextStyle {
  static readonly System.Collections.Generic.Dictionary<string, FontFamily> families = new System.Collections.Generic.Dictionary<string, FontFamily>();
  internal static FontFamily Family(string name) {
   FontFamily f;
   if (families.TryGetValue(name, out f)) return f;
   try { f = new FontFamily(name); } catch (ArgumentException) { f = new FontFamily("Microsoft YaHei UI"); }
   families[name] = f;
   return f;
  }

  internal static float Width(string text, string family, FontStyle style, float px) {
   if (String.IsNullOrEmpty(text)) return 0;
   using (var path = new GraphicsPath()) {
    path.AddString(text, Family(family), (int)style, px, PointF.Empty, StringFormat.GenericTypographic);
    return path.GetBounds().Width;
   }
  }

  static GraphicsPath Build(string text, string family, FontStyle style, float px, RectangleF rect, StringFormat format) {
   var path = new GraphicsPath();
   path.AddString(text, Family(family), (int)style, px, rect, format);
   return path;
  }

  // weight: extra stroke width in px (0 = the font's own weight).
  internal static void Draw(Graphics g, string text, string family, FontStyle style, float px, Color color, RectangleF rect, StringFormat format, float weight) {
   DrawShadowed(g, text, family, style, px, color, rect, format, weight, 0f);
  }

  // shadow: 0..1 strength of a soft dark halo + 1 px drop (for text over wallpaper / glass).
  internal static void DrawShadowed(Graphics g, string text, string family, FontStyle style, float px, Color color, RectangleF rect, StringFormat format, float weight, float shadow) {
   if (String.IsNullOrEmpty(text) || color.A == 0) return;
   using (var path = Build(text, family, style, px, rect, format)) {
    if (color.A == 255) { Paint(g, path, color, weight, shadow); return; }
    // Translucent ink: render opaque into a layer, then composite once with the alpha, so the
    // weight stroke and the fill do not double up into darker seams.
    RectangleF b = path.GetBounds();
    b.Inflate(weight + 4, weight + 4);
    if (b.Width < 1 || b.Height < 1) return;
    using (var layer = new Bitmap((int)Math.Ceiling(b.Width), (int)Math.Ceiling(b.Height), PixelFormat.Format32bppPArgb)) {
     using (var lg = Graphics.FromImage(layer)) {
      lg.Clear(Color.Transparent);
      lg.SmoothingMode = SmoothingMode.AntiAlias;
      lg.PixelOffsetMode = PixelOffsetMode.Half;
      lg.TranslateTransform(-b.X, -b.Y);
      Paint(lg, path, Color.FromArgb(255, color), weight, shadow);
     }
     using (var ia = new ImageAttributes()) {
      ia.SetColorMatrix(new ColorMatrix { Matrix33 = color.A / 255f });
      g.DrawImage(layer, new Rectangle((int)Math.Round(b.X), (int)Math.Round(b.Y), layer.Width, layer.Height), 0, 0, layer.Width, layer.Height, GraphicsUnit.Pixel, ia);
     }
    }
   }
  }

  static void Paint(Graphics g, GraphicsPath path, Color color, float weight, float shadow) {
   var smoothing = g.SmoothingMode;
   g.SmoothingMode = SmoothingMode.AntiAlias;
   if (shadow > 0.01f) {
    using (var halo = new Pen(Color.FromArgb((int)(60 * shadow), 0, 0, 0), 3.4f + weight) { LineJoin = LineJoin.Round })
     g.DrawPath(halo, path);
    using (var drop = (GraphicsPath)path.Clone())
    using (var m = new System.Drawing.Drawing2D.Matrix()) {
     m.Translate(0, 1.1f);
     drop.Transform(m);
     using (var b = new SolidBrush(Color.FromArgb((int)(110 * shadow), 0, 0, 0))) g.FillPath(b, drop);
    }
   }
   using (var ink = new SolidBrush(color)) g.FillPath(ink, path);
   if (weight > 0.01f) using (var pen = new Pen(color, weight) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
   g.SmoothingMode = smoothing;
  }
 }
}
