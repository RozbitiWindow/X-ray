using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.Logging;
using XRay.Core.Formatting;
using XRay.Core.Scanning;
using XRay.Core.Settings;
using XRay.Core.Storage;

namespace XRay.App.Services;

/// <summary>Application-wide cancellation signal raised on shutdown.</summary>
public sealed class AppLifetime
{
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Stopping => _cts.Token;

    public void StopApplication()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }
}

/// <summary>Holds the live <see cref="AppSettings"/>, persists changes and notifies listeners.</summary>
public sealed class SettingsService
{
    private readonly SettingsStore _store;

    public SettingsService(SettingsStore store)
    {
        _store = store;
        Current = store.Load().Normalize();
        Format.Units = Current.SizeUnits;
    }

    public AppSettings Current { get; }

    public event EventHandler? Changed;

    /// <summary>Applies a change, saves the file and raises <see cref="Changed"/>.</summary>
    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Current.Normalize();
        Format.Units = Current.SizeUnits;
        _store.TrySave(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        Update(s =>
        {
            var d = new AppSettings();
            s.Theme = d.Theme;
            s.SizeUnits = d.SizeUnits;
            s.SizeMetric = d.SizeMetric;
            s.DefaultScanPath = d.DefaultScanPath;
            s.TreemapDepth = d.TreemapDepth;
            s.TreemapShowLabels = d.TreemapShowLabels;
            s.TreemapShading = d.TreemapShading;
            s.PollingIntervalMs = d.PollingIntervalMs;
            s.HistoryRetentionDays = d.HistoryRetentionDays;
            s.SnapshotDepth = d.SnapshotDepth;
            // StartWithWindows is deliberately not reset here: it is changed only through its own toggle.
        });
    }

    /// <summary>Folder used when no explicit folder was chosen.</summary>
    public string DefaultScanPath =>
        Current.DefaultScanPath is { } p && Directory.Exists(p)
            ? p
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>Formatting helpers that honour the user's unit preference.</summary>
public static class Format
{
    public static SizeUnitMode Units { get; set; } = SizeUnitMode.Binary;

    public static string Size(long bytes) => SizeFormatter.Format(bytes, Units);

    public static string Delta(long bytes) => SizeFormatter.FormatDelta(bytes, Units);

    public static string Rate(double bytesPerSecond) => SizeFormatter.FormatRate(bytesPerSecond, Units);

    public static string Count(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:00}s"
        : $"{t.TotalSeconds:0.0}s";

    public static string Date(DateTime utc) =>
        utc == default ? "-" : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"3 minutes ago", "yesterday", or a date.</summary>
    public static string Relative(DateTime utc)
    {
        if (utc == default)
        {
            return "-";
        }

        TimeSpan age = DateTime.UtcNow - utc;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours} h ago";
        }

        if (age < TimeSpan.FromDays(2))
        {
            return "yesterday";
        }

        return age < TimeSpan.FromDays(30) ? $"{(int)age.TotalDays} days ago" : Date(utc);
    }
}

/// <summary>Lets view models request navigation without referencing the main view model.</summary>
public sealed class NavigationService
{
    public event EventHandler<Type>? NavigationRequested;

    public void NavigateTo<TViewModel>() => NavigationRequested?.Invoke(this, typeof(TViewModel));
}

/// <summary>Shares the most recent Disk Analyzer scan with other screens.</summary>
public sealed class AnalyzerSession
{
    private readonly AppStateStore _stateStore;

    public AnalyzerSession(AppStateStore stateStore)
    {
        _stateStore = stateStore;
        State = stateStore.Load();
    }

    public ScanResult? Current { get; private set; }

    /// <summary>Persisted state, including the summary of the last scan from a previous session.</summary>
    public AppState State { get; }

    public event EventHandler? Changed;

    public void Publish(ScanResult result)
    {
        Current = result;
        State.LastScan = RecentScanInfo.From(result);
        State.AddRecentFolder(result.Root.FullPath);
        _stateStore.TrySave(State);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns the current scan if it covers <paramref name="path"/>, plus the node for that path.</summary>
    public (ScanResult Result, FileSystemNode Node)? FindCovering(string path)
    {
        if (Current is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        FileSystemNode? node = TreeQueries.FindByPath(Current.Root, path);
        return node is { IsDirectory: true } ? (Current, node) : null;
    }
}

/// <summary>Opens Explorer and the clipboard. Never modifies files.</summary>
public sealed class ShellService(ILogger<ShellService> logger)
{
    /// <summary>Opens Explorer with the item selected (or the folder itself if it is a directory without a parent).</summary>
    public void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            }
            else
            {
                string? parent = Path.GetDirectoryName(path);
                if (parent is not null && Directory.Exists(parent))
                {
                    OpenFolder(parent);
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("Could not open Explorer: {Message}", ex.Message);
        }
    }

    public void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("Could not open folder: {Message}", ex.Message);
        }
    }

    public void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            logger.LogWarning("Clipboard unavailable: {Message}", ex.Message);
        }
    }
}
