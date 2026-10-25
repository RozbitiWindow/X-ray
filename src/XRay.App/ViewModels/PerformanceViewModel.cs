using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using XRay.App.Services;
using XRay.App.Services.Metrics;

namespace XRay.App.ViewModels;

public sealed class ProcessRow(ProcessSample sample)
{
    public string Name => sample.Name;

    public int Id => sample.Id;

    public string CpuText => sample.CpuPercent is { } c ? $"{c:0.0}%" : "n/a";

    public double Cpu => sample.CpuPercent ?? -1;

    public string MemoryText => Format.Size(sample.WorkingSetBytes);

    public string PrivateText => Format.Size(sample.PrivateBytes);

    public string CpuTooltip => sample.CpuPercent is null
        ? "Windows does not allow reading this process's CPU time without administrator rights."
        : "Share of total CPU capacity across all processors.";
}

public sealed class GpuRow(GpuInfo gpu)
{
    public string Name => gpu.Name;

    public string Utilization => gpu.UtilizationPercent is { } u ? $"{u:0}%" : "Not available";

    public double UtilizationValue => gpu.UtilizationPercent ?? 0;

    public string Temperature => gpu.TemperatureCelsius is { } t ? $"{t:0} °C" : "Not reported";

    public string? TemperatureNote => gpu.TemperatureNote;

    public string Memory => gpu.DedicatedUsedBytes is { } used
        ? $"{Format.Size((long)used)} of {Format.Size((long)gpu.DedicatedTotalBytes)}"
        : gpu.DedicatedTotalBytes > 0 ? $"{Format.Size((long)gpu.DedicatedTotalBytes)} dedicated (usage not reported)" : "Shared memory only";
}

/// <summary>Live performance metrics with session history and a process table.</summary>
public sealed partial class PerformanceViewModel : PageViewModel
{
    private const int ProcessRows = 40;
    private readonly SystemMetricsService _metrics;
    private readonly SettingsService _settings;
    private IDisposable? _processSampling;

    [ObservableProperty] private long _revision;
    [ObservableProperty] private string _cpuText = "-";
    [ObservableProperty] private string _memoryText = "-";
    [ObservableProperty] private string _memoryDetail = string.Empty;
    [ObservableProperty] private string _gpuText = "-";
    [ObservableProperty] private string _gpuDetail = string.Empty;
    [ObservableProperty] private string _diskText = "-";
    [ObservableProperty] private string _diskDetail = string.Empty;
    [ObservableProperty] private string? _gpuUnavailable;
    [ObservableProperty] private string? _diskUnavailable;
    [ObservableProperty] private string? _processNote;
    [ObservableProperty] private bool _sortProcessesByMemory;
    [ObservableProperty] private string _historySpan = string.Empty;

    public PerformanceViewModel(SystemMetricsService metrics, SettingsService settings)
    {
        _metrics = metrics;
        _settings = settings;
        _metrics.Updated += (_, _) => OnMetrics();
    }

    public override string Title => "Performance";

    public override string Glyph => "";

    public SystemMetricsService Metrics => _metrics;

    public int HistoryCapacity => SystemMetricsService.HistoryCapacity;

    public ObservableCollection<ProcessRow> Processes { get; } = [];

    public ObservableCollection<GpuRow> Gpus { get; } = [];

    public ObservableCollection<DriveCard> Drives { get; } = [];

    public bool SortProcessesByCpu
    {
        get => !SortProcessesByMemory;
        set => SortProcessesByMemory = !value;
    }

    public override void OnActivated()
    {
        _processSampling ??= _metrics.RequestProcessSampling();
        ProcessNote = "Collecting process data…";
    }

    public override void OnDeactivated()
    {
        // Per-process sampling is the most expensive metric; stop it when nobody is looking.
        _processSampling?.Dispose();
        _processSampling = null;
    }

    partial void OnSortProcessesByMemoryChanged(bool value)
    {
        OnPropertyChanged(nameof(SortProcessesByCpu));
        UpdateProcesses(_metrics.Latest);
    }

    private void OnMetrics()
    {
        MetricsSnapshot? s = _metrics.Latest;
        if (s is null)
        {
            return;
        }

        Revision = _metrics.Revision;
        double seconds = _metrics.CurrentInterval / 1000.0 * SystemMetricsService.HistoryCapacity;
        HistorySpan = $"Last {Format.Duration(TimeSpan.FromSeconds(seconds))} · sampled every {_metrics.CurrentInterval / 1000.0:0.#} s";

        CpuText = s.CpuPercent is { } cpu ? $"{cpu:0}%" : "-";

        if (s.Memory is { } mem)
        {
            MemoryText = $"{mem.UsedPercent:0}%";
            MemoryDetail = $"{Format.Size((long)mem.UsedBytes)} used · {Format.Size((long)mem.AvailableBytes)} available · {Format.Size((long)mem.TotalBytes)} installed";
        }

        GpuUnavailable = s.Gpus.Count == 0 ? s.GpuUnavailableReason ?? "Detecting graphics adapters…" : null;
        if (s.Gpus.Count > 0)
        {
            GpuInfo g = s.Gpus[0];
            GpuText = g.UtilizationPercent is { } u ? $"{u:0}%" : "n/a";
            GpuDetail = g.Name + (g.TemperatureCelsius is { } t ? $" · {t:0} °C" : string.Empty);
        }
        else
        {
            GpuText = "n/a";
            GpuDetail = string.Empty;
        }

        if (Gpus.Count != s.Gpus.Count)
        {
            Gpus.Clear();
            foreach (GpuInfo g in s.Gpus)
            {
                Gpus.Add(new GpuRow(g));
            }
        }
        else
        {
            for (int i = 0; i < s.Gpus.Count; i++)
            {
                Gpus[i] = new GpuRow(s.Gpus[i]);
            }
        }

        DiskUnavailable = s.Disk is null ? s.DiskUnavailableReason ?? "Initialising disk counters…" : null;
        if (s.Disk is { } disk)
        {
            DiskText = $"{disk.ActivePercent ?? 0:0}%";
            DiskDetail = $"Read {Format.Rate(disk.ReadBytesPerSecond ?? 0)} · Write {Format.Rate(disk.WriteBytesPerSecond ?? 0)}";
        }

        if (Drives.Count != s.Drives.Count || Revision % 5 == 0)
        {
            Drives.Clear();
            foreach (DriveSample d in s.Drives)
            {
                Drives.Add(new DriveCard(d));
            }
        }

        UpdateProcesses(s);
    }

    private void UpdateProcesses(MetricsSnapshot? s)
    {
        if (s?.Processes is not { } processes)
        {
            return;
        }

        IEnumerable<ProcessSample> ordered = SortProcessesByMemory
            ? processes.OrderByDescending(p => p.WorkingSetBytes)
            : processes.OrderByDescending(p => p.CpuPercent ?? -1).ThenByDescending(p => p.WorkingSetBytes);

        List<ProcessRow> rows = ordered.Take(ProcessRows).Select(p => new ProcessRow(p)).ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < Processes.Count)
            {
                Processes[i] = rows[i];
            }
            else
            {
                Processes.Add(rows[i]);
            }
        }

        while (Processes.Count > rows.Count)
        {
            Processes.RemoveAt(Processes.Count - 1);
        }

        ProcessNote = $"{processes.Count} processes · top {rows.Count} shown" +
                      (s.ProcessesWithoutAccess is > 0 ? $" · CPU time of {s.ProcessesWithoutAccess} protected processes is not readable without administrator rights" : string.Empty);
    }
}
