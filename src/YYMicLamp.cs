using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Accessibility;
using Microsoft.Win32;

[assembly:System.Reflection.AssemblyTitle("YY 麦克风灯")]
[assembly:System.Reflection.AssemblyDescription("通过 YY 自身的辅助功能按钮切换自由发言；无键盘模拟。")]
[assembly:System.Reflection.AssemblyVersion("1.3.0.0")]

static class Native {
 public delegate bool EnumProc(IntPtr hwnd, IntPtr param);
 public delegate void WinEventProc(IntPtr hook,uint eventId,IntPtr hwnd,int objectId,int childId,uint thread,uint time);
 [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min,uint max,IntPtr module,WinEventProc callback,uint pid,uint thread,uint flags);
 [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr param);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr param);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder value, int count);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder value, int count);
 [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x,int y,int cx,int cy,uint flags);
 [DllImport("oleacc.dll")] public static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
 [DllImport("oleacc.dll")] public static extern int AccessibleChildren(IAccessible parent, int start, int count, [Out,MarshalAs(UnmanagedType.LPArray, SizeParamIndex=2)] object[] children, out int obtained);
 public static string Class(IntPtr h){var b=new StringBuilder(256);GetClassName(h,b,b.Capacity);return b.ToString();}
 public static string Title(IntPtr h){var b=new StringBuilder(512);GetWindowText(h,b,b.Capacity);return b.ToString();}
}

sealed class Channel {
 public IntPtr Host;
 public IAccessible Voice;
 public IAccessible Mic;
 public bool? ReadState() {
  if(!Native.IsWindow(Host) || Voice==null)return null;
  string voice=ReadName(Voice);
  if(IsPush(voice))return false;
  if(voice!="自由发言")return null;
  if(Mic!=null && ReadName(Mic).StartsWith("打开麦克风"))return false;
  return true;
 }
 public static string ReadName(IAccessible a){return (a.get_accName(0)??"").Trim('\0',' ','\r','\n');}
 public static bool IsPush(string n){return Regex.IsMatch(n,"^按住.+说话$");}
 public void Toggle(){
  bool? s=ReadState();if(!s.HasValue)throw new InvalidOperationException("YY 发言状态暂时无法读取");
  if(s.Value)Voice.accDoDefaultAction(0);
  else {
   if(Mic!=null && ReadName(Mic).StartsWith("打开麦克风"))Mic.accDoDefaultAction(0);
   if(IsPush(ReadName(Voice)))Voice.accDoDefaultAction(0);
  }
 }
 public static Channel Find(){
  var ids=new HashSet<int>();foreach(var p in Process.GetProcessesByName("YY")){ids.Add(p.Id);p.Dispose();}
  if(ids.Count==0)return null;
  var roots=new List<IntPtr>();
  Native.EnumWindows(delegate(IntPtr h,IntPtr l){uint id;Native.GetWindowThreadProcessId(h,out id);if(ids.Contains((int)id)&&Native.Class(h)=="QWidget"&&Regex.IsMatch(Native.Title(h),"^\\d+[-－]"))roots.Add(h);return true;},IntPtr.Zero);
  roots.Sort(delegate(IntPtr a,IntPtr b){return Native.IsWindowVisible(b).CompareTo(Native.IsWindowVisible(a));});
  foreach(var root in roots){
   var hosts=new List<IntPtr>();Native.EnumChildWindows(root,delegate(IntPtr h,IntPtr l){if(Native.Class(h)=="Chrome_RenderWidgetHostHWND")hosts.Add(h);return true;},IntPtr.Zero);
   foreach(var h in hosts){
    try{
     object obj;Guid iid=new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");if(Native.AccessibleObjectFromWindow(h,0xfffffffc,ref iid,out obj)!=0)continue;
     var c=new Channel();c.Host=h;int count=0;Scan(obj as IAccessible,c,0,ref count);if(c.Voice!=null)return c;
    }catch{}
   }
  }
  return null;
 }
 static void Scan(IAccessible a,Channel result,int depth,ref int visited){
  if(a==null||depth>14||visited++>1400)return;
  try{
   string name=ReadName(a);int role=Convert.ToInt32(a.get_accRole(0));
   if(role==43){if(name=="自由发言"||IsPush(name))result.Voice=a;else if(name.StartsWith("关闭麦克风")||name.StartsWith("打开麦克风"))result.Mic=a;}
   if(result.Voice!=null&&result.Mic!=null)return;
   // Nested web documents are advertisements or the channel content, not the voice toolbar.
   if(depth>0&&role==15)return;
   int n=Math.Min(a.accChildCount,1500);if(n<=0)return;
   var children=new object[n];int got;Native.AccessibleChildren(a,0,n,children,out got);
   for(int i=0;i<got;i++){
    IAccessible child=children[i] as IAccessible;
    if(child==null&&children[i] is int){try{child=a.get_accChild(children[i]) as IAccessible;}catch{}}
    Scan(child,result,depth+1,ref visited);
    if(result.Voice!=null&&result.Mic!=null)return;
   }
  }catch{}
 }
}

sealed class Backend:IDisposable {
 readonly AutoResetEvent signal=new AutoResetEvent(false);
 readonly Thread worker;volatile bool stop;int requested;
 public event Action<bool?,string,bool> Update;
 Channel channel;
 readonly string logPath;
 public Backend(string path){logPath=path;worker=new Thread(Loop);worker.IsBackground=true;worker.SetApartmentState(ApartmentState.STA);worker.Start();}
 public void Request(){Interlocked.Exchange(ref requested,1);signal.Set();}
 void Log(string s){if(logPath!=null)try{File.AppendAllText(logPath,DateTime.Now.ToString("HH:mm:ss.fff")+" "+s+Environment.NewLine,Encoding.UTF8);}catch{}}
 public void Trace(string s){Log(s);}
 void Publish(bool? s,string text,bool busy){var ev=Update;if(ev!=null)ev(s,text,busy);}
 void Loop(){
  DateTime nextFind=DateTime.MinValue;bool? last=null;
  while(!stop){
   try{
    if(channel==null&&DateTime.UtcNow>=nextFind){channel=Channel.Find();nextFind=DateTime.UtcNow.AddSeconds(2);if(channel!=null)Log("CONNECTED host="+channel.Host);}
    bool? state=channel==null?null:channel.ReadState();
    if(!state.HasValue){channel=null;Publish(null,"未连接 YY 发言按钮，请打开 YY 并进入频道",false);}
    else {
     if(state!=last){Log("STATE "+(state.Value?"OPEN":"CLOSED"));last=state;}
     Publish(state,state.Value?"开麦 · 点击关麦，拖动可移动":"关麦 · 点击开麦，拖动可移动",false);
    }
    if(Interlocked.Exchange(ref requested,0)==1){
     if(channel==null){Publish(null,"未找到 YY 发言按钮，暂时无法操作",false);}
     else {
      bool before=channel.ReadState().Value;Publish(before,"正在切换 YY…",true);Log("TOGGLE requested "+before);
      channel.Toggle();bool changed=false;
      for(int i=0;i<16&&!stop;i++){Thread.Sleep(100);bool? now=channel.ReadState();if(now.HasValue&&now.Value!=before){changed=true;last=now;Log("CONFIRMED "+(now.Value?"OPEN":"CLOSED"));Publish(now,now.Value?"开麦 · 点击关麦":"关麦 · 点击开麦",false);break;}}
      if(!changed){Log("UNCONFIRMED");Publish(channel.ReadState(),"YY 未确认切换，灯的颜色保持实际读取的状态",false);}
     }
    }
   }catch(Exception e){Log("ERROR "+e.GetType().Name+" "+e.Message);channel=null;Publish(null,"YY 暂时无法读取，正在重新连接",false);}
   signal.WaitOne(500);
  }
 }
 public void Dispose(){stop=true;signal.Set();}
}

sealed class LampButton:Button {
 public LampButton(){SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true);}
 public Action<Point> FinishMove;
 protected override void OnMouseUp(MouseEventArgs e){if(FinishMove!=null)FinishMove(PointToScreen(e.Location));base.OnMouseUp(e);}
}

sealed class WaveStrip:Control {
 float level,peakHold;DateTime holdUntil;
 bool open,active;
 public WaveStrip(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);AccessibleRole=AccessibleRole.Indicator;}
 public void SetOpen(bool value){open=value;if(!value){level=0;peakHold=0;active=false;}Invalidate();}
 public void Sample(float peak,bool available){
  active=open&&available;
  if(!active){level=0;peakHold=0;}
  else {float target=peak<=0?0:(float)Math.Max(0,Math.Min(1,(20*Math.Log10(peak)+50)/50));level=target>=level?target:level*.75f+target*.25f;if(level>=peakHold){peakHold=level;holdUntil=DateTime.UtcNow.AddMilliseconds(350);}else if(DateTime.UtcNow>holdUntil)peakHold=Math.Max(level,peakHold-.025f);}
  Invalidate();
 }
 protected override void OnPaint(PaintEventArgs e){
  var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(BackColor);
  using(var path=new GraphicsPath()){
   int d=Math.Min(8,Height-1);path.AddArc(0,0,d,d,180,90);path.AddArc(Width-1-d,0,d,d,270,90);path.AddArc(Width-1-d,Height-1-d,d,d,0,90);path.AddArc(0,Height-1-d,d,d,90,90);path.CloseFigure();
   using(var fill=new SolidBrush(Color.FromArgb(15,20,29)))g.FillPath(fill,path);
  }
  int center=Height/2;float available=Width-8;
  using(var pen=new Pen(Color.FromArgb(62,73,91),2)){pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;g.DrawLine(pen,4,center,Width-5,center);}
  if(open&&active&&level>.001f){using(var pen=new Pen(level>.9f?Color.FromArgb(248,193,89):Color.FromArgb(67,222,163),4)){pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;g.DrawLine(pen,4,center,4+level*available,center);}using(var pen=new Pen(Color.FromArgb(230,240,247),1))g.DrawLine(pen,4+peakHold*available,center-3,4+peakHold*available,center+3);}
  if(open&&!active)using(var fill=new SolidBrush(Color.FromArgb(248,178,76)))g.FillEllipse(fill,Width-8,3,4,4);
 }
}

sealed class LampForm:Form {
 readonly Backend backend;readonly ToolTip tip=new ToolTip();
 readonly AudioMonitor audio;
 readonly WaveStrip wave=new WaveStrip();
 readonly ContextMenuStrip menu=new ContextMenuStrip();
 readonly NotifyIcon tray=new NotifyIcon();
 readonly ToolStripMenuItem visibilityItem=new ToolStripMenuItem("隐藏悬浮窗");
 readonly ToolStripMenuItem gameItem=new ToolStripMenuItem("游戏置顶模式");
 readonly Native.WinEventProc foregroundCallback;
 IntPtr foregroundHook;bool gameMode=true;
 bool audioActive,audioExact;float latestPeak;
 readonly System.Windows.Forms.Timer topTimer=new System.Windows.Forms.Timer();
 EventHandler displayChanged;
 readonly Font exitFont=new Font("Microsoft YaHei UI",13,FontStyle.Regular,GraphicsUnit.Pixel);
 readonly LampButton lamp=new LampButton();readonly Button close=new Button();
 bool? micState;bool busy;string status="正在连接 YY…";
 Point downScreen,startLocation;bool pressed,dragged;
 const int CardWidth=76,CardHeight=68;
 bool lampHover,exitHover;
 readonly Rectangle lampRect=new Rectangle(6,8,64,24);
 public LampForm(string log){
  Text="YY 麦克风灯";Name="YYMicLamp";FormBorderStyle=FormBorderStyle.None;AutoScaleMode=AutoScaleMode.None;
  ClientSize=new Size(CardWidth,CardHeight);BackColor=Color.FromArgb(24,29,39);TopMost=true;ShowInTaskbar=false;
  MinimumSize=new Size(CardWidth,CardHeight);MaximumSize=new Size(CardWidth,CardHeight);
  tip.InitialDelay=800;tip.ReshowDelay=800;tip.AutoPopDelay=1800;
  using(var stream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("YYMicLamp.Icon")){Icon=stream!=null?new Icon(stream):Icon.ExtractAssociatedIcon(Application.ExecutablePath);}
  DoubleBuffered=true;StartPosition=FormStartPosition.Manual;var area=Screen.PrimaryScreen.WorkingArea;Location=new Point(area.Right-100,area.Top+80);
  try{using(var key=Registry.CurrentUser.OpenSubKey(@"Software\YYMicLamp")){if(key!=null){int x=Convert.ToInt32(key.GetValue("X",Location.X)),y=Convert.ToInt32(key.GetValue("Y",Location.Y));var p=new Point(x,y);if(Screen.AllScreens.Length>0&&Array.Exists(Screen.AllScreens,s=>s.WorkingArea.Contains(p)))Location=p;gameMode=Convert.ToInt32(key.GetValue("GameMode",1))!=0;}}}catch{}
  Location=ClampLocation(Location);
  using(var path=Rounded(new Rectangle(0,0,CardWidth,CardHeight),11))Region=new Region(path);
  AccessibleName="YY 麦克风开关";
  backend=new Backend(log);backend.Update+=Receive;
  audio=new AudioMonitor(backend.Trace);audio.Update+=ReceiveAudio;
  gameItem.CheckOnClick=true;gameItem.Checked=gameMode;
  gameItem.CheckedChanged+=delegate{gameMode=gameItem.Checked;topTimer.Interval=gameMode?500:2000;SavePosition();KeepOnTop();backend.Trace("GAME_MODE "+gameMode);};
  visibilityItem.Click+=delegate{ToggleVisibility();};
  menu.Items.Add(visibilityItem);menu.Items.Add(new ToolStripSeparator());menu.Items.Add(gameItem);menu.Items.Add(new ToolStripSeparator());menu.Items.Add("退出",null,delegate{Close();});ContextMenuStrip=menu;
  tray.Icon=(Icon)Icon.Clone();tray.Text="YY 麦克风灯（双击显示或隐藏）";tray.ContextMenuStrip=menu;tray.Visible=true;
  tray.MouseDoubleClick+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)ToggleVisibility();};
  Shown+=delegate{Native.SetWindowPos(Handle,new IntPtr(-1),Location.X,Location.Y,CardWidth,CardHeight,0x10);backend.Trace("WINDOW "+Bounds+" client="+ClientSize);};
  lamp.Bounds=lampRect;lamp.FlatStyle=FlatStyle.Flat;lamp.FlatAppearance.BorderSize=0;lamp.TabStop=false;lamp.Text="";lamp.BackColor=BackColor;lamp.AccessibleName="YY 发言开关";lamp.Cursor=Cursors.Hand;
  lamp.Paint+=delegate(object sender,PaintEventArgs e){PaintLamp(e.Graphics);};
  lamp.MouseEnter+=delegate{lampHover=true;lamp.Invalidate();};lamp.MouseLeave+=delegate{lampHover=false;lamp.Invalidate();};
  lamp.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left){pressed=true;dragged=false;downScreen=lamp.PointToScreen(e.Location);startLocation=Location;backend.Trace("LAMP DOWN "+e.Location+" at="+Location);}};
  lamp.MouseMove+=delegate(object sender,MouseEventArgs e){MoveDrag(lamp.PointToScreen(e.Location));};
  lamp.FinishMove=MoveDrag;
  lamp.MouseUp+=delegate(object sender,MouseEventArgs e){pressed=false;if(dragged)SavePosition();backend.Trace("LAMP UP "+e.Location+" dragged="+dragged+" at="+Location);};
  lamp.Click+=delegate{if(!dragged&&!busy){backend.Trace("LAMP CLICK");backend.Request();}};
  wave.SetBounds(8,34,60,8);wave.BackColor=BackColor;wave.AccessibleName="YY 音量（关麦，零波动）";Controls.Add(wave);
  wave.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left){pressed=true;dragged=false;downScreen=wave.PointToScreen(e.Location);startLocation=Location;wave.Capture=true;}};
  wave.MouseMove+=delegate(object sender,MouseEventArgs e){MoveDrag(wave.PointToScreen(e.Location));};
  wave.MouseUp+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left){MoveDrag(wave.PointToScreen(e.Location));pressed=false;wave.Capture=false;if(dragged)SavePosition();}};
  close.SetBounds(8,44,60,22);close.FlatStyle=FlatStyle.Flat;close.FlatAppearance.BorderSize=0;close.TabStop=false;close.Text="";close.BackColor=BackColor;close.AccessibleName="退出悬浮灯";close.Cursor=Cursors.Hand;
  close.Paint+=delegate(object sender,PaintEventArgs e){var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(BackColor);using(var p=Rounded(new Rectangle(0,0,59,21),6))using(var fill=new SolidBrush(exitHover?Color.FromArgb(58,66,82):Color.FromArgb(39,46,59)))g.FillPath(fill,p);TextRenderer.DrawText(g,"退出",exitFont,new Rectangle(0,0,60,22),Color.FromArgb(205,214,228),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);};
  close.MouseEnter+=delegate{exitHover=true;close.Invalidate();};close.MouseLeave+=delegate{exitHover=false;close.Invalidate();};close.Click+=delegate{Close();};
  Controls.Add(lamp);Controls.Add(close);tip.SetToolTip(close,"关闭悬浮灯（保留 YY 当前发言状态）");
  topTimer.Interval=gameMode?500:2000;topTimer.Tick+=delegate{KeepOnTop();};topTimer.Start();
  foregroundCallback=delegate(IntPtr hook,uint eventId,IntPtr hwnd,int objectId,int childId,uint thread,uint time){if(gameMode&&!IsDisposed&&IsHandleCreated)try{BeginInvoke((Action)KeepOnTop);}catch{}};
  foregroundHook=Native.SetWinEventHook(3,3,IntPtr.Zero,foregroundCallback,0,0,2);
  displayChanged=delegate{if(!IsDisposed&&IsHandleCreated)try{BeginInvoke((Action)delegate{Location=ClampLocation(Location);SavePosition();KeepOnTop();});}catch{}};
  SystemEvents.DisplaySettingsChanged+=displayChanged;RefreshHints();
  FormClosed+=delegate{SavePosition();SystemEvents.DisplaySettingsChanged-=displayChanged;if(foregroundHook!=IntPtr.Zero)Native.UnhookWinEvent(foregroundHook);topTimer.Dispose();audio.Dispose();backend.Dispose();tray.Visible=false;var trayImage=tray.Icon;tray.Dispose();trayImage.Dispose();tip.Dispose();menu.Dispose();exitFont.Dispose();};
 }
 protected override bool ShowWithoutActivation{get{return true;}}
 protected override CreateParams CreateParams{get{var c=base.CreateParams;c.ExStyle|=0x08000000;return c;}}
 protected override void WndProc(ref Message m){if(m.Msg==0x21){m.Result=new IntPtr(3);return;}base.WndProc(ref m);}
 void Receive(bool? value,string text,bool working){if(IsDisposed||!IsHandleCreated)return;try{BeginInvoke((Action)delegate{if(value==micState&&working==busy&&text==status)return;micState=value;status=text;busy=working;AccessibleDescription=status;lamp.AccessibleName=!value.HasValue?"未连接 YY":value.Value?"开麦，点击关麦":"关麦，点击开麦";tray.Text="YY 麦克风灯 · "+(!value.HasValue?"未连接 YY":value.Value?"开麦":"关麦")+"（双击显示或隐藏）";bool open=value==true;audio.SetEnabled(open);wave.SetOpen(open);if(!open){latestPeak=0;audioActive=false;}RefreshAudioHint();tip.SetToolTip(lamp,!value.HasValue?status:"");lamp.Invalidate();Invalidate();backend.Trace("WAVE_GATE "+open);});}catch{}}
 void ReceiveAudio(float peak,bool active,bool exact){if(IsDisposed||!IsHandleCreated)return;try{BeginInvoke((Action)delegate{bool changed=audioActive!=active||audioExact!=exact;audioActive=micState==true&&active;audioExact=exact;latestPeak=micState==true?peak:0;wave.Sample(latestPeak,audioActive);if(changed)RefreshAudioHint();});}catch{}}
 void RefreshAudioHint(){
  string text=micState!=true?"关麦，音量条归零":!audioActive?"YY 已开麦，暂未检测到 YY 的音频输入（橙点）":audioExact?"正在监测 YY 的麦克风输入音量；不能确认其他人是否已听到":"正在监测 YY 使用的麦克风音量；不能确认其他人是否已听到";
  wave.AccessibleName="YY 音量（"+(micState!=true?"关麦，零波动":audioActive?"已检测到输入":"未检测到 YY 音频输入")+"）";tip.SetToolTip(wave,text);
 }
 void RefreshHints(){tip.SetToolTip(this,"拖动可移动，右键设置游戏置顶");AccessibleDescription=status+"，可拖动";}
 void ToggleVisibility(){if(Visible)Hide();else{Show();KeepOnTop();}visibilityItem.Text=Visible?"隐藏悬浮窗":"显示悬浮窗";backend.Trace("OVERLAY_VISIBLE "+Visible);}
 void KeepOnTop(){if(!IsDisposed&&IsHandleCreated&&Visible)Native.SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x213);}
 Point ClampLocation(Point point){var area=Screen.FromPoint(point).WorkingArea;return new Point(Math.Max(area.Left,Math.Min(point.X,area.Right-CardWidth)),Math.Max(area.Top,Math.Min(point.Y,area.Bottom-CardHeight)));}
 static GraphicsPath Rounded(Rectangle r,int radius){var p=new GraphicsPath();int d=radius*2;p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
 protected override void OnPaint(PaintEventArgs e){
  var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
  using(var pen=new Pen(Color.FromArgb(59,68,85),1))using(var p=Rounded(new Rectangle(1,1,CardWidth-3,CardHeight-3),10))g.DrawPath(pen,p);
  using(var brush=new SolidBrush(Color.FromArgb(92,104,122)))for(int i=0;i<3;i++)g.FillEllipse(brush,30+i*6,3,2,2);
 }
 void PaintLamp(Graphics g){
  g.Clear(BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;
  Color colour=!micState.HasValue?Color.FromArgb(125,138,158):micState.Value?Color.FromArgb(43,207,146):Color.FromArgb(244,98,115);
  var r=new Rectangle(1,1,61,21);
  using(var path=Rounded(r,6)){
   using(var fill=new LinearGradientBrush(r,lampHover?ControlPaint.Light(colour,.16f):colour,ControlPaint.Dark(colour,.12f),90f))g.FillPath(fill,path);
   using(var pen=new Pen(Color.FromArgb(65,255,255,255),1))g.DrawPath(pen,path);
  }
  using(var pen=new Pen(Color.FromArgb(245,255,255,255),1.5f)){
   pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;
   if(micState.HasValue){
    using(var body=Rounded(new Rectangle(29,4,6,10),3))g.DrawPath(pen,body);
    g.DrawArc(pen,26,9,12,9,0,180);g.DrawLine(pen,32,18,32,20);g.DrawLine(pen,29,20,35,20);
    if(!micState.Value){using(var gap=new Pen(colour,3.5f)){gap.StartCap=LineCap.Round;gap.EndCap=LineCap.Round;g.DrawLine(gap,25,5,39,19);}g.DrawLine(pen,25,5,39,19);}
   }else{TextRenderer.DrawText(g,"?",exitFont,new Rectangle(0,0,64,24),Color.White,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);}
  }
  if(busy)using(var pen=new Pen(Color.FromArgb(235,255,255,255),1.5f))g.DrawArc(pen,48,6,10,10,-80,270);
 }
 void MoveDrag(Point current){if(!pressed)return;int dx=current.X-downScreen.X,dy=current.Y-downScreen.Y;if(Math.Abs(dx)+Math.Abs(dy)>4)dragged=true;if(dragged)Location=ClampLocation(new Point(startLocation.X+dx,startLocation.Y+dy));}
 protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;pressed=true;dragged=false;downScreen=PointToScreen(e.Location);startLocation=Location;Capture=true;}
 protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(pressed){MoveDrag(PointToScreen(e.Location));}else{Cursor=Cursors.SizeAll;}}
 protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(e.Button!=MouseButtons.Left||!pressed)return;pressed=false;Capture=false;if(dragged)SavePosition();}
 void SavePosition(){try{using(var key=Registry.CurrentUser.CreateSubKey(@"Software\YYMicLamp")){key.SetValue("X",Location.X);key.SetValue("Y",Location.Y);key.DeleteValue("PositionLocked",false);key.SetValue("GameMode",gameMode?1:0);}}catch{}}
}

static class Program {
 [STAThread] static void Main(string[] args){
  Native.SetProcessDPIAware();bool created;using(var mutex=new Mutex(true,@"Local\YYMicLampSingleInstance",out created)){
   if(!created)return;string log=null;foreach(string arg in args)if(arg.StartsWith("--diagnostics="))log=arg.Substring(14);
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   Application.Run(new LampForm(log));
  }
 }
}
