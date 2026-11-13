using System.Runtime.InteropServices;

namespace MagicCompanion.Agent;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatus
    {
        public uint Length,
            Load;
        public ulong TotalPhysical,
            AvailablePhysical,
            TotalPageFile,
            AvailablePageFile,
            TotalVirtual,
            AvailableVirtual,
            AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint Low,
            High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("user32.dll")]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    internal static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("kernel32.dll")]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll")]
    internal static extern bool GetSystemTimes(
        out FileTime idle,
        out FileTime kernel,
        out FileTime user
    );

    internal static ushort ParseKey(string key) =>
        key.ToUpperInvariant() switch
        {
            "ENTER" => 13,
            "BACKSPACE" => 8,
            "TAB" => 9,
            "ESCAPE" or "ESC" => 27,
            "DELETE" => 46,
            "LEFT" => 37,
            "UP" => 38,
            "RIGHT" => 39,
            "DOWN" => 40,
            "HOME" => 36,
            "END" => 35,
            "CTRL" or "CONTROL" => 17,
            "SHIFT" => 16,
            "ALT" => 18,
            "SPACE" => 32,
            "`" or "GRAVE" or "BACKTICK" or "OEM_3" => 0xC0,
            _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => char.ToUpperInvariant(
                key[0]
            ),
            _
                when key.StartsWith("F", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(key[1..], out var function)
                    && function is >= 1 and <= 12 => (ushort)(111 + function),
            _ => throw new AgentException("INVALID_ARGUMENT", "不支持此按键"),
        };
}
