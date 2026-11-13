using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MagicCompanion.Core;
using Xunit;

namespace MagicCompanion.Tests;

public sealed class ServerTests
{
    [Fact]
    public async Task RealTlsSocketsShareIdentityDedupeAndReadOnlyTodoSubscription()
    {
        using var store = new AgentStore(":memory:");
        using var phone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var host = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificateRequest = new CertificateRequest(
            "CN=localhost",
            host,
            HashAlgorithmName.SHA256
        );
        using var original = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1)
        );
        using var certificate = X509CertificateLoader.LoadPkcs12(
            original.Export(X509ContentType.Pfx),
            null,
            X509KeyStorageFlags.UserKeySet
        );
        var device = new TrustedDevice(
            Wire.Fingerprint(phone.ExportSubjectPublicKeyInfo()),
            "Test",
            Convert.ToBase64String(phone.ExportSubjectPublicKeyInfo()),
            PermissionSet.All,
            false
        );
        store.SaveDevice(device);
        var allowed = new AppEntry("test-app", "Fixture", "fixture", null, "", "aWNvbg==");
        store.SaveApp(allowed);
        var platform = new FakePlatform();
        var terminal = new FakeTerminal();
        await using var server = new AgentServer(store, platform, new(store, "host"), "host", terminal);
        var port = FreePort();
        await server.StartAsync(certificate, port, port + 1, [], enableBluetooth: true);
        using var first = await Connect(port, phone, certificate);
        using var second = await Connect(port, phone, certificate);
        Assert.Equal(first.Session, second.Session);
        var subscribed = await first.Request("todo.subscribe");
        Assert.Equal("ok", subscribed.GetProperty("status").GetString());
        store.ChangeTodo("add", title: "电脑配置待办");
        var pushed = await first.Receive();
        Assert.Equal("todo.snapshot", pushed.GetProperty("eventType").GetString());
        Assert.Equal(
            "电脑配置待办",
            pushed.GetProperty("payload").GetProperty("items")[0].GetProperty("title").GetString()
        );
        var forbidden = await first.Request("todo.add", new { title = "phone write" });
        Assert.Equal(
            "UNSUPPORTED_ACTION",
            forbidden.GetProperty("error").GetProperty("code").GetString()
        );
        Assert.Single(store.Todos().Items);
        var id = Wire.NewId();
        var payload = new { appId = allowed.AppId };
        var a = await first.Request("app.launch", payload, id);
        var b = await second.Request("app.launch", payload, id);
        Assert.Equal("ok", a.GetProperty("status").GetString());
        Assert.Equal(a.GetRawText(), b.GetRawText());
        Assert.Equal(1, platform.Launches);
        Assert.NotEqual(0, server.BluetoothPort);
        using var bluetooth = await Connect(server.BluetoothPort, phone, certificate);
        var capabilities = (await bluetooth.Request("session.hello")).GetProperty("result")
            .GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("apps", capabilities);
        Assert.Contains("audio", capabilities);
        Assert.Contains("media", capabilities);
        Assert.DoesNotContain("hotkeys", capabilities);
        var usbCapabilities = (await first.Request("session.hello")).GetProperty("result")
            .GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.DoesNotContain("hotkeys", usbCapabilities);
        store.SaveApp(allowed with { DisplayName = "Renamed" });
        await bluetooth.ReceiveEvent("app.catalog.changed");
        var catalog = (await bluetooth.Request("app.catalog.get")).GetProperty("result").GetProperty("items");
        Assert.Equal("Renamed", Assert.Single(catalog.EnumerateArray()).GetProperty("displayName").GetString());
        var icon = await bluetooth.Request("app.icon.get", new { iconId = allowed.AppId });
        Assert.Equal(allowed.IconBase64, icon.GetProperty("result").GetProperty("dataBase64").GetString());
        var launchReplay = await bluetooth.Request("app.launch", payload, id);
        Assert.Equal(a.GetRawText(), launchReplay.GetRawText());
        Assert.Equal(1, platform.Launches);
        Assert.Equal("ok", (await bluetooth.Request("app.launch", payload)).GetProperty("status").GetString());
        Assert.Equal(2, platform.Launches);
        var audio = await bluetooth.Request("audio.state.subscribe");
        Assert.True(audio.GetProperty("result").GetProperty("available").GetBoolean());
        var volume = await bluetooth.Request("audio.volume.set", new { level = .25f });
        Assert.Equal(.25f, volume.GetProperty("result").GetProperty("level").GetSingle());
        var mute = await bluetooth.Request("audio.mute.set", new { muted = true });
        Assert.True(mute.GetProperty("result").GetProperty("muted").GetBoolean());
        await bluetooth.ReceiveEvent("audio.state");
        Assert.Equal("ok", (await bluetooth.Request("media.command", new { command = "playPause" })).GetProperty("status").GetString());
        Assert.Equal("ok", (await bluetooth.Request("todo.subscribe")).GetProperty("status").GetString());
        var metrics = (await bluetooth.Request("system.metrics.subscribe")).GetProperty("result");
        Assert.Equal("C:", metrics.GetProperty("disks")[0].GetProperty("name").GetString());
        Assert.Equal(1024, metrics.GetProperty("diskIo").GetProperty("readBytesPerSecond").GetInt64());
        Assert.Equal(2048, metrics.GetProperty("network").GetProperty("sentBytesPerSecond").GetInt64());
        var opened = await bluetooth.Request("terminal.open", new { columns = 80, rows = 24 });
        Assert.Equal("ok", opened.GetProperty("status").GetString());
        Assert.Equal("PowerShell", opened.GetProperty("result").GetProperty("shell").GetString());
        var legacyOpen = await bluetooth.Request("terminal.open", new { columns = 80, rows = 24, profileId = Wire.NewId() });
        Assert.Equal("UNSUPPORTED_ACTION", legacyOpen.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("UNSUPPORTED_ACTION",
            (await first.Request("terminal.profiles")).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("ok", (await bluetooth.Request("terminal.write", new { id = "terminal", sequence = 1, text = "echo test\r" })).GetProperty("status").GetString());
        Assert.Equal(1, terminal.Writes);
        var template = new TaskTemplate("check", "Fixture script", "unused", [], "unused");
        store.SetSetting("taskTemplates", Wire.Serialize(new[] { template }));
        var templates = (await bluetooth.Request("task.templates")).GetProperty("result");
        Assert.Equal(template.Id, Assert.Single(templates.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        Assert.Empty(templates.GetProperty("hotkeys").EnumerateArray());
        var missingDirectory = await bluetooth.Request("screen.capture");
        Assert.Equal("INVALID_ARGUMENT", missingDirectory.GetProperty("error").GetProperty("code").GetString());
        Assert.Null(platform.ScreenshotDirectory);
        var screenshotDirectory = Path.GetTempPath();
        store.SetSetting("screenshotDirectory", screenshotDirectory);
        var screenshotId = Wire.NewId();
        var captured = await bluetooth.Request("screen.capture", id: screenshotId);
        var captureReplay = await first.Request("screen.capture", id: screenshotId);
        Assert.Equal(2, captured.GetProperty("result").GetProperty("count").GetInt32());
        Assert.Equal(captured.GetRawText(), captureReplay.GetRawText());
        Assert.Equal(1, platform.Captures);
        Assert.Equal(screenshotDirectory, platform.ScreenshotDirectory);
        server.SetPaused(true);
        var pausedCapture = await bluetooth.Request("screen.capture");
        Assert.Equal("PERMISSION_DENIED", pausedCapture.GetProperty("error").GetProperty("code").GetString());
        Assert.True(terminal.Closes > 0);
        Assert.Equal("PERMISSION_DENIED", (await bluetooth.Request("terminal.open", new { columns = 80, rows = 24 })).GetProperty("error").GetProperty("code").GetString());
        server.SetPaused(false);
        store.SaveDevice(device with { Permissions = ["todos.read", "system.read"] });
        var deniedCapture = await bluetooth.Request("screen.capture");
        Assert.Equal("PERMISSION_DENIED", deniedCapture.GetProperty("error").GetProperty("code").GetString());
        foreach (var action in new[] { "app.catalog.get", "app.launch", "audio.state.subscribe", "audio.volume.set", "media.command" })
            Assert.Equal("PERMISSION_DENIED", (await bluetooth.Request(action)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, platform.Captures);
        Assert.Equal("PERMISSION_DENIED", (await bluetooth.Request("terminal.open", new { columns = 80, rows = 24 })).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("PERMISSION_DENIED", (await bluetooth.Request("terminal.read", new { id = "terminal", offset = 0 })).GetProperty("error").GetProperty("code").GetString());
        var todoId = store.Todos().Items.Single().Id;
        var complete = new { operation = "setCompleted", id = todoId, completed = true };
        var deniedWrite = await bluetooth.Request("todo.change", complete);
        Assert.Equal("PERMISSION_DENIED", deniedWrite.GetProperty("error").GetProperty("code").GetString());
        Assert.Single(store.Todos().Items);
        store.SaveDevice(device);
        var todoRequestId = Wire.NewId();
        var completed = await bluetooth.Request("todo.change", complete, todoRequestId);
        var replay = await first.Request("todo.change", complete, todoRequestId);
        Assert.Equal(completed.GetRawText(), replay.GetRawText());
        Assert.True(store.Todos().Items.Single(item => item.Id == todoId).Completed);
        foreach (var blocked in new object[]
        {
            new { operation = "add", title = "手机待办" },
            new { operation = "rename", id = todoId, title = "手机改名" },
            new { operation = "delete", id = todoId },
            new { operation = "setCompleted", id = todoId, completed = false },
        })
        {
            var rejected = await bluetooth.Request("todo.change", blocked);
            Assert.Equal("UNSUPPORTED_ACTION", rejected.GetProperty("error").GetProperty("code").GetString());
        }
        Assert.True(store.Todos().Items.Single().Completed);
        Assert.Equal("电脑配置待办", store.Todos().Items.Single().Title);
        Assert.Single(store.Todos().Items);
        Assert.Equal(
            "UNSUPPORTED_ACTION",
            (await bluetooth.Request("input.hotkey")).GetProperty("error").GetProperty("code").GetString()
        );
        store.SaveDevice(device with { Permissions = ["input.control"] });
        Assert.Equal("UNSUPPORTED_ACTION",
            (await first.Request("input.hotkey", new { presetId = "save" })).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("PERMISSION_DENIED",
            (await first.Request("task.templates")).GetProperty("error").GetProperty("code").GetString());
        store.SaveDevice(device);
        Assert.Equal(2, platform.Launches);
        var changed = await second.Request("app.launch", new { appId = "different" }, id);
        Assert.Equal(
            "INVALID_ARGUMENT",
            changed.GetProperty("error").GetProperty("code").GetString()
        );
        server.SetPaused(true);
        var paused = await bluetooth.Request("app.launch", payload);
        Assert.Equal(
            "PERMISSION_DENIED",
            paused.GetProperty("error").GetProperty("code").GetString()
        );
        var stopped = await first.Request(
            "audio.volume.set",
            new { level = double.NaN.ToString() }
        );
        Assert.Equal("error", stopped.GetProperty("status").GetString());
        server.SetPaused(false);
        store.SaveDevice(device with { Permissions = ["system.read"] });
        var denied = await first.Request("todo.snapshot.get");
        Assert.Equal(
            "PERMISSION_DENIED",
            denied.GetProperty("error").GetProperty("code").GetString()
        );
        foreach (var removed in new[] { "window.list", "window.stream.start", "window.stream.stop", "window.control.start", "window.control.stop" })
            Assert.Equal("UNSUPPORTED_ACTION", (await first.Request(removed)).GetProperty("error").GetProperty("code").GetString());
        using var http = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, remote, _, _) => remote?.Thumbprint == certificate.Thumbprint,
        }) { Timeout = TimeSpan.FromSeconds(5) };
        using var removedEndpoint = await http.GetAsync($"https://127.0.0.1:{port}/window-stream");
        Assert.Equal(HttpStatusCode.NotFound, removedEndpoint.StatusCode);
        await first.SendRequest("session.unpair", Wire.NewId());
        await first.ReceiveEvent("device.revoked");
        await second.ReceiveEvent("device.revoked");
        await bluetooth.ReceiveEvent("device.revoked");
        Assert.True(store.Device(device.DeviceId)!.Revoked);
        Assert.Empty(store.Device(device.DeviceId)!.Permissions);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<Connection> Connect(int port, ECDsa phone, X509Certificate2 cert)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, remote, _, _) =>
            remote != null
            && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(remote.GetPublicKey()),
                SHA256.HashData(cert.GetPublicKey())
            );
        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/ws"), CancellationToken.None);
        var challenge = JsonSerializer.Deserialize<JsonElement>(
            await AgentServer.ReceiveAsync(socket, 8192, new CancellationTokenSource(5000).Token)
        );
        var auth = StoreAndPairingTests.Auth(
            phone,
            "host",
            challenge.GetProperty("linkId").GetString()!,
            challenge.GetProperty("nonce").GetString()!
        );
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(auth.GetRawText()),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );
        var accepted = JsonSerializer.Deserialize<JsonElement>(
            await AgentServer.ReceiveAsync(socket, 8192, new CancellationTokenSource(5000).Token)
        );
        Assert.Equal("authenticated", accepted.GetProperty("type").GetString());
        return new(
            socket,
            accepted.GetProperty("sessionId").GetString()!,
            accepted.GetProperty("deviceId").GetString()!
        );
    }

    private sealed class Connection(ClientWebSocket socket, string session, string device)
        : IDisposable
    {
        public readonly string Session = session;

        public async Task<JsonElement> Receive() =>
            JsonSerializer.Deserialize<JsonElement>(
                await AgentServer.ReceiveAsync(
                    socket,
                    65536,
                    new CancellationTokenSource(5000).Token
                )
            );

        public async Task<JsonElement> Request(
            string action,
            object? payload = null,
            string? id = null
        )
        {
            id ??= Wire.NewId();
            await SendRequest(action, id, payload);
            while (true)
            {
                var value = await Receive();
                if (value.TryGetProperty("requestId", out var request) && request.GetString() == id)
                    return value;
            }
        }

        public async Task ReceiveEvent(string eventType)
        {
            while (true)
            {
                var value = await Receive();
                if (value.TryGetProperty("eventType", out var name) && name.GetString() == eventType)
                    return;
            }
        }

        public async Task SendRequest(string action, string id, object? payload = null)
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(
                    Wire.Serialize(
                        new
                        {
                            protocolVersion = 1,
                            messageType = "request",
                            sessionId = Session,
                            deviceId = device,
                            requestId = id,
                            action,
                            payload = payload ?? new { },
                        }
                    )
                ),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            );
        }

        public void Dispose()
        {
            socket.Abort();
            socket.Dispose();
        }
    }

    private sealed class FakeTerminal : ITerminalPlatform
    {
        public int Writes, Closes;
        public TerminalState Open(string owner, int columns, int rows) => new("terminal", "PowerShell", true);
        public TerminalOutput Read(string owner, string id, long offset) => new(offset, "", false, true);
        public void Write(string owner, string id, long sequence, string text) => Writes++;
        public void Resize(string owner, string id, int columns, int rows) { }
        public void Close(string owner) => Closes++;
        public void Dispose() { }
    }

    private sealed class FakePlatform : IAgentPlatform
    {
        public int Launches;
        public int Captures;
        public string? ScreenshotDirectory;
        public string HostName => "Fixture";

        public IReadOnlyList<DiscoveredApp> DiscoverApps() => [];

        public void Launch(AppEntry app) => Interlocked.Increment(ref Launches);

        public SystemSnapshot Sample() => new(
            10, 1, 2, 3, HostName,
            [new("C:", 97, 100)],
            new(1024, 512),
            new(4096, 2048)
        );

        public AudioState GetAudio() => new(1, .5f, false, true);

        public AudioState SetAudio(float? level, bool? muted) =>
            new(2, level ?? .5f, muted ?? false, true);

        public Task<bool> MediaAsync(string command) => Task.FromResult(true);

        public int SaveScreenshots(string directory)
        {
            ScreenshotDirectory = directory;
            Interlocked.Increment(ref Captures);
            return 2;
        }
    }

}
