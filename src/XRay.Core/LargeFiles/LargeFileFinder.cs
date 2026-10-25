using XRay.Core.Scanning;

namespace XRay.Core.LargeFiles;

/// <summary>Filter criteria for the large-file search.</summary>
public sealed record LargeFileQuery
{
    /// <summary>Minimum size in bytes (inclusive), measured with <see cref="Metric"/>.</summary>
    public long MinimumSize { get; init; }

    /// <summary>Extensions to include (e.g. ".iso"); null or empty includes all.</summary>
    public IReadOnlySet<string>? Extensions { get; init; }

    /// <summary>Only include files last modified before this time; null disables the age filter.</summary>
    public DateTime? ModifiedBeforeUtc { get; init; }

    /// <summary>Restrict to one review category; null includes everything, <see cref="ReviewCategory.None"/> is not allowed.</summary>
    public ReviewCategory? Category { get; init; }

    /// <summary>Only include files with any review suggestion.</summary>
    public bool OnlySuggested { get; init; }

    public SizeMetric Metric { get; init; } = SizeMetric.Logical;

    /// <summary>Maximum number of results returned (largest first). Further matches are counted only.</summary>
    public int MaxResults { get; init; } = 5000;

    /// <summary>Parses a user-entered list such as "iso, .zip;mp4" into normalized extensions.</summary>
    public static IReadOnlySet<string>? ParseExtensions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            string ext = raw.Trim().TrimStart('*');
            if (ext.Length == 0 || ext == ".")
            {
                continue;
            }

            set.Add(ext.StartsWith('.') ? ext : "." + ext);
        }

        return set.Count == 0 ? null : set;
    }
}

/// <summary>A file that matched a large-file query.</summary>
public sealed record LargeFileMatch(FileSystemNode Node, string FullPath, long Size, ReviewSuggestion Suggestion)
{
    public string Name => Node.Name;

    public string Extension => Node.Extension;

    public DateTime LastWriteTimeUtc => Node.LastWriteTimeUtc;

    public string Folder => Path.GetDirectoryName(FullPath) ?? string.Empty;
}

/// <summary>Result of a large-file query.</summary>
public sealed record LargeFileSearchResult(IReadOnlyList<LargeFileMatch> Matches, long TotalMatches, long TotalMatchedSize)
{
    public bool IsTruncated => TotalMatches > Matches.Count;
}

/// <summary>Sort orders for large-file results.</summary>
public enum LargeFileSort
{
    SizeDescending,
    SizeAscending,
    OldestFirst,
    NewestFirst,
    Name,
}

/// <summary>Searches an already-scanned tree for large files. Does not access the file system.</summary>
public static class LargeFileFinder
{
    public static LargeFileSearchResult Find(FileSystemNode root, LargeFileQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(query);

        int max = Math.Max(1, query.MaxResults);
        var heap = new PriorityQueue<LargeFileMatch, long>(Math.Min(max, 1024) + 1);
        long total = 0, totalSize = 0, visited = 0;

        foreach (FileSystemNode file in TreeQueries.EnumerateFiles(root))
        {
            if ((++visited & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            long size = file.GetSize(query.Metric);
            if (size < query.MinimumSize)
            {
                continue;
            }

            if (query.Extensions is { Count: > 0 } exts && !exts.Contains(file.Extension))
            {
                continue;
            }

            if (query.ModifiedBeforeUtc is { } before && file.LastWriteTimeUtc >= before)
            {
                continue;
            }

            string path = file.FullPath;
            ReviewSuggestion suggestion = ReviewHeuristics.Evaluate(path, size);
            if (query.OnlySuggested && !suggestion.IsSuggested)
            {
                continue;
            }

            if (query.Category is { } category && suggestion.Category != category)
            {
                continue;
            }

            total++;
            totalSize += size;
            var match = new LargeFileMatch(file, path, size, suggestion);
            if (heap.Count < max)
            {
                heap.Enqueue(match, size);
            }
            else if (heap.TryPeek(out _, out long smallest) && size > smallest)
            {
                heap.EnqueueDequeue(match, size);
            }
        }

        var list = new List<LargeFileMatch>(heap.Count);
        while (heap.TryDequeue(out LargeFileMatch? m, out _))
        {
            list.Add(m);
        }

        list.Reverse();
        return new LargeFileSearchResult(list, total, totalSize);
    }

    /// <summary>Sorts matches; ties are broken by path for a stable, deterministic order.</summary>
    public static List<LargeFileMatch> Sort(IEnumerable<LargeFileMatch> matches, LargeFileSort sort)
    {
        var list = matches.ToList();
        Comparison<LargeFileMatch> primary = sort switch
        {
            LargeFileSort.SizeAscending => (a, b) => a.Size.CompareTo(b.Size),
            LargeFileSort.OldestFirst => (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc),
            LargeFileSort.NewestFirst => (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc),
            LargeFileSort.Name => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            _ => (a, b) => b.Size.CompareTo(a.Size),
        };
        list.Sort((a, b) =>
        {
            int c = primary(a, b);
            return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath);
        });
        return list;
    }
}
