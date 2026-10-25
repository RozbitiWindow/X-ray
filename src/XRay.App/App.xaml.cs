using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XRay.App.Services;
using XRay.App.Services.Metrics;
using XRay.App.ViewModels;
using XRay.Core.Duplicates;
using XRay.Core.History;
using XRay.Core.Logging;
using XRay.Core.Scanning;
using XRay.Core.Settings;
using XRay.Core.Storage;

namespace XRay.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private bool _showingError;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();
        Directory.CreateDirectory(paths.Root);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(paths.LogsDirectory));
        });

        services.AddSingleton(paths);
        services.AddSingleton(sp => new SettingsStore(paths.SettingsFile, sp.GetRequiredService<ILogger<SettingsStore>>()));
        services.AddSingleton(_ => new AppStateStore(paths.StateFile));
        services.AddSingleton(sp => new SnapshotStore(paths.SnapshotsDirectory, sp.GetRequiredService<ILogger<SnapshotStore>>()));
        services.AddSingleton<IDirectoryScanner>(sp => new DirectoryScanner(logger: sp.GetRequiredService<ILogger<DirectoryScanner>>()));
        services.AddSingleton(_ => new DuplicateFinder());
        services.AddSingleton(_ => new DuplicateCleaner(new ShellRecycler()));

        services.AddSingleton<AppLifetime>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ShellService>();
        services.AddSingleton<AnalyzerSession>();
        services.AddSingleton<StartupRegistration>();
        services.AddSingleton<SystemMetricsService>();

        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<DiskAnalyzerViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<LargeFilesViewModel>();
        services.AddSingleton<DuplicatesViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider();
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("X-RAY {Version} starting on {OS}", typeof(App).Assembly.GetName().Version, Environment.OSVersion);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var settings = _services.GetRequiredService<SettingsService>();
        _services.GetRequiredService<ThemeService>().Apply(settings.Current.Theme);
        ApplyRetention(settings.Current);

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        _services.GetRequiredService<SystemMetricsService>().Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("X-RAY shutting down");
        if (_services is not null)
        {
            // Cancels running scans and searches, then stops metric polling.
            _services.GetRequiredService<AppLifetime>().StopApplication();
            _services.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>Colours a window's native title bar to match the active theme.</summary>
    public void ApplyTitleBar(Window window) => _services?.GetService<ThemeService>()?.ApplyTitleBar(window);

    private void ApplyRetention(AppSettings settings)
    {
        if (settings.HistoryRetentionDays <= 0)
        {
            return;
        }

        try
        {
            _services!.GetRequiredService<SnapshotStore>().Prune(TimeSpan.FromDays(settings.HistoryRetentionDays));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Snapshot retention failed");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        if (_showingError)
        {
            return;
        }

        _showingError = true;
        try
        {
            _services?.GetService<DialogService>()?.ShowError(
                "Something went wrong",
                "X-RAY hit an unexpected problem and recovered. Details were written to the log file.",
                e.Exception.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not show the error dialog");
        }
        finally
        {
            _showingError = false;
        }
    }
}
