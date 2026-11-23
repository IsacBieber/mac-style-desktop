using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

using MacDesk.Core;

namespace MacDeskNative {
 // The one remaining real window: the "编辑入口" form (it needs native text boxes for long
 // paths). Everything else is a glass popup. It still activates while open - accepted, see
 // docs/ARCHITECTURE.md - but it wears the same tokens: dark frame, iOS navigation bar,
 // grouped cells, a segmented type control, errors shown inline.
 public static class EditDialogs {
  static T Clone<T>(T value) { var s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; return s.Deserialize<T>(s.Serialize(value)); }
  static string EditedValue(TextBox field, string original, bool trim) { return field.Text == (original ?? "") ? original : (trim ? field.Text.Trim() : field.Text); }

  public static DeskItem Item(IWin32Window owner, DeskItem existing) {
   var result = existing == null ? new DeskItem { id = DeskService.NewId(), title = "", description = "", type = "app", target = "", icon = "", arguments = "" } : Clone(existing);
   using (var d = new FormSheet(existing == null ? "添加入口" : "编辑入口")) {
    var title = d.Field("名称", result.title, null, null);
    var description = d.Field("说明", result.description, null, null);
    string[] types = { "app", "file", "folder", "url" };
    var type = d.Segments("类型", new[] { "应用", "文件", "文件夹", "网址" }, Math.Max(0, Array.IndexOf(types, result.type)));
    TextBox target = null;
    target = d.Field("目标", result.target, "浏览…", delegate {
     if (type.Selected == 2) {
      string folder = FolderPicker.Pick(d, "选择文件夹");
      if (folder != null) {
       target.Text = folder;
       if (title.Text.Trim().Length == 0) title.Text = Path.GetFileName(folder);
      }
     } else if (type.Selected == 3) {
      target.Focus();
     } else using (var f = new OpenFileDialog { CheckFileExists = true, DereferenceLinks = false }) {
      if (f.ShowDialog(d) == DialogResult.OK) {
       target.Text = f.FileName;
       if (title.Text.Trim().Length == 0) title.Text = Path.GetFileNameWithoutExtension(f.FileName);
      }
     }
    });
    var arguments = d.Field("启动参数（仅应用）", result.arguments, null, null);
    TextBox icon = null;
    icon = d.Field("图标（可选，留空为默认）", result.icon, "选择…", delegate {
     using (var f = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif", CheckFileExists = true }) {
      if (f.ShowDialog(d) == DialogResult.OK) icon.Text = f.FileName;
     }
    });
    Action syncArguments = delegate { arguments.Enabled = type.Selected == 0; arguments.Parent.Invalidate(); };
    syncArguments();
    type.Changed += delegate { syncArguments(); };
    d.Check = delegate {
     try {
      if (String.IsNullOrWhiteSpace(title.Text)) return "请输入名称。";
      DeskService.ValidateTarget(types[type.Selected], target.Text.Trim(), false);
      string iconPath = icon.Text.Trim();
      if (iconPath.Length > 0 && (!Path.IsPathRooted(iconPath) || !File.Exists(iconPath))) return "请选择存在的完整图标路径，或留空。";
      if (iconPath.Length > 0) using (var preview = Image.FromFile(iconPath)) { if (preview.Width < 1) return "图标图片无效。"; }
      return null;
     } catch (Exception ex) { return ex.Message; }
    };
    if (d.ShowDialog(owner) != DialogResult.OK) return null;
    result.title = EditedValue(title, result.title, true);
    result.description = EditedValue(description, result.description, true);
    result.type = types[type.Selected];
    string validatedTarget = DeskService.ValidateTarget(result.type, target.Text.Trim(), false);
    result.target = target.Text == (result.target ?? "") ? result.target : validatedTarget;
    result.icon = EditedValue(icon, result.icon, true);
    result.arguments = EditedValue(arguments, result.arguments, false);
    return result;
   }
  }

  // ---- The form window ----------------------------------------------------------------------

  sealed class FormSheet : Form {
   [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
   [DllImport("user32.dll")] static extern bool ReleaseCapture();
   [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

   const int FormWidth = 460, HeaderH = 52, PadX = 20, CellH = 40;
   readonly FlowLayoutPanel fields = new FlowLayoutPanel();
   readonly Label error = new Label();
   readonly Font labelFont = Ui.MakeFont(Ui.Cjk, Theme.Footnote, FontStyle.Regular);
   readonly Font fieldFont = Ui.MakeFont(Ui.Cjk, Theme.Body, FontStyle.Regular);
   readonly Font titleFont = Ui.MakeFont(Ui.Cjk, Theme.Title, FontStyle.Bold);
   readonly Font buttonFont = Ui.MakeFont(Ui.Cjk, Theme.Title - 1, FontStyle.Regular);
   readonly Font buttonBoldFont = Ui.MakeFont(Ui.Cjk, Theme.Title - 1, FontStyle.Bold);
   public Func<string> Check;

   public FormSheet(string title) {
    Text = title;
    AccessibleName = title;
    AutoScaleMode = AutoScaleMode.None;
    FormBorderStyle = FormBorderStyle.None;
    MaximizeBox = MinimizeBox = false;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.CenterScreen;
    BackColor = Theme.FormBackground;
    ForeColor = Theme.Label;
    Font = fieldFont;
    KeyPreview = true;

    var header = new Panel { Dock = DockStyle.Top, Height = HeaderH, BackColor = Theme.FormBackground };
    var cancel = NavButton("取消", buttonFont, DockStyle.Left);
    var done = NavButton("完成", buttonBoldFont, DockStyle.Right);
    cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
    done.Click += delegate { Submit(); };
    var titleLbl = new Label { Text = title, Font = titleFont, ForeColor = Theme.Label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.FormBackground };
    MouseEventHandler drag = delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } };
    header.MouseDown += drag;
    titleLbl.MouseDown += drag;
    header.Paint += delegate(object s, PaintEventArgs e) { using (var pen = new Pen(Theme.FormCell)) e.Graphics.DrawLine(pen, 0, HeaderH - 1, header.Width, HeaderH - 1); };
    header.Controls.Add(titleLbl);
    header.Controls.Add(cancel);
    header.Controls.Add(done);

    fields.Dock = DockStyle.Fill;
    fields.FlowDirection = FlowDirection.TopDown;
    fields.WrapContents = false;
    fields.AutoScroll = true;
    fields.BackColor = Theme.FormBackground;
    fields.Padding = new Padding(PadX, 10, PadX, 0);

    error.Dock = DockStyle.Bottom;
    error.Height = 0;
    error.ForeColor = Theme.Red;
    error.Font = labelFont;
    error.Padding = new Padding(PadX + 4, 2, PadX, 8);
    error.BackColor = Theme.FormBackground;

    Controls.Add(fields);
    Controls.Add(error);
    Controls.Add(header);
    ClientSize = new Size(FormWidth, HeaderH + 20);
   }

   Label NavButton(string text, Font font, DockStyle dock) {
    var b = new Label { Text = text, Font = font, ForeColor = Theme.BlueOnGlass, Dock = dock, Width = 80, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, BackColor = Theme.FormBackground, AccessibleRole = AccessibleRole.PushButton, AccessibleName = text };
    b.MouseEnter += delegate { b.ForeColor = Color.FromArgb(140, 200, 255); };
    b.MouseLeave += delegate { b.ForeColor = Theme.BlueOnGlass; };
    return b;
   }

   void Submit() {
    string message = Check == null ? null : Check();
    if (message != null) {
     error.Text = message;
     error.Height = 34;
     return;
    }
    DialogResult = DialogResult.OK;
   }

   protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
    if (keyData == Keys.Enter) { Submit(); return true; }
    if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
    return base.ProcessCmdKey(ref msg, keyData);
   }

   protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4);          // DWMWA_USE_IMMERSIVE_DARK_MODE
    int corner = 2; DwmSetWindowAttribute(Handle, 33, ref corner, 4);      // DWMWA_WINDOW_CORNER_PREFERENCE = round
    int border = Theme.WindowBorderColorRef; DwmSetWindowAttribute(Handle, 34, ref border, 4);
   }

   protected override void OnShown(EventArgs e) {
    base.OnShown(e);
    int content = 10;
    foreach (Control c in fields.Controls) content += c.Height + c.Margin.Vertical;
    ClientSize = new Size(FormWidth, HeaderH + Math.Min(560, content + 12) + 34);
    CenterToScreen();
   }

   void Caption(string text) {
    fields.Controls.Add(new Label { Text = text, AutoSize = true, Font = labelFont, ForeColor = Theme.SecondaryLabel, BackColor = Theme.FormBackground, Margin = new Padding(4, 8, 0, 4) });
   }

   // A rounded cell holding a borderless text box, with an optional blue action at its end.
   public TextBox Field(string label, string value, string actionText, Action action) {
    Caption(label);
    var cell = new RoundCell { Width = FormWidth - PadX * 2 - 4, Height = CellH, Margin = new Padding(0, 0, 0, 4) };
    var box = new TextBox { Text = value ?? "", BorderStyle = BorderStyle.None, Font = fieldFont, BackColor = Theme.FormCell, ForeColor = Theme.Label, AccessibleName = label };
    int right = 12;
    if (actionText != null) {
     var link = new Label { Text = actionText, Font = labelFont, ForeColor = Theme.BlueOnGlass, BackColor = Theme.FormCell, AutoSize = true, Cursor = Cursors.Hand, AccessibleRole = AccessibleRole.PushButton, AccessibleName = actionText };
     link.Click += delegate { if (action != null) action(); };
     cell.Controls.Add(link);
     link.Location = new Point(cell.Width - link.PreferredWidth - 12, (CellH - link.PreferredHeight) / 2);
     right = cell.Width - link.Left + 8;
    }
    box.Location = new Point(12, (CellH - box.PreferredHeight) / 2 + 1);
    box.Width = cell.Width - 12 - right;
    box.EnabledChanged += delegate { box.BackColor = Theme.FormCell; cell.Dimmed = !box.Enabled; cell.Invalidate(); };
    cell.Click += delegate { box.Focus(); };
    cell.Controls.Add(box);
    fields.Controls.Add(cell);
    return box;
   }

   public Segmented Segments(string label, string[] options, int selected) {
    Caption(label);
    var control = new Segmented(options, fieldFont) { Width = FormWidth - PadX * 2 - 4, Height = 34, Margin = new Padding(0, 0, 0, 4), Selected = selected, AccessibleName = label };
    fields.Controls.Add(control);
    return control;
   }

   protected override void Dispose(bool disposing) {
    if (disposing) { labelFont.Dispose(); fieldFont.Dispose(); titleFont.Dispose(); buttonFont.Dispose(); buttonBoldFont.Dispose(); }
    base.Dispose(disposing);
   }
  }

  sealed class RoundCell : Panel {
   public bool Dimmed;
   public RoundCell() { DoubleBuffered = true; BackColor = Theme.FormBackground; }
   protected override void OnPaint(PaintEventArgs e) {
    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
    using (var path = Ui.Squircle(new RectangleF(0, 0, Width - 1, Height - 1), Theme.RowRadius))
    using (var b = new SolidBrush(Theme.FormCell)) e.Graphics.FillPath(b, path);
    if (Dimmed) using (var b = new SolidBrush(Color.FromArgb(120, Theme.FormBackground))) e.Graphics.FillRectangle(b, ClientRectangle);
   }
  }

  // iOS segmented control: a lighter rounded thumb behind the selected segment.
  internal sealed class Segmented : Control {
   readonly string[] options;
   int selected, hovered = -1;
   public event EventHandler Changed;
   public Segmented(string[] options, Font font) {
    this.options = options;
    Font = font;
    DoubleBuffered = true;
    BackColor = Theme.FormBackground;
    Cursor = Cursors.Hand;
    SetStyle(ControlStyles.Selectable, true);
    TabStop = true;
   }
   public int Selected {
    get { return selected; }
    set { int v = Math.Max(0, Math.Min(options.Length - 1, value)); if (v == selected) return; selected = v; Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); }
   }
   int At(Point p) { return Math.Max(0, Math.Min(options.Length - 1, p.X * options.Length / Math.Max(1, Width))); }
   protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); Selected = At(e.Location); }
   protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = At(e.Location); if (h != hovered) { hovered = h; Invalidate(); } }
   protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = -1; Invalidate(); }
   protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData); }
   protected override void OnKeyDown(KeyEventArgs e) {
    base.OnKeyDown(e);
    if (e.KeyCode == Keys.Left) Selected = selected - 1;
    if (e.KeyCode == Keys.Right) Selected = selected + 1;
   }
   protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
   protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
   protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    using (var track = Ui.Squircle(new RectangleF(0, 0, Width - 1, Height - 1), Theme.RowRadius))
    using (var b = new SolidBrush(Theme.FormCell)) g.FillPath(b, track);
    float w = (Width - 4) / (float)options.Length;
    var thumb = new RectangleF(2 + selected * w, 2, w, Height - 5);
    using (var path = Ui.Squircle(thumb, Theme.RowRadius - 2))
    using (var b = new SolidBrush(Color.FromArgb(99, 99, 106))) g.FillPath(b, path);
    if (Focused) using (var path = Ui.Squircle(thumb, Theme.RowRadius - 2)) using (var pen = new Pen(Theme.Blue, 1.4f)) g.DrawPath(pen, path);
    for (int i = 0; i < options.Length; i++) {
     var r = new Rectangle((int)(2 + i * w), 0, (int)w, Height);
     Color ink = i == selected || i == hovered ? Theme.Label : Theme.SecondaryLabel;
     TextRenderer.DrawText(g, options[i], Font, r, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
   }
  }
 }
}
