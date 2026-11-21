using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using MacDesk.Core;

namespace MacDeskNative {
 // An open folder, iOS style: big title above a frosted sheet, icons with labels underneath.
 // Per-pixel-alpha layered window rendered by us at display refresh rate (springs for hover,
 // press, reflow, scroll and reveal). Same material + same snapshot as GlassMotion, so the
 // hand-off from the morph to the live panel is invisible.
 public class PanelForm : Form {
  private const string ItemFormat = "MacDeskNative.Item";
  // Geometry (pixels).
  public const int TitleBand = 60;
  public const float SheetRadius = 34f;
  private const int PadX = 26, PadTop = 26, PadBottom = 22, CellW = 106, CellH = 112, GapX = 8, GapY = 10, IconPx = 64;
  private const int SearchH = 36, SearchGap = 14, PickerHeaderH = 34;

  private readonly AppController app;
  private readonly string groupId;
  private readonly List<PanelEntry> filtered = new List<PanelEntry>();
  private readonly List<TileView> tiles = new List<TileView>();
  private readonly HashSet<ContextMenuStrip> activeMenus = new HashSet<ContextMenuStrip>();
  private readonly Font titleFont = Ui.MakeFont(Ui.Cjk, 23f, FontStyle.Bold);
  private readonly Font labelFont = Ui.MakeFont(Ui.Cjk, 12.5f, FontStyle.Regular);
  private readonly Font subLabelFont = Ui.MakeFont(Ui.Cjk, 11f, FontStyle.Regular);
  private readonly Font searchFont = Ui.MakeFont(Ui.Cjk, 14.5f, FontStyle.Regular);
  private readonly Font buttonFont = Ui.MakeFont(Ui.Cjk, 14.5f, FontStyle.Regular);
  private readonly Font buttonBoldFont = Ui.MakeFont(Ui.Cjk, 14.5f, FontStyle.Bold);
  private readonly Font pickerTitleFont = Ui.MakeFont(Ui.Cjk, 15f, FontStyle.Bold);
  private readonly Font plusFont = Ui.MakeFont(Ui.TextRegular, 30f, FontStyle.Regular);

  private readonly LayeredSurface surface = new LayeredSurface();
  private readonly FramePump pump;
  private TextureBrush material;
  private Point materialOrigin;

  private DeskGroup group;
  private int gridColumns = 4, pendingMenuActions;
  private bool layoutBusy, draggingItem;
  private string selectedId;
  private bool backgroundDrop;

  private TileView hoveredTile, pressedTile;
  private Point mouseClient = new Point(-999, -999);
  private Point mouseDownPoint;
  private bool mouseArmed;
  private float contentHeight;
  private readonly Spring scroll = new Spring(0, 0.11, 1.0);
  private readonly Spring scrollbar = new Spring(0, 0.25, 1.0);
  private readonly Spring searchReveal = new Spring(0, 0.30, 0.9);
  private readonly Spring titleReveal = new Spring(1, 0.22, 1.0);
  private readonly Spring titleHover = new Spring(0, 0.18, 1.0);
  private readonly Spring dropGlow = new Spring(0, 0.2, 1.0);
  private int dragSourceTileIndex = -1, dragHoverInsertIndex = -1;
  private double scrollbarHold;

  private bool pickerMode;
  private bool searchActive;
  private string searchQuery = "";

  // In-panel text editing (rename). The panel never takes focus, so keys are routed here
  // from the desktop layer and the field is drawn by us, like the search field.
  private bool editing, editSelectAll;
  private string editText = "";
  private string renamingGroupId, renamingItemId;
  private bool renamingFolderTitle;

  private readonly Timer outsideClickTimer;
  private bool leftWasDown, rightWasDown;

  [DllImport("user32.dll")]
  private static extern short GetAsyncKeyState(int virtualKey);

  public string GroupId { get { return groupId; } }

  private sealed class TileView {
   public string Key;
   public PanelEntry Entry;
   public bool IsPlus;
   public int SlotIndex;
   public RectangleF Target;
   public bool Initialized;
   public readonly Spring X = new Spring(0, 0.38, 0.86);
   public readonly Spring Y = new Spring(0, 0.38, 0.86);
   public readonly Spring Scale = new Spring(1, 0.22, 0.86);
   public readonly Spring Select = new Spring(0, 0.18, 1.0);
   public readonly Spring Fade = new Spring(1, 0.2, 1.0);
   public IEnumerable<Spring> Springs { get { yield return X; yield return Y; yield return Scale; yield return Select; yield return Fade; } }
  }

  public static Rectangle SheetBounds(Rectangle window) { return new Rectangle(window.X, window.Y + TitleBand, window.Width, Math.Max(1, window.Height - TitleBand)); }

  public PanelForm(AppController app, string groupId) {
   this.app = app;
   this.groupId = groupId;
   AutoScaleMode = AutoScaleMode.None;
   FormBorderStyle = FormBorderStyle.None;
   ShowInTaskbar = false;
   StartPosition = FormStartPosition.Manual;
   ClientSize = new Size(560, 380);
   SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
   KeyPreview = true;
   AllowDrop = true;
   AccessibleName = "文件夹快捷入口面板";
   Text = "MacDesk 文件夹";

   AttachDrop(this);
   pump = new FramePump(this, Tick);

   RefreshData();
   ClientSize = GetExpandedSize(new Point(Left + Width / 2, Top + Height / 2));
   LayoutContent(true);

   outsideClickTimer = new Timer();
   outsideClickTimer.Interval = 30;
   outsideClickTimer.Tick += PollOutsideClick;
  }

  // Never activated: taking focus away from the desktop would make Windows leave
  // "show desktop" mode and pile every window back on top of the open folder.
  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams {
   get {
    var p = base.CreateParams;
    p.ExStyle = (p.ExStyle | 0x80000 | 0x80 | 0x8000000) & ~0x40000; // LAYERED | TOOLWINDOW | NOACTIVATE
    return p;
   }
  }
  protected override void WndProc(ref Message m) {
   if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // WM_MOUSEACTIVATE -> MA_NOACTIVATE
   base.WndProc(ref m);
  }

  internal void ResetTransientState() {
   CommitInlineRename(false);
   pickerMode = false;
   searchActive = false;
   searchQuery = "";
   searchReveal.Snap(0);
   hoveredTile = null;
   pressedTile = null;
   dragSourceTileIndex = -1;
   dragHoverInsertIndex = -1;
   scroll.Snap(0);
   scrollbar.Snap(0);
   titleReveal.Snap(1);
   foreach (var t in tiles) { t.Scale.Snap(1); t.Fade.Snap(1); }
  }

  internal void SetBackdrop(Backdrop backdrop) {
   if (material != null) { material.Dispose(); material = null; }
   if (backdrop != null && backdrop.Image != null) {
    material = new TextureBrush(backdrop.Image, WrapMode.Clamp);
    materialOrigin = backdrop.Area.Location;
   }
  }

  internal void PresentNow() { Render(); }
  internal void BeginReveal() { titleReveal.Snap(0); titleReveal.Target = 1; WakeAnimation(); }

  // --- Geometry -------------------------------------------------------------------------

  private int GridTop() {
   if (pickerMode) return TitleBand + PadTop + PickerHeaderH + 12 + SearchH + SearchGap;
   return TitleBand + PadTop + (int)Math.Round(searchReveal.Value * (SearchH + SearchGap));
  }
  private Rectangle GridViewport() {
   int top = GridTop();
   return new Rectangle(PadX, top, Math.Max(1, ClientSize.Width - PadX * 2), Math.Max(1, ClientSize.Height - top - PadBottom));
  }
  private Rectangle SheetRect() { return new Rectangle(0, TitleBand, ClientSize.Width, Math.Max(1, ClientSize.Height - TitleBand)); }
  private string measuredTitle;
  private int measuredTitleWidth;
  private static readonly Bitmap measureBitmap = new Bitmap(1, 1);
  private Rectangle TitleRect() {
   string t = TitleText();
   if (t != measuredTitle) {
    using (var g = Graphics.FromImage(measureBitmap)) measuredTitleWidth = (int)Math.Ceiling(g.MeasureString(t, titleFont).Width) + 28;
    measuredTitle = t;
   }
   int w = Math.Min(ClientSize.Width - 40, Math.Max(120, measuredTitleWidth));
   return new Rectangle((ClientSize.Width - w) / 2, 8, w, TitleBand - 18);
  }
  private Rectangle SearchRect() {
   int top = pickerMode ? TitleBand + PadTop + PickerHeaderH + 12 : TitleBand + PadTop - (int)Math.Round((1 - searchReveal.Value) * 10);
   return new Rectangle(PadX + 4, top, ClientSize.Width - PadX * 2 - 8, SearchH);
  }
  private Rectangle PickerDoneBounds() { return new Rectangle(PadX, TitleBand + PadTop - 4, 72, PickerHeaderH); }
  private Rectangle PickerFolderBounds() { return new Rectangle(ClientSize.Width - PadX - 72, TitleBand + PadTop - 4, 72, PickerHeaderH); }
  private Rectangle PickerFileBounds() { return new Rectangle(ClientSize.Width - PadX - 72 - 66, TitleBand + PadTop - 4, 64, PickerHeaderH); }

  private string TitleText() { return pickerMode ? "添加到「" + (group != null ? group.title : "") + "」" : (group != null ? group.title ?? "文件夹" : "文件夹"); }

  public Size GetExpandedSize(Point sourceCenter) {
   Rectangle area = Screen.FromPoint(sourceCenter).WorkingArea;
   int cells = pickerMode ? Math.Max(12, filtered.Count) : (group != null ? group.items.Count : filtered.Count) + 1;
   int columns = pickerMode ? 6 : Math.Max(3, Math.Min(5, cells <= 4 ? Math.Max(3, cells) : (cells <= 9 ? 4 : 5)));
   int rows = Math.Max(1, (cells + columns - 1) / columns);
   int width = PadX * 2 + columns * CellW + (columns - 1) * GapX;
   int top = pickerMode ? PadTop + PickerHeaderH + 12 + SearchH + SearchGap : PadTop;
   int gridH = rows * CellH + (rows - 1) * GapY;
   int maxSheet = (int)(area.Height * 0.80) - TitleBand;
   int sheetH = Math.Min(top + gridH + PadBottom, maxSheet);
   if (pickerMode) sheetH = Math.Max(sheetH, Math.Min(maxSheet, 520));
   return new Size(Math.Min(width, area.Width - 48), TitleBand + Math.Max(PadTop + CellH + PadBottom, sheetH));
  }

  // --- Data -----------------------------------------------------------------------------

  public void RefreshData() {
   group = null;
   foreach (DeskGroup candidate in app.Data.groups)
    if (candidate.id == groupId) { group = candidate; break; }

   filtered.Clear();
   groupTargets.Clear();
   if (group != null) foreach (DeskItem it in group.items) if (it.target != null) groupTargets.Add(it.target);
   string query = (searchQuery ?? "").Trim();
   if (group != null) {
    if (pickerMode) {
     List<DeskItem> apps = EntryServices.DiscoverSystemApps();
     for (int i = 0; i < apps.Count; i++) {
      DeskItem item = apps[i];
      if (query.Length == 0 || Contains(item.title, query) || Contains(item.target, query))
       filtered.Add(new PanelEntry(item, i, group.id, group.title, false));
     }
    } else {
     for (int i = 0; i < group.items.Count; i++) {
      DeskItem item = group.items[i];
      if (query.Length == 0 || Contains(item.title, query) || Contains(item.description, query) || Contains(item.target, query))
       filtered.Add(new PanelEntry(item, i, group.id, group.title, false));
     }
     if (query.Length > 0) {
      foreach (DeskGroup other in app.Data.groups) {
       if (other.id == groupId) continue;
       for (int i = 0; i < other.items.Count; i++) {
        DeskItem item = other.items[i];
        if (Contains(item.title, query) || Contains(item.description, query) || Contains(item.target, query))
         filtered.Add(new PanelEntry(item, i, other.id, other.title, true));
       }
      }
     }
    }
    Text = "MacDesk · " + group.title;
    AccessibleName = group.title + " 文件夹快捷入口面板";
   }

   bool selectionVisible = false;
   foreach (PanelEntry entry in filtered) if (entry.Item.id == selectedId) selectionVisible = true;
   if (!selectionVisible) selectedId = query.Length > 0 && filtered.Count > 0 ? filtered[0].Item.id : null;

   SyncTileViews();
   LayoutContent(false);
   WakeAnimation();
  }

  private void SyncTileViews() {
   var old = new Dictionary<string, TileView>();
   foreach (var t in tiles) if (t.Key != null && !old.ContainsKey(t.Key)) old[t.Key] = t;
   tiles.Clear();
   for (int i = 0; i < filtered.Count; i++) {
    string key = (filtered[i].IsCrossGroup ? "x:" : "i:") + filtered[i].GroupId + "/" + filtered[i].Item.id;
    TileView t;
    if (!old.TryGetValue(key, out t)) t = new TileView { Key = key };
    old.Remove(key);
    t.Entry = filtered[i];
    t.IsPlus = false;
    t.SlotIndex = i;
    tiles.Add(t);
   }
   bool showPlus = !pickerMode && (searchQuery ?? "").Trim().Length == 0;
   if (showPlus) {
    TileView plus;
    if (!old.TryGetValue("+", out plus)) plus = new TileView { Key = "+" };
    plus.Entry = null;
    plus.IsPlus = true;
    plus.SlotIndex = tiles.Count;
    tiles.Add(plus);
   }
   if (hoveredTile != null && !tiles.Contains(hoveredTile)) hoveredTile = null;
   if (pressedTile != null && !tiles.Contains(pressedTile)) pressedTile = null;
  }

  private void LayoutContent(bool snapImmediately) {
   if (layoutBusy) return;
   layoutBusy = true;
   try {
    Rectangle view = GridViewport();
    gridColumns = Math.Max(1, (view.Width + GapX) / (CellW + GapX));
    int usedW = gridColumns * CellW + (gridColumns - 1) * GapX;
    float left = view.X + (view.Width - usedW) * 0.5f;
    int movable = 0;
    foreach (var t in tiles) if (!t.IsPlus) movable++;
    for (int i = 0; i < tiles.Count; i++) {
     TileView t = tiles[i];
     int slot = i;
     // Live reorder: neighbours slide out of the way while an item is dragged.
     if (!t.IsPlus && dragSourceTileIndex >= 0 && dragHoverInsertIndex >= 0 && dragSourceTileIndex < movable) {
      int insert = Math.Max(0, Math.Min(movable - 1, dragHoverInsertIndex));
      if (i == dragSourceTileIndex) slot = insert;
      else if (dragSourceTileIndex < insert && i > dragSourceTileIndex && i <= insert) slot = i - 1;
      else if (dragSourceTileIndex > insert && i >= insert && i < dragSourceTileIndex) slot = i + 1;
     }
     float x = left + (slot % gridColumns) * (CellW + GapX);
     float y = view.Y + (slot / gridColumns) * (CellH + GapY);
     t.Target = new RectangleF(x, y, CellW, CellH);
     t.X.Target = x; t.Y.Target = y;
     if (snapImmediately || !t.Initialized) { t.X.Snap(x); t.Y.Snap(y); t.Initialized = true; }
    }
    int rows = Math.Max(1, (tiles.Count + gridColumns - 1) / gridColumns);
    contentHeight = rows * (CellH + GapY) - GapY;
    float maxScroll = Math.Max(0f, contentHeight - view.Height);
    scroll.Target = Math.Max(0f, Math.Min(maxScroll, scroll.Target));
    if (snapImmediately) scroll.Snap(scroll.Target);
   } finally {
    layoutBusy = false;
   }
  }

  // --- Animation ------------------------------------------------------------------------

  private void WakeAnimation() {
   if (IsDisposed || Disposing || !IsHandleCreated) return;
   if (!Visible) return;
   pump.Start();
  }

  private bool Tick(double dt) {
   bool active = false;
   double searchBefore = searchReveal.Value;
   searchReveal.Target = (searchActive || pickerMode) && !pickerMode ? 1 : 0;
   active |= searchReveal.Step(dt);
   if (Math.Abs(searchReveal.Value - searchBefore) > 0.0001) LayoutContent(false);
   active |= scroll.Step(dt);
   if (scrollbarHold > 0) { scrollbarHold -= dt; scrollbar.Target = 1; active = true; } else scrollbar.Target = 0;
   active |= scrollbar.Step(dt);
   active |= titleReveal.Step(dt);
   titleHover.Target = !pickerMode && TitleRect().Contains(mouseClient) && !editing ? 1 : 0;
   active |= titleHover.Step(dt);
   dropGlow.Target = backgroundDrop ? 1 : 0;
   active |= dropGlow.Step(dt);

   for (int i = 0; i < tiles.Count; i++) {
    TileView t = tiles[i];
    bool selected = t.Entry != null && t.Entry.Item.id == selectedId && (searchActive || keyboardSelection);
    t.Scale.Target = t == pressedTile ? 0.86 : (t == hoveredTile ? 1.07 : 1.0);
    t.Select.Target = selected ? 1 : 0;
    t.Fade.Target = (draggingItem && i == dragSourceTileIndex) ? 0.35 : 1;
    foreach (var s in t.Springs) active |= s.Step(dt);
   }
   Render();
   return active;
  }
  private bool keyboardSelection;

  // Offscreen benchmark (MacDesk.exe --bench): wheel-scrolling the app picker, then a hover
  // sweep across the folder, using the real Tick/Render path.
  internal Bench.Stat[] Bench() {
   IntPtr h = Handle;
   const double dt = 1 / 240.0;
   EnterPickerMode();
   LayoutContent(true);
   Render();
   Rectangle view = GridViewport();
   float max = Math.Max(0f, contentHeight - view.Height);
   int frame = 0;
   var scrollStat = MacDeskNative.Bench.Time("picker wheel scroll", 240, delegate {
    if (frame++ % 12 == 0) scroll.Target = Math.Min(max, scroll.Target + 96);
    Tick(dt);
   });
   ExitPickerMode();
   var list = tiles.ToArray();
   int k = 0;
   var hoverStat = MacDeskNative.Bench.Time("panel hover sweep", list.Length * 16, delegate {
    hoveredTile = list[(k++ / 16) % list.Length];
    Tick(dt);
   });
   hoveredTile = null;
   return new[] { scrollStat, hoverStat };
  }

  // --- Rendering ------------------------------------------------------------------------

  private void Render() {
   if (IsDisposed || Disposing || !IsHandleCreated || ClientSize.Width < 2 || ClientSize.Height < 2) return;
   var g = surface.Begin(ClientSize);
   surface.Clear(new Rectangle(Point.Empty, ClientSize));
   Ui.PrepareQuality(g);
   RenderCanvas(g, false);
   surface.Present(Handle, null, 255, null);
  }

  // Sheet-sized snapshot of the icons + labels only (transparent), for the morph layer.
  internal Bitmap MotionSnapshot() {
   LayoutContent(true);
   Rectangle sheet = SheetRect();
   var result = new Bitmap(Math.Max(1, sheet.Width), Math.Max(1, sheet.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
   using (Graphics g = Graphics.FromImage(result)) {
    g.Clear(Color.Transparent);
    Ui.PrepareQuality(g);
    g.TranslateTransform(0, -TitleBand);
    g.SetClip(GridClip());
    DrawGrid(g, true);
   }
   return result;
  }
  private Rectangle GridClip() {
   Rectangle sheet = SheetRect(), view = GridViewport();
   return Rectangle.FromLTRB(sheet.Left + 6, view.Top - 6, sheet.Right - 6, sheet.Bottom - 8);
  }

  private void RenderCanvas(Graphics g, bool snapshot) {
   Rectangle sheet = SheetRect();
   DrawSheetBackground(g);
   if (dropGlow.Value > 0.01) {
    var r = new RectangleF(sheet.X + 1.5f, sheet.Y + 1.5f, sheet.Width - 3f, sheet.Height - 3f);
    using (var path = Ui.Squircle(r, SheetRadius - 1.5f))
    using (var pen = new Pen(Ui.WithAlpha(Ui.Blue, 0.9 * dropGlow.Value), 2.5f)) g.DrawPath(pen, path);
   }

   DrawTitle(g);
   if (pickerMode) DrawPickerHeader(g);
   if (pickerMode || searchReveal.Value > 0.02) DrawSearchField(g);

   Region oldClip = g.Clip;
   Rectangle view = GridViewport();
   // Let hover-scaled icons and labels breathe a little past the viewport edge.
   g.SetClip(GridClip());
   try { DrawGrid(g, false); } finally { g.Clip = oldClip; oldClip.Dispose(); }

   if (contentHeight > view.Height && scrollbar.Value > 0.02) {
    float ratio = (float)view.Height / contentHeight;
    float barH = Math.Max(30f, view.Height * ratio);
    float maxScroll = Math.Max(1f, contentHeight - view.Height);
    float barY = view.Y + (view.Height - barH) * (float)(scroll.Value / maxScroll);
    var bar = new RectangleF(ClientSize.Width - 11, barY, 4.5f, barH);
    using (var path = Ui.Squircle(bar, 2.25f))
    using (var brush = new SolidBrush(Color.FromArgb((int)(150 * scrollbar.Value), 255, 255, 255))) g.FillPath(brush, path);
   }
  }

  // --- Render caches: every frame only blits pre-rendered pieces (240 Hz budget is ~4 ms) ---

  private Bitmap sheetCache, titleCache;
  private Point sheetCacheOrigin;
  private TextureBrush sheetCacheMaterial;
  private string titleCacheText;
  // Shared by every folder panel (and the picker): an icon or label is rendered once per process.
  private static readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>();
  private static readonly Dictionary<string, Bitmap> labelCache = new Dictionary<string, Bitmap>();
  private readonly System.Drawing.Imaging.ImageAttributes blitAttributes = new System.Drawing.Imaging.ImageAttributes();
  private readonly System.Drawing.Imaging.ColorMatrix blitMatrix = new System.Drawing.Imaging.ColorMatrix();
  private readonly HashSet<string> groupTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

  // Renders every icon/label this folder (and optionally the app picker) will need, so the
  // first scroll or open never pays for icon extraction or text layout mid-animation.
  internal void WarmCaches(bool includePicker) {
   if (group != null) foreach (DeskItem item in group.items) { CachedIcon(item); CachedLabel(item.title, null, 1f); }
   CachedLabel("添加", null, 0.85f);
   if (includePicker) foreach (DeskItem item in EntryServices.DiscoverSystemApps()) { CachedIcon(item); CachedLabel(item.title, null, 1f); }
  }

  private void DrawSheetBackground(Graphics g) {
   Point screen = IsHandleCreated ? PointToScreen(Point.Empty) : Location;
   if (sheetCache == null || sheetCache.Size != ClientSize || sheetCacheOrigin != screen || sheetCacheMaterial != material) {
    if (sheetCache != null) sheetCache.Dispose();
    sheetCache = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
    using (var cg = Graphics.FromImage(sheetCache)) {
     cg.Clear(Color.Transparent);
     Ui.PrepareQuality(cg);
     // The transparent band above the sheet still catches clicks (alpha 1/255): clicking
     // there closes the folder instead of falling through to whatever is underneath.
     using (var catcher = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) cg.FillRectangle(catcher, 0, 0, ClientSize.Width, TitleBand);
     Rectangle sheet = SheetRect();
     using (var shape = Ui.Squircle(sheet, SheetRadius)) {
      if (material != null) {
       material.ResetTransform();
       material.TranslateTransform(materialOrigin.X - screen.X, materialOrigin.Y - screen.Y);
      }
      Material.Fill(cg, shape, material, Material.SheetTint, Material.SheetFallback);
     }
     Material.Rim(cg, sheet, SheetRadius, 1f);
    }
    sheetCacheOrigin = screen;
    sheetCacheMaterial = material;
   }
   var mode = g.CompositingMode;
   g.CompositingMode = CompositingMode.SourceCopy;
   Ui.Blit(g, sheetCache, 0, 0);
   g.CompositingMode = mode;
  }

  private Bitmap CachedIcon(DeskItem item) {
   string key = item.type + "|" + item.target + "|" + item.icon;
   Bitmap bmp;
   if (iconCache.TryGetValue(key, out bmp)) return bmp;
   Image source = null;
   try { source = IconProvider.Get(item); } catch { }
   if (source == null) return null;
   if (iconCache.Count > 400) ClearCache(iconCache);
   bmp = new Bitmap(IconPx, IconPx, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
   using (var cg = Graphics.FromImage(bmp)) {
    cg.Clear(Color.Transparent);
    Ui.PrepareQuality(cg);
    cg.DrawImage(source, new Rectangle(0, 0, IconPx, IconPx));
   }
   iconCache[key] = bmp;
   return bmp;
  }

  private Bitmap CachedLabel(string text, string sub, float bakedAlpha) {
   string key = (text ?? "") + "\u0001" + (sub ?? "") + "\u0001" + bakedAlpha.ToString("0.00");
   Bitmap bmp;
   if (labelCache.TryGetValue(key, out bmp)) return bmp;
   if (labelCache.Count > 400) ClearCache(labelCache);
   bmp = new Bitmap(CellW + 12, 36, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
   using (var cg = Graphics.FromImage(bmp))
   using (var f = Ui.TopCentered()) {
    cg.Clear(Color.Transparent);
    Ui.PrepareQuality(cg);
    var r = new RectangleF(8, 0, CellW - 4, 19);
    Ui.SoftText(cg, text ?? "", labelFont, Ui.Label, r, f, 0.95f * bakedAlpha);
    if (sub != null) using (var b = new SolidBrush(Ui.WithAlpha(Ui.SecondaryLabel, bakedAlpha))) cg.DrawString(sub, subLabelFont, b, new RectangleF(8, 19, CellW - 4, 15), f);
   }
   labelCache[key] = bmp;
   return bmp;
  }

  private static void ClearCache(Dictionary<string, Bitmap> cache) { foreach (var b in cache.Values) b.Dispose(); cache.Clear(); }

  private void Blit(Graphics g, Bitmap bmp, float x, float y, float alpha) {
   Ui.Blit(g, bmp, (int)Math.Round(x), (int)Math.Round(y), alpha, blitAttributes, blitMatrix);
  }


  private void DrawTitle(Graphics g) {
   if (editing && renamingFolderTitle) {
    Rectangle er = TitleRect();
    DrawEditField(g, new RectangleF(er.X - 14, er.Y + 1, er.Width + 28, er.Height), titleFont);
    return;
   }
   float a = (float)Math.Max(0, Math.Min(1, titleReveal.Value));
   if (a <= 0.01f) return;
   Rectangle r = TitleRect();
   if (titleHover.Value > 0.01) {
    using (var path = Ui.Squircle(r, r.Height / 2f))
    using (var brush = new SolidBrush(Color.FromArgb((int)(38 * titleHover.Value * a), 255, 255, 255))) g.FillPath(brush, path);
   }
   string text = TitleText();
   if (titleCache == null || titleCacheText != text || titleCache.Width != r.Width + 8) {
    if (titleCache != null) titleCache.Dispose();
    titleCache = new Bitmap(r.Width + 8, r.Height + 8, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
    using (var cg = Graphics.FromImage(titleCache)) using (var f = Ui.Centered(true)) {
     cg.Clear(Color.Transparent);
     Ui.PrepareQuality(cg);
     Ui.ShadowText(cg, text, titleFont, Ui.Label, new RectangleF(4, 4, r.Width, r.Height), f, 1f);
    }
    titleCacheText = text;
   }
   Blit(g, titleCache, r.X - 4, r.Y - 2 + (1 - a) * 8, a);
  }

  private void DrawPickerHeader(Graphics g) {
   Rectangle done = PickerDoneBounds(), file = PickerFileBounds(), folder = PickerFolderBounds();
   DrawTextButton(g, done, "完成", buttonBoldFont, Ui.Blue, done.Contains(mouseClient));
   DrawTextButton(g, file, "文件…", buttonBoldFont, Ui.Blue, file.Contains(mouseClient));
   DrawTextButton(g, folder, "文件夹…", buttonBoldFont, Ui.Blue, folder.Contains(mouseClient));
   var mid = new RectangleF(done.Right, done.Y, file.Left - done.Right, done.Height);
   using (var f = Ui.Centered(true)) using (var b = new SolidBrush(Ui.Label)) g.DrawString("选择应用", pickerTitleFont, b, mid, f);
  }

  private void DrawTextButton(Graphics g, Rectangle r, string text, Font font, Color color, bool hover) {
   if (hover) {
    using (var path = Ui.Squircle(r, 10))
    using (var b = new SolidBrush(Color.FromArgb(34, 255, 255, 255))) g.FillPath(b, path);
   }
   using (var f = Ui.Centered(false)) Ui.SoftText(g, text, font, Color.FromArgb(255, Math.Min(255, color.R + 60), Math.Min(255, color.G + 50), 255), r, f, 1f);
  }

  private void DrawSearchField(Graphics g) {
   float a = pickerMode ? 1f : (float)Math.Max(0, Math.Min(1, searchReveal.Value));
   Rectangle r = SearchRect();
   using (var path = Ui.Squircle(r, r.Height / 2f))
   using (var fill = new SolidBrush(Color.FromArgb((int)(96 * a), 0, 0, 0)))
   using (var fill2 = new SolidBrush(Color.FromArgb((int)(24 * a), 255, 255, 255))) {
    g.FillPath(fill, path);
    g.FillPath(fill2, path);
   }
   // Magnifier glyph.
   float cx = r.X + 20, cy = r.Y + r.Height / 2f - 1;
   using (var pen = new Pen(Color.FromArgb((int)(170 * a), 235, 235, 245), 1.8f)) {
    pen.StartCap = pen.EndCap = LineCap.Round;
    g.DrawEllipse(pen, cx - 6, cy - 6, 11, 11);
    g.DrawLine(pen, cx + 3.6f, cy + 3.6f, cx + 7.5f, cy + 7.5f);
   }
   var textRect = new RectangleF(r.X + 36, r.Y, r.Width - 48, r.Height);
   using (var f = Ui.Leading()) {
    if (searchQuery.Length == 0) {
     string hint = pickerMode ? "搜索应用，或粘贴路径 / 网址后回车" : "搜索（含其他文件夹）";
     using (var b = new SolidBrush(Ui.WithAlpha(Ui.SecondaryLabel, a))) g.DrawString(hint, searchFont, b, textRect, f);
    } else {
     using (var b = new SolidBrush(Ui.WithAlpha(Ui.Label, a))) g.DrawString(searchQuery, searchFont, b, textRect, f);
     float w = g.MeasureString(searchQuery, searchFont, new SizeF(textRect.Width, textRect.Height), StringFormat.GenericTypographic).Width;
     using (var caret = new Pen(Ui.WithAlpha(Ui.Blue, a), 2f)) g.DrawLine(caret, textRect.X + w + 3, r.Y + 9, textRect.X + w + 3, r.Bottom - 9);
    }
   }
  }

  private void DrawGrid(Graphics g, bool snapshot) {
   float scrollY = (float)scroll.Value;
   TileView top = null;
   for (int i = 0; i < tiles.Count; i++) {
    if (!snapshot && tiles[i] == pressedTile) { top = tiles[i]; continue; }
    DrawTile(g, tiles[i], snapshot, scrollY);
   }
   if (top != null) DrawTile(g, top, snapshot, scrollY);
  }

  private void DrawTile(Graphics g, TileView tile, bool snapshot, float scrollY) {
   float x = snapshot ? tile.Target.X : tile.X.F, y = (snapshot ? tile.Target.Y : tile.Y.F) - scrollY;
   if (y > ClientSize.Height || y + CellH < TitleBand) return;
   float scale = snapshot ? 1f : tile.Scale.F;
   float fade = snapshot ? 1f : Math.Max(0f, Math.Min(1f, tile.Fade.F));
   float size = IconPx * scale;
   var icon = new RectangleF(x + (CellW - size) / 2f, y + 8 + (IconPx - size) / 2f, size, size);
   float radius = size * 0.2237f;

   if (!snapshot && tile.Select.Value > 0.01) {
    var hl = new RectangleF(x + 4, y + 1, CellW - 8, CellH - 4);
    using (var path = Ui.Squircle(hl, 18))
    using (var b = new SolidBrush(Color.FromArgb((int)(40 * tile.Select.Value), 255, 255, 255))) g.FillPath(b, path);
   }

   if (tile.IsPlus) {
    using (var path = Ui.Squircle(icon, radius))
    using (var b = new SolidBrush(Color.FromArgb((int)(36 * fade), 255, 255, 255)))
    using (var pen = new Pen(Color.FromArgb((int)(70 * fade), 255, 255, 255), 1f)) { g.FillPath(b, path); g.DrawPath(pen, path); }
    float arm = size * 0.2f, cx = icon.X + icon.Width / 2, cy = icon.Y + icon.Height / 2;
    using (var pen = new Pen(Color.FromArgb((int)(235 * fade), 255, 255, 255), 2.6f)) {
     pen.StartCap = pen.EndCap = LineCap.Round;
     g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
     g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
    }
    DrawLabel(g, "添加", null, x, y + 8 + IconPx, fade, 0.85f);
    return;
   }

   PanelEntry entry = tile.Entry;
   if (entry == null) return;
   Bitmap image = CachedIcon(entry.Item);
   if (image != null) {
    if (Math.Abs(scale - 1f) < 0.004f) Blit(g, image, icon.X, icon.Y, fade);
    else {
     // Only the one or two tiles mid-spring take this path: resample the 128 px original so
     // a hover-enlarged icon stays crisp.
     Image source = null;
     try { source = IconProvider.Get(entry.Item); } catch { }
     if (source == null) source = image;
     if (fade >= 0.99f) g.DrawImage(source, icon);
     else using (var ia = Alpha(fade)) g.DrawImage(source, new[] { new PointF(icon.Left, icon.Top), new PointF(icon.Right, icon.Top), new PointF(icon.Left, icon.Bottom) }, new RectangleF(0, 0, source.Width, source.Height), GraphicsUnit.Pixel, ia);
    }
   }

   if (!snapshot && pickerMode) {
    bool inGroup = entry.Item.target != null && groupTargets.Contains(entry.Item.target);
    var badge = new RectangleF(icon.Right - 15, icon.Top - 5, 22, 22);
    if (inGroup) {
     using (var b = new SolidBrush(Ui.Blue)) g.FillEllipse(b, badge);
     using (var pen = new Pen(Color.White, 1.6f)) g.DrawEllipse(pen, badge);
     using (var pen = new Pen(Color.White, 2.2f)) {
      pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
      g.DrawLines(pen, new[] { new PointF(badge.X + 6.2f, badge.Y + 11.4f), new PointF(badge.X + 9.6f, badge.Y + 14.6f), new PointF(badge.X + 15.8f, badge.Y + 7.8f) });
     }
    } else {
     using (var b = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillEllipse(b, badge);
     using (var pen = new Pen(Color.FromArgb(210, 255, 255, 255), 1.5f)) g.DrawEllipse(pen, badge);
    }
   }

   if (!snapshot && editing && !renamingFolderTitle && entry.Item.id == renamingItemId && entry.GroupId == renamingGroupId) {
    DrawEditField(g, new RectangleF(x - 8, y + IconPx + 10, CellW + 16, 26), labelFont);
    return;
   }
   string sub = entry.IsCrossGroup ? entry.GroupTitle : null;
   DrawLabel(g, entry.Item.title, sub, x, y + 8 + IconPx, fade, 1f);
  }

  private void DrawLabel(Graphics g, string text, string sub, float cellX, float iconBottom, float alpha, float baked) {
   Blit(g, CachedLabel(text, sub, baked), cellX - 6, iconBottom + 6, alpha);
  }

  private static System.Drawing.Imaging.ImageAttributes Alpha(float a) {
   var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = Math.Max(0f, Math.Min(1f, a)) };
   var ia = new System.Drawing.Imaging.ImageAttributes();
   ia.SetColorMatrix(cm);
   return ia;
  }

  protected override void OnPaint(PaintEventArgs e) { }
  protected override void OnPaintBackground(PaintEventArgs e) { }

  // --- Window lifecycle -----------------------------------------------------------------

  private void ResizeAroundCenter(Size nextSize) {
   Rectangle work = Screen.FromControl(this).WorkingArea;
   int cx = Left + Width / 2;
   int cy = Top + Height / 2;
   int nx = Math.Max(work.Left + 12, Math.Min(work.Right - nextSize.Width - 12, cx - nextSize.Width / 2));
   int ny = Math.Max(work.Top + 12, Math.Min(work.Bottom - nextSize.Height - 12, cy - nextSize.Height / 2));
   Bounds = new Rectangle(nx, ny, nextSize.Width, nextSize.Height);
   LayoutContent(true);
   Render();
  }

  protected override void OnSizeChanged(EventArgs e) {
   base.OnSizeChanged(e);
   LayoutContent(true);
  }

  protected override void OnVisibleChanged(EventArgs e) {
   base.OnVisibleChanged(e);
   if (outsideClickTimer == null) return;
   outsideClickTimer.Stop();
   if (!Visible) {
    pump.Stop();
    if (editing) CommitInlineRename(true);
    return;
   }
   if (IsDisposed || Disposing || app == null || app.Panel != this) return;
   leftWasDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
   rightWasDown = (GetAsyncKeyState(0x02) & 0x8000) != 0;
   outsideClickTimer.Start();
   WakeAnimation();
  }

  // --- Mouse ----------------------------------------------------------------------------

  private TileView HitTile(Point p) {
   Rectangle view = GridViewport();
   if (!Rectangle.FromLTRB(view.Left, view.Top - 4, view.Right, Math.Max(view.Bottom, ClientSize.Height - 8)).Contains(p)) return null;
   for (int i = 0; i < tiles.Count; i++) {
    TileView t = tiles[i];
    var r = new RectangleF(t.X.F, t.Y.F - (float)scroll.Value, CellW, CellH);
    if (r.Contains(p)) return t;
   }
   return null;
  }

  protected override void OnMouseMove(MouseEventArgs e) {
   base.OnMouseMove(e);
   mouseClient = e.Location;
   if (!pickerMode && mouseArmed && pressedTile != null && !pressedTile.IsPlus && e.Button == MouseButtons.Left) {
    Size threshold = new Size(14, 14); // generous: a slightly shaky click must still launch
    Rectangle allowed = new Rectangle(mouseDownPoint.X - threshold.Width / 2, mouseDownPoint.Y - threshold.Height / 2, threshold.Width, threshold.Height);
    if (!allowed.Contains(e.Location)) {
     mouseArmed = false;
     TileView dragTile = pressedTile;
     pressedTile = null;
     StartItemDrag(dragTile);
     return;
    }
   }
   TileView hit = HitTile(e.Location);
   bool overButton = pickerMode
    ? (PickerDoneBounds().Contains(e.Location) || PickerFileBounds().Contains(e.Location) || PickerFolderBounds().Contains(e.Location))
    : TitleRect().Contains(e.Location);
   if (hit != hoveredTile) hoveredTile = hit;
   Cursor = (hit != null || overButton) ? Cursors.Hand : Cursors.Default;
   WakeAnimation();
  }

  protected override void OnMouseLeave(EventArgs e) {
   base.OnMouseLeave(e);
   mouseClient = new Point(-999, -999);
   hoveredTile = null;
   Cursor = Cursors.Default;
   WakeAnimation();
  }

  protected override void OnMouseDown(MouseEventArgs e) {
   base.OnMouseDown(e);
   if (editing) CommitInlineRename(true);
   keyboardSelection = false;
   TileView hit = HitTile(e.Location);
   if (hit != null && hit.Entry != null) selectedId = hit.Entry.Item.id;
   if (e.Button == MouseButtons.Left) {
    pressedTile = hit;
    mouseDownPoint = e.Location;
    mouseArmed = hit != null;
   }
   WakeAnimation();
  }

  protected override void OnMouseUp(MouseEventArgs e) {
   base.OnMouseUp(e);
   TileView activePress = pressedTile;
   bool wasArmed = mouseArmed;
   pressedTile = null;
   mouseArmed = false;
   WakeAnimation();

   if (e.Button == MouseButtons.Left) {
    if (pickerMode) {
     if (PickerDoneBounds().Contains(e.Location)) { ExitPickerMode(); return; }
     if (PickerFileBounds().Contains(e.Location)) { app.PickFilesToAdd(groupId); return; }
     if (PickerFolderBounds().Contains(e.Location)) { app.PickFolderToAdd(groupId); return; }
    } else if (TitleRect().Contains(e.Location)) {
     StartFolderTitleRename();
     return;
    }
    // Empty band above the sheet behaves like the dimmed background: it closes the folder.
    if (e.Location.Y < TitleBand && activePress == null) { app.ClosePanel(); return; }
   }

   TileView hit = HitTile(e.Location);
   if (e.Button == MouseButtons.Right) {
    if (!pickerMode && hit != null && hit.Entry != null) {
     selectedId = hit.Entry.Item.id;
     ShowEntryMenu(hit.Entry, e.Location);
    } else {
     ShowPanelBackgroundMenu(e.Location);
    }
    return;
   }

   if (e.Button == MouseButtons.Left && wasArmed && hit == activePress && hit != null) {
    if (pickerMode) {
     if (hit.Entry != null) {
      hit.Scale.Velocity += 2.5; // tactile pop on toggle
      app.ToggleCandidateInGroup(groupId, hit.Entry.Item);
     }
    } else if (hit.IsPlus) {
     EnterPickerMode();
    } else if (hit.Entry != null) {
     app.Launch(hit.Entry.GroupId, hit.Entry.Item.id);
    }
   }
  }

  // A click that arrived during the open morph (forwarded by AppController).
  internal void ClickAt(Point client) {
   TileView hit = HitTile(client);
   if (hit == null) return;
   if (hit.IsPlus) EnterPickerMode();
   else if (hit.Entry != null) { hit.Scale.Velocity -= 2.0; app.Launch(hit.Entry.GroupId, hit.Entry.Item.id); }
  }

  protected override void OnMouseWheel(MouseEventArgs e) {
   base.OnMouseWheel(e);
   Rectangle view = GridViewport();
   float maxScroll = Math.Max(0f, contentHeight - view.Height);
   if (maxScroll <= 0f) return;
   scroll.Target = Math.Max(0f, Math.Min(maxScroll, scroll.Target - (e.Delta / 120f) * 96f));
   scrollbarHold = 0.9;
   WakeAnimation();
  }

  private void ScrollGrid(int direction) {
   Rectangle view = GridViewport();
   float maxScroll = Math.Max(0f, contentHeight - view.Height);
   scroll.Target = Math.Max(0f, Math.Min(maxScroll, scroll.Target + direction * Math.Max(60f, view.Height - 40f)));
   scrollbarHold = 0.9;
   WakeAnimation();
  }

  private void EnsureSelectedVisible() {
   if (selectedId == null) return;
   Rectangle view = GridViewport();
   foreach (TileView t in tiles) {
    if (t.Entry != null && t.Entry.Item.id == selectedId) {
     float top = t.Target.Y - view.Y;
     float bottom = t.Target.Bottom - view.Y;
     if (top < scroll.Target) scroll.Target = Math.Max(0f, top - 12f);
     else if (bottom > scroll.Target + view.Height) scroll.Target = Math.Min(Math.Max(0f, contentHeight - view.Height), bottom - view.Height + 12f);
     scrollbarHold = 0.6;
     WakeAnimation();
     break;
    }
   }
  }

  // --- Picker / search ------------------------------------------------------------------

  private void EnterPickerMode() {
   if (pickerMode || group == null) return;
   pickerMode = true;
   searchActive = true;
   searchQuery = "";
   scroll.Snap(0);
   RefreshData();
   ResizeAroundCenter(GetExpandedSize(new Point(Left + Width / 2, Top + Height / 2)));
  }

  private void ExitPickerMode() {
   if (!pickerMode) return;
   pickerMode = false;
   searchActive = false;
   searchQuery = "";
   searchReveal.Snap(0);
   scroll.Snap(0);
   RefreshData();
   ResizeAroundCenter(GetExpandedSize(new Point(Left + Width / 2, Top + Height / 2)));
  }

  internal void FocusSearch() {
   searchActive = true;
   WakeAnimation();
  }

  // --- Rename (drawn in place; keys arrive through AppController.RouteKey/RouteChar) ----

  private void StartFolderTitleRename() {
   if (group == null || pickerMode) return;
   BeginEdit(true, group.id, null, group.title ?? "");
  }

  private void StartInlineRename(PanelEntry entry) {
   if (entry == null) return;
   BeginEdit(false, entry.GroupId, entry.Item.id, entry.Item.title ?? "");
  }

  private void BeginEdit(bool folder, string gId, string iId, string text) {
   renamingFolderTitle = folder;
   renamingGroupId = gId;
   renamingItemId = iId;
   editText = text;
   editSelectAll = true;
   editing = true;
   PlaceIme();
   WakeAnimation();
  }

  private RectangleF EditFieldRect() {
   if (renamingFolderTitle) { Rectangle r = TitleRect(); return new RectangleF(r.X - 14, r.Y + 1, r.Width + 28, r.Height); }
   foreach (TileView t in tiles)
    if (t.Entry != null && t.Entry.Item.id == renamingItemId && t.Entry.GroupId == renamingGroupId)
     return new RectangleF(t.X.F - 8, t.Y.F - (float)scroll.Value + IconPx + 10, CellW + 16, 26);
   return RectangleF.Empty;
  }

  // Lets the IME composition / candidate window appear right at the field being typed into.
  private void PlaceIme() {
   RectangleF r = editing ? EditFieldRect() : SearchRect();
   if (r.IsEmpty || app.DesktopFolders == null) return;
   app.DesktopFolders.PlaceImeWindow(PointToScreen(new Point((int)r.X + 12, (int)r.Bottom + 2)));
  }

  private void DrawEditField(Graphics g, RectangleF r, Font font) {
   using (var path = Ui.Squircle(r, Math.Min(12f, r.Height / 2f)))
   using (var fill = new SolidBrush(Color.FromArgb(150, 20, 20, 24)))
   using (var pen = new Pen(Ui.Blue, 1.6f)) { g.FillPath(fill, path); g.DrawPath(pen, path); }
   using (var f = Ui.Centered(true)) {
    var inner = new RectangleF(r.X + 8, r.Y, r.Width - 16, r.Height);
    SizeF size = g.MeasureString(editText.Length == 0 ? " " : editText, font, new SizeF(inner.Width, inner.Height), StringFormat.GenericTypographic);
    float textW = Math.Min(inner.Width, size.Width);
    if (editSelectAll && editText.Length > 0) {
     var sel = new RectangleF(inner.X + (inner.Width - textW) / 2f - 2, r.Y + 4, textW + 4, r.Height - 8);
     using (var b = new SolidBrush(Color.FromArgb(150, 10, 132, 255))) g.FillRectangle(b, sel);
    }
    using (var b = new SolidBrush(Color.White)) g.DrawString(editText, font, b, inner, f);
    if (!editSelectAll) {
     float cx = inner.X + (inner.Width + textW) / 2f + 2;
     using (var caret = new Pen(Ui.Blue, 2f)) g.DrawLine(caret, cx, r.Y + 6, cx, r.Bottom - 6);
    }
   }
  }

  private bool HandleEditKey(Keys keyData) {
   if (keyData == Keys.Enter) { CommitInlineRename(true); return true; }
   if (keyData == Keys.Escape) { CommitInlineRename(false); return true; }
   if (keyData == Keys.Back) {
    if (editSelectAll) editText = ""; else if (editText.Length > 0) editText = editText.Substring(0, editText.Length - 1);
    editSelectAll = false; WakeAnimation(); return true;
   }
   if (keyData == (Keys.Control | Keys.Back) || keyData == Keys.Delete) { editText = ""; editSelectAll = false; WakeAnimation(); return true; }
   if (keyData == (Keys.Control | Keys.A)) { editSelectAll = true; WakeAnimation(); return true; }
   if (keyData == (Keys.Control | Keys.V)) {
    string clip = "";
    try { clip = Clipboard.GetText() ?? ""; } catch { }
    clip = clip.Replace("\r", " ").Replace("\n", " ");
    editText = (editSelectAll ? "" : editText) + clip; editSelectAll = false; WakeAnimation(); return true;
   }
   // Plain typing keys must fall through so WM_CHAR (incl. IME text) still arrives.
   Keys code = keyData & Keys.KeyCode;
   bool modifier = (keyData & (Keys.Control | Keys.Alt)) != 0;
   return modifier || code == Keys.Up || code == Keys.Down || code == Keys.Left || code == Keys.Right || code == Keys.Tab || (code >= Keys.F1 && code <= Keys.F24);
  }

  private void HandleEditChar(char c) {
   if (char.IsControl(c)) return;
   editText = (editSelectAll ? "" : editText) + c;
   editSelectAll = false;
   WakeAnimation();
  }

  private void CommitInlineRename(bool save) {
   if (!editing) return;
   bool isFolder = renamingFolderTitle;
   string gId = renamingGroupId, iId = renamingItemId, text = editText;
   editing = false;
   renamingFolderTitle = false;
   renamingGroupId = null;
   renamingItemId = null;
   if (save && !String.IsNullOrWhiteSpace(text) && gId != null) {
    if (isFolder) app.RenameGroupInline(gId, text);
    else if (iId != null) app.RenameItemInline(gId, iId, text);
   }
   WakeAnimation();
  }

  // --- Drag & drop ----------------------------------------------------------------------

  private void StartItemDrag(TileView tile) {
   if (tile == null || tile.Entry == null || tile.Entry.IsCrossGroup) return;
   Exception failure = null;
   draggingItem = true;
   dragSourceTileIndex = tile.SlotIndex;
   dragHoverInsertIndex = tile.SlotIndex;
   WakeAnimation();
   try {
    DataObject data = new DataObject();
    data.SetData(ItemFormat, groupId + "\n" + tile.Entry.Item.id);
    PerformItemDrag(this, data);
   } catch (Exception ex) { failure = ex; } finally {
    draggingItem = false;
    dragSourceTileIndex = -1;
    dragHoverInsertIndex = -1;
    ClearDropFeedback();
    LayoutContent(false);
    WakeAnimation();
   }
   if (failure != null) app.ReportError(failure);
  }

  protected virtual DragDropEffects PerformItemDrag(Control source, IDataObject data) {
   return source.DoDragDrop(data, DragDropEffects.Move);
  }

  private void AttachDrop(Control control) {
   control.AllowDrop = true;
   control.DragEnter += DragOverContent;
   control.DragOver += DragOverContent;
   control.DragLeave += delegate { ClearDropFeedback(); };
   control.DragDrop += DropContent;
  }

  private sealed class DropPayload {
   internal string SourceGroup, ItemId, Url;
   internal string[] Paths;
   internal DragDropEffects Effect;
  }

  private DropPayload ReadDropPayload(IDataObject data, DragDropEffects allowed) {
   if (group == null || data == null || allowed == DragDropEffects.None) return null;
   if (data.GetDataPresent(ItemFormat)) {
    if ((allowed & DragDropEffects.Move) == 0) return null;
    string value = data.GetData(ItemFormat) as string;
    if (value == null) return null;
    string[] parts = value.Split(new char[] { '\n' });
    if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
    foreach (DeskGroup candidate in app.Data.groups)
     if (candidate.id == parts[0])
      foreach (DeskItem entry in candidate.items)
       if (entry.id == parts[1])
        return new DropPayload { SourceGroup = parts[0], ItemId = parts[1], Effect = DragDropEffects.Move };
    return null;
   }
   if (data.GetDataPresent(DataFormats.FileDrop)) {
    if ((allowed & DragDropEffects.Link) == 0) return null;
    string[] paths = data.GetData(DataFormats.FileDrop) as string[];
    if (paths == null || paths.Length == 0) return null;
    foreach (string path in paths) if (String.IsNullOrWhiteSpace(path)) return null;
    return new DropPayload { Paths = (string[])paths.Clone(), Effect = DragDropEffects.Link };
   }
   if (!data.GetDataPresent(DataFormats.UnicodeText)) return null;
   DragDropEffects effect = (allowed & DragDropEffects.Link) != 0 ? DragDropEffects.Link :
    ((allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None);
   if (effect == DragDropEffects.None) return null;
   string url = data.GetData(DataFormats.UnicodeText) as string;
   Uri uri;
   if (url == null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) ||
       (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
   return new DropPayload { Url = uri.AbsoluteUri, Effect = effect };
  }

  private int ComputeDropSlot(Point client, out int modelInsertIndex) {
   modelInsertIndex = -1;
   Rectangle view = GridViewport();
   if (!view.Contains(client)) return -1;
   int movable = 0;
   foreach (var t in tiles) if (!t.IsPlus) movable++;
   int bestSlot = movable;
   float bestDist = float.MaxValue;
   int usedW = gridColumns * CellW + (gridColumns - 1) * GapX;
   float left = view.X + (view.Width - usedW) * 0.5f;
   for (int i = 0; i < movable; i++) {
    // Compare against the unshifted logical slot so the live reflow cannot oscillate.
    float sx = left + (i % gridColumns) * (CellW + GapX) + CellW * 0.5f;
    float sy = view.Y + (i / gridColumns) * (CellH + GapY) - (float)scroll.Value + CellH * 0.5f;
    float d = (client.X - sx) * (client.X - sx) + (client.Y - sy) * (client.Y - sy);
    if (d < bestDist) { bestDist = d; bestSlot = i; }
   }
   // Map the visual slot back to the folder's real item index. Slots are positions in the
   // (possibly search-filtered) list, so they must never be used as model indices directly.
   if (bestSlot >= 0 && bestSlot < filtered.Count && filtered[bestSlot].GroupId == groupId) {
    int target = filtered[bestSlot].Index;
    bool sameFolderDrag = dragSourceTileIndex >= 0 && dragSourceTileIndex < filtered.Count && filtered[dragSourceTileIndex].GroupId == groupId;
    // Same-folder reorder: TryMoveItem decrements insertIndex when old < insertIndex.
    modelInsertIndex = sameFolderDrag && filtered[dragSourceTileIndex].Index < target ? target + 1 : target;
   }
   return bestSlot;
  }

  private void DragOverContent(object sender, DragEventArgs e) {
   e.Effect = DragDropEffects.None;
   try {
    DropPayload payload = ReadDropPayload(e.Data, e.AllowedEffect);
    if (payload == null) { ClearDropFeedback(); return; }
    Point client = PointToClient(new Point(e.X, e.Y));
    Rectangle view = GridViewport();
    if (view.Contains(client)) {
     float delta = client.Y < view.Top + 28 ? -14f : (client.Y > view.Bottom - 28 ? 14f : 0f);
     if (delta != 0f) {
      float maxScroll = Math.Max(0f, contentHeight - view.Height);
      scroll.Target = Math.Max(0f, Math.Min(maxScroll, scroll.Target + delta));
     }
    }
    int modelInsert;
    int hoverSlot = ComputeDropSlot(client, out modelInsert);
    if (hoverSlot != dragHoverInsertIndex) {
     dragHoverInsertIndex = hoverSlot;
     LayoutContent(false);
    }
    backgroundDrop = payload.ItemId == null || payload.SourceGroup != groupId;
    e.Effect = payload.Effect;
    WakeAnimation();
   } catch (Exception) {
    e.Effect = DragDropEffects.None;
    ClearDropFeedback();
   }
  }

  private void DropContent(object sender, DragEventArgs e) {
   e.Effect = DragDropEffects.None;
   try {
    DropPayload payload = ReadDropPayload(e.Data, e.AllowedEffect);
    if (payload == null) return;
    int index;
    ComputeDropSlot(PointToClient(new Point(e.X, e.Y)), out index);
    bool committed;
    if (payload.ItemId != null)
     committed = app.TryMoveItem(payload.SourceGroup, payload.ItemId, groupId, index);
    else if (payload.Paths != null)
     committed = app.TryAddPaths(groupId, payload.Paths, index);
    else
     committed = app.TryAddUrl(groupId, payload.Url, index);
    if (committed) e.Effect = payload.Effect;
   } catch (Exception ex) {
    e.Effect = DragDropEffects.None;
    app.ReportError(ex);
   } finally {
    ClearDropFeedback();
   }
  }

  private void ClearDropFeedback() {
   backgroundDrop = false;
   if (dragHoverInsertIndex != -1 && !draggingItem) {
    dragHoverInsertIndex = -1;
    LayoutContent(false);
   }
   WakeAnimation();
  }

  // --- Menus ----------------------------------------------------------------------------

  private EventHandler MenuAction(Action action) {
   return delegate {
    if (IsDisposed || Disposing) return;
    if (!IsHandleCreated) { action(); return; }
    pendingMenuActions++;
    try {
     BeginInvoke((MethodInvoker)delegate {
      try { if (!IsDisposed && !Disposing) action(); } finally { pendingMenuActions--; }
     });
    } catch { pendingMenuActions--; throw; }
   };
  }

  private ContextMenuStrip NewMenu() {
   ContextMenuStrip menu = new ContextMenuStrip();
   PanelDrawing.StyleMenu(menu);
   activeMenus.Add(menu);
   menu.Disposed += delegate { activeMenus.Remove(menu); };
   menu.Closed += delegate {
    if (!IsDisposed && !Disposing && IsHandleCreated)
     BeginInvoke((MethodInvoker)delegate { menu.Dispose(); });
    else menu.Dispose();
   };
   return menu;
  }

  private void ShowPanelBackgroundMenu(Point point) {
   ContextMenuStrip menu = NewMenu();
   if (pickerMode) {
    menu.Items.Add("完成", null, MenuAction(delegate { ExitPickerMode(); }));
    menu.Items.Add("选择文件添加…", null, MenuAction(delegate { app.PickFilesToAdd(groupId); }));
    menu.Items.Add("选择文件夹添加…", null, MenuAction(delegate { app.PickFolderToAdd(groupId); }));
   } else {
    menu.Items.Add("添加应用…", null, MenuAction(delegate { EnterPickerMode(); }));
    menu.Items.Add("手动添加入口 / 网址…", null, MenuAction(delegate { if (group != null) app.AddItem(groupId); }));
    menu.Items.Add("搜索", null, MenuAction(delegate { FocusSearch(); }));
    menu.Items.Add("重命名文件夹", null, MenuAction(delegate { StartFolderTitleRename(); }));
   }
   menu.Items.Add(new ToolStripSeparator());
   menu.Items.Add("关闭", null, MenuAction(delegate { app.ClosePanel(); }));
   menu.Show(this, point);
  }

  private void ShowEntryMenu(PanelEntry entry, Point point) {
   if (entry == null) return;
   string itemGroupId = entry.GroupId;
   DeskItem item = entry.Item;
   string itemId = item.id;
   int index = entry.Index;
   ContextMenuStrip menu = NewMenu();

   menu.Items.Add("打开", null, MenuAction(delegate { app.Launch(itemGroupId, itemId); }));
   menu.Items.Add("重命名", null, MenuAction(delegate { StartInlineRename(entry); }));
   menu.Items.Add("编辑…", null, MenuAction(delegate { app.EditItem(itemGroupId, itemId); }));

   if (item.type != "url") {
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("在终端中打开", null, MenuAction(delegate { app.RunEntryAction(delegate { EntryServices.OpenInTerminal(item); }, true); }));
    if (EntryServices.FindEditorExecutable("vscode") != null)
     menu.Items.Add("用 VS Code 打开", null, MenuAction(delegate { app.RunEntryAction(delegate { EntryServices.OpenInCodeEditor(item, "vscode"); }, true); }));
    if (EntryServices.FindEditorExecutable("cursor") != null)
     menu.Items.Add("用 Cursor 打开", null, MenuAction(delegate { app.RunEntryAction(delegate { EntryServices.OpenInCodeEditor(item, "cursor"); }, true); }));
    menu.Items.Add("在资源管理器中显示", null, MenuAction(delegate { app.RunEntryAction(delegate { EntryServices.OpenContainingFolder(item); }, true); }));
   }
   menu.Items.Add(item.type == "url" ? "复制网址" : "复制路径", null, MenuAction(delegate { app.RunEntryAction(delegate { if (!String.IsNullOrEmpty(item.target)) Clipboard.SetText(item.target); }, false); }));

   if (!entry.IsCrossGroup) {
    menu.Items.Add(new ToolStripSeparator());
    ToolStripMenuItem move = new ToolStripMenuItem("移到文件夹");
    foreach (DeskGroup other in app.Data.groups) {
     if (other.id == groupId) continue;
     string target = other.id;
     move.DropDownItems.Add((other.title ?? "未命名").Replace("&", "&&"), null, MenuAction(delegate { app.MoveItem(groupId, itemId, target, -1); }));
    }
    move.Enabled = move.DropDownItems.Count > 0;
    PanelDrawing.StyleDropDown(move);
    menu.Items.Add(move);
    ToolStripItem up = menu.Items.Add("前移", null, MenuAction(delegate { app.MoveItem(groupId, itemId, groupId, index - 1); }));
    up.Enabled = index > 0;
    ToolStripItem down = menu.Items.Add("后移", null, MenuAction(delegate { app.MoveItem(groupId, itemId, groupId, index + 2); }));
    down.Enabled = group != null && index < group.items.Count - 1;
   }

   menu.Items.Add(new ToolStripSeparator());
   ToolStripItem remove = menu.Items.Add("移除（不删除原文件）", null, MenuAction(delegate { app.DeleteItem(itemGroupId, itemId); }));
   remove.Tag = PanelDrawing.Destructive;
   menu.Show(this, point);
  }

  // --- Dismissal ------------------------------------------------------------------------
  // Outside clicks are consumed by the backdrop overlay; this poll covers the rest: clicks on
  // the taskbar, and another app taking the foreground (then the folder simply goes away).

  [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int count);
  private static readonly uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

  internal static bool ForegroundAllowsPanel() {
   IntPtr fg = GetForegroundWindow();
   if (fg == IntPtr.Zero) return true;
   uint pid; GetWindowThreadProcessId(fg, out pid);
   if (pid == ownPid) return true;
   var name = new System.Text.StringBuilder(64);
   GetClassName(fg, name, name.Capacity);
   string c = name.ToString();
   return c == "Progman" || c == "WorkerW";
  }

  private void PollOutsideClick(object sender, EventArgs e) {
   if (IsDisposed || Disposing || !Visible || app.Panel != this) {
    outsideClickTimer.Stop();
    return;
   }
   if (app.ModalInProgress || draggingItem || pendingMenuActions != 0) return;
   foreach (ContextMenuStrip menu in activeMenus) if (!menu.IsDisposed && menu.Visible) return;
   if (!ForegroundAllowsPanel()) { app.ClosePanel(); return; }
   bool left = (GetAsyncKeyState(0x01) & 0x8000) != 0;
   bool right = (GetAsyncKeyState(0x02) & 0x8000) != 0;
   bool pressedEdge = (left && !leftWasDown) || (right && !rightWasDown);
   leftWasDown = left;
   rightWasDown = right;
   if (!pressedEdge) return;
   if (!Bounds.Contains(Control.MousePosition) && !app.SourceContains(Control.MousePosition)) app.ClosePanel();
  }

  // --- Keyboard -------------------------------------------------------------------------

  protected override void OnKeyPress(KeyPressEventArgs e) {
   base.OnKeyPress(e);
   if (HandleChar(e.KeyChar)) e.Handled = true;
  }
  protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
   return HandleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);
  }

  // Characters typed while the folder is open (routed from the desktop layer).
  internal bool HandleChar(char c) {
   if (editing) { HandleEditChar(c); return true; }
   if (char.IsControl(c)) return false;
   if (!pickerMode && !searchActive && c >= '1' && c <= '9') {
    int idx = c - '1';
    if (idx < filtered.Count) { app.Launch(filtered[idx].GroupId, filtered[idx].Item.id); return true; }
   }
   // Type-to-search: any printable character opens the search field.
   searchActive = true;
   searchQuery += c;
   scroll.Target = 0;
   RefreshData();
   PlaceIme();
   return true;
  }

  // Command keys while the folder is open (routed from the desktop layer).
  internal bool HandleKey(Keys keyData) {
   if (editing) return HandleEditKey(keyData);

   if (keyData == Keys.Escape) {
    if (pickerMode) {
     if (searchQuery.Length > 0) { searchQuery = ""; RefreshData(); }
     else ExitPickerMode();
     return true;
    }
    if (searchActive || searchQuery.Length > 0) {
     searchActive = false;
     searchQuery = "";
     RefreshData();
     return true;
    }
    app.ClosePanel();
    return true;
   }
   if (keyData == (Keys.Control | Keys.F)) {
    if (pickerMode) return true;
    searchActive = !searchActive;
    if (!searchActive && searchQuery.Length > 0) { searchQuery = ""; RefreshData(); }
    else WakeAnimation();
    return true;
   }
   if (keyData == Keys.Back && (searchActive || pickerMode)) {
    if (searchQuery.Length > 0) {
     searchQuery = searchQuery.Substring(0, searchQuery.Length - 1);
     RefreshData();
    } else if (!pickerMode) {
     searchActive = false;
     WakeAnimation();
    }
    return true;
   }
   if (keyData == (Keys.Control | Keys.Back) && (searchActive || pickerMode)) {
    searchQuery = "";
    RefreshData();
    return true;
   }
   if (keyData == (Keys.Control | Keys.N)) {
    if (!pickerMode && group != null) EnterPickerMode();
    return true;
   }
   if (keyData == Keys.PageUp) { ScrollGrid(-1); return true; }
   if (keyData == Keys.PageDown) { ScrollGrid(1); return true; }

   if (keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down) {
    MoveSelectionByArrow(keyData);
    return true;
   }

   if (pickerMode && keyData == Keys.Enter) {
    string raw = (searchQuery ?? "").Trim().Trim('"');
    if (raw.Length > 0 && (EntryServices.IsAbsolutePath(raw) || raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) {
     bool ok = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? app.TryAddUrl(groupId, raw, -1) : app.TryAddPaths(groupId, new[] { raw }, -1);
     if (ok) { searchQuery = ""; RefreshData(); }
     return true;
    }
    if (selectedId != null) {
     PanelEntry sel = filtered.Find(v => v.Item.id == selectedId);
     if (sel != null) { app.ToggleCandidateInGroup(groupId, sel.Item); return true; }
    } else if (filtered.Count > 0) {
     app.ToggleCandidateInGroup(groupId, filtered[0].Item);
     return true;
    }
   }

   if (!pickerMode && selectedId != null) {
    PanelEntry selectedEntry = filtered.Find(v => v.Item.id == selectedId);
    if (selectedEntry != null) {
     if (keyData == Keys.Delete && !searchActive) { app.DeleteItem(selectedEntry.GroupId, selectedId); return true; }
     if (keyData == Keys.F2) { StartInlineRename(selectedEntry); return true; }
     if (keyData == Keys.Enter) { app.Launch(selectedEntry.GroupId, selectedId); return true; }
    }
   } else if (!pickerMode && keyData == Keys.F2) {
    StartFolderTitleRename();
    return true;
   } else if (!pickerMode && keyData == Keys.Enter && filtered.Count > 0) {
    app.Launch(filtered[0].GroupId, filtered[0].Item.id);
    return true;
   }
   if (keyData == (Keys.Control | Keys.V) && (searchActive || pickerMode)) {
    string clip = "";
    try { clip = Clipboard.GetText() ?? ""; } catch { }
    searchActive = true;
    searchQuery += clip.Replace('\r', ' ').Replace('\n', ' ');
    RefreshData();
    return true;
   }
   return false;
  }

  private void MoveSelectionByArrow(Keys arrow) {
   if (filtered.Count == 0) return;
   keyboardSelection = true;
   int current = filtered.FindIndex(v => v.Item.id == selectedId);
   if (current < 0) {
    selectedId = filtered[0].Item.id;
   } else {
    int delta = arrow == Keys.Left ? -1 : (arrow == Keys.Right ? 1 : (arrow == Keys.Up ? -gridColumns : gridColumns));
    int next = Math.Max(0, Math.Min(filtered.Count - 1, current + delta));
    selectedId = filtered[next].Item.id;
   }
   EnsureSelectedVisible();
   WakeAnimation();
  }

  private static bool Contains(string text, string query) {
   return (text ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
  }

  protected override void Dispose(bool disposing) {
   if (disposing) {
    pump.Dispose();
    if (outsideClickTimer != null) { outsideClickTimer.Stop(); outsideClickTimer.Dispose(); }
    foreach (ContextMenuStrip menu in new List<ContextMenuStrip>(activeMenus)) menu.Dispose();
    activeMenus.Clear();
    surface.Dispose();
    if (material != null) { material.Dispose(); material = null; }
    if (sheetCache != null) { sheetCache.Dispose(); sheetCache = null; }
    if (titleCache != null) { titleCache.Dispose(); titleCache = null; }
    blitAttributes.Dispose();
    titleFont.Dispose(); labelFont.Dispose(); subLabelFont.Dispose(); searchFont.Dispose();
    buttonFont.Dispose(); buttonBoldFont.Dispose(); pickerTitleFont.Dispose(); plusFont.Dispose();
   }
   base.Dispose(disposing);
  }
 }

 internal sealed class PanelEntry {
  internal readonly DeskItem Item;
  internal readonly int Index;
  internal readonly string GroupId;
  internal readonly string GroupTitle;
  internal readonly bool IsCrossGroup;
  internal PanelEntry(DeskItem item, int index, string groupId, string groupTitle, bool isCrossGroup) {
   Item = item;
   Index = index;
   GroupId = groupId;
   GroupTitle = groupTitle;
   IsCrossGroup = isCrossGroup;
  }
 }

 // Context menus styled like iOS/macOS dark menus: rounded window, rounded highlight, roomy rows.
 internal static class PanelDrawing {
  internal static readonly object Destructive = new object();
  [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
  static readonly Font MenuFont = Ui.MakeFont(Ui.Cjk, 13.5f, FontStyle.Regular);

  internal static void StyleMenu(ContextMenuStrip menu) {
   if (menu == null) return;
   menu.ShowImageMargin = false;
   menu.ShowCheckMargin = false;
   menu.Font = MenuFont;
   menu.Padding = new Padding(5, 6, 5, 6);
   menu.BackColor = Color.FromArgb(38, 38, 42);
   menu.ForeColor = Color.White;
   menu.Renderer = new AppleMenuRenderer();
   menu.ItemAdded += delegate(object s, ToolStripItemEventArgs e) { Pad(e.Item); };
   foreach (ToolStripItem item in menu.Items) Pad(item);
   menu.Opened += delegate { Round(menu); };
  }
  internal static void StyleDropDown(ToolStripMenuItem parent) {
   var dd = parent.DropDown as ToolStripDropDownMenu;
   if (dd == null) return;
   dd.ShowImageMargin = false;
   dd.ShowCheckMargin = false;
   dd.Font = MenuFont;
   dd.Padding = new Padding(5, 6, 5, 6);
   dd.BackColor = Color.FromArgb(38, 38, 42);
   dd.Renderer = new AppleMenuRenderer();
   foreach (ToolStripItem item in dd.Items) Pad(item);
   dd.Opened += delegate { Round(dd); };
  }
  static void Pad(ToolStripItem item) {
   if (item is ToolStripSeparator) { item.Margin = new Padding(0, 3, 0, 3); return; }
   item.Padding = new Padding(8, 5, 18, 5);
   item.Margin = new Padding(0, 1, 0, 1);
  }
  static void Round(ToolStripDropDown dd) {
   try { if (dd.IsHandleCreated) { int corner = 2; DwmSetWindowAttribute(dd.Handle, 33, ref corner, 4); } } catch { }
  }

  private sealed class AppleMenuRenderer : ToolStripProfessionalRenderer {
   public AppleMenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }
   protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {
    using (var b = new SolidBrush(Color.FromArgb(38, 38, 42))) e.Graphics.FillRectangle(b, e.AffectedBounds);
   }
   // Windows 11 draws the rounded hairline border for DWMWCP_ROUND popups itself.
   protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
   protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
   protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
    if (!e.Item.Selected || !e.Item.Enabled) return;
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    var r = new RectangleF(2, 0, e.Item.Width - 4, e.Item.Height);
    bool destructive = e.Item.Tag == Destructive;
    using (var path = Ui.Squircle(r, 6))
    using (var b = new SolidBrush(destructive ? Color.FromArgb(255, 69, 58) : Color.FromArgb(10, 132, 255))) g.FillPath(b, path);
   }
   protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
    bool destructive = e.Item.Tag == Destructive;
    e.TextColor = !e.Item.Enabled ? Color.FromArgb(110, 235, 235, 245) : (e.Item.Selected ? Color.White : (destructive ? Color.FromArgb(255, 105, 97) : Color.FromArgb(245, 245, 250)));
    base.OnRenderItemText(e);
   }
   protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) {
    int y = e.Item.Height / 2;
    using (var pen = new Pen(Color.FromArgb(46, 255, 255, 255))) e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
   }
   protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) {
    e.ArrowColor = Color.FromArgb(200, 235, 235, 245);
    base.OnRenderArrow(e);
   }
  }
  private sealed class MenuColors : ProfessionalColorTable {
   public override Color ToolStripDropDownBackground { get { return Color.FromArgb(38, 38, 42); } }
   public override Color MenuBorder { get { return Color.FromArgb(70, 70, 76); } }
   public override Color MenuItemBorder { get { return Color.Transparent; } }
   public override Color MenuItemSelected { get { return Color.FromArgb(10, 132, 255); } }
   public override Color ImageMarginGradientBegin { get { return Color.FromArgb(38, 38, 42); } }
   public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(38, 38, 42); } }
   public override Color ImageMarginGradientEnd { get { return Color.FromArgb(38, 38, 42); } }
  }

  internal static GraphicsPath Rounded(Rectangle rectangle, int radius) {
   return Ui.Squircle(rectangle, radius);
  }
 }
}
