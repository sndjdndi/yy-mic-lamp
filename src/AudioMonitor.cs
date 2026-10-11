using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

[ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorClass {}
[ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDeviceEnumerator {
 [PreserveSig] int EnumAudioEndpoints(int flow,uint mask,out IMMDeviceCollection devices);
 [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out IMMDevice device);
 [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)]string id,out IMMDevice device);
 [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
 [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
}
[ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDeviceCollection {
 [PreserveSig] int GetCount(out uint count);
 [PreserveSig] int Item(uint index,out IMMDevice device);
}
[ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDevice {
 [PreserveSig] int Activate(ref Guid iid,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)]out object obj);
 [PreserveSig] int OpenPropertyStore(uint access,out IntPtr store);
 [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)]out string id);
 [PreserveSig] int GetState(out uint state);
}
[ComImport,Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioSessionManager2 {
 [PreserveSig] int GetAudioSessionControl(ref Guid session,uint flags,out IntPtr control);
 [PreserveSig] int GetSimpleAudioVolume(ref Guid session,uint flags,out IntPtr volume);
 [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
}
[ComImport,Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioSessionEnumerator {
 [PreserveSig] int GetCount(out int count);
 [PreserveSig] int GetSession(int index,[MarshalAs(UnmanagedType.IUnknown)]out object control);
}
[ComImport,Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioSessionControl2 {
 [PreserveSig] int GetState(out int state);
 [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)]out string name);
 [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)]string name,ref Guid context);
 [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)]out string path);
 [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)]string path,ref Guid context);
 [PreserveSig] int GetGroupingParam(out Guid grouping);
 [PreserveSig] int SetGroupingParam(ref Guid grouping,ref Guid context);
 [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
 [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
 [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)]out string id);
 [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)]out string id);
 [PreserveSig] int GetProcessId(out uint pid);
 [PreserveSig] int IsSystemSoundsSession();
 [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)]bool optOut);
}
[ComImport,Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioMeterInformation {
 [PreserveSig] int GetPeakValue(out float peak);
 [PreserveSig] int GetMeteringChannelCount(out uint channels);
 [PreserveSig] int GetChannelsPeakValues(uint count,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=0)]float[] peaks);
 [PreserveSig] int QueryHardwareSupport(out uint mask);
}

// Read-only Core Audio session inspection. Does not open, record or save microphone audio.
sealed class AudioSource:IDisposable {
 public object SessionObject,EndpointMeterObject;
 public IAudioSessionControl2 Session;
 public IAudioMeterInformation Meter;
 public uint Pid;
 public string DeviceId;
 public bool SessionMeter;
 public bool Read(out float value){
  value=0;int state;
  return Session.GetState(out state)>=0&&state==1&&Meter.GetPeakValue(out value)>=0;
 }
 public void Dispose(){CoreAudio.Release(EndpointMeterObject);CoreAudio.Release(SessionObject);EndpointMeterObject=null;SessionObject=null;Session=null;Meter=null;}
}
static class CoreAudio {
 public static void Release(object obj){if(obj!=null&&Marshal.IsComObject(obj))try{Marshal.ReleaseComObject(obj);}catch{}}
 public static List<AudioSource> FindYY(Action<string> trace){
  var result=new List<AudioSource>();var ids=new HashSet<uint>();
  foreach(string name in new[]{"YY","yyexternal","yyexternal_x64"})foreach(var p in Process.GetProcessesByName(name)){
   try{string path=p.MainModule.FileName;if(name=="YY"||path.IndexOf(@"\duowan\yy\",StringComparison.OrdinalIgnoreCase)>=0)ids.Add((uint)p.Id);}catch{}finally{p.Dispose();}
  }
  IMMDeviceEnumerator root=null;IMMDeviceCollection devices=null;
  try{
   root=(IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
   Marshal.ThrowExceptionForHR(root.EnumAudioEndpoints(1,1,out devices));uint count;Marshal.ThrowExceptionForHR(devices.GetCount(out count));
   for(uint i=0;i<count;i++){
    IMMDevice device=null;object managerObject=null;IAudioSessionEnumerator sessions=null;
    try{
     Marshal.ThrowExceptionForHR(devices.Item(i,out device));string deviceId;device.GetId(out deviceId);
     Guid managerId=typeof(IAudioSessionManager2).GUID;
     Marshal.ThrowExceptionForHR(device.Activate(ref managerId,23,IntPtr.Zero,out managerObject));
     var manager=(IAudioSessionManager2)managerObject;Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));int n;Marshal.ThrowExceptionForHR(sessions.GetCount(out n));
     for(int j=0;j<n;j++){
      object sessionObject=null;
      try{
       Marshal.ThrowExceptionForHR(sessions.GetSession(j,out sessionObject));var session=(IAudioSessionControl2)sessionObject;
       uint pid;int state;session.GetProcessId(out pid);session.GetState(out state);
       if(trace!=null)trace("CAPTURE device="+i+" pid="+pid+" state="+state+" YY="+ids.Contains(pid));
       if(!ids.Contains(pid)||state==2)continue;
       var source=new AudioSource();source.SessionObject=sessionObject;source.Session=session;source.Pid=pid;source.DeviceId=deviceId;
       try{source.Meter=(IAudioMeterInformation)sessionObject;source.SessionMeter=true;}
       catch(InvalidCastException){Guid meterId=typeof(IAudioMeterInformation).GUID;object meterObject;Marshal.ThrowExceptionForHR(device.Activate(ref meterId,23,IntPtr.Zero,out meterObject));source.EndpointMeterObject=meterObject;source.Meter=(IAudioMeterInformation)meterObject;}
       result.Add(source);sessionObject=null;
       if(trace!=null)trace("YY_METER pid="+pid+" mode="+(source.SessionMeter?"session":"YY-device"));
      }catch(Exception e){if(trace!=null)trace("AUDIO_SESSION_ERROR "+e.Message);}finally{Release(sessionObject);}
     }
    }catch(Exception e){if(trace!=null)trace("AUDIO_DEVICE_ERROR "+e.Message);}finally{Release(sessions);Release(managerObject);Release(device);}
   }
  }finally{Release(devices);Release(root);}
  return result;
 }
}
sealed class AudioMonitor:IDisposable {
 readonly Thread worker;readonly AutoResetEvent signal=new AutoResetEvent(false);
 readonly Action<string> trace;volatile bool stopping,enabled;
 public event Action<float,bool,bool> Update;
 public AudioMonitor(Action<string> log){trace=log;worker=new Thread(Loop);worker.SetApartmentState(ApartmentState.MTA);worker.IsBackground=true;worker.Start();}
 public void SetEnabled(bool value){if(enabled==value)return;enabled=value;signal.Set();}
 void Loop(){
  var sources=new List<AudioSource>();DateTime nextFind=DateTime.MinValue,nextTrace=DateTime.MinValue;float maxPeak=0;
  try{while(!stopping){
   if(!enabled){Publish(0,false,false);signal.WaitOne(100);continue;}
   try{
    if(DateTime.UtcNow>=nextFind){foreach(var s in sources)s.Dispose();sources.Clear();sources=CoreAudio.FindYY(trace);nextFind=DateTime.UtcNow.AddSeconds(2);}
    float peak=0;bool active=false,sessionMeter=false;
    foreach(var s in sources){float p;if(s.Read(out p)){peak=Math.Max(peak,p);active=true;sessionMeter|=s.SessionMeter;}}
    if(float.IsNaN(peak)||float.IsInfinity(peak))peak=0;peak=Math.Max(0,Math.Min(1,peak));maxPeak=Math.Max(maxPeak,peak);
    Publish(peak,active,sessionMeter);
    if(trace!=null&&DateTime.UtcNow>=nextTrace){trace("AUDIO active="+active+" maxPeak="+maxPeak.ToString("F6",System.Globalization.CultureInfo.InvariantCulture));maxPeak=0;nextTrace=DateTime.UtcNow.AddSeconds(1);}
   }catch(Exception e){if(trace!=null)trace("AUDIO_ERROR "+e.Message);Publish(0,false,false);nextFind=DateTime.MinValue;}
   signal.WaitOne(40);
  }}finally{foreach(var s in sources)s.Dispose();signal.Dispose();}
 }
 void Publish(float peak,bool active,bool exact){var ev=Update;if(ev!=null)ev(peak,active,exact);}
 public void Dispose(){stopping=true;signal.Set();}
}
