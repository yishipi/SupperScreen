using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MagicCompanion.Agent;

internal sealed class AdbBridge(string executable, int serverPort, string selectedSerial)
    : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    public event Action<string>? Status;

    public void Start() => worker = RunAsync();

    private async Task<string> Run(params string[] args)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-P");
        info.ArgumentList.Add(serverPort.ToString());
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("无法运行 ADB");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(5000);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var result = await output;
            var problem = await error;
            if (process.ExitCode != 0)
                throw new IOException(problem);
            return result;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    private async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(executable))
                {
                    Status?.Invoke("未找到 ADB，请在设置中选择 Android SDK 的 adb.exe");
                    return;
                }
                var rows = (await Run("devices"))
                    .Split('\n')
                    .Select(x =>
                        x.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    )
                    .Where(x =>
                        x.Length >= 2 && x[1] == "device"
                        && !x[0].Contains(':')
                        && !x[0].Contains("._adb-tls-", StringComparison.Ordinal)
                        && !x[0].StartsWith("emulator-", StringComparison.Ordinal)
                    )
                    .Select(x => x[0])
                    .ToArray();
                var serial =
                    selectedSerial.Length > 0 ? rows.FirstOrDefault(x => x == selectedSerial)
                    : rows.Length == 1 ? rows[0]
                    : null;
                if (serial == null)
                    Status?.Invoke(
                        rows.Length > 1
                            ? "发现多台设备，请在设置填写目标序列号"
                            : "等待 USB 手机及调试授权"
                    );
                else
                {
                    await Run("-s", serial, "reverse", "tcp:8765", "tcp:8765");
                    Status?.Invoke("USB 通道已准备 · " + serial);
                }
            }
            catch (Exception ex)
                when (ex
                        is IOException
                            or System.ComponentModel.Win32Exception
                            or OperationCanceledException
                )
            {
                if (!stop.IsCancellationRequested)
                    Status?.Invoke("USB 通道未就绪，请检查 ADB 和调试授权");
            }
            try
            {
                await Task.Delay(8000, stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose() => stop.Cancel();
}

// DNS-SD carries only discovery metadata; trust always comes from QR-pinned TLS and device signatures.
internal sealed class LanDiscovery : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly UdpClient socket;
    private readonly byte[] announcement;
    private readonly string hostname;
    private readonly IPEndPoint group = new(IPAddress.Parse("224.0.0.251"), 5353);

    public LanDiscovery(string hostId, IPAddress address, int port)
    {
        socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            socket.JoinMulticastGroup(group.Address, address);
            socket.MulticastLoopback = false;
            socket.Client.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastInterface,
                address.GetAddressBytes()
            );
        }
        catch
        {
            socket.Dispose();
            stop.Dispose();
            throw;
        }
        var suffix = hostId[^8..] + "-" + Convert.ToHexString(address.GetAddressBytes()).ToLowerInvariant();
        hostname = "mc-" + suffix;
        announcement = Packet(hostId, suffix, hostname, address, port);
        _ = AdvertiseAsync();
        _ = RespondAsync();
    }

    private async Task AdvertiseAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await socket.SendAsync(announcement, group, stop.Token);
                await Task.Delay(30000, stop.Token);
            }
        }
        catch (Exception ex)
            when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        { }
    }

    private async Task RespondAsync()
    {
        try
        {
            long lastReply = 0;
            while (!stop.IsCancellationRequested)
            {
                var data = await socket.ReceiveAsync(stop.Token);
                var query = Encoding.ASCII.GetString(data.Buffer);
                if (
                    data.Buffer.Length >= 12
                    && (data.Buffer[2] & 0x80) == 0
                    && (
                        query.Contains("_mcompanion", StringComparison.OrdinalIgnoreCase)
                        || query.Contains(hostname, StringComparison.OrdinalIgnoreCase)
                    )
                    && Stopwatch.GetElapsedTime(lastReply).TotalMilliseconds > 250
                )
                {
                    await socket.SendAsync(announcement, group, stop.Token);
                    lastReply = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (Exception ex)
            when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        { }
    }

    private static byte[] Packet(string hostId, string suffix, string hostname, IPAddress ip, int port)
    {
        var service = "_mcompanion._tcp.local";
        var instance = "Magic-" + suffix + "." + service;
        var host = hostname + ".local";
        using var output = new MemoryStream();
        void U16(int n)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)n);
            output.Write(b);
        }
        void U32(int n)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)n);
            output.Write(b);
        }
        byte[] Name(string name)
        {
            using var m = new MemoryStream();
            foreach (var part in name.Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(part);
                m.WriteByte((byte)bytes.Length);
                m.Write(bytes);
            }
            m.WriteByte(0);
            return m.ToArray();
        }
        void Record(string name, int type, byte[] bytes, bool unique)
        {
            output.Write(Name(name));
            U16(type);
            U16(unique ? 0x8001 : 1);
            U32(120);
            U16(bytes.Length);
            output.Write(bytes);
        }
        U16(0);
        U16(0x8400);
        U16(0);
        U16(4);
        U16(0);
        U16(0);
        Record(service, 12, Name(instance), false);
        using var srv = new MemoryStream();
        srv.Write(new byte[] { 0, 0, 0, 0, (byte)(port >> 8), (byte)port });
        srv.Write(Name(host));
        Record(instance, 33, srv.ToArray(), true);
        var txt = Encoding.UTF8.GetBytes("hostId=" + hostId);
        var text = new[] { (byte)txt.Length }
            .Concat(txt)
            .Concat(new byte[] { 3, 118, 61, 49 })
            .ToArray();
        Record(instance, 16, text, true);
        Record(host, 1, ip.GetAddressBytes(), true);
        return output.ToArray();
    }

    public void Dispose()
    {
        stop.Cancel();
        socket.Dispose();
    }
}
