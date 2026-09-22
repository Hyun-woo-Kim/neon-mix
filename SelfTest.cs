using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NeonMix;

internal static class SelfTest
{
    [StructLayout(LayoutKind.Sequential)] struct WaveFormat { public ushort Tag, Channels; public uint Samples, Bytes; public ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)] struct WaveHeader { public IntPtr Data; public uint Length, Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
    [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr wave, uint id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr wave, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr wave, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr wave);
    [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr wave, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr wave);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    public static void Probe()
    {
        var f = new WaveFormat { Tag = 1, Channels = 1, Samples = 44100, Bytes = 88200, Align = 2, Bits = 16 };
        if (waveOutOpen(out var w, uint.MaxValue, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0) return;
        // A silent PCM stream exercises real sessions without emitting sound.
        var data = Marshal.AllocHGlobal(88200 * 12); Marshal.Copy(new byte[88200 * 12], 0, data, 88200 * 12);
        var h = new WaveHeader { Data = data, Length = 88200 * 12 }; uint size = (uint)Marshal.SizeOf<WaveHeader>(); var ptr = Marshal.AllocHGlobal((int)size); Marshal.StructureToPtr(h, ptr, false);
        try { if (waveOutPrepareHeader(w, ptr, size) != 0) return; if (waveOutWrite(w, ptr, size) != 0) return; Thread.Sleep(11000); }
        finally { waveOutReset(w); waveOutUnprepareHeader(w, ptr, size); waveOutClose(w); Marshal.FreeHGlobal(ptr); Marshal.FreeHGlobal(data); }
    }
    public static void Run()
    {
        var lines = new List<string>(); Process? probe = null;
        try
        {
            using (var s = AudioSnapshot.Read())
            {
                lines.Add("PASS: Windows audio endpoint/session enumeration");
                lines.Add(JsonSerializer.Serialize(s.Channels.Select(c => new { c.Name, c.Key, Volume = c.Volume, Muted = c.Muted, Sessions = c.Sessions.Count })));
            }
            probe = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--probe") { UseShellExecute = false, CreateNoWindow = true });
            Thread.Sleep(1700);
            using (var s = AudioSnapshot.Read())
            {
                var own = s.Channels.FirstOrDefault(c => c.Sessions.Any(x => { x.GetProcessId(out uint pid); return pid == probe!.Id; }));
                if (own == null) throw new Exception("Silent test audio session was not discovered.");
                var session = own.Sessions.First(x => { x.GetProcessId(out uint pid); return pid == probe!.Id; });
                var volume = (ISimpleAudioVolume)session; Com.Check(volume.GetMasterVolume(out float oldVolume)); Com.Check(volume.GetMute(out bool oldMute)); var context = Guid.Empty;
                try
                {
                    Com.Check(volume.SetMasterVolume(.37f, ref context)); Com.Check(volume.GetMasterVolume(out float actual));
                    if (Math.Abs(actual - .37f) > .005f) throw new Exception("Volume roundtrip mismatch");
                    lines.Add("PASS: newly opened app detection and actual session volume 37% roundtrip");
                    Com.Check(volume.SetMute(true, ref context)); Com.Check(volume.GetMute(out bool muted)); if (!muted) throw new Exception("Mute failed");
                    Com.Check(volume.SetMute(false, ref context)); Com.Check(volume.GetMute(out muted)); if (muted) throw new Exception("Unmute failed");
                    lines.Add("PASS: actual session mute/unmute roundtrip");
                }
                finally { volume.SetMasterVolume(oldVolume, ref context); volume.SetMute(oldMute, ref context); }
            }
            bool registered = RegisterHotKey(IntPtr.Zero, 120, 0x4003, 0x4D);
            lines.Add(registered ? "PASS: Ctrl+Alt+M global hotkey registration" : "NOTICE: Ctrl+Alt+M is already occupied");
            if (registered) UnregisterHotKey(IntPtr.Zero, 120);
            lines.Add("PASS: test audio state restored");
        }
        catch (Exception e) { lines.Add("FAIL: " + e); Environment.ExitCode = 1; }
        finally
        {
            if (probe != null) { if (!probe.HasExited) probe.Kill(); probe.Dispose(); }
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), lines);
        }
    }
}
