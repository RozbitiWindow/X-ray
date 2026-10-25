using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XRay.App.Services.Metrics;

/// <summary>Total CPU utilisation from GetSystemTimes deltas (idle vs. kernel + user time).</summary>
internal sealed class CpuSampler
{
    private ulong _lastIdle, _lastKernel, _lastUser;
    private bool _primed;

    /// <summary>Returns utilisation in percent, or null on the first call or if the API fails.</summary>
    public double? Sample()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user))
        {
            return null;
        }

        ulong i = (ulong)idle, k = (ulong)kernel, u = (ulong)user;
        double? result = null;
        if (_primed)
        {
            ulong idleDelta = i - _lastIdle;
            ulong totalDelta = (k - _lastKernel) + (u - _lastUser); // kernel time includes idle time
            if (totalDelta > 0)
            {
                result = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
            }
        }

        _lastIdle = i;
        _lastKernel = k;
        _lastUser = u;
        _primed = true;
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}

/// <summary>Physical memory from GlobalMemoryStatusEx.</summary>
internal static class MemorySampler
{
    public static MemoryInfo? Sample()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? new MemoryInfo(status.TotalPhys, status.AvailPhys) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>Physical disk throughput from the PhysicalDisk performance counters (_Total instance).</summary>
internal sealed class DiskActivitySampler : IDisposable
{
    private PerformanceCounter? _read;
    private PerformanceCounter? _write;
    private PerformanceCounter? _idle;

    public string? UnavailableReason { get; private set; }

    public void Initialize()
    {
        try
        {
            _read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
            _write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
            _idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
            _read.NextValue();
            _write.NextValue();
            _idle.NextValue();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            UnavailableReason = "Windows disk performance counters are disabled or unavailable on this system.";
            Dispose();
        }
    }

    public DiskActivity? Sample()
    {
        if (_read is null || _write is null || _idle is null)
        {
            return null;
        }

        try
        {
            double idle = _idle.NextValue();
            return new DiskActivity(_read.NextValue(), _write.NextValue(), Math.Clamp(100 - idle, 0, 100));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            UnavailableReason = "Disk performance counters stopped responding.";
            return null;
        }
    }

    public void Dispose()
    {
        _read?.Dispose();
        _write?.Dispose();
        _idle?.Dispose();
        _read = _write = _idle = null;
    }
}

/// <summary>Per-process CPU and memory, computed from processor-time deltas between samples.</summary>
internal sealed class ProcessSampler
{
    private readonly Dictionary<int, (TimeSpan Cpu, long Ticks)> _previous = new();

    public (List<ProcessSample> Samples, int WithoutAccess) Sample()
    {
        long now = Stopwatch.GetTimestamp();
        var samples = new List<ProcessSample>(400);
        var seen = new HashSet<int>();
        int noAccess = 0;
        int cpuCount = Environment.ProcessorCount;

        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == 0)
                    {
                        continue; // System Idle Process
                    }

                    seen.Add(p.Id);
                    double? cpu = null;
                    try
                    {
                        TimeSpan total = p.TotalProcessorTime;
                        if (_previous.TryGetValue(p.Id, out var prev))
                        {
                            double elapsedMs = Stopwatch.GetElapsedTime(prev.Ticks, now).TotalMilliseconds;
                            if (elapsedMs > 0)
                            {
                                cpu = Math.Clamp((total - prev.Cpu).TotalMilliseconds / elapsedMs / cpuCount * 100, 0, 100);
                            }
                        }

                        _previous[p.Id] = (total, now);
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        noAccess++;
                    }

                    samples.Add(new ProcessSample(p.Id, p.ProcessName, cpu, p.WorkingSet64, p.PrivateMemorySize64));
                }
                catch (InvalidOperationException)
                {
                    // The process exited while being sampled.
                }
            }
        }

        foreach (int stale in _previous.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _previous.Remove(stale);
        }

        return (samples, noAccess);
    }

    public void Reset() => _previous.Clear();
}

/// <summary>Capacity of fixed and removable drives. Network drives are skipped because they can block.</summary>
internal static class DriveSampler
{
    public static List<DriveSample> Sample()
    {
        var list = new List<DriveSample>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady)
                {
                    continue;
                }

                string type = d.DriveType == DriveType.Removable ? "Removable" : "Local disk";
                list.Add(new DriveSample(d.Name, string.IsNullOrWhiteSpace(d.VolumeLabel) ? type : d.VolumeLabel,
                    d.DriveFormat, type, d.TotalSize, d.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive not ready or access denied; omit it.
            }
        }

        return list;
    }
}
