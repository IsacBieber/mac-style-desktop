using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace MacDeskNative {
 // Pre-rendered single-line labels. TextStyle (filled glyph outlines) is crisp on our layered
 // surfaces but builds a path per call; animated lists draw the same strings every frame, so
 // each (text, font, color, width) is rendered once and then only blitted.
 internal sealed class TextSprites : IDisposable {
  readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
  readonly ImageAttributes attributes = new ImageAttributes();
  readonly ColorMatrix matrix = new ColorMatrix();
  const int Capacity = 256;

  /// A bitmap of `text` (ellipsis beyond maxWidth), `Height` = line box; draw with Ui.Blit at
  /// (x, centerY - Height / 2).
  public Bitmap Get(string text, string family, FontStyle style, float px, Color color, float weight, int maxWidth) {
   if (String.IsNullOrEmpty(text) || maxWidth < 4) return null;
   string key = family + "|" + (int)style + "|" + px + "|" + color.ToArgb() + "|" + weight + "|" + maxWidth + "|" + text;
   Bitmap bmp;
   if (cache.TryGetValue(key, out bmp)) return bmp;
   if (cache.Count >= Capacity) Clear();
   int h = (int)Math.Ceiling(px * 1.55f) + 2;
   int w = Math.Min(maxWidth, (int)Math.Ceiling(TextStyle.Width(text, family, style, px)) + 6);
   bmp = new Bitmap(Math.Max(1, w + 2), h, PixelFormat.Format32bppPArgb);
   using (var g = Graphics.FromImage(bmp))
   using (var f = Typographic()) {
    g.Clear(Color.Transparent);
    Ui.PrepareQuality(g);
    TextStyle.Draw(g, text, family, style, px, color, new RectangleF(1, 0, w, h), f, weight);
   }
   cache[key] = bmp;
   return bmp;
  }

  // Measured and laid out the same way (no GDI+ side padding), so a label is never cut short.
  static StringFormat Typographic() {
   var f = (StringFormat)StringFormat.GenericTypographic.Clone();
   f.FormatFlags |= StringFormatFlags.NoWrap;
   f.Trimming = StringTrimming.EllipsisCharacter;
   f.LineAlignment = StringAlignment.Center;
   return f;
  }

  /// Draws `text` left-aligned at x, vertically centred on centerY; returns the drawn width.
  public int Draw(Graphics g, string text, string family, FontStyle style, float px, Color color, float weight, float x, float centerY, int maxWidth) {
   return Draw(g, text, family, style, px, color, weight, x, centerY, maxWidth, 1f);
  }

  /// Same, faded by `alpha` (the sprite itself is shared across alphas).
  public int Draw(Graphics g, string text, string family, FontStyle style, float px, Color color, float weight, float x, float centerY, int maxWidth, float alpha) {
   Bitmap bmp = Get(text, family, style, px, color, weight, maxWidth);
   if (bmp == null) return 0;
   Ui.Blit(g, bmp, (int)Math.Round(x) - 1, (int)Math.Round(centerY - bmp.Height / 2f), alpha, attributes, matrix);
   return bmp.Width - 2;
  }

  public void Clear() {
   foreach (var b in cache.Values) b.Dispose();
   cache.Clear();
  }

  public void Dispose() { Clear(); attributes.Dispose(); }
 }
}
