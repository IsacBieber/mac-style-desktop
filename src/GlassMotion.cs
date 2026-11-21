using System;using System.ComponentModel;using System.Drawing;using System.Drawing.Drawing2D;using System.Drawing.Imaging;using System.Runtime.InteropServices;using System.Windows.Forms;
namespace MacDeskNative {
 internal static class GlassMaterial {
  [StructLayout(LayoutKind.Sequential)]struct Margins{public int L,R,T,B;}
  [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr h,int attribute,ref int value,int size);
  [DllImport("dwmapi.dll")]static extern int DwmExtendFrameIntoClientArea(IntPtr h,ref Margins margins);
  [StructLayout(LayoutKind.Sequential)]struct Accent{public int State,Flags,Color,Id;}
  [StructLayout(LayoutKind.Sequential)]struct Composition{public int Attribute;public IntPtr Data;public int Size;}
  [DllImport("user32.dll")]static extern int SetWindowCompositionAttribute(IntPtr window,ref Composition data);
  [StructLayout(LayoutKind.Sequential)]struct RECT{public int L,T,R,B;}
  [DllImport("user32.dll")]static extern int FillRect(IntPtr dc,ref RECT rectangle,IntPtr brush);
  [DllImport("gdi32.dll")]static extern IntPtr GetStockObject(int index);
  // GDI clears RGB and alpha to zero. Graphics.Clear(Color.Black) writes opaque alpha=255 and conceals Acrylic.
  internal static void ClearGlass(Graphics graphics,Size size){IntPtr dc=graphics.GetHdc();try{var rect=new RECT{L=0,T=0,R=size.Width,B=size.Height};FillRect(dc,ref rect,GetStockObject(4));}finally{graphics.ReleaseHdc(dc);}}
  // Per-window Acrylic, not a global transparency setting. Verified on this Win11 desktop; unsupported systems retain the opaque fallback.
  internal static bool Enable(IntPtr window){try{var margins=new Margins{L=-1,R=-1,T=-1,B=-1};if(DwmExtendFrameIntoClientArea(window,ref margins)<0)return false;var accent=new Accent{State=4,Flags=2,Color=unchecked((int)0x2C566070)};IntPtr memory=Marshal.AllocHGlobal(Marshal.SizeOf(accent));try{Marshal.StructureToPtr(accent,memory,false);var data=new Composition{Attribute=19,Data=memory,Size=Marshal.SizeOf(accent)};return SetWindowCompositionAttribute(window,ref data)!=0;}finally{Marshal.FreeHGlobal(memory);}}catch(DllNotFoundException){return false;}catch(EntryPointNotFoundException){return false;}}
  // A one-shot approximation ONLY for the noninteractive motion surface. Live UI uses per-window compositor Acrylic.
  internal static Bitmap Frost(Rectangle bounds){using(var raw=new Bitmap(bounds.Width,bounds.Height)){using(var g=Graphics.FromImage(raw))g.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);using(var small=new Bitmap(Math.Max(1,bounds.Width/16),Math.Max(1,bounds.Height/16))){using(var g=Graphics.FromImage(small)){g.InterpolationMode=InterpolationMode.HighQualityBilinear;g.DrawImage(raw,new Rectangle(Point.Empty,small.Size));}var result=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppPArgb);using(var g=Graphics.FromImage(result)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(small,new Rectangle(Point.Empty,result.Size));using(var tint=new SolidBrush(Color.FromArgb(105,60,78,98)))g.FillRectangle(tint,new Rectangle(Point.Empty,result.Size));}return result;}}}
 }
 // Phone folders dim the rest of the screen so the glass sheet reads clearly. Click-through, never activated.
 internal sealed class DimOverlay : Form {
  internal DimOverlay(Rectangle bounds,Form owner){Owner=owner;ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;BackColor=Color.Black;Text="MacDesk Dim";Bounds=bounds;}
  protected override bool ShowWithoutActivation{get{return true;}}
  protected override CreateParams CreateParams{get{CreateParams p=base.CreateParams;p.ExStyle|=0x80000|0x20|0x80;p.ExStyle&=~0x40000;return p;}}
 }
 internal sealed class GlassMotion : Form {
  Bitmap content;IntPtr screen,memory,dib,previous,bits;Size capacity;bool released;readonly bool activate;internal Func<Keys,bool> KeyHandler;
  Bitmap frameBitmap;Graphics frameGraphics;IntPtr layered;
  internal GlassMotion(Bitmap content,Form owner,bool activate){this.content=content;this.activate=activate;Owner=owner;ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;KeyPreview=true;Text="MacDesk Folder Motion";screen=GetDC(IntPtr.Zero);memory=CreateCompatibleDC(screen);if(screen==IntPtr.Zero||memory==IntPtr.Zero){if(memory!=IntPtr.Zero)DeleteDC(memory);if(screen!=IntPtr.Zero)ReleaseDC(IntPtr.Zero,screen);content.Dispose();this.content=null;released=true;throw new Win32Exception();}}
  protected override bool ShowWithoutActivation{get{return !activate;}}
  protected override bool ProcessCmdKey(ref Message message,Keys key){if(KeyHandler!=null&&KeyHandler(key))return true;return base.ProcessCmdKey(ref message,key);}
  protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle=(p.ExStyle|0x80000|0x80|0x20|(activate?0:0x8000000))&~0x40000;return p;}}
  internal void Present(Rectangle bounds,double opacity){if(bounds.Width<1||bounds.Height<1)return;Ensure(bounds.Size);Graphics g=frameGraphics;var target=new Rectangle(Point.Empty,bounds.Size);
   // Clear only the frame being drawn and reuse the cached Graphics: cheap, stable per-frame cost for high refresh displays.
   IntPtr hdc=g.GetHdc();try{var clear=new RECT{L=0,T=0,R=target.Right,B=target.Bottom};FillRect(hdc,ref clear,GetStockObject(4));}finally{g.ReleaseHdc(hdc);}
   g.SmoothingMode=SmoothingMode.AntiAlias;g.InterpolationMode=InterpolationMode.Bilinear;g.PixelOffsetMode=PixelOffsetMode.HighSpeed;g.CompositingQuality=CompositingQuality.HighSpeed;
   using(var path=PanelDrawing.Rounded(target,Math.Max(10,(int)(28.0*bounds.Width/content.Width)))){g.SetClip(path);g.DrawImage(content,target);}g.ResetClip();
   var at=new POINT(bounds.X,bounds.Y);var size=new SIZE(bounds.Width,bounds.Height);var src=new POINT(0,0);var blend=new BLEND{Operation=0,Flags=0,Alpha=(byte)Math.Max(0,Math.Min(255,(int)(opacity*255))),Format=1};IntPtr hwnd=layered;if(hwnd==IntPtr.Zero){hwnd=Handle;layered=hwnd;}
   if(!UpdateLayeredWindow(hwnd,screen,ref at,ref size,memory,ref src,0,ref blend,2))throw new Win32Exception(Marshal.GetLastWin32Error());}
  void Ensure(Size size){if(dib!=IntPtr.Zero&&size.Width<=capacity.Width&&size.Height<=capacity.Height)return;if(frameGraphics!=null){frameGraphics.Dispose();frameGraphics=null;}if(frameBitmap!=null){frameBitmap.Dispose();frameBitmap=null;}if(dib!=IntPtr.Zero){SelectObject(memory,previous);DeleteObject(dib);}capacity=new Size(Math.Max(size.Width,content.Width),Math.Max(size.Height,content.Height));var info=new INFO();info.Size=Marshal.SizeOf(typeof(INFO));info.W=capacity.Width;info.H=-capacity.Height;info.Planes=1;info.BitCount=32;dib=CreateDIBSection(screen,ref info,0,out bits,IntPtr.Zero,0);if(dib==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());previous=SelectObject(memory,dib);frameBitmap=new Bitmap(capacity.Width,capacity.Height,capacity.Width*4,PixelFormat.Format32bppPArgb,bits);frameGraphics=Graphics.FromImage(frameBitmap);}
  protected override void OnPaint(PaintEventArgs e){}protected override void OnPaintBackground(PaintEventArgs e){}
  protected override void Dispose(bool disposing){if(!released){released=true;if(frameGraphics!=null){frameGraphics.Dispose();frameGraphics=null;}if(frameBitmap!=null){frameBitmap.Dispose();frameBitmap=null;}if(dib!=IntPtr.Zero){SelectObject(memory,previous);DeleteObject(dib);}if(memory!=IntPtr.Zero)DeleteDC(memory);if(screen!=IntPtr.Zero)ReleaseDC(IntPtr.Zero,screen);if(content!=null){content.Dispose();content=null;}}base.Dispose(disposing);}
  [StructLayout(LayoutKind.Sequential)]struct RECT{public int L,T,R,B;}
  [DllImport("user32.dll")]static extern int FillRect(IntPtr dc,ref RECT rectangle,IntPtr brush);
  [DllImport("gdi32.dll")]static extern IntPtr GetStockObject(int index);
  [StructLayout(LayoutKind.Sequential)]struct POINT{public int X,Y;public POINT(int x,int y){X=x;Y=y;}}
  [StructLayout(LayoutKind.Sequential)]struct SIZE{public int W,H;public SIZE(int w,int h){W=w;H=h;}}
  [StructLayout(LayoutKind.Sequential)]struct INFO{public int Size,W,H;public short Planes,BitCount;public int Compression,ImageSize,XPels,YPels,Used,Important;}
  [StructLayout(LayoutKind.Sequential,Pack=1)]struct BLEND{public byte Operation,Flags,Alpha,Format;}
  [DllImport("user32.dll")]static extern IntPtr GetDC(IntPtr h);[DllImport("user32.dll")]static extern int ReleaseDC(IntPtr h,IntPtr dc);
  [DllImport("gdi32.dll")]static extern IntPtr CreateCompatibleDC(IntPtr dc);[DllImport("gdi32.dll")]static extern bool DeleteDC(IntPtr dc);[DllImport("gdi32.dll")]static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);[DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr obj);[DllImport("gdi32.dll",SetLastError=true)]static extern IntPtr CreateDIBSection(IntPtr dc,ref INFO info,int usage,out IntPtr bits,IntPtr section,int offset);
  [DllImport("user32.dll",SetLastError=true)]static extern bool UpdateLayeredWindow(IntPtr h,IntPtr dst,ref POINT position,ref SIZE size,IntPtr source,ref POINT origin,int key,ref BLEND blend,int flags);
 }
}
