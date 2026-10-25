using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XRay.App.Services;
using XRay.App.Services.Metrics;
using XRay.Core.History;

namespace XRay.App.ViewModels;

/// <summary>A drive tile on the overview and performance pages.</summary>
public sealed class DriveCard(DriveSample drive)
{
    public DriveSample Drive { get; } = drive;

    public string Title => $"{Drive.Label} ({Drive.Name.TrimEnd('\\')})";

    public string Root => Drive.Name;

    public string Detail => $"{Drive.Format} · {Drive.Type}";

    public double UsedPercent => Drive.UsedPercent;

    public string FreeText => $"{Format.Size(Drive.FreeBytes)} free";

    public string TotalText => $"of {Format.Size(Drive.TotalBytes)}";

    public string UsedText => $"{Drive.UsedPercent:0}% used";

    public bool IsLow => Drive.FreePercent < 10;
}

/// <summary>Home screen: live system summary, drives, last scan and quick actions.</summary>
public sealed partial class OverviewViewModel : PageViewModel
{
    private readonly SystemMetricsService _metrics;
    private readonly NavigationService _navigation;
    private readonly AnalyzerSession _session;
    private readonly SnapshotStore _snapshots;
    private readonly DialogService _dialogs;
    private readonly DiskAnalyzerViewModel _analyzer;
    private readonly SettingsService _settings;
    private string _driveSignature = string.Empty;

    [ObservableProperty] private string _cpuText = "-";
    [ObservableProperty] private string _cpuDetail = "Measuring…";
    [ObservableProperty] private string _memoryText = "-";
    [ObservableProperty] private string _memoryDetail = string.Empty;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _gpuText = "-";
    [ObservableProperty] private string _gpuDetail = string.Empty;
    [ObservableProperty] private string? _gpuNote;
    [ObservableProperty] private bool _gpuAvailable;
    [ObservableProperty] private long _revision;
    [ObservableProperty] private string? _lastScanPath;
    [ObservableProperty] private string? _lastScanDetail;
    [ObservableProperty] private string? _lastScanSize;
    [ObservableProperty] private string? _lastSnapshotPath;
    [ObservableProperty] private string? _lastSnapshotDetail;
    [ObservableProperty] private string? _lastSnapshotSize;

    public OverviewViewModel(
        SystemMetricsService metrics,
        NavigationService navigation,
        AnalyzerSession session,
        SnapshotStore snapshots,
        DialogService dialogs,
        DiskAnalyzerViewModel analyzer,
        SettingsService settings)
    {
        _metrics = metrics;
        _navigation = navigation;
        _session = session;
        _snapshots = snapshots;
        _dialogs = dialogs;
        _analyzer = analyzer;
        _settings = settings;
        _metrics.Updated += (_, _) => OnMetrics();
        _session.Changed += (_, _) => RefreshLastScan();
        _snapshots.Changed += (_, _) => RefreshLastSnapshot();
        _settings.Changed += (_, _) =>
        {
            _driveSignature = string.Empty;
            RefreshLastScan();
            RefreshLastSnapshot();
        };
        RefreshLastScan();
        RefreshLastSnapshot();
    }

    public override string Title => "Overview";

    public override string Glyph => "";

    public SystemMetricsService Metrics => _metrics;

    public ObservableCollection<DriveCard> Drives { get; } = [];

    public bool HasLastScan => LastScanPath is not null;

    public bool HasLastSnapshot => LastSnapshotPath is not null;

    public string Greeting => $"{Environment.MachineName} · Windows {Environment.OSVersion.Version.Build} · {Environment.ProcessorCount} logical processors";

    public override void OnActivated() => RefreshLastSnapshot();

    [RelayCommand]
    private void ScanFolder()
    {
        string? folder = _dialogs.PickFolder(_settings.DefaultScanPath);
        if (folder is not null)
        {
            _navigation.NavigateTo<DiskAnalyzerViewModel>();
            _analyzer.ScanFolder(folder);
        }
    }

    [RelayCommand]
    private void AnalyzeDrive(DriveCard? drive)
    {
        if (drive is null)
        {
            return;
        }

        _navigation.NavigateTo<DiskAnalyzerViewModel>();
        _analyzer.ScanFolder(drive.Root);
    }

    [RelayCommand]
    private void OpenAnalyzer() => _navigation.NavigateTo<DiskAnalyzerViewModel>();

    [RelayCommand]
    private void OpenPerformance() => _navigation.NavigateTo<PerformanceViewModel>();

    [RelayCommand]
    private void OpenHistory() => _navigation.NavigateTo<HistoryViewModel>();

    [RelayCommand]
    private void OpenLargeFiles() => _navigation.NavigateTo<LargeFilesViewModel>();

    private void OnMetrics()
    {
        MetricsSnapshot? s = _metrics.Latest;
        if (s is null)
        {
            return;
        }

        Revision = _metrics.Revision;
        if (s.CpuPercent is { } cpu)
        {
            CpuText = $"{cpu:0}%";
            CpuDetail = $"{Environment.ProcessorCount} logical processors";
        }
        else
        {
            CpuDetail = s.CpuUnavailableReason ?? "Measuring…";
        }

        if (s.Memory is { } mem)
        {
            MemoryText = $"{mem.UsedPercent:0}%";
            MemoryPercent = mem.UsedPercent;
            MemoryDetail = $"{Format.Size((long)mem.UsedBytes)} of {Format.Size((long)mem.TotalBytes)} in use";
        }
        else
        {
            MemoryText = "n/a";
            MemoryDetail = "Memory status unavailable.";
        }

        if (s.Gpus.Count > 0)
        {
            GpuInfo gpu = s.Gpus[0];
            GpuAvailable = true;
            GpuText = gpu.UtilizationPercent is { } util ? $"{util:0}%" : "n/a";
            string temp = gpu.TemperatureCelsius is { } t ? $" · {t:0} °C" : string.Empty;
            GpuDetail = gpu.Name + temp + (s.Gpus.Count > 1 ? $" (+{s.Gpus.Count - 1} more)" : string.Empty);
            GpuNote = gpu.UtilizationPercent is null
                ? "Utilisation counters are not available on this system."
                : gpu.TemperatureCelsius is null ? gpu.TemperatureNote : null;
        }
        else
        {
            GpuAvailable = false;
            GpuText = "n/a";
            GpuDetail = "No GPU data";
            GpuNote = s.GpuUnavailableReason ?? "Detecting graphics adapters…";
        }

        string signature = string.Join('|', s.Drives.Select(d => $"{d.Name}:{d.FreeBytes / (64L * 1024 * 1024)}"));
        if (signature != _driveSignature)
        {
            _driveSignature = signature;
            Drives.Clear();
            foreach (DriveSample d in s.Drives)
            {
                Drives.Add(new DriveCard(d));
            }
        }
    }

    private void RefreshLastScan()
    {
        var info = _session.State.LastScan;
        if (info is null)
        {
            LastScanPath = null;
        }
        else
        {
            LastScanPath = info.RootPath;
            LastScanSize = Format.Size(info.TotalSize);
            LastScanDetail = $"{Format.Count(info.FileCount)} files · {Format.Count(info.DirectoryCount)} folders · {Format.Relative(info.CompletedUtc)}" +
                             (info.IssueCount > 0 ? $" · {Format.Count(info.IssueCount)} skipped or unreadable" : string.Empty);
        }

        OnPropertyChanged(nameof(HasLastScan));
    }

    private void RefreshLastSnapshot()
    {
        SnapshotSummary? latest = _snapshots.List().FirstOrDefault();
        if (latest is null)
        {
            LastSnapshotPath = null;
        }
        else
        {
            LastSnapshotPath = latest.RootPath;
            LastSnapshotSize = Format.Size(latest.TotalSize);
            LastSnapshotDetail = $"{Format.Date(latest.CreatedUtc)} · {Format.Relative(latest.CreatedUtc)}";
        }

        OnPropertyChanged(nameof(HasLastSnapshot));
    }
}
