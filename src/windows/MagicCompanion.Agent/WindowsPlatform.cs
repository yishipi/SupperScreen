using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace MagicCompanion.Agent;

internal sealed class WindowsPlatform : IAgentPlatform, IDisposable
{
    private ulong lastIdle,
        lastTotal;
    private readonly object sampleGate = new();
    private readonly AudioEndpoint audio = new();
    private readonly WindowsPerformance performance = new();
    private readonly object screenshotGate = new();
    public string HostName => Environment.MachineName;

    public int SaveScreenshots(string directory)
    {
        lock (screenshotGate)
            return ScreenSnapshots.Save(directory);
    }
    public IReadOnlyList<DiscoveredApp> DiscoverApps()
    {
        var apps = new Dictionary<string, DiscoveredApp>(StringComparer.OrdinalIgnoreCase);
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null)
            return [];
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                try
                {
                    string id =
                        item.ExtendedProperty("System.AppUserModel.ID") as string
                        ?? item.Path as string
                        ?? "";
                    string name = item.Name as string ?? id;
                    if (
                        string.IsNullOrWhiteSpace(id)
                        || id.Contains("MagicCompanion", StringComparison.OrdinalIgnoreCase)
                    )
                        continue;
                    var executable = Path.IsPathFullyQualified(id) && File.Exists(id) ? id : null;
                    var arguments = "";
                    if (
                        executable == null
                        && item.ExtendedProperty("System.Link.TargetParsingPath") is string target
                        && Path.IsPathFullyQualified(target)
                        && File.Exists(target)
                        && Path.GetExtension(target)
                            .Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        executable = target;
                        arguments = item.ExtendedProperty("System.Link.Arguments") as string ?? "";
                    }
                    apps[id] = new(
                        id,
                        name.Length > 120 ? name[..120] : name,
                        executable,
                        arguments,
                        TryIcon(id)
                    );
                }
                catch (Exception ex)
                    when (ex
                            is COMException
                                or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                or ArgumentException
                    ) { }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
        return apps
            .Values.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void Launch(AppEntry app)
    {
        if (app.ExecutablePath != null)
        {
            if (!File.Exists(app.ExecutablePath))
                throw new AgentException("INVALID_ARGUMENT", "应用已卸载或路径失效");
            Process.Start(
                new ProcessStartInfo(app.ExecutablePath)
                {
                    Arguments = app.Arguments,
                    UseShellExecute = true,
                }
            );
        }
        else
        {
            var info = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("shell:AppsFolder\\" + app.SourceId);
            Process.Start(info);
        }
    }

    public SystemSnapshot Sample()
    {
        double cpu;
        (TransferRate? DiskIo, NetworkRate? Network) rates;
        lock (sampleGate)
        {
            NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user);
            var total = kernel.Value + user.Value;
            cpu =
                total > lastTotal && lastTotal != 0
                    ? Math.Clamp(
                        100 * (1 - (double)(idle.Value - lastIdle) / (total - lastTotal)),
                        0,
                        100
                    )
                    : 0;
            lastIdle = idle.Value;
            lastTotal = total;
            rates = performance.Sample();
        }
        var memory = new NativeMethods.MemoryStatus
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatus>(),
        };
        NativeMethods.GlobalMemoryStatusEx(ref memory);
        var volumes = WindowsPerformance.Volumes();
        return new(
            cpu,
            memory.TotalPhysical - memory.AvailablePhysical,
            memory.TotalPhysical,
            volumes.Sum(disk => disk.TotalBytes - disk.UsedBytes),
            HostName,
            volumes,
            rates.DiskIo,
            rates.Network
        );
    }

    public void Dispose()
    {
        lock (sampleGate)
            performance.Dispose();
    }

    public AudioState GetAudio() => audio.Read();

    public AudioState SetAudio(float? level, bool? muted) => audio.Set(level, muted);

    public async Task<bool> MediaAsync(string command)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetCurrentSession();
        if (session == null)
            return false;
        return command switch
        {
            "playPause" => await session.TryTogglePlayPauseAsync(),
            "previous" => await session.TrySkipPreviousAsync(),
            "next" => await session.TrySkipNextAsync(),
            _ => throw new AgentException("INVALID_ARGUMENT", "未知媒体操作"),
        };
    }

    [
        ComImport,
        Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(Size size, uint flags, out nint bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path,
        nint bindContext,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory
    );

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    private static string? TryIcon(string id)
    {
        nint handle = 0;
        IShellItemImageFactory? factory = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName("shell:AppsFolder\\" + id, 0, ref iid, out factory);
            if (factory.GetImage(new Size(48, 48), 0x100, out handle) != 0)
                return null;
            using var bitmap = Image.FromHbitmap(handle);
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            return memory.Length <= 24 * 1024 ? Convert.ToBase64String(memory.ToArray()) : null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or ExternalException)
        {
            return null;
        }
        finally
        {
            if (handle != 0)
                DeleteObject(handle);
            if (factory != null)
                Marshal.FinalReleaseComObject(factory);
        }
    }
}

internal sealed class AudioEndpoint
{
    private readonly object gate = new();
    private AudioState previous = new(0, 0, false, false);

    public AudioState Read() => Set(null, null);

    public AudioState Set(float? level, bool? muted)
    {
        lock (gate)
        {
            object? enumerator = null;
            IMMDevice? device = null;
            IAudioEndpointVolume? volume = null;
            try
            {
                enumerator = Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!
                );
                ((IMMDeviceEnumerator)enumerator!).GetDefaultAudioEndpoint(0, 1, out device);
                var iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, 23, 0, out var obj);
                volume = (IAudioEndpointVolume)obj;
                var context = Guid.Empty;
                if (level.HasValue)
                    volume.SetMasterVolumeLevelScalar(level.Value, ref context);
                if (muted.HasValue)
                    volume.SetMute(muted.Value, ref context);
                volume.GetMasterVolumeLevelScalar(out var actual);
                volume.GetMute(out var isMuted);
                if (
                    !previous.Available
                    || Math.Abs(actual - previous.Level) > 0.0001f
                    || isMuted != previous.Muted
                )
                    previous = new(previous.Revision + 1, actual, isMuted, true);
            }
            catch (COMException)
            {
                previous = previous with { Available = false };
            }
            finally
            {
                if (volume != null)
                    Marshal.ReleaseComObject(volume);
                if (device != null)
                    Marshal.ReleaseComObject(device);
                if (enumerator != null)
                    Marshal.ReleaseComObject(enumerator);
            }
            if ((level.HasValue || muted.HasValue) && !previous.Available)
                throw new AgentException("UNSUPPORTED_CAPABILITY", "没有可用的默认输出音频设备");
            return previous;
        }
    }

    [
        ComImport,
        Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, uint mask, out nint devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [
        ComImport,
        Guid("D666063F-1587-4E43-81F1-B948E807363F"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IMMDevice
    {
        void Activate(
            ref Guid iid,
            uint context,
            nint activation,
            [MarshalAs(UnmanagedType.IUnknown)] out object result
        );
    }

    [
        ComImport,
        Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(nint notify);
        void UnregisterControlChangeNotify(nint notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float level, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float level);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
