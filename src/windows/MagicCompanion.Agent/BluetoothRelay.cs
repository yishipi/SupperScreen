using System.Net;
using System.Net.Sockets;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Radios;
using Windows.Networking.Sockets;

namespace MagicCompanion.Agent;

internal sealed class BluetoothRelay(int port) : IDisposable
{
    private static readonly Guid ServiceId = new("2d2b5b4a-f2e6-4d46-af54-d1ba12b60ef3");
    private readonly StreamSocketListener listener = new();
    private readonly ConcurrentDictionary<StreamSocket, byte> connections = new();
    private RfcommServiceProvider? provider;
    private Radio? radio;
    public bool IsAvailable => !disposed && (!advertising || radio?.State == RadioState.On);
    private bool advertising;
    private volatile bool disposed;

    public async Task<string> StartAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null || !adapter.IsClassicSupported)
            throw new InvalidOperationException("电脑没有可用的经典蓝牙适配器");
        radio = await adapter.GetRadioAsync();
        if (radio?.State != RadioState.On)
            throw new InvalidOperationException("请先打开电脑蓝牙");

        ObjectDisposedException.ThrowIf(disposed, this);
        provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(ServiceId));
        ObjectDisposedException.ThrowIf(disposed, this);
        listener.ConnectionReceived += ConnectionReceived;
        await listener.BindServiceNameAsync(
            provider.ServiceId.AsString(),
            SocketProtectionLevel.PlainSocket
        );
        ObjectDisposedException.ThrowIf(disposed, this);
        provider.StartAdvertising(listener, false);
        advertising = true;
        return string.Join(":", Enumerable.Range(0, 6).Select(index =>
            ((adapter.BluetoothAddress >> (8 * (5 - index))) & 0xff).ToString("X2")));
    }

    private async void ConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        using var socket = args.Socket;
        if (disposed || connections.Count >= 2 || !connections.TryAdd(socket, 0))
            return;
        try
        {
            if (disposed)
                return;
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            using var local = tcp.GetStream();
            using var input = socket.InputStream.AsStreamForRead();
            using var output = socket.OutputStream.AsStreamForWrite(bufferSize: 0);
            var incoming = input.CopyToAsync(local);
            var outgoing = local.CopyToAsync(output);
            await Task.WhenAny(incoming, outgoing);
            tcp.Close();
            socket.Dispose();
            await Task.WhenAll(incoming, outgoing);
        }
        catch (Exception ex) when (ex is IOException or SocketException or
            ObjectDisposedException or System.Runtime.InteropServices.COMException or OperationCanceledException) { }
        finally
        {
            connections.TryRemove(socket, out _);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (advertising)
        {
            try { provider?.StopAdvertising(); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        listener.ConnectionReceived -= ConnectionReceived;
        listener.Dispose();
        foreach (var socket in connections.Keys)
            socket.Dispose();
    }
}
