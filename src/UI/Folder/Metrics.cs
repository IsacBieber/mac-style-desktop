using System;
using System.Drawing;

namespace MacDeskNative {
 // Every size used by folders, in one place. Desktop folder icons, the open folder, the
 // transition between them and all hit-testing read these; nothing re-derives them.
 internal static class Metrics {
  // Desktop folder icon.
  public const int DesktopIcon = 80;
  public const float DesktopIconRadius = DesktopIcon * 0.2237f;

  // Open folder: a title band above a frosted sheet with a grid of cells.
  public const int TitleBand = 60;
  public const float SheetRadius = 34f;
  public const int PadX = 26, PadTop = 26, PadBottom = 22;
  public const int CellW = 106, CellH = 112, GapX = 8, GapY = 10;
  public const int IconPx = 64, IconTop = 8;      // icon inside a cell
  public const int LabelGap = 6;                  // icon bottom -> label top
  public const int SearchH = 36, SearchGap = 14, PickerHeaderH = 34;

  // The 3x3 preview inside a desktop folder icon: slot i of an icon drawn in `box`.
  public static RectangleF MiniSlot(int i, RectangleF box) {
   float pad = box.Width * 0.135f, gap = box.Width * 0.055f;
   float cell = (box.Width - pad * 2 - gap * 2) / 3f;
   return new RectangleF(box.X + pad + (i % 3) * (cell + gap), box.Y + pad + (i / 3) * (cell + gap), cell, cell);
  }

  public static Rectangle IconPixels(RectangleF bounds) {
   int size = Math.Max(2, (int)Math.Round(bounds.Width));
   return new Rectangle((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y), size, size);
  }
 }

 // Pure, integer folder geometry. Same inputs -> same pixels, for drawing, hit-testing and the
 // open/close transition alike.
 internal static class FolderLayout {
  // Panel = title band + sheet, sized for its content and limited to the work area.
  public static Size PanelSize(Size work, bool picker, int itemCount, int pickerCount) {
   int cells = picker ? Math.Max(12, pickerCount) : itemCount + 1;
   int columns = picker ? 6 : Math.Max(3, Math.Min(5, cells <= 4 ? Math.Max(3, cells) : (cells <= 9 ? 4 : 5)));
   int rows = Math.Max(1, (cells + columns - 1) / columns);
   int width = Metrics.PadX * 2 + columns * Metrics.CellW + (columns - 1) * Metrics.GapX;
   int top = picker ? Metrics.PadTop + Metrics.PickerHeaderH + 12 + Metrics.SearchH + Metrics.SearchGap : Metrics.PadTop;
   int gridH = rows * Metrics.CellH + (rows - 1) * Metrics.GapY;
   int maxSheet = (int)(work.Height * 0.80) - Metrics.TitleBand;
   int sheetH = Math.Min(top + gridH + Metrics.PadBottom, maxSheet);
   if (picker) sheetH = Math.Max(sheetH, Math.Min(maxSheet, 520));
   return new Size(Math.Min(width, work.Width - 48), Metrics.TitleBand + Math.Max(Metrics.PadTop + Metrics.CellH + Metrics.PadBottom, sheetH));
  }

  // Where the panel sits inside the work area (work-area-relative): centred, a little high.
  public static Point PanelOrigin(Size work, Size panel) {
   int x = (work.Width - panel.Width) / 2, y = (work.Height - panel.Height) / 2 - 18;
   return new Point(Math.Max(12, Math.Min(work.Width - panel.Width - 12, x)), Math.Max(12, Math.Min(work.Height - panel.Height - 12, y)));
  }

  // Sheet inside the panel (panel-relative).
  public static Rectangle Sheet(Size panel) { return new Rectangle(0, Metrics.TitleBand, panel.Width, Math.Max(1, panel.Height - Metrics.TitleBand)); }

  public static int Columns(int viewWidth) { return Math.Max(1, (viewWidth + Metrics.GapX) / (Metrics.CellW + Metrics.GapX)); }

  // Integer left edge of the centred grid inside a viewport.
  public static int GridLeft(Rectangle view, int columns) {
   int used = columns * Metrics.CellW + (columns - 1) * Metrics.GapX;
   return view.X + (view.Width - used) / 2;
  }

  // Icon rectangle inside a cell whose top-left is (cellX, cellY), scaled by s around its centre;
  // always whole pixels and an even size, so it never lands on a half pixel.
  public static Rectangle IconInCell(float cellX, float cellY, float s) {
   float cx = cellX + Metrics.CellW / 2f, cy = cellY + Metrics.IconTop + Metrics.IconPx / 2f;
   int size = Math.Max(2, 2 * (int)Math.Round(Metrics.IconPx * s / 2f));
   return new Rectangle((int)Math.Round(cx - size / 2f), (int)Math.Round(cy - size / 2f), size, size);
  }
 }
}
