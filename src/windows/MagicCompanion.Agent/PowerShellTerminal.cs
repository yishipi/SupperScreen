using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MagicCompanion.Agent;

// ConPTY emits the same VT stream used by Windows Terminal, including PSReadLine editing.
public sealed class PowerShellTerminal : ITerminalPlatform
{
    private readonly object gate = new();
    private Session? current;
    public static string Executable => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"))
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe")
        : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static TaskTemplate Script(string name, string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new AgentException("INVALID_ARGUMENT", "请选择存在的脚本");
        var title = AgentStore.ValidateTitle(name);
        var directory = Path.GetDirectoryName(path)!;
        if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            return new(Wire.NewId(), title, Path.Combine(Environment.SystemDirectory, "cmd.exe"), [], directory, BatchFile: path);
        if (!path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            throw new AgentException("INVALID_ARGUMENT", "请选择 .ps1 或 .cmd 脚本");
        // Windows PowerShell 5.1 otherwise uses the active legacy code page for redirected output.
        var command = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); & '" + path.Replace("'", "''") + "'";
        return new(Wire.NewId(), title, Executable,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command], directory);
    }

    public TerminalState Open(string owner, int columns, int rows)
    {
        lock (gate)
        {
            if (current != null && current.Owner != owner)
                throw new AgentException("DEVICE_BUSY", "终端正在被另一台手机使用");
            if (current is { Running: false })
            {
                current.Dispose();
                current = null;
            }
            current ??= new Session(owner, columns, rows);
            return new(current.Id, "PowerShell", current.Running, current.InputSequence);
        }
    }

    private Session Owned(string owner, string id) => current is { } s && s.Owner == owner && s.Id == id
        ? s : throw new AgentException("TERMINAL_CLOSED", "终端已关闭，请重新打开");

    public TerminalOutput Read(string owner, string id, long offset)
    {
        lock (gate) return Owned(owner, id).Read(offset);
    }
    public void Write(string owner, string id, long sequence, string text)
    {
        lock (gate) Owned(owner, id).Write(sequence, text);
    }
    public void Resize(string owner, string id, int columns, int rows)
    {
        lock (gate) Owned(owner, id).Resize(columns, rows);
    }
    public void Close(string owner)
    {
        lock (gate)
        {
            if (current?.Owner != owner) return;
            current.Dispose();
            current = null;
        }
    }
    public void Dispose() { lock (gate) { current?.Dispose(); current = null; } }

    private sealed class Session : IDisposable
    {
        public readonly string Owner;
        public readonly string Id = Wire.NewId();
        private IntPtr console;
        private readonly object consoleGate = new();
        private FileStream? input, output;
        private Process? process;
        private readonly object outputGate = new();
        private readonly List<byte> buffer = new();
        private long start, sequence;
        public long InputSequence => sequence;
        private bool ended;
        public bool Running => process is { HasExited: false };

        public Session(string owner, int columns, int rows)
        {
            Owner = owner;
            var executable = Executable;
            var directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!File.Exists(executable) || !Directory.Exists(directory))
                throw new AgentException("INVALID_ARGUMENT", "PowerShell 或用户目录不可用");
            IntPtr attributes = IntPtr.Zero;
            try
            {
                if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
                using var inputReadHandle = inputRead;
                input = new FileStream(inputWrite, FileAccess.Write, 4096, false);
                if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
                using var outputWriteHandle = outputWrite;
                output = new FileStream(outputRead, FileAccess.Read, 4096, false);
                Check(CreatePseudoConsole(new((short)columns, (short)rows), inputRead, outputWrite, 0, out console));
                nuint size = 0;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal((nint)size);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
                if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20016, console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                // Null standard handles prevent the parent console/redirection from leaking into the PTY.
                var startup = new StartupInfoEx { Info = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100 }, Attributes = attributes };
                var command = new StringBuilder("\"" + executable + "\" -NoLogo -NoProfile");
                // Drain output before launching: startup/teardown can block when the PTY pipe fills.
                _ = Task.Run(Drain);
                if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x80000, IntPtr.Zero,
                    directory, ref startup, out var info)) throw new Win32Exception();
                CloseHandle(info.Thread);
                try { process = Process.GetProcessById(info.ProcessId); }
                finally { CloseHandle(info.Process); }
                process.Exited += (_, _) => CloseConsole();
                process.EnableRaisingEvents = true;
            }
            catch { Dispose(); throw; }
            finally
            {
                if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            }
        }

        private void Drain()
        {
            var bytes = new byte[8192];
            try
            {
                int count;
                while ((count = output!.Read(bytes, 0, bytes.Length)) > 0)
                    lock (outputGate)
                    {
                        buffer.AddRange(bytes.AsSpan(0, count).ToArray());
                        if (buffer.Count > 262144)
                        {
                            var remove = buffer.Count - 262144;
                            // Keep the retained tail on a UTF-8 character boundary.
                            while (remove < buffer.Count && (buffer[remove] & 0xC0) == 0x80) remove++;
                            buffer.RemoveRange(0, remove); start += remove;
                        }
                    }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            finally { lock (outputGate) ended = true; output?.Dispose(); }
        }

        public TerminalOutput Read(long offset)
        {
            lock (outputGate)
            {
                var reset = offset < start || offset > start + buffer.Count;
                if (reset) offset = start;
                var data = buffer.Skip((int)(offset - start)).Take(12288).ToArray();
                return new(offset + data.Length, Convert.ToBase64String(data), reset, !ended);
            }
        }

        public void Write(long next, string text)
        {
            if (next <= sequence) return;
            if (next != sequence + 1) throw new AgentException("INVALID_ARGUMENT", "终端输入顺序不一致，请重新打开");
            if (!Running) throw new AgentException("TERMINAL_CLOSED", "终端程序已退出");
            var bytes = Encoding.UTF8.GetBytes(text);
            input!.Write(bytes); input.Flush(); sequence = next;
        }

        public void Resize(int columns, int rows)
        {
            lock (consoleGate)
                if (console != IntPtr.Zero)
                    Check(ResizePseudoConsole(console, new((short)columns, (short)rows)));
        }

        private void CloseConsole()
        {
            lock (consoleGate)
            {
                if (console == IntPtr.Zero) return;
                ClosePseudoConsole(console);
                console = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            try { if (process is { HasExited: false }) { process.Kill(true); process.WaitForExit(2000); } }
            catch (InvalidOperationException) { }
            process?.Dispose(); process = null;
            input?.Dispose();
            CloseConsole();
        }
    }

    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize;
        public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
