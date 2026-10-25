using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XRay.App.Services;
using XRay.Core.Classification;
using XRay.Core.History;
using XRay.Core.Scanning;

namespace XRay.App.ViewModels;

/// <summary>Which list is shown beside the treemap.</summary>
public enum AnalyzerListMode
{
    Contents,
    LargestFiles,
}

/// <summary>Disk Analyzer: scanning, treemap navigation, sorting and item details.</summary>
public sealed partial class DiskAnalyzerViewModel : PageViewModel
{
    private const int LargestFilesCount = 100;

    private readonly IDirectoryScanner _scanner;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly ShellService _shell;
    private readonly AnalyzerSession _session;
    private readonly SnapshotStore _snapshots;
    private readonly AppLifetime _lifetime;
    private readonly ILogger<DiskAnalyzerViewModel> _logger;
    private CancellationTokenSource? _scanCts;
    private bool _syncingSelection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _scanPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CancelCommand), nameof(SaveSnapshotCommand), nameof(RescanCommand))]
    [NotifyPropertyChangedFor(nameof(ShowResults), nameof(ShowWelcome))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ShowWelcome), nameof(ShowResults))]
    [NotifyCanExecuteChangedFor(nameof(SaveSnapshotCommand), nameof(RescanCommand))]
    private ScanResult? _result;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateUpCommand))]
    private FileSystemNode? _currentNode;

    [ObservableProperty]
    private FileSystemNode? _selectedNode;

    [ObservableProperty]
    private NodeRow? _selectedRow;

    [ObservableProperty]
    private NodeSortKey _sortKey = NodeSortKey.Size;

    [ObservableProperty]
    private bool _sortDescending = true;

    [ObservableProperty]
    private AnalyzerListMode _listMode = AnalyzerListMode.Contents;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _emptyMessage;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string? _limitationsText;

    [ObservableProperty]
    private string _itemCountText = string.Empty;

    public DiskAnalyzerViewModel(
        IDirectoryScanner scanner,
        SettingsService settings,
        DialogService dialogs,
        ShellService shell,
        AnalyzerSession session,
        SnapshotStore snapshots,
        AppLifetime lifetime,
        ILogger<DiskAnalyzerViewModel> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _dialogs = dialogs;
        _shell = shell;
        _session = session;
        _snapshots = snapshots;
        _lifetime = lifetime;
        _logger = logger;
        _scanPath = settings.DefaultScanPath;
        _settings.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(Metric));
            OnPropertyChanged(nameof(TreemapDepth));
            OnPropertyChanged(nameof(ShowLabels));
            OnPropertyChanged(nameof(Shading));
            OnPropertyChanged(nameof(MetricLabel));
            RefreshLists();
            UpdateDetails();
            UpdateSummary();
        };
    }

    public override string Title => "Disk Analyzer";

    public override string Glyph => "";

    public ScanProgressState Progress { get; } = new();

    public ObservableCollection<FileSystemNode> Breadcrumbs { get; } = [];

    public ObservableCollection<NodeRow> Items { get; } = [];

    public ObservableCollection<ScanIssue> IssueSamples { get; } = [];

    public ObservableCollection<LegendEntry> Legend { get; } =
        new(FileTypeClassifier.LegendOrder.Select(c => new LegendEntry(FileTypeClassifier.DisplayName(c), Controls.CategoryPalette.BrushFor(c))));

    public bool HasResult => Result is not null;

    public bool ShowWelcome => Result is null && !IsScanning;

    public bool ShowResults => Result is not null && !IsScanning;

    public SizeMetric Metric => _settings.Current.SizeMetric;

    public string MetricLabel => Metric == SizeMetric.Allocated ? "Size on disk (estimated)" : "Size (logical)";

    public int TreemapDepth => _settings.Current.TreemapDepth;

    public bool ShowLabels => _settings.Current.TreemapShowLabels;

    public bool Shading => _settings.Current.TreemapShading;

    public IReadOnlyList<string> RecentFolders => _session.State.RecentFolders;

    public bool SortBySize
    {
        get => SortKey == NodeSortKey.Size;
        set { if (value) { SortKey = NodeSortKey.Size; } }
    }

    public bool SortByName
    {
        get => SortKey == NodeSortKey.Name;
        set { if (value) { SortKey = NodeSortKey.Name; } }
    }

    public bool SortByFiles
    {
        get => SortKey == NodeSortKey.FileCount;
        set { if (value) { SortKey = NodeSortKey.FileCount; } }
    }

    public bool SortByModified
    {
        get => SortKey == NodeSortKey.Modified;
        set { if (value) { SortKey = NodeSortKey.Modified; } }
    }

    public bool ShowContents
    {
        get => ListMode == AnalyzerListMode.Contents;
        set { if (value) { ListMode = AnalyzerListMode.Contents; } }
    }

    public bool ShowLargestFiles
    {
        get => ListMode == AnalyzerListMode.LargestFiles;
        set { if (value) { ListMode = AnalyzerListMode.LargestFiles; } }
    }

    // ---- Details panel ----
    public FileSystemNode? DetailNode => SelectedNode ?? CurrentNode;

    public string DetailName => DetailNode?.Name ?? string.Empty;

    public string DetailPath => DetailNode?.FullPath ?? string.Empty;

    public string DetailKind => DetailNode is null ? string.Empty
        : DetailNode.IsReparsePoint ? "Link (symbolic link, junction or mount point)"
        : DetailNode.IsDirectory ? "Folder"
        : FileTypeClassifier.DisplayName(FileTypeClassifier.Classify(DetailNode.Extension)) +
          (DetailNode.Extension.Length > 0 ? $" ({DetailNode.Extension})" : string.Empty);

    public string DetailSize => DetailNode is null ? string.Empty
        : $"{Format.Size(DetailNode.Size)}  ({Core.Formatting.SizeFormatter.FormatExact(DetailNode.Size)})";

    public string DetailAllocated => DetailNode is null ? string.Empty : Format.Size(DetailNode.AllocatedSize);

    public string DetailContents => DetailNode is not { IsDirectory: true } d ? string.Empty
        : $"{Format.Count(d.FileCount)} files, {Format.Count(d.DirectoryCount)} folders";

    public string DetailModified => DetailNode is null ? string.Empty
        : Format.Date(DetailNode.LastWriteTimeUtc) + (DetailNode.IsDirectory ? "  (newest item)" : string.Empty);

    public string DetailShare
    {
        get
        {
            if (DetailNode?.Parent is not { } parent || parent.GetSize(Metric) <= 0)
            {
                return string.Empty;
            }

            return $"{100.0 * DetailNode.GetSize(Metric) / parent.GetSize(Metric):0.0}% of {parent.Name}";
        }
    }

    public string? DetailWarning => DetailNode is null ? null
        : DetailNode.IsReparsePoint ? "This is a link to another location. It was not followed, so its target's size is not included."
        : (DetailNode.Flags & NodeFlags.AccessDenied) != 0 ? "Windows denied access to this folder. Its contents are unknown and not included in any total."
        : (DetailNode.Flags & NodeFlags.Vanished) != 0 ? "This item was removed while the scan was running."
        : (DetailNode.Flags & NodeFlags.Error) != 0 ? "This folder could not be read completely."
        : (DetailNode.Flags & NodeFlags.IncompleteDescendants) != 0 ? "Some sub-folders could not be read, so the totals shown are lower bounds."
        : (DetailNode.Flags & NodeFlags.CloudOrOffline) != 0 ? "Cloud or offline file: its data may not be stored on this disk."
        : null;

    public bool HasDetailWarning => DetailWarning is not null;

    /// <summary>Opens the analyzer on a folder and starts scanning it (used by quick actions elsewhere).</summary>
    public void ScanFolder(string path)
    {
        ScanPath = path;
        if (ScanCommand.CanExecute(null))
        {
            ScanCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void Browse()
    {
        string? folder = _dialogs.PickFolder(Directory.Exists(ScanPath) ? ScanPath : _settings.DefaultScanPath);
        if (folder is not null)
        {
            ScanPath = folder;
            if (ScanCommand.CanExecute(null))
            {
                ScanCommand.Execute(null);
            }
        }
    }

    private bool CanScan() => !IsScanning && !string.IsNullOrWhiteSpace(ScanPath);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        string path = ScanPath.Trim().Trim('"');
        if (!Directory.Exists(path))
        {
            _dialogs.ShowError("Folder not found", $"The folder '{path}' does not exist or is not accessible. Choose another folder.");
            return;
        }

        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Stopping);
        IsScanning = true;
        StatusMessage = null;
        Progress.Reset();
        OnPropertyChanged(nameof(ShowWelcome));
        var progress = new Progress<ScanProgress>(Progress.Update);
        try
        {
            ScanResult result = await _scanner.ScanAsync(path, null, progress, _scanCts.Token);
            Load(result);
            _session.Publish(result);
            OnPropertyChanged(nameof(RecentFolders));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Result is null
                ? "Scan cancelled."
                : $"Scan cancelled. Showing the previous results for {Result.Root.FullPath}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Scan failed");
            _dialogs.ShowError("Scan failed", $"X-RAY could not scan '{path}'.", ex.Message);
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(ShowWelcome));
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    private bool CanCancel() => IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _scanCts?.Cancel();

    private bool CanRescan() => !IsScanning && Result is not null;

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private void Rescan()
    {
        ScanPath = Result!.Root.FullPath;
        ScanCommand.Execute(null);
    }

    [RelayCommand]
    private void NavigateTo(FileSystemNode? node)
    {
        if (node is null)
        {
            return;
        }

        if (!node.IsDirectory)
        {
            SelectedNode = node;
            return;
        }

        CurrentNode = node;
    }

    private bool CanNavigateUp() => CurrentNode?.Parent is not null;

    [RelayCommand(CanExecute = nameof(CanNavigateUp))]
    private void NavigateUp()
    {
        if (CurrentNode?.Parent is { } parent)
        {
            FileSystemNode previous = CurrentNode;
            CurrentNode = parent;
            SelectedNode = previous;
        }
    }

    [RelayCommand]
    private void OpenRow(NodeRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsDirectory)
        {
            CurrentNode = row.Node;
        }
        else if (ListMode == AnalyzerListMode.LargestFiles && row.Node.Parent is { } parent)
        {
            // Show the file in context.
            ListMode = AnalyzerListMode.Contents;
            CurrentNode = parent;
            SelectedNode = row.Node;
        }
    }

    [RelayCommand]
    private void RevealInExplorer(FileSystemNode? node)
    {
        node ??= DetailNode;
        if (node is not null)
        {
            _shell.RevealInExplorer(node.FullPath);
        }
    }

    [RelayCommand]
    private void CopyPath(FileSystemNode? node)
    {
        node ??= DetailNode;
        if (node is not null)
        {
            _shell.CopyText(node.FullPath);
            StatusMessage = "Path copied to the clipboard.";
        }
    }

    [RelayCommand]
    private void ScanRecent(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            ScanFolder(path);
        }
    }

    private bool CanSaveSnapshot() => Result is not null && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanSaveSnapshot))]
    private void SaveSnapshot()
    {
        if (Result is null)
        {
            return;
        }

        try
        {
            StorageSnapshot snapshot = SnapshotBuilder.Create(Result, _settings.Current.SnapshotDepth);
            _snapshots.Save(snapshot);
            StatusMessage = $"Snapshot of {Result.Root.FullPath} saved. Compare snapshots on the History page.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Snapshot not saved", "The snapshot could not be written to disk.", ex.Message);
        }
    }

    partial void OnCurrentNodeChanged(FileSystemNode? value)
    {
        Breadcrumbs.Clear();
        if (value is not null)
        {
            foreach (FileSystemNode n in value.GetAncestry())
            {
                Breadcrumbs.Add(n);
            }
        }

        SelectedNode = null;
        RefreshLists();
        UpdateDetails();
    }

    partial void OnSelectedNodeChanged(FileSystemNode? value)
    {
        if (!_syncingSelection)
        {
            _syncingSelection = true;
            SelectedRow = value is null ? null : Items.FirstOrDefault(r => ReferenceEquals(r.Node, value));
            _syncingSelection = false;
        }

        UpdateDetails();
    }

    partial void OnSelectedRowChanged(NodeRow? value)
    {
        if (_syncingSelection)
        {
            return;
        }

        _syncingSelection = true;
        SelectedNode = value?.Node;
        _syncingSelection = false;
        UpdateDetails();
    }

    partial void OnSortKeyChanged(NodeSortKey value)
    {
        OnPropertyChanged(nameof(SortBySize));
        OnPropertyChanged(nameof(SortByName));
        OnPropertyChanged(nameof(SortByFiles));
        OnPropertyChanged(nameof(SortByModified));
        // Names read naturally A-Z; sizes, counts and dates largest/newest first.
        SortDescending = value != NodeSortKey.Name;
        RefreshLists();
    }

    partial void OnSortDescendingChanged(bool value) => RefreshLists();

    partial void OnListModeChanged(AnalyzerListMode value)
    {
        OnPropertyChanged(nameof(ShowContents));
        OnPropertyChanged(nameof(ShowLargestFiles));
        RefreshLists();
    }

    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    private void Load(ScanResult result)
    {
        Result = result;
        IssueSamples.Clear();
        foreach (ScanIssue issue in result.Issues.Where(i => i.Kind != ScanIssueKind.ReparsePointSkipped).Take(200))
        {
            IssueSamples.Add(issue);
        }

        CurrentNode = result.Root;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (Result is not { } r)
        {
            SummaryText = string.Empty;
            LimitationsText = null;
            return;
        }

        SummaryText = $"{Format.Size(r.Root.GetSize(Metric))} in {Format.Count(r.Root.FileCount)} files and {Format.Count(r.Root.DirectoryCount)} folders · scanned in {Format.Duration(r.Elapsed)} · {Format.Relative(r.CompletedUtc)}";

        var notes = new List<string>();
        long denied = r.Count(ScanIssueKind.AccessDenied);
        long vanished = r.Count(ScanIssueKind.Vanished);
        long errors = r.Count(ScanIssueKind.IoError) + r.Count(ScanIssueKind.PathTooLong);
        long links = r.Count(ScanIssueKind.ReparsePointSkipped);
        if (denied > 0)
        {
            notes.Add($"{Format.Count(denied)} folder(s) could not be read (access denied); their contents are not counted.");
        }

        if (vanished > 0)
        {
            notes.Add($"{Format.Count(vanished)} item(s) disappeared during the scan.");
        }

        if (errors > 0)
        {
            notes.Add($"{Format.Count(errors)} folder(s) had read errors.");
        }

        if (links > 0)
        {
            notes.Add($"{Format.Count(links)} link(s) (junctions, symbolic links or mount points) were not followed to avoid double counting.");
        }

        if (Metric == SizeMetric.Allocated)
        {
            notes.Add($"Size on disk is estimated from the {Format.Size(r.ClusterSize)} cluster size; compressed and sparse files use their actual allocation.");
        }

        LimitationsText = notes.Count == 0 ? null : string.Join("\n", notes);
    }

    private void RefreshLists()
    {
        Items.Clear();
        EmptyMessage = null;
        FileSystemNode? current = CurrentNode;
        if (current is null)
        {
            ItemCountText = string.Empty;
            return;
        }

        if (ListMode == AnalyzerListMode.LargestFiles)
        {
            List<FileSystemNode> largest = TreeQueries.LargestFiles(current, LargestFilesCount, Metric);
            foreach (FileSystemNode n in largest)
            {
                Items.Add(new NodeRow(n, Metric, current.GetSize(Metric)));
            }

            ItemCountText = $"Top {largest.Count} files in this folder and its sub-folders";
            if (largest.Count == 0)
            {
                EmptyMessage = "No files in this folder or its sub-folders.";
            }
        }
        else
        {
            foreach (FileSystemNode n in TreeQueries.Sort(current.Children, SortKey, SortDescending, Metric))
            {
                Items.Add(new NodeRow(n, Metric, current.GetSize(Metric)));
            }

            ItemCountText = $"{Format.Count(current.Children.Count)} items";
        }

        if (current.IsUnreadable)
        {
            EmptyMessage = (current.Flags & NodeFlags.AccessDenied) != 0
                ? "Windows denied access to this folder. Its contents are unknown - it is not empty. Running X-RAY as administrator may allow reading it."
                : "This folder could not be read completely.";
        }
        else if (current.IsReparsePoint)
        {
            EmptyMessage = "This is a link to another location and was not followed.";
        }
        else if (current.Children.Count == 0)
        {
            EmptyMessage = "This folder is empty.";
        }
        else if (current.GetSize(Metric) == 0 && ListMode == AnalyzerListMode.Contents)
        {
            EmptyMessage = "This folder only contains empty files and folders, so there is nothing to draw.";
        }

        if (SelectedNode is not null)
        {
            _syncingSelection = true;
            SelectedRow = Items.FirstOrDefault(r => ReferenceEquals(r.Node, SelectedNode));
            _syncingSelection = false;
        }
    }

    private void UpdateDetails()
    {
        foreach (string p in new[]
                 {
                     nameof(DetailNode), nameof(DetailName), nameof(DetailPath), nameof(DetailKind), nameof(DetailSize),
                     nameof(DetailAllocated), nameof(DetailContents), nameof(DetailModified), nameof(DetailShare),
                     nameof(DetailWarning), nameof(HasDetailWarning),
                 })
        {
            OnPropertyChanged(p);
        }
    }
}

/// <summary>One entry of the treemap colour legend.</summary>
public sealed record LegendEntry(string Name, System.Windows.Media.Brush Brush);
