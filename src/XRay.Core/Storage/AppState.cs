using System.Text.Json;
using XRay.Core.Scanning;

namespace XRay.Core.Storage;

/// <summary>Summary of the most recent completed scan, shown on the overview after a restart.</summary>
public sealed class RecentScanInfo
{
    public string RootPath { get; set; } = string.Empty;

    public DateTime CompletedUtc { get; set; }

    public TimeSpan Elapsed { get; set; }

    public long TotalSize { get; set; }

    public long TotalAllocatedSize { get; set; }

    public long FileCount { get; set; }

    public long DirectoryCount { get; set; }

    public long IssueCount { get; set; }

    public static RecentScanInfo From(ScanResult result) => new()
    {
        RootPath = result.Root.FullPath,
        CompletedUtc = result.CompletedUtc,
        Elapsed = result.Elapsed,
        TotalSize = result.Root.Size,
        TotalAllocatedSize = result.Root.AllocatedSize,
        FileCount = result.Root.FileCount,
        DirectoryCount = result.Root.DirectoryCount,
        IssueCount = result.TotalIssueCount,
    };
}

/// <summary>Small piece of non-preference state persisted between sessions.</summary>
public sealed class AppState
{
    public RecentScanInfo? LastScan { get; set; }

    public List<string> RecentFolders { get; set; } = [];

    public void AddRecentFolder(string path, int max = 8)
    {
        RecentFolders.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, path);
        if (RecentFolders.Count > max)
        {
            RecentFolders.RemoveRange(max, RecentFolders.Count - max);
        }
    }
}

/// <summary>Loads and saves <see cref="AppState"/>; malformed content is ignored.</summary>
public sealed class AppStateStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public AppState Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonFile.Read<AppState>(FilePath) ?? new AppState() : new AppState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AppState();
        }
    }

    public void TrySave(AppState state)
    {
        try
        {
            JsonFile.WriteAtomic(FilePath, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // State is a convenience; failing to save it must not disturb the user.
        }
    }
}
