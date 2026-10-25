using CommunityToolkit.Mvvm.ComponentModel;
using XRay.App.Services;

namespace XRay.App.ViewModels;

/// <summary>Shell view model: owns the navigation list and the active page.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private PageViewModel _currentPage;

    public MainViewModel(
        NavigationService navigation,
        OverviewViewModel overview,
        DiskAnalyzerViewModel analyzer,
        PerformanceViewModel performance,
        LargeFilesViewModel largeFiles,
        DuplicatesViewModel duplicates,
        HistoryViewModel history,
        SettingsViewModel settings)
    {
        Pages = [overview, analyzer, performance, largeFiles, duplicates, history, settings];
        _currentPage = overview;
        overview.OnActivated();
        navigation.NavigationRequested += (_, type) =>
        {
            PageViewModel? target = Pages.FirstOrDefault(p => p.GetType() == type);
            if (target is not null)
            {
                CurrentPage = target;
            }
        };
    }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public string Version => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    partial void OnCurrentPageChanging(PageViewModel value) => CurrentPage?.OnDeactivated();

    partial void OnCurrentPageChanged(PageViewModel value) => value.OnActivated();
}
