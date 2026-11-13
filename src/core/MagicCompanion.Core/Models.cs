using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MagicCompanion.Core;

public static class Wire
{
    public const int Version = 1;
    public const int MaxMessageBytes = 65536;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(object value)
    {
        var json = JsonSerializer.Serialize(value, Json);
        if (json.IndexOf("\\uD", StringComparison.OrdinalIgnoreCase) < 0)
            return json;
        // System.Text.Json escapes supplementary Unicode even with a relaxed encoder.
        // Emit valid surrogate pairs as UTF-8 so 100 valid 80-character titles fit the 64KiB wire limit.
        var output = new StringBuilder(json.Length);
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] != '\\' || i + 1 >= json.Length)
            {
                output.Append(json[i]);
                continue;
            }
            if (
                json[i + 1] == 'u'
                && i + 11 < json.Length
                && json[i + 6] == '\\'
                && json[i + 7] == 'u'
                && ushort.TryParse(
                    json.AsSpan(i + 2, 4),
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out var high
                )
                && char.IsHighSurrogate((char)high)
                && ushort.TryParse(
                    json.AsSpan(i + 8, 4),
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out var low
                )
                && char.IsLowSurrogate((char)low)
            )
            {
                output.Append((char)high).Append((char)low);
                i += 11;
            }
            else
            {
                output.Append(json[i]).Append(json[++i]);
            }
        }
        return output.ToString();
    }

    public static string Fingerprint(ReadOnlySpan<byte> key) =>
        Convert.ToHexStringLower(SHA256.HashData(key));

    public static string Hash(string value) => Fingerprint(Encoding.UTF8.GetBytes(value));

    public static string NewId() => Guid.NewGuid().ToString("D");

    public static string ChallengeText(
        string hostId,
        string linkId,
        string nonce,
        string deviceId
    ) => $"1\n{hostId}\n{linkId}\n{nonce}\n{deviceId}";
}

public sealed class AgentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record TodoItem(string Id, string Title, bool Completed, int SortOrder, string? Color = null, string? TextColor = null, string? BackgroundColor = null);

public sealed record TodoSnapshot(long Revision, string UpdatedAt, IReadOnlyList<TodoItem> Items);

public sealed record TrustedDevice(
    string DeviceId,
    string Name,
    string PublicKey,
    string[] Permissions,
    bool Revoked
);

public sealed record AppEntry(
    string AppId,
    string DisplayName,
    string SourceId,
    string? ExecutablePath,
    string Arguments,
    string? IconBase64
);

public sealed record DiscoveredApp(
    string SourceId,
    string DisplayName,
    string? ExecutablePath,
    string Arguments,
    string? IconBase64
)
{
    public override string ToString() => DisplayName;
}

public sealed record AudioState(long Revision, float Level, bool Muted, bool Available);

public sealed record DiskCapacity(string Name, long UsedBytes, long TotalBytes);

public sealed record TransferRate(long? ReadBytesPerSecond, long? WriteBytesPerSecond);

public sealed record NetworkRate(long ReceivedBytesPerSecond, long SentBytesPerSecond);

public sealed record SystemSnapshot(
    double CpuPercent,
    ulong RamUsedBytes,
    ulong RamTotalBytes,
    long DiskFreeBytes,
    string HostName,
    IReadOnlyList<DiskCapacity>? Disks = null,
    TransferRate? DiskIo = null,
    NetworkRate? Network = null
);

public sealed record TaskTemplate(
    string Id,
    string Name,
    string Executable,
    string[] Arguments,
    string WorkingDirectory,
    int TimeoutSeconds = 300,
    Dictionary<string, string>? ParameterPatterns = null
);

public sealed record TaskJob(
    string JobId,
    string TemplateId,
    string State,
    string? StartedAt,
    int? ExitCode,
    string[] Lines
);

public sealed record TerminalState(string Id, string Shell, bool Running, long InputSequence = 0);
public sealed record TerminalOutput(long Offset, string Data, bool Reset, bool Running);

public interface ITerminalPlatform : IDisposable
{
    TerminalState Open(string owner, int columns, int rows);
    TerminalOutput Read(string owner, string id, long offset);
    void Write(string owner, string id, long sequence, string text);
    void Resize(string owner, string id, int columns, int rows);
    void Close(string owner);
}

public interface IAgentPlatform
{
    string HostName { get; }
    IReadOnlyList<DiscoveredApp> DiscoverApps();
    void Launch(AppEntry app);
    SystemSnapshot Sample();
    AudioState GetAudio();
    AudioState SetAudio(float? level, bool? muted);
    Task<bool> MediaAsync(string command);
    int SaveScreenshots(string directory);
}

public static class PermissionSet
{
    private static readonly Dictionary<string, string> Requirements = new()
    {
        ["tasks.run"] = "tasks.read",
        ["todos.write"] = "todos.read",
    };
    public static readonly string[] Basic =
    [
        "system.read",
        "todos.read",
        "app.launch",
        "audio.control",
    ];
    public static readonly string[] All =
    [
        .. Basic,
        "todos.write",
        "tasks.read",
        "tasks.run",
        "terminal.control",
        "screen.capture",
    ];

    public static string[] Normalize(IEnumerable<string> permissions)
    {
        var selected = permissions.Intersect(All).ToHashSet(StringComparer.Ordinal);
        foreach (var (permission, required) in Requirements)
            if (selected.Contains(permission))
                selected.Add(required);
        return All.Where(selected.Contains).ToArray();
    }

    public static string[] Set(IEnumerable<string> permissions, string permission, bool enabled)
    {
        var selected = permissions.ToHashSet(StringComparer.Ordinal);
        if (enabled)
            selected.Add(permission);
        else
        {
            selected.Remove(permission);
            foreach (var (dependent, required) in Requirements)
                if (required == permission)
                    selected.Remove(dependent);
        }
        return Normalize(selected);
    }

    public static void Demand(TrustedDevice device, string permission)
    {
        if (device.Revoked)
            throw new AgentException("DEVICE_REVOKED", "设备已解除配对");
        if (!device.Permissions.Contains(permission, StringComparer.Ordinal))
            throw new AgentException("PERMISSION_DENIED", "电脑未授予此操作权限");
    }
}
