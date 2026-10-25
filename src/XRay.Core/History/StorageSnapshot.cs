using XRay.Core.Scanning;

namespace XRay.Core.History;

/// <summary>Summary information about a snapshot, stored separately so listing does not load all entries.</summary>
public sealed class SnapshotSummary
{
    public int FormatVersion { get; set; } = 1;

    public Guid Id { get; set; }

    public string RootPath { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    /// <summary>When the underlying scan finished.</summary>
    public DateTime ScanCompletedUtc { get; set; }

    public string? Label { get; set; }

    public long TotalSize { get; set; }

    public long TotalAllocatedSize { get; set; }

    public long FileCount { get; set; }

    public long DirectoryCount { get; set; }

    /// <summary>Directories that could not be read during the scan.</summary>
    public long UnreadableDirectoryCount { get; set; }

    /// <summary>Directories nested deeper than this (relative to the root) are not recorded individually.</summary>
    public int MaxDepth { get; set; }

    /// <summary>True if the entry limit was reached, so not every directory up to <see cref="MaxDepth"/> was recorded.</summary>
    public bool Truncated { get; set; }

    public int EntryCount { get; set; }
}

/// <summary>Recorded size of one directory in a snapshot.</summary>
public sealed class SnapshotEntry
{
    /// <summary>Path relative to the snapshot root using '\' separators; empty for the root itself.</summary>
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public long AllocatedSize { get; set; }

    public long FileCount { get; set; }

    /// <summary>True if part of this directory could not be read, so its size is a lower bound.</summary>
    public bool Incomplete { get; set; }

    /// <summary>True if the directory is a link that was not followed.</summary>
    public bool Link { get; set; }

    public int Depth => Path.Length == 0 ? 0 : Path.Count(c => c == '\\') + 1;

    public long GetSize(SizeMetric metric) => metric == SizeMetric.Allocated ? AllocatedSize : Size;
}

/// <summary>A complete snapshot: summary plus directory entries.</summary>
public sealed class StorageSnapshot
{
    public SnapshotSummary Summary { get; set; } = new();

    public List<SnapshotEntry> Entries { get; set; } = [];
}

/// <summary>Creates snapshots from scanned trees.</summary>
public static class SnapshotBuilder
{
    public const int DefaultMaxDepth = 6;
    public const int DefaultMaxEntries = 200_000;

    /// <summary>
    /// Records every directory of <paramref name="result"/> down to <paramref name="maxDepth"/> levels,
    /// breadth-first, up to <paramref name="maxEntries"/> entries.
    /// </summary>
    public static StorageSnapshot Create(
        ScanResult result,
        int maxDepth = DefaultMaxDepth,
        int maxEntries = DefaultMaxEntries,
        string? label = null,
        DateTime? createdUtc = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        FileSystemNode root = result.Root;
        maxDepth = Math.Clamp(maxDepth, 1, 32);
        var entries = new List<SnapshotEntry>();
        bool truncated = false;
        long unreadable = 0;

        var queue = new Queue<FileSystemNode>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            FileSystemNode dir = queue.Dequeue();
            if (dir.IsUnreadable)
            {
                unreadable++;
            }

            if (entries.Count >= maxEntries)
            {
                truncated = true;
                continue;
            }

            entries.Add(new SnapshotEntry
            {
                Path = dir.RelativePath,
                Size = dir.Size,
                AllocatedSize = dir.AllocatedSize,
                FileCount = dir.FileCount,
                Incomplete = dir.IsIncomplete,
                Link = dir.IsReparsePoint,
            });

            if (dir.Depth >= maxDepth)
            {
                // Count unreadable directories below the recording depth too, without recording them.
                if ((dir.Flags & NodeFlags.IncompleteDescendants) != 0)
                {
                    unreadable += TreeQueries.EnumerateUnreadable(dir).Count(n => !ReferenceEquals(n, dir));
                }

                continue;
            }

            foreach (FileSystemNode child in dir.Children)
            {
                if (child.IsDirectory)
                {
                    queue.Enqueue(child);
                }
            }
        }

        return new StorageSnapshot
        {
            Summary = new SnapshotSummary
            {
                Id = Guid.NewGuid(),
                RootPath = root.FullPath,
                CreatedUtc = createdUtc ?? DateTime.UtcNow,
                ScanCompletedUtc = result.CompletedUtc,
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                TotalSize = root.Size,
                TotalAllocatedSize = root.AllocatedSize,
                FileCount = root.FileCount,
                DirectoryCount = root.DirectoryCount,
                UnreadableDirectoryCount = unreadable,
                MaxDepth = maxDepth,
                Truncated = truncated,
                EntryCount = entries.Count,
            },
            Entries = entries,
        };
    }
}
