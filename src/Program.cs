using System;using System.IO;using System.Drawing;using System.Diagnostics;using System.Threading;using System.Windows.Forms;using System.Runtime.InteropServices;using System.Reflection;using System.Security.Cryptography;using System.Text;using System.Web.Script.Serialization;
namespace MacDeskNative {
 public static class NativeDesktop {
  // Keep Home above the verified Shell desktop, below ordinary windows. Never reparent,
  // alter Explorer, use TOPMOST, intercept hotkeys, or make a desktop HWND our owner.
  const int LayerMessage=0x8000+0x3E1;
  const uint PositionFlags=0x0001|0x0002|0x0010|0x0200;
  delegate bool EnumProc(IntPtr h,IntPtr p);
  delegate void WinEventProc(IntPtr hook,uint evt,IntPtr h,int obj,int child,uint thread,uint time);
  [StructLayout(LayoutKind.Sequential)]struct RECT {public int L,T,R,B;}
  [DllImport("user32.dll",SetLastError=true)]static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int z,uint flags);
  [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc proc,IntPtr p);
  [DllImport("user32.dll")]static extern IntPtr GetShellWindow();
  [DllImport("user32.dll")]static extern IntPtr GetDesktopWindow();
  [DllImport("user32.dll")]static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")]static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")]static extern int GetWindowLong(IntPtr h,int index);
  [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr h,out RECT r);
  [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder b,int count);
  [DllImport("user32.dll",SetLastError=true)]static extern bool PostMessage(IntPtr h,int msg,IntPtr w,IntPtr l);
  [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEventProc proc,uint process,uint thread,uint flags);
  [DllImport("user32.dll")]static extern bool UnhookWinEvent(IntPtr hook);
  [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(IntPtr h,int attribute,out int value,int size);
  [DllImport("user32.dll")]static extern IntPtr GetWindow(IntPtr h,uint kind);
  [DllImport("user32.dll")]static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll",ExactSpelling=true)]static extern bool GetWindowBand(IntPtr h,out uint band);
  public sealed class WindowFact {public long Handle,Owner,Parent,Previous,Next;public uint Pid;public string Class,ProbeError;public int ExStyle,Band,Cloak,CloakResult,L,T,R,B;public bool RectValid;public bool Visible,Iconic;}
  static WindowFact Observe(IntPtr h){try{return ReadWindow(h);}catch(Exception ex){return new WindowFact{Handle=h.ToInt64(),Class="<probe-failed>",Band=-3,ProbeError=ex.GetType().Name+": "+ex.Message};}}
  static WindowFact ReadWindow(IntPtr h){uint pid=0,band=0;int cloak=0,b=-1,cloakResult=-1;RECT rect=new RECT();bool rectValid=false;var name=new StringBuilder(128);if(h!=IntPtr.Zero){GetWindowThreadProcessId(h,out pid);GetClassName(h,name,name.Capacity);cloakResult=DwmGetWindowAttribute(h,14,out cloak,4);rectValid=GetWindowRect(h,out rect);try{if(GetWindowBand(h,out band))b=(int)band;}catch(EntryPointNotFoundException){b=-2;}}
   return new WindowFact{Handle=h.ToInt64(),Owner=GetWindow(h,4).ToInt64(),Parent=GetParent(h).ToInt64(),Previous=GetWindow(h,3).ToInt64(),Next=GetWindow(h,2).ToInt64(),Pid=pid,Class=name.ToString(),ExStyle=GetWindowLong(h,-20),Band=b,Cloak=cloakResult==0?cloak:-1,CloakResult=cloakResult,RectValid=rectValid,L=rect.L,T=rect.T,R=rect.R,B=rect.B,Visible=IsWindowVisible(h),Iconic=IsIconic(h)};
  }
  static string diagnosticPath;static long traceSequence;static string trigger="initial",queuedTrigger="initial";
  static readonly System.Collections.Generic.Queue<object> traces=new System.Collections.Generic.Queue<object>();
  public static void ConfigureDiagnostics(string root){diagnosticPath=Path.Combine(root,"native-layer.json");}
  static void Trace(string stage,object detail){if(diagnosticPath==null)return;try{if(traces.Count>=128)traces.Dequeue();traces.Enqueue(new{seq=++traceSequence,utc=DateTime.UtcNow.ToString("o"),stage=stage,trigger=trigger,pending=pending,home=home.ToInt64(),detail=detail});}catch(Exception ex){Debug.WriteLine(ex);}}
  public static void FlushDiagnostics(){if(diagnosticPath==null)return;try{File.WriteAllText(diagnosticPath,new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(traces.ToArray()),new UTF8Encoding(false));}catch(Exception ex){Debug.WriteLine(ex);}}
  public struct LayerItem {public IntPtr Window;public bool Desktop,Eligible;}
  public static bool PlanLayer(LayerItem[] windows,IntPtr home,out IntPtr after,out bool move) {
   after=IntPtr.Zero;move=false;bool seen=false,ordinaryBelow=false;
   foreach(LayerItem item in windows) {
    if(item.Window==home){seen=true;continue;}
    if(item.Desktop){move=!seen||ordinaryBelow;return true;}
    if(item.Eligible){after=item.Window;if(seen)ordinaryBelow=true;}
   }
   return false;
  }
  static IntPtr home,foregroundHook,reorderHook;static LayerWindow layer;
  static readonly WinEventProc eventProc=OnEvent;
  static bool pending,applying,fallbackApplied;
  public static string LayerError {get;private set;}
  public static string ForegroundHookError {get;private set;}
  public static string ReorderHookError {get;private set;}
  public static bool ForegroundHookReady {get{return foregroundHook!=IntPtr.Zero;}}
  public static bool ReorderHookReady {get{return reorderHook!=IntPtr.Zero;}}
  public static string HookError {get{return ForegroundHookError==null?ReorderHookError:ReorderHookError==null?ForegroundHookError:ForegroundHookError+"; "+ReorderHookError;}}
  public static string LastError {get{return HookError==null?LayerError:LayerError==null?HookError:HookError+"; "+LayerError;}}
  public static bool AllowFallback(bool desktopAvailable,ref bool applied){if(desktopAvailable){applied=false;return false;}if(applied)return false;applied=true;return true;}
  sealed class LayerWindow:NativeWindow {
   public LayerWindow(IntPtr h){AssignHandle(h);}
   protected override void WndProc(ref Message m){
    if(m.Msg==LayerMessage){if(m.WParam==new IntPtr(2)){if(diagnosticPath!=null)Trace("snapshot",new{requestNonce=m.LParam.ToInt64(),window=Observe(home)});FlushDiagnostics();return;}pending=false;trigger=m.WParam==new IntPtr(1)?"explicit-maintenance-command":queuedTrigger;Trace("dispatch",new{window=m.HWnd.ToInt64(),command=m.WParam.ToInt64()});Maintain();if(m.WParam==new IntPtr(1))FlushDiagnostics();return;}
    bool gone=m.Msg==0x82;IntPtr destroyed=m.HWnd;
    try{base.WndProc(ref m);}finally{if(gone&&home==destroyed)Release();}
   }
  }
  static void Release(){if(foregroundHook!=IntPtr.Zero)UnhookWinEvent(foregroundHook);if(reorderHook!=IntPtr.Zero)UnhookWinEvent(reorderHook);foregroundHook=reorderHook=IntPtr.Zero;home=IntPtr.Zero;pending=false;fallbackApplied=false;layer=null;LayerError=null;ForegroundHookError=null;ReorderHookError=null;}
  static void Queue(){if(pending||home==IntPtr.Zero||!IsWindow(home))return;pending=true;queuedTrigger=trigger;bool posted=PostMessage(home,LayerMessage,IntPtr.Zero,IntPtr.Zero);int error=posted?0:Marshal.GetLastWin32Error();if(!posted){pending=false;LayerError="PostMessage: "+error;}Trace("queue",new{posted=posted,error=error});}
  static void OnEvent(IntPtr hook,uint evt,IntPtr h,int obj,int child,uint thread,uint time){try{
   if(evt==3){trigger="foreground:"+h.ToInt64();Queue();return;}
   if(evt!=0x8004||obj!=0||child!=0)return;
   IntPtr shell=GetShellWindow();uint pid;GetWindowThreadProcessId(shell,out pid);
   if(h==IntPtr.Zero||h==GetDesktopWindow()||IsDesktopSurface(h,pid)){trigger="reorder:"+h.ToInt64();Queue();}
  }catch(Exception ex){LayerError=ex.Message;}}
  static bool IsDesktopSurface(IntPtr h,uint shellPid){
   if(shellPid==0||h==IntPtr.Zero||!IsWindowVisible(h))return false;
   uint pid;GetWindowThreadProcessId(h,out pid);if(pid!=shellPid)return false;
   var name=new StringBuilder(128);GetClassName(h,name,name.Capacity);string c=name.ToString();if(c!="Progman"&&c!="WorkerW")return false;
   RECT r,t;if(!GetWindowRect(h,out r)||!GetWindowRect(home,out t))return false;
   int x=t.L+(t.R-t.L)/2,y=t.T+Math.Min(304,(t.B-t.T)/2);
   return r.L<=x&&r.R>x&&r.T<=y&&r.B>y;
  }
  static void Maintain(){
   if(applying||home==IntPtr.Zero||!IsWindow(home)||!IsWindowVisible(home)||IsIconic(home)){if(diagnosticPath!=null)Trace("skip",new{applying=applying,window=Observe(home)});return;}
   WindowFact before=diagnosticPath==null?null:Observe(home);object plan=null;bool? positioned=null;long? actualInsertAfter=null;int positionError=0;
   applying=true;try{
    IntPtr shell=GetShellWindow();uint shellPid;GetWindowThreadProcessId(shell,out shellPid);
    var list=new System.Collections.Generic.List<LayerItem>();
    EnumWindows(delegate(IntPtr h,IntPtr p){bool desktop=IsDesktopSurface(h,shellPid);int cloak=0;bool visible=IsWindowVisible(h)&&!IsIconic(h);if(visible){int hr=DwmGetWindowAttribute(h,14,out cloak,4);if(hr!=0)cloak=0;}
     list.Add(new LayerItem{Window=h,Desktop=desktop,Eligible=visible&&cloak==0&&(GetWindowLong(h,-20)&8)==0});return !desktop;},IntPtr.Zero);
    IntPtr after;bool move;bool found=PlanLayer(list.ToArray(),home,out after,out move);
    if(diagnosticPath!=null){IntPtr desktop=IntPtr.Zero;foreach(var item in list)if(item.Desktop){desktop=item.Window;break;}plan=new{shell=Observe(shell),desktop=Observe(desktop),after=Observe(after),point=new{x=before.L+(before.R-before.L)/2,y=before.T+Math.Min(304,(before.B-before.T)/2)},found=found,move=move,fallbackBefore=fallbackApplied,items=list.ConvertAll(v=>new{window=v.Window.ToInt64(),desktop=v.Desktop,eligible=v.Eligible})};}
    if(!found){
     if(AllowFallback(false,ref fallbackApplied)){LayerError="No verified desktop surface; ordinary non-topmost fallback";actualInsertAfter=1;positioned=SetWindowPos(home,new IntPtr(1),0,0,0,0,PositionFlags);positionError=positioned.Value?0:Marshal.GetLastWin32Error();if(!positioned.Value)LayerError+="; SetWindowPos: "+positionError;}
     return;
    }
    AllowFallback(true,ref fallbackApplied);
    if(move){actualInsertAfter=after.ToInt64();positioned=SetWindowPos(home,after,0,0,0,0,PositionFlags);positionError=positioned.Value?0:Marshal.GetLastWin32Error();if(!positioned.Value){LayerError="SetWindowPos: "+positionError;return;}}
    LayerError=null;
   }catch(Exception ex){LayerError=ex.Message;}finally{try{if(diagnosticPath!=null)Trace("pass",new{before=before,plan=plan,actualInsertAfter=actualInsertAfter,positioned=positioned,error=positionError,after=Observe(home),layerError=LayerError,hookError=HookError});}catch(Exception ex){Debug.WriteLine(ex);}finally{applying=false;}}
  }
  public static void Lower(IntPtr window){
   if(window==IntPtr.Zero||!IsWindow(window))return;
   if((GetWindowLong(window,-16)&0x40000000)!=0){if(layer!=null&&layer.Handle!=IntPtr.Zero)layer.ReleaseHandle();Release();return;} // child follows its real desktop surface; never global Z positioning
   if(home!=window){if(layer!=null&&layer.Handle!=IntPtr.Zero)layer.ReleaseHandle();Release();home=window;layer=new LayerWindow(window);}
   // Retry missing hooks on explicit Lower/HandleCreated, not from a recurring timer.
   if(foregroundHook==IntPtr.Zero){foregroundHook=SetWinEventHook(3,3,IntPtr.Zero,eventProc,0,0,0);int error=Marshal.GetLastWin32Error();ForegroundHookError=foregroundHook==IntPtr.Zero?"Foreground hook: "+error:null;}
   if(reorderHook==IntPtr.Zero){reorderHook=SetWinEventHook(0x8004,0x8004,IntPtr.Zero,eventProc,0,0,0);int error=Marshal.GetLastWin32Error();ReorderHookError=reorderHook==IntPtr.Zero?"Desktop reorder hook: "+error:null;}
   trigger="explicit-lower";if(diagnosticPath!=null)Trace("lower",new{window=Observe(home),foregroundHook=ForegroundHookReady,reorderHook=ReorderHookReady,hookError=HookError});Maintain();Queue(); // HandleCreated can precede visibility; maintain again after this stack.
  }
 }
 public static class Program {
  [STAThread]public static int Main(string[] args){string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MacDesk");bool launch=false,diagnostics=false,selfTest=Array.IndexOf(args,"--self-test")>=0;
   try{root=ParseArguments(args,out launch,out diagnostics,out selfTest);if(diagnostics)WriteLog(Path.Combine(root,"native-start.txt"),DateTime.Now.ToString("o")+" PID="+Process.GetCurrentProcess().Id+" Root="+root+Environment.NewLine+String.Join(Environment.NewLine,args));if(selfTest){SelfTest();return 0;}
    if(launch){string exe=Assembly.GetExecutingAssembly().Location;EntryServices.Open(new DeskItem{type="app",target=exe,arguments="--root \""+root+"\""});return 0;}
    string name;using(var hash=SHA256.Create())name="Local\\MacDeskNative-"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()))).Replace("-","").Substring(0,16);
    bool created;using(var mutex=new Mutex(true,name,out created)){if(!created)return 0;Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
     if(diagnostics)NativeDesktop.ConfigureDiagnostics(root);
    using(NativeDesktopHost.Prepare())using(var app=new AppController(root)){ThreadExceptionEventHandler errorHandler=delegate(object sender,ThreadExceptionEventArgs e){WriteLog(Path.Combine(root,"native-error.log"),DateTime.Now.ToString("o")+" "+e.Exception+Environment.NewLine);app.ReportError(e.Exception);};Application.ThreadException+=errorHandler;try{if(diagnostics){WriteStatus(app,root,"constructed");EventHandler ready=null;ready=delegate{Application.Idle-=ready;WriteStatus(app,root,"ready");};Application.Idle+=ready;}Application.Run(app);if(diagnostics)WriteStatus(app,root,"exited");}finally{Application.ThreadException-=errorHandler;}}
    }return 0;
   }catch(Exception ex){if(selfTest){WriteLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test-error.log"),ex.ToString());return 1;}WriteLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"startup-error.log"),ex.ToString());if(!launch)MessageBox.Show(ex.Message,"MacDesk · 启动失败",MessageBoxButtons.OK,MessageBoxIcon.Warning);return 1;}
  }
  static void WriteLog(string path,string message){try{Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,message);}catch{Debug.WriteLine(message);}}
  public static string ParseArguments(string[] args,out bool launch,out bool diagnostics,out bool selfTest){string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MacDesk");launch=false;diagnostics=false;selfTest=false;bool hasRoot=false;for(int i=0;i<args.Length;i++){switch(args[i]){case "--root":if(hasRoot||i+1>=args.Length||String.IsNullOrWhiteSpace(args[i+1])||args[i+1].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException("--root 必须指定且只能指定一次测试数据目录。");hasRoot=true;root=args[++i];break;case "--launch":launch=true;break;case "--diagnostics":diagnostics=true;break;case "--self-test":selfTest=true;break;default:throw new ArgumentException("不支持的启动参数："+args[i]);}}if(launch&&selfTest)throw new ArgumentException("启动与自测模式不能混用。");root=Path.GetFullPath(root);string drive=Path.GetPathRoot(root);if(root.Length>drive.Length)root=root.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);return root;}
  static void WriteStatus(AppController app,string root,string phase){try{var h=app.Home;var c=app.DesktopFolders;IntPtr ch=c!=null&&!c.IsDisposed&&c.IsHandleCreated?c.Handle:IntPtr.Zero;IntPtr hh=!h.IsDisposed&&h.IsHandleCreated?h.Handle:IntPtr.Zero;var status=new{phase=phase,pid=Process.GetCurrentProcess().Id,root=app.Store.Root,groups=app.Data.groups.ConvertAll(g=>new{id=g.id,title=g.title,count=g.items.Count,desktopX=g.desktopX,desktopY=g.desktopY}),foldersHandle=ch.ToInt64(),foldersVisible=c!=null&&!c.IsDisposed&&c.Visible,homeHandle=hh.ToInt64(),visible=!h.IsDisposed&&h.Visible,title=h.Text,x=h.Left,y=h.Top,width=h.Width,height=h.Height,desktop=new{policy=h.DesktopChildMode?"desktop-view-child-no-global-zorder":"home-only-nontopmost-zorder",hostHealthy=NativeDesktopHost.IsHosted(hh),host=NativeDesktopHost.Parent.ToInt64(),hostError=NativeDesktopHost.Error,foregroundHook=NativeDesktop.ForegroundHookReady,reorderHook=NativeDesktop.ReorderHookReady,hookError=NativeDesktop.HookError,layerError=NativeDesktop.LayerError}};WriteLog(Path.Combine(root,"native-session.json"),new JavaScriptSerializer().Serialize(status));NativeDesktop.FlushDiagnostics();}catch(Exception ex){Debug.WriteLine(ex);}}
  static void Check(bool value,string name){if(!value)throw new Exception("Self test failed: "+name);}
  public static void SelfTest(){string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"IntegrationTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);bool launch,diagnostics,selfTest;Check(ParseArguments(new[]{"--diagnostics","--root",root+Path.DirectorySeparatorChar},out launch,out diagnostics,out selfTest)==root&&diagnostics,"canonical explicit root");foreach(var bad in new[]{new[]{"--root"},new[]{"--root","--diagnostics"},new[]{"--rooot",root},new[]{"--root",root,"--root",root},new[]{"--self-test","--launch"}}){bool rejected=false;try{ParseArguments(bad,out launch,out diagnostics,out selfTest);}catch(ArgumentException){rejected=true;}Check(rejected,"invalid startup arguments fail closed");}string file=Path.Combine(root,"原文件.txt");File.WriteAllText(file,"UNCHANGED");
   using(var app=new AppController(root,false)){
    app.Change(d=>{d.groups.Add(new DeskGroup{id="one",title="分类一",subtitle=""});d.groups.Add(new DeskGroup{id="two",title="分类二",subtitle=""});});
    app.AddPaths("one",new[]{file},-1);string first=app.Data.groups[0].items[0].id;app.AddUrl("one","https://example.com/",-1);string second=app.Data.groups[0].items[1].id;
    app.MoveItem("one",first,"one",2);Check(app.Data.groups[0].items[0].id==second,"same group insert gap");
    app.MoveItem("one",first,"two",-1);Check(app.Data.groups[0].items.Count==1&&app.Data.groups[1].items[0].id==first,"cross group transfer");
    app.MoveGroup("one",2);Check(app.Data.groups[1].id=="one","category reorder");
    var fresh=new DesktopStore(root);Check(fresh.Data.groups[0].items[0].id==first,"restart persistence");Check(File.ReadAllText(file)=="UNCHANGED","never move source file");
    var old=fresh.Data.groups[0].title;try{fresh.Edit(d=>{d.groups[0].title="";});throw new Exception("Invalid title accepted");}catch(InvalidDataException){}Check(fresh.Data.groups[0].title==old,"validation rollback");
    app.Quit();
   }
   File.WriteAllText(Path.Combine(root,"PASS.txt"),"Controller AddPaths/AddUrl/reorder/cross-group/restart/validation/source-protection tests passed.");
  }
 }
}
