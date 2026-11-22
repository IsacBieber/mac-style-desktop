using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MacDeskNative {
 // Screen-aligned, opaque glass pixels. Each row is one block copy except for
 // the antialiased outline, where the moving squircle only partly covers pixels.
 internal sealed class FolderGlass {
  private const int CurveSamples = 1024;
  private static readonly float[] InsetByHeight = BuildCornerCurve();
  private readonly int[] pixels;
  internal readonly Rectangle Area;

  internal FolderGlass(Backdrop backdrop, Rectangle area) {
   Area = area;
   using (var bitmap = backdrop.CopyRegion(area)) {
    using (var g = Graphics.FromImage(bitmap))
    using (var tint = new SolidBrush(Material.SheetTint))
     g.FillRectangle(tint, 0, 0, area.Width, area.Height);
    pixels = new int[area.Width * area.Height];
    var source = bitmap.LockBits(new Rectangle(Point.Empty, area.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
    try {
     for (int y = 0; y < area.Height; y++)
      Marshal.Copy(IntPtr.Add(source.Scan0, y * source.Stride), pixels, y * area.Width, area.Width);
    } finally { bitmap.UnlockBits(source); }
   }
  }

  // Sample the same unit Bézier corner as Ui.Squircle, indexed by vertical distance
  // from the top or bottom edge. This inverse lookup is shared by all frame sizes.
  private static float[] BuildCornerCurve() {
   const float c = Ui.SquircleControlRatio;
   var curve = new float[CurveSamples + 1];
   for (int i = 0; i <= CurveSamples; i++) {
    float height = (float)i / CurveSamples;
    float low = 0, high = 1;
    for (int step = 0; step < 16; step++) {
     float t = (low + high) * 0.5f, u = 1 - t;
     float y = t * t * (3 * u * c + t);
     if (y < height) low = t; else high = t;
    }
    float time = (low + high) * 0.5f, rest = 1 - time;
    curve[i] = rest * rest * (rest + 3 * time * c);
   }
   curve[0] = 1;
   curve[CurveSamples] = 0;
   return curve;
  }

  private static float Inset(RectangleF shape, float corner, float y) {
   float height = Math.Min(y - shape.Top, shape.Bottom - y);
   if (height <= 0) return shape.Width * 0.5f;
   if (height >= corner) return 0;
   float at = height * CurveSamples / corner;
   int index = Math.Min((int)at, CurveSamples - 1);
   return corner * (InsetByHeight[index] + (at - index) * (InsetByHeight[index + 1] - InsetByHeight[index]));
  }

  internal bool Paint(Bitmap bitmap, Rectangle view, RectangleF shape, float radius) {
   var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
   try { return Paint(data.Scan0, data.Stride, view, shape, radius); }
   finally { bitmap.UnlockBits(data); }
  }

  internal bool Paint(IntPtr target, int stride, Rectangle view, RectangleF shape, float radius) {
   var bounds = Rectangle.Intersect(view, Rectangle.FromLTRB((int)Math.Floor(shape.Left), (int)Math.Floor(shape.Top),
    (int)Math.Ceiling(shape.Right), (int)Math.Ceiling(shape.Bottom)));
   if (bounds.Width <= 0 || bounds.Height <= 0) return true;
   if (!Area.Contains(bounds)) return false;
   float corner = Ui.SquircleCorner(shape, radius);
   for (int y = bounds.Top; y < bounds.Bottom; y++) {
    float a = Inset(shape, corner, y + 0.125f), b = Inset(shape, corner, y + 0.375f);
    float c = Inset(shape, corner, y + 0.625f), d = Inset(shape, corner, y + 0.875f);
    float inner = Math.Max(Math.Max(a, b), Math.Max(c, d));
    float outer = Math.Min(Math.Min(a, b), Math.Min(c, d));
    int left = Math.Max(bounds.Left, (int)Math.Floor(shape.Left + outer));
    int right = Math.Min(bounds.Right, (int)Math.Ceiling(shape.Right - outer));
    if (left >= right) continue;
    int firstOpaque = Math.Min(right, Math.Max(left, (int)Math.Ceiling(shape.Left + inner)));
    int endOpaque = Math.Max(firstOpaque, Math.Min(right, (int)Math.Floor(shape.Right - inner)));
    int sourceRow = (y - Area.Top) * Area.Width - Area.Left;
    IntPtr destRow = IntPtr.Add(target, (y - view.Top) * stride - view.Left * 4);
    if (firstOpaque < endOpaque)
     Marshal.Copy(pixels, sourceRow + firstOpaque, IntPtr.Add(destRow, firstOpaque * 4), endOpaque - firstOpaque);
    for (int x = left; x < firstOpaque; x++) PaintEdge(destRow, sourceRow, shape, x, a, b, c, d);
    for (int x = endOpaque; x < right; x++) PaintEdge(destRow, sourceRow, shape, x, a, b, c, d);
   }
   return true;
  }

  private void PaintEdge(IntPtr destRow, int sourceRow, RectangleF shape, int x, float a, float b, float c, float d) {
   float coverage = Coverage(shape, x, a) + Coverage(shape, x, b) + Coverage(shape, x, c) + Coverage(shape, x, d);
   int alpha = (int)(coverage * 63.75f + 0.5f);
   if (alpha == 0) return;
   int color = pixels[sourceRow + x];
   int r = (((color >> 16) & 255) * alpha + 127) / 255;
   int g = (((color >> 8) & 255) * alpha + 127) / 255;
   int bChannel = ((color & 255) * alpha + 127) / 255;
   Marshal.WriteInt32(destRow, x * 4, (alpha << 24) | (r << 16) | (g << 8) | bChannel);
  }

  private static float Coverage(RectangleF shape, int x, float inset) {
   return Math.Max(0, Math.Min(x + 1, shape.Right - inset) - Math.Max(x, shape.Left + inset));
  }
 }
}
