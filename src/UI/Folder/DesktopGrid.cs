using System;
using System.Collections.Generic;
using System.Drawing;

namespace MacDeskNative {
 internal sealed class DesktopGrid {
  const int Gap = 12;
  readonly int[] columns;
  public readonly Rectangle Viewport;
  public readonly Rectangle Reserved;

  internal DesktopGrid(Rectangle work, Rectangle widget) {
   Reserved = widget.IsEmpty ? Rectangle.Empty : Rectangle.Intersect(work, Rectangle.Inflate(widget, Gap, Gap));
   if (Reserved.Width <= 0 || Reserved.Height <= 0) Reserved = Rectangle.Empty;
   Viewport = work;
   var left = new List<int>();
   var right = new List<int>();
   int inset = Math.Min(DesktopFolderForm.EdgePad, Math.Max(0, (work.Width - TileWidth) / 2));
   int first = work.Left + inset, last = work.Right - inset - TileWidth;
   int leftLimit = Reserved.IsEmpty ? last : Math.Min(last, Reserved.Left - TileWidth);
   for (int x = first; x <= leftLimit; x += DesktopFolderForm.StepX) left.Add(x);
   if (!Reserved.IsEmpty) {
    for (int x = last; x >= Math.Max(first, Reserved.Right); x -= DesktopFolderForm.StepX) right.Add(x);
    right.Reverse();
   }
   if (left.Count + right.Count == 0) {
    // On a narrow work area use the larger band above or below the widgets.
    if (!Reserved.IsEmpty) {
     Rectangle above = Rectangle.FromLTRB(work.Left, work.Top, work.Right, Reserved.Top);
     Rectangle below = Rectangle.FromLTRB(work.Left, Reserved.Bottom, work.Right, work.Bottom);
     Viewport = below.Height >= above.Height ? below : above;
    }
    for (int x = first; x <= last; x += DesktopFolderForm.StepX) left.Add(x);
    if (left.Count == 0) left.Add(work.Left);
   }
   bool rightFirst = !Reserved.IsEmpty && widget.Left + widget.Width / 2 < work.Left + work.Width / 2;
   var preferred = rightFirst ? right : left;
   preferred.AddRange(rightFirst ? left : right);
   columns = preferred.ToArray();
  }

  static int TileWidth { get { return DesktopFolderForm.TileWidth; } }
  int Top { get { return Viewport.Top + Math.Min(DesktopFolderForm.EdgePad, Math.Max(0, Viewport.Height - DesktopFolderForm.TileHeight)); } }

  internal Point Cell(int index) {
   return new Point(columns[index % columns.Length], Top + (index / columns.Length) * DesktopFolderForm.StepY);
  }

  internal int SlotAt(float x, float y) {
   if (y < Top) return -1;
   int row = (int)(y - Top) / DesktopFolderForm.StepY;
   for (int col = 0; col < columns.Length; col++)
    if (x >= columns[col] && x < columns[col] + TileWidth) return row * columns.Length + col;
   return -1;
  }

  internal bool SameSlots(DesktopGrid other) {
   if (other == null || Viewport != other.Viewport || columns.Length != other.columns.Length) return false;
   for (int i = 0; i < columns.Length; i++) if (columns[i] != other.columns[i]) return false;
   return true;
  }

  internal int MaxScroll(int count) {
   int bottom = Cell(Math.Max(0, count - 1)).Y + DesktopFolderForm.TileHeight + DesktopFolderForm.EdgePad;
   return Math.Max(0, bottom - Viewport.Bottom);
  }
 }
}
