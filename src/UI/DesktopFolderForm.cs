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
  public const int TileWidth = 112, TileHeight = 130, StepX = 132, StepY = 148, LeftReserve = 440, EdgePad = 24;
  public const int IconSize = 80;
  public const float IconRadius = IconSize * 0.2237f;
  const int IconTop = 6;
  const string ItemFormat = "MacDeskNative.Item";
  readonly AppController app;
  readonly List<FolderTile> tiles = new List<FolderTile>();
  readonly HashSet<ContextMenuStrip> menus = new HashSet<ContextMenuStrip>();
  readonly Font caption = Ui.MakeFont(Ui.Cjk, 12.5f, FontStyle.Regular);
  readonly LayeredSurface surface = new LayeredSurface();
  readonly FramePump pump;
  readonly Spring ghostAlpha = new Spring(0, 0.18, 1.0);
  readonly Dictionary<string, Bitmap> faces = new Dictionary<string, Bitmap>();

  TextureBrush wallpaperBrush;
  Backdrop wallpaperSource;
  Point wallpaperOrigin;

  FolderTile hover, pressed, dropTarget;
  Point down, grabOffset, mouseClient = new Point(-999, -999);
  Rectangle magneticPreview = Rectangle.Empty, ghostDrawn = Rectangle.Empty;
  bool dragging, rendering, desktopChildMode, fullRedraw = true;
  int scroll, contentHeight;

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
   public bool Placed;
   // Pre-rendered folder (shadow + frosted body + 3x3 preview + rim) at 2x, and its label.
   public Bitmap Sprite, LabelSprite;
   public string SpriteKey, LabelKey;
   public void DisposeSprites() {
    if (Sprite != null) { Sprite.Dispose(); Sprite = null; }
    if (LabelSprite != null) { LabelSprite.Dispose(); LabelSprite = null; }
    SpriteKey = LabelKey = null;
   }
   public IEnumerable<Spring> Springs { get { yield return X; yield return Y; yield return Scale; yield return Hover; yield return Lift; yield return Alpha; } }
  }
  sealed class Payload { public string Source, Item, Url; public string[] Paths; public DragDropEffects Effect; }

  public DesktopFolderForm(AppController controller) {
   if (controller == null) throw new ArgumentNullException("controller");
   app = controller;
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

  public Dictionary<string, Point> SnapshotPositions() {
   var positions = new Dictionary<string, Point>();
   foreach (var tile in tiles) if (!tile.Plus) positions[tile.Id] = tile.Bounds.Location;
   return positions;
  }

  public static Point SnapPointToGrid(int x, int y, int leftOrigin, int clientWidth, int clientHeight) {
   int left = Math.Min(leftOrigin, Math.Max(8, clientWidth - TileWidth - EdgePad));
   int col = Math.Max(0, (int)Math.Round((double)(x - left) / StepX));
   int row = Math.Max(0, (int)Math.Round((double)(y - EdgePad) / StepY));
   int snappedX = Math.Max(left, Math.Min(Math.Max(left, clientWidth - TileWidth - EdgePad), left + col * StepX));
   int snappedY = Math.Max(EdgePad, Math.Min(Math.Max(EdgePad, clientHeight - TileHeight - EdgePad), EdgePad + row * StepY));
   return new Point(snappedX, snappedY);
  }

  int DynamicLeftOrigin() {
   int reserve = app != null && app.Home != null && !app.Home.IsDisposed ? Math.Max(280, app.Home.Right + 28) : LeftReserve;
   return Math.Min(reserve, Math.Max(8, ClientSize.Width - TileWidth - EdgePad));
  }

  Rectangle ComputeMagneticSlot(FolderTile movingTile, Point rawLocation) {
   int left = DynamicLeftOrigin();
   int columns = Math.Max(1, (ClientSize.Width - left - EdgePad + StepX - TileWidth) / StepX);
   Point snapped = SnapPointToGrid(rawLocation.X, rawLocation.Y, left, ClientSize.Width, ClientSize.Height);
   var candidate = new Rectangle(snapped.X, snapped.Y, TileWidth, TileHeight);
   bool conflict = false;
   foreach (var other in tiles) {
    if (other == movingTile || other.Plus) continue;
    if (other.Bounds.IntersectsWith(candidate)) { conflict = true; break; }
   }
   if (!conflict) return candidate;
   int bestDist = int.MaxValue;
   Rectangle best = candidate;
   for (int row = 0; row < 12; row++) {
    for (int col = 0; col < columns; col++) {
     var slotRect = new Rectangle(left + col * StepX, EdgePad + row * StepY, TileWidth, TileHeight);
     bool hit = false;
     foreach (var other in tiles) {
      if (other == movingTile || other.Plus) continue;
      if (other.Bounds.IntersectsWith(slotRect)) { hit = true; break; }
     }
     if (!hit) {
      int dx = slotRect.X - rawLocation.X, dy = slotRect.Y - rawLocation.Y;
      int dist = dx * dx + dy * dy;
      if (dist < bestDist) { bestDist = dist; best = slotRect; }
     }
    }
   }
   return best;
  }

  public void RefreshData() {
   if (IsDisposed || Disposing) return;
   FitHost();
   LayoutTiles();
   fullRedraw = true;
   Render();
   WakeAnim();
  }

  void LayoutTiles() {
   var previous = new Dictionary<string, FolderTile>();
   foreach (var t in tiles) previous[t.Plus ? "+" : t.Id] = t;
   tiles.Clear();
   int left = DynamicLeftOrigin();
   int columns = Math.Max(1, (ClientSize.Width - left - EdgePad + StepX - TileWidth) / StepX);
   var occupied = new List<Rectangle>();
   foreach (var group in app.Data.groups)
    if (group.desktopX >= 0 && group.desktopY >= 0)
     occupied.Add(new Rectangle(Math.Max(EdgePad, Math.Min(ClientSize.Width - TileWidth, group.desktopX)), Math.Max(EdgePad, group.desktopY), TileWidth, TileHeight));

   int slot = 0, index = 0;
   contentHeight = 0;
   var liveFaces = new HashSet<string>();
   foreach (var group in app.Data.groups) {
    Rectangle bounds;
    if (group.desktopX >= 0 && group.desktopY >= 0)
     bounds = new Rectangle(Math.Max(EdgePad, Math.Min(ClientSize.Width - TileWidth, group.desktopX)), Math.Max(EdgePad, group.desktopY), TileWidth, TileHeight);
    else
     bounds = NextSlot(left, columns, ref slot, occupied);
    FolderTile tile;
    if (!previous.TryGetValue(group.id, out tile)) tile = new FolderTile();
    tile.Id = group.id; tile.Title = group.title; tile.Group = group; tile.Bounds = bounds; tile.Index = index++; tile.Plus = false;
    tile.FaceKey = FaceKey(group);
    liveFaces.Add(tile.FaceKey);
    Place(tile, bounds);
    tiles.Add(tile);
    contentHeight = Math.Max(contentHeight, bounds.Bottom + EdgePad);
   }
   Rectangle add = NextSlot(left, columns, ref slot, occupied);
   FolderTile plus;
   if (!previous.TryGetValue("+", out plus)) { plus = new FolderTile(); plus.Alpha.Snap(0.0); }
   plus.Id = null; plus.Title = "新建"; plus.Plus = true; plus.Group = null; plus.Bounds = add; plus.Index = index;
   Place(plus, add);
   tiles.Add(plus);
   contentHeight = Math.Max(contentHeight, add.Bottom + EdgePad);
   scroll = Math.Max(0, Math.Min(scroll, Math.Max(0, contentHeight - ClientSize.Height)));
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
   for (int i = 0; i < Math.Min(9, g.items.Count); i++) sb.Append('|').Append(g.items[i].id).Append(g.items[i].target).Append(g.items[i].icon);
   return sb.ToString();
  }

  static Rectangle NextSlot(int left, int columns, ref int slot, List<Rectangle> occupied) {
   for (; ; ) {
    int current = slot++;
    var r = new Rectangle(left + (current % columns) * StepX, EdgePad + (current / columns) * StepY, TileWidth, TileHeight);
    bool overlaps = false;
    foreach (var other in occupied) if (r.IntersectsWith(other)) { overlaps = true; break; }
    if (!overlaps) { occupied.Add(r); return r; }
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
   var stat = MacDeskNative.Bench.Time("desktop folder hover in/out", Math.Max(1, folders.Count) * 48, delegate {
    int i = k++;
    hover = (i / 24) % 2 == 0 && folders.Count > 0 ? folders[(i / 48) % folders.Count] : null;
    Tick(1 / 240.0);
   });
   return new[] { stat };
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
     tile.Alpha.Target = (app.Data.groups.Count == 0 || tile == hover) ? 1.0 : (dist < 150 ? 0.15 + 0.85 * (1 - dist / 150) : 0.0);
    }
    bool moving = false;
    foreach (var s in tile.Springs) moving |= s.Step(dt);
    if (moving) active = true;
   }
   ghostAlpha.Target = dragging && !magneticPreview.IsEmpty ? 1 : 0;
   bool ghostMoving = ghostAlpha.Step(dt);
   active |= ghostMoving;
   Render();
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

  // Everything a tile can paint (shadow, scaled icon, label), used for dirty tracking.
  Rectangle PaintBounds(FolderTile tile) {
   RectangleF b = VisualBounds(tile);
   return Rectangle.FromLTRB((int)Math.Floor(b.X - 16), (int)Math.Floor(b.Y - 14), (int)Math.Ceiling(b.Right + 16), (int)Math.Ceiling(b.Bottom + 12));
  }

  FolderTile Hit(Point point) {
   for (int i = tiles.Count - 1; i >= 0; i--) {
    var t = tiles[i];
    if (t.Plus && t.Alpha.Value < 0.08 && app.Data.groups.Count > 0) continue;
    var b = VisualBounds(t);
    var r = new RectangleF(b.X + 8, b.Y, TileWidth - 16, TileHeight - 6);
    if (r.Contains(point)) return t;
   }
   return null;
  }

  // --- Rendering ------------------------------------------------------------------------

  TextureBrush WallpaperBrush() {
   Backdrop bd = Wallpaper.Current;
   if (bd == null) return null;
   Point origin = IsHandleCreated ? PointToScreen(Point.Empty) : Location;
   if (wallpaperBrush == null || wallpaperSource != bd || wallpaperOrigin != origin) {
    if (wallpaperBrush != null) wallpaperBrush.Dispose();
    wallpaperBrush = bd.CreateBrush(origin);
    wallpaperSource = bd;
    wallpaperOrigin = origin;
   }
   return wallpaperBrush;
  }

  void OnWallpaperChanged(object sender, EventArgs e) {
   if (IsDisposed) return;
   fullRedraw = true;
   if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) Render(); });
  }

  // The 3x3 preview (icons only, transparent), cached per folder content.
  Bitmap Face(FolderTile tile) {
   Bitmap face;
   if (tile.FaceKey != null && faces.TryGetValue(tile.FaceKey, out face)) return face;
   face = new Bitmap(IconSize * 2, IconSize * 2, PixelFormat.Format32bppPArgb); // 2x for crisp scaling
   using (var g = Graphics.FromImage(face)) {
    g.Clear(Color.Transparent);
    Ui.PrepareQuality(g);
    DrawMiniGrid(g, tile.Group, new RectangleF(0, 0, face.Width, face.Height));
   }
   if (tile.FaceKey != null) faces[tile.FaceKey] = face;
   return face;
  }

  static void DrawMiniGrid(Graphics g, DeskGroup grp, RectangleF box) {
   if (grp == null) return;
   int count = Math.Min(9, grp.items.Count);
   float pad = box.Width * 0.135f, gap = box.Width * 0.055f;
   float cell = (box.Width - pad * 2 - gap * 2) / 3f;
   for (int i = 0; i < count; i++) {
    try {
     Image image = IconProvider.Get(grp.items[i]);
     if (image == null) continue;
     var dest = new RectangleF(box.X + pad + (i % 3) * (cell + gap), box.Y + pad + (i / 3) * (cell + gap), cell, cell);
     g.DrawImage(image, dest);
    } catch (Exception) { }
   }
  }

  // Folder face exactly as on the desktop, for the first frame of the open morph.
  internal Bitmap RenderTilePreview(string groupId) {
   FolderTile target = null;
   foreach (var t in tiles) if (!t.Plus && t.Id == groupId) { target = t; break; }
   if (target == null || target.Group == null) return null;
   var bmp = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppPArgb);
   using (var g = Graphics.FromImage(bmp)) {
    g.Clear(Color.Transparent);
    Ui.PrepareQuality(g);
    Rectangle vis = VisibleBounds(target);
    var icon = new RectangleF(vis.X + (TileWidth - IconSize) / 2f, vis.Y + IconTop, IconSize, IconSize);
    g.TranslateTransform(-icon.X, -icon.Y);
    DrawFolderBody(g, target, icon, 0f, 1f);
   }
   return bmp;
  }

  void DrawFolderBody(Graphics g, FolderTile tile, RectangleF icon, float hover, float alpha) {
   float radius = icon.Width * 0.2237f;
   using (var shape = Ui.Squircle(icon, radius)) {
    var brush = WallpaperBrush();
    Color tint = Color.FromArgb((int)((Material.FolderTint.A + 26 * hover) * alpha), 255, 255, 255);
    if (alpha >= 0.99f) Material.Fill(g, shape, brush, tint, Material.FolderFallback);
    else using (var faded = new SolidBrush(Color.FromArgb((int)(70 * alpha), 255, 255, 255))) g.FillPath(faded, shape);
   }
   Material.Rim(g, icon, radius, (0.75f + 0.25f * hover) * alpha);
   if (!tile.Plus) {
    var face = Face(tile);
    g.DrawImage(face, icon);
   }
  }

  const int SpritePad = 12;
  readonly System.Drawing.Imaging.ImageAttributes spriteAttributes = new System.Drawing.Imaging.ImageAttributes();
  readonly System.Drawing.Imaging.ColorMatrix spriteMatrix = new System.Drawing.Imaging.ColorMatrix();

  // Renders the whole resting folder once; hover/press frames only scale this bitmap.
  Bitmap Sprite(FolderTile tile) {
   if (dragging && tile == pressed && tile.Sprite != null) return tile.Sprite; // keep the material while carried
   Backdrop bd = Wallpaper.Current;
   float restX = tile.Bounds.X + (TileWidth - IconSize) / 2f, restY = tile.Bounds.Y + IconTop - scroll;
   string key = (tile.Plus ? "+" : tile.FaceKey) + "|" + restX + "," + restY + "|" + (bd == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bd)) + "|" + (IsHandleCreated ? PointToScreen(Point.Empty) : Location);
   if (tile.Sprite != null && tile.SpriteKey == key) return tile.Sprite;
   if (tile.Sprite != null) tile.Sprite.Dispose();
   int side = (IconSize + SpritePad * 2) * 2;
   var bmp = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
   using (var g = Graphics.FromImage(bmp)) {
    g.Clear(Color.Transparent);
    Ui.PrepareQuality(g);
    // Draw in window coordinates so the screen-aligned wallpaper material lines up exactly.
    g.ScaleTransform(2f, 2f);
    g.TranslateTransform(SpritePad - restX, SpritePad - restY);
    var icon = new RectangleF(restX, restY, IconSize, IconSize);
    float radius = IconRadius;
    for (int i = 0; i < 3; i++) {
     float spread = 1.5f + i * 2.2f;
     var r = new RectangleF(icon.X - spread * 0.5f, icon.Y + 2.5f - spread * 0.2f, icon.Width + spread, icon.Height + spread);
     using (var path = Ui.Squircle(r, radius + spread * 0.5f))
     using (var b = new SolidBrush(Color.FromArgb(16 - i * 4, 0, 0, 0))) g.FillPath(b, path);
    }
    if (tile.Plus) {
     using (var shape = Ui.Squircle(icon, radius))
     using (var b = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillPath(b, shape);
     Material.Rim(g, icon, radius, 0.8f);
     float arm = icon.Width * 0.19f, cx = icon.X + icon.Width / 2f, cy = icon.Y + icon.Height / 2f;
     using (var pen = new Pen(Color.FromArgb(240, 255, 255, 255), 3f)) {
      pen.StartCap = pen.EndCap = LineCap.Round;
      g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
      g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
     }
    } else {
     DrawFolderBody(g, tile, icon, 0f, 1f);
    }
   }
   tile.Sprite = bmp;
   tile.SpriteKey = key;
   return bmp;
  }

  Bitmap LabelSprite(FolderTile tile) {
   string key = tile.Title ?? "";
   if (tile.LabelSprite != null && tile.LabelKey == key) return tile.LabelSprite;
   if (tile.LabelSprite != null) tile.LabelSprite.Dispose();
   var bmp = new Bitmap(TileWidth + 16 + 8, 28, PixelFormat.Format32bppPArgb);
   using (var g = Graphics.FromImage(bmp))
   using (var f = Ui.TopCentered()) {
    g.Clear(Color.Transparent);
    Ui.PrepareQuality(g);
    Ui.ShadowText(g, key, caption, Ui.Label, new RectangleF(4, 4, TileWidth + 16, 20), f, 1f);
   }
   tile.LabelSprite = bmp;
   tile.LabelKey = key;
   return bmp;
  }

  void DrawTile(Graphics g, FolderTile tile) {
   float alpha = (float)Math.Max(0, Math.Min(1, tile.Alpha.Value));
   if (alpha <= 0.02f) return;
   RectangleF icon = IconRect(tile);
   float s = icon.Width / IconSize;
   float lift = tile.Lift.F, hover = (float)Math.Max(0, Math.Min(1, tile.Hover.Value));

   if (lift > 0.01f) {
    // Extra, larger shadow only while the folder is being carried.
    for (int i = 0; i < 2; i++) {
     float spread = 4f + i * 5f + lift * 6f;
     var r = new RectangleF(icon.X - spread * 0.5f, icon.Y + lift * 8f, icon.Width + spread, icon.Height + spread * 0.7f);
     using (var path = Ui.Squircle(r, IconRadius * s + spread * 0.5f))
     using (var b = new SolidBrush(Color.FromArgb((int)((10 + i * 4) * lift * alpha), 0, 0, 0))) g.FillPath(b, path);
    }
   }

   Bitmap sprite = Sprite(tile);
   float pad = SpritePad * s;
   var dest = new RectangleF(icon.X - pad, icon.Y - pad, icon.Width + pad * 2, icon.Height + pad * 2);
   var im = g.InterpolationMode;
   g.InterpolationMode = InterpolationMode.HighQualityBilinear;
   if (alpha >= 0.995f) g.DrawImage(sprite, dest);
   else {
    spriteMatrix.Matrix33 = alpha;
    spriteAttributes.SetColorMatrix(spriteMatrix);
    g.DrawImage(sprite, new[] { new PointF(dest.Left, dest.Top), new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) }, new RectangleF(0, 0, sprite.Width, sprite.Height), GraphicsUnit.Pixel, spriteAttributes);
   }
   g.InterpolationMode = im;

   if (hover > 0.01f && !tile.Plus) {
    // Hover just brightens the glass a touch; one cheap translucent fill.
    using (var shape = Ui.Squircle(icon, IconRadius * s))
    using (var b = new SolidBrush(Color.FromArgb((int)(30 * hover * alpha), 255, 255, 255))) g.FillPath(b, shape);
   }

   RectangleF b2 = VisualBounds(tile);
   Ui.Blit(g, LabelSprite(tile), (int)Math.Round(b2.X - 12), (int)Math.Round(b2.Y + IconTop + IconSize + 3 + lift * 2f), alpha, spriteAttributes, spriteMatrix);
  }

  void Render() {
   if (IsDisposed || Disposing || rendering || !IsHandleCreated || ClientSize.Width < 1 || ClientSize.Height < 1) return;
   rendering = true;
   try {
    var g = surface.Begin(ClientSize);
    bool full = fullRedraw || surface.Size != ClientSize;
    Rectangle dirty = Rectangle.Empty;
    var current = new Dictionary<FolderTile, Rectangle>();
    foreach (var tile in tiles) {
     Rectangle now = PaintBounds(tile);
     current[tile] = now;
     if (!full && (now != tile.Drawn || !AllSettled(tile))) dirty = Union(Union(dirty, tile.Drawn), now);
    }
    Rectangle ghost = magneticPreview;
    if (!ghost.IsEmpty) ghost.Offset(0, -scroll);
    if (!ghost.IsEmpty) ghost.Inflate(6, 6);
    if (!full && (ghost != ghostDrawn || !ghostAlpha.Settled)) dirty = Union(Union(dirty, ghostDrawn), ghost);
    if (full) dirty = new Rectangle(Point.Empty, ClientSize);
    dirty = Rectangle.Intersect(dirty, new Rectangle(Point.Empty, ClientSize));
    if (dirty.Width <= 0 || dirty.Height <= 0) return;

    g.SetClip(dirty);
    surface.Clear(dirty);

    if (ghostAlpha.Value > 0.01 && !magneticPreview.IsEmpty) {
     var slot = magneticPreview; slot.Offset(0, -scroll);
     var gi = new RectangleF(slot.X + (TileWidth - IconSize) / 2f, slot.Y + IconTop, IconSize, IconSize);
     using (var path = Ui.Squircle(gi, IconRadius))
     using (var b = new SolidBrush(Color.FromArgb((int)(46 * ghostAlpha.Value), 255, 255, 255)))
     using (var pen = new Pen(Color.FromArgb((int)(90 * ghostAlpha.Value), 255, 255, 255), 1f)) { g.FillPath(b, path); g.DrawPath(pen, path); }
    }
    ghostDrawn = ghost;

    FolderTile top = null;
    foreach (var tile in tiles) {
     tile.Drawn = current[tile];
     if (dragging && tile == pressed) { top = tile; continue; }
     if (current[tile].IntersectsWith(dirty)) DrawTile(g, tile);
    }
    if (top != null) DrawTile(g, top);

    if (contentHeight > ClientSize.Height) {
     var marker = new RectangleF(ClientSize.Width - 46, ClientSize.Height - 40, 30, 30);
     using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillEllipse(b, marker);
     using (var f = Ui.Centered(false)) using (var b = new SolidBrush(Color.White)) g.DrawString("↕", caption, b, marker, f);
    }
    g.ResetClip();
    surface.Present(Handle, desktopChildMode ? (Point?)null : Location, 255, full ? (Rectangle?)null : dirty);
    fullRedraw = false;
   } finally { rendering = false; }
  }

  static bool AllSettled(FolderTile t) { foreach (var s in t.Springs) if (!s.Settled) return false; return true; }
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
    if (!threshold.Contains(e.Location)) dragging = true;
    if (dragging) {
     int rawX = Math.Max(0, Math.Min(Width - TileWidth, e.X - grabOffset.X));
     int rawY = Math.Max(0, e.Y - grabOffset.Y + scroll);
     // The icon sticks to the finger while dragging; springs take over again on release.
     pressed.X.Snap(rawX); pressed.Y.Snap(rawY);
     pressed.Bounds = new Rectangle(Math.Max(EdgePad, rawX), Math.Max(EdgePad, rawY), TileWidth, TileHeight);
     bool freeMove = (Control.ModifierKeys & Keys.Alt) != 0;
     magneticPreview = freeMove ? Rectangle.Empty : ComputeMagneticSlot(pressed, pressed.Bounds.Location);
     Cursor = Cursors.SizeAll;
     WakeAnim();
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
   Rectangle snapTarget = magneticPreview;
   pressed = null;
   dragging = false;
   magneticPreview = Rectangle.Empty;
   Capture = false;
   Cursor = Cursors.Default;
   if (e.Button == MouseButtons.Right) { ShowMenu(Hit(e.Location), e.Location); return; }
   if (e.Button != MouseButtons.Left || tile == null) { WakeAnim(); return; }
   if (moved) {
    string id = tile.Id;
    bool freeMove = (Control.ModifierKeys & Keys.Alt) != 0;
    Point position = (!freeMove && !snapTarget.IsEmpty) ? snapTarget.Location : tile.Bounds.Location;
    tile.Bounds = new Rectangle(position, tile.Bounds.Size);
    tile.X.Target = position.X; tile.Y.Target = position.Y; // spring into the slot
    Defer(delegate { app.SetFolderPosition(id, position.X, position.Y); });
   } else if (Hit(e.Location) == tile) {
    tile.Scale.Velocity += 1.2; // release "pop"
    RectangleF icon = IconRect(tile);
    Point center = PointToScreen(new Point((int)Math.Round(icon.X + icon.Width / 2f), (int)Math.Round(VisualBounds(tile).Y + IconTop + IconSize / 2f)));
    if (tile.Plus) Defer(delegate { app.CreateAndOpenNewGroup(center); });
    else { string id = tile.Id; Defer(delegate { app.OpenGroup(id, center); }); }
   }
   WakeAnim();
  }

  protected override void OnMouseCaptureChanged(EventArgs e) {
   base.OnMouseCaptureChanged(e);
   if (!Capture && pressed != null) {
    pressed = null;
    dragging = false;
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
   int next = Math.Max(0, Math.Min(Math.Max(0, contentHeight - Height), scroll - (e.Delta / 120) * StepY));
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
  internal void PlaceImeWindow(Point screen) {
   if (!IsHandleCreated) return;
   IntPtr ctx = ImmGetContext(Handle);
   if (ctx == IntPtr.Zero) return;
   try {
    Point p = PointToClient(screen);
    var comp = new COMPOSITIONFORM { dwStyle = 0x0002, x = p.X, y = p.Y }; // CFS_POINT
    ImmSetCompositionWindow(ctx, ref comp);
    var cand = new CANDIDATEFORM { dwIndex = 0, dwStyle = 0x0040, x = p.X, y = p.Y }; // CFS_CANDIDATEPOS
    ImmSetCandidateWindow(ctx, ref cand);
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
   AddMenu(menu, "重命名…", delegate { app.EditGroup(id); });
   AddMenu(menu, "新建文件夹", delegate {
    Rectangle r = VisibleBounds(tile);
    app.CreateAndOpenNewGroup(PointToScreen(new Point(r.Left + r.Width / 2, r.Top + IconTop + IconSize / 2)));
   });
   menu.Items.Add(new ToolStripSeparator());
   AddMenu(menu, "整理到网格", delegate {
    app.ResetFolderPositions();
   });
   AddMenu(menu, "前移", delegate { app.TryMoveGroup(id, Math.Max(0, index - 1)); });
   AddMenu(menu, "后移", delegate { app.TryMoveGroup(id, index + 2); });
   menu.Items.Add(new ToolStripSeparator());
   menu.Items.Add("删除文件夹（不删除原文件）…", null, delegate { Defer(delegate { app.DeleteGroup(id); }); }).Tag = PanelDrawing.Destructive;
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
    foreach (var t in tiles) t.DisposeSprites();
    spriteAttributes.Dispose();
    if (wallpaperBrush != null) { wallpaperBrush.Dispose(); wallpaperBrush = null; }
    surface.Dispose();
    caption.Dispose();
   }
   base.Dispose(disposing);
  }

  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll", SetLastError = true)] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
 }
}
