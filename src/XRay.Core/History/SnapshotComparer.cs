using XRay.Core.Scanning;

namespace XRay.Core.History;

/// <summary>How a directory changed between two snapshots.</summary>
public enum ChangeKind
{
    Unchanged,
    Grown,
    Shrunk,
    Added,
    Removed,

    /// <summary>The change cannot be measured reliably (scope differs, data incomplete or not recorded).</summary>
    NotComparable,
}

/// <summary>Change of one directory between two snapshots.</summary>
public sealed record SnapshotDiffEntry(
    string Path,
    int Depth,
    long? OldSize,
    long? NewSize,
    ChangeKind Kind,
    string? Note)
{
    /// <summary>Size difference in bytes (new - old). Missing sides count as zero.</summary>
    public long Delta => (NewSize ?? 0) - (OldSize ?? 0);

    /// <summary>True if <see cref="Delta"/> represents an actual measured change.</summary>
    public bool IsMeasured => Kind != ChangeKind.NotComparable;
}

/// <summary>The result of comparing two snapshots.</summary>
public sealed class SnapshotComparison
{
    public required SnapshotSummary Older { get; init; }

    public required SnapshotSummary Newer { get; init; }

    /// <summary>True when both snapshots cover the same root folder.</summary>
    public required bool SameScope { get; init; }

    /// <summary>Human-readable warnings about the reliability of the comparison.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public required IReadOnlyList<SnapshotDiffEntry> Entries { get; init; }

    public required SizeMetric Metric { get; init; }

    /// <summary>Depth up to which both snapshots recorded directories.</summary>
    public required int ComparedDepth { get; init; }

    /// <summary>Change of the root total; null if the scopes differ.</summary>
    public long? TotalDelta { get; init; }

    public int Count(ChangeKind kind) => Entries.Count(e => e.Kind == kind);
}

/// <summary>Compares two snapshots directory by directory.</summary>
public static class SnapshotComparer
{
    /// <summary>
    /// Compares two snapshots. The order of arguments does not matter; the older one (by creation time) is the baseline.
    /// </summary>
    public static SnapshotComparison Compare(StorageSnapshot a, StorageSnapshot b, SizeMetric metric = SizeMetric.Logical)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        (StorageSnapshot older, StorageSnapshot newer) = a.Summary.CreatedUtc <= b.Summary.CreatedUtc ? (a, b) : (b, a);
        bool sameScope = PathsEqual(older.Summary.RootPath, newer.Summary.RootPath);
        int depth = Math.Min(older.Summary.MaxDepth, newer.Summary.MaxDepth);

        var warnings = new List<string>();
        if (!sameScope)
        {
            warnings.Add($"The snapshots cover different folders ('{older.Summary.RootPath}' and '{newer.Summary.RootPath}'). Sizes are shown side by side but no change is measured.");
        }

        if (older.Summary.MaxDepth != newer.Summary.MaxDepth)
        {
            warnings.Add($"The snapshots were recorded to different depths; only the first {depth} levels are compared.");
        }

        if (older.Summary.Truncated || newer.Summary.Truncated)
        {
            warnings.Add("At least one snapshot hit the entry limit, so folders missing from it are not reported as added or removed.");
        }

        if (older.Summary.UnreadableDirectoryCount > 0 || newer.Summary.UnreadableDirectoryCount > 0)
        {
            warnings.Add("Some folders could not be read in at least one scan. Folders affected by this are marked as not comparable.");
        }

        Dictionary<string, SnapshotEntry> oldMap = ToMap(older, depth);
        Dictionary<string, SnapshotEntry> newMap = ToMap(newer, depth);

        var entries = new List<SnapshotDiffEntry>(Math.Max(oldMap.Count, newMap.Count));
        foreach (var (path, oldEntry) in oldMap)
        {
            newMap.TryGetValue(path, out SnapshotEntry? newEntry);
            entries.Add(Classify(path, oldEntry, newEntry, sameScope, older, newer, oldMap, newMap, metric));
        }

        foreach (var (path, newEntry) in newMap)
        {
            if (!oldMap.ContainsKey(path))
            {
                entries.Add(Classify(path, null, newEntry, sameScope, older, newer, oldMap, newMap, metric));
            }
        }

        entries.Sort((x, y) =>
        {
            int c = Math.Abs(y.Delta).CompareTo(Math.Abs(x.Delta));
            return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(x.Path, y.Path);
        });

        long? totalDelta = null;
        if (sameScope)
        {
            totalDelta = (metric == SizeMetric.Allocated ? newer.Summary.TotalAllocatedSize : newer.Summary.TotalSize) -
                         (metric == SizeMetric.Allocated ? older.Summary.TotalAllocatedSize : older.Summary.TotalSize);
        }

        return new SnapshotComparison
        {
            Older = older.Summary,
            Newer = newer.Summary,
            SameScope = sameScope,
            Warnings = warnings,
            Entries = entries,
            Metric = metric,
            ComparedDepth = depth,
            TotalDelta = totalDelta,
        };
    }

    private static SnapshotDiffEntry Classify(
        string path,
        SnapshotEntry? oldEntry,
        SnapshotEntry? newEntry,
        bool sameScope,
        StorageSnapshot older,
        StorageSnapshot newer,
        Dictionary<string, SnapshotEntry> oldMap,
        Dictionary<string, SnapshotEntry> newMap,
        SizeMetric metric)
    {
        int depth = (oldEntry ?? newEntry)!.Depth;
        long? oldSize = oldEntry?.GetSize(metric);
        long? newSize = newEntry?.GetSize(metric);

        if (!sameScope)
        {
            return new(path, depth, oldSize, newSize, ChangeKind.NotComparable, "Different scan scope");
        }

        if (oldEntry is not null && newEntry is not null)
        {
            if (oldEntry.Link != newEntry.Link)
            {
                return new(path, depth, oldSize, newSize, ChangeKind.NotComparable, "Changed between folder and link");
            }

            if (oldEntry.Incomplete || newEntry.Incomplete)
            {
                return new(path, depth, oldSize, newSize, ChangeKind.NotComparable, "Partly unreadable in one of the scans");
            }

            long delta = newSize!.Value - oldSize!.Value;
            ChangeKind kind = delta > 0 ? ChangeKind.Grown : delta < 0 ? ChangeKind.Shrunk : ChangeKind.Unchanged;
            return new(path, depth, oldSize, newSize, kind, null);
        }

        // Present on one side only.
        bool added = oldEntry is null;
        StorageSnapshot missingIn = added ? older : newer;
        Dictionary<string, SnapshotEntry> missingMap = added ? oldMap : newMap;

        if (missingIn.Summary.Truncated)
        {
            return new(path, depth, oldSize, newSize, ChangeKind.NotComparable, "Not recorded (snapshot entry limit reached)");
        }

        // If the nearest recorded ancestor on the missing side was unreadable or a link, the folder may simply have been hidden.
        string? parent = ParentOf(path);
        while (parent is not null)
        {
            if (missingMap.TryGetValue(parent, out SnapshotEntry? ancestor))
            {
                if (ancestor.Incomplete || ancestor.Link)
                {
                    return new(path, depth, oldSize, newSize, ChangeKind.NotComparable,
                        added ? "Parent folder was unreadable in the older scan" : "Parent folder was unreadable in the newer scan");
                }

                break;
            }

            parent = ParentOf(parent);
        }

        return added
            ? new(path, depth, null, newSize, ChangeKind.Added, null)
            : new(path, depth, oldSize, null, ChangeKind.Removed, null);
    }

    private static Dictionary<string, SnapshotEntry> ToMap(StorageSnapshot snapshot, int maxDepth)
    {
        var map = new Dictionary<string, SnapshotEntry>(snapshot.Entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (SnapshotEntry e in snapshot.Entries)
        {
            if (e.Depth <= maxDepth)
            {
                map[e.Path] = e;
            }
        }

        return map;
    }

    private static string? ParentOf(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        int idx = path.LastIndexOf('\\');
        return idx < 0 ? string.Empty : path[..idx];
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a.Trim()),
            Path.TrimEndingDirectorySeparator(b.Trim()),
            StringComparison.OrdinalIgnoreCase);
}
