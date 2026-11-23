using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using MacDesk.Core;

namespace MacDeskNative {
 // Created on the UI thread inside NativeDesktopHost.Prepare's retained DPI scope.
 // No Explorer owner, SetParent, global mouse hook, or topmost policy is used here.
 // iOS home-screen folders: frosted wallpaper squircles with a 3x3 app preview, spring
 // physics for hover / press / drop, rendered at display refresh with dirty rectangles.
 public class DesktopFolderForm : Form {
  public const int TileWidth = 112, TileHeight = 130, StepX = 132, StepY = 148, EdgePad = 24;
  public const int IconSize = Metrics.DesktopIcon;
  public const float IconRadius = Metrics.DesktopIconRadius;
  const int IconTop = 6;
  const string ItemFormat = "MacDeskNative.Item";
  readonly AppController app;
  readonly List<FolderTile> tiles = new List<FolderTile>();
  readonly HashSet<ContextMenuStrip> menus = new HashSet<ContextMenuStrip>();
  readonly Font caption = Ui.MakeFont(Ui.Cjk, 12.5f, FontStyle.Regular);
  readonly LayeredSurface surface = new LayeredSurface();
  readonly DesktopGlassBrush wallpaperGlass = new DesktopGlassBrush(0.42f, Material.FolderTint);
  readonly FramePump pump;
  readonly Spring ghostAlpha = new Spring(0, 0.18, 1.0);
  readonly Dictionary<string, Bitmap> faces = new Dictionary<string, Bitmap>();
  readonly List<Rectangle> dirtyTiles = new List<Rectangle>();
  readonly Region dirtyClip = new Region();

  FolderTile hover, pressed, dropTarget;
  Point down, grabOffset, mouseClient = new Point(-999, -999);
  Rectangle magneticPreview = Rectangle.Empty, ghostDrawn = Rectangle.Empty;
  bool dragging, rendering, desktopChildMode, fullRedraw = true;
  int scroll;
  DesktopGrid grid;

  public bool DesktopChildMode { get { return desktopChildMode; } }

  sealed class FolderTile {
   public string Id, Title, FaceKey;
   public DeskGroup Group;
   public Rectangle Bounds;
   public int Index;
   public bool Plus;
   public readonly Spring X = new Spring(0, 0.38, 0.86);
   public readonly Spring Y = new Spring(0, 0.38, 0.86);
   public readonly Spring Scale = new Spring(1, 0.22, 0.86);
   public readonly Spring Hover = new Spring(0, 0.18, 1.0);
   public readonly Spring Lift = new Spring(0, 0.24, 0.9);
   public readonly Spring Alpha = new Spring(1, 0.22, 1.0);
   public Rectangle Drawn = Rectangle.Empty;
   public Rectangle PaintArea;
   public bool PaintDirty;
   public HashSet<string> MissingIcons;
   public bool Placed;
   public Bitmap LabelSprite;
   public string LabelKey;
   public void DisposeLabel() {
    if (LabelSprite != null) { LabelSprite.Dispose(); LabelSprite = null; }
    LabelKey = null;
   }
   public bool Step(double dt) { return X.Step(dt) | Y.Step(dt) | Scale.Step(dt) | Hover.Step(dt) | Lift.Step(dt) | Alpha.Step(dt); }
  }
  sealed class Payload { public string Source, Item, Url; public string[] Paths; public DragDropEffects Effect; }

  public DesktopFolderForm(AppController controller) {
   if (controller == null) throw new ArgumentNullException("controller");
   app = controller;
   spriteAttributes.SetWrapMode(WrapMode.TileFlipXY);
   desktopChildMode = NativeDesktopHost.Parent != IntPtr.Zero;
   if (desktopChildMode) TopLevel = false;
   AutoScaleMode = AutoScaleMode.None;
   FormBorderStyle = FormBorderStyle.None;
   ShowInTaskbar = true;
   TopMost = false;
   StartPosition = FormStartPosition.Manual;
   Location = Point.Empty;
   AccessibleName = "桌面透明文件夹画布";
   Text = "MacDesk Desktop Folders";
   AllowDrop = true;
   KeyPreview = true;
   SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
   pump = new FramePump(this, Tick);
   Wallpaper.Changed += OnWallpaperChanged;

   FitHost();
   RefreshData();
  }

  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams {
   get {
    var cp = base.CreateParams;
    if (desktopChildMode && NativeDesktopHost.Parent != IntPtr.Zero) {
     cp.Parent = NativeDesktopHost.Parent;
     cp.Style = (cp.Style | 0x40000000) & ~unchecked((int)0x80000000);
    } else {
     cp.Parent = IntPtr.Zero;
     cp.Style = (cp.Style | unchecked((int)0x80000000)) & ~0x40000000;
    }
    cp.ExStyle = (cp.ExStyle | 0x80000 | 0x80) & ~0x40000;
    return cp;
   }
  }

  protected override void CreateHandle() {
   if (desktopChildMode && NativeDesktopHost.Parent != IntPtr.Zero) {
    try {
     base.CreateHandle();
     return;
    } catch (Exception ex) {
     NativeDesktopHost.ResetStaleHandle(this);
     desktopChildMode = false;
     NativeDesktopHost.DisableChildHosting("DesktopFolderForm child hosting fallback: " + ex.Message);
     TopLevel = true;
    }
   } else if (desktopChildMode) {
    desktopChildMode = false;
    TopLevel = true;
   }
   base.CreateHandle();
  }

  void FitHost() {
   RECT rect;
   if (desktopChildMode && NativeDesktopHost.Parent != IntPtr.Zero && GetClientRect(NativeDesktopHost.Parent, out rect))
    ClientSize = new Size(Math.Max(1, rect.R - rect.L), Math.Max(1, rect.B - rect.T));
   else
    ClientSize = Screen.PrimaryScreen.Bounds.Size;
  }


  DesktopGrid CurrentGrid() {
   Rectangle work = Screen.PrimaryScreen.WorkingArea;
   HomeForm home = app.Home;
   Rectangle widgets = home == null || home.IsDisposed ? Rectangle.Empty : home.Bounds;
   if (IsHandleCreated) {
    work = RectangleToClient(work);
    if (home != null && home.IsHandleCreated) widgets = RectangleToClient(home.RectangleToScreen(home.ClientRectangle));
   } else work.Offset(-Screen.PrimaryScreen.Bounds.Left, -Screen.PrimaryScreen.Bounds.Top);
   return new DesktopGrid(Rectangle.Intersect(ClientRectangle, work), widgets);
  }

  internal void ReflowWidgets() {
   if (Disposing || IsDisposed || !IsHandleCreated) return;
   DesktopGrid next = CurrentGrid(), old = grid;
   grid = next;
   if (!next.SameSlots(old)) {
    int newScroll = Math.Min(scroll, grid.MaxScroll(tiles.Count));
    fullRedraw |= old == null || next.Viewport != old.Viewport || scroll != newScroll;
    scroll = newScroll;
    foreach (var tile in tiles) {
     Point nextCell = CellPos(tile.Index);
     if (tile.Bounds.Location == nextCell) continue;
     tile.Bounds.Location = nextCell;
     tile.PaintDirty = true;
     Place(tile, tile.Bounds);
    }
    WakeAnim();
   }
  }

  public void RefreshData() {
   if (IsDisposed || Disposing) return;
   FitHost();
   LayoutTiles();
   fullRedraw = true;
   Render();
   WakeAnim();
  }

  // Folders have one order. The desktop grid maps it into the space around the widgets.
  void LayoutTiles() {
   grid = CurrentGrid();
   var previous = new Dictionary<string, FolderTile>();
   foreach (var t in tiles) previous[t.Plus ? "+" : t.Id] = t;
   tiles.Clear();
   int index = 0;
   var liveFaces = new HashSet<string>();
   foreach (var group in app.Data.groups) {
    var bounds = new Rectangle(CellPos(index), new Size(TileWidth, TileHeight));
    FolderTile tile;
    if (!previous.TryGetValue(group.id, out tile)) tile = new FolderTile();
    else previous.Remove(group.id);
    tile.Id = group.id; tile.Title = group.title; tile.Group = group; tile.Bounds = bounds; tile.Index = index++; tile.Plus = false;
    string key = FaceKey(group);
    if (tile.FaceKey != key) tile.MissingIcons = null;
    tile.FaceKey = key;
    liveFaces.Add(tile.FaceKey);
    Place(tile, bounds);
    tiles.Add(tile);
   }
   var add = new Rectangle(CellPos(index), new Size(TileWidth, TileHeight));
   FolderTile plus;
   if (!previous.TryGetValue("+", out plus)) { plus = new FolderTile(); plus.Alpha.Snap(0.0); }
   else previous.Remove("+");
   plus.Id = null; plus.Title = "新建"; plus.Plus = true; plus.Group = null; plus.Bounds = add; plus.Index = index;
   Place(plus, add);
   tiles.Add(plus);
   foreach (var removed in previous.Values) removed.DisposeLabel();
   scroll = Math.Min(scroll, grid.MaxScroll(tiles.Count));
   if (hover != null && !tiles.Contains(hover)) hover = null;
   dropTarget = null;
   // Drop cached 3x3 previews of folders whose contents changed.
   foreach (var key in new List<string>(faces.Keys)) if (!liveFaces.Contains(key)) { faces[key].Dispose(); faces.Remove(key); }
  }

  static void Place(FolderTile tile, Rectangle bounds) {
   tile.X.Target = bounds.X; tile.Y.Target = bounds.Y;
   if (!tile.Placed) { tile.X.Snap(bounds.X); tile.Y.Snap(bounds.Y); tile.Placed = true; }
  }

  static string FaceKey(DeskGroup g) {
   var sb = new System.Text.StringBuilder(g.id);
   foreach (DeskItem item in PreviewApps(g))
    sb.Append('|').Append(item.id).Append(item.target).Append(item.icon);
   return sb.ToString();
  }

  internal static IEnumerable<DeskItem> PreviewApps(DeskGroup group) {
   if (group == null) yield break;
   int count = 0;
   foreach (DeskItem item in group.items) {
    if (item.type != "app") continue;
    yield return item;
    if (++count == 9) yield break;
   }
  }

  // --- Animation ------------------------------------------------------------------------

  void WakeAnim() {
   if (IsDisposed || Disposing || !IsHandleCreated) return;
   pump.Start();
  }

  // Offscreen benchmark: move the pointer onto each folder and off again.
  internal Bench.Stat[] Bench() {
   IntPtr h = Handle;
   fullRedraw = true;
   Render();
   var folders = tiles.FindAll(t => !t.Plus);
   int k = 0;
   // Hover stability: every pixel of a folder's hover area must be non-transparent at rest
   // AND at full hover scale, otherwise Windows reports enter/leave flicker (jitter).
   var holes = new MacDeskNative.Bench.Stat("hover-area transparent px");
   foreach (bool hovered in new[] { false, true }) {
    foreach (var t in folders) { hover = hovered ? t : null; for (int f = 0; f < 120; f++) Tick(1 / 240.0); CountHoles(t, holes); }
   }
   hover = null;
   var stat = MacDeskNative.Bench.Time("desktop folder hover in/out", Math.Max(1, folders.Count) * 48, delegate {
    int i = k++;
    hover = (i / 24) % 2 == 0 && folders.Count > 0 ? folders[(i / 48) % folders.Count] : null;
    Tick(1 / 240.0);
   });
   hover = null;
   var reorder = BenchReorder();
   return new[] { stat, holes, reorder };
  }

  // Carry folder #1 onto folder #3's cell, hold, drop. Frames -> MACDESK_BENCH_FRAMES.
  Bench.Stat BenchReorder() {
   var folders = tiles.FindAll(t => !t.Plus);
   if (folders.Count < 3) return new Bench.Stat("desktop reorder: skipped");
   string dir = Environment.GetEnvironmentVariable("MACDESK_BENCH_FRAMES");
   var carried = folders[0];
   string carriedId = carried.Id, secondId = folders[1].Id, thirdId = folders[2].Id;
   int startCell = carried.Index, targetCell = folders[2].Index;
   pressed = carried;
   grabOffset = new Point(TileWidth / 2, IconTop + IconSize / 2);
   dragging = true;
   BeginFolderDrag(carried);
   Point a = CellPos(startCell), b = CellPos(targetCell);
   var path = new List<Point>();
   for (int i = 1; i <= 50; i++) path.Add(new Point(a.X + (b.X - a.X) * i / 50 + grabOffset.X, a.Y + (b.Y - a.Y) * i / 50 + grabOffset.Y - scroll));
   for (int i = 0; i < 70; i++) path.Add(path[path.Count - 1]);
   int n = 0;
   var stat = MacDeskNative.Bench.Time("desktop reorder drag", path.Count, delegate {
    UpdateFolderDrag(path[n]);
    Tick(1 / 240.0);
    if (dir != null && n % 10 == 0) surface.SaveSnapshot(System.IO.Path.Combine(dir, "desk_" + n.ToString("000") + ".png"));
    n++;
   });
   pressed = null;
   dragging = false;
   EndFolderDrag(carried, true);
   for (int i = 0; i < 90; i++) { Tick(1 / 240.0); if (dir != null && i % 30 == 0) surface.SaveSnapshot(System.IO.Path.Combine(dir, "deskdrop_" + i.ToString("000") + ".png")); }
   var data = app.Data.groups;
   Func<string, int> cellOf = id => data.FindIndex(v => v.id == id);
   bool ok = cellOf(carriedId) == targetCell && cellOf(secondId) == startCell && cellOf(thirdId) == startCell + 1;
   var result = new Bench.Stat("desktop reorder #1->#3 " + (ok ? "OK" : "WRONG(" + cellOf(carriedId) + "," + cellOf(secondId) + "," + cellOf(thirdId) + ")") + " drag");
   result.Add(0);
   return ok ? stat : result;
  }
  void CountHoles(FolderTile t, MacDeskNative.Bench.Stat holes) {
   RectangleF r = HitRect(t);
   int count = 0;
   for (int y = (int)Math.Ceiling(r.Top); y < (int)Math.Floor(r.Bottom); y++)
    for (int x = (int)Math.Ceiling(r.Left); x < (int)Math.Floor(r.Right); x++)
     if (surface.AlphaAt(x, y) == 0) count++;
   holes.Add(count);
  }

  bool Tick(double dt) {
   bool active = false;
   foreach (var tile in tiles) {
    bool isDragged = dragging && tile == pressed;
    tile.Hover.Target = (tile == hover || tile == dropTarget) && !isDragged ? 1 : 0;
    tile.Scale.Target = tile == dropTarget ? 1.12 : (tile == pressed && !dragging ? 0.88 : (isDragged ? 1.08 : (tile == hover ? 1.05 : 1.0)));
    tile.Lift.Target = isDragged ? 1 : 0;
    if (tile.Plus) {
     Rectangle vis = VisibleBounds(tile);
     double dx = mouseClient.X - (vis.X + vis.Width / 2.0), dy = mouseClient.Y - (vis.Y + IconTop + IconSize / 2.0);
     double dist = Math.Sqrt(dx * dx + dy * dy);
     tile.Alpha.Target = dragging ? 0.0 : (app.Data.groups.Count == 0 || tile == hover) ? 1.0 : (dist < 150 ? 0.15 + 0.85 * (1 - dist / 150) : 0.0);
    }
    bool wasMoving = !AllSettled(tile);
    bool moving = tile.Step(dt);
    if (wasMoving || moving) tile.PaintDirty = true;
    if (moving) active = true;
   }
   ghostAlpha.Target = dragging && !magneticPreview.IsEmpty ? 1 : 0;
   bool ghostMoving = ghostAlpha.Step(dt);
   active |= ghostMoving;
   double t0 = FrameLog.On ? FrameLog.Now : 0;
   Render();
   if (FrameLog.On) {
    var h = hover;
    FrameLog.Add("desk", dt * 1000, FrameLog.Now - t0, h == null ? "-" : (h.Title + " scale=" + h.Scale.Value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)));
    if (!active) FrameLog.Flush();
   }
   return active;
  }

  Rectangle VisibleBounds(FolderTile tile) { var r = tile.Bounds; r.Offset(0, -scroll); return r; }
  RectangleF VisualBounds(FolderTile tile) { return new RectangleF(tile.X.F, tile.Y.F - scroll, TileWidth, TileHeight); }

  RectangleF IconRect(FolderTile tile) {
   RectangleF b = VisualBounds(tile);
   float s = tile.Scale.F, size = IconSize * s;
   float lift = -4f * tile.Lift.F;
   return new RectangleF(b.X + (TileWidth - size) / 2f, b.Y + IconTop + (IconSize - size) / 2f + lift, size, size);
  }

  Rectangle PreviewRect(FolderTile tile) {
   RectangleF b = VisualBounds(tile);
   return Metrics.IconPixels(new RectangleF(b.X + (TileWidth - IconSize) / 2f, b.Y + IconTop, IconSize, IconSize));
  }

  // Everything a tile can paint (shadow, scaled icon, label), used for dirty tracking.
  Rectangle PaintBounds(FolderTile tile) {
   RectangleF b = VisualBounds(tile);
   return Rectangle.FromLTRB((int)Math.Floor(b.X - 16), (int)Math.Floor(b.Y - 14), (int)Math.Ceiling(b.Right + 16), (int)Math.Ceiling(b.Bottom + 12));
  }

  FolderTile Hit(Point point) {
   if (grid == null || !grid.Viewport.Contains(point) || grid.Reserved.Contains(point)) return null;
   for (int i = tiles.Count - 1; i >= 0; i--) {
    var t = tiles[i];
    if (!Hittable(t)) continue;
    if (HitRect(t).Contains(point)) return t;
   }
   return null;
  }
  bool Hittable(FolderTile t) { return !(t.Plus && t.Alpha.Value < 0.08 && app.Data.groups.Count > 0); }
  RectangleF HitRect(FolderTile t) { var b = VisualBounds(t); return new RectangleF(b.X + 8, b.Y, TileWidth - 16, TileHeight - 6); }

  // A layered window is click-through wherever a pixel is fully transparent. Without this, the
  // rounded corners, the gap above the label and the space between glyphs made Windows report
  // "mouse left" and "mouse entered" as the pointer crossed them; the hover scale then toggled
  // on and off (and changed which pixels were transparent) - the visible jitter. An invisible
  // alpha-1 plate over exactly the Hit() rectangle makes the hover area stable.
  static readonly SolidBrush HitPlate = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
  void DrawHitPlate(Graphics g, FolderTile t) {
   if (!Hittable(t)) return;
   RectangleF r = HitRect(t);
   var mode = g.SmoothingMode;
   g.SmoothingMode = SmoothingMode.None;
   g.FillRectangle(HitPlate, Rectangle.FromLTRB((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom)));
   g.SmoothingMode = mode;
  }

  // --- Rendering ------------------------------------------------------------------------

  void OnWallpaperChanged(object sender, EventArgs e) {
   if (IsDisposed) return;
   fullRedraw = true;
   if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) Render(); });
  }

  // The 3x3 preview (icons only, transparent), cached per folder content.
  Bitmap Face(FolderTile tile) {
   Bitmap face;
   if (tile.FaceKey != null && faces.TryGetValue(tile.FaceKey, out face)) return face;
   face = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppPArgb);
   using (var g = Graphics.FromImage(face)) {
    g.Clear(Color.Transparent);
    Ui.PrepareQuality(g);
    tile.MissingIcons = null;
    DrawMiniGrid(g, tile.Group, new Rectangle(Point.Empty, face.Size), spriteAttributes, spriteMatrix, item => {
     Bitmap icon = app.GetIcon(item, false);
     if (icon == null) {
      if (tile.MissingIcons == null) tile.MissingIcons = new HashSet<string>();
      tile.MissingIcons.Add(FolderLayer.IconKey(item));
     }
     return icon;
    });
   }
   if (tile.FaceKey != null) faces[tile.FaceKey] = face;
   return face;
  }

  internal static void DrawMiniGrid(Graphics g, DeskGroup grp, Rectangle box, ImageAttributes attributes, ColorMatrix matrix,
   Func<DeskItem, Bitmap> lookup = null) {
   int slot = 0;
   foreach (DeskItem item in PreviewApps(grp)) {
    Rectangle cell = Metrics.IconPixels(Metrics.MiniSlot(slot++, box));
    try {
     Image image = lookup == null ? FolderLayer.CachedIcon(item) : lookup(item);
     if (image == null) continue;
     Ui.DrawIcon(g, image, cell, 1f, attributes, matrix);
    } catch (Exception) { }
   }
  }

  internal void RefreshIcons(HashSet<string> ready) {
   bool redraw = false;
   foreach (var tile in tiles) {
    if (tile.MissingIcons == null || !tile.MissingIcons.Overlaps(ready)) continue;
    Bitmap old;
    if (tile.FaceKey != null && faces.TryGetValue(tile.FaceKey, out old)) {
     old.Dispose();
     faces.Remove(tile.FaceKey);
    }
    tile.PaintDirty = true;
    redraw = true;
   }
   if (redraw) WakeAnim();
  }

  // The folder that is open (or opening/closing) is not drawn on the desktop: the folder layer
  // draws it. Repaints just that tile, synchronously, so the hand-over happens in one frame.
  string openFolderId;
  internal void SetOpenFolder(string groupId) {
   if (openFolderId == groupId || IsDisposed) return;
   string before = openFolderId;
   openFolderId = groupId;
   foreach (var t in tiles) if (!t.Plus && (t.Id == before || t.Id == groupId)) {
    if (t.Id == before) { t.Scale.Snap(1); t.Lift.Snap(0); t.Hover.Snap(0); }
    t.PaintDirty = true;
   }
   Render();
  }

  // The preview is pinned to the tile while the glass responds to hover and press.
  internal Rectangle? IconRectOnScreen(string groupId) {
   if (!IsHandleCreated) return null;
   foreach (var t in tiles) {
    if (t.Plus || t.Id != groupId) continue;
    Rectangle r = PreviewRect(t);
    return new Rectangle(PointToScreen(r.Location), r.Size);
   }
   return null;
  }

  // The folder icon's frosted body as drawn on the desktop, WITHOUT its 3x3 preview: the open
  // transition shows it for the first frames while the preview icons themselves fly out.
  internal Bitmap RenderTileBody(string groupId) {
   FolderTile target = null;
   foreach (var t in tiles) if (!t.Plus && t.Id == groupId) { target = t; break; }
   if (target == null) return null;
   var bmp = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppPArgb);
   using (var g = Graphics.FromImage(bmp)) {
    g.Clear(Color.Transparent);
    Ui.PrepareQuality(g);
    Rectangle icon = PreviewRect(target);
    g.TranslateTransform(-icon.X, -icon.Y);
    DrawFolderBody(g, icon);
   }
   return bmp;
  }

  void DrawFolderBody(Graphics g, RectangleF icon) {
   float radius = icon.Width * 0.2237f;
   using (var shape = Ui.Squircle(icon, radius)) FillFolderGlass(g, shape, GlassBrush());
   Material.Rim(g, icon, radius, 0.75f);
  }

  static void FillFolderGlass(Graphics g, GraphicsPath shape, TextureBrush glass) {
   Material.Fill(g, shape, glass, glass == null ? Material.FolderTint : Color.Transparent, Material.FolderFallback);
  }

  TextureBrush GlassBrush() {
   Point origin = IsHandleCreated ? PointToScreen(Point.Empty) : Location;
   return wallpaperGlass.At(Wallpaper.Current, origin);
  }

  readonly System.Drawing.Imaging.ImageAttributes spriteAttributes = new System.Drawing.Imaging.ImageAttributes();
  readonly System.Drawing.Imaging.ColorMatrix spriteMatrix = new System.Drawing.Imaging.ColorMatrix();

  Bitmap LabelSprite(FolderTile tile) {
   string key = tile.Title ?? "";
   if (tile.LabelSprite != null && tile.LabelKey == key) return tile.LabelSprite;
   if (tile.LabelSprite != null) tile.LabelSprite.Dispose();
   var bmp = new Bitmap(TileWidth + 16 + 8, 28, PixelFormat.Format32bppPArgb);
   using (var g = Graphics.FromImage(bmp))
   using (var f = Ui.TopCentered()) {
    g.Clear(Color.Transparent);
    Ui.PrepareQuality(g);
    TextStyle.DrawShadowed(g, key, Ui.Cjk, FontStyle.Regular, 13.5f, Ui.Label, new RectangleF(4, 3, TileWidth + 16, 23), f, 0.45f, 1f);
   }
   tile.LabelSprite = bmp;
   tile.LabelKey = key;
   return bmp;
  }

  void DrawTile(Graphics g, FolderTile tile, TextureBrush glass) {
   DrawHitPlate(g, tile);
   float alpha = (float)Math.Max(0, Math.Min(1, tile.Alpha.Value));
   if (alpha <= 0.02f) return;
   if (!tile.Plus && tile.Id == openFolderId) {
    // This folder is open: its icon is the open folder now (like iOS, the source icon leaves
    // the desktop while it is out), so only its name stays.
    RectangleF vb = VisualBounds(tile);
    Ui.Blit(g, LabelSprite(tile), (int)Math.Round(vb.X - 12), (int)Math.Round(vb.Y + IconTop + IconSize + 3 + tile.Lift.F * 2f), alpha, spriteAttributes, spriteMatrix);
    return;
   }
   RectangleF icon = IconRect(tile);
   float s = icon.Width / IconSize;
   float radius = IconRadius * s;
   float lift = tile.Lift.F, hover = (float)Math.Max(0, Math.Min(1, tile.Hover.Value));

   // Soft contact shadow (vector, so it scales smoothly with the icon).
   for (int i = 0; i < 3; i++) {
    float spread = 1.5f + i * 2.2f + lift * (3f + i * 2f);
    var r = new RectangleF(icon.X - spread * 0.5f, icon.Y + 2.5f + lift * 7f - spread * 0.2f, icon.Width + spread, icon.Height + spread);
    using (var path = Ui.Squircle(r, radius + spread * 0.5f))
    using (var b = new SolidBrush(Color.FromArgb((int)((16 - i * 4 + lift * 10) * alpha), 0, 0, 0))) g.FillPath(b, path);
   }

   using (var shape = Ui.Squircle(icon, radius)) {
    if (tile.Plus) {
     using (var b = new SolidBrush(Color.FromArgb((int)(60 * alpha), 255, 255, 255))) g.FillPath(b, shape);
     float arm = icon.Width * 0.19f, cx = icon.X + icon.Width / 2f, cy = icon.Y + icon.Height / 2f;
     using (var pen = new Pen(Color.FromArgb((int)(240 * alpha), 255, 255, 255), 3f)) {
      pen.StartCap = pen.EndCap = LineCap.Round;
      g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
      g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
     }
    } else {
     FillFolderGlass(g, shape, glass);
     // Hover just brightens the glass a touch.
     if (hover > 0.01f) using (var b = new SolidBrush(Color.FromArgb((int)(30 * hover), 255, 255, 255))) g.FillPath(b, shape);
    }
   }
   Material.Rim(g, icon, radius, (tile.Plus ? 0.8f : 0.75f + 0.25f * hover) * alpha);
   if (!tile.Plus) {
    Rectangle preview = dragging && tile == pressed ? Metrics.IconPixels(icon) : PreviewRect(tile);
    Ui.DrawIcon(g, Face(tile), preview, alpha, spriteAttributes, spriteMatrix);
   }

   RectangleF b2 = VisualBounds(tile);
   Ui.Blit(g, LabelSprite(tile), (int)Math.Round(b2.X - 12), (int)Math.Round(b2.Y + IconTop + IconSize + 3 + lift * 2f), alpha, spriteAttributes, spriteMatrix);
  }

  void Render() {
   if (IsDisposed || Disposing || rendering || grid == null || !IsHandleCreated || ClientSize.Width < 1 || ClientSize.Height < 1) return;
   rendering = true;
   try {
    Rectangle client = ClientRectangle;
    bool full = fullRedraw || surface.Size != client.Size;
    dirtyTiles.Clear();
    foreach (var tile in tiles) {
     tile.PaintArea = PaintBounds(tile);
     if (full || (!tile.PaintDirty && tile.PaintArea == tile.Drawn && AllSettled(tile))) continue;
     AddDirty(tile.Drawn, client);
     AddDirty(tile.PaintArea, client);
    }
    Rectangle ghost = magneticPreview;
    if (!ghost.IsEmpty) ghost.Offset(0, -scroll);
    if (!ghost.IsEmpty) ghost.Inflate(6, 6);
    if (full) dirtyTiles.Add(client);
    else if (ghost != ghostDrawn || !ghostAlpha.Settled) {
     AddDirty(ghostDrawn, client);
     AddDirty(ghost, client);
    }
    if (dirtyTiles.Count == 0) return;

    var g = surface.Begin(client.Size);
    Rectangle dirty = Rectangle.Empty;
    if (dirtyTiles.Count == 1) {
     dirty = dirtyTiles[0];
     g.SetClip(dirty);
    } else {
     dirtyClip.MakeEmpty();
     foreach (var area in dirtyTiles) {
      dirtyClip.Union(area);
      dirty = Union(dirty, area);
     }
     g.SetClip(dirtyClip, CombineMode.Replace);
    }
    surface.Clear(dirty); // the clip clears only the changed rectangles
    g.SetClip(grid.Viewport, CombineMode.Intersect);
    // Reservation decides the landing slots. The widget window above this surface handles
    // visual overlap while a folder is moving, keeping its outline intact.

    if (ghostAlpha.Value > 0.01 && !magneticPreview.IsEmpty && g.IsVisible(ghost)) {
     var slot = magneticPreview; slot.Offset(0, -scroll);
     var gi = new RectangleF(slot.X + (TileWidth - IconSize) / 2f, slot.Y + IconTop, IconSize, IconSize);
     using (var path = Ui.Squircle(gi, IconRadius))
     using (var b = new SolidBrush(Color.FromArgb((int)(46 * ghostAlpha.Value), 255, 255, 255)))
     using (var pen = new Pen(Color.FromArgb((int)(90 * ghostAlpha.Value), 255, 255, 255), 1f)) { g.FillPath(b, path); g.DrawPath(pen, path); }
    }
    ghostDrawn = ghost;

    TextureBrush glass = GlassBrush();
    FolderTile top = null;
    foreach (var tile in tiles) {
     tile.Drawn = tile.PaintArea;
     tile.PaintDirty = false;
     if (!g.IsVisible(tile.Drawn)) continue;
     if (dragging && tile == pressed) { top = tile; continue; }
     DrawTile(g, tile, glass);
    }
    if (top != null) DrawTile(g, top, glass);

    if (grid.MaxScroll(tiles.Count) > 0) {
     Rectangle viewport = grid.Viewport;
     var marker = new RectangleF(viewport.Right - 46, viewport.Bottom - 40, 30, 30);
     if (g.IsVisible(marker)) {
      using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillEllipse(b, marker);
      using (var f = Ui.Centered(false)) using (var b = new SolidBrush(Color.White)) g.DrawString("↕", caption, b, marker, f);
     }
    }
    g.ResetClip();
    surface.Present(Handle, desktopChildMode ? (Point?)null : Location, 255, full ? (Rectangle?)null : dirty);
    fullRedraw = false;
   } finally { rendering = false; }
  }

  void AddDirty(Rectangle area, Rectangle client) {
   area.Intersect(client);
   if (area.Width > 0 && area.Height > 0) dirtyTiles.Add(area);
  }

  static bool AllSettled(FolderTile t) {
   return t.X.Settled && t.Y.Settled && t.Scale.Settled && t.Hover.Settled && t.Lift.Settled && t.Alpha.Settled;
  }
  static Rectangle Union(Rectangle a, Rectangle b) { if (a.IsEmpty) return b; if (b.IsEmpty) return a; return Rectangle.Union(a, b); }

  protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); fullRedraw = true; Render(); }
  protected override void OnPaint(PaintEventArgs e) { }
  protected override void OnPaintBackground(PaintEventArgs e) { }
  protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (app != null && !rendering) { LayoutTiles(); fullRedraw = true; Render(); } }
  protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); fullRedraw = true; }

  protected override void WndProc(ref Message m) {
   if (m.Msg == 0x7B) { m.Result = IntPtr.Zero; return; }
   if (m.Msg == 0x84) {
    long packed = m.LParam.ToInt64();
    var point = PointToClient(new Point((short)(packed & 65535), (short)((packed >> 16) & 65535)));
    mouseClient = point;
    WakeAnim();
    if (Hit(point) == null && !dragging) { m.Result = new IntPtr(-1); return; }
   }
   if (m.Msg == 0x14) { m.Result = new IntPtr(1); return; }
   base.WndProc(ref m);
  }

  // --- Mouse ----------------------------------------------------------------------------

  protected override void OnMouseDown(MouseEventArgs e) {
   base.OnMouseDown(e);
   if (e.Button != MouseButtons.Left) return;
   pressed = Hit(e.Location);
   down = e.Location;
   dragging = false;
   magneticPreview = Rectangle.Empty;
   if (pressed != null) {
    grabOffset = new Point(e.X - (int)pressed.X.Value, e.Y - (int)(pressed.Y.Value - scroll));
    Capture = true;
   }
   WakeAnim();
   if (pressed != null && !pressed.Plus) {
    // Show the press first, then capture the backdrop for the folder that is about to open.
    Render();
    Point at = PointToScreen(e.Location);
    Defer(delegate { if (pressed != null && !dragging) app.PrefetchBackdrop(at); });
   }
  }

  protected override void OnMouseMove(MouseEventArgs e) {
   base.OnMouseMove(e);
   mouseClient = e.Location;
   if (pressed != null && !pressed.Plus && e.Button == MouseButtons.Left) {
    // A click with a little hand jitter must stay a click: only start moving the folder after 7 px.
    var threshold = new Rectangle(down.X - 7, down.Y - 7, 14, 14);
    if (!dragging && !threshold.Contains(e.Location)) { dragging = true; BeginFolderDrag(pressed); }
    if (dragging) {
     UpdateFolderDrag(e.Location);
     Cursor = Cursors.SizeAll;
     return;
    }
   }
   var hit = Hit(e.Location);
   if (hit != hover) {
    hover = hit;
    Cursor = hit == null ? Cursors.Default : Cursors.Hand;
   }
   WakeAnim();
  }

  protected override void OnMouseUp(MouseEventArgs e) {
   base.OnMouseUp(e);
   var tile = pressed;
   bool moved = dragging;
   pressed = null;
   dragging = false;
   magneticPreview = Rectangle.Empty;
   Capture = false;
   Cursor = Cursors.Default;
   if (e.Button == MouseButtons.Right) { ShowMenu(Hit(e.Location), e.Location); return; }
   if (e.Button != MouseButtons.Left || tile == null) { WakeAnim(); return; }
   if (moved) {
    EndFolderDrag(tile, true);
   } else if (Hit(e.Location) == tile) {
    if (tile.Plus) tile.Scale.Velocity += 1.2;
    RectangleF icon = IconRect(tile);
    Point center = PointToScreen(new Point((int)Math.Round(icon.X + icon.Width / 2f), (int)Math.Round(VisualBounds(tile).Y + IconTop + IconSize / 2f)));
    if (tile.Plus) Defer(delegate { app.CreateAndOpenNewGroup(center); });
    else { string id = tile.Id; Defer(delegate { app.OpenGroup(id, center); }); }
   } else app.DiscardPrefetch();
   WakeAnim();
  }


  // --- Folder rearranging, iOS home-screen style ----------------------------------------
  // Folders form one ordered list on a fixed grid. While one is carried, the folders between
  // its old and new place slide one cell toward the gap (springs); release saves the order.

  int dragFromIndex = -1, dragToIndex = -1;

  Point CellPos(int cell) { return grid.Cell(cell); }
  int FolderCount() { int n = 0; foreach (var t in tiles) if (!t.Plus) n++; return n; }

  void BeginFolderDrag(FolderTile tile) {
   app.DiscardPrefetch();
   dragFromIndex = tile.Index;
   dragToIndex = tile.Index;
   hover = null;
  }

  void UpdateFolderDrag(Point mouse) {
   FolderTile tile = pressed;
   if (tile == null) return;
   int rawX = Math.Max(0, Math.Min(Width - TileWidth, mouse.X - grabOffset.X));
   int rawY = Math.Max(0, mouse.Y - grabOffset.Y + scroll);
   // The folder sticks to the pointer while carried; springs take over again on release.
   tile.X.Snap(rawX); tile.Y.Snap(rawY);
   // Hysteresis: the carried icon's centre must be well inside a cell to claim it.
   float cx = rawX + TileWidth / 2f, cy = rawY + IconTop + IconSize / 2f;
   int cell = grid.SlotAt(cx, cy), count = FolderCount();
   if (cell >= 0 && count > 0 && !grid.Reserved.Contains(Point.Round(new PointF(cx, cy - scroll)))) {
    Point at = CellPos(cell);
    var inner = new RectangleF(at.X + 18, at.Y + IconTop + 10, TileWidth - 36, IconSize - 20);
    if (cell >= count) SetDropIndex(count - 1);       // past the last folder: goes last
    else if (inner.Contains(cx, cy)) SetDropIndex(cell);
   }
   WakeAnim();
  }

  void SetDropIndex(int index) {
   if (index == dragToIndex) return;
   dragToIndex = index;
   foreach (var t in tiles) {
    if (t.Plus || t == pressed) continue;
    int i = t.Index, slot = i;
    if (dragFromIndex < dragToIndex && i > dragFromIndex && i <= dragToIndex) slot = i - 1;
    else if (dragFromIndex > dragToIndex && i >= dragToIndex && i < dragFromIndex) slot = i + 1;
    Point at = CellPos(slot);
    t.X.Target = at.X; t.Y.Target = at.Y;
   }
  }

  void EndFolderDrag(FolderTile tile, bool commit) {
   int from = dragFromIndex, to = dragToIndex;
   dragFromIndex = dragToIndex = -1;
   magneticPreview = Rectangle.Empty;
   if (commit && from >= 0 && to >= 0 && to != from) {
    // Show the final order right away (no snap back), then save it.
    foreach (var t in tiles) {
     if (t.Plus) continue;
     int i = t.Index, slot = t == tile ? to : (from < to && i > from && i <= to) ? i - 1 : (from > to && i >= to && i < from) ? i + 1 : i;
     t.Bounds = new Rectangle(CellPos(slot), t.Bounds.Size);
     t.X.Target = t.Bounds.X; t.Y.Target = t.Bounds.Y;
    }
    app.MoveGroup(tile.Id, to > from ? to + 1 : to); // insertion index before the move
   } else {
    foreach (var t in tiles) { Point at = CellPos(t.Index); t.Bounds = new Rectangle(at, t.Bounds.Size); t.X.Target = at.X; t.Y.Target = at.Y; }
   }
   WakeAnim();
  }

  protected override void OnMouseCaptureChanged(EventArgs e) {
   base.OnMouseCaptureChanged(e);
   if (!Capture && pressed != null) {
    app.DiscardPrefetch();
    var tile = pressed;
    bool wasDragging = dragging;
    pressed = null;
    dragging = false;
    if (wasDragging) EndFolderDrag(tile, false);
    magneticPreview = Rectangle.Empty;
    LayoutTiles();
    WakeAnim();
   }
  }

  protected override void OnMouseLeave(EventArgs e) {
   base.OnMouseLeave(e);
   mouseClient = new Point(-999, -999);
   hover = null;
   WakeAnim();
  }

  protected override void OnMouseWheel(MouseEventArgs e) {
   base.OnMouseWheel(e);
   int next = Math.Max(0, Math.Min(grid.MaxScroll(tiles.Count), scroll - (e.Delta / 120) * StepY));
   if (next == scroll) return;
   scroll = next;
   fullRedraw = true;
   Render();
  }

  protected override void OnKeyDown(KeyEventArgs e) {
   base.OnKeyDown(e);
   if (e.KeyCode == Keys.Escape) app.ClosePanel();
   if (e.Control && e.KeyCode == Keys.N) Defer(delegate { app.AddGroup(); });
  }

  // --- Keyboard for the open folder -----------------------------------------------------
  // This window is a child of the desktop view, so it can hold keyboard focus without the
  // desktop losing activation. While a folder is open we take focus here and forward keys.

  IntPtr previousFocus;
  internal void TakeKeyboardFocus() {
   if (!IsHandleCreated) return;
   IntPtr current = GetFocus();
   if (current != Handle) previousFocus = current;
   SetFocus(Handle);
  }
  internal void ReleaseKeyboardFocus() {
   if (!IsHandleCreated) return;
   if (GetFocus() == Handle && previousFocus != IntPtr.Zero && IsWindow(previousFocus)) SetFocus(previousFocus);
   previousFocus = IntPtr.Zero;
  }
  protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
   if (app.RouteKey(keyData)) return true;
   return base.ProcessCmdKey(ref msg, keyData);
  }
  protected override void OnKeyPress(KeyPressEventArgs e) {
   base.OnKeyPress(e);
   if (app.RouteChar(e.KeyChar)) e.Handled = true;
  }

  // Puts the IME composition/candidate window at a screen point (the field being typed into).
  internal bool PlaceImeWindow(Point screen) {
   if (!IsHandleCreated) return false;
   IntPtr ctx = ImmGetContext(Handle);
   if (ctx == IntPtr.Zero) return false;
   try {
    Point p = PointToClient(screen);
    var comp = new COMPOSITIONFORM { dwStyle = 0x0002, x = p.X, y = p.Y }; // CFS_POINT
    ImmSetCompositionWindow(ctx, ref comp);
    var cand = new CANDIDATEFORM { dwIndex = 0, dwStyle = 0x0040, x = p.X, y = p.Y }; // CFS_CANDIDATEPOS
    ImmSetCandidateWindow(ctx, ref cand);
    return true;
   } finally { ImmReleaseContext(Handle, ctx); }
  }
  [StructLayout(LayoutKind.Sequential)] struct COMPOSITIONFORM { public int dwStyle, x, y, l, t, r, b; }
  [StructLayout(LayoutKind.Sequential)] struct CANDIDATEFORM { public int dwIndex, dwStyle, x, y, l, t, r, b; }
  [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr h);
  [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr h, IntPtr ctx);
  [DllImport("imm32.dll")] static extern bool ImmSetCompositionWindow(IntPtr ctx, ref COMPOSITIONFORM form);
  [DllImport("imm32.dll")] static extern bool ImmSetCandidateWindow(IntPtr ctx, ref CANDIDATEFORM form);
  [DllImport("user32.dll")] static extern IntPtr GetFocus();
  [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);

  void Defer(Action action) {
   if (IsDisposed || Disposing || !IsHandleCreated) return;
   try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && !Disposing) action(); }); } catch (InvalidOperationException) { }
  }

  void AddMenu(ContextMenuStrip menu, string title, Action action) {
   menu.Items.Add(title, null, delegate { Defer(action); });
  }

  void ShowMenu(FolderTile tile, Point point) {
   if (tile == null || tile.Plus) return;
   string id = tile.Id;
   int index = tile.Index;
   var menu = new ContextMenuStrip();
   PanelDrawing.StyleMenu(menu);
   menus.Add(menu);
   AddMenu(menu, "打开", delegate {
    RectangleF icon = IconRect(tile);
    app.OpenGroup(id, PointToScreen(new Point((int)(icon.X + icon.Width / 2), (int)(VisualBounds(tile).Y + IconTop + IconSize / 2))));
   });
   AddMenu(menu, "重命名", delegate {
    RectangleF icon = IconRect(tile);
    app.RenameGroupFromDesktop(id, PointToScreen(new Point((int)(icon.X + icon.Width / 2), (int)(VisualBounds(tile).Y + IconTop + IconSize / 2))));
   });
   AddMenu(menu, "新建文件夹", delegate {
    Rectangle r = VisibleBounds(tile);
    app.CreateAndOpenNewGroup(PointToScreen(new Point(r.Left + r.Width / 2, r.Top + IconTop + IconSize / 2)));
   });
   menu.Items.Add(new ToolStripSeparator());
   AddMenu(menu, "前移", delegate { app.TryMoveGroup(id, Math.Max(0, index - 1)); });
   AddMenu(menu, "后移", delegate { app.TryMoveGroup(id, index + 2); });
   menu.Items.Add(new ToolStripSeparator());
   menu.Items.Add("移除文件夹", null, delegate { Defer(delegate { app.DeleteGroup(id); }); }).Tag = PanelDrawing.Destructive;
   AddMenu(menu, "退出 MacDesk", delegate { app.Quit(); });
   menu.Closed += delegate { Defer(delegate { menus.Remove(menu); menu.Dispose(); }); };
   try { app.ShowDesktopMenu(menu, PointToScreen(point)); } catch { menus.Remove(menu); menu.Dispose(); throw; }
  }

  // --- Drag & drop from Explorer / other folders ----------------------------------------

  Payload ReadPayload(IDataObject data, DragDropEffects allowed) {
   if (data == null || allowed == DragDropEffects.None) return null;
   if (data.GetDataPresent(ItemFormat)) {
    if ((allowed & DragDropEffects.Move) == 0) return null;
    string value = data.GetData(ItemFormat) as string;
    if (value == null) return null;
    var parts = value.Split('\n');
    if (parts.Length != 2) return null;
    foreach (var group in app.Data.groups)
     if (group.id == parts[0])
      foreach (var item in group.items)
       if (item.id == parts[1])
        return new Payload { Source = parts[0], Item = parts[1], Effect = DragDropEffects.Move };
    return null;
   }
   if (data.GetDataPresent(DataFormats.FileDrop)) {
    if ((allowed & DragDropEffects.Link) == 0) return null;
    var paths = data.GetData(DataFormats.FileDrop) as string[];
    if (paths == null || paths.Length == 0) return null;
    foreach (var path in paths) if (String.IsNullOrWhiteSpace(path)) return null;
    return new Payload { Paths = (string[])paths.Clone(), Effect = DragDropEffects.Link };
   }
   if (data.GetDataPresent(DataFormats.UnicodeText)) {
    var effect = (allowed & DragDropEffects.Link) != 0 ? DragDropEffects.Link : (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    if (effect == DragDropEffects.None) return null;
    string text = data.GetData(DataFormats.UnicodeText) as string;
    Uri uri;
    if (text != null && Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
     return new Payload { Url = uri.AbsoluteUri, Effect = effect };
   }
   return null;
  }

  protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); OnDragOver(e); }
  protected override void OnDragOver(DragEventArgs e) {
   base.OnDragOver(e);
   e.Effect = DragDropEffects.None;
   try {
    var tile = Hit(PointToClient(new Point(e.X, e.Y)));
    var payload = tile == null || tile.Plus ? null : ReadPayload(e.Data, e.AllowedEffect);
    dropTarget = payload == null ? null : tile;
    if (payload != null) e.Effect = payload.Effect;
   } catch (Exception) { dropTarget = null; }
   WakeAnim();
  }
  protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); dropTarget = null; WakeAnim(); }
  protected override void OnDragDrop(DragEventArgs e) {
   base.OnDragDrop(e);
   e.Effect = DragDropEffects.None;
   var target = dropTarget;
   dropTarget = null;
   try {
    var tile = Hit(PointToClient(new Point(e.X, e.Y)));
    var payload = tile == null || tile.Plus ? null : ReadPayload(e.Data, e.AllowedEffect);
    if (payload == null) return;
    bool saved = payload.Item != null ? app.TryMoveItem(payload.Source, payload.Item, tile.Id, -1) : payload.Paths != null ? app.TryAddPaths(tile.Id, payload.Paths, -1) : app.TryAddUrl(tile.Id, payload.Url, -1);
    if (saved) { e.Effect = payload.Effect; tile.Scale.Velocity -= 2.0; } // little "gulp" as it lands
   } catch (Exception ex) { Defer(delegate { app.ReportError(ex); }); } finally { WakeAnim(); }
  }

  protected override void Dispose(bool disposing) {
   if (disposing) {
    Wallpaper.Changed -= OnWallpaperChanged;
    pump.Dispose();
    foreach (var menu in menus) menu.Dispose();
    menus.Clear();
    foreach (var f in faces.Values) f.Dispose();
    faces.Clear();
    foreach (var t in tiles) t.DisposeLabel();
    wallpaperGlass.Dispose();
    spriteAttributes.Dispose();
    surface.Dispose();
    dirtyClip.Dispose();
    caption.Dispose();
   }
   base.Dispose(disposing);
  }

  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll", SetLastError = true)] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
 }
}
