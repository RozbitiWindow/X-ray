using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XRay.App.Services;
using XRay.Core.History;
using XRay.Core.Scanning;

namespace XRay.App.ViewModels;

public sealed class SnapshotItem(SnapshotSummary summary)
{
    public SnapshotSummary Summary { get; } = summary;

    public string RootPath => Summary.RootPath;

    // Seconds included so snapshots taken in the same minute remain distinguishable.
    public string CreatedText => Summary.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public string RelativeText => Format.Relative(Summary.CreatedUtc);

    public string SizeText => Format.Size(Summary.TotalSize);

    public string Detail => $"{Format.Count(Summary.FileCount)} files · {Format.Count(Summary.DirectoryCount)} folders · depth {Summary.MaxDepth}" +
                            (Summary.UnreadableDirectoryCount > 0 ? $" · {Format.Count(Summary.UnreadableDirectoryCount)} unreadable" : string.Empty);

    public string Display => $"{CreatedText}  ·  {RootPath}  ·  {SizeText}";
}

public sealed class DiffRow(SnapshotDiffEntry entry)
{
    public SnapshotDiffEntry Entry { get; } = entry;

    public string Path => Entry.Path.Length == 0 ? "(root folder)" : Entry.Path;

    public string OldText => Entry.OldSize is { } o ? Format.Size(o) : "-";

    public string NewText => Entry.NewSize is { } n ? Format.Size(n) : "-";

    public string DeltaText => Entry.IsMeasured ? Format.Delta(Entry.Delta) : "?";

    public string DeltaExact => Entry.IsMeasured ? $"{Entry.Delta:+#,0;-#,0;0} bytes" : Entry.Note ?? "Not comparable";

    public string KindText => Entry.Kind switch
    {
        ChangeKind.Grown => "Grown",
        ChangeKind.Shrunk => "Shrunk",
        ChangeKind.Added => "New",
        ChangeKind.Removed => "Removed",
        ChangeKind.Unchanged => "Unchanged",
        _ => "Not comparable",
    };

    public string? Note => Entry.Note;

    public ChangeKind Kind => Entry.Kind;
}

public sealed record DiffFilter(string Label, Func<SnapshotDiffEntry, bool> Predicate);

/// <summary>Storage history: create snapshots, compare two of them, delete with confirmation.</summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    private const int MaxDiffRows = 3000;
    private readonly SnapshotStore _store;
    private readonly IDirectoryScanner _scanner;
    private readonly AnalyzerSession _session;
    private readonly DialogService _dialogs;
    private readonly ShellService _shell;
    private readonly SettingsService _settings;
    private readonly AppLifetime _lifetime;
    private readonly ILogger<HistoryViewModel> _logger;
    private CancellationTokenSource? _cts;
    private SnapshotComparison? _comparison;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CreateSnapshotCommand))] private string _snapshotPath;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CreateSnapshotCommand), nameof(CancelCommand))] private bool _isScanning;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CompareCommand))] private SnapshotItem? _baseline;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CompareCommand))] private SnapshotItem? _target;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DeleteSnapshotCommand))] private SnapshotItem? _selectedSnapshot;
    [ObservableProperty] private DiffFilter _selectedFilter;
    [ObservableProperty] private int _maxDepth = 2;
    [ObservableProperty] private string? _comparisonTitle;
    [ObservableProperty] private string? _comparisonSummary;
    [ObservableProperty] private string? _comparisonWarnings;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _diffCountText;

    public HistoryViewModel(
        SnapshotStore store,
        IDirectoryScanner scanner,
        AnalyzerSession session,
        DialogService dialogs,
        ShellService shell,
        SettingsService settings,
        AppLifetime lifetime,
        ILogger<HistoryViewModel> logger)
    {
        _store = store;
        _scanner = scanner;
        _session = session;
        _dialogs = dialogs;
        _shell = shell;
        _settings = settings;
        _lifetime = lifetime;
        _logger = logger;
        _snapshotPath = settings.DefaultScanPath;
        _selectedFilter = Filters[0];
        _store.Changed += (_, _) => Reload();
        _settings.Changed += (_, _) => ApplyFilter();
        Reload();
    }

    public override string Title => "History";

    public override string Glyph => "";

    public ScanProgressState Progress { get; } = new();

    public ObservableCollection<SnapshotItem> Snapshots { get; } = [];

    public ObservableCollection<DiffRow> Diff { get; } = [];

    public IReadOnlyList<DiffFilter> Filters { get; } =
    [
        new("All changes", e => e.Kind != ChangeKind.Unchanged),
        new("Grown or new", e => e.Kind is ChangeKind.Grown or ChangeKind.Added),
        new("Shrunk or removed", e => e.Kind is ChangeKind.Shrunk or ChangeKind.Removed),
        new("Not comparable", e => e.Kind == ChangeKind.NotComparable),
        new("Everything", _ => true),
    ];

    public IReadOnlyList<int> DepthOptions { get; } = [1, 2, 3, 4, 6, 12];

    public bool HasSnapshots => Snapshots.Count > 0;

    public bool HasComparison => _comparison is not null;

    public string ScopeHint => $"Snapshots record every folder down to {_settings.Current.SnapshotDepth} levels below the chosen folder. Files themselves are not stored.";

    public override void OnActivated()
    {
        OnPropertyChanged(nameof(ScopeHint));
        if (_session.Current is { } current && string.IsNullOrWhiteSpace(SnapshotPath))
        {
            SnapshotPath = current.Root.FullPath;
        }
    }

    [RelayCommand]
    private void Browse()
    {
        string? folder = _dialogs.PickFolder(Directory.Exists(SnapshotPath) ? SnapshotPath : _settings.DefaultScanPath, "Select a folder to snapshot");
        if (folder is not null)
        {
            SnapshotPath = folder;
        }
    }

    private bool CanCreate() => !IsScanning && !string.IsNullOrWhiteSpace(SnapshotPath);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateSnapshotAsync()
    {
        string path = SnapshotPath.Trim().Trim('"');
        if (!Directory.Exists(path))
        {
            _dialogs.ShowError("Folder not found", $"The folder '{path}' does not exist or is not accessible.");
            return;
        }

        string root = DirectoryScanner.NormalizeRoot(path);
        StatusMessage = null;
        ScanResult result;

        // A snapshot of exactly the folder the Disk Analyzer just scanned can reuse that scan.
        if (_session.Current is { } current &&
            string.Equals(current.Root.FullPath, root, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow - current.CompletedUtc < TimeSpan.FromMinutes(10))
        {
            result = current;
        }
        else
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Stopping);
            IsScanning = true;
            Progress.Reset();
            try
            {
                result = await _scanner.ScanAsync(root, null, new Progress<ScanProgress>(Progress.Update), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Snapshot cancelled. Nothing was saved.";
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(ex, "Snapshot scan failed");
                _dialogs.ShowError("Snapshot failed", $"X-RAY could not scan '{root}'.", ex.Message);
                return;
            }
            finally
            {
                IsScanning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        try
        {
            StorageSnapshot snapshot = SnapshotBuilder.Create(result, _settings.Current.SnapshotDepth);
            _store.Save(snapshot);
            StatusMessage = $"Snapshot saved: {Format.Size(snapshot.Summary.TotalSize)} in {root}" +
                            (ReferenceEquals(result, _session.Current) ? " (from the latest Disk Analyzer scan)." : ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Snapshot not saved", "The snapshot could not be written to disk.", ex.Message);
        }
    }

    private bool CanCancel() => IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private bool CanCompare() => Baseline is not null && Target is not null && !ReferenceEquals(Baseline, Target);

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private void Compare()
    {
        StorageSnapshot? a = _store.Load(Baseline!.Summary.Id);
        StorageSnapshot? b = _store.Load(Target!.Summary.Id);
        if (a is null || b is null)
        {
            _dialogs.ShowError("Snapshot unreadable", "One of the snapshots could not be loaded. The file may be damaged or was removed.");
            Reload();
            return;
        }

        _comparison = SnapshotComparer.Compare(a, b, _settings.Current.SizeMetric);
        ComparisonTitle = $"{Format.Date(_comparison.Older.CreatedUtc)}  →  {Format.Date(_comparison.Newer.CreatedUtc)}";
        TimeSpan span = _comparison.Newer.CreatedUtc - _comparison.Older.CreatedUtc;
        ComparisonSummary = _comparison.TotalDelta is { } total
            ? $"{_comparison.Older.RootPath}: {Format.Delta(total)} over {Format.Duration(span)} ({Format.Size(MetricSize(_comparison.Older))} → {Format.Size(MetricSize(_comparison.Newer))}) · " +
              $"{_comparison.Count(ChangeKind.Grown)} grown, {_comparison.Count(ChangeKind.Shrunk)} shrunk, {_comparison.Count(ChangeKind.Added)} new, {_comparison.Count(ChangeKind.Removed)} removed, {_comparison.Count(ChangeKind.NotComparable)} not comparable"
            : "These snapshots cover different folders, so no change can be measured.";
        ComparisonWarnings = _comparison.Warnings.Count == 0 ? null : string.Join("\n", _comparison.Warnings);
        OnPropertyChanged(nameof(HasComparison));
        ApplyFilter();
    }

    private bool CanDelete() => SelectedSnapshot is not null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteSnapshot()
    {
        if (SelectedSnapshot is not { } item)
        {
            return;
        }

        bool confirmed = _dialogs.Confirm(
            "Delete this snapshot?",
            "The saved size record will be permanently removed from X-RAY's history. Your files and folders are not affected.",
            "Delete snapshot",
            $"Folder: {item.RootPath}\nCreated: {item.CreatedText}\nRecorded size: {item.SizeText}",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        _store.Delete(item.Summary.Id);
        StatusMessage = "Snapshot deleted.";
    }

    [RelayCommand]
    private void OpenSnapshotFolder() => _shell.OpenFolder(_store.Directory);

    partial void OnSelectedFilterChanged(DiffFilter value) => ApplyFilter();

    partial void OnMaxDepthChanged(int value) => ApplyFilter();

    private long MetricSize(SnapshotSummary s) => _settings.Current.SizeMetric == SizeMetric.Allocated ? s.TotalAllocatedSize : s.TotalSize;

    private void ApplyFilter()
    {
        Diff.Clear();
        if (_comparison is null)
        {
            DiffCountText = null;
            return;
        }

        int total = 0;
        foreach (SnapshotDiffEntry e in _comparison.Entries)
        {
            if (e.Depth > MaxDepth || !SelectedFilter.Predicate(e))
            {
                continue;
            }

            total++;
            if (Diff.Count < MaxDiffRows)
            {
                Diff.Add(new DiffRow(e));
            }
        }

        DiffCountText = total > Diff.Count
            ? $"{Format.Count(total)} folders · showing the {Format.Count(Diff.Count)} largest changes"
            : $"{Format.Count(total)} folders";
    }

    private void Reload()
    {
        Guid? baseline = Baseline?.Summary.Id, target = Target?.Summary.Id, selected = SelectedSnapshot?.Summary.Id;
        Snapshots.Clear();
        foreach (SnapshotSummary s in _store.List())
        {
            Snapshots.Add(new SnapshotItem(s));
        }

        Baseline = Snapshots.FirstOrDefault(s => s.Summary.Id == baseline);
        Target = Snapshots.FirstOrDefault(s => s.Summary.Id == target);
        SelectedSnapshot = Snapshots.FirstOrDefault(s => s.Summary.Id == selected);

        // Sensible defaults: compare the two most recent snapshots of the same folder.
        if (Baseline is null && Target is null && Snapshots.Count >= 2)
        {
            Target = Snapshots[0];
            Baseline = Snapshots.Skip(1).FirstOrDefault(s => string.Equals(s.RootPath, Target.RootPath, StringComparison.OrdinalIgnoreCase)) ?? Snapshots[1];
        }

        if (_comparison is not null && (Snapshots.All(s => s.Summary.Id != _comparison.Older.Id) || Snapshots.All(s => s.Summary.Id != _comparison.Newer.Id)))
        {
            _comparison = null;
            ComparisonTitle = ComparisonSummary = ComparisonWarnings = null;
            OnPropertyChanged(nameof(HasComparison));
            ApplyFilter();
        }

        OnPropertyChanged(nameof(HasSnapshots));
    }
}
