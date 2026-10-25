namespace XRay.Core.Scanning;

/// <summary>Kinds of problems that can occur while scanning.</summary>
public enum ScanIssueKind
{
    AccessDenied,
    Vanished,
    ReparsePointSkipped,
    PathTooLong,
    IoError,
}

/// <summary>A single item that could not be fully scanned.</summary>
public sealed record ScanIssue(string Path, ScanIssueKind Kind, string Message);

/// <summary>Options that control a directory scan.</summary>
public sealed record ScanOptions
{
    /// <summary>Do not descend into symbolic links, junctions and mount points (strongly recommended).</summary>
    public bool SkipLinkedDirectories { get; init; } = true;

    /// <summary>Maximum number of issues kept in detail; further issues are only counted.</summary>
    public int MaxRecordedIssues { get; init; } = 5000;

    /// <summary>Number of parallel directory readers. 0 selects a value based on the processor count.</summary>
    public int DegreeOfParallelism { get; init; }

    /// <summary>How often progress is reported.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    public static ScanOptions Default { get; } = new();
}

/// <summary>Incremental scan progress.</summary>
public readonly record struct ScanProgress(
    long FilesScanned,
    long DirectoriesScanned,
    long BytesFound,
    long IssueCount,
    string? CurrentPath,
    TimeSpan Elapsed);

/// <summary>The outcome of a completed scan.</summary>
public sealed class ScanResult
{
    public required FileSystemNode Root { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public required ScanOptions Options { get; init; }

    /// <summary>Volume cluster size used to estimate allocated sizes.</summary>
    public required long ClusterSize { get; init; }

    /// <summary>Recorded issues (up to <see cref="ScanOptions.MaxRecordedIssues"/>).</summary>
    public required IReadOnlyList<ScanIssue> Issues { get; init; }

    /// <summary>Total number of issues by kind, including ones not recorded in detail.</summary>
    public required IReadOnlyDictionary<ScanIssueKind, long> IssueCounts { get; init; }

    public DateTime CompletedUtc => StartedUtc + Elapsed;

    public long TotalIssueCount => IssueCounts.Values.Sum();

    public long Count(ScanIssueKind kind) => IssueCounts.TryGetValue(kind, out long count) ? count : 0;

    /// <summary>True if any part of the tree could not be read, so totals are lower bounds.</summary>
    public bool IsIncomplete => Count(ScanIssueKind.AccessDenied) + Count(ScanIssueKind.IoError) +
                                Count(ScanIssueKind.PathTooLong) + Count(ScanIssueKind.Vanished) > 0;
}
