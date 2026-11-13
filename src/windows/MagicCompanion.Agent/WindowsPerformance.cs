using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace MagicCompanion.Agent;

internal sealed class WindowsPerformance : IDisposable
{
    private const uint FormatLarge = 0x00000400;
    private IntPtr query;
    private IntPtr reads;
    private IntPtr writes;
    private long lastSample;
    private Dictionary<string, (long Received, long Sent)> previous = [];
    private (TransferRate? DiskIo, NetworkRate? Network) rates;

    public WindowsPerformance()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0)
            return;
        PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out reads);
        PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out writes);
    }

    public static IReadOnlyList<DiskCapacity> Volumes()
    {
        var volumes = new List<DiskCapacity>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                    continue;
                var total = drive.TotalSize;
                if (total > 0)
                    volumes.Add(new(drive.Name.TrimEnd('\\'), Math.Clamp(total - drive.TotalFreeSpace, 0, total), total));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return volumes;
    }

    public (TransferRate? DiskIo, NetworkRate? Network) Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = lastSample == 0 ? 0 : Stopwatch.GetElapsedTime(lastSample, now).TotalSeconds;
        if (lastSample != 0 && elapsed < 0.75)
            return rates;

        TransferRate? disk = null;
        if (query != IntPtr.Zero && PdhCollectQueryData(query) == 0 && lastSample != 0)
            disk = new(ReadCounter(reads), ReadCounter(writes));

        var current = new Dictionary<string, (long Received, long Sent)>();
        long received = 0, sent = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            try
            {
                var stats = nic.GetIPStatistics();
                current[nic.Id] = (stats.BytesReceived, stats.BytesSent);
                if (!previous.TryGetValue(nic.Id, out var old))
                    continue;
                received += Math.Max(0, stats.BytesReceived - old.Received);
                sent += Math.Max(0, stats.BytesSent - old.Sent);
            }
            catch (NetworkInformationException) { }
        }
        previous = current;
        lastSample = now;
        rates = (disk, elapsed > 0 && current.Count > 0
            ? new((long)(received / elapsed), (long)(sent / elapsed))
            : null);
        return rates;
    }

    private static long? ReadCounter(IntPtr counter)
    {
        if (counter != IntPtr.Zero &&
            PdhGetFormattedCounterValue(counter, FormatLarge, IntPtr.Zero, out var value) == 0 &&
            value.Status is 0 or 1 && value.Value >= 0)
            return value.Value;
        return null;
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero)
            PdhCloseQuery(query);
        query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public long Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out CounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
