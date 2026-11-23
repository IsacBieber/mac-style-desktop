using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using MacDesk.Core;

namespace MacDeskNative {
 // The open folder, iOS style, and the ONLY thing that draws it - from the first frame of the
 // open transition to the last frame of the close. One layered, non-activating window covering
 // the path of the animation; one progress spring p (0 = the desktop folder icon, 1 = the open panel);
 // one geometry (FolderLayout / Geometry(p)). There is no separate morph window and no hand-off,
 // so the frame, icons, labels and title cannot get out of step.
 public class FolderLayer : Form {
  private const string ItemFormat = "MacDeskNative.Item";
  // Geometry comes from Metrics (one source for every folder size).
  public const int TitleBand = Metrics.TitleBand;
  public const float SheetRadius = Metrics.SheetRadius;
  private const int PadX = Metrics.PadX, PadTop = Metrics.PadTop, PadBottom = Metrics.PadBottom, CellW = Metrics.CellW, CellH = Metrics.CellH, GapX = Metrics.GapX, GapY = Metrics.GapY, IconPx = Metrics.IconPx;
  private const int SearchH = Metrics.SearchH, SearchGap = Metrics.SearchGap, PickerHeaderH = Metrics.PickerHeaderH;

  private readonly AppController app;
  private string groupId;
  private int groupAppCount;
  private readonly List<PanelEntry> filtered = new List<PanelEntry>();
  private readonly List<TileView> tiles = new List<TileView>();
  private readonly HashSet<ContextMenuStrip> activeMenus = new HashSet<ContextMenuStrip>();
  private readonly Font titleFont = Ui.MakeFont(Ui.Cjk, 24f, FontStyle.Bold);
  private readonly Font labelFont = Ui.MakeFont(Ui.Cjk, 12.5f, FontStyle.Regular);
  private readonly Font subLabelFont = Ui.MakeFont(Ui.Cjk, 11f, FontStyle.Regular);
  private readonly Font searchFont = Ui.MakeFont(Ui.Cjk, 14.5f, FontStyle.Regular);
  private readonly Font buttonFont = Ui.MakeFont(Ui.Cjk, 14.5f, FontStyle.Regular);
  private readonly Font buttonBoldFont = Ui.MakeFont(Ui.Cjk, 14.5f, FontStyle.Bold);
  private readonly Font pickerTitleFont = Ui.MakeFont(Ui.Cjk, 15f, FontStyle.Bold);
  private readonly Font plusFont = Ui.MakeFont(Ui.TextRegular, 30f, FontStyle.Regular);

  private readonly LayeredSurface surface = new LayeredSurface();
  private readonly FramePump pump;
  private FolderGlass material;

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
  private bool editing;
  private readonly TextField editField = new TextField { MaxLength = 120 };
  private string renamingGroupId, renamingItemId;
  private bool renamingFolderTitle;

  private readonly OutsideClickWatcher outsideClicks;

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
   public readonly Spring X = new Spring(0, 0.28, 0.9);
   public readonly Spring Y = new Spring(0, 0.28, 0.9);
   public readonly Spring Scale = new Spring(1, 0.22, 0.86);
   public readonly Spring Select = new Spring(0, 0.18, 1.0);
   public readonly Spring Hover = new Spring(0, 0.16, 1.0);
   public readonly Spring Fade = new Spring(1, 0.2, 1.0);
   public bool Step(double dt) { return X.Step(dt) | Y.Step(dt) | Scale.Step(dt) | Select.Step(dt) | Hover.Step(dt) | Fade.Step(dt); }
  }

  public FolderLayer(AppController app, string groupId) {
   this.app = app;
   this.groupId = groupId;
   blitAttributes.SetWrapMode(WrapMode.TileFlipXY);
   AutoScaleMode = AutoScaleMode.None;
   FormBorderStyle = FormBorderStyle.None;
   ShowInTaskbar = false;
   StartPosition = FormStartPosition.Manual;
   SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
   KeyPreview = true;
   AllowDrop = true;
   AccessibleName = "文件夹快捷入口面板";
   Text = "MacDesk 文件夹";

   AttachDrop(this);
   pump = new FramePump(this, Tick);
   editField.Changed += delegate { WakeAnimation(); };

   work = Screen.PrimaryScreen.WorkingArea;
   RefreshData();
   panelSize = ComputePanelSize();
   origin = FolderLayout.PanelOrigin(work.Size, panelSize);
   LayoutContent(true);

   outsideClicks = new OutsideClickWatcher(30) {
    Paused = OutsideClicksPaused,
    ForegroundLost = delegate { app.ClosePanel(); },
    Pressed = delegate(Point screen) {
     // Presses on our popups (an alert over the panel, a toast) are theirs, not "outside".
     if (!PanelScreenRect().Contains(screen) && !app.PopupContains(screen, null)) app.ClosePanel();
    }
   };
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
   mouseClient = new Point(-999, -999);
   mouseArmed = false;
   selectedId = null;
   backgroundDrop = false;
   dropGlow.Snap(0);
   dragSourceTileIndex = -1;
   dragHoverInsertIndex = -1;
   scroll.Snap(0);
   scrollbar.Snap(0);
   scrollbarHold = 0;
   titleReveal.Snap(0);
   titleHover.Snap(0);
   keyboardSelection = false;
   foreach (var t in tiles) { t.Scale.Snap(1); t.Fade.Snap(1); t.Hover.Snap(0); t.Select.Snap(0); }
  }

  // The blurred screen behind the folder. Only the part under this window is copied (a full
  // screen copy is ~8 MB and used to stall the frame where the morph hands over to the panel).
  private Backdrop backdropSource;
  internal void SetBackdrop(Backdrop backdrop) {
   if (backdrop == backdropSource && (backdrop == null || material != null)) return;
   backdropSource = backdrop;
   material = null;
   sheetCacheMaterial = null;
   if (sheetCache != null) { sheetCache.Dispose(); sheetCache = null; }
   EnsureMaterial();
  }
  private void EnsureMaterial() {
   Backdrop bd = backdropSource;
   if (bd == null || bd.Image == null) { material = null; return; }
   Rectangle window = PanelScreenRect();
   if (!tileRect.IsEmpty) window = Rectangle.Union(window, new Rectangle(tileRect.X + work.X, tileRect.Y + work.Y, tileRect.Width, tileRect.Height));
   if (material != null && material.Area.Contains(Rectangle.Intersect(window, bd.Area))) return;
   Rectangle crop = Rectangle.Intersect(Rectangle.Inflate(window, 48, 48), bd.Area);
   material = crop.Width > 0 && crop.Height > 0 ? new FolderGlass(bd, crop) : null;
  }


  // === Open / close (the whole transition lives here) =====================================

  private Rectangle work;                 // screen work area
  private Rectangle canvas;               // window bounds in work-area coordinates
  private Point origin;                   // panel top-left in work-area coordinates
  private Size panelSize;                 // title band + sheet
  private Size PanelSize { get { return panelSize; } }
  private readonly Spring progress = new Spring(1, 0.27, 1.0); // critically damped: never overshoots
  private Rectangle tileRect = Rectangle.Empty; // desktop folder icon, work-area coordinates
  private Bitmap tileBody;                // that icon's frosted body (no mini icons), for the first frames
  private bool collapsedNow, justOpened;
  internal Action<double> BackdropAlpha;  // backdrop dim follows p in the same frame
  internal Action Collapsed;
  internal Action Landing;

  internal bool IsOpening { get { return Visible && progress.Target >= 1 && progress.Value < 1; } }
  internal bool IsClosing { get { return progress.Target <= 0; } }
  /// Screen rectangle of the open panel (title band + sheet), for popups placed over it.
  internal Rectangle ScreenRect { get { return PanelScreenRect(); } }

  // New folder / desktop "重命名": edit the title in place as soon as the panel has landed.
  private bool renameTitleOnLand;
  internal void RenameTitleOnLand() {
   if (IsOpening) { renameTitleOnLand = true; return; }
   renameTitleOnLand = false;
   StartFolderTitleRename();
  }
  private void LandedForRename() {
   if (!renameTitleOnLand) return;
   renameTitleOnLand = false;
   StartFolderTitleRename();
  }
  internal bool CanSetOpeningBackdrop { get { return IsOpening && progress.Value < 0.25; } }

  private Rectangle PanelScreenRect() { return new Rectangle(work.X + origin.X, work.Y + origin.Y, panelSize.Width, panelSize.Height); }
  private Point ToWindow(Point panelPoint) { return new Point(panelPoint.X + origin.X - canvas.X, panelPoint.Y + origin.Y - canvas.Y); }
  private Point ToPanel(Point windowPoint) { return new Point(windowPoint.X + canvas.X - origin.X, windowPoint.Y + canvas.Y - origin.Y); }
  private MouseEventArgs ToPanel(MouseEventArgs e) {
   Point point = ToPanel(e.Location);
   return new MouseEventArgs(e.Button, e.Clicks, point.X, point.Y, e.Delta);
  }

  // Lays the folder out for `workArea` without showing anything (also used offscreen by tests).
  internal void Prepare(Rectangle workArea) {
   work = workArea;
   RefreshData();
   panelSize = ComputePanelSize();
   origin = FolderLayout.PanelOrigin(work.Size, panelSize);
   LayoutContent(true);
   IntPtr h = Handle;
  }

  // Starts the open transition from the desktop folder icon at `tileScreen`.
  internal void Open(string id, Rectangle workArea, Rectangle tileScreen, Backdrop backdrop, Bitmap body) {
   ResetTransientState();
   groupId = id;
   Prepare(workArea);
   tileRect = new Rectangle(tileScreen.X - work.X, tileScreen.Y - work.Y, tileScreen.Width, tileScreen.Height);
   if (tileBody != null) tileBody.Dispose();
   tileBody = body;
   backdropSource = backdrop;
   material = null;
   EnsureSheetCache();
   CaptureTransitionGrid();
   progress.Snap(0);
   progress.Target = 1;
   justOpened = true;
   Render(); // the first frame is ready before the window becomes visible
  }

  internal void CloseFolder(Rectangle? desktopTile) {
   if (!Visible || IsClosing) return;
   if (editing) CommitInlineRename(true);
   if (liveDrag != null) EndLiveDrag(null);
   // A reversal keeps its existing path. A new close starts from the frame the user saw.
   if (progress.Value >= 1) {
    if (desktopTile.HasValue) {
     tileRect = desktopTile.Value;
     tileRect.Offset(-work.X, -work.Y);
    }
    CaptureTransitionGrid();
   }
   hoveredTile = null; pressedTile = null;
   progress.Target = 0;
   WakeAnimation();
  }

  // Click or key during the opening: land the panel immediately.
  internal void CompleteOpening() {
   if (!IsOpening) return;
   progress.Snap(1);
   titleReveal.Snap(1);
   LandedForRename();
   WakeAnimation();
  }

  // Advances p; returns true while the transition is moving.
  private bool StepTransition(double dt) {
   if (progress.Settled && progress.Value >= 1) return false;
   // The first frame of an open costs several ms (full clear + present). Don't let that
   // setup time count as motion, or frame two jumps ahead.
   if (justOpened) { justOpened = false; dt = Math.Min(dt, AnimationClock.FramePeriod); }
   bool moving = progress.Step(dt);
   // A spring creeps toward its end for a long time. Once nothing on screen is more than half
   // a pixel from where it ends, land now: otherwise icons crawl by sub-pixel amounts (soft,
   // edges shimmering) and then snap crisp - which reads as a small shake after opening.
   if (progress.Target >= 1 && (!moving || (1 - progress.Value) * TransitionSpan() < 0.5)) { progress.Snap(1); titleReveal.Snap(1); moving = false; LandedForRename(); }
   if (BackdropAlpha != null) BackdropAlpha(Math.Max(0.0, Math.Min(1.0, progress.Value * 1.25)));
   if (progress.Target <= 0 && (!moving || progress.Value * TransitionSpan() < 0.5)) {
    progress.Snap(0);
    collapsedNow = true;
    if (Landing != null) Landing(); // the desktop shows its folder icon again first...
    Hide();                          // ...then this layer goes: no blank frame between them
    if (Collapsed != null) Collapsed();
    return false;
   }
   return moving;
  }

  // ---- Geometry of the transition: everything is a function of p ------------------------

  internal struct TransitionItem {
   public bool Plus;
   public int PreviewSlot; // -1 for panel-only items
   public DeskItem Item;
   public string Label, SubLabel;
   public float PanelAlpha;
   public RectangleF Icon, LabelRect;
   public Rectangle FinalIcon;
   public float IconAlpha, LabelAlpha;
  }
  internal sealed class TransitionFrame {
   public RectangleF Frame, GridClip;
   public float Radius, FaceAlpha, TitleAlpha, Rim;
   public PointF TitleShift; // the title rides on the frame's top edge
   public readonly List<TransitionItem> Items = new List<TransitionItem>();
  }

  private readonly List<TransitionItem> transitionGrid = new List<TransitionItem>();
  private Rectangle transitionGridClip;

  private void CaptureTransitionGrid() {
   transitionGrid.Clear();
   transitionGridClip = GridClip();
   transitionGridClip.Offset(origin);
   var preview = new List<DeskItem>();
   if (!tileRect.IsEmpty) preview.AddRange(DesktopFolderForm.PreviewApps(group));
   var shared = new bool[preview.Count];
   foreach (var tile in tiles) {
    float x = tile.X.F, y = tile.Y.F - scroll.F;
    Rectangle icon = FolderLayout.IconInCell(x, y, tile.Scale.F);
    icon.Offset(origin);
    var label = new Rectangle(origin.X + (int)Math.Round(x - 6), origin.Y + (int)Math.Round(y + Metrics.IconTop + IconPx + Metrics.LabelGap), CellW + 12, 36);
    if (!transitionGridClip.IntersectsWith(icon) && !transitionGridClip.IntersectsWith(label)) continue;
    PanelEntry entry = tile.Entry;
    int slot = !pickerMode && entry != null && !entry.IsCrossGroup ? preview.IndexOf(entry.Item) : -1;
    if (slot >= 0) shared[slot] = true;
    transitionGrid.Add(new TransitionItem {
     Plus = tile.IsPlus, Item = entry == null ? null : entry.Item, PreviewSlot = slot,
     Label = tile.IsPlus ? "添加" : entry.Item.title, SubLabel = entry != null && entry.IsCrossGroup ? entry.GroupTitle : null,
     LabelRect = label, FinalIcon = icon, PanelAlpha = Math.Max(0f, Math.Min(1f, tile.Fade.F))
    });
   }
   // A filtered or scrolled panel may not show every item in the desktop preview.
   for (int i = 0; i < preview.Count; i++)
    if (!shared[i]) transitionGrid.Add(new TransitionItem { Item = preview[i], PreviewSlot = i });
   span = -1;
  }

  private Rectangle SheetInWindow() { return new Rectangle(origin.X, origin.Y + TitleBand, panelSize.Width, Math.Max(1, panelSize.Height - TitleBand)); }

  private static float Smooth(double x) { x = Math.Max(0, Math.Min(1, x)); return (float)(x * x * (3 - 2 * x)); }
  private static RectangleF Lerp(RectangleF a, RectangleF b, float t) {
   return new RectangleF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Width + (b.Width - a.Width) * t, a.Height + (b.Height - a.Height) * t);
  }
  // The same rectangle, carried proportionally from sheet S into the small frame T.
  private static RectangleF MapInto(RectangleF r, RectangleF S, RectangleF T) {
   float rx = T.Width / S.Width, ry = T.Height / S.Height, k = Math.Min(rx, ry);
   return new RectangleF(T.X + (r.X - S.X) * rx, T.Y + (r.Y - S.Y) * ry, r.Width * k, r.Height * k);
  }

  // Frame, icons, labels and title at progress p (window coordinates). Linear interpolation of
  // rectangles that start inside the folder icon and end inside the sheet stays inside the
  // interpolated frame, so icons can never run ahead of the glass.
  internal TransitionFrame Geometry(double p) {
   float t = (float)Math.Max(0, Math.Min(1, p));
   var f = new TransitionFrame();
   RectangleF S = SheetInWindow();
   int side = Metrics.DesktopIcon;
   RectangleF T = tileRect.IsEmpty ? new RectangleF(S.X + (S.Width - side) / 2, S.Y + (S.Height - side) / 2, side, side) : (RectangleF)tileRect;
   f.Frame = Lerp(T, S, t);
   f.GridClip = Lerp(T, transitionGridClip, t);
   f.Radius = Math.Min(Metrics.DesktopIconRadius + (SheetRadius - Metrics.DesktopIconRadius) * t, Math.Min(f.Frame.Width, f.Frame.Height) / 2f);
   f.FaceAlpha = 1f - Smooth(t / 0.35);
   // The title sits on the glass's top edge and moves with it, fading in over most of the
   // motion, so text and glass arrive together.
   f.TitleAlpha = Smooth((t - 0.2) / 0.7);
   f.TitleShift = new PointF(f.Frame.X + f.Frame.Width / 2f - (S.X + S.Width / 2f), f.Frame.Y - S.Y);
   f.Rim = 0.55f + 0.45f * t;
   foreach (var anchor in transitionGrid) {
    TransitionItem item = anchor;
    bool shared = item.PreviewSlot >= 0;
    RectangleF start = shared ? Metrics.IconPixels(Metrics.MiniSlot(item.PreviewSlot, T)) : MapInto(item.FinalIcon, S, T);
    if (item.FinalIcon.IsEmpty) {
     item.Icon = MapInto(start, T, f.Frame);
     item.IconAlpha = 1f - Smooth(t / 0.35);
    } else {
     item.Icon = Lerp(start, item.FinalIcon, t);
     item.IconAlpha = shared ? 1f + (item.PanelAlpha - 1f) * t : item.PanelAlpha * Smooth((t - 0.45) / 0.45);
     item.LabelRect.Offset(item.Icon.X + item.Icon.Width / 2f - (item.FinalIcon.X + item.FinalIcon.Width / 2f), item.Icon.Bottom - item.FinalIcon.Bottom);
     item.LabelAlpha = item.PanelAlpha * Smooth((t - 0.45) / 0.5);
    }
    f.Items.Add(item);
   }
   return f;
  }

  // Longest distance (px) anything travels between p = 0 and p = 1; fixed for one open.
  private float span = -1;
  private float TransitionSpan() {
   if (span >= 0) return span;
   TransitionFrame a = Geometry(0), b = Geometry(1);
   float m = MaxDelta(a.Frame, b.Frame);
   for (int i = 0; i < a.Items.Count && i < b.Items.Count; i++) m = Math.Max(m, MaxDelta(a.Items[i].Icon, b.Items[i].Icon));
   span = Math.Max(1f, m);
   return span;
  }
  private static float MaxDelta(RectangleF a, RectangleF b) {
   return Math.Max(Math.Max(Math.Abs(a.Left - b.Left), Math.Abs(a.Right - b.Right)), Math.Max(Math.Abs(a.Top - b.Top), Math.Abs(a.Bottom - b.Bottom)));
  }

  private Rectangle TransitionArea(TransitionFrame f) {
   Rectangle frame = Rectangle.FromLTRB((int)Math.Floor(f.Frame.Left), (int)Math.Floor(f.Frame.Top), (int)Math.Ceiling(f.Frame.Right), (int)Math.Ceiling(f.Frame.Bottom));
   Rectangle title = TitleRect();
   title.Offset(origin.X + (int)Math.Floor(f.TitleShift.X), origin.Y + (int)Math.Floor(f.TitleShift.Y));
   title.Inflate(10, 10);
   Rectangle area = Rectangle.Inflate(frame, 6, 6);
   foreach (var it in f.Items) if (it.LabelAlpha > 0.01f) area = Rectangle.Union(area, Rectangle.Inflate(Rectangle.Round(it.LabelRect), 2, 2));
   return f.TitleAlpha > 0.01f ? Rectangle.Union(area, title) : area;
  }

  private readonly System.Drawing.Imaging.ImageAttributes transitionAttributes = new System.Drawing.Imaging.ImageAttributes();
  private readonly System.Drawing.Imaging.ColorMatrix transitionMatrix = new System.Drawing.Imaging.ColorMatrix();
  private bool transitionWrapSet;
  private void DrawScaled(Graphics g, Image image, RectangleF dest, float alpha) {
   if (!transitionWrapSet) { transitionAttributes.SetWrapMode(WrapMode.TileFlipXY); transitionWrapSet = true; }
   if (image == null || alpha <= 0.01f || dest.Width < 0.5f || dest.Height < 0.5f) return;
   // Same path for every alpha: TileFlipXY stops the edge pixels from blending with
   // "outside the image", which otherwise makes edges flicker as the sub-pixel phase changes.
   transitionMatrix.Matrix33 = Math.Min(1f, alpha);
   transitionAttributes.SetColorMatrix(transitionMatrix);
   g.DrawImage(image, new[] { new PointF(dest.Left, dest.Top), new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) }, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, transitionAttributes);
  }

  private void DrawTransition(Graphics g, TransitionFrame f, bool glassPainted) {
   if (!glassPainted)
    using (var shape = Ui.Squircle(f.Frame, f.Radius))
     Material.Fill(g, shape, null, Material.SheetTint, Material.SheetFallback);
   // First frames: the glass still looks exactly like the desktop folder icon.
   if (tileBody != null && f.FaceAlpha > 0.01f) DrawScaled(g, tileBody, f.Frame, f.FaceAlpha);
   Material.Rim(g, f.Frame, f.Radius, f.Rim);
   DrawTransitionItems(g, f);
   if (f.TitleAlpha > 0.01f) {
    Rectangle r = TitleRect();
    Bitmap title = EnsureTitleCache(r);
    var at = new RectangleF(origin.X + r.X - 4 + f.TitleShift.X, origin.Y + r.Y - 2 + f.TitleShift.Y, title.Width, title.Height);
    DrawScaled(g, title, at, f.TitleAlpha);
   }
  }

  private void DrawTransitionItems(Graphics g, TransitionFrame f) {
   var state = g.Save();
   g.IntersectClip(f.GridClip);
   foreach (var it in f.Items) {
    if (it.IconAlpha > 0.005f) {
     Rectangle pixels = Metrics.IconPixels(it.Icon);
     if (it.Plus) DrawPlusGlyph(g, pixels, it.IconAlpha);
     else Ui.DrawIcon(g, app.GetIcon(it.Item, true), pixels, it.IconAlpha, blitAttributes, blitMatrix);
    }
    if (it.LabelAlpha > 0.01f) {
     Bitmap label = CachedLabel(it.Label, it.SubLabel, it.Plus ? 0.85f : 1f);
     int x = (int)Math.Round(it.LabelRect.X), y = (int)Math.Round(it.LabelRect.Y);
     if (it.LabelAlpha >= 0.995f) Ui.Blit(g, label, x, y);
     else {
      Rectangle ink = labelInk.GetValue(label, measureLabelInk).Bounds;
      if (!ink.IsEmpty) Ui.Blit(g, label, x, y, ink, it.LabelAlpha, blitAttributes, blitMatrix);
     }
    }
   }
   g.Restore(state);
  }

  private void DrawPlusGlyph(Graphics g, RectangleF icon, float alpha) {
   if (alpha <= 0.01f) return;
   float radius = icon.Width * 0.2237f;
   using (var path = Ui.Squircle(icon, radius))
   using (var b = new SolidBrush(Color.FromArgb((int)(36 * alpha), 255, 255, 255)))
   using (var pen = new Pen(Color.FromArgb((int)(70 * alpha), 255, 255, 255), 1f)) { g.FillPath(b, path); g.DrawPath(pen, path); }
   float arm = icon.Width * 0.2f, cx = icon.X + icon.Width / 2, cy = icon.Y + icon.Height / 2;
   using (var pen = new Pen(Color.FromArgb((int)(235 * alpha), 255, 255, 255), 2.6f * icon.Width / IconPx)) {
    pen.StartCap = pen.EndCap = LineCap.Round;
    g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
    g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
   }
  }

  // ---- Self-test: the transition is structurally in step ---------------------------------
  // Returns null when all invariants hold, otherwise a description of the first violation.
  internal string CheckTransitionInvariants() {
   // 1) icons never leave the glass; 2) everything moves monotonically with p.
   TransitionFrame prev = null;
   for (int k = 0; k <= 200; k++) {
    TransitionFrame f = Geometry(k / 200.0);
    foreach (var it in f.Items) {
     RectangleF fr = RectangleF.Inflate(f.Frame, 0.51f, 0.51f);
     RectangleF visible = RectangleF.Intersect(it.Icon, f.GridClip);
     if (it.IconAlpha > 0.005f && visible.Width > 0 && visible.Height > 0 && !fr.Contains(visible)) return "visible icon outside the frame at p=" + (k / 200.0);
    }
    if (prev != null) {
     for (int i = 0; i < f.Items.Count; i++) {
      RectangleF a = prev.Items[i].Icon, b = f.Items[i].Icon, end = f.Items[i].FinalIcon;
      if (f.Items[i].FinalIcon.IsEmpty) continue;
      if (Math.Sign(Math.Round(b.X - a.X, 4)) * Math.Sign(Math.Round(end.X - a.X, 4)) < 0 || Math.Sign(Math.Round(b.Y - a.Y, 4)) * Math.Sign(Math.Round(end.Y - a.Y, 4)) < 0)
       return "icon moved away from its target at p=" + (k / 200.0);
     }
    }
    prev = f;
   }
   // 3) at p = 1 every icon is exactly where the open panel draws it (whole pixels).
   TransitionFrame done = Geometry(1);
   if (done.Frame != (RectangleF)SheetInWindow()) return "frame does not land on the sheet";
   foreach (var it in done.Items) if (!it.FinalIcon.IsEmpty && it.Icon != (RectangleF)it.FinalIcon) return "icon does not land on its cell: " + it.Icon + " vs " + it.FinalIcon;
   // 4) reversing half-way is continuous: the frame never jumps between frames at 240 Hz.
   var spring = new Spring(0, 0.34, 1.0) { Target = 1 };
   float maxStep = 0, lastX = Geometry(0).Frame.X;
   for (int n = 0; n < 240; n++) {
    if (n == 30) spring.Target = 0;
    spring.Step(1 / 240.0);
    float x = Geometry(spring.Value).Frame.X;
    maxStep = Math.Max(maxStep, Math.Abs(x - lastX));
    lastX = x;
   }
   float span = Math.Abs(SheetInWindow().X - (tileRect.IsEmpty ? SheetInWindow().X : tileRect.X));
   if (maxStep > Math.Max(2f, span * 0.12f)) return "frame jumps " + maxStep + " px in one frame when reversed";
   return null;
  }

  // Compare the last animated pixels to the real grid painters, including clipped rows.
  internal string CheckTransitionHandoffs(int expectedAppCount, string nonAppQuery) {
   if (filtered.Count != expectedAppCount || filtered.Exists(entry => entry.Item.type != "app"))
    return "folder grid includes non-app entries or drops apps";
   string problem = CompareTransitionHandoffs();
   if (problem != null) return problem;
   int scrollLimit = Math.Max(0, (int)(contentHeight - GridViewport().Height));
   if (scrollLimit == 0) return null;
   scroll.Snap(scrollLimit - 0.5);
   CaptureTransitionGrid();
   problem = CompareTransitionHandoffs();
   if (problem != null) return "after scrolling: " + problem;
   searchReveal.Snap(1);
   scroll.Snap(0);
   searchQuery = nonAppQuery;
   RefreshData();
   if (filtered.Count != 0) return "search shows non-app entries";
   searchQuery = group.items.Find(item => item.type == "app").target;
   RefreshData();
   if (filtered.Count != 1 || filtered[0].Item.type != "app") return "search does not show the matching app";
   LayoutContent(true);
   CaptureTransitionGrid();
   return CompareTransitionHandoffs();
  }

  private string CompareTransitionHandoffs() {
   double nearEnd = 0.24 / TransitionSpan();
   byte[] desktop = GridPixels(tileRect, g => DesktopFolderForm.DrawMiniGrid(g, group, tileRect, blitAttributes, blitMatrix));
   if (!SamePixels(desktop, GridPixels(tileRect, g => DrawTransitionItems(g, Geometry(nearEnd))))) return "transition does not match the desktop preview";
   byte[] panel = GridPixels(transitionGridClip, g => {
    g.TranslateTransform(origin.X, origin.Y);
    g.IntersectClip(GridClip());
    DrawGrid(g, false);
   });
   if (!SamePixels(panel, GridPixels(transitionGridClip, g => DrawTransitionItems(g, Geometry(1 - nearEnd))))) return "transition does not match the panel grid";
   return null;
  }

  private static byte[] GridPixels(Rectangle area, Action<Graphics> draw) {
   var format = System.Drawing.Imaging.PixelFormat.Format32bppPArgb;
   using (var bitmap = new Bitmap(area.Width, area.Height, format)) {
    using (var g = Graphics.FromImage(bitmap)) {
     Ui.PrepareQuality(g);
     g.TranslateTransform(-area.X, -area.Y);
     draw(g);
    }
    var data = bitmap.LockBits(new Rectangle(Point.Empty, area.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly, format);
    try {
     var pixels = new byte[data.Stride * data.Height];
     Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
     return pixels;
    } finally { bitmap.UnlockBits(data); }
   }
  }

  private static bool SamePixels(byte[] a, byte[] b) {
   for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
   return true;
  }

  // Offscreen: open as if from a desktop folder at `tileScreen` (tests / bench).
  internal void PrepareTransitionForTest(Rectangle workArea, Rectangle tileScreen) {
   ResetTransientState();
   Prepare(workArea);
   tileRect = new Rectangle(tileScreen.X - work.X, tileScreen.Y - work.Y, tileScreen.Width, tileScreen.Height);
   EnsureSheetCache(); // as Open() does
   CaptureTransitionGrid();
   progress.Snap(0);
   progress.Target = 1;
  }
  internal double TransitionFrameMs(double p) {
   progress.Snap(p);
   long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
   Render();
   double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
   string dir = Environment.GetEnvironmentVariable("MACDESK_BENCH_FRAMES");
   if (dir != null && Math.Abs(p * 120 - Math.Round(p * 120)) < 1e-6 && (int)Math.Round(p * 120) % 12 == 0 && !System.IO.File.Exists(System.IO.Path.Combine(dir, "open_" + ((int)Math.Round(p * 120)).ToString("000") + ".png")))
    surface.SaveSnapshot(System.IO.Path.Combine(dir, "open_" + ((int)Math.Round(p * 120)).ToString("000") + ".png"));
   return ms;
  }

  // --- Geometry -------------------------------------------------------------------------

  private int GridTop() {
   if (pickerMode) return TitleBand + PadTop + PickerHeaderH + 12 + SearchH + SearchGap;
   return TitleBand + PadTop + (int)Math.Round(searchReveal.Value * (SearchH + SearchGap));
  }
  private Rectangle GridViewport() {
   int top = GridTop();
   return new Rectangle(PadX, top, Math.Max(1, PanelSize.Width - PadX * 2), Math.Max(1, PanelSize.Height - top - PadBottom));
  }
  private Rectangle SheetRect() { return new Rectangle(0, TitleBand, PanelSize.Width, Math.Max(1, PanelSize.Height - TitleBand)); }
  private string measuredTitle;
  private int measuredTitleWidth;
  private static readonly Bitmap measureBitmap = new Bitmap(1, 1);
  private Rectangle TitleRect() {
   string t = TitleText();
   if (t != measuredTitle) {
    using (var g = Graphics.FromImage(measureBitmap)) measuredTitleWidth = (int)Math.Ceiling(g.MeasureString(t, titleFont).Width) + 28;
    measuredTitle = t;
   }
   int w = Math.Min(PanelSize.Width - 40, Math.Max(120, measuredTitleWidth));
   return new Rectangle((PanelSize.Width - w) / 2, 8, w, TitleBand - 18);
  }
  private Rectangle SearchRect() {
   int top = pickerMode ? TitleBand + PadTop + PickerHeaderH + 12 : TitleBand + PadTop - (int)Math.Round((1 - searchReveal.Value) * 10);
   return new Rectangle(PadX + 4, top, PanelSize.Width - PadX * 2 - 8, SearchH);
  }
  private Rectangle PickerDoneBounds() { return new Rectangle(PadX, TitleBand + PadTop - 4, 72, PickerHeaderH); }
  private Rectangle PickerFolderBounds() { return new Rectangle(PanelSize.Width - PadX - 72, TitleBand + PadTop - 4, 72, PickerHeaderH); }
  private Rectangle PickerFileBounds() { return new Rectangle(PanelSize.Width - PadX - 72 - 66, TitleBand + PadTop - 4, 64, PickerHeaderH); }

  private string TitleText() { return pickerMode ? "添加到「" + (group != null ? group.title : "") + "」" : (group != null ? group.title ?? "文件夹" : "文件夹"); }

  private Size ComputePanelSize() {
   var catalog = app.Catalog.Items;
   return FolderLayout.PanelSize(work.Size, pickerMode, groupAppCount, catalog == null ? 0 : catalog.Count);
  }

  // --- Data -----------------------------------------------------------------------------

  public void RefreshData() {
   group = null;
   foreach (DeskGroup candidate in app.Data.groups)
    if (candidate.id == groupId) { group = candidate; break; }

   filtered.Clear();
   groupTargets.Clear();
   groupAppCount = 0;
   if (group != null) foreach (DeskItem item in group.items) {
    if (item.target != null) groupTargets.Add(item.target);
    if (item.type == "app") groupAppCount++;
   }
   string query = (searchQuery ?? "").Trim();
   if (group != null) {
    if (pickerMode) {
     var apps = app.Catalog.Items;
     for (int i = 0; apps != null && i < apps.Count; i++) {
      DeskItem item = apps[i];
      if (query.Length == 0 || (item.title ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
          item.target.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
       filtered.Add(new PanelEntry(item, i, group.id, group.title, false));
     }
    } else {
     for (int i = 0; i < group.items.Count; i++) {
      DeskItem item = group.items[i];
       if (item.type != "app") continue;
      if (query.Length == 0 || Contains(item.title, query) || Contains(item.description, query) || Contains(item.target, query))
       filtered.Add(new PanelEntry(item, i, group.id, group.title, false));
     }
     if (query.Length > 0) {
      foreach (DeskGroup other in app.Data.groups) {
       if (other.id == groupId) continue;
       for (int i = 0; i < other.items.Count; i++) {
        DeskItem item = other.items[i];
         if (item.type != "app") continue;
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
    gridColumns = FolderLayout.Columns(view.Width);
    float left = FolderLayout.GridLeft(view, gridColumns);
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

  private bool renderRequested, hadMotion;

  private void WakeAnimation() {
   if (IsDisposed || Disposing || !IsHandleCreated) return;
   if (!Visible) return;
   renderRequested = true;
   pump.Start();
  }

  private bool Tick(double dt) {
   bool requested = renderRequested;
   renderRequested = false;
   bool active = StepTransition(dt);
   if (collapsedNow) { collapsedNow = false; hadMotion = false; return false; }
   if (progress.Value < 1) { hadMotion = true; Render(); return active || renderRequested; }
   double searchBefore = searchReveal.Value;
   searchReveal.Target = (searchActive || pickerMode) && !pickerMode ? 1 : 0;
   active |= searchReveal.Step(dt);
   if (Math.Abs(searchReveal.Value - searchBefore) > 0.0001) LayoutContent(false);
   active |= scroll.Step(dt);
   bool holdingScrollbar = scrollbarHold > 0;
   if (holdingScrollbar) scrollbarHold -= dt;
   scrollbar.Target = holdingScrollbar ? 1 : 0;
   active |= scrollbar.Step(dt);
   active |= titleReveal.Step(dt);
   titleHover.Target = !pickerMode && TitleRect().Contains(mouseClient) && !editing ? 1 : 0;
   active |= titleHover.Step(dt);
   dropGlow.Target = backgroundDrop ? 1 : 0;
   active |= dropGlow.Step(dt);

   for (int i = 0; i < tiles.Count; i++) {
    TileView t = tiles[i];
    bool selected = t.Entry != null && t.Entry.Item.id == selectedId && (searchActive || keyboardSelection);
    // Hover never rescales the bitmap icon (sub-pixel resampling makes it shimmer); it raises
    // a vector highlight plate instead. Only a press shrinks the icon, in whole pixels.
    t.Scale.Target = t == pressedTile ? 0.86 : 1.0;
    t.Hover.Target = t == liveDrag || (t == hoveredTile && !draggingItem) ? 1 : 0;
    t.Select.Target = selected ? 1 : 0;
    t.Fade.Target = (draggingItem && liveDrag == null && i == dragSourceTileIndex) ? 0.35 : 1;
    if (t == liveDrag) { t.X.Snap(liveX); t.Y.Snap(liveY); } // the lifted icon follows the pointer exactly
    active |= t.Step(dt);
    if (t == liveDrag) active = true;
   }
   // Draw the settling frame, then leave static pixels alone during the scrollbar's hold.
   if (requested || active || hadMotion) Render();
   hadMotion = active;
   return active || holdingScrollbar || renderRequested;
  }
  private bool keyboardSelection;

  // Offscreen benchmark (MacDesk.exe --bench): wheel-scrolling the app picker, then a hover
  // sweep across the folder, using the real Tick/Render path.
  internal Bench.Stat[] Bench() {
   IntPtr h = Handle;
   const double dt = 1 / 240.0;
   string shots = Environment.GetEnvironmentVariable("MACDESK_BENCH_FRAMES");
   LayoutContent(true);
   Render();
   if (shots != null) surface.SaveSnapshot(System.IO.Path.Combine(shots, "panel.png"));
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
   // Live reorder: pick up icon #1, carry it onto cell #3, hold, then onto cell #6, hold, drop.
   // With MACDESK_BENCH_FRAMES=<dir> every 8th frame is saved as a PNG for visual review.
   var stats = new List<Bench.Stat> { scrollStat, hoverStat };
   string frameDir = Environment.GetEnvironmentVariable("MACDESK_BENCH_FRAMES");
   for (int i = 0; i < 40; i++) Tick(dt);
   TileView first = tiles.Find(t => t.Entry != null);
   int movableCount = tiles.FindAll(t => !t.IsPlus).Count;
   if (first != null && movableCount >= 6) {
    string firstId = first.Entry.Item.id;
    Point start = CellIconCenter(0);
    BeginLiveDrag(first, start, start);
    var path = new List<Point>();
    Point a = CellIconCenter(2), b = CellIconCenter(5);
    for (int i = 1; i <= 40; i++) path.Add(Lerp(start, a, i / 40f));
    for (int i = 0; i < 50; i++) path.Add(a);
    for (int i = 1; i <= 40; i++) path.Add(Lerp(a, b, i / 40f));
    for (int i = 0; i < 50; i++) path.Add(b);
    int n = 0;
    stats.Add(MacDeskNative.Bench.Time("panel live reorder drag", path.Count, delegate {
     UpdateLiveDrag(path[n]);
     Tick(dt);
     if (frameDir != null && n % 8 == 0) surface.SaveSnapshot(System.IO.Path.Combine(frameDir, "drag_" + n.ToString("000") + ".png"));
     n++;
    }));
    EndLiveDrag(b);
    for (int i = 0; i < 90; i++) { Tick(dt); if (frameDir != null && i % 15 == 0) surface.SaveSnapshot(System.IO.Path.Combine(frameDir, "drop_" + i.ToString("000") + ".png")); }
    int now = filtered.FindIndex(entry => entry.Item.id == firstId);
    var result = new Bench.Stat("panel reorder: #1 -> slot 6 " + (now == 5 ? "OK" : "WRONG(" + now + ")"));
    result.Add(0);
    stats.Add(result);
   }
   int key = 0;
   stats.Add(MacDeskNative.Bench.Time("folder type-to-search", 64, delegate {
    int index = key++ % 4;
    if (index == 3) HandleKey(Keys.Escape);
    else HandleChar("app"[index]);
    Tick(dt);
   }));
   return stats.ToArray();
  }

  private Point CellIconCenter(int slot) {
   Rectangle view = GridViewport();
   float left = FolderLayout.GridLeft(view, gridColumns);
   return new Point((int)(left + (slot % gridColumns) * (CellW + GapX) + CellW / 2f), (int)(view.Y + (slot / gridColumns) * (CellH + GapY) - (float)scroll.Value + 8 + IconPx / 2f));
  }
  private static Point Lerp(Point a, Point b, float t) { return new Point((int)Math.Round(a.X + (b.X - a.X) * t), (int)Math.Round(a.Y + (b.Y - a.Y) * t)); }


  // --- Rendering ------------------------------------------------------------------------

  // One renderer for every frame: the transition (p < 1) and the open panel (p = 1) are drawn
  // into the same window from the same geometry. The window follows the painted content.
  private void Render() {
   if (IsDisposed || Disposing || !IsHandleCreated || work.Width < 2 || work.Height < 2) return;
   TransitionFrame transition = progress.Value < 1.0 ? Geometry(progress.Value) : null;
   Rectangle panelArea = Rectangle.Inflate(new Rectangle(origin, panelSize), 40, 40);
   Rectangle workBounds = new Rectangle(Point.Empty, work.Size);
   Rectangle nextCanvas = Rectangle.Intersect(transition != null ? TransitionArea(transition) : panelArea, workBounds);
   if (nextCanvas.Width < 1 || nextCanvas.Height < 1) return;
   canvas = nextCanvas;
   surface.Reserve(Rectangle.Intersect(panelArea, workBounds).Size);
   var g = surface.Begin(canvas.Size);
   surface.Clear(new Rectangle(Point.Empty, canvas.Size));
   bool glassPainted = false;
   if (transition != null && material != null) {
    Rectangle clip = canvas;
    clip.Offset(work.Location);
    RectangleF shape = transition.Frame;
    shape.Offset(work.X, work.Y);
    int stride;
    IntPtr target = surface.FlushPixelBuffer(out stride);
    glassPainted = material.Paint(target, stride, clip, shape, transition.Radius);
   }
   g.TranslateTransform(-canvas.X, -canvas.Y);
   if (transition != null) DrawTransition(g, transition, glassPainted);
   else {
    g.TranslateTransform(origin.X, origin.Y);
    RenderCanvas(g, false);
   }
   g.ResetTransform();
   g.ResetClip();
   surface.Present(Handle, new Point(work.X + canvas.X, work.Y + canvas.Y), 255, null);
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
    var bar = new RectangleF(PanelSize.Width - 11, barY, 4.5f, barH);
    using (var path = Ui.Squircle(bar, 2.25f))
    using (var brush = new SolidBrush(Color.FromArgb((int)(150 * scrollbar.Value), 255, 255, 255))) g.FillPath(brush, path);
   }
  }

  // --- Render caches: every frame only blits pre-rendered pieces (240 Hz budget is ~4 ms) ---

  private Bitmap sheetCache, titleCache;
  private Point sheetCacheOrigin;
  private FolderGlass sheetCacheMaterial;
  private string titleCacheText;
  // Shared by every folder panel (and the picker): an icon or label is rendered once per process.
  private static readonly BitmapCache iconCache = new BitmapCache(384);
  private static readonly BitmapCache labelCache = new BitmapCache(384);
  private static readonly ConditionalWeakTable<Bitmap, LabelInk> labelInk = new ConditionalWeakTable<Bitmap, LabelInk>();
  private static readonly ConditionalWeakTable<Bitmap, LabelInk>.CreateValueCallback measureLabelInk = MeasureLabelInk;
  private readonly System.Drawing.Imaging.ImageAttributes blitAttributes = new System.Drawing.Imaging.ImageAttributes();
  private readonly System.Drawing.Imaging.ColorMatrix blitMatrix = new System.Drawing.Imaging.ColorMatrix();
  private readonly HashSet<string> groupTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

  // Renders every icon/label this folder (and optionally the app picker) will need, so the
  // first scroll or open never pays for icon extraction or text layout mid-animation.
  internal void WarmCaches(bool includePicker) {
   if (group != null) foreach (DeskItem item in group.items) {
    if (item.type != "app") continue;
    CachedIcon(item);
    CachedLabel(item.title, null, 1f);
   }
   CachedLabel("添加", null, 0.85f);
   if (includePicker) {
    app.Catalog.LoadForBenchmark();
    foreach (DeskItem item in app.Catalog.Items) { CachedIcon(item); CachedLabel(item.title, null, 1f); }
   }
  }

  private void DrawSheetBackground(Graphics g) {
   EnsureSheetCache();
   var mode = g.CompositingMode;
   g.CompositingMode = CompositingMode.SourceCopy;
   Ui.Blit(g, sheetCache, 0, 0);
   g.CompositingMode = mode;
  }

  // The frosted sheet depends on the backdrop captured for this open, so it is rebuilt once per
  // open (a few ms). Open() builds it before the first frame, while the folder still looks like
  // its desktop icon, so the landing frame never pays for it.
  private void EnsureSheetCache() {
   Point screen = PanelScreenRect().Location;
   EnsureMaterial();
   if (sheetCache == null || sheetCache.Size != PanelSize || sheetCacheOrigin != screen || sheetCacheMaterial != material) {
    if (sheetCache != null) sheetCache.Dispose();
    sheetCache = new Bitmap(Math.Max(1, PanelSize.Width), Math.Max(1, PanelSize.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
    Rectangle sheet = SheetRect(), screenSheet = sheet;
    screenSheet.Offset(screen);
    bool glassPainted = material != null && material.Paint(sheetCache, new Rectangle(screen, PanelSize), screenSheet, SheetRadius);
    using (var cg = Graphics.FromImage(sheetCache)) {
     Ui.PrepareQuality(cg);
     // The transparent band above the sheet still catches clicks (alpha 1/255): clicking
     // there closes the folder instead of falling through to whatever is underneath.
     using (var catcher = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) cg.FillRectangle(catcher, 0, 0, PanelSize.Width, TitleBand);
     if (!glassPainted)
      using (var shape = Ui.Squircle(sheet, SheetRadius))
       Material.Fill(cg, shape, null, Material.SheetTint, Material.SheetFallback);
     Material.Rim(cg, sheet, SheetRadius, 1f);
    }
    sheetCacheOrigin = screen;
    sheetCacheMaterial = material;
   }
  }

  // Shared with the desktop folder's 3x3 preview, so the mini icons on the desktop and the ones
  // that fly out of it are the same pixels' source.
  internal static Bitmap CachedIcon(DeskItem item) {
   string key = IconKey(item);
   Bitmap bmp;
   if (TryGetIcon(key, out bmp)) return bmp;
   bmp = LoadIcon(item);
   StoreIcon(key, bmp);
   return bmp;
  }

  internal static string IconKey(DeskItem item) { return item.type + "|" + item.target + "|" + item.icon; }
  internal static bool TryGetIcon(string key, out Bitmap bmp) { return iconCache.TryGet(key, out bmp); }
  internal static void StoreIcon(string key, Bitmap bmp) { iconCache.Add(key, bmp); }

  internal static Bitmap LoadIcon(DeskItem item) {
   Bitmap bmp = null;
   try {
    using (var source = DeskService.LoadIcon(item)) {
     bmp = new Bitmap(IconPx, IconPx, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
     using (var cg = Graphics.FromImage(bmp)) {
      Ui.PrepareQuality(cg);
      cg.DrawImage(source, new Rectangle(0, 0, IconPx, IconPx));
     }
    }
    return bmp;
   } catch {
    if (bmp != null) bmp.Dispose();
    return null;
   }
  }

  internal void RefreshIcons() { WakeAnimation(); }

  private Bitmap CachedLabel(string text, string sub, float bakedAlpha) {
   string key = (bakedAlpha < 1 ? "+" : "-") + (text ?? "") + "\u0001" + (sub ?? "");
   Bitmap bmp = labelCache.Get(key);
   if (bmp != null) return bmp;
   bmp = new Bitmap(CellW + 12, 36, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
   using (var cg = Graphics.FromImage(bmp))
   using (var f = Ui.TopCentered()) {
    cg.Clear(Color.Transparent);
    Ui.PrepareQuality(cg);
    var r = new RectangleF(8, 0, CellW - 4, 20);
    TextStyle.DrawShadowed(cg, text ?? "", Ui.Cjk, FontStyle.Regular, 13f, Ui.WithAlpha(Ui.Label, 0.97 * bakedAlpha), r, f, 0.4f, 0.45f);
    if (sub != null) TextStyle.Draw(cg, sub, Ui.Cjk, FontStyle.Regular, 11f, Ui.WithAlpha(Ui.SecondaryLabel, bakedAlpha), new RectangleF(8, 19, CellW - 4, 16), f, 0.25f);
   }
   labelCache.Add(key, bmp);
   return bmp;
  }

  private sealed class LabelInk {
   internal Rectangle Bounds;
  }

  // Color-matrix fades are expensive over the empty padding around each label.
  // Cache the alpha bounds with the bitmap, including the text shadow.
  private static LabelInk MeasureLabelInk(Bitmap bitmap) {
   var full = new Rectangle(Point.Empty, bitmap.Size);
   var data = bitmap.LockBits(full, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
   int left = full.Right, right = 0, top = full.Bottom, bottom = 0;
   try {
    var row = new int[full.Width];
    for (int y = 0; y < full.Height; y++) {
     Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
     for (int x = 0; x < row.Length; x++) {
      if (row[x] == 0) continue;
      left = Math.Min(left, x);
      right = Math.Max(right, x + 1);
      top = Math.Min(top, y);
      bottom = y + 1;
     }
    }
   } finally { bitmap.UnlockBits(data); }
   return new LabelInk { Bounds = right > left ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty };
  }

  private sealed class BitmapCache {
   private readonly int capacity;
   private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Bitmap>>> entries;
   private readonly LinkedList<KeyValuePair<string, Bitmap>> recent = new LinkedList<KeyValuePair<string, Bitmap>>();

   internal BitmapCache(int capacity) {
    this.capacity = capacity;
    entries = new Dictionary<string, LinkedListNode<KeyValuePair<string, Bitmap>>>(capacity, StringComparer.Ordinal);
   }

   internal Bitmap Get(string key) {
    Bitmap value;
    TryGet(key, out value);
    return value;
   }

   internal bool TryGet(string key, out Bitmap value) {
    LinkedListNode<KeyValuePair<string, Bitmap>> entry;
    if (!entries.TryGetValue(key, out entry)) { value = null; return false; }
    if (entry != recent.First) { recent.Remove(entry); recent.AddFirst(entry); }
    value = entry.Value.Value;
    return true;
   }

   internal void Add(string key, Bitmap bitmap) {
    if (entries.Count == capacity) {
     var oldest = recent.Last.Value;
     entries.Remove(oldest.Key);
     recent.RemoveLast();
     if (oldest.Value != null) oldest.Value.Dispose();
    }
    entries.Add(key, recent.AddFirst(new KeyValuePair<string, Bitmap>(key, bitmap)));
   }
  }

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
   Blit(g, EnsureTitleCache(r), r.X - 4, r.Y - 2, a);
  }

  private Bitmap EnsureTitleCache(Rectangle r) {
   string text = TitleText();
   if (titleCache == null || titleCacheText != text || titleCache.Width != r.Width + 8) {
    if (titleCache != null) titleCache.Dispose();
    titleCache = new Bitmap(r.Width + 8, r.Height + 8, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
    using (var cg = Graphics.FromImage(titleCache)) using (var f = Ui.Centered(true)) {
     cg.Clear(Color.Transparent);
     Ui.PrepareQuality(cg);
     TextStyle.DrawShadowed(cg, text, Ui.Cjk, FontStyle.Bold, 24f, Ui.Label, new RectangleF(4, 4, r.Width, r.Height), f, 0.2f, 0.9f);
    }
    titleCacheText = text;
   }
   return titleCache;
  }

  private void DrawPickerHeader(Graphics g) {
   Rectangle done = PickerDoneBounds(), file = PickerFileBounds(), folder = PickerFolderBounds();
   DrawTextButton(g, done, "完成", buttonBoldFont, Ui.Blue, done.Contains(mouseClient));
   DrawTextButton(g, file, "链接文件", buttonBoldFont, Ui.Blue, file.Contains(mouseClient));
   DrawTextButton(g, folder, "链接文件夹", buttonBoldFont, Ui.Blue, folder.Contains(mouseClient));
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
   TextField.DrawPill(g, r, r.Height / 2f, a, false);
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
     string hint = pickerMode ? (app.Catalog.Loading ? "正在收集应用…" : "搜索应用，或粘贴路径 / 网址后回车") : "搜索（含其他文件夹）";
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
   Rectangle clip = GridClip();
   TileView top = null;
   TileView lifted = snapshot ? null : liveDrag;
   for (int i = 0; i < tiles.Count; i++) {
    if (tiles[i] == lifted) continue;
    if (!snapshot && tiles[i] == pressedTile) { top = tiles[i]; continue; }
    float y = (snapshot ? tiles[i].Target.Y : tiles[i].Y.F) - scrollY;
    if (y > clip.Bottom || y + CellH + 16 < clip.Top) continue;
    DrawTile(g, tiles[i], snapshot, scrollY);
   }
   if (top != null) DrawTile(g, top, snapshot, scrollY);
   if (lifted != null) DrawTile(g, lifted, snapshot, scrollY); // the carried icon floats above all
  }

  private void DrawTile(Graphics g, TileView tile, bool snapshot, float scrollY) {
   float x = snapshot ? tile.Target.X : tile.X.F, y = (snapshot ? tile.Target.Y : tile.Y.F) - scrollY;
   if (y > PanelSize.Height || y + CellH < TitleBand) return;
   float scale = snapshot ? 1f : tile.Scale.F;
   float fade = snapshot ? 1f : Math.Max(0f, Math.Min(1f, tile.Fade.F));
   // Bitmap icons are only ever drawn at whole-pixel positions and even whole-pixel sizes
   // (centred), so their edges never land on half pixels and shimmer while animating.
   float cxf = x + CellW / 2f, cyf = y + Metrics.IconTop + IconPx / 2f;
   Rectangle iconBox = FolderLayout.IconInCell(x, y, scale);
   int sizeInt = iconBox.Width, ix = iconBox.X, iy = iconBox.Y;
   var icon = new RectangleF(ix, iy, sizeInt, sizeInt);

   if (!snapshot && tile.Select.Value > 0.01) {
    var hl = new RectangleF(x + 4, y + 1, CellW - 8, CellH - 4);
    using (var path = Ui.Squircle(hl, 18))
    using (var b = new SolidBrush(Color.FromArgb((int)(40 * tile.Select.Value), 255, 255, 255))) g.FillPath(b, path);
   }

   if (!snapshot && tile.Hover.Value > 0.005) {
    // Hover: a soft glass plate rises behind the icon (vector, so it animates smoothly).
    float h = (float)Math.Min(1.0, tile.Hover.Value);
    float grow = 5f + 4f * h;
    var plate = new RectangleF(cxf - IconPx / 2f - grow, cyf - IconPx / 2f - grow, IconPx + grow * 2, IconPx + grow * 2);
    float pr = IconPx * 0.2237f + grow * 0.8f;
    using (var path = Ui.Squircle(plate, pr))
    using (var b = new SolidBrush(Color.FromArgb((int)(34 * h), 255, 255, 255))) g.FillPath(b, path);
    Material.Rim(g, plate, pr, 0.55f * h);
   }

   if (tile.IsPlus) {
    DrawPlusGlyph(g, icon, fade);
    DrawLabel(g, "添加", null, x, y + 8 + IconPx, fade, 0.85f);
    return;
   }

   PanelEntry entry = tile.Entry;
   if (entry == null) return;
   Ui.DrawIcon(g, app.GetIcon(entry.Item, true), iconBox, fade, blitAttributes, blitMatrix);

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

  protected override void OnPaint(PaintEventArgs e) { }
  protected override void OnPaintBackground(PaintEventArgs e) { }

  // --- Window lifecycle -----------------------------------------------------------------

  private void ResizeAroundCenter(Size nextSize) {
   int cx = origin.X + panelSize.Width / 2, cy = origin.Y + panelSize.Height / 2;
   panelSize = nextSize;
   origin = new Point(Math.Max(12, Math.Min(work.Width - nextSize.Width - 12, cx - nextSize.Width / 2)), Math.Max(12, Math.Min(work.Height - nextSize.Height - 12, cy - nextSize.Height / 2)));
   EnsureMaterial();
   LayoutContent(true);
   Render();
  }

  protected override void OnVisibleChanged(EventArgs e) {
   base.OnVisibleChanged(e);
   if (outsideClicks == null) return;
   outsideClicks.Stop();
   if (!Visible) {
    pump.Stop();
    if (editing) CommitInlineRename(true);
    return;
   }
   if (IsDisposed || Disposing || app == null || app.Panel != this) return;
   outsideClicks.Start();
   WakeAnimation();
  }

  // --- Mouse ----------------------------------------------------------------------------

  private TileView HitTile(Point p) {
   Rectangle view = GridViewport();
   if (!Rectangle.FromLTRB(view.Left, view.Top - 4, view.Right, Math.Max(view.Bottom, PanelSize.Height - 8)).Contains(p)) return null;
   for (int i = 0; i < tiles.Count; i++) {
    TileView t = tiles[i];
    var r = new RectangleF(t.X.F, t.Y.F - (float)scroll.Value, CellW, CellH);
    if (r.Contains(p)) return t;
   }
   return null;
  }

  protected override void OnMouseMove(MouseEventArgs e) {
   base.OnMouseMove(e);
   if (progress.Value < 1 || IsClosing) return;
   e = ToPanel(e);
   Point previous = mouseClient;
   mouseClient = e.Location;
   if (liveDrag != null) { UpdateLiveDrag(e.Location); return; }
   if (!pickerMode && mouseArmed && pressedTile != null && !pressedTile.IsPlus && e.Button == MouseButtons.Left) {
    Size threshold = new Size(14, 14); // generous: a slightly shaky click must still launch
    Rectangle allowed = new Rectangle(mouseDownPoint.X - threshold.Width / 2, mouseDownPoint.Y - threshold.Height / 2, threshold.Width, threshold.Height);
    if (!allowed.Contains(e.Location)) {
     mouseArmed = false;
     TileView dragTile = pressedTile;
     pressedTile = null;
     if (dragTile.Entry != null && !dragTile.Entry.IsCrossGroup) BeginLiveDrag(dragTile, mouseDownPoint, e.Location);
     else StartItemDrag(dragTile);
     return;
    }
   }
   TileView hit = HitTile(e.Location);
   bool overButton = pickerMode
    ? (PickerDoneBounds().Contains(e.Location) || PickerFileBounds().Contains(e.Location) || PickerFolderBounds().Contains(e.Location))
    : TitleRect().Contains(e.Location);
   bool changed = hit != hoveredTile;
   hoveredTile = hit;
   Cursor = (hit != null || overButton) ? Cursors.Hand : Cursors.Default;
   if (pickerMode) {
    changed |= PickerDoneBounds().Contains(previous) != PickerDoneBounds().Contains(e.Location);
    changed |= PickerFileBounds().Contains(previous) != PickerFileBounds().Contains(e.Location);
    changed |= PickerFolderBounds().Contains(previous) != PickerFolderBounds().Contains(e.Location);
   } else changed |= TitleRect().Contains(previous) != overButton;
   if (changed) WakeAnimation();
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
   if (progress.Target < 1) return;
   CompleteOpening(); // a click while it is still opening lands it at once
   e = ToPanel(e);
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

  // --- Live reorder: the icon is picked up and follows the pointer; neighbours slide aside ---

  private TileView liveDrag;
  private float liveX, liveY;
  private PointF liveGrab;

  private void BeginLiveDrag(TileView tile, Point down, Point now) {
   liveDrag = tile;
   draggingItem = true;
   dragSourceTileIndex = tile.SlotIndex;
   dragHoverInsertIndex = tile.SlotIndex;
   liveGrab = new PointF(down.X - tile.X.F, down.Y - (tile.Y.F - (float)scroll.Value));
   hoveredTile = null;
   Capture = true;
   Cursor = Cursors.SizeAll;
   UpdateLiveDrag(now);
  }

  private void UpdateLiveDrag(Point p) {
   TileView tile = liveDrag;
   if (tile == null) return;
   // Carried out of the folder: hand over to a system drag so it can land on another folder.
   Rectangle inside = new Rectangle(-24, TitleBand - 24, PanelSize.Width + 48, PanelSize.Height - TitleBand + 48);
   if (!inside.Contains(p)) {
    EndLiveDrag(null);
    StartItemDrag(tile);
    return;
   }
   liveX = p.X - liveGrab.X;
   liveY = p.Y - liveGrab.Y + (float)scroll.Value;
   Rectangle view = GridViewport();
   float edge = p.Y < view.Top + 30 ? -10f : (p.Y > view.Bottom - 30 ? 10f : 0f);
   if (edge != 0f) {
    float maxScroll = Math.Max(0f, contentHeight - view.Height);
    scroll.Target = Math.Max(0f, Math.Min(maxScroll, scroll.Target + edge));
   }
   var center = new PointF(liveX + CellW / 2f, liveY - (float)scroll.Value + 8 + IconPx / 2f);
   int slot = LiveSlotAt(center, dragHoverInsertIndex);
   if (slot >= 0 && slot != dragHoverInsertIndex) {
    dragHoverInsertIndex = slot;
    LayoutContent(false); // neighbours get new spring targets and glide into place
   }
   WakeAnimation();
  }

  // drop == null cancels (no change); otherwise commits the order shown on screen.
  private void EndLiveDrag(Point? drop) {
   TileView tile = liveDrag;
   if (tile == null) return;
   // Commit exactly the arrangement on screen (the gap the user sees), not a re-hit-test.
   int slot = dragHoverInsertIndex;
   int modelInsert = ModelInsertForSlot(slot);
   bool commit = drop.HasValue && slot >= 0 && slot != dragSourceTileIndex && modelInsert >= 0 && tile.Entry != null;
   string itemId = tile.Entry != null ? tile.Entry.Item.id : null;
   liveDrag = null;
   draggingItem = false;
   dragSourceTileIndex = -1;
   dragHoverInsertIndex = -1;
   Capture = false;
   Cursor = Cursors.Default;
   if (commit && app.TryMoveItem(groupId, itemId, groupId, modelInsert)) RefreshData(); // tiles keep their springs and fly to the new order
   else LayoutContent(false); // springs carry the icon back to its slot
   WakeAnimation();
  }

  protected override void OnMouseCaptureChanged(EventArgs e) {
   base.OnMouseCaptureChanged(e);
   if (!Capture && liveDrag != null) EndLiveDrag(null);
  }

  protected override void OnMouseUp(MouseEventArgs e) {
   if (progress.Target < 1) { base.OnMouseUp(e); return; }
   e = ToPanel(e);
   if (liveDrag != null && e.Button == MouseButtons.Left) { EndLiveDrag(e.Location); base.OnMouseUp(e); return; }
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
   app.RefreshAppCatalog();
   RefreshData();
   ResizeAroundCenter(ComputePanelSize());
  }

  internal void UpdateAppCatalog() {
   if (!pickerMode || IsDisposed || Disposing || IsClosing) return;
   RefreshData();
   Size size = ComputePanelSize();
   if (size != panelSize) ResizeAroundCenter(size);
  }

  private void ExitPickerMode() {
   if (!pickerMode) return;
   pickerMode = false;
   searchActive = false;
   searchQuery = "";
   searchReveal.Snap(0);
   scroll.Snap(0);
   RefreshData();
   ResizeAroundCenter(ComputePanelSize());
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
   renameTitleOnLand = false;
   renamingFolderTitle = folder;
   renamingGroupId = gId;
   renamingItemId = iId;
   editField.Reset(text, true);
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
   Rectangle panel = PanelScreenRect();
   app.DesktopFolders.PlaceImeWindow(new Point(panel.X + (int)r.X + 12, panel.Y + (int)r.Bottom + 2));
  }

  private void DrawEditField(Graphics g, RectangleF r, Font font) {
   TextField.DrawPill(g, r, Math.Min(12f, r.Height / 2f), 1f, true);
   editField.Draw(g, new RectangleF(r.X + 8, r.Y, r.Width - 16, r.Height), font, true, null, Theme.Label);
  }

  private bool HandleEditKey(Keys keyData) {
   if (keyData == Keys.Enter) { CommitInlineRename(true); return true; }
   if (keyData == Keys.Escape) { CommitInlineRename(false); return true; }
   // Plain typing keys fall through so WM_CHAR (incl. IME text) still arrives.
   return editField.HandleKey(keyData);
  }

  private void HandleEditChar(char c) {
   editField.HandleChar(c);
  }

  private void CommitInlineRename(bool save) {
   if (!editing) return;
   bool isFolder = renamingFolderTitle;
   string gId = renamingGroupId, iId = renamingItemId, text = editField.Text;
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
   float left = FolderLayout.GridLeft(view, gridColumns);
   for (int i = 0; i < movable; i++) {
    // Compare against the unshifted logical slot so the live reflow cannot oscillate.
    float sx = left + (i % gridColumns) * (CellW + GapX) + CellW * 0.5f;
    float sy = view.Y + (i / gridColumns) * (CellH + GapY) - (float)scroll.Value + CellH * 0.5f;
    float d = (client.X - sx) * (client.X - sx) + (client.Y - sy) * (client.Y - sy);
    if (d < bestDist) { bestDist = d; bestSlot = i; }
   }
   modelInsertIndex = ModelInsertForSlot(bestSlot);
   return bestSlot;
  }

  // Map a visual slot back to the folder's real item index. Slots are positions in the
  // (possibly search-filtered) list, so they must never be used as model indices directly.
  private int ModelInsertForSlot(int slot) {
   if (slot < 0 || slot >= filtered.Count || filtered[slot].GroupId != groupId) return -1;
   int target = filtered[slot].Index;
   bool sameFolderDrag = dragSourceTileIndex >= 0 && dragSourceTileIndex < filtered.Count && filtered[dragSourceTileIndex].GroupId == groupId;
   // Same-folder reorder: TryMoveItem decrements insertIndex when old < insertIndex.
   return sameFolderDrag && filtered[dragSourceTileIndex].Index < target ? target + 1 : target;
  }

  // Live reorder target, iOS style: the gap moves only once the carried icon's centre is
  // clearly inside another cell (cells shrunk by a margin = hysteresis), so hovering near a
  // border never makes the neighbours shuffle back and forth. Past the last icon = last slot.
  private int LiveSlotAt(PointF center, int current) {
   Rectangle view = GridViewport();
   int movable = 0;
   foreach (var t in tiles) if (!t.IsPlus) movable++;
   if (movable == 0) return current;
   float left = FolderLayout.GridLeft(view, gridColumns), top = view.Y - (float)scroll.Value;
   RectangleF last = RectangleF.Empty;
   for (int i = 0; i < movable; i++) {
    var cell = new RectangleF(left + (i % gridColumns) * (CellW + GapX), top + (i / gridColumns) * (CellH + GapY), CellW, CellH);
    last = cell;
    cell.Inflate(-16, -18);
    if (cell.Contains(center)) return i;
   }
   if (center.Y > last.Bottom || (center.Y > last.Top && center.X > last.Right)) return movable - 1;
   return current;
  }

  private void DragOverContent(object sender, DragEventArgs e) {
   e.Effect = DragDropEffects.None;
   try {
    DropPayload payload = ReadDropPayload(e.Data, e.AllowedEffect);
    if (payload == null) { ClearDropFeedback(); return; }
    Point client = ToPanel(PointToClient(new Point(e.X, e.Y)));
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
    ComputeDropSlot(ToPanel(PointToClient(new Point(e.X, e.Y))), out index);
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
    menu.Items.Add("添加文件到托盘…", null, MenuAction(delegate { app.PickFilesToAdd(groupId); }));
    menu.Items.Add("添加目录到托盘…", null, MenuAction(delegate { app.PickFolderToAdd(groupId); }));
   } else {
    menu.Items.Add("添加应用…", null, MenuAction(delegate { EnterPickerMode(); }));
    menu.Items.Add("手动添加入口 / 网址…", null, MenuAction(delegate { if (group != null) app.AddItem(groupId); }));
    menu.Items.Add("搜索", null, MenuAction(delegate { FocusSearch(); }));
    menu.Items.Add("重命名文件夹", null, MenuAction(delegate { StartFolderTitleRename(); }));
   }
   menu.Items.Add(new ToolStripSeparator());
   menu.Items.Add("关闭", null, MenuAction(delegate { app.ClosePanel(); }));
   menu.Show(this, ToWindow(point));
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
    menu.Items.Add("在终端中打开", null, MenuAction(delegate { app.OpenEntryWith(itemGroupId, itemId, EntryAction.Terminal); }));
    if (DeskService.HasEditor(EntryAction.VSCode))
     menu.Items.Add("用 VS Code 打开", null, MenuAction(delegate { app.OpenEntryWith(itemGroupId, itemId, EntryAction.VSCode); }));
    if (DeskService.HasEditor(EntryAction.Cursor))
     menu.Items.Add("用 Cursor 打开", null, MenuAction(delegate { app.OpenEntryWith(itemGroupId, itemId, EntryAction.Cursor); }));
    menu.Items.Add("在资源管理器中显示", null, MenuAction(delegate { app.OpenEntryWith(itemGroupId, itemId, EntryAction.Reveal); }));
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
    int previous = index > 0 ? group.items.FindLastIndex(index - 1, candidate => candidate.type == "app") : -1;
    int next = group.items.FindIndex(index + 1, candidate => candidate.type == "app");
    ToolStripItem up = menu.Items.Add("前移", null, MenuAction(delegate { app.MoveItem(groupId, itemId, groupId, previous); }));
    up.Enabled = previous >= 0;
    ToolStripItem down = menu.Items.Add("后移", null, MenuAction(delegate { app.MoveItem(groupId, itemId, groupId, next + 1); }));
    down.Enabled = next >= 0;
   }

   menu.Items.Add(new ToolStripSeparator());
   ToolStripItem remove = menu.Items.Add("移除图标", null, MenuAction(delegate { app.DeleteItem(itemGroupId, itemId); }));
   remove.Tag = PanelDrawing.Destructive;
   menu.Show(this, ToWindow(point));
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

  // A press swallowed while paused is not replayed afterwards (clicking outside an open menu
  // closes just the menu, like macOS).
  private bool OutsideClicksPaused() {
   if (IsDisposed || Disposing || !Visible || app.Panel != this) { outsideClicks.Stop(); return true; }
   if (app.ModalInProgress || app.KeyboardPopupOpen || draggingItem || pendingMenuActions != 0) return true;
   foreach (ContextMenuStrip menu in activeMenus) if (!menu.IsDisposed && menu.Visible) return true;
   return false;
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
    if (DeskService.LooksLikeTarget(raw)) {
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
    if (outsideClicks != null) outsideClicks.Dispose();
    foreach (ContextMenuStrip menu in new List<ContextMenuStrip>(activeMenus)) menu.Dispose();
    activeMenus.Clear();
    surface.Dispose();
     material = sheetCacheMaterial = null;
    if (sheetCache != null) { sheetCache.Dispose(); sheetCache = null; }
    if (titleCache != null) { titleCache.Dispose(); titleCache = null; }
    blitAttributes.Dispose();
    transitionAttributes.Dispose();
    if (tileBody != null) { tileBody.Dispose(); tileBody = null; }
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
  const int TextInset = 12, ArrowInset = 24;
  static readonly Font MenuFont = Ui.MakeFont(Ui.Cjk, 15f, FontStyle.Regular);

  internal static void StyleMenu(ContextMenuStrip menu) {
   if (menu == null) return;
   menu.ShowImageMargin = false;
   menu.ShowCheckMargin = false;
   menu.Font = MenuFont;
   menu.Padding = new Padding(6, 6, 6, 6);
   menu.DropShadowEnabled = false; // the legacy square shadow showed as boxes at the rounded corners
   menu.BackColor = Theme.MenuBackground;
   menu.ForeColor = Theme.MenuText;
   menu.Renderer = new AppleMenuRenderer();
   menu.ItemAdded += delegate(object s, ToolStripItemEventArgs e) { Pad(e.Item); };
   foreach (ToolStripItem item in menu.Items) Pad(item);
   menu.Opened += delegate { Round(menu); };
   CloseOnOutsideClick(menu);
  }

  // Our menus open over the desktop / non-activating windows, so the desktop stays the
  // active window and WinForms' "click elsewhere closes the menu" (which relies on our app
  // losing activation) never fires. Watch the buttons ourselves while a menu is open and close
  // it on the first press outside it (or outside any submenu it has open).
  static void CloseOnOutsideClick(ToolStripDropDown menu) {
   var watcher = new OutsideClickWatcher(10);
   watcher.Paused = () => { if (menu.IsDisposed || !menu.Visible) { watcher.Stop(); return true; } return false; };
   watcher.Pressed = screen => { if (!Contains(menu, screen)) menu.Close(ToolStripDropDownCloseReason.AppClicked); };
   menu.Opened += delegate { watcher.Start(); };
   menu.Closed += delegate { watcher.Stop(); };
   menu.Disposed += delegate { watcher.Dispose(); };
  }
  static bool Contains(ToolStripDropDown dd, Point screen) {
   if (dd.Visible && dd.Bounds.Contains(screen)) return true;
   foreach (ToolStripItem item in dd.Items) {
    var mi = item as ToolStripMenuItem;
    if (mi != null && mi.HasDropDownItems && mi.DropDown.Visible && Contains(mi.DropDown, screen)) return true;
   }
   return false;
  }
  internal static void StyleDropDown(ToolStripMenuItem parent) {
   var dd = parent.DropDown as ToolStripDropDownMenu;
   if (dd == null) return;
   dd.ShowImageMargin = false;
   dd.ShowCheckMargin = false;
   dd.Font = MenuFont;
   dd.Padding = new Padding(6, 6, 6, 6);
   dd.DropShadowEnabled = false;
   dd.BackColor = Theme.MenuBackground;
   dd.Renderer = new AppleMenuRenderer();
   foreach (ToolStripItem item in dd.Items) Pad(item);
   dd.Opened += delegate { Round(dd); };
  }
  static void Pad(ToolStripItem item) {
   if (item is ToolStripSeparator) { item.Margin = new Padding(0, 3, 0, 3); return; }
   item.Padding = new Padding(TextInset, 6, ArrowInset, 6);
   item.Margin = new Padding(0, 1, 0, 1);
  }
  static void Round(ToolStripDropDown dd) {
   // Rounded corners from DWM, with a dark hairline instead of Windows 11's light default
   // border (DWMWA_BORDER_COLOR, COLORREF = 0x00BBGGRR).
   try {
    if (dd.IsHandleCreated) {
     int corner = 2; DwmSetWindowAttribute(dd.Handle, 33, ref corner, 4);
     int border = Theme.WindowBorderColorRef; DwmSetWindowAttribute(dd.Handle, 34, ref border, 4);
    }
   } catch { }
  }

  private sealed class AppleMenuRenderer : ToolStripProfessionalRenderer {
   public AppleMenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }
   protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {
    using (var b = new SolidBrush(Theme.MenuBackground)) e.Graphics.FillRectangle(b, e.AffectedBounds);
   }
   // Windows 11 draws the rounded hairline border for DWMWCP_ROUND popups itself.
   protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
   protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
   protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
    if (!e.Item.Selected || !e.Item.Enabled) return;
    var g = e.Graphics;
    var previous = g.SmoothingMode;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    var r = new RectangleF(1, 0, e.Item.Width - 2, e.Item.Height);
    bool destructive = e.Item.Tag == Destructive;
    using (var path = Ui.Squircle(r, 7))
    using (var b = new SolidBrush(destructive ? Theme.MenuDestructiveHighlight : Theme.MenuHighlight)) g.FillPath(b, path);
    g.SmoothingMode = previous;
   }
   protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
    bool destructive = e.Item.Tag == Destructive;
    Color color = !e.Item.Enabled ? Theme.MenuDisabledText : (e.Item.Selected ? Color.White : (destructive ? Theme.MenuDestructiveText : Theme.MenuText));
    var row = new Rectangle(TextInset, 0, Math.Max(0, e.Item.Width - TextInset - ArrowInset), e.Item.Height);
    TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, row, color, TextFormatFlags.SingleLine |
     TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.HidePrefix |
     TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
   }
   protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) {
    int y = e.Item.Height / 2;
    using (var pen = new Pen(Theme.MenuSeparator)) e.Graphics.DrawLine(pen, TextInset, y, e.Item.Width - TextInset, y);
   }
   protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) {
    e.ArrowColor = Theme.SecondaryLabel;
    base.OnRenderArrow(e);
   }
  }
  private sealed class MenuColors : ProfessionalColorTable {
   public override Color ToolStripDropDownBackground { get { return Theme.MenuBackground; } }
   public override Color MenuBorder { get { return Color.FromArgb(70, 70, 76); } }
   public override Color MenuItemBorder { get { return Color.Transparent; } }
   public override Color MenuItemSelected { get { return Theme.MenuHighlight; } }
   public override Color ImageMarginGradientBegin { get { return Theme.MenuBackground; } }
   public override Color ImageMarginGradientMiddle { get { return Theme.MenuBackground; } }
   public override Color ImageMarginGradientEnd { get { return Theme.MenuBackground; } }
  }

  internal static GraphicsPath Rounded(Rectangle rectangle, int radius) {
   return Ui.Squircle(rectangle, radius);
  }
 }
}
