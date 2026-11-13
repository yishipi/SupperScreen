using System.Text;
using System.IO;
using MagicCompanion.Agent;
using MagicCompanion.Core;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;

if (args.Contains("--screenshots"))
{
    System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
    var folder = Path.Combine(Path.GetTempPath(), "SupperScreen-capture-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        var screens = System.Windows.Forms.Screen.AllScreens.OrderByDescending(screen => screen.Primary)
            .ThenBy(screen => screen.Bounds.Left).ThenBy(screen => screen.Bounds.Top).ToArray();
        var count = ScreenSnapshots.Save(folder);
        var files = Directory.GetFiles(folder, "*.png").Order().ToArray();
        if (count != screens.Length || files.Length != count)
            throw new Exception("Expected one PNG per active monitor");
        var batch = files[0][..^8];
        for (var index = 0; index < count; index++)
        {
            using var png = System.Drawing.Image.FromFile(files[index]);
            var bounds = screens[index].Bounds;
            if (!files[index].StartsWith(batch, StringComparison.Ordinal) || png.Size != bounds.Size
                || png.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Png.Guid)
                throw new Exception("Screenshot batch, format or monitor dimensions are incorrect");
            Console.WriteLine($"PNG {index + 1}: {png.Width}x{png.Height}");
        }
        Console.WriteLine($"PASS: {count} monitor screenshots saved to the requested directory");
    }
    finally
    {
        foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
        Directory.Delete(folder);
    }
    return;
}

if (args.Contains("--lan"))
{
    var addresses = HostIdentity.LocalAddresses();
    if (addresses.Length == 0)
        throw new Exception("The test needs at least one active private IPv4 interface");
    var folder = Directory.CreateTempSubdirectory("SupperScreen-lan-check-");
    try
    {
        using var store = new AgentStore(":memory:");
        using var identity = new HostIdentity(store, folder.FullName);
        using var platform = new WindowsPlatform();
        using var certificate = identity.CreateCertificate(addresses);
        await using var agent = new AgentServer(store, platform, new(store, identity.HostId), identity.HostId);
        var socket = new TcpListener(IPAddress.Any, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        await agent.StartAsync(certificate, 0, port, addresses);
        using var client = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, remote, _, errors) =>
                remote?.GetPublicKeyString() == certificate.GetPublicKeyString() &&
                (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) == 0,
        }) { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var address in addresses)
        {
            var health = await client.GetStringAsync($"https://{address}:{port}/health");
            if (!health.Contains("MagicCompanion", StringComparison.Ordinal))
                throw new Exception("LAN endpoint did not identify the Agent");
        }
        Console.WriteLine($"PASS: {addresses.Length} automatically selected LAN address(es), valid TLS IP names, health endpoints");
    }
    finally { folder.Delete(true); }
    return;
}

using var terminal = new PowerShellTerminal();
var session = terminal.Open("test-owner", 100, 24);
if (terminal.Open("test-owner", 100, 24).Id != session.Id) throw new Exception("Open must resume the owned terminal");
try { terminal.Read("another-owner", session.Id, 0); throw new Exception("Ownership was not checked"); }
catch (MagicCompanion.Core.AgentException ex) when (ex.Code == "TERMINAL_CLOSED") { }
long offset = 0;
var output = new StringBuilder();
var decoder = Encoding.UTF8.GetDecoder();
async Task WaitFor(string expected)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    while (!output.ToString().Contains(expected, StringComparison.Ordinal))
    {
        if (timeout.IsCancellationRequested) throw new Exception("Expected " + expected + "; received " + output.ToString().Replace("\u001b", "<ESC>"));
        var chunk = terminal.Read("test-owner", session.Id, offset);
        offset = chunk.Offset;
        var bytes = Convert.FromBase64String(chunk.Data);
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
        output.Append(chars, 0, count);
        await Task.Delay(100);
    }
}
// Wait for PSReadLine to initialize before sending a command.
await WaitFor("PS ");
terminal.Resize("test-owner", session.Id, 90, 20);
// The hosted Windows runner may start with a legacy console code page.
terminal.Write("test-owner", session.Id, 1, "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $ssProbe = 40; [Console]::WriteLine(('SUPPER'+'-READY'));\r");
await WaitFor("SUPPER-READY");
terminal.Write("test-owner", session.Id, 2, "$ssProbe++; [Console]::WriteLine(('VALUE=' + $ssProbe)); [Console]::WriteLine([char]0x4E2D + [string][char]0x6587)\r");
// A duplicate input sequence must not execute the increment again.
terminal.Write("test-owner", session.Id, 2, "$ssProbe++;\r");
await WaitFor("VALUE=41");
await WaitFor("中文");
terminal.Write("test-owner", session.Id, 3, "[Console]::WriteLine(('FINAL=' + $ssProbe))\r");
await WaitFor("FINAL=41");
terminal.Write("test-owner", session.Id, 4, "Start-Sleep -Seconds 30\r");
await Task.Delay(500);
terminal.Write("test-owner", session.Id, 5, "\u0003");
await Task.Delay(500);
terminal.Write("test-owner", session.Id, 6, "[Console]::WriteLine(('CANCEL'+'-READY'))\r");
await WaitFor("CANCEL-READY");
terminal.Write("test-owner", session.Id, 7, "[Console]::Write(('x'*280000)); [Console]::WriteLine(('TAIL'+'-MARK'))\r");
await WaitFor("TAIL-MARK");
if (!terminal.Read("test-owner", session.Id, 0).Reset) throw new Exception("Output retention must reset an evicted offset");
terminal.Write("test-owner", session.Id, 8, "[Console]::WriteLine(('ERASE'+'-OK'))x\u007f\r");
await WaitFor("ERASE-OK");
if (!output.ToString().Contains('\u001b')) throw new Exception("ConPTY did not emit VT output");
terminal.Close("test-owner");
Console.WriteLine("PASS: ConPTY PowerShell, persistent variables, UTF-8, VT, resize, ownership, input deduplication, Ctrl+C, output retention, teardown");

var temporary = Path.Combine(Path.GetTempPath(), "SupperScreen-script-" + Guid.NewGuid());
Directory.CreateDirectory(temporary);
try
{
    var path = Path.Combine(temporary, "带 空格和'引号.ps1");
    // BOM also supports the legacy PowerShell script parser.
    await File.WriteAllTextAsync(path, "[Console]::WriteLine('脚本成功'); exit 0", new UTF8Encoding(true));
    using var store = new MagicCompanion.Core.AgentStore(":memory:");
    var script = PowerShellTerminal.Script("测试脚本", path);
    store.SetSetting("taskTemplates", MagicCompanion.Core.Wire.Serialize(new[] { script }));
    await using var runner = new MagicCompanion.Core.TaskRunner(store);
    var job = runner.Start("phone", script.Id, default);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (runner.Get("phone", job.JobId).State == "running") await Task.Delay(100, timeout.Token);
    var result = runner.Get("phone", job.JobId);
    if (result.State != "succeeded" || !result.Lines.Contains("脚本成功"))
        throw new Exception("Script failed: " + string.Join("\n", result.Lines));
    Console.WriteLine("PASS: configured PowerShell script, spaces/quotes in path, Chinese output, exit status");
}
finally { Directory.Delete(temporary, true); }
