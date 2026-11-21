using System;using System.Drawing;using System.Drawing.Drawing2D;using System.Windows.Forms;
namespace MacDeskNative {
 // The left column is widgets only. Folders live on DesktopFolderForm, never here.
 public class HomeForm:Form {
  readonly AppController app;readonly Timer timer=new Timer();
  readonly Font headerFont=new Font("Microsoft YaHei UI",15,FontStyle.Regular,GraphicsUnit.Pixel),clockFont=new Font("Segoe UI",76,FontStyle.Regular,GraphicsUnit.Pixel),dateFont=new Font("Microsoft YaHei UI",16,FontStyle.Regular,GraphicsUnit.Pixel),monthFont=new Font("Microsoft YaHei UI",22,FontStyle.Regular,GraphicsUnit.Pixel),weekdayFont=new Font("Microsoft YaHei UI",15,FontStyle.Regular,GraphicsUnit.Pixel),dayFont=new Font("Segoe UI",16,FontStyle.Regular,GraphicsUnit.Pixel);
  static readonly Rectangle ClockCardBounds=new Rectangle(0,0,352,232),MonthCardBounds=new Rectangle(0,256,352,278),ClockTextBounds=new Rectangle(22,58,308,104);
  DateTime paintedDay=DateTime.MinValue;
  public bool DesktopChildMode{get;private set;}
  internal static DateTime BeijingFromUtc(DateTime utc){if(utc.Kind!=DateTimeKind.Utc)throw new ArgumentException("必须传入 UTC 时间。","utc");return DateTime.SpecifyKind(utc.AddHours(8),DateTimeKind.Unspecified);}
  static readonly Color Surface=Color.FromArgb(27,31,39),Primary=Color.FromArgb(242,245,251),Secondary=Color.FromArgb(186,198,216),CalendarText=Color.FromArgb(220,229,243),Accent=Color.FromArgb(173,194,255);
  public HomeForm(AppController controller){DesktopChildMode=NativeDesktopHost.Parent!=IntPtr.Zero;if(DesktopChildMode)TopLevel=false;app=controller;AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;Location=new Point(48,64);Size=new Size(352,534);BackColor=Color.FromArgb(1,2,3);TransparencyKey=BackColor;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);AccessibleName="桌面卡片：北京时间和月历";var menu=new ContextMenuStrip();menu.Items.Add("新增桌面文件夹…",null,delegate{BeginInvoke((Action)delegate{if(!IsDisposed)app.AddGroup();});});ContextMenuStrip=menu;timer.Interval=1000;timer.Tick+=delegate{DateTime today=BeijingFromUtc(DateTime.UtcNow).Date;if(today!=paintedDay){paintedDay=today;Invalidate();}else Invalidate(ClockTextBounds);};timer.Start();RefreshData();}
  protected override CreateParams CreateParams{get{var p=base.CreateParams;if(NativeDesktopHost.Parent!=IntPtr.Zero){p.Parent=NativeDesktopHost.Parent;p.Style=(p.Style|0x40000000)&~unchecked((int)0x80000000);}else p.Style=(p.Style|unchecked((int)0x80000000))&~0x40000000;p.ExStyle=(p.ExStyle|0x80)&~0x40000;return p;}}
  protected override void WndProc(ref Message m){if(m.Msg==0x7B&&ContextMenuStrip!=null){long p=m.LParam.ToInt64();Point screen=p==-1?Cursor.Position:new Point((short)(p&65535),(short)((p>>16)&65535));app.ShowDesktopMenu(ContextMenuStrip,screen);m.Result=IntPtr.Zero;return;}base.WndProc(ref m);}
  static GraphicsPath Round(Rectangle r,int radius){var p=new GraphicsPath();int d=radius*2;p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
  public void RefreshData(){if(IsDisposed)return;if(DesktopChildMode){var shape=new Region();shape.MakeEmpty();using(var p=Round(ClockCardBounds,26))shape.Union(p);using(var p=Round(MonthCardBounds,24))shape.Union(p);var old=Region;Region=shape;if(old!=null)old.Dispose();}Invalidate();}
  static void TextAt(Graphics g,string text,Font font,Color color,Rectangle bounds,TextFormatFlags flags){TextRenderer.DrawText(g,text,font,bounds,color,flags|TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.PreserveGraphicsClipping);}
  protected override void OnPaint(PaintEventArgs e){
   base.OnPaint(e);
   // A single UTC sample drives the clock, date, month and selected calendar day.
   DateTime now=BeijingFromUtc(DateTime.UtcNow);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   using(var b=new SolidBrush(Surface)){using(var p=Round(ClockCardBounds,26))g.FillPath(b,p);using(var p=Round(MonthCardBounds,24))g.FillPath(b,p);}
   using(var b=new SolidBrush(Accent))g.FillEllipse(b,26,32,7,7);
   TextAt(g,"我的桌面  /  北京时间",headerFont,Secondary,new Rectangle(44,22,282,28),TextFormatFlags.VerticalCenter);
   TextAt(g,now.ToString("HH:mm"),clockFont,Primary,ClockTextBounds,TextFormatFlags.VerticalCenter);
   TextAt(g,now.ToString("yyyy 年 MM 月 dd 日"),dateFont,Secondary,new Rectangle(26,178,300,28),TextFormatFlags.VerticalCenter);
   DateTime today=now.Date,first=new DateTime(today.Year,today.Month,1);
   TextAt(g,today.ToString("yyyy 年 M 月"),monthFont,Primary,new Rectangle(26,274,300,34),TextFormatFlags.VerticalCenter);
   // Equal 44-pixel columns and six 28-pixel rows keep even the longest months airy.
   string[] week={"日","一","二","三","四","五","六"};
   TextFormatFlags cellFlags=TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter;
   for(int c=0;c<7;c++)TextAt(g,week[c],weekdayFont,Secondary,new Rectangle(22+c*44,320,44,26),cellFlags);
   int offset=(int)first.DayOfWeek,days=DateTime.DaysInMonth(today.Year,today.Month);
   for(int d=1;d<=days;d++){
    int cell=offset+d-1,x=22+(cell%7)*44,y=352+(cell/7)*28;bool active=d==today.Day;
    if(active)using(var b=new SolidBrush(Accent))g.FillEllipse(b,x+8,y,28,28);
    TextAt(g,d.ToString(),dayFont,active?Surface:CalendarText,new Rectangle(x,y,44,28),cellFlags);
   }
  }
  protected override void Dispose(bool disposing){if(disposing){timer.Dispose();headerFont.Dispose();clockFont.Dispose();dateFont.Dispose();monthFont.Dispose();weekdayFont.Dispose();dayFont.Dispose();if(ContextMenuStrip!=null)ContextMenuStrip.Dispose();}base.Dispose(disposing);}
 }
}
