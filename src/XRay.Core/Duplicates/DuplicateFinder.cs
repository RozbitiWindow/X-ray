using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using XRay.Core.Scanning;

namespace XRay.Core.Duplicates;

/// <summary>Options for <see cref="DuplicateFinder"/>.</summary>
public sealed record DuplicateOptions
{
    /// <summary>Files smaller than this (in bytes) are ignored. Empty files are always ignored.</summary>
    public long MinimumSize { get; init; } = 1024 * 1024;

    /// <summary>Number of files hashed in parallel. Disks are the bottleneck, so keep this small.</summary>
    public int DegreeOfParallelism { get; init; } = 2;
}

/// <summary>Incremental progress of a duplicate search.</summary>
public readonly record struct DuplicateProgress(string Phase, long FilesDone, long FilesTotal, long BytesDone, long BytesTotal);

/// <summary>One file that belongs to a group of identical files.</summary>
public sealed record DuplicateFile(FileSystemNode Node, string FullPath, long Size, DateTime LastWriteTimeUtc)
{
    public string Name => Node.Name;

    public string Folder => Path.GetDirectoryName(FullPath) ?? string.Empty;
}

/// <summary>Files with byte-identical content (verified with SHA-256).</summary>
public sealed class DuplicateGroup
{
    public required string Hash { get; init; }

    public required long FileSize { get; init; }

    public required List<DuplicateFile> Files { get; init; }

    /// <summary>Space that would be freed if all but one copy were removed.</summary>
    public long WastedBytes => FileSize * Math.Max(0, Files.Count - 1);
}

/// <summary>Why a candidate file was not compared.</summary>
public enum DuplicateSkipReason
{
    /// <summary>Cloud placeholder or offline file; reading it could trigger a download.</summary>
    CloudOrOffline,

    /// <summary>The file could not be opened (locked, access denied, removed).</summary>
    Unreadable,

    /// <summary>The file is a hard link to another candidate, so it does not occupy extra space.</summary>
    HardLink,
}

public sealed record DuplicateSkip(string Path, DuplicateSkipReason Reason, string Message);

/// <summary>Result of a duplicate search.</summary>
public sealed class DuplicateSearchResult
{
    public required IReadOnlyList<DuplicateGroup> Groups { get; init; }

    /// <summary>Files that shared their size with at least one other file and were therefore examined.</summary>
    public required long CandidateFiles { get; init; }

    public required long BytesHashed { get; init; }

    public required IReadOnlyList<DuplicateSkip> Skipped { get; init; }

    public required IReadOnlyDictionary<DuplicateSkipReason, int> SkipCounts { get; init; }

    public long WastedBytes => Groups.Sum(g => g.WastedBytes);

    public long DuplicateFileCount => Groups.Sum(g => (long)g.Files.Count - 1);

    public int Count(DuplicateSkipReason reason) => SkipCounts.TryGetValue(reason, out int c) ? c : 0;
}

/// <summary>Reads file content for hashing. Abstracted so tests can inject failures.</summary>
public interface IFileContentSource
{
    /// <summary>Opens a file for sequential reading. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> on failure.</summary>
    FileStream OpenRead(string path);
}

/// <summary>Default content source: shared read access so files in use by other programs can still be read.</summary>
public sealed class FileContentSource : IFileContentSource
{
    public static FileContentSource Instance { get; } = new();

    public FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.SequentialScan,
        BufferSize = 0, // we read in large chunks ourselves
    });
}

/// <summary>
/// Finds files with identical content inside an already scanned tree. Only files that share their exact size are
/// read; candidates are narrowed with a hash of the first and last 16 KiB before full SHA-256 hashing. Files are
/// never modified. Cloud placeholders are skipped so the search never triggers downloads.
/// </summary>
public sealed class DuplicateFinder(IFileContentSource? source = null)
{
    private const int EdgeBytes = 16 * 1024;
    private const int BufferSize = 1024 * 1024;
    private const int MaxRecordedSkips = 2000;

    private readonly IFileContentSource _source = source ?? FileContentSource.Instance;

    public async Task<DuplicateSearchResult> FindAsync(
        FileSystemNode root,
        DuplicateOptions? options = null,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new DuplicateOptions();
        var skips = new ConcurrentQueue<DuplicateSkip>();
        var skipCounts = new ConcurrentDictionary<DuplicateSkipReason, int>();

        void Skip(string path, DuplicateSkipReason reason, string message)
        {
            skipCounts.AddOrUpdate(reason, 1, static (_, c) => c + 1);
            if (skips.Count < MaxRecordedSkips)
            {
                skips.Enqueue(new DuplicateSkip(path, reason, message));
            }
        }

        // 1. Group by exact size. Only sizes shared by two or more files can contain duplicates.
        progress?.Report(new DuplicateProgress("Comparing file sizes", 0, 0, 0, 0));
        var bySize = new Dictionary<long, List<FileSystemNode>>();
        long minSize = Math.Max(1, options.MinimumSize);
        foreach (FileSystemNode file in TreeQueries.EnumerateFiles(root))
        {
            if (file.Size < minSize)
            {
                continue;
            }

            if (!bySize.TryGetValue(file.Size, out List<FileSystemNode>? list))
            {
                bySize[file.Size] = list = [];
            }

            list.Add(file);
        }

        var candidates = new List<Candidate>();
        foreach (List<FileSystemNode> group in bySize.Values)
        {
            if (group.Count < 2)
            {
                continue;
            }

            foreach (FileSystemNode node in group)
            {
                if ((node.Flags & NodeFlags.CloudOrOffline) != 0)
                {
                    Skip(node.FullPath, DuplicateSkipReason.CloudOrOffline, "Cloud or offline file - not downloaded for comparison.");
                    continue;
                }

                candidates.Add(new Candidate(node, node.FullPath));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 2. Quick hash of the start and end of each candidate (also records the file identity for hard links).
        long quickTotal = candidates.Count;
        long quickDone = 0;
        await ForEachAsync(candidates, options, cancellationToken, c =>
        {
            ComputeQuickHash(c, Skip);
            long done = Interlocked.Increment(ref quickDone);
            if ((done & 31) == 0 || done == quickTotal)
            {
                progress?.Report(new DuplicateProgress("Comparing file beginnings and endings", done, quickTotal, 0, 0));
            }
        }).ConfigureAwait(false);

        // Drop unreadable files and collapse hard links (same volume + file ID) into one representative.
        var readable = new List<Candidate>();
        foreach (IGrouping<long, Candidate> sizeGroup in candidates.Where(c => c.QuickHash is not null).GroupBy(c => c.Node.Size))
        {
            var seenIds = new Dictionary<string, Candidate>();
            foreach (Candidate c in sizeGroup)
            {
                if (c.FileId is { } id && seenIds.TryGetValue(id, out Candidate? first))
                {
                    Skip(c.Path, DuplicateSkipReason.HardLink, $"Hard link to {first.Path} - removing it would not free space.");
                    continue;
                }

                if (c.FileId is { } newId)
                {
                    seenIds[newId] = c;
                }

                readable.Add(c);
            }
        }

        // 3. Full SHA-256 for files whose size and quick hash both match another file.
        List<Candidate> needFull = readable
            .GroupBy(c => (c.Node.Size, c.QuickHash!))
            .Where(g => g.Count() >= 2)
            .SelectMany(g => g)
            .ToList();

        long bytesTotal = needFull.Where(c => !c.QuickIsFull).Sum(c => c.Node.Size);
        long bytesDone = 0, filesDone = 0;
        await ForEachAsync(needFull, options, cancellationToken, c =>
        {
            if (c.QuickIsFull)
            {
                c.FullHash = c.QuickHash;
            }
            else
            {
                ComputeFullHash(c, Skip, cancellationToken, read =>
                {
                    long b = Interlocked.Add(ref bytesDone, read);
                    progress?.Report(new DuplicateProgress("Verifying identical content", Interlocked.Read(ref filesDone), needFull.Count, b, bytesTotal));
                });
            }

            Interlocked.Increment(ref filesDone);
        }).ConfigureAwait(false);

        // 4. Build groups of byte-identical files.
        var groups = needFull
            .Where(c => c.FullHash is not null)
            .GroupBy(c => (c.Node.Size, c.FullHash!))
            .Where(g => g.Count() >= 2)
            .Select(g => new DuplicateGroup
            {
                Hash = g.Key.Item2,
                FileSize = g.Key.Size,
                Files = g.Select(c => new DuplicateFile(c.Node, c.Path, c.Node.Size, c.Node.LastWriteTimeUtc))
                         .OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
                         .ToList(),
            })
            .OrderByDescending(g => g.WastedBytes)
            .ThenBy(g => g.Files[0].FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        progress?.Report(new DuplicateProgress("Done", needFull.Count, needFull.Count, bytesTotal, bytesTotal));
        return new DuplicateSearchResult
        {
            Groups = groups,
            CandidateFiles = candidates.Count,
            BytesHashed = Interlocked.Read(ref bytesDone),
            Skipped = skips.ToArray(),
            SkipCounts = skipCounts.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
    }

    private static async Task ForEachAsync(IEnumerable<Candidate> items, DuplicateOptions options, CancellationToken ct, Action<Candidate> body)
    {
        // File reading is blocking I/O; run it on the thread pool with a small, disk-friendly degree of parallelism.
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(options.DegreeOfParallelism, 1, 8), CancellationToken = ct },
            (item, token) =>
            {
                token.ThrowIfCancellationRequested();
                body(item);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
    }

    private void ComputeQuickHash(Candidate c, Action<string, DuplicateSkipReason, string> skip)
    {
        try
        {
            using FileStream stream = _source.OpenRead(c.Path);
            if (stream.Length != c.Node.Size)
            {
                skip(c.Path, DuplicateSkipReason.Unreadable, "The file changed since the folder was scanned.");
                return;
            }

            c.FileId = FileIdentity.TryGet(stream.SafeFileHandle);
            long length = stream.Length;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(EdgeBytes * 2);
            try
            {
                int total;
                if (length <= EdgeBytes * 2)
                {
                    total = ReadExactly(stream, buffer, 0, (int)length);
                    c.QuickIsFull = true;
                }
                else
                {
                    total = ReadExactly(stream, buffer, 0, EdgeBytes);
                    stream.Seek(-EdgeBytes, SeekOrigin.End);
                    total += ReadExactly(stream, buffer, EdgeBytes, EdgeBytes);
                }

                c.QuickHash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, total)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skip(c.Path, DuplicateSkipReason.Unreadable, ex.Message);
        }
    }

    private void ComputeFullHash(Candidate c, Action<string, DuplicateSkipReason, string> skip, CancellationToken ct, Action<int> onRead)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using FileStream stream = _source.OpenRead(c.Path);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long read = 0;
            int n;
            while ((n = stream.Read(buffer, 0, BufferSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, n);
                read += n;
                onRead(n);
            }

            if (read != c.Node.Size)
            {
                skip(c.Path, DuplicateSkipReason.Unreadable, "The file changed while it was being compared.");
                return;
            }

            c.FullHash = Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skip(c.Path, DuplicateSkipReason.Unreadable, ex.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, offset + total, count - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private sealed class Candidate(FileSystemNode node, string path)
    {
        public FileSystemNode Node { get; } = node;

        public string Path { get; } = path;

        public string? QuickHash { get; set; }

        /// <summary>True when the quick hash already covered the whole file.</summary>
        public bool QuickIsFull { get; set; }

        public string? FullHash { get; set; }

        public string? FileId { get; set; }
    }
}

/// <summary>Reads the NTFS volume serial number and file index, which are equal for hard links to the same file.</summary>
internal static class FileIdentity
{
    public static string? TryGet(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid)
        {
            return null;
        }

        return GetFileInformationByHandle(handle, out ByHandleFileInformation info)
            ? $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}"
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        // FILETIME is two DWORDs (4-byte aligned); using long here would insert padding and shift the layout.
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}
