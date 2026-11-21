using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDeskNative
{
    // All writes are delegated to AppController; this view never mutates the model.
    public class PanelForm : Form
    {
        private const string ItemFormat = "MacDeskNative.Item";
        private readonly AppController app;
        private readonly string groupId;
        private readonly TextBox search;
        private readonly Button closeButton, addButton;
        private readonly Panel grid;
        private PanelEntryTile[] tiles = new PanelEntryTile[0];
        private readonly List<PanelEntry> filtered = new List<PanelEntry>();
        private readonly HashSet<ContextMenuStrip> activeMenus = new HashSet<ContextMenuStrip>();
        private readonly Font bodyFont = new Font("Microsoft YaHei UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
        private DeskGroup group;
        private int gridColumns = 4, pendingMenuActions;
        private bool layoutBusy, dismissQueued, draggingItem;
        private string selectedId;
        private bool backgroundDrop;
        private bool glassActive;
        private Bitmap motionBackdrop;
        private Bitmap cachedMotion;
        internal bool GlassActive { get { return glassActive; } }
        private sealed class GlassGrid : Panel
        {
            readonly PanelForm owner;
            internal GlassGrid(PanelForm owner) { this.owner = owner; DoubleBuffered = true; }
            protected override void OnPaintBackground(PaintEventArgs e) { owner.DrawMaterialBackground(e.Graphics, this); }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            glassActive = GlassMaterial.Enable(Handle);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { DrawMaterialBackground(e.Graphics, this); }
        internal void DrawMaterialBackground(Graphics g, Control control)
        {
            if (motionBackdrop != null)
            {
                Point at = control == this ? Point.Empty : PointToClient(control.PointToScreen(Point.Empty));
                g.DrawImage(motionBackdrop, new Rectangle(0, 0, control.Width, control.Height), new Rectangle(at, control.Size), GraphicsUnit.Pixel);
            }
            else if (glassActive) GlassMaterial.ClearGlass(g, control.ClientSize);
            else g.Clear(Color.FromArgb(48, 62, 80));
        }
        internal bool HasCachedMotion(Size size) { return cachedMotion != null && cachedMotion.Width == size.Width && cachedMotion.Height == size.Height; }

        internal Bitmap MotionSnapshot()
        {
            if (Visible)
            {
                Bitmap visible = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try { using (Graphics g = Graphics.FromImage(visible)) g.CopyFromScreen(Location, Point.Empty, Size); return visible; }
                catch { visible.Dispose(); throw; }
            }
            if (cachedMotion != null && cachedMotion.Width == Width && cachedMotion.Height == Height)
                return (Bitmap)cachedMotion.Clone();
            using (Bitmap behind = GlassMaterial.Frost(Bounds))
            {
                motionBackdrop = behind;
                try
                {
                    Bitmap result = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    try { DrawToBitmap(result, ClientRectangle); }
                    catch { result.Dispose(); throw; }
                    if (cachedMotion != null) cachedMotion.Dispose();
                    cachedMotion = result;
                    return (Bitmap)result.Clone();
                }
                finally { motionBackdrop = null; }
            }
        }
        private readonly Timer outsideClickTimer;
        private bool leftWasDown, rightWasDown;
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, string lParam);

        public string GroupId { get { return groupId; } }

        public PanelForm(AppController app, string groupId)
        {
            this.app = app;
            this.groupId = groupId;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(780, 470);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            ForeColor = Color.FromArgb(241, 244, 252);
            Font = bodyFont;
            DoubleBuffered = true;
            KeyPreview = true;
            AllowDrop = true;
            AccessibleName = "文件夹快捷入口面板";
            Text = "MacDesk 文件夹";

            closeButton = MakeButton("×", "关闭文件夹面板", new Rectangle(712, 22, 44, 40));
            closeButton.Font = new Font("Segoe UI", 28, FontStyle.Regular, GraphicsUnit.Pixel);
            closeButton.Click += delegate { app.ClosePanel(); };
            // Icon-only phone folder: no title, no search, no hint text. The native editor stays hidden
            // so existing filtering/drop plumbing keeps compiling and no IME surface appears.
            search = new GlassSearchBox();
            search.SetBounds(0, 0, 1, 1);
            search.BorderStyle = BorderStyle.None;
            search.BackColor = Color.FromArgb(35, 50, 70);
            search.ForeColor = Color.FromArgb(253, 254, 255);
            search.Font = new Font("Microsoft YaHei UI", 20, FontStyle.Regular, GraphicsUnit.Pixel);
            search.AccessibleName = "搜索入口名称或描述";
            search.Visible = false;
            search.TabStop = false;
            search.TabIndex = 0;
            search.TextChanged += delegate { if (grid != null) grid.AutoScrollPosition = Point.Empty; RefreshData(); };
            Controls.Add(search);
            search.CreateControl();
            AttachDrop(search);
            grid = new GlassGrid(this);
            grid.Name = "FolderIconGrid";
            grid.AccessibleName = "文件夹全部入口图标，可滚动";
            grid.BackColor = BackColor;
            grid.AutoScroll = true;
            grid.TabIndex = 1;
            Controls.Add(grid);
            AttachDrop(grid);
            grid.Scroll += delegate { ClearDropFeedback(); };
            addButton = MakeButton("＋ 添加入口", "添加入口", Rectangle.Empty);
            addButton.Visible = false;
            addButton.Click += delegate { app.AddItem(groupId); };
            AttachDrop(this);
            AttachDrop(addButton);
            RefreshData();
            ClientSize = GetExpandedSize(new Point(Left + Width / 2, Top + Height / 2));
            LayoutContent();
            UpdateRoundedRegion();
            outsideClickTimer = new Timer();
            outsideClickTimer.Interval = 30;
            outsideClickTimer.Tick += PollOutsideClick;
        }

        private sealed class GlassSearchBox : TextBox
        {
            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint="GetWindowLongW")]static extern int GetStyle(IntPtr h,int index);
            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint="SetWindowLongW")]static extern int SetStyle(IntPtr h,int index,int value);
            [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool SetLayeredWindowAttributes(IntPtr h,int color,byte alpha,int flags);
            [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr h,int message,IntPtr wParam,IntPtr lParam);
            // Keep the native editor (IME, caret, selection) but round it into a phone-style search pill.
            void ApplyPill()
            {
                if(Handle==IntPtr.Zero)return;
                SendMessage(Handle,0xD3,(IntPtr)3,(IntPtr)((18&0xFFFF)|((18&0xFFFF)<<16)));
            }
            protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SetStyle(Handle,-20,GetStyle(Handle,-20)|0x80000); SetLayeredWindowAttributes(Handle,0,255,2); ApplyPill(); }
            protected override void OnResize(EventArgs e) { base.OnResize(e); }
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if(m.Msg==0xF && Text.Length==0 && !Focused)
                    using(Graphics g=Graphics.FromHwnd(Handle))
                        TextRenderer.DrawText(g,"搜索名称或描述 · Ctrl+F",Font,ClientRectangle,Color.FromArgb(250,252,255),TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine);
            }
        }

        private sealed class ReadableDisabledButton : Button
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                if (Enabled) { base.OnPaint(e); return; }
                e.Graphics.Clear(BackColor);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(130, 141, 160),
                    TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        private Button MakeButton(string text, string name, Rectangle bounds)
        {
            Button button = new ReadableDisabledButton();
            button.Text = text;
            button.AccessibleName = name;
            button.Bounds = bounds;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.Transparent;
            button.FlatAppearance.MouseDownBackColor = Color.Transparent;
            button.BackColor = Color.Transparent;
            button.ForeColor = Color.FromArgb(232, 239, 253);
            button.Font = bodyFont;
            button.Cursor = Cursors.Hand;
            Controls.Add(button);
            return button;
        }

        public void RefreshData()
        {
            group = null;
            foreach (DeskGroup candidate in app.Data.groups)
                if (candidate.id == groupId) { group = candidate; break; }
            filtered.Clear();
            string query = search.Text.Trim();
            if (group != null)
            {
                for (int i = 0; i < group.items.Count; i++)
                {
                    DeskItem item = group.items[i];
                    if (query.Length == 0 || Contains(item.title, query) || Contains(item.description, query))
                        filtered.Add(new PanelEntry(item, i));
                }
                Text = "MacDesk · " + group.title;
                AccessibleName = group.title + " 文件夹快捷入口面板";
            }
            bool selectionVisible = false;
            foreach (PanelEntry entry in filtered) if (entry.Item.id == selectedId) selectionVisible = true;
            if (!selectionVisible) selectedId = null;
            EnsureTiles(filtered.Count + 1);
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i].Entry = i < filtered.Count ? filtered[i] : null;
                tiles[i].Selected = tiles[i].Entry != null && tiles[i].Entry.Item.id == selectedId;
                tiles[i].DropSide = 0;
                tiles[i].AccessibleName = tiles[i].Entry == null ? "添加入口" : tiles[i].Entry.Item.title + "，" + tiles[i].Entry.Item.description;
                tiles[i].Invalidate();
            }
            addButton.Enabled = group != null;
            LayoutContent();
            if (cachedMotion != null) { cachedMotion.Dispose(); cachedMotion = null; }
            Invalidate();
        }

        private void EnsureTiles(int count)
        {
            if (tiles.Length == count) return;
            PanelEntryTile[] next = new PanelEntryTile[count];
            int retained = Math.Min(count, tiles.Length);
            Array.Copy(tiles, next, retained);
            grid.SuspendLayout();
            try
            {
                for (int i = count; i < tiles.Length; i++) { grid.Controls.Remove(tiles[i]); tiles[i].Dispose(); }
                for (int i = retained; i < count; i++)
                {
                    next[i] = new PanelEntryTile(this);
                    next[i].TabIndex = i;
                    grid.Controls.Add(next[i]);
                    AttachDrop(next[i]);
                }
                tiles = next;
            }
            finally { grid.ResumeLayout(); }
        }

        // The controller owns bounds/opacity animation; ask for this target BEFORE animating.
        public Size GetExpandedSize(Point sourceCenter)
        {
            Rectangle area = Screen.FromPoint(sourceCenter).WorkingArea;
            // Icon-only phone folder: a compact sheet of glass-covered app icons plus one "+" slot.
            int cells = Math.Max(1, filtered.Count) + 1;
            int columns = Math.Max(1, Math.Min(5, cells));
            int rows = (cells + columns - 1) / columns;
            int width = Math.Min(columns * 150 + (columns - 1) * 18 + 91, area.Width - 64);
            int height = Math.Min(rows * 168 + 88, (int)(area.Height * .86));
            return new Size(Math.Max(240, width), Math.Max(180, height));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (grid == null || addButton == null) return;
            LayoutContent();
            UpdateRoundedRegion();
            Invalidate();
        }

        private void LayoutContent()
        {
            if (layoutBusy || grid == null || addButton == null) return;
            layoutBusy = true;
            grid.SuspendLayout();
            try
            {
                closeButton.SetBounds(Math.Max(6, ClientSize.Width - 52), 10, 40, 40);
                grid.SetBounds(36, 56, Math.Max(1, ClientSize.Width - 72), Math.Max(1, ClientSize.Height - 88));
                int scroll = -grid.AutoScrollPosition.Y;
                grid.AutoScrollPosition = Point.Empty;
                int available = Math.Max(1, grid.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
                gridColumns = Math.Max(1, Math.Min(5, Math.Max(1, tiles.Length)));
                int tileWidth = Math.Max(96, (available - (gridColumns - 1) * 18) / gridColumns);
                for (int i = 0; i < tiles.Length; i++)
                    tiles[i].SetBounds((i % gridColumns) * (tileWidth + 18), (i / gridColumns) * 168, tileWidth, 150);
                int rows = (tiles.Length + gridColumns - 1) / gridColumns;
                grid.AutoScrollMinSize = new Size(0, rows * 168);
                grid.AutoScrollPosition = new Point(0, scroll);
            }
            finally { grid.ResumeLayout(); layoutBusy = false; }
        }

        private void ScrollGrid(int direction)
        {
            grid.AutoScrollPosition = new Point(0, Math.Max(0, -grid.AutoScrollPosition.Y + direction * Math.Max(30, grid.ClientSize.Height - 24)));
        }

        private static bool Contains(string text, string query)
        {
            return (text ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        private void UpdateRoundedRegion()
        {
            // Let the compositor clip the curved edges anti-aliased; hand-clipped regions leave stair steps.
            int preference = 2;
            if (Handle == IntPtr.Zero || DwmSetWindowAttribute(Handle, 33, ref preference, 4) != 0)
            {
                using (GraphicsPath path = PanelDrawing.Rounded(new Rectangle(0, 0, Width, Height), 24))
                {
                    Region old = Region;
                    Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Match the compositor's own rounded clip so the outline never doubles up at the corners.
            using (Pen border = new Pen(Color.FromArgb(150, 236, 246, 255)))
            using (GraphicsPath shape = PanelDrawing.Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 9))
                g.DrawPath(border, shape);
            if (backgroundDrop)
            {
                using (Pen pen = new Pen(Color.FromArgb(150, 175, 235, 255), 3))
                using (GraphicsPath path = PanelDrawing.Rounded(new Rectangle(5, 5, Width - 11, Height - 11), 21))
                    g.DrawPath(pen, path);
            }
        }

        internal void SelectEntry(PanelEntryTile tile)
        {
            selectedId = tile.Entry == null ? null : tile.Entry.Item.id;
            foreach (PanelEntryTile other in tiles)
            {
                other.Selected = other.Entry != null && other.Entry.Item.id == selectedId;
                other.Invalidate();
            }
        }

        internal void LaunchEntry(PanelEntryTile tile)
        {
            if (tile.Entry != null) app.Launch(groupId, tile.Entry.Item.id);
            else if (group != null) app.AddItem(groupId);
        }

        internal void StartItemDrag(PanelEntryTile tile)
        {
            if (tile.Entry == null) return;
            Exception failure = null;
            draggingItem = true;
            try
            {
                DataObject data = new DataObject();
                data.SetData(ItemFormat, groupId + "\n" + tile.Entry.Item.id);
                PerformItemDrag(tile, data);
            }
            catch (Exception ex) { failure = ex; }
            finally { draggingItem = false; ClearDropFeedback(); QueueOutsideClose(); }
            if (failure != null) app.ReportError(failure);
        }

        protected virtual DragDropEffects PerformItemDrag(Control source, IDataObject data)
        {
            return source.DoDragDrop(data, DragDropEffects.Move);
        }

        // Modal UI/model refresh must run after ToolStrip has unwound its click/close stack.
        private EventHandler MenuAction(Action action)
        {
            return delegate
            {
                if (IsDisposed || Disposing) return;
                if (!IsHandleCreated) { action(); return; }
                pendingMenuActions++;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try { if (!IsDisposed && !Disposing) action(); }
                        finally { pendingMenuActions--; QueueOutsideClose(); }
                    });
                }
                catch { pendingMenuActions--; throw; }
            };
        }

        internal void ShowEntryMenu(PanelEntryTile tile, Point point)
        {
            ContextMenuStrip menu = CreateEntryMenu(tile);
            if (menu != null) menu.Show(tile, point);
        }

        internal ContextMenuStrip CreateEntryMenu(PanelEntryTile tile)
        {
            SelectEntry(tile);
            if (tile.Entry == null) return null;
            string itemId = tile.Entry.Item.id;
            int index = tile.Entry.Index;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = bodyFont;
            activeMenus.Add(menu);
            menu.Disposed += delegate { activeMenus.Remove(menu); };
            menu.Items.Add("打开", null, MenuAction(delegate { app.Launch(groupId, itemId); }));
            menu.Items.Add("编辑入口…", null, MenuAction(delegate { app.EditItem(groupId, itemId); }));
            menu.Items.Add(new ToolStripSeparator());
            ToolStripItem up = menu.Items.Add("上移", null, MenuAction(delegate { app.MoveItem(groupId, itemId, groupId, index - 1); }));
            up.Enabled = index > 0;
            ToolStripItem down = menu.Items.Add("下移", null, MenuAction(delegate { app.MoveItem(groupId, itemId, groupId, index + 2); }));
            down.Enabled = group != null && index < group.items.Count - 1;
            ToolStripMenuItem move = new ToolStripMenuItem("移动到文件夹");
            foreach (DeskGroup other in app.Data.groups)
            {
                if (other.id == groupId) continue;
                string target = other.id;
                move.DropDownItems.Add((other.title ?? "未命名").Replace("&", "&&"), null, MenuAction(delegate { app.MoveItem(groupId, itemId, target, -1); }));
            }
            move.Enabled = move.DropDownItems.Count > 0;
            menu.Items.Add(move);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("删除入口（不删除原文件）", null, MenuAction(delegate { app.DeleteItem(groupId, itemId); }));
            menu.Closed += delegate
            {
                if (!IsDisposed && !Disposing && IsHandleCreated)
                    BeginInvoke((MethodInvoker)delegate { menu.Dispose(); QueueOutsideClose(); });
                else menu.Dispose();
            };
            return menu;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (outsideClickTimer == null) return; // Form construction can raise visibility events.
            outsideClickTimer.Stop();
            if (IsDisposed || Disposing || !Visible || app == null || app.Panel != this) return;
            // Seed both HIGH bits: opening while a button is held is not a new outside click.
            leftWasDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            rightWasDown = (GetAsyncKeyState(0x02) & 0x8000) != 0;
            outsideClickTimer.Start();
        }

        private void PollOutsideClick(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing || !Visible || app.Panel != this)
            {
                outsideClickTimer.Stop();
                return;
            }
            // Poll only. Do not consume the unreliable LOW bit, capture input, or install a hook.
            bool left = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            bool right = (GetAsyncKeyState(0x02) & 0x8000) != 0;
            bool pressedEdge = (left && !leftWasDown) || (right && !rightWasDown);
            leftWasDown = left;
            rightWasDown = right;
            // Always consume samples during modal/menu/drag activity: no deferred held-button close.
            if (!pressedEdge || app.ModalInProgress || draggingItem || pendingMenuActions != 0) return;
            foreach (ContextMenuStrip menu in activeMenus) if (!menu.IsDisposed && menu.Visible) return;
            // No ContainsFocus test: clicking transparent Shell background may leave this form active.
            if (!Bounds.Contains(Control.MousePosition) && !app.SourceContains(Control.MousePosition)) app.ClosePanel();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            QueueOutsideClose();
        }

        private void QueueOutsideClose()
        {
            if (dismissQueued || IsDisposed || Disposing || !IsHandleCreated || !Visible) return;
            dismissQueued = true;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    dismissQueued = false;
                    if (IsDisposed || Disposing || !Visible || app.Panel != this || ContainsFocus || draggingItem || pendingMenuActions != 0 || app.ModalInProgress) return;
                    foreach (ContextMenuStrip menu in activeMenus) if (!menu.IsDisposed && menu.Visible) return;
                    if (app.SourceContains(Control.MousePosition) && (((GetAsyncKeyState(1) | GetAsyncKeyState(2)) & 0x8000) != 0)) return;
                    // An old cached folder must not dismiss a newly activated folder.
                    if (Form.ActiveForm is PanelForm && Form.ActiveForm != this) return;
                    app.ClosePanel();
                });
            }
            catch (InvalidOperationException) { dismissQueued = false; }
        }

        private void AttachDrop(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += DragOverContent;
            control.DragOver += DragOverContent;
            control.DragLeave += delegate { ClearDropFeedback(); };
            control.DragDrop += DropContent;
        }

        private sealed class DropPayload
        {
            internal string SourceGroup, ItemId, Url;
            internal string[] Paths;
            internal DragDropEffects Effect;
        }

        // Parse each provider only once per event. Never re-read external COM data
        // after validation: a delayed-rendering source may change or disconnect.
        private DropPayload ReadDropPayload(IDataObject data, DragDropEffects allowed)
        {
            if (group == null || data == null || allowed == DragDropEffects.None) return null;
            if (data.GetDataPresent(ItemFormat))
            {
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
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                // References only: never tell Explorer that source files were moved.
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

        private int InsertionIndex(Point client, bool feedback)
        {
            int result = -1;
            Point gridPoint = grid.PointToClient(PointToScreen(client));
            foreach (PanelEntryTile tile in tiles)
            {
                int side = 0;
                if (grid.ClientRectangle.Contains(gridPoint) && tile.Bounds.Contains(gridPoint))
                {
                    if (tile.Entry != null)
                    {
                        side = gridPoint.X - tile.Left < tile.Width / 2 ? -1 : 1;
                        result = tile.Entry.Index + (side > 0 ? 1 : 0);
                    }
                    else side = 2;
                }
                if (feedback && tile.DropSide != side) { tile.DropSide = side; tile.Invalidate(); }
            }
            return result;
        }

        private void DragOverContent(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            try
            {
                DropPayload payload = ReadDropPayload(e.Data, e.AllowedEffect);
                if (payload == null) { ClearDropFeedback(); return; }
                Point client = PointToClient(new Point(e.X, e.Y));
                Point overGrid = grid.PointToClient(new Point(e.X, e.Y));
                if (grid.ClientRectangle.Contains(overGrid))
                {
                    int delta = overGrid.Y < 22 ? -22 : (overGrid.Y > grid.ClientSize.Height - 22 ? 22 : 0);
                    if (delta != 0) grid.AutoScrollPosition = new Point(0, Math.Max(0, -grid.AutoScrollPosition.Y + delta));
                }
                InsertionIndex(client, true);
                backgroundDrop = true;
                e.Effect = payload.Effect;
                Invalidate();
            }
            catch (Exception)
            {
                e.Effect = DragDropEffects.None;
                // COM providers can disappear during hover. Do not open a modal
                // error dialog from DragOver (re-entrant hover could spam dialogs).
                ClearDropFeedback();
            }
        }

        private void DropContent(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            ClearDropFeedback();
            try
            {
                DropPayload payload = ReadDropPayload(e.Data, e.AllowedEffect);
                if (payload == null) return;
                int index = InsertionIndex(PointToClient(new Point(e.X, e.Y)), false);
                bool committed;
                if (payload.ItemId != null)
                    committed = app.TryMoveItem(payload.SourceGroup, payload.ItemId, groupId, index);
                else if (payload.Paths != null)
                    committed = app.TryAddPaths(groupId, payload.Paths, index);
                else
                    committed = app.TryAddUrl(groupId, payload.Url, index);
                // A rejected save/CAS must not be acknowledged as a successful drop.
                if (committed) e.Effect = payload.Effect;
            }
            catch (Exception ex)
            {
                e.Effect = DragDropEffects.None;
                app.ReportError(ex);
            }
            finally { ClearDropFeedback(); }
        }

        private void ClearDropFeedback()
        {
            backgroundDrop = false;
            foreach (PanelEntryTile tile in tiles)
                if (tile.DropSide != 0) { tile.DropSide = 0; tile.Invalidate(); }
            Invalidate();
        }

        internal void FocusSearch() { Activate(); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { app.ClosePanel(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { return true; }
            if (keyData == (Keys.Control | Keys.N)) { if (group != null) app.AddItem(groupId); return true; }
            {
                if (keyData == Keys.PageUp) { ScrollGrid(-1); return true; }
                if (keyData == Keys.PageDown) { ScrollGrid(1); return true; }
                if (selectedId != null && keyData == Keys.Delete) { app.DeleteItem(groupId, selectedId); return true; }
                if (selectedId != null && keyData == Keys.F2) { app.EditItem(groupId, selectedId); return true; }
                if (selectedId != null && keyData == Keys.Enter) { app.Launch(groupId, selectedId); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (outsideClickTimer != null) { outsideClickTimer.Stop(); outsideClickTimer.Dispose(); }
                foreach (ContextMenuStrip menu in new List<ContextMenuStrip>(activeMenus)) menu.Dispose();
                activeMenus.Clear();
                if (cachedMotion != null) { cachedMotion.Dispose(); cachedMotion = null; }
                closeButton.Font.Dispose();
                search.Font.Dispose();
                bodyFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class PanelEntry
    {
        internal readonly DeskItem Item;
        internal readonly int Index;
        internal PanelEntry(DeskItem item, int index) { Item = item; Index = index; }
    }

    internal sealed class PanelEntryTile : Control
    {
        private readonly PanelForm owner;
        private readonly Font titleFont = new Font("Microsoft YaHei UI", 17, FontStyle.Regular, GraphicsUnit.Pixel);
        private Point down;
        private bool armed, dragged, hover;
        internal PanelEntry Entry;
        internal bool Selected;
        internal int DropSide;

        internal PanelEntryTile(PanelForm owner)
        {
            this.owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            BackColor = Color.Black;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { owner.DrawMaterialBackground(e.Graphics, this); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Each entry is a glass cover with just the app icon inside - no labels, no captions.
            Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = Math.Max(10, Math.Min(26, Math.Min(Width, Height) / 6));
            using (GraphicsPath path = PanelDrawing.Rounded(box, radius))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(hover ? 74 : 34, 240, 248, 255)))
            using (Pen border = new Pen(Selected || Focused || DropSide != 0 ? Color.FromArgb(215, 150, 195, 255) : Color.FromArgb(86, 255, 255, 255), Selected || Focused || DropSide != 0 ? 2 : 1))
            {
                g.FillPath(brush, path);
                g.DrawPath(border, path);
            }
            if (Entry == null)
            {
                int arm = Math.Min(Width, Height) / 7;
                int cx = Width / 2, cy = Height / 2;
                using (Pen pen = new Pen(Color.FromArgb(232, 240, 252), Math.Max(4, arm / 6)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
                    g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
                }
            }
            else
            {
                Image icon = null;
                try { icon = EntryServices.Icon(Entry.Item); } catch { /* Failed icons must not hide an entry. */ }
                int size = Math.Max(32, (int)(Math.Min(Width, Height) * 0.62));
                if (icon != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(icon, new Rectangle((Width - size) / 2, (Height - size) / 2, size, size));
                }
                else
                {
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(110, 160, 235)))
                    using (GraphicsPath path = PanelDrawing.Rounded(new Rectangle((Width - size) / 2, (Height - size) / 2, size, size), 14))
                        g.FillPath(brush, path);
                }
            }
            if (DropSide == -1 || DropSide == 1)
            {
                int x = DropSide < 0 ? 3 : Width - 4;
                using (Pen pen = new Pen(Color.FromArgb(160, 195, 255), 4)) g.DrawLine(pen, x, 17, x, Height - 17);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); owner.SelectEntry(this); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            owner.SelectEntry(this);
            down = e.Location;
            armed = e.Button == MouseButtons.Left;
            dragged = false;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!armed || e.Button != MouseButtons.Left) return;
            Size threshold = SystemInformation.DragSize;
            Rectangle allowed = new Rectangle(down.X - threshold.Width / 2, down.Y - threshold.Height / 2, threshold.Width, threshold.Height);
            if (allowed.Contains(e.Location)) return;
            armed = false;
            dragged = true;
            owner.StartItemDrag(this);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool click = armed && !dragged && ClientRectangle.Contains(e.Location);
            armed = false;
            if (e.Button == MouseButtons.Right) owner.ShowEntryMenu(this, e.Location);
            else if (e.Button == MouseButtons.Left && click) owner.LaunchEntry(this);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { titleFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal static class PanelDrawing
    {
        internal static GraphicsPath Rounded(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rectangle.Width < 3 || rectangle.Height < 3)
            {
                path.AddRectangle(new Rectangle(rectangle.X, rectangle.Y, Math.Max(1, rectangle.Width), Math.Max(1, rectangle.Height)));
                return path;
            }
            radius = Math.Max(1, Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2));
            int diameter = radius * 2;
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
        internal static void Text(Graphics graphics, string text, Font font, Color color, Rectangle bounds)
        { Text(graphics, text, font, color, bounds, TextFormatFlags.Left); }
        internal static void Text(Graphics graphics, string text, Font font, Color color, Rectangle bounds, TextFormatFlags alignment)
        {
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap))
            using (SolidBrush ink = new SolidBrush(color))
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(100, 8, 20, 36)))
            {
                format.Alignment = (alignment & TextFormatFlags.HorizontalCenter) != 0 ? StringAlignment.Center : ((alignment & TextFormatFlags.Right) != 0 ? StringAlignment.Far : StringAlignment.Near);
                format.LineAlignment = StringAlignment.Center; format.Trimming = StringTrimming.EllipsisCharacter;
                Rectangle behind = bounds; behind.Offset(0, 1);
                graphics.DrawString(text ?? "", font, shadow, behind, format);
                graphics.DrawString(text ?? "", font, ink, bounds, format);
            }
        }
    }
}
