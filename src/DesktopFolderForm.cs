using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDeskNative {
 // Created on the UI thread inside NativeDesktopHost.Prepare's retained DPI scope.
 // No Explorer owner, SetParent, global mouse hook, or topmost policy is used here.
 public class DesktopFolderForm : Form {
  const int TileWidth=112,TileHeight=130,StepX=132,StepY=148,LeftReserve=440,EdgePad=24;
  const string ItemFormat="MacDeskNative.Item";
  readonly AppController app;
  readonly List<FolderTile> tiles=new List<FolderTile>();
  readonly HashSet<ContextMenuStrip> menus=new HashSet<ContextMenuStrip>();
  readonly Font caption=new Font("Microsoft YaHei UI",14,FontStyle.Regular,GraphicsUnit.Pixel);
  readonly Font plusFont=new Font("Segoe UI",36,FontStyle.Regular,GraphicsUnit.Pixel);
  Bitmap frame; FolderTile hover,pressed,dropTarget; Point down,originalPosition; bool dragging,rendering; int scroll,contentHeight;
  public bool DesktopChildMode {get{return true;}}
  sealed class FolderTile {public string Id,Title;public DeskGroup Group;public Rectangle Bounds;public int Index;public bool Plus;}
  sealed class Payload {public string Source,Item,Url;public string[] Paths;public DragDropEffects Effect;}
  public DesktopFolderForm(AppController controller){
   if(controller==null)throw new ArgumentNullException("controller");
   if(NativeDesktopHost.Parent==IntPtr.Zero)throw new InvalidOperationException("透明文件夹需要已准备的原生桌面子窗口宿主。");
   app=controller;TopLevel=false;AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=true;TopMost=false;StartPosition=FormStartPosition.Manual;Location=Point.Empty;
   AccessibleName="桌面透明文件夹画布";Text="MacDesk Desktop Folders";AllowDrop=true;KeyPreview=true;SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint,true);
   FitHost();RefreshData();
  }
  protected override bool ShowWithoutActivation {get{return true;}}
  protected override CreateParams CreateParams {get{var cp=base.CreateParams;cp.Parent=NativeDesktopHost.Parent;cp.Style=(cp.Style|0x40000000)&~unchecked((int)0x80000000);cp.ExStyle=(cp.ExStyle|0x80000|0x80)&~0x40000;return cp;}}
  void FitHost(){RECT rect;if(GetClientRect(NativeDesktopHost.Parent,out rect))ClientSize=new Size(Math.Max(1,rect.R-rect.L),Math.Max(1,rect.B-rect.T));else ClientSize=Screen.PrimaryScreen.Bounds.Size;}
  public Dictionary<string,Point> SnapshotPositions(){var positions=new Dictionary<string,Point>();foreach(var tile in tiles)if(!tile.Plus)positions[tile.Id]=tile.Bounds.Location;return positions;}
   public void RefreshData(){if(IsDisposed||Disposing)return;FitHost();LayoutTiles();Render();}
  void LayoutTiles(){
   tiles.Clear();int left=Math.Min(LeftReserve,Math.Max(8,ClientSize.Width-TileWidth-EdgePad));int columns=Math.Max(1,(ClientSize.Width-left-EdgePad+StepX-TileWidth)/StepX);
   var occupied=new List<Rectangle>();foreach(var group in app.Data.groups)if(group.desktopX>=0&&group.desktopY>=0)occupied.Add(new Rectangle(Math.Max(left,Math.Min(ClientSize.Width-TileWidth,group.desktopX)),Math.Max(EdgePad,group.desktopY),TileWidth,TileHeight));
   int slot=0,index=0;contentHeight=0;foreach(var group in app.Data.groups){Rectangle bounds;if(group.desktopX>=0&&group.desktopY>=0)bounds=new Rectangle(Math.Max(left,Math.Min(ClientSize.Width-TileWidth,group.desktopX)),Math.Max(EdgePad,group.desktopY),TileWidth,TileHeight);else bounds=NextSlot(left,columns,ref slot,occupied);tiles.Add(new FolderTile{Id=group.id,Title=group.title,Group=group,Bounds=bounds,Index=index++});contentHeight=Math.Max(contentHeight,bounds.Bottom+EdgePad);}
   Rectangle add=NextSlot(left,columns,ref slot,occupied);tiles.Add(new FolderTile{Title="新增文件夹",Plus=true,Bounds=add});contentHeight=Math.Max(contentHeight,add.Bottom+EdgePad);scroll=Math.Max(0,Math.Min(scroll,Math.Max(0,contentHeight-ClientSize.Height)));
   hover=null;dropTarget=null;
  }
  static Rectangle NextSlot(int left,int columns,ref int slot,List<Rectangle> occupied){for(;;){int current=slot++;var r=new Rectangle(left+(current%columns)*StepX,EdgePad+(current/columns)*StepY,TileWidth,TileHeight);bool overlaps=false;foreach(var other in occupied)if(r.IntersectsWith(other)){overlaps=true;break;}if(!overlaps){occupied.Add(r);return r;}}}
  Rectangle VisibleBounds(FolderTile tile){var r=tile.Bounds;r.Offset(0,-scroll);return r;}
  FolderTile Hit(Point point){for(int i=tiles.Count-1;i>=0;i--)if(VisibleBounds(tiles[i]).Contains(point))return tiles[i];return null;}
  static GraphicsPath Rounded(RectangleF r,float radius){var p=new GraphicsPath();float d=radius*2;p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
  void DrawTile(Graphics g,FolderTile tile){Rectangle r=VisibleBounds(tile);if(!r.IntersectsWith(ClientRectangle))return;Rectangle icon=new Rectangle(r.X+12,r.Y+3,88,88);bool active=tile==hover||tile==dropTarget||tile==pressed;
   using(var path=Rounded(icon,22))using(var fill=new SolidBrush(Color.FromArgb(active?140:92,238,244,255)))using(var border=new Pen(Color.FromArgb(active?210:115,255,255,255),active?2:1)){g.FillPath(fill,path);g.DrawPath(border,path);}
   if(tile.Plus){using(var brush=new SolidBrush(Color.FromArgb(235,255,255,255)))using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})g.DrawString("+",plusFont,brush,icon,format);}
   else if(tile.Group!=null){int count=Math.Min(4,tile.Group.items.Count);for(int i=0;i<count;i++){try{Image image=EntryServices.Icon(tile.Group.items[i]);if(image==null)continue;var dest=new Rectangle(icon.X+12+(i%2)*35,icon.Y+12+(i/2)*35,29,29);g.DrawImage(image,dest);}catch(Exception){/* One broken icon must not hide other folders. */}}if(count==0)using(var p=new Pen(Color.FromArgb(180,255,255,255),2))g.DrawRectangle(p,icon.X+25,icon.Y+29,38,30);}
   var label=new RectangleF(r.X-4,r.Y+98,r.Width+8,29);using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Near,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})using(var shadow=new SolidBrush(Color.FromArgb(220,0,0,0)))using(var text=new SolidBrush(Color.White)){var behind=label;behind.Offset(1,2);g.DrawString(tile.Title??"",caption,shadow,behind,format);g.DrawString(tile.Title??"",caption,text,label,format);}
  }
  void Render(){if(IsDisposed||Disposing||rendering||ClientSize.Width<1||ClientSize.Height<1)return;rendering=true;try{var next=new Bitmap(ClientSize.Width,ClientSize.Height,PixelFormat.Format32bppPArgb);try{using(var g=Graphics.FromImage(next)){g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;foreach(var tile in tiles)DrawTile(g,tile);if(contentHeight>ClientSize.Height){var marker=new Rectangle(ClientSize.Width-50,ClientSize.Height-38,36,30);using(var b=new SolidBrush(Color.FromArgb(160,25,30,40)))g.FillEllipse(b,marker);using(var b=new SolidBrush(Color.White))g.DrawString("↕",caption,b,marker);}}
    if(IsHandleCreated)Present(next);var old=frame;frame=next;next=null;if(old!=null)old.Dispose();}finally{if(next!=null)next.Dispose();}}finally{rendering=false;}}
  void Present(Bitmap bitmap){IntPtr screen=GetDC(IntPtr.Zero),memory=IntPtr.Zero,hbitmap=IntPtr.Zero,old=IntPtr.Zero;try{if(screen==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());memory=CreateCompatibleDC(screen);if(memory==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());hbitmap=bitmap.GetHbitmap(Color.FromArgb(0));old=SelectObject(memory,hbitmap);var size=new SIZE(bitmap.Width,bitmap.Height);var source=new POINT(0,0);var blend=new BLEND{Operation=0,Flags=0,Alpha=255,Format=1};if(!UpdateLayeredWindow(Handle,screen,IntPtr.Zero,ref size,memory,ref source,0,ref blend,2))throw new Win32Exception(Marshal.GetLastWin32Error());}finally{if(old!=IntPtr.Zero&&memory!=IntPtr.Zero)SelectObject(memory,old);if(hbitmap!=IntPtr.Zero)DeleteObject(hbitmap);if(memory!=IntPtr.Zero)DeleteDC(memory);if(screen!=IntPtr.Zero)ReleaseDC(IntPtr.Zero,screen);}}
  protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);Render();}
  protected override void OnPaint(PaintEventArgs e){/* Only UpdateLayeredWindow owns the ARGB surface. */}
  protected override void OnPaintBackground(PaintEventArgs e){}
  protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);if(app!=null&&!rendering){LayoutTiles();Render();}}
  protected override void WndProc(ref Message m){if(m.Msg==0x7B){m.Result=IntPtr.Zero;return;}if(m.Msg==0x84){long packed=m.LParam.ToInt64();var point=PointToClient(new Point((short)(packed&65535),(short)((packed>>16)&65535)));if(Hit(point)==null){m.Result=new IntPtr(-1);return;}}if(m.Msg==0x14){m.Result=new IntPtr(1);return;}base.WndProc(ref m);}
  protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;pressed=Hit(e.Location);down=e.Location;dragging=false;if(pressed!=null){originalPosition=pressed.Bounds.Location;Capture=true;}Render();}
  protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(pressed!=null&&!pressed.Plus&&e.Button==MouseButtons.Left){var threshold=new Rectangle(down.X-SystemInformation.DragSize.Width/2,down.Y-SystemInformation.DragSize.Height/2,SystemInformation.DragSize.Width,SystemInformation.DragSize.Height);if(!threshold.Contains(e.Location))dragging=true;if(dragging){int left=Math.Min(LeftReserve,Math.Max(8,Width-TileWidth-EdgePad));pressed.Bounds=new Rectangle(Math.Max(left,Math.Min(Width-TileWidth,originalPosition.X+e.X-down.X)),Math.Max(EdgePad,originalPosition.Y+e.Y-down.Y),TileWidth,TileHeight);Render();return;}}var hit=Hit(e.Location);if(hit!=hover){hover=hit;Cursor=hit==null?Cursors.Default:Cursors.Hand;Render();}}
  protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);var tile=pressed;bool moved=dragging;pressed=null;dragging=false;Capture=false;if(e.Button==MouseButtons.Right){ShowMenu(Hit(e.Location),e.Location);return;}if(e.Button!=MouseButtons.Left||tile==null)return;if(moved){string id=tile.Id;Point position=tile.Bounds.Location;Defer(delegate{try{app.SetFolderPosition(id,position.X,position.Y);}finally{RefreshData();}});}else if(Hit(e.Location)==tile){if(tile.Plus)Defer(delegate{app.AddGroup();});else{Rectangle r=VisibleBounds(tile);Point center=PointToScreen(new Point(r.Left+r.Width/2,r.Top+45));string id=tile.Id;Defer(delegate{app.OpenGroup(id,center);});}}Render();}
  protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture&&pressed!=null){pressed=null;dragging=false;LayoutTiles();Render();}}
  protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=null;Render();}
  protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);scroll=Math.Max(0,Math.Min(Math.Max(0,contentHeight-Height),scroll-(e.Delta/120)*StepY));Render();}
  protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode==Keys.Escape)app.ClosePanel();if(e.Control&&e.KeyCode==Keys.N)Defer(delegate{app.AddGroup();});}
  void Defer(Action action){if(IsDisposed||Disposing||!IsHandleCreated)return;try{BeginInvoke((MethodInvoker)delegate{if(!IsDisposed&&!Disposing)action();});}catch(InvalidOperationException){}}
  void AddMenu(ContextMenuStrip menu,string title,Action action){menu.Items.Add(title,null,delegate{Defer(action);});}
  void ShowMenu(FolderTile tile,Point point){if(tile==null||tile.Plus)return;string id=tile.Id;int index=tile.Index;var menu=new ContextMenuStrip();menus.Add(menu);AddMenu(menu,"打开文件夹",delegate{app.OpenGroup(id,PointToScreen(new Point(tile.Bounds.Left+56,tile.Bounds.Top-scroll+45)));});AddMenu(menu,"添加入口…",delegate{app.AddItem(id);});AddMenu(menu,"编辑文件夹…",delegate{app.EditGroup(id);});AddMenu(menu,"上移顺序",delegate{app.TryMoveGroup(id,Math.Max(0,index-1));});AddMenu(menu,"下移顺序",delegate{app.TryMoveGroup(id,index+2);});menu.Items.Add(new ToolStripSeparator());AddMenu(menu,"删除文件夹（不删除原文件）…",delegate{app.DeleteGroup(id);});menu.Closed+=delegate{Defer(delegate{menus.Remove(menu);menu.Dispose();});};try{app.ShowDesktopMenu(menu,PointToScreen(point));}catch{menus.Remove(menu);menu.Dispose();throw;}}
  Payload ReadPayload(IDataObject data,DragDropEffects allowed){if(data==null||allowed==DragDropEffects.None)return null;if(data.GetDataPresent(ItemFormat)){if((allowed&DragDropEffects.Move)==0)return null;string value=data.GetData(ItemFormat) as string;if(value==null)return null;var parts=value.Split('\n');if(parts.Length!=2)return null;foreach(var group in app.Data.groups)if(group.id==parts[0])foreach(var item in group.items)if(item.id==parts[1])return new Payload{Source=parts[0],Item=parts[1],Effect=DragDropEffects.Move};return null;}if(data.GetDataPresent(DataFormats.FileDrop)){if((allowed&DragDropEffects.Link)==0)return null;var paths=data.GetData(DataFormats.FileDrop) as string[];if(paths==null||paths.Length==0)return null;foreach(var path in paths)if(String.IsNullOrWhiteSpace(path))return null;return new Payload{Paths=(string[])paths.Clone(),Effect=DragDropEffects.Link};}if(data.GetDataPresent(DataFormats.UnicodeText)){var effect=(allowed&DragDropEffects.Link)!=0?DragDropEffects.Link:(allowed&DragDropEffects.Copy)!=0?DragDropEffects.Copy:DragDropEffects.None;if(effect==DragDropEffects.None)return null;string text=data.GetData(DataFormats.UnicodeText) as string;Uri uri;if(text!=null&&Uri.TryCreate(text.Trim(),UriKind.Absolute,out uri)&&(uri.Scheme=="http"||uri.Scheme=="https"))return new Payload{Url=uri.AbsoluteUri,Effect=effect};}return null;}
  protected override void OnDragEnter(DragEventArgs e){base.OnDragEnter(e);OnDragOver(e);}
  protected override void OnDragOver(DragEventArgs e){base.OnDragOver(e);e.Effect=DragDropEffects.None;try{var tile=Hit(PointToClient(new Point(e.X,e.Y)));var payload=tile==null||tile.Plus?null:ReadPayload(e.Data,e.AllowedEffect);dropTarget=payload==null?null:tile;if(payload!=null)e.Effect=payload.Effect;}catch(Exception){dropTarget=null;}Render();}
  protected override void OnDragLeave(EventArgs e){base.OnDragLeave(e);dropTarget=null;Render();}
  protected override void OnDragDrop(DragEventArgs e){base.OnDragDrop(e);e.Effect=DragDropEffects.None;dropTarget=null;try{var tile=Hit(PointToClient(new Point(e.X,e.Y)));var payload=tile==null||tile.Plus?null:ReadPayload(e.Data,e.AllowedEffect);if(payload==null)return;bool saved=payload.Item!=null?app.TryMoveItem(payload.Source,payload.Item,tile.Id,-1):payload.Paths!=null?app.TryAddPaths(tile.Id,payload.Paths,-1):app.TryAddUrl(tile.Id,payload.Url,-1);if(saved)e.Effect=payload.Effect;}catch(Exception ex){Defer(delegate{app.ReportError(ex);});}finally{Render();}}
  protected override void Dispose(bool disposing){if(disposing){foreach(var menu in menus)menu.Dispose();menus.Clear();if(frame!=null){frame.Dispose();frame=null;}caption.Dispose();plusFont.Dispose();}base.Dispose(disposing);}
  [StructLayout(LayoutKind.Sequential)]struct POINT{public int X,Y;public POINT(int x,int y){X=x;Y=y;}}
  [StructLayout(LayoutKind.Sequential)]struct SIZE{public int W,H;public SIZE(int w,int h){W=w;H=h;}}
  [StructLayout(LayoutKind.Sequential)]struct RECT{public int L,T,R,B;}
  [StructLayout(LayoutKind.Sequential,Pack=1)]struct BLEND{public byte Operation,Flags,Alpha,Format;}
  [DllImport("user32.dll",SetLastError=true)]static extern bool GetClientRect(IntPtr hwnd,out RECT rect);
  [DllImport("user32.dll",SetLastError=true)]static extern IntPtr GetDC(IntPtr hwnd);
  [DllImport("user32.dll")]static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
  [DllImport("gdi32.dll",SetLastError=true)]static extern IntPtr CreateCompatibleDC(IntPtr dc);
  [DllImport("gdi32.dll")]static extern bool DeleteDC(IntPtr dc);
  [DllImport("gdi32.dll")]static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
  [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr obj);
  [DllImport("user32.dll",SetLastError=true)]static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr dst,IntPtr position,ref SIZE size,IntPtr source,ref POINT origin,int color,ref BLEND blend,int flags);
 }
}
