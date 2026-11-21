using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace MacDesk.Core {
 // Icons for entries: Shell thumbnails (128 px, alpha preserved), cached per target.
 public static class IconProvider {
  static readonly Dictionary<string, Image> cache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
  static readonly object gate = new object();
  [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }
  [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap); }
  [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
  static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
  static Image ShellIcon(string path) {
   IShellItemImageFactory factory = null; IntPtr h = IntPtr.Zero;
   try {
    Guid id = typeof(IShellItemImageFactory).GUID;
    if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref id, out factory) != 0 || factory == null) return null;
    if (factory.GetImage(new SIZE(128, 128), 1 | 4, out h) != 0 || h == IntPtr.Zero) return null;
    return FromHBitmapWithAlpha(h);
   } catch { return null; } finally { if (h != IntPtr.Zero) DeleteObject(h); if (factory != null && Marshal.IsComObject(factory)) Marshal.ReleaseComObject(factory); }
  }

  // Image.FromHbitmap drops the alpha channel, which turns every transparent pixel of a shell
  // icon black (the "black box" around icons). Copy the 32-bit DIB ourselves instead.
  [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO_HDR { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
  [StructLayout(LayoutKind.Sequential)] struct BITMAPDATA { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
  [StructLayout(LayoutKind.Sequential)] struct DIBSECTION { public BITMAPDATA dsBm; public BITMAPINFO_HDR dsBmih; public int dsBitfields0, dsBitfields1, dsBitfields2; public IntPtr dshSection; public int dsOffset; }
  [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] static extern int GetObject(IntPtr h, int size, ref DIBSECTION ds);

  static Bitmap FromHBitmapWithAlpha(IntPtr h) {
   var ds = new DIBSECTION();
   int got = GetObject(h, Marshal.SizeOf(typeof(DIBSECTION)), ref ds);
   if (got == 0 || ds.dsBm.bmBitsPixel != 32 || ds.dsBm.bmBits == IntPtr.Zero) {
    using (var source = Image.FromHbitmap(h)) return new Bitmap(source);
   }
   int w = ds.dsBm.bmWidth, ht = Math.Abs(ds.dsBm.bmHeight), stride = ds.dsBm.bmWidthBytes;
   bool bottomUp = ds.dsBmih.biHeight > 0;
   var px = new int[w * ht];
   var row = new int[w];
   for (int y = 0; y < ht; y++) {
    int srcRow = bottomUp ? ht - 1 - y : y;
    Marshal.Copy(new IntPtr(ds.dsBm.bmBits.ToInt64() + (long)srcRow * stride), row, 0, w);
    Array.Copy(row, 0, px, y * w, w);
   }
   // Decide how to interpret alpha: none at all -> opaque; any colour above its alpha ->
   // straight alpha; otherwise premultiplied.
   bool anyAlpha = false, straight = false;
   for (int i = 0; i < px.Length; i++) {
    int c = px[i], a = (c >> 24) & 255;
    if (a != 0) anyAlpha = true;
    if (((c >> 16) & 255) > a || ((c >> 8) & 255) > a || (c & 255) > a) straight = true;
   }
   if (!anyAlpha) for (int i = 0; i < px.Length; i++) px[i] |= unchecked((int)0xFF000000);
   var format = !anyAlpha ? System.Drawing.Imaging.PixelFormat.Format32bppArgb : straight ? System.Drawing.Imaging.PixelFormat.Format32bppArgb : System.Drawing.Imaging.PixelFormat.Format32bppPArgb;
   var result = new Bitmap(w, ht, format);
   var data = result.LockBits(new Rectangle(0, 0, w, ht), System.Drawing.Imaging.ImageLockMode.WriteOnly, format);
   try { for (int y = 0; y < ht; y++) Marshal.Copy(px, y * w, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), w); }
   finally { result.UnlockBits(data); }
   return result;
  }
  public static Image Get(DeskItem item) {
   if (item == null) throw new ArgumentNullException("item");
   string custom = item.icon ?? "", target = item.target ?? "";
   string key = item.type + "|" + target + "|" + custom;
   try {
    if (File.Exists(custom)) key += "|" + File.GetLastWriteTimeUtc(custom).Ticks;
    if (File.Exists(target)) key += "|" + File.GetLastWriteTimeUtc(target).Ticks;
   } catch { }
   lock (gate) {
    Image result;
    if (cache.TryGetValue(key, out result)) return result;
    try { if (File.Exists(custom)) { using (var source = Image.FromFile(custom)) result = new Bitmap(source); } } catch { }
    if (result == null && item.type != "url") result = ShellIcon(target);
    if (result == null) {
     string name = item.type == "url" ? "Browser" : item.type == "app" ? "Terminal" : "Explorer";
     string asset = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDesk", "Rainmeter", "Skins", "MacDesk", "@Resources", "Icons", name + ".png");
     try { using (var source = Image.FromFile(asset)) result = new Bitmap(source); } catch { }
    }
    if (result == null) {
     var b = new Bitmap(128, 128);
     using (var g = Graphics.FromImage(b)) {
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.Clear(Color.Transparent);
      var rect = new Rectangle(10, 10, 108, 108);
      using (var path = RoundedRect(rect, 26))
      using (var brush = new LinearGradientBrush(rect, item.type == "url" ? Color.FromArgb(68, 138, 255) : Color.FromArgb(82, 108, 148), item.type == "url" ? Color.FromArgb(36, 88, 210) : Color.FromArgb(46, 64, 92), LinearGradientMode.Vertical))
      using (var border = new Pen(Color.FromArgb(140, 255, 255, 255), 2f)) {
       g.FillPath(brush, path);
       g.DrawPath(border, path);
      }
      using (var font = new Font("Segoe UI", 44, FontStyle.Bold, GraphicsUnit.Pixel))
      using (var brush = new SolidBrush(Color.White))
      using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
       g.DrawString(item.type == "url" ? "↗" : (item.type == "folder" ? "▤" : "▣"), font, brush, new RectangleF(10, 10, 108, 108), fmt);
      }
     }
     result = b;
    }
    cache[key] = result;
    return result;
   }
  }

  static GraphicsPath RoundedRect(Rectangle r, int radius) {
   var p = new GraphicsPath();
   int d = radius * 2;
   p.AddArc(r.Left, r.Top, d, d, 180, 90);
   p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
   p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
   p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
   p.CloseFigure();
   return p;
  }
 }
}
