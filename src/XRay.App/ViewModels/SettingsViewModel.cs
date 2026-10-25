using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XRay.App.Services;
using XRay.Core.Formatting;
using XRay.Core.Scanning;
using XRay.Core.Settings;
using XRay.Core.Storage;

namespace XRay.App.ViewModels;

public sealed record Choice<T>(string Label, T Value);

/// <summary>Settings page. Every change is applied and saved immediately.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly DialogService _dialogs;
    private readonly ShellService _shell;
    private readonly StartupRegistration _startup;
    private readonly AppPaths _paths;
    private bool _loading;

    [ObservableProperty] private Choice<AppTheme> _selectedTheme;
    [ObservableProperty] private Choice<SizeUnitMode> _units;
    [ObservableProperty] private Choice<SizeMetric> _metric;
    [ObservableProperty] private string _defaultScanPath = string.Empty;
    [ObservableProperty] private Choice<int> _treemapDepth;
    [ObservableProperty] private bool _showLabels;
    [ObservableProperty] private bool _shading;
    [ObservableProperty] private Choice<int> _pollingInterval;
    [ObservableProperty] private Choice<int> _retention;
    [ObservableProperty] private Choice<int> _snapshotDepth;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string? _startupMessage;

    public SettingsViewModel(
        SettingsService settings,
        ThemeService theme,
        DialogService dialogs,
        ShellService shell,
        StartupRegistration startup,
        AppPaths paths)
    {
        _settings = settings;
        _theme = theme;
        _dialogs = dialogs;
        _shell = shell;
        _startup = startup;
        _paths = paths;
        _selectedTheme = Themes[0];
        _units = UnitChoices[0];
        _metric = MetricChoices[0];
        _treemapDepth = DepthChoices[2];
        _pollingInterval = PollingChoices[1];
        _retention = RetentionChoices[0];
        _snapshotDepth = SnapshotDepthChoices[2];
        Load();
    }

    public override string Title => "Settings";

    public override string Glyph => "";

    public IReadOnlyList<Choice<AppTheme>> Themes { get; } = [new("Dark", AppTheme.Dark), new("Light", AppTheme.Light)];

    public IReadOnlyList<Choice<SizeUnitMode>> UnitChoices { get; } =
    [
        new("Binary - KiB, MiB, GiB (1024)", SizeUnitMode.Binary),
        new("Decimal - kB, MB, GB (1000)", SizeUnitMode.Decimal),
        new("Windows style - KB, MB, GB (1024)", SizeUnitMode.Windows),
    ];

    public IReadOnlyList<Choice<SizeMetric>> MetricChoices { get; } =
    [
        new("Logical size (file length)", SizeMetric.Logical),
        new("Size on disk (estimated allocation)", SizeMetric.Allocated),
    ];

    public IReadOnlyList<Choice<int>> DepthChoices { get; } =
    [
        new("1 level - direct contents only", 1), new("2 levels", 2), new("3 levels (recommended)", 3), new("4 levels", 4),
    ];

    public IReadOnlyList<Choice<int>> PollingChoices { get; } =
    [
        new("0.5 seconds", 500), new("1 second (recommended)", 1000), new("2 seconds", 2000), new("5 seconds", 5000),
    ];

    public IReadOnlyList<Choice<int>> RetentionChoices { get; } =
    [
        new("Keep forever", 0), new("30 days", 30), new("90 days", 90), new("180 days", 180), new("1 year", 365),
    ];

    public IReadOnlyList<Choice<int>> SnapshotDepthChoices { get; } =
    [
        new("4 levels", 4), new("5 levels", 5), new("6 levels (recommended)", 6), new("8 levels", 8), new("12 levels", 12),
    ];

    public string DataFolder => _paths.Root;

    public string LogFolder => _paths.LogsDirectory;

    [RelayCommand]
    private void BrowseDefaultPath()
    {
        string? folder = _dialogs.PickFolder(_settings.DefaultScanPath, "Select the default folder to scan");
        if (folder is not null)
        {
            DefaultScanPath = folder;
        }
    }

    [RelayCommand]
    private void ClearDefaultPath() => DefaultScanPath = string.Empty;

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(_paths.Root);

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(_paths.LogsDirectory);
        _shell.OpenFolder(_paths.LogsDirectory);
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        if (!_dialogs.Confirm("Reset settings?", "All preferences return to their defaults. Snapshots, logs and the startup option are not affected.", "Reset"))
        {
            return;
        }

        _settings.Reset();
        _theme.Apply(_settings.Current.Theme);
        Load();
    }

    private void Load()
    {
        _loading = true;
        AppSettings s = _settings.Current;
        SelectedTheme = Themes.First(c => c.Value == s.Theme);
        Units = UnitChoices.First(c => c.Value == s.SizeUnits);
        Metric = MetricChoices.First(c => c.Value == s.SizeMetric);
        DefaultScanPath = s.DefaultScanPath ?? string.Empty;
        TreemapDepth = DepthChoices.FirstOrDefault(c => c.Value == s.TreemapDepth) ?? DepthChoices[2];
        ShowLabels = s.TreemapShowLabels;
        Shading = s.TreemapShading;
        PollingInterval = PollingChoices.FirstOrDefault(c => c.Value == s.PollingIntervalMs) ?? PollingChoices[1];
        Retention = RetentionChoices.FirstOrDefault(c => c.Value == s.HistoryRetentionDays) ?? RetentionChoices[0];
        SnapshotDepth = SnapshotDepthChoices.FirstOrDefault(c => c.Value == s.SnapshotDepth) ?? SnapshotDepthChoices[2];
        // Reflect the real registry state rather than the stored flag.
        StartWithWindows = _startup.IsEnabled();
        _loading = false;
    }

    partial void OnSelectedThemeChanged(Choice<AppTheme> value)
    {
        if (_loading)
        {
            return;
        }

        _settings.Update(s => s.Theme = value.Value);
        _theme.Apply(value.Value);
    }

    partial void OnUnitsChanged(Choice<SizeUnitMode> value) => Save(s => s.SizeUnits = value.Value);

    partial void OnMetricChanged(Choice<SizeMetric> value) => Save(s => s.SizeMetric = value.Value);

    partial void OnDefaultScanPathChanged(string value) => Save(s => s.DefaultScanPath = string.IsNullOrWhiteSpace(value) ? null : value);

    partial void OnTreemapDepthChanged(Choice<int> value) => Save(s => s.TreemapDepth = value.Value);

    partial void OnShowLabelsChanged(bool value) => Save(s => s.TreemapShowLabels = value);

    partial void OnShadingChanged(bool value) => Save(s => s.TreemapShading = value);

    partial void OnPollingIntervalChanged(Choice<int> value) => Save(s => s.PollingIntervalMs = value.Value);

    partial void OnSnapshotDepthChanged(Choice<int> value) => Save(s => s.SnapshotDepth = value.Value);

    partial void OnRetentionChanged(Choice<int> value)
    {
        if (_loading)
        {
            return;
        }

        if (value.Value > 0 && !_dialogs.Confirm(
                "Limit snapshot history?",
                $"Snapshots older than {value.Label.ToLowerInvariant()} will be deleted automatically each time X-RAY starts. Only X-RAY's own size records are removed - never your files.",
                "Apply retention"))
        {
            _loading = true;
            Retention = RetentionChoices.First(c => c.Value == _settings.Current.HistoryRetentionDays);
            _loading = false;
            return;
        }

        Save(s => s.HistoryRetentionDays = value.Value);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        // This is the only place the Run key is touched, and only after the user flips the switch.
        if (_startup.TrySet(value))
        {
            Save(s => s.StartWithWindows = value);
            StartupMessage = value ? "X-RAY will start when you sign in to Windows." : "X-RAY will no longer start automatically.";
        }
        else
        {
            StartupMessage = "Windows did not allow changing the startup setting.";
            _loading = true;
            StartWithWindows = _startup.IsEnabled();
            _loading = false;
        }
    }

    private void Save(Action<AppSettings> change)
    {
        if (!_loading)
        {
            _settings.Update(change);
        }
    }
}

