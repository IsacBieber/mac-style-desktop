using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

using MacDesk.Core;

namespace MacDeskNative {
 public static class EditDialogs {
  static T Clone<T>(T value) { var s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; return s.Deserialize<T>(s.Serialize(value)); }
  static string EditedValue(TextBox field, string original, bool trim) { return field.Text == (original ?? "") ? original : (trim ? field.Text.Trim() : field.Text); }

  public static DeskGroup Group(IWin32Window owner, DeskGroup existing) {
   var result = existing == null ? new DeskGroup { id = DesktopStore.NewId(), title = "", subtitle = "" } : Clone(existing);
   using (var d = new ShortDialog(existing == null ? "新增文件夹" : "编辑文件夹", 250)) {
    var title = d.Field("名称", result.title);
    var sub = d.Field("副标题", result.subtitle);
    d.Check = delegate { if (String.IsNullOrWhiteSpace(title.Text)) return "请输入文件夹名称。"; return null; };
    if (d.ShowDialog(owner) != DialogResult.OK) return null;
    result.title = EditedValue(title, result.title, true);
    result.subtitle = EditedValue(sub, result.subtitle, true);
    return result;
   }
  }

  public static string CalendarNote(IWin32Window owner, string dateKey, string existingNote, Point screenLocation) {
   using (var d = new ShortDialog(dateKey + " · 每日备忘", 185)) {
    if (screenLocation != Point.Empty) {
     d.StartPosition = FormStartPosition.Manual;
     d.Location = screenLocation;
    }
    var note = d.Field("当日备忘（留空并确定即可清除圆点）", existingNote ?? "");
    if (d.ShowDialog(owner) != DialogResult.OK) return null;
    return (note.Text ?? "").Trim();
   }
  }

  public static DeskItem Item(IWin32Window owner, DeskItem existing) {
   var result = existing == null ? new DeskItem { id = DesktopStore.NewId(), title = "", description = "", type = "app", target = "", icon = "", arguments = "" } : Clone(existing);
   using (var d = new ShortDialog(existing == null ? "添加入口" : "编辑入口", 560)) {
    var title = d.Field("名称", result.title);
    var description = d.Field("说明", result.description);
    d.Label("类型");
    var type = new ComboBox { Width = 472, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, AccessibleName = "入口类型" };
    type.Items.AddRange(new string[] { "应用程序", "文件", "文件夹", "网址" });
    string[] types = { "app", "file", "folder", "url" };
    type.SelectedIndex = Math.Max(0, Array.IndexOf(types, result.type));
    d.Add(type);
    var target = d.Field("目标", result.target);
    d.Action("浏览目标…", delegate {
     if (type.SelectedIndex == 2) {
      using (var f = new FolderBrowserDialog()) {
       if (f.ShowDialog(d) == DialogResult.OK) {
        target.Text = f.SelectedPath;
        if (title.Text.Trim().Length == 0) title.Text = Path.GetFileName(f.SelectedPath);
       }
      }
     } else if (type.SelectedIndex == 3) {
      target.Focus();
     } else using (var f = new OpenFileDialog { CheckFileExists = true, DereferenceLinks = false }) {
      if (f.ShowDialog(d) == DialogResult.OK) {
       target.Text = f.FileName;
       if (title.Text.Trim().Length == 0) title.Text = Path.GetFileNameWithoutExtension(f.FileName);
      }
     }
    });
    var arguments = d.Field("启动参数（仅应用程序）", result.arguments);
    arguments.Enabled = type.SelectedIndex == 0;
    type.SelectedIndexChanged += delegate { arguments.Enabled = type.SelectedIndex == 0; };
    var icon = d.Field("图标（可选，留空为默认）", result.icon);
    d.Action("选择图标…", delegate {
     using (var f = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif", CheckFileExists = true }) {
      if (f.ShowDialog(d) == DialogResult.OK) icon.Text = f.FileName;
     }
    });
    d.Check = delegate {
     try {
      if (String.IsNullOrWhiteSpace(title.Text)) return "请输入名称。";
      EntryServices.ValidateTarget(types[type.SelectedIndex], target.Text.Trim(), false);
      if (icon.Text.Trim().Length > 0 && (!Path.IsPathRooted(icon.Text.Trim()) || !File.Exists(icon.Text.Trim()))) return "请选择存在的完整图标路径，或留空。";
      if (icon.Text.Trim().Length > 0) {
       using (var preview = Image.FromFile(icon.Text.Trim())) { if (preview.Width < 1) return "图标图片无效。"; }
      }
      return null;
     } catch (Exception ex) { return ex.Message; }
    };
    if (d.ShowDialog(owner) != DialogResult.OK) return null;
    result.title = EditedValue(title, result.title, true);
    result.description = EditedValue(description, result.description, true);
    result.type = types[type.SelectedIndex];
    string validatedTarget = EntryServices.ValidateTarget(result.type, target.Text.Trim(), false);
    result.target = target.Text == (result.target ?? "") ? result.target : validatedTarget;
    result.icon = EditedValue(icon, result.icon, true);
    result.arguments = EditedValue(arguments, result.arguments, false);
    return result;
   }
  }

  sealed class ShortDialog : Form {
   [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
   [DllImport("user32.dll")] static extern bool ReleaseCapture();
   [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

   static readonly Color Bg = Color.FromArgb(32, 32, 36), FieldBg = Color.FromArgb(50, 50, 56), Accent = Color.FromArgb(10, 132, 255);
   FlowLayoutPanel fields = new FlowLayoutPanel();
   public Func<string> Check;

   public ShortDialog(string title, int height) {
    Text = title;
    AutoScaleMode = AutoScaleMode.None;
    ClientSize = new Size(524, height);
    FormBorderStyle = FormBorderStyle.None;
    MaximizeBox = false;
    MinimizeBox = false;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.CenterParent;
    Font = new Font("Microsoft YaHei UI", 13.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    BackColor = Bg;
    ForeColor = Color.FromArgb(245, 245, 250);
    AccessibleName = title;
    Padding = new Padding(1);

    var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Bg, Cursor = Cursors.SizeAll };
    var titleLbl = new Label {
     Text = title,
     ForeColor = Color.White,
     Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel),
     AutoSize = false,
     Dock = DockStyle.Fill,
     TextAlign = ContentAlignment.MiddleCenter,
     Padding = new Padding(42, 4, 0, 0)
    };
    var closeBtn = new Button {
     Text = "×",
     Dock = DockStyle.Right,
     Width = 42,
     FlatStyle = FlatStyle.Flat,
     BackColor = Color.Transparent,
     ForeColor = Color.FromArgb(160, 235, 235, 245),
     Font = new Font("Segoe UI", 18, FontStyle.Regular, GraphicsUnit.Pixel),
     Cursor = Cursors.Hand,
     TabStop = false
    };
    closeBtn.FlatAppearance.BorderSize = 0;
    closeBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 58, 64);
    closeBtn.Click += delegate { DialogResult = DialogResult.Cancel; };
    MouseEventHandler drag = delegate(object s, MouseEventArgs e) {
     if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); }
    };
    header.MouseDown += drag;
    titleLbl.MouseDown += drag;
    header.Controls.Add(titleLbl);
    header.Controls.Add(closeBtn);

    fields.Dock = DockStyle.Fill;
    fields.FlowDirection = FlowDirection.TopDown;
    fields.WrapContents = false;
    fields.AutoScroll = true;
    fields.BackColor = Bg;
    fields.Padding = new Padding(24, 4, 14, 8);

    var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(14, 12, 22, 12), BackColor = Bg };
    var ok = new Button { Text = "确定", AccessibleName = "确定", Width = 96, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Cursor = Cursors.Hand, Font = new Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold, GraphicsUnit.Pixel) };
    ok.FlatAppearance.BorderSize = 0;
    ok.Click += delegate {
     string error = Check == null ? null : Check();
     if (error != null) { MessageBox.Show(this, error, "请检查输入", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
     DialogResult = DialogResult.OK;
    };
    var cancel = new Button { Text = "取消", AccessibleName = "取消", DialogResult = DialogResult.Cancel, Width = 96, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = FieldBg, ForeColor = Color.FromArgb(245, 245, 250), Cursor = Cursors.Hand };
    cancel.FlatAppearance.BorderSize = 0;
    footer.Controls.Add(ok);
    footer.Controls.Add(cancel);

    Controls.Add(fields);
    Controls.Add(footer);
    Controls.Add(header);
    AcceptButton = ok;
    CancelButton = cancel;
   }

   protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    int corner = 2;
    DwmSetWindowAttribute(Handle, 33, ref corner, 4);
   }

   protected override void OnPaint(PaintEventArgs e) {
    base.OnPaint(e);
    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
    using (var pen = new Pen(Color.FromArgb(60, 60, 66), 1f))
     e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
   }

   void Theme(Control c) {
    c.ForeColor = Color.FromArgb(245, 245, 250);
    if (c is TextBox || c is ComboBox) c.BackColor = FieldBg;
    else if (c is Button) {
     var b = (Button)c;
     b.FlatStyle = FlatStyle.Flat;
     b.BackColor = FieldBg;
     b.FlatAppearance.BorderSize = 0;
     b.ForeColor = Color.FromArgb(100, 170, 255);
     b.Cursor = Cursors.Hand;
    } else c.BackColor = Bg;
    foreach (Control child in c.Controls) Theme(child);
   }

   public void Add(Control c) { fields.Controls.Add(c); Theme(c); }
   public void Label(string text) { Add(new Label { Text = text, AutoSize = true, ForeColor = Color.FromArgb(150, 235, 235, 245), Margin = new Padding(0, 8, 0, 3) }); }
   public TextBox Field(string label, string value) { Label(label); var t = new TextBox { Text = value ?? "", Width = 472, BorderStyle = BorderStyle.None, AccessibleName = label, Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Regular, GraphicsUnit.Pixel), Margin = new Padding(0, 2, 0, 4) }; Add(t); return t; }
   public void Action(string title, Action action) { var b = new Button { Text = title, AccessibleName = title, AutoSize = true, Height = 28, Margin = new Padding(0, 4, 0, 4) }; b.Click += delegate { action(); }; Add(b); }
  }
 }
}
