using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MagicCompanion.Core;

public sealed class AgentServer : IAsyncDisposable
{
    private static readonly string[] Capabilities =
        ["todos", "metrics", "tasks", "terminal", "screenshot", "apps", "audio", "media"];

    private sealed class Client(WebSocket socket, string deviceId, string sessionId, bool bluetooth)
    {
        public readonly WebSocket Socket = socket;
        public readonly string DeviceId = deviceId;
        public readonly string SessionId = sessionId;
        public readonly bool Bluetooth = bluetooth;
        public bool Todos,
            Metrics,
            Audio;
        private int revoked;
        public readonly Channel<string> Out = Channel.CreateBounded<string>(
            new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            }
        );

        public bool Event(string type, object payload) =>
            (!Bluetooth || type is "todo.snapshot" or "system.metrics" or "permission.changed"
                or "device.revoked" or "task.updated" or "control.paused"
                or "app.catalog.changed" or "audio.state")
            &&
            Out.Writer.TryWrite(
                Wire.Serialize(
                    new
                    {
                        protocolVersion = 1,
                        messageType = "event",
                        eventType = type,
                        payload,
                    }
                )
            );

        public void Revoke()
        {
            if (Interlocked.Exchange(ref revoked, 1) != 0)
                return;
            Event("device.revoked", new { });
            Out.Writer.TryComplete();
        }
    }

    private readonly AgentStore store;
    private readonly IAgentPlatform platform;
    private readonly PairingCoordinator pairing;
    private readonly TaskRunner tasks;
    private readonly ITerminalPlatform? terminal;
    private readonly string hostId;
    private readonly ConcurrentDictionary<string, Client> clients = new();
    private readonly ConcurrentDictionary<string, string> sessions = new();
    private readonly CancellationTokenSource stop = new();
    private WebApplication? app;
    private Task? publisher;
    private Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions? bluetoothEndpoint;
    public int BluetoothPort => bluetoothEndpoint?.IPEndPoint?.Port ?? 0;
    public string? BluetoothAddress { get; set; }
    public bool ControlPaused { get; private set; }
    public event Action<string>? Diagnostic;
    public int LinkCount => clients.Count;

    public AgentServer(
        AgentStore store,
        IAgentPlatform platform,
        PairingCoordinator pairing,
        string hostId,
        ITerminalPlatform? terminal = null
    )
    {
        this.store = store;
        this.platform = platform;
        this.pairing = pairing;
        this.hostId = hostId;
        this.terminal = terminal;
        tasks = new(store);
        store.TodosChanged += OnTodos;
        store.ConfigurationChanged += OnConfiguration;
        tasks.Changed += OnTask;
    }

    public async Task StartAsync(
        X509Certificate2 certificate,
        int usbPort,
        int lanPort,
        IEnumerable<IPAddress> addresses,
        CancellationToken cancellation = default,
        bool enableBluetooth = false
    )
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (Environment.GetEnvironmentVariable("MAGIC_DIAGNOSTIC_CONSOLE") == "1")
            builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Debug);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = Wire.MaxMessageBytes;
            server.Listen(IPAddress.Loopback, usbPort, endpoint => endpoint.UseHttps(certificate));
            if (enableBluetooth)
                server.Listen(IPAddress.Loopback, 0, endpoint =>
                {
                    endpoint.UseHttps(certificate);
                    bluetoothEndpoint = endpoint;
                });
            foreach (var address in addresses.Distinct())
                server.Listen(address, lanPort, endpoint => endpoint.UseHttps(certificate));
        });
        app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5) });
        app.MapGet(
            "/health",
            () => Results.Json(new { service = "MagicCompanion", protocolVersion = 1 })
        );
        app.Map("/ws", HandleControlAsync);
        await app.StartAsync(cancellation);
        publisher = PublishAsync(stop.Token);
    }

    private async Task<(TrustedDevice Device, string Session)> AuthenticateAsync(
        WebSocket socket,
        bool allowPair,
        CancellationToken cancellation
    )
    {
        var linkId = Wire.NewId();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await SendAsync(
            socket,
            Wire.Serialize(
                new
                {
                    type = "challenge",
                    protocolVersion = 1,
                    hostId,
                    linkId,
                    nonce,
                }
            ),
            cancellation
        );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(125));
        using var auth = JsonDocument.Parse(await ReceiveAsync(socket, 8192, timeout.Token));
        if (auth.RootElement.RequiredString("type", 32) != "authenticate")
            throw new AgentException("AUTH_FAILED", "需要设备身份认证");
        var device = await pairing.AuthenticateAsync(
            auth.RootElement,
            linkId,
            nonce,
            allowPair,
            timeout.Token
        );
        var session = sessions.GetOrAdd(device.DeviceId, _ => Wire.NewId());
        await SendAsync(
            socket,
            Wire.Serialize(
                new
                {
                    type = "authenticated",
                    protocolVersion = 1,
                    sessionId = session,
                    deviceId = device.DeviceId,
                    permissions = device.Permissions.Intersect(PermissionSet.All),
                    hostId,
                    hostName = platform.HostName,
                }
            ),
            cancellation
        );
        return (device, session);
    }

    private async Task HandleControlAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        Client? client = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            stop.Token,
            context.RequestAborted
        );
        Task? sender = null;
        try
        {
            var auth = await AuthenticateAsync(socket, true, linked.Token);
            client = new(socket, auth.Device.DeviceId, auth.Session, context.Connection.LocalPort == BluetoothPort);
            var key = Wire.NewId();
            clients[key] = client;
            sender = SendQueueAsync(client, linked.Token);
            _ = sender.ContinueWith(
                completed =>
                {
                    if (completed.IsCompletedSuccessfully)
                        linked.CancelAfter(TimeSpan.FromSeconds(2));
                    else
                        linked.Cancel();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
            try
            {
                while (!linked.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var text = await ReceiveAsync(socket, Wire.MaxMessageBytes, linked.Token);
                    using var json = JsonDocument.Parse(text);
                    await ProcessAsync(client, json.RootElement, linked.Token);
                }
            }
            finally
            {
                clients.TryRemove(key, out _);
                if (!clients.Values.Any(c => c.DeviceId == client.DeviceId))
                    terminal?.Close(client.DeviceId);
            }
        }
        catch (AgentException ex)
        {
            if (client == null && socket.State == WebSocketState.Open)
                await TrySendErrorAsync(socket, ex.Code, ex.Message, linked.Token);
        }
        catch (Exception ex)
            when (ex
                    is WebSocketException
                        or OperationCanceledException
                        or JsonException
                        or IOException
            )
        {
            Diagnostic?.Invoke("连接结束：" + ex.GetType().Name);
        }
        finally
        {
            linked.Cancel();
            client?.Out.Writer.TryComplete();
            if (sender != null)
                try
                {
                    await sender;
                }
                catch (Exception ex)
                    when (ex is WebSocketException or OperationCanceledException or IOException) { }
            if (socket.State == WebSocketState.Open)
                try
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "连接已结束",
                        CancellationToken.None
                    );
                }
                catch (WebSocketException) { }
        }
    }

    private TrustedDevice CurrentDevice(Client client) =>
        store.Device(client.DeviceId) is { Revoked: false } device
            ? device
            : throw new AgentException("DEVICE_REVOKED", "电脑已解除配对");

    private async Task ProcessAsync(
        Client client,
        JsonElement message,
        CancellationToken cancellation
    )
    {
        string? requestId = null;
        try
        {
            var device = CurrentDevice(client);
            if (
                message.Integer("protocolVersion", 1, 1) != 1
                || message.RequiredString("deviceId", 128) != device.DeviceId
                || message.RequiredString("sessionId", 128) != client.SessionId
            )
                throw new AgentException("AUTH_FAILED", "消息会话不匹配");
            var messageType = message.RequiredString("messageType", 32);
            if (messageType != "request")
                throw new AgentException("INVALID_ARGUMENT", "消息类型无效");
            requestId = message.RequiredString("requestId", 64);
            if (!Guid.TryParse(requestId, out _))
                throw new AgentException("INVALID_ARGUMENT", "请求 ID 格式无效");
            var action = message.RequiredString("action", 64);
            var payload = message.GetProperty("payload");
            if (client.Bluetooth && action is not (
                "session.hello" or "session.visibility" or "session.ping" or "session.unpair"
                or "todo.snapshot.get" or "todo.subscribe" or "todo.change"
                or "system.snapshot.get" or "system.metrics.subscribe"
                or "terminal.open" or "terminal.read" or "terminal.write" or "terminal.resize" or "terminal.close"
                or "task.templates" or "task.run" or "task.status.get" or "task.cancel" or "request.status"
                or "screen.capture"
                or "app.catalog.get" or "app.icon.get" or "app.launch"
                or "audio.state.get" or "audio.state.subscribe" or "audio.volume.set" or "audio.mute.set" or "media.command"
            ))
                throw new AgentException("UNSUPPORTED_ACTION", "当前蓝牙连接不支持此操作");
            if (action == "todo.change")
            {
                PermissionSet.Demand(device, "todos.read");
                PermissionSet.Demand(device, "todos.write");
                if (payload.RequiredString("operation", 24) != "setCompleted" || !payload.Boolean("completed"))
                    throw new AgentException("UNSUPPORTED_ACTION", "手机仅支持完成待办");
            }
            if (action == "session.unpair")
            {
                // Revocation sends device.revoked and closes all of this phone's links.
                pairing.Revoke(device.DeviceId);
                return;
            }
            if (action == "screen.capture")
            {
                PermissionSet.Demand(device, "screen.capture");
                if (ControlPaused)
                    throw new AgentException("PERMISSION_DENIED", "电脑已暂停控制");
            }
            object? result;
            var sideEffect =
                action is "app.launch" or "task.run" or "media.command" or "todo.change" or "screen.capture";
            if (sideEffect)
            {
                var previous = store.BeginRequest(
                    device.DeviceId,
                    requestId,
                    Wire.Hash(action + "\n" + payload.GetRawText())
                );
                if (!previous.Execute)
                {
                    if (previous.Result != null)
                    {
                        await client.Out.Writer.WriteAsync(previous.Result, cancellation);
                        return;
                    }
                    throw new AgentException(
                        "UNKNOWN_OUTCOME",
                        "原请求仍在执行或结果未知，请查询状态"
                    );
                }
            }
            try
            {
                result = await DispatchAsync(client, device, action, payload);
                var response = Wire.Serialize(
                    new
                    {
                        protocolVersion = 1,
                        messageType = "response",
                        requestId,
                        status = "ok",
                        result,
                    }
                );
                if (sideEffect)
                    store.CompleteRequest(device.DeviceId, requestId, "succeeded", response);
                await client.Out.Writer.WriteAsync(response, cancellation);
            }
            catch (Exception ex)
                when (ex
                        is AgentException
                            or ArgumentException
                            or InvalidOperationException
                            or System.ComponentModel.Win32Exception
                            or IOException
                )
            {
                var error =
                    ex as AgentException
                    ?? new AgentException("INTERNAL_ERROR", "操作未完成，请查看电脑诊断");
                var response = ErrorResponse(requestId, error.Code, error.Message);
                if (sideEffect)
                    store.CompleteRequest(device.DeviceId, requestId, "failed", response);
                await client.Out.Writer.WriteAsync(response, cancellation);
            }
        }
        catch (Exception ex)
            when (ex
                    is AgentException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or FormatException
            )
        {
            var error =
                ex as AgentException ?? new AgentException("INVALID_ARGUMENT", "消息字段格式无效");
            await client.Out.Writer.WriteAsync(
                ErrorResponse(requestId, error.Code, error.Message),
                cancellation
            );
            if (error.Code == "DEVICE_REVOKED")
                client.Out.Writer.TryComplete();
        }
    }

    private async Task<object?> DispatchAsync(
        Client client,
        TrustedDevice device,
        string action,
        JsonElement p
    )
    {
        void Demand(string permission, bool control = false)
        {
            PermissionSet.Demand(device, permission);
            if (control && ControlPaused)
                throw new AgentException("PERMISSION_DENIED", "电脑已暂停控制");
        }
        ITerminalPlatform Terminal() => terminal ?? throw new AgentException("UNSUPPORTED_CAPABILITY", "此电脑未提供终端");
        AppEntry App(string id) =>
            store.Apps().FirstOrDefault(a => a.AppId == id)
            ?? throw new AgentException("INVALID_ARGUMENT", "应用不在允许列表");
        switch (action)
        {
            case "session.hello":
                return new
                {
                    bluetoothAddress = BluetoothAddress,
                    capabilities = Capabilities,
                };
            case "session.visibility":
                if (!p.Boolean("foreground"))
                {
                    client.Metrics = client.Audio = false;
                }
                return new { acknowledged = true };
            case "session.ping":
                return new { alive = true };
            case "screen.capture":
                if (store.GetSetting("screenshotDirectory") is not { Length: > 0 } directory)
                    throw new AgentException("INVALID_ARGUMENT", "请先在电脑「截屏」页设置保存目录");
                return new { count = platform.SaveScreenshots(directory) };
            case "request.status":
                return store.RequestStatus(device.DeviceId, p.RequiredString("requestId", 64));
            case "todo.snapshot.get":
                Demand("todos.read");
                return store.Todos();
            case "todo.subscribe":
                Demand("todos.read");
                client.Todos = true;
                return new { subscriptionId = "todos", snapshot = store.Todos() };
            case "todo.change":
                return store.ChangeTodo("setCompleted", p.RequiredString("id", 64), completed: true);
            case "system.snapshot.get":
                Demand("system.read");
                return platform.Sample();
            case "system.metrics.subscribe":
                Demand("system.read");
                client.Metrics = true;
                return platform.Sample();
            case "app.catalog.get":
                Demand("app.launch");
                return new
                {
                    items = store
                        .Apps()
                        .Select(a => new
                        {
                            a.AppId,
                            a.DisplayName,
                            iconId = a.AppId,
                            launchAvailable = a.ExecutablePath == null
                                || File.Exists(a.ExecutablePath),
                        }),
                };
            case "app.icon.get":
                Demand("app.launch");
                return new
                {
                    mime = "image/png",
                    dataBase64 = App(p.RequiredString("iconId", 128)).IconBase64,
                };
            case "app.launch":
                Demand("app.launch", true);
                var appEntry = App(p.RequiredString("appId", 128));
                platform.Launch(appEntry);
                return new { launched = true };
            case "audio.state.get":
                Demand("audio.control");
                return platform.GetAudio();
            case "audio.state.subscribe":
                Demand("audio.control");
                client.Audio = true;
                return platform.GetAudio();
            case "audio.volume.set":
                Demand("audio.control", true);
                if (
                    !p.TryGetProperty("level", out var number)
                    || !number.TryGetSingle(out var level)
                    || !float.IsFinite(level)
                    || level < 0
                    || level > 1
                )
                    throw new AgentException("INVALID_ARGUMENT", "音量范围为 0～1");
                return platform.SetAudio(level, null);
            case "audio.mute.set":
                Demand("audio.control", true);
                return platform.SetAudio(null, p.Boolean("muted"));
            case "media.command":
                Demand("audio.control", true);
                if (!await platform.MediaAsync(p.RequiredString("command", 32)))
                    throw new AgentException("UNSUPPORTED_CAPABILITY", "没有可控制的媒体会话");
                return new { executed = true };
            case "terminal.open":
                Demand("terminal.control", true);
                if (p.TryGetProperty("profileId", out var profile) && profile.ValueKind != JsonValueKind.Null)
                    throw new AgentException("UNSUPPORTED_ACTION", "请更新手机 App，并从顶部打开 PowerShell");
                return Terminal().Open(device.DeviceId, p.Integer("columns", 20, 300), p.Integer("rows", 5, 120));
            case "terminal.read":
                Demand("terminal.control");
                if (!p.GetProperty("offset").TryGetInt64(out var offset) || offset < 0)
                    throw new AgentException("INVALID_ARGUMENT", "终端偏移无效");
                return Terminal().Read(device.DeviceId, p.RequiredString("id", 64), offset);
            case "terminal.write":
                Demand("terminal.control", true);
                if (!p.GetProperty("sequence").TryGetInt64(out var sequence) || sequence < 1)
                    throw new AgentException("INVALID_ARGUMENT", "终端输入序号无效");
                Terminal().Write(device.DeviceId, p.RequiredString("id", 64), sequence, p.RequiredString("text", 8192));
                return new { written = true };
            case "terminal.resize":
                Demand("terminal.control");
                Terminal().Resize(device.DeviceId, p.RequiredString("id", 64), p.Integer("columns", 20, 300), p.Integer("rows", 5, 120));
                return new { resized = true };
            case "terminal.close":
                Demand("terminal.control");
                Terminal().Close(device.DeviceId);
                return new { closed = true };
            case "task.templates":
                Demand("tasks.read");
                return new
                {
                    items = tasks.Templates.Select(t => new
                    {
                        t.Id,
                        t.Name,
                        parameters = t.ParameterPatterns?.Keys,
                    }),
                    // Older Android builds expect this field before loading script templates.
                    hotkeys = Array.Empty<object>(),
                };
            case "task.run":
                Demand("tasks.run", true);
                return tasks.Start(
                    device.DeviceId,
                    p.RequiredString("templateId", 128),
                    p.TryGetProperty("params", out var parameters) ? parameters : default
                );
            case "task.status.get":
                Demand("tasks.read");
                return tasks.Get(device.DeviceId, p.RequiredString("jobId", 128));
            case "task.cancel":
                Demand("tasks.run", true);
                return tasks.Cancel(device.DeviceId, p.RequiredString("jobId", 128));
            default:
                throw new AgentException("UNSUPPORTED_ACTION", "不支持此操作");
        }
    }

    private void OnTodos(TodoSnapshot snapshot)
    {
        foreach (
            var c in clients.Values.Where(c =>
                c.Todos
                && store.Device(c.DeviceId) is { Revoked: false } d
                && d.Permissions.Contains("todos.read")
            )
        )
            c.Event("todo.snapshot", snapshot);
    }

    private void OnTask(string owner, TaskJob job)
    {
        foreach (
            var c in clients.Values.Where(c =>
                c.DeviceId == owner
                && store.Device(owner) is { Revoked: false } d
                && d.Permissions.Contains("tasks.read")
            )
        )
            c.Event("task.updated", job with { Lines = job.Lines.TakeLast(20).ToArray() });
    }

    private void OnConfiguration()
    {
        foreach (var c in clients.Values)
        {
            var device = store.Device(c.DeviceId);
            if (device == null || device.Revoked)
            {
                c.Revoke();
                terminal?.Close(c.DeviceId);
                continue;
            }
            if (!device.Permissions.Contains("terminal.control"))
                terminal?.Close(c.DeviceId);
            if (!device.Permissions.Contains("todos.read"))
                c.Todos = false;
            c.Event("permission.changed", new { permissions = device.Permissions.Intersect(PermissionSet.All) });
            c.Event("app.catalog.changed", new { });
        }
    }

    private async Task PublishAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation))
            {
                if (tick++ % 4 != 0)
                    continue;
                SystemSnapshot? metrics = null;
                AudioState? audio = null;
                try
                {
                    if (clients.Values.Any(c => c.Metrics))
                        metrics = platform.Sample();
                    if (clients.Values.Any(c => c.Audio))
                        audio = platform.GetAudio();
                }
                catch (Exception ex)
                    when (ex
                            is IOException
                                or UnauthorizedAccessException
                                or System.Runtime.InteropServices.COMException
                                or System.ComponentModel.Win32Exception
                    )
                {
                    Diagnostic?.Invoke("状态采样暂不可用：" + ex.GetType().Name);
                }
                foreach (var c in clients.Values)
                {
                    var device = store.Device(c.DeviceId);
                    if (device == null || device.Revoked)
                    {
                        c.Revoke();
                        continue;
                    }
                    if (c.Metrics && metrics != null && device.Permissions.Contains("system.read"))
                        c.Event("system.metrics", metrics);
                    if (c.Audio && audio != null && device.Permissions.Contains("audio.control"))
                        c.Event("audio.state", audio);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void SetPaused(bool paused)
    {
        ControlPaused = paused;
        if (paused && store.Device() is { } device)
        {
            terminal?.Close(device.DeviceId);
        }
        foreach (var c in clients.Values)
            c.Event("control.paused", new { paused });
    }

    private static string ErrorResponse(string? id, string code, string message) =>
        Wire.Serialize(
            new
            {
                protocolVersion = 1,
                messageType = "response",
                requestId = id,
                status = "error",
                error = new
                {
                    code,
                    message,
                    retryable = false,
                },
            }
        );

    private static Task SendAsync(WebSocket socket, string text, CancellationToken cancellation) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text,
            true,
            cancellation
        );

    private static async Task TrySendErrorAsync(
        WebSocket socket,
        string code,
        string message,
        CancellationToken cancellation
    )
    {
        try
        {
            await SendAsync(
                socket,
                Wire.Serialize(
                    new
                    {
                        type = "auth.error",
                        code,
                        message,
                    }
                ),
                cancellation
            );
        }
        catch (WebSocketException) { }
    }

    private static async Task SendQueueAsync(Client client, CancellationToken cancellation)
    {
        await foreach (var text in client.Out.Reader.ReadAllAsync(cancellation))
            await SendAsync(client.Socket, text, cancellation);
        if (client.Socket.State == WebSocketState.Open)
            await client.Socket.CloseOutputAsync(
                WebSocketCloseStatus.PolicyViolation,
                "会话已结束",
                cancellation
            );
    }

    public static async Task<string> ReceiveAsync(
        WebSocket socket,
        int maximum,
        CancellationToken cancellation
    )
    {
        var buffer = new byte[Math.Min(8192, maximum)];
        using var memory = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellation);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new IOException("WebSocket 已关闭");
            if (
                result.MessageType != WebSocketMessageType.Text
                || memory.Length + result.Count > maximum
            )
                throw new AgentException("INVALID_ARGUMENT", "消息类型或大小无效");
            memory.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return new UTF8Encoding(false, true).GetString(memory.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        store.TodosChanged -= OnTodos;
        store.ConfigurationChanged -= OnConfiguration;
        foreach (var c in clients.Values)
        {
            c.Out.Writer.TryComplete();
            c.Socket.Abort();
        }
        if (publisher != null)
            await publisher;
        await tasks.DisposeAsync();
        terminal?.Dispose();
        if (app != null)
            await app.DisposeAsync();
        stop.Dispose();
    }
}
