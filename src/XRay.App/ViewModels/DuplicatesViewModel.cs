using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XRay.App.Controls;
using XRay.App.Services;
using XRay.Core.Duplicates;
using XRay.Core.Scanning;

namespace XRay.App.ViewModels;

/// <summary>One file inside a duplicate group, with its selection state.</summary>
public sealed class DuplicateFileRow : ObservableObject
{
    private readonly DuplicateGroupRow _group;
    private bool _isSelected;

    public DuplicateFileRow(DuplicateGroupRow group, DuplicateFile file, bool isProtected, bool hasRecycleBin, string searchRoot)
    {
        _group = group;
        File = file;
        IsProtected = isProtected;
        HasRecycleBin = hasRecycleBin;
        string relative = Path.GetRelativePath(searchRoot, file.Folder);
        // Show folders relative to the searched folder so the distinguishing part of the path stays visible.
        Folder = relative == "." ? @".\" : relative.StartsWith("..", StringComparison.Ordinal) ? file.Folder : @".\" + relative;
    }

    public DuplicateFile File { get; }

    public bool IsProtected { get; }

    public bool HasRecycleBin { get; }

    /// <summary>Files in protected locations or on drives without a Recycle Bin can only be revealed, not removed.</summary>
    public bool CanSelect => !IsProtected && HasRecycleBin;

    public string Name => File.Name;

    public string Folder { get; }

    public string FullPath => File.FullPath;

    public string ModifiedText => Format.Date(File.LastWriteTimeUtc);

    public string? Note => IsProtected ? "Protected location - kept"
        : !HasRecycleBin ? "No Recycle Bin on this drive - kept"
        : null;

    public bool HasNote => Note is not null;

    /// <summary>Bound two-way to the checkbox; every change goes through the guard in <see cref="TrySetSelected"/>.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!TrySetSelected(value))
            {
                _group.OnSelectionRefused(this);
            }
        }
    }

    /// <summary>
    /// Changes the selection unless that would select every copy in the group or the file cannot be removed.
    /// Returns false if the change was refused.
    /// </summary>
    public bool TrySetSelected(bool value)
    {
        if (value == _isSelected)
        {
            return true;
        }

        if (value && (!CanSelect || !_group.CanSelectAnother()))
        {
            OnPropertyChanged(nameof(IsSelected)); // re-sync a checkbox the user just clicked
            return false;
        }

        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
        _group.OnSelectionChanged();
        return true;
    }
}

/// <summary>A group of identical files.</summary>
public sealed partial class DuplicateGroupRow : ObservableObject
{
    private readonly Action _selectionChanged;
    private readonly Action<DuplicateFileRow> _selectionRefused;

    public DuplicateGroupRow(DuplicateGroup group, DuplicateCleaner cleaner, string searchRoot, Action selectionChanged, Action<DuplicateFileRow> selectionRefused)
    {
        Group = group;
        _selectionChanged = selectionChanged;
        _selectionRefused = selectionRefused;
        Files = new ObservableCollection<DuplicateFileRow>(group.Files.Select(f =>
            new DuplicateFileRow(this, f, cleaner.IsProtected(f.FullPath), DuplicateCleaner.HasRecycleBin(f.FullPath), searchRoot)));
        Swatch = CategoryPalette.BrushFor(group.Files[0].Node);
    }

    public DuplicateGroup Group { get; }

    public ObservableCollection<DuplicateFileRow> Files { get; }

    public System.Windows.Media.Brush Swatch { get; }

    public string Title => Group.Files[0].Name;

    public string Detail => $"{Files.Count} identical copies · {Format.Size(Group.FileSize)} each";

    public string WastedText => Format.Size(Group.FileSize * Math.Max(0, Files.Count - 1)) + " reclaimable";

    public int SelectedCount => Files.Count(f => f.IsSelected);

    public bool CanSelectAnother() => Files.Count(f => !f.IsSelected) > 1;

    public void OnSelectionRefused(DuplicateFileRow row) => _selectionRefused(row);

    public void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        _selectionChanged();
    }

    /// <summary>Selects every removable copy except the one chosen by <paramref name="keep"/>.</summary>
    public void SelectAllExcept(Func<IEnumerable<DuplicateFileRow>, DuplicateFileRow> keep)
    {
        foreach (DuplicateFileRow f in Files)
        {
            f.TrySetSelected(false);
        }

        // Prefer keeping a copy that cannot be removed anyway (protected or no Recycle Bin).
        DuplicateFileRow kept = Files.FirstOrDefault(f => !f.CanSelect) ?? keep(Files);
        foreach (DuplicateFileRow f in Files)
        {
            if (!ReferenceEquals(f, kept) && f.CanSelect)
            {
                f.TrySetSelected(true);
            }
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(WastedText));
        OnPropertyChanged(nameof(SelectedCount));
    }
}

/// <summary>
/// Duplicate finder. Files are compared by size and SHA-256 content hash. Removal is optional, always goes to the
/// Recycle Bin, needs explicit confirmation listing every path, and always keeps at least one copy per group.
/// </summary>
public sealed partial class DuplicatesViewModel : PageViewModel
{
    private const int MaxGroupsShown = 1500;
    private readonly IDirectoryScanner _scanner;
    private readonly DuplicateFinder _finder;
    private readonly DuplicateCleaner _cleaner;
    private readonly AnalyzerSession _session;
    private readonly DialogService _dialogs;
    private readonly ShellService _shell;
    private readonly SettingsService _settings;
    private readonly AppLifetime _lifetime;
    private readonly ILogger<DuplicatesViewModel> _logger;
    private CancellationTokenSource? _cts;
    private FileSystemNode? _tree;
    private DuplicateSearchResult? _last;
    private DuplicateOptions? _lastOptions;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(FindCommand))] private string _folderPath;
    [ObservableProperty] private string _minSizeMb = "1";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand), nameof(CancelCommand), nameof(RescanCommand), nameof(RecycleSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(ShowResults), nameof(ShowEmptyState))]
    private bool _isBusy;
    [ObservableProperty] private bool _isScanningTree;
    [ObservableProperty] private string _phaseText = string.Empty;
    [ObservableProperty] private string _phaseDetail = string.Empty;
    [ObservableProperty] private double _phasePercent;
    [ObservableProperty] private bool _phaseIndeterminate = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowResults), nameof(ShowEmptyState))] private bool _hasSearched;
    [ObservableProperty] private string? _summaryText;
    [ObservableProperty] private string? _sourceNote;
    [ObservableProperty] private string? _skippedNote;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _validationMessage;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RecycleSelectedCommand))] private int _selectedCount;
    [ObservableProperty] private string _selectedText = "Nothing selected";

    public DuplicatesViewModel(
        IDirectoryScanner scanner,
        DuplicateFinder finder,
        DuplicateCleaner cleaner,
        AnalyzerSession session,
        DialogService dialogs,
        ShellService shell,
        SettingsService settings,
        AppLifetime lifetime,
        ILogger<DuplicatesViewModel> logger)
    {
        _scanner = scanner;
        _finder = finder;
        _cleaner = cleaner;
        _session = session;
        _dialogs = dialogs;
        _shell = shell;
        _settings = settings;
        _lifetime = lifetime;
        _logger = logger;
        _folderPath = settings.DefaultScanPath;
    }

    public override string Title => "Duplicates";

    public override string Glyph => "";

    public ScanProgressState TreeProgress { get; } = new();

    public ObservableCollection<DuplicateGroupRow> Groups { get; } = [];

    public bool ShowResults => HasSearched && !IsBusy;

    public bool ShowEmptyState => !HasSearched && !IsBusy;

    public bool HasGroups => Groups.Count > 0;

    [RelayCommand]
    private void Browse()
    {
        string? folder = _dialogs.PickFolder(Directory.Exists(FolderPath) ? FolderPath : _settings.DefaultScanPath, "Select a folder to search for duplicates");
        if (folder is not null)
        {
            FolderPath = folder;
            _tree = null;
        }
    }

    private bool CanFind() => !IsBusy && !string.IsNullOrWhiteSpace(FolderPath);

    [RelayCommand(CanExecute = nameof(CanFind))]
    private Task FindAsync() => RunAsync(forceRescan: false);

    private bool CanRescan() => !IsBusy && _tree is not null;

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanAsync() => RunAsync(forceRescan: true);

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void KeepNewest() => SelectAll(files => files.MaxBy(f => f.File.LastWriteTimeUtc)!);

    [RelayCommand]
    private void KeepOldest() => SelectAll(files => files.MinBy(f => f.File.LastWriteTimeUtc)!);

    [RelayCommand]
    private void KeepShortestPath() => SelectAll(files => files.MinBy(f => f.FullPath.Length)!);

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (DuplicateGroupRow g in Groups)
        {
            foreach (DuplicateFileRow f in g.Files)
            {
                f.TrySetSelected(false);
            }
        }
    }

    private void OnSelectionRefused(DuplicateFileRow row) =>
        StatusMessage = row.CanSelect
            ? "At least one copy of every file is always kept, so the last unselected copy cannot be selected."
            : row.Note;

    [RelayCommand]
    private void Reveal(DuplicateFileRow? row)
    {
        if (row is not null)
        {
            _shell.RevealInExplorer(row.FullPath);
        }
    }

    [RelayCommand]
    private void CopyPath(DuplicateFileRow? row)
    {
        if (row is not null)
        {
            _shell.CopyText(row.FullPath);
            StatusMessage = "Path copied to the clipboard.";
        }
    }

    private bool CanRecycle() => !IsBusy && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanRecycle))]
    private void RecycleSelected()
    {
        List<DuplicateFileRow> selected = Groups.SelectMany(g => g.Files).Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var paths = selected.Select(f => f.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<DuplicateGroup> groups = Groups.Where(g => g.SelectedCount > 0).Select(g => g.Group).ToList();
        IReadOnlyList<string> problems = _cleaner.Validate(groups, paths);
        if (problems.Count > 0)
        {
            _dialogs.ShowError("Selection cannot be removed", "Fix these problems first. Nothing has been changed.", string.Join("\n", problems.Take(50)));
            return;
        }

        long bytes = selected.Sum(f => f.File.Size);
        var detail = new StringBuilder();
        foreach (DuplicateFileRow f in selected.Take(300))
        {
            detail.AppendLine(f.FullPath);
        }

        if (selected.Count > 300)
        {
            detail.AppendLine($"… and {selected.Count - 300} more");
        }

        bool confirmed = _dialogs.Confirm(
            $"Move {selected.Count} file(s) to the Recycle Bin?",
            $"These {selected.Count} duplicate file(s) ({Format.Size(bytes)}) will be moved to the Recycle Bin. " +
            "At least one identical copy of each stays where it is. You can restore them from the Recycle Bin until it is emptied. " +
            "Programs or shortcuts that use these exact paths may stop finding them.",
            "Move to Recycle Bin",
            detail.ToString(),
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        CleanupReport report = _cleaner.Clean(groups, paths);
        foreach (CleanupItemResult item in report.Items)
        {
            _logger.LogInformation("Duplicate cleanup {Outcome}: {Path}", item.Outcome, item.Path);
        }

        var recycled = report.Items.Where(i => i.Outcome == CleanupOutcome.Recycled).Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateGroupRow g in Groups.ToList())
        {
            foreach (DuplicateFileRow f in g.Files.Where(f => recycled.Contains(f.FullPath)).ToList())
            {
                g.Files.Remove(f);
            }

            if (g.Files.Count < 2)
            {
                Groups.Remove(g);
            }
            else
            {
                g.Refresh();
            }
        }

        UpdateSelection();
        UpdateSummary();
        StatusMessage = $"Moved {report.RecycledCount} file(s) ({Format.Size(report.RecycledBytes)}) to the Recycle Bin. Other screens show the old sizes until you rescan.";

        List<CleanupItemResult> notDone = report.NotRecycled.ToList();
        if (notDone.Count > 0)
        {
            _dialogs.ShowInfo(
                $"{notDone.Count} file(s) were left in place",
                "These files were not moved. Nothing was deleted permanently.",
                string.Join("\n", notDone.Take(100).Select(i => $"{i.Path}\n    {i.Message}")));
        }
    }

    private void SelectAll(Func<IEnumerable<DuplicateFileRow>, DuplicateFileRow> keep)
    {
        foreach (DuplicateGroupRow g in Groups)
        {
            g.SelectAllExcept(keep);
        }

        StatusMessage = null;
    }

    private async Task RunAsync(bool forceRescan)
    {
        string path = FolderPath.Trim().Trim('"');
        if (!Directory.Exists(path))
        {
            _dialogs.ShowError("Folder not found", $"The folder '{path}' does not exist or is not accessible.");
            return;
        }

        if (!double.TryParse(MinSizeMb.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double mb) || mb < 0)
        {
            ValidationMessage = "Enter a minimum size in MB, for example 1 or 0.1.";
            return;
        }

        ValidationMessage = null;
        StatusMessage = null;
        string root = DirectoryScanner.NormalizeRoot(path);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Stopping);
        IsBusy = true;
        try
        {
            // Reuse an existing tree (this page's or the Disk Analyzer's) unless a rescan was requested.
            bool reuseOwn = !forceRescan && _tree is not null && string.Equals(_tree.FullPath, root, StringComparison.OrdinalIgnoreCase);
            if (!reuseOwn)
            {
                if (!forceRescan && _session.FindCovering(root) is { } covered)
                {
                    _tree = covered.Node;
                    SourceNote = $"Folder structure from the Disk Analyzer scan ({Format.Relative(covered.Result.CompletedUtc)}). File contents are read now.";
                }
                else
                {
                    IsScanningTree = true;
                    PhaseText = "Reading folder structure…";
                    TreeProgress.Reset();
                    ScanResult scan = await _scanner.ScanAsync(root, null, new Progress<ScanProgress>(TreeProgress.Update), _cts.Token);
                    _tree = scan.Root;
                    SourceNote = scan.IsIncomplete
                        ? $"{Format.Count(scan.Count(ScanIssueKind.AccessDenied))} folder(s) could not be read and were not searched."
                        : null;
                }
            }

            IsScanningTree = false;
            PhaseText = "Comparing files…";
            PhaseIndeterminate = true;
            var progress = new Progress<DuplicateProgress>(p =>
            {
                PhaseText = p.Phase + "…";
                if (p.BytesTotal > 0)
                {
                    PhaseIndeterminate = false;
                    PhasePercent = 100.0 * p.BytesDone / p.BytesTotal;
                    PhaseDetail = $"{Format.Size(p.BytesDone)} of {Format.Size(p.BytesTotal)} read · {Format.Count(p.FilesDone)} of {Format.Count(p.FilesTotal)} files";
                }
                else if (p.FilesTotal > 0)
                {
                    PhaseIndeterminate = false;
                    PhasePercent = 100.0 * p.FilesDone / p.FilesTotal;
                    PhaseDetail = $"{Format.Count(p.FilesDone)} of {Format.Count(p.FilesTotal)} files with matching sizes";
                }
            });

            var options = new DuplicateOptions { MinimumSize = Math.Max(1, (long)(mb * 1024 * 1024)) };
            DuplicateSearchResult result = await _finder.FindAsync(_tree!, options, progress, _cts.Token);
            Load(result, options);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Search cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Duplicate search failed");
            _dialogs.ShowError("Search failed", $"X-RAY could not search '{root}'.", ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsScanningTree = false;
            _cts.Dispose();
            _cts = null;
            RescanCommand.NotifyCanExecuteChanged();
        }
    }

    private void Load(DuplicateSearchResult result, DuplicateOptions options)
    {
        _last = result;
        _lastOptions = options;
        Groups.Clear();
        foreach (DuplicateGroup g in result.Groups.Take(MaxGroupsShown))
        {
            Groups.Add(new DuplicateGroupRow(g, _cleaner, _tree!.FullPath, UpdateSelection, OnSelectionRefused));
        }

        HasSearched = true;
        UpdateSelection();
        UpdateSummary();

        var skipped = new List<string>();
        if (result.Count(DuplicateSkipReason.Unreadable) > 0)
        {
            skipped.Add($"{Format.Count(result.Count(DuplicateSkipReason.Unreadable))} file(s) could not be read (in use or access denied)");
        }

        if (result.Count(DuplicateSkipReason.CloudOrOffline) > 0)
        {
            skipped.Add($"{Format.Count(result.Count(DuplicateSkipReason.CloudOrOffline))} cloud-only file(s) were not downloaded for comparison");
        }

        if (result.Count(DuplicateSkipReason.HardLink) > 0)
        {
            skipped.Add($"{Format.Count(result.Count(DuplicateSkipReason.HardLink))} hard link(s) share storage already and are not counted");
        }

        SkippedNote = skipped.Count == 0 ? null : string.Join(" · ", skipped) + ".";
    }

    private void UpdateSummary()
    {
        OnPropertyChanged(nameof(HasGroups));
        if (_last is null || _tree is null)
        {
            SummaryText = null;
            return;
        }

        long wasted = Groups.Sum(g => g.Group.FileSize * Math.Max(0, g.Files.Count - 1));
        long files = Groups.Sum(g => (long)g.Files.Count - 1);
        string limit = _last.Groups.Count > Groups.Count && Groups.Count == MaxGroupsShown
            ? $" · showing the {Format.Count(MaxGroupsShown)} groups with most reclaimable space"
            : string.Empty;
        SummaryText = Groups.Count == 0
            ? $"No duplicates of at least {Format.Size(_lastOptions!.MinimumSize)} found in {_tree.FullPath}."
            : $"{Format.Count(Groups.Count)} {(Groups.Count == 1 ? "group" : "groups")} · {Format.Count(files)} extra {(files == 1 ? "copy" : "copies")} · {Format.Size(wasted)} reclaimable in {_tree.FullPath}{limit}";
    }

    private void UpdateSelection()
    {
        List<DuplicateFileRow> selected = Groups.SelectMany(g => g.Files).Where(f => f.IsSelected).ToList();
        SelectedCount = selected.Count;
        SelectedText = selected.Count == 0
            ? "Nothing selected"
            : $"{Format.Count(selected.Count)} selected · {Format.Size(selected.Sum(f => f.File.Size))}";
    }
}
