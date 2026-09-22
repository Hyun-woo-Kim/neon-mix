using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NeonMix;

internal static class Com
{
    public static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
    public static void Release(object? obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
}

internal sealed class AudioChannel : IDisposable
{
    public string Key = "", Name = "", Detail = "";
    public readonly List<IAudioSessionControl2> Sessions = new();
    public IAudioEndpointVolume? Endpoint;
    public bool Active;
    public float Peak
    {
        get
        {
            float peak = 0;
            if (Endpoint is IAudioMeterInformation master && master.GetPeakValue(out float p) >= 0) peak = p;
            foreach (var session in Sessions) if (session is IAudioMeterInformation meter && meter.GetPeakValue(out float v) >= 0) peak = Math.Max(peak, v);
            return peak;
        }
    }
    public float Volume => Endpoint != null ? ReadMaster() : Sessions.Count == 0 ? 0 : Sessions.Average(s => { Com.Check(((ISimpleAudioVolume)s).GetMasterVolume(out float v)); return v; });
    float ReadMaster() { Com.Check(Endpoint!.GetMasterVolumeLevelScalar(out float v)); return v; }
    public bool Muted
    {
        get
        {
            if (Endpoint != null) { Com.Check(Endpoint.GetMute(out bool b)); return b; }
            return Sessions.Count > 0 && Sessions.All(s => { Com.Check(((ISimpleAudioVolume)s).GetMute(out bool b)); return b; });
        }
    }
    public void SetVolume(float value)
    {
        value = Math.Clamp(value, 0, 1); var context = Guid.Empty;
        if (Endpoint != null) Com.Check(Endpoint.SetMasterVolumeLevelScalar(value, ref context));
        foreach (var s in Sessions) Com.Check(((ISimpleAudioVolume)s).SetMasterVolume(value, ref context));
    }
    public void SetMute(bool mute)
    {
        var context = Guid.Empty;
        if (Endpoint != null) Com.Check(Endpoint.SetMute(mute, ref context));
        foreach (var s in Sessions) Com.Check(((ISimpleAudioVolume)s).SetMute(mute, ref context));
    }
    public void Dispose() { foreach (var s in Sessions) Com.Release(s); Sessions.Clear(); Com.Release(Endpoint); Endpoint = null; }
}

internal sealed class AudioSnapshot : IDisposable
{
    public readonly List<AudioChannel> Channels = new();
    public readonly List<string> Warnings = new();
    public void Dispose() { foreach (var c in Channels) c.Dispose(); Channels.Clear(); }
    public static AudioSnapshot Read()
    {
        var result = new AudioSnapshot();
        IMMDeviceEnumerator? enumerator = null; IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var main) >= 0)
            {
                try
                {
                    var id = typeof(IAudioEndpointVolume).GUID;
                    Com.Check(main.Activate(ref id, 23, IntPtr.Zero, out object endpoint));
                    result.Channels.Add(new AudioChannel { Key = "master", Name = "전체 출력", Detail = "기본 출력 장치", Endpoint = (IAudioEndpointVolume)endpoint, Active = true });
                }
                finally { Com.Release(main); }
            }
            Com.Check(enumerator.EnumAudioEndpoints(0, 1, out devices));
            Com.Check(devices.GetCount(out uint count));
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null; object? managerObject = null; IAudioSessionEnumerator? sessions = null;
                try
                {
                    Com.Check(devices.Item(i, out device));
                    var id = typeof(IAudioSessionManager2).GUID;
                    Com.Check(device.Activate(ref id, 23, IntPtr.Zero, out managerObject));
                    Com.Check(((IAudioSessionManager2)managerObject).GetSessionEnumerator(out sessions));
                    Com.Check(sessions.GetCount(out int length));
                    for (int j = 0; j < length; j++)
                    {
                        IAudioSessionControl2? session = null;
                        try
                        {
                            Com.Check(sessions.GetSession(j, out session));
                            Com.Check(session.GetState(out int state));
                            if (state == 2) continue;
                            Com.Check(session.GetProcessId(out uint pid));
                            string name = "시스템 사운드", key = "system";
                            if (pid != 0)
                            {
                                using var process = Process.GetProcessById((int)pid);
                                name = process.ProcessName;
                                key = "app:" + name.ToLowerInvariant();
                            }
                            var channel = result.Channels.FirstOrDefault(c => c.Key == key);
                            if (channel == null) { channel = new AudioChannel { Key = key, Name = name }; result.Channels.Add(channel); }
                            channel.Sessions.Add(session); session = null;
                            channel.Active |= state == 1;
                            channel.Detail = channel.Active ? "오디오 연결됨" : "재생 대기";
                            if (channel.Sessions.Count > 1) channel.Detail += $" · {channel.Sessions.Count}개 세션";
                        }
                        catch (Exception e) when (e is COMException or ArgumentException or InvalidOperationException) { }
                        finally { Com.Release(session); }
                    }
                }
                catch (COMException e) { result.Warnings.Add(e.Message); }
                finally { Com.Release(sessions); Com.Release(managerObject); Com.Release(device); }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { Com.Release(devices); Com.Release(enumerator); }
    }
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator { }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
}
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}
[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}
[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
}
[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
    [PreserveSig] int GetGroupingParam(out Guid grouping);
    [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr callback);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr callback);
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}
[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float volume, ref Guid context);
    [PreserveSig] int GetMasterVolume(out float volume);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
}
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
    [PreserveSig] int GetMasterVolumeLevel(out float level);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
