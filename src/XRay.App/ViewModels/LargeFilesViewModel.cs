using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XRay.App.Controls;
using XRay.App.Services;
using XRay.Core.LargeFiles;
using XRay.Core.Scanning;

namespace XRay.App.ViewModels;

public sealed record AgeOption(string Label, int? Days);

public sealed record CategoryOption(string Label, ReviewCategory? Category, bool OnlySuggested);

public sealed record SortOption(string Label, LargeFileSort Sort);

public sealed class LargeFileRow(LargeFileMatch match)
{
    public LargeFileMatch Match { get; } = match;

    public string Name => Match.Name;

    public string Folder => Match.Folder;

    public string FullPath => Match.FullPath;

    public string SizeText => Format.Size(Match.Size);

    public string ModifiedText => Format.Date(Match.LastWriteTimeUtc);

    public string AgeText
    {
        get
        {
            double days = (DateTime.UtcNow - Match.LastWriteTimeUtc).TotalDays;
            return days < 1 ? "today" : days < 60 ? $"{days:0} days" : days < 730 ? $"{days / 30.4:0} months" : $"{days / 365.25:0.#} years";
        }
    }

    public bool HasSuggestion => Match.Suggestion.IsSuggested;

    public string CategoryText => ReviewHeuristics.DisplayName(Match.Suggestion.Category);

    public string Reason => Match.Suggestion.Reason;

    public System.Windows.Media.Brush Swatch => CategoryPalette.BrushFor(Match.Node);
}

/// <summary>Large Files and review suggestions ("junk radar"). Read-only: nothing is ever deleted.</summary>
public sealed partial class LargeFilesViewModel : PageViewModel
{
    private const int MaxResults = 5000;
    private readonly IDirectoryScanner _scanner;
    private readonly AnalyzerSession _session;
    private readonly DialogService _dialogs;
    private readonly ShellService _shell;
    private readonly SettingsService _settings;
    private readonly AppLifetime _lifetime;
    private readonly ILogger<LargeFilesViewModel> _logger;
    private CancellationTokenSource? _cts;
    private FileSystemNode? _tree;
    private DateTime _treeTimeUtc;
    private List<LargeFileMatch> _matches = [];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SearchCommand))] private string _folderPath;
    [ObservableProperty] private string _minSizeMb = "100";
    [ObservableProperty] private string _extensionsText = string.Empty;
    [ObservableProperty] private AgeOption _selectedAge;
    [ObservableProperty] private CategoryOption _selectedCategory;
    [ObservableProperty] private SortOption _selectedSort;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(CancelCommand), nameof(RescanCommand))]
    [NotifyPropertyChangedFor(nameof(ShowResults), nameof(ShowEmptyState))]
    private bool _isSearching;
    [ObservableProperty] private LargeFileRow? _selectedResult;
    [ObservableProperty] private string? _scopeText;
    [ObservableProperty] private string? _resultSummary;
    [ObservableProperty] private string? _sourceNote;
    [ObservableProperty] private string? _validationMessage;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowResults), nameof(ShowEmptyState))] private bool _hasSearched;

    public LargeFilesViewModel(
        IDirectoryScanner scanner,
        AnalyzerSession session,
        DialogService dialogs,
        ShellService shell,
        SettingsService settings,
        AppLifetime lifetime,
        ILogger<LargeFilesViewModel> logger)
    {
        _scanner = scanner;
        _session = session;
        _dialogs = dialogs;
        _shell = shell;
        _settings = settings;
        _lifetime = lifetime;
        _logger = logger;
        _folderPath = settings.DefaultScanPath;
        _selectedAge = AgeOptions[0];
        _selectedCategory = CategoryOptions[0];
        _selectedSort = SortOptions[0];
        _settings.Changed += (_, _) => ApplyFilters();
    }

    public override string Title => "Large Files";

    public override string Glyph => "";

    public ScanProgressState Progress { get; } = new();

    public ObservableCollection<LargeFileRow> Results { get; } = [];

    public IReadOnlyList<AgeOption> AgeOptions { get; } =
    [
        new("Any age", null),
        new("Older than 30 days", 30),
        new("Older than 90 days", 90),
        new("Older than 6 months", 182),
        new("Older than 1 year", 365),
        new("Older than 2 years", 730),
    ];

    public IReadOnlyList<CategoryOption> CategoryOptions { get; } =
    [
        new("All files", null, false),
        new("Any review suggestion", null, true),
        new("Installers", ReviewCategory.Installer, false),
        new("Archives", ReviewCategory.Archive, false),
        new("Disk images", ReviewCategory.DiskImage, false),
        new("Build output", ReviewCategory.BuildOutput, false),
        new("Temporary files", ReviewCategory.Temporary, false),
        new("Large logs", ReviewCategory.LargeLog, false),
        new("Crash dumps", ReviewCategory.CrashDump, false),
        new("Backup copies", ReviewCategory.Backup, false),
    ];

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new("Largest first", LargeFileSort.SizeDescending),
        new("Smallest first", LargeFileSort.SizeAscending),
        new("Oldest first", LargeFileSort.OldestFirst),
        new("Newest first", LargeFileSort.NewestFirst),
        new("Name", LargeFileSort.Name),
    ];

    public bool HasResults => Results.Count > 0;

    public bool ShowResults => HasSearched && !IsSearching;

    public bool ShowEmptyState => !HasSearched && !IsSearching;

    [RelayCommand]
    private void Browse()
    {
        string? folder = _dialogs.PickFolder(Directory.Exists(FolderPath) ? FolderPath : _settings.DefaultScanPath, "Select a folder to search");
        if (folder is not null)
        {
            FolderPath = folder;
            _tree = null;
        }
    }

    private bool CanSearch() => !IsSearching && !string.IsNullOrWhiteSpace(FolderPath);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync() => RunSearchAsync(forceRescan: false);

    private bool CanRescan() => !IsSearching && _tree is not null;

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanAsync() => RunSearchAsync(forceRescan: true);

    private bool CanCancel() => IsSearching;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Reveal(LargeFileRow? row)
    {
        row ??= SelectedResult;
        if (row is not null)
        {
            _shell.RevealInExplorer(row.FullPath);
        }
    }

    [RelayCommand]
    private void CopyPath(LargeFileRow? row)
    {
        row ??= SelectedResult;
        if (row is not null)
        {
            _shell.CopyText(row.FullPath);
        }
    }

    partial void OnSelectedAgeChanged(AgeOption value) => ApplyFilters();

    partial void OnSelectedCategoryChanged(CategoryOption value) => ApplyFilters();

    partial void OnSelectedSortChanged(SortOption value) => ApplySort();

    partial void OnFolderPathChanged(string value)
    {
        RescanCommand.NotifyCanExecuteChanged();
    }

    private async Task RunSearchAsync(bool forceRescan)
    {
        string path = FolderPath.Trim().Trim('"');
        if (!Directory.Exists(path))
        {
            _dialogs.ShowError("Folder not found", $"The folder '{path}' does not exist or is not accessible.");
            return;
        }

        string normalized = DirectoryScanner.NormalizeRoot(path);
        bool sameTree = _tree is not null && string.Equals(_tree.FullPath, normalized, StringComparison.OrdinalIgnoreCase);
        if (!forceRescan && sameTree)
        {
            ApplyFilters();
            return;
        }

        // Reuse the Disk Analyzer's in-memory tree when it already covers this folder: no second traversal.
        if (!forceRescan && _session.FindCovering(normalized) is { } covered)
        {
            _tree = covered.Node;
            _treeTimeUtc = covered.Result.CompletedUtc;
            SourceNote = $"Using the Disk Analyzer scan from {Format.Relative(_treeTimeUtc)}. Use Rescan for fresh data.";
            ApplyFilters();
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Stopping);
        IsSearching = true;
        Progress.Reset();
        try
        {
            ScanResult result = await _scanner.ScanAsync(normalized, null, new Progress<ScanProgress>(Progress.Update), _cts.Token);
            _tree = result.Root;
            _treeTimeUtc = result.CompletedUtc;
            SourceNote = result.IsIncomplete
                ? $"Scanned {Format.Relative(_treeTimeUtc)}. {Format.Count(result.Count(ScanIssueKind.AccessDenied))} folder(s) could not be read and were not searched."
                : $"Scanned {Format.Relative(_treeTimeUtc)}.";
            ApplyFilters();
        }
        catch (OperationCanceledException)
        {
            SourceNote = "Search cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Large file search failed");
            _dialogs.ShowError("Search failed", $"X-RAY could not search '{normalized}'.", ex.Message);
        }
        finally
        {
            IsSearching = false;
            _cts.Dispose();
            _cts = null;
            RescanCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void ApplyFilters()
    {
        ValidationMessage = null;
        if (_tree is null)
        {
            return;
        }

        if (!double.TryParse(MinSizeMb.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double mb) || mb < 0)
        {
            ValidationMessage = "Enter a minimum size in MB, for example 100 or 0.5.";
            return;
        }

        var query = new LargeFileQuery
        {
            MinimumSize = (long)(mb * 1024 * 1024),
            Extensions = LargeFileQuery.ParseExtensions(ExtensionsText),
            ModifiedBeforeUtc = SelectedAge.Days is { } days ? DateTime.UtcNow.AddDays(-days) : null,
            Category = SelectedCategory.Category,
            OnlySuggested = SelectedCategory.OnlySuggested,
            Metric = _settings.Current.SizeMetric,
            MaxResults = MaxResults,
        };

        LargeFileSearchResult result = LargeFileFinder.Find(_tree, query);
        _matches = result.Matches.ToList();
        HasSearched = true;

        string scope = $"{_tree.FullPath} and all sub-folders";
        var filters = new List<string> { $"at least {Format.Size(query.MinimumSize)}" };
        if (query.Extensions is { Count: > 0 } exts)
        {
            filters.Add("types " + string.Join(", ", exts));
        }

        if (SelectedAge.Days is not null)
        {
            filters.Add(SelectedAge.Label.ToLowerInvariant());
        }

        if (SelectedCategory.Category is not null || SelectedCategory.OnlySuggested)
        {
            filters.Add(SelectedCategory.Label.ToLowerInvariant());
        }

        ScopeText = $"Searching {scope} · {string.Join(" · ", filters)} · links and junctions not followed";
        ResultSummary = result.TotalMatches == 0
            ? "No files match these filters."
            : $"{Format.Count(result.TotalMatches)} files · {Format.Size(result.TotalMatchedSize)} total" +
              (result.IsTruncated ? $" · showing the largest {Format.Count(result.Matches.Count)}" : string.Empty);
        ApplySort();
    }

    private void ApplySort()
    {
        Results.Clear();
        foreach (LargeFileMatch m in LargeFileFinder.Sort(_matches, SelectedSort.Sort))
        {
            Results.Add(new LargeFileRow(m));
        }

        OnPropertyChanged(nameof(HasResults));
    }
}
