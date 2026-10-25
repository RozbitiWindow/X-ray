namespace XRay.App.Services.Metrics;

/// <summary>Physical memory status from GlobalMemoryStatusEx.</summary>
public sealed record MemoryInfo(ulong TotalBytes, ulong AvailableBytes)
{
    public ulong UsedBytes => TotalBytes - AvailableBytes;

    public double UsedPercent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

/// <summary>One graphics adapter. Null values mean "not reported", never zero.</summary>
public sealed record GpuInfo(
    string Name,
    double? UtilizationPercent,
    double? TemperatureCelsius,
    ulong? DedicatedUsedBytes,
    ulong DedicatedTotalBytes,
    string? TemperatureNote);

/// <summary>Aggregate physical disk throughput. Null values mean the counters are unavailable.</summary>
public sealed record DiskActivity(double? ReadBytesPerSecond, double? WriteBytesPerSecond, double? ActivePercent);

/// <summary>A process sample. CPU is null when Windows denies access to the process times.</summary>
public sealed record ProcessSample(int Id, string Name, double? CpuPercent, long WorkingSetBytes, long PrivateBytes);

/// <summary>Capacity of one drive.</summary>
public sealed record DriveSample(string Name, string Label, string Format, string Type, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    public double UsedPercent => TotalBytes <= 0 ? 0 : 100.0 * UsedBytes / TotalBytes;

    public double FreePercent => 100 - UsedPercent;
}

/// <summary>Everything sampled in one polling tick.</summary>
public sealed record MetricsSnapshot(
    DateTime TimestampUtc,
    double? CpuPercent,
    string? CpuUnavailableReason,
    MemoryInfo? Memory,
    IReadOnlyList<GpuInfo> Gpus,
    string? GpuUnavailableReason,
    DiskActivity? Disk,
    string? DiskUnavailableReason,
    IReadOnlyList<DriveSample> Drives,
    IReadOnlyList<ProcessSample>? Processes,
    int? ProcessesWithoutAccess);
