using System;using System.Collections.Generic;using System.Linq;using System.IO;using System.Diagnostics;using System.Drawing;using System.Threading;using System.Windows.Forms;using System.Web.Script.Serialization;
namespace MacDeskNative {
 public class AppController : ApplicationContext {
  public DesktopStore Store {get;private set;} public DesktopData Data {get{return Store.Data;}} public HomeForm Home {get;private set;}
  public DesktopFolderForm DesktopFolders {get;private set;} public bool ModalInProgress {get{return modalDepth>0;}} int modalDepth;
  public PanelForm Panel {get;private set;} readonly Dictionary<string,PanelForm> panels=new Dictionary<string,PanelForm>();
  // Windows 11 coalesces every timer to ~15.6 ms; the animation is rendered by its own 120 Hz thread instead.
  Thread renderThread;volatile bool motionRunning,reverseRequested,motionDone;
  void StartMotion(){StopMotion();motionRunning=true;reverseRequested=false;motionDone=false;clock.Restart();MotionLogReset();renderThread=new Thread(RenderLoop);renderThread.IsBackground=true;renderThread.Priority=ThreadPriority.AboveNormal;renderThread.Start();}
  void StopMotion(){motionRunning=false;Thread t=renderThread;if(t!=null&&t!=Thread.CurrentThread&&t.IsAlive)t.Join(400);renderThread=null;}
 readonly Stopwatch clock=new Stopwatch(); NotifyIcon tray; bool closing,refreshQueued,testMode;
  Rectangle originBounds,fromBounds,toBounds,expandedBounds,motionBounds;double alphaFrom,alphaTo,motionAlpha;bool closeMotion,motionMouse,handingOff;GlassMotion motion;Form dim;const int OpenMs=260,CloseMs=190;
  // 1 ms timer resolution plus a ~6 ms present gate keeps motion at ~166 fps for 120 Hz displays.

  // Optional motion diagnostics (off unless MACDESK_MOTION_LOG is set): proves real frame pacing.
  static readonly string MotionLog=Environment.GetEnvironmentVariable("MACDESK_MOTION_LOG");
  long motionStartTicks,firstFrameTicks;int motionFrames;double motionMaxGapMs,lastFrameMs;
  void MotionLogReset(){motionStartTicks=Stopwatch.GetTimestamp();firstFrameTicks=0;motionFrames=0;motionMaxGapMs=0;lastFrameMs=0;workMax=0;workSum=0;}
  double workMax;double workSum;
  void MotionLogFrame(double workMs){if(workMs>workMax)workMax=workMs;workSum+=workMs;long ms=(long)((Stopwatch.GetTimestamp()-motionStartTicks)*1000.0/Stopwatch.Frequency);if(firstFrameTicks==0)firstFrameTicks=ms;if(lastFrameMs>0)trackGap(ms-lastFrameMs);lastFrameMs=ms;motionFrames++;}
  void trackGap(double gap){if(gap>motionMaxGapMs)motionMaxGapMs=gap;}
  void MotionLogDone(bool closing){if(String.IsNullOrEmpty(MotionLog))return;try{string line="{\"closing\":"+(closing?"true":"false")+",\"frames\":"+motionFrames+",\"firstFrameMs\":"+firstFrameTicks+",\"maxGapMs\":"+Math.Round(motionMaxGapMs,1)+",\"workAvgMs\":"+Math.Round(workSum/Math.Max(1,motionFrames),2)+",\"workMaxMs\":"+Math.Round(workMax,2)+"}\r\n";File.AppendAllText(MotionLog,line);}catch{}}
  void ShowDim(Rectangle bounds,double alpha){if(dim==null||dim.IsDisposed)dim=new DimOverlay(bounds,OwnDialogWindow());dim.Bounds=bounds;dim.Opacity=alpha;if(!dim.Visible)dim.Show();else dim.Update();}
  void HideDim(){Form old=dim;dim=null;if(old!=null){old.Hide();old.Dispose();}}
  [System.Runtime.InteropServices.DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
  void DisposeMotion(){GlassMotion old=motion;motion=null;if(old!=null)old.Dispose();}
  [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
  bool MotionKey(Keys key){if(key==Keys.Escape){ClosePanel();return true;}if(key==(Keys.Control|Keys.F)){FinishMotion();if(Panel!=null&&!Panel.IsDisposed&&Panel.Visible)Panel.FocusSearch();return true;}return false;}
  internal bool SourceContains(Point point){return new Rectangle(originBounds.X-12,originBounds.Y-3,112,130).Contains(point);}
  void MotionDeactivated(object sender,EventArgs args){if(handingOff||motion==null||sender!=motion||closing)return;if((GetAsyncKeyState(1)&0x8000)!=0||(GetAsyncKeyState(2)&0x8000)!=0)return;StopMotion();if(Panel!=null&&!Panel.IsDisposed)Panel.Hide();HideDim();closeMotion=true;DisposeMotion();}
  void PrepareMotion(){motion.KeyHandler=MotionKey;motion.Deactivate+=MotionDeactivated;}
  void FinishMotion(){StopMotion();if(motion==null)return;MotionLogDone(closeMotion);bool activate=GetForegroundWindow()==motion.Handle;handingOff=true;try{if(closeMotion)HideDim();if(Panel!=null&&!Panel.IsDisposed){if(closeMotion||!activate)Panel.Hide();else{Panel.Bounds=expandedBounds;Panel.Opacity=1;Panel.Show(OwnDialogWindow());Panel.Update();}}DisposeMotion();if(activate&&!closeMotion&&Panel!=null&&!Panel.IsDisposed&&Panel.Visible)Panel.Activate();}finally{handingOff=false;}}
  public AppController(string root):this(root,true){}
  public AppController(string root,bool show){testMode=!show;try{Store=new DesktopStore(root);Home=new HomeForm(this);Home.Text="MacDesk Desktop";
   Home.FormClosed+=delegate{Quit();};Home.HandleCreated+=delegate{if(!testMode)NativeDesktop.Lower(Home.Handle);};Home.Shown+=delegate{NativeDesktop.Lower(Home.Handle);};
   if(show){DesktopFolders=new DesktopFolderForm(this);DesktopFolders.FormClosed+=delegate{Quit();};DesktopFolders.Show();Home.Show();NativeDesktop.Lower(Home.Handle);CreateTray();}
   }catch{Quit();throw;}
  }
  protected override void Dispose(bool disposing){if(disposing)Quit();base.Dispose(disposing);}
  public void Change(Action<DesktopData> mutation){TryChange(mutation);}
  public bool TryChange(Action<DesktopData> mutation){try{Store.Edit(mutation);QueueRefresh();return true;}catch(Exception ex){Fail(ex);return false;}}
  public void ReportError(Exception ex){Fail(ex);}
  void Fail(Exception ex){if(testMode)throw ex;Modal(owner=>MessageBox.Show(owner,ex.Message,"MacDesk · 操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning));}
  Form dialogOwner;
  bool HostedHome {get{return Home!=null&&Home.DesktopChildMode;}}
  sealed class DialogOwnerForm:Form {public DialogOwnerForm(){ShowInTaskbar=true;FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;TopMost=false;}protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle=(p.ExStyle|0x80)&~0x40000;return p;}}}
  Form OwnDialogWindow(){if(dialogOwner==null||dialogOwner.IsDisposed)dialogOwner=new DialogOwnerForm();dialogOwner.Bounds=Screen.FromControl(Home).WorkingArea;IntPtr ready=dialogOwner.Handle;return dialogOwner;}
  IWin32Window Owner(){if(Panel!=null&&!Panel.IsDisposed&&Panel.Visible&&!closeMotion)return Panel;if(!HostedHome)return Home;return OwnDialogWindow();}
  [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr hwnd);
  [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]static extern IntPtr SetWindowLongPtr64(IntPtr hwnd,int index,IntPtr value);
  [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]static extern IntPtr GetWindowLongPtr64(IntPtr hwnd,int index);
  [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="SetWindowLongW")]static extern int SetWindowLong32(IntPtr hwnd,int index,int value);
  [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern int GetWindowLong32(IntPtr hwnd,int index);
  static void OwnPopup(IntPtr popup,IntPtr owner){if((GetWindowLong32(popup,-16)&0x40000000)!=0)throw new InvalidOperationException("菜单必须是本程序的独立弹出窗口。");if(IntPtr.Size==8)SetWindowLongPtr64(popup,-8,owner);else SetWindowLong32(popup,-8,owner.ToInt32());IntPtr actual=IntPtr.Size==8?GetWindowLongPtr64(popup,-8):new IntPtr(GetWindowLong32(popup,-8));if(actual!=owner)throw new InvalidOperationException("无法设置本程序菜单的所有者。");}
  public void ShowDesktopMenu(ContextMenuStrip menu,Point screenPoint){var owner=OwnDialogWindow();OwnPopup(menu.Handle,owner.Handle);try{menu.Show(owner,owner.PointToClient(screenPoint));if(menu.IsDisposed||!menu.Visible||!menu.IsHandleCreated)return;IntPtr popup=menu.Handle;OwnPopup(popup,owner.Handle);SetForegroundWindow(popup);if(!menu.IsDisposed&&menu.Visible&&menu.IsHandleCreated)OwnPopup(popup,owner.Handle);}catch{if(!menu.IsDisposed)menu.Close();throw;}}
  T Modal<T>(Func<IWin32Window,T> action){FinishMotion();bool hosted=HostedHome,enabled=Home.Enabled,foldersEnabled=DesktopFolders!=null&&DesktopFolders.Enabled;modalDepth++;if(hosted)Home.Enabled=false;if(DesktopFolders!=null)DesktopFolders.Enabled=false;try{return action(Owner());}finally{if(hosted&&!Home.IsDisposed)Home.Enabled=enabled;if(DesktopFolders!=null&&!DesktopFolders.IsDisposed)DesktopFolders.Enabled=foldersEnabled;modalDepth--;}}
  DeskGroup Group(string id){var g=Data.groups.Find(v=>v.id==id);if(g==null)throw new InvalidDataException("分类已变化，请重试。");return g;}
  void QueueRefresh(){if(refreshQueued||closing)return;refreshQueued=true;Action action=delegate{refreshQueued=false;if(closing)return;Home.RefreshData();if(DesktopFolders!=null)DesktopFolders.RefreshData();foreach(var pair in panels.ToArray()){if(!Data.groups.Any(g=>g.id==pair.Key)){if(Panel==pair.Value){StopMotion();DisposeMotion();Panel=null;}pair.Value.Dispose();panels.Remove(pair.Key);}else pair.Value.RefreshData();}};if(Home.IsHandleCreated)Home.BeginInvoke(action);else action();}
  public void OpenGroup(string id,Point sourceCenter){try{
   Group(id);if(Panel!=null&&Panel.GroupId==id&&(Panel.Visible||motion!=null)&&!closeMotion){ClosePanel();return;}
   StopMotion();DisposeMotion();if(Panel!=null)Panel.Hide();
   if(!panels.ContainsKey(id)||panels[id].IsDisposed)panels[id]=new PanelForm(this,id);
   Panel=panels[id];Panel.Text="MacDesk · "+Group(id).title;Panel.RefreshData();
   Rectangle work=Screen.FromPoint(sourceCenter).WorkingArea;Size size=Panel.GetExpandedSize(sourceCenter);
   originBounds=new Rectangle(sourceCenter.X-44,sourceCenter.Y-44,88,88);
   // Phone folders open as a large glass sheet in the middle of the screen, not beside the icon.
   int centerX=work.Left+(work.Width-size.Width)/2,centerY=work.Top+(work.Height-size.Height)/2-10;
   expandedBounds=new Rectangle(Math.Max(work.Left+12,Math.Min(work.Right-size.Width-12,centerX)),Math.Max(work.Top+12,Math.Min(work.Bottom-size.Height-12,centerY)),size.Width,size.Height);
   Panel.Opacity=1;Panel.WindowState=FormWindowState.Normal;
   // A warm snapshot already has the final size; deferring layout keeps the first animated frame immediate.
   bool warm=Panel.HasCachedMotion(size);
   if(!warm){Panel.Bounds=expandedBounds;Panel.PerformLayout();}
   if(Panel.Handle==IntPtr.Zero)throw new InvalidOperationException("无法准备文件夹窗口。");
   motion=new GlassMotion(Panel.MotionSnapshot(),OwnDialogWindow(),true);fromBounds=originBounds;toBounds=expandedBounds;motionBounds=fromBounds;alphaFrom=motionAlpha=.12;alphaTo=1;closeMotion=false;
   PrepareMotion();ShowDim(work,.10);motion.Bounds=fromBounds;motion.Show();SetForegroundWindow(motion.Handle);motion.Present(fromBounds,motionAlpha);motionMouse=(GetAsyncKeyState(1)&0x8000)!=0||(GetAsyncKeyState(2)&0x8000)!=0;StartMotion();
  }catch(Exception ex){StopMotion();DisposeMotion();HideDim();Fail(ex);}}
  public void ClosePanel(){if(Panel==null||Panel.IsDisposed||closeMotion||(!Panel.Visible&&motion==null))return;try{
   // An animation is already on screen: let the render thread turn around without dropping a frame.
   if(motion!=null){reverseRequested=true;return;}
   StopMotion();motion=new GlassMotion(Panel.MotionSnapshot(),OwnDialogWindow(),false);motionBounds=Panel.Bounds;motionAlpha=1;if(dim==null||dim.IsDisposed)ShowDim(Screen.FromControl(Panel).WorkingArea,.30);Panel.Hide();PrepareMotion();motion.Bounds=motionBounds;motion.Show();motion.Present(motionBounds,1);
   fromBounds=motionBounds;toBounds=originBounds;alphaFrom=motionAlpha;alphaTo=0;closeMotion=true;StartMotion();
  }catch(Exception ex){DisposeMotion();Panel.Hide();Fail(ex);}}
  // Runs on its own thread: one UpdateLayeredWindow per refresh, no message-loop jitter.
  void RenderLoop(){long frameTicks=Math.Max(1,Stopwatch.Frequency/120);long next=0;
   try{
    while(motionRunning&&!closing){
     if(Panel==null||Panel.IsDisposed||motion==null)return;
     long now=clock.ElapsedTicks;
     if(now<next){Thread.SpinWait(300);continue;}
     next=now+frameTicks;
     bool down=(GetAsyncKeyState(1)&0x8000)!=0||(GetAsyncKeyState(2)&0x8000)!=0;
     if(!closeMotion&&(reverseRequested||(down&&!motionMouse&&!expandedBounds.Contains(Cursor.Position)&&!SourceContains(Cursor.Position)))){
      reverseRequested=false;fromBounds=motionBounds;toBounds=originBounds;alphaFrom=motionAlpha;alphaTo=0;closeMotion=true;clock.Restart();next=0;motionMouse=down;continue;
     }
     motionMouse=down;
     double t=Math.Min(1,clock.Elapsed.TotalMilliseconds/(closeMotion?CloseMs:OpenMs));
     double e=t*t*(3-2*t);
     Rectangle b=new Rectangle((int)Math.Round(fromBounds.X+(toBounds.X-fromBounds.X)*e),(int)Math.Round(fromBounds.Y+(toBounds.Y-fromBounds.Y)*e),(int)Math.Round(fromBounds.Width+(toBounds.Width-fromBounds.Width)*e),(int)Math.Round(fromBounds.Height+(toBounds.Height-fromBounds.Height)*e));
     double a=Math.Max(0,Math.Min(1,alphaFrom+(alphaTo-alphaFrom)*e));
     motionBounds=b;motionAlpha=a;
     long t0=Stopwatch.GetTimestamp();
     motion.Present(b,a);
     MotionLogFrame((Stopwatch.GetTimestamp()-t0)*1000.0/Stopwatch.Frequency);
     if(t>=1){motionDone=true;break;}
    }
   }catch(Exception ex){try{Home.BeginInvoke((Action)delegate{Fail(ex);});}catch{}return;}
   if(motionDone){try{Home.BeginInvoke((Action)FinishMotion);}catch{}}
  }

  public void SetFolderPosition(string id,int x,int y){var positions=DesktopFolders==null?null:DesktopFolders.SnapshotPositions();int width=DesktopFolders==null?Screen.PrimaryScreen.Bounds.Width:DesktopFolders.ClientSize.Width;int height=DesktopFolders==null?Screen.PrimaryScreen.WorkingArea.Height:DesktopFolders.ClientSize.Height;TryChange(d=>{if(positions!=null)foreach(var group in d.groups){Point p;if(group.desktopX<0&&positions.TryGetValue(group.id,out p)){group.desktopX=p.X;group.desktopY=p.Y;}}var g=Find(d,id);g.desktopX=Math.Max(Math.Min(440,Math.Max(0,width-112)),Math.Min(Math.Max(0,width-112),x));g.desktopY=Math.Max(24,Math.Min(Math.Max(24,height-154),y));});}
   public void AddGroup(){var g=Modal(owner=>EditDialogs.Group(owner,null));if(g!=null)Change(d=>d.groups.Add(g));}
  public void EditGroup(string id){try{var original=Group(id);string title=original.title,subtitle=original.subtitle;var g=Modal(owner=>EditDialogs.Group(owner,original));if(g!=null)Change(d=>{var current=Find(d,id);if(current.title!=title||current.subtitle!=subtitle)throw new InvalidDataException("分类名称已被其他操作修改，请重新编辑。");current.title=g.title;current.subtitle=g.subtitle;});}catch(Exception ex){Fail(ex);}}
  public void DeleteGroup(string id){try{var g=Group(id);if(Modal(owner=>MessageBox.Show(owner,"删除文件夹“"+g.title+"”及其中的入口？\n不会删除或移动任何原文件。","删除文件夹",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning))==DialogResult.OK)Change(d=>d.groups.RemoveAll(v=>v.id==id));}catch(Exception ex){Fail(ex);}}
  public void AddItem(string groupId){var item=Modal(owner=>EditDialogs.Item(owner,null));if(item!=null)Change(d=>Find(d,groupId).items.Add(item));}
  static string ItemSnapshot(DeskItem item){return new JavaScriptSerializer().Serialize(new[]{item.id,item.title,item.description,item.type,item.target,item.icon,item.arguments});}
   public void EditItem(string groupId,string itemId){try{var old=Group(groupId).items.Find(v=>v.id==itemId);if(old==null)return;string baseline=ItemSnapshot(old);var edited=Modal(owner=>EditDialogs.Item(owner,old));if(edited!=null)Change(d=>{var items=Find(d,groupId).items;int i=items.FindIndex(v=>v.id==itemId);if(i<0)throw new InvalidDataException("条目已删除。");if(ItemSnapshot(items[i])!=baseline)throw new InvalidDataException("此入口已被其他操作修改，请重新编辑。");items[i]=edited;});}catch(Exception ex){Fail(ex);}}
  public void DeleteItem(string groupId,string itemId){if(Modal(owner=>MessageBox.Show(owner,"只删除此入口，不会删除原文件。","移除入口",MessageBoxButtons.OKCancel,MessageBoxIcon.Question))==DialogResult.OK)Change(d=>Find(d,groupId).items.RemoveAll(v=>v.id==itemId));}
  static DeskGroup Find(DesktopData d,string id){var g=d.groups.Find(v=>v.id==id);if(g==null)throw new InvalidDataException("分类已删除。");return g;}
  public bool TryMoveItem(string sourceGroup,string itemId,string targetGroup,int insertIndex){return TryChange(d=>{var source=Find(d,sourceGroup).items;var target=Find(d,targetGroup).items;int old=source.FindIndex(v=>v.id==itemId);if(old<0)throw new InvalidDataException("条目已删除。");var item=source[old];int index=insertIndex<0?target.Count:Math.Min(target.Count,Math.Max(0,insertIndex));if(source==target&&old<index)index--;source.RemoveAt(old);target.Insert(Math.Min(index,target.Count),item);});}
  public bool TryMoveGroup(string groupId,int targetIndex){return TryChange(d=>{int old=d.groups.FindIndex(v=>v.id==groupId);if(old<0)throw new InvalidDataException("分类已删除。");int index=targetIndex<0?d.groups.Count:Math.Min(d.groups.Count,Math.Max(0,targetIndex));var g=d.groups[old];if(old<index)index--;d.groups.RemoveAt(old);d.groups.Insert(Math.Min(index,d.groups.Count),g);});}
  public bool TryAddPaths(string groupId,string[] paths,int insertIndex){try{var entries=new List<DeskItem>();foreach(string path in paths)entries.Add(EntryServices.FromPath(path));return TryChange(d=>{var items=Find(d,groupId).items;int index=insertIndex<0?items.Count:Math.Min(items.Count,Math.Max(0,insertIndex));items.InsertRange(index,entries);});}catch(Exception ex){Fail(ex);return false;}}
  public bool TryAddUrl(string groupId,string url,int insertIndex){try{var item=EntryServices.FromUrl(url);return TryChange(d=>{var items=Find(d,groupId).items;int index=insertIndex<0?items.Count:Math.Min(items.Count,Math.Max(0,insertIndex));items.Insert(index,item);});}catch(Exception ex){Fail(ex);return false;}}
  public void MoveItem(string sourceGroup,string itemId,string targetGroup,int index){TryMoveItem(sourceGroup,itemId,targetGroup,index);}
  public void MoveGroup(string id,int index){TryMoveGroup(id,index);}
  public void AddPaths(string groupId,string[] paths,int index){TryAddPaths(groupId,paths,index);}
  public void AddUrl(string groupId,string url,int index){TryAddUrl(groupId,url,index);}
  public void Launch(string groupId,string itemId){try{var item=Group(groupId).items.Find(v=>v.id==itemId);if(item==null)return;EntryServices.Open(item);ClosePanel();}catch(Exception ex){Fail(ex);}}
  void CreateTray(){tray=new NotifyIcon{Icon=SystemIcons.Application,Text="MacDesk · 拖入添加，右键编辑，自动保存",Visible=true};var menu=new ContextMenuStrip();menu.Items.Add("显示桌面卡片",null,delegate{Home.WindowState=FormWindowState.Normal;Home.Show();if(DesktopFolders!=null){DesktopFolders.RefreshData();DesktopFolders.Show();}NativeDesktop.Lower(Home.Handle);});menu.Items.Add("新增文件夹",null,delegate{AddGroup();});menu.Items.Add("重新读取配置",null,delegate{try{Store.Reload();QueueRefresh();}catch(Exception ex){Fail(ex);}});menu.Items.Add("打开配置备份目录",null,delegate{try{string dir=Path.Combine(Store.Root,"Backups");Directory.CreateDirectory(dir);EntryServices.Open(new DeskItem{type="folder",target=dir});}catch(Exception ex){Fail(ex);}});menu.Items.Add(new ToolStripSeparator());menu.Items.Add("退出桌面启动器",null,delegate{Quit();});tray.ContextMenuStrip=menu;tray.DoubleClick+=delegate{Home.WindowState=FormWindowState.Normal;Home.Show();NativeDesktop.Lower(Home.Handle);};}
  public void Quit(){if(closing)return;closing=true;StopMotion();DisposeMotion();HideDim();foreach(var p in panels.Values)p.Dispose();panels.Clear();if(tray!=null){tray.Visible=false;tray.Dispose();}if(DesktopFolders!=null&&!DesktopFolders.IsDisposed)DesktopFolders.Dispose();if(Home!=null&&!Home.IsDisposed)Home.Dispose();if(dialogOwner!=null&&!dialogOwner.IsDisposed)dialogOwner.Dispose();ExitThread();}
 }
}
