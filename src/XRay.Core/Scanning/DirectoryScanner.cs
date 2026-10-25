using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace XRay.Core.Scanning;

/// <summary>Scans a directory tree and builds an in-memory size tree.</summary>
public interface IDirectoryScanner
{
    /// <summary>
    /// Scans <paramref name="rootPath"/> recursively. Throws <see cref="OperationCanceledException"/> when cancelled
    /// and <see cref="DirectoryNotFoundException"/> if the root does not exist.
    /// </summary>
    Task<ScanResult> ScanAsync(
        string rootPath,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Parallel breadth-first directory scanner. A small pool of workers pulls directories from a channel, lists them
/// with <see cref="IDirectoryReader"/>, records file metadata and queues sub-directories. Errors on individual
/// directories are recorded as <see cref="ScanIssue"/>s and never abort the scan.
/// </summary>
public sealed class DirectoryScanner : IDirectoryScanner
{
    private readonly IDirectoryReader _reader;
    private readonly ILogger _logger;

    public DirectoryScanner(IDirectoryReader? reader = null, ILogger<DirectoryScanner>? logger = null)
    {
        _reader = reader ?? FileSystemDirectoryReader.Instance;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async Task<ScanResult> ScanAsync(
        string rootPath,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        options ??= ScanOptions.Default;

        string fullRoot = NormalizeRoot(rootPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"The folder '{fullRoot}' does not exist or is not accessible.");
        }

        var startedUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var state = new ScanState(options, AllocationEstimator.ForPath(fullRoot));
        var root = FileSystemNode.CreateRoot(fullRoot);
        root.LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(fullRoot);

        _logger.LogInformation("Scan started: {Root}", fullRoot);

        int workers = options.DegreeOfParallelism > 0
            ? options.DegreeOfParallelism
            : Math.Clamp(Environment.ProcessorCount, 2, 8);

        var channel = Channel.CreateUnbounded<FileSystemNode>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

        state.Pending = 1;
        channel.Writer.TryWrite(root);

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task progressTask = progress is null
            ? Task.CompletedTask
            : ReportProgressAsync(state, stopwatch, progress, options.ProgressInterval, progressCts.Token);

        // Directory listing is blocking I/O, so the workers run on thread-pool threads to keep the caller
        // (typically the UI thread) free.
        var tasks = new Task[workers];
        for (int i = 0; i < workers; i++)
        {
            tasks[i] = Task.Run(() => WorkerAsync(channel, state, cancellationToken), cancellationToken);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            channel.Writer.TryComplete();
            _logger.LogInformation("Scan cancelled after {Elapsed}: {Root}", stopwatch.Elapsed, fullRoot);
            throw;
        }
        finally
        {
            progressCts.Cancel();
            try
            {
                await progressTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the reporter is stopped.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        TreeAggregator.Aggregate(root);
        stopwatch.Stop();

        progress?.Report(new ScanProgress(
            Interlocked.Read(ref state.Files),
            Interlocked.Read(ref state.Directories),
            root.Size,
            state.TotalIssues,
            null,
            stopwatch.Elapsed));

        var result = new ScanResult
        {
            Root = root,
            StartedUtc = startedUtc,
            Elapsed = stopwatch.Elapsed,
            Options = options,
            ClusterSize = state.Allocation.ClusterSize,
            Issues = state.Issues.ToArray(),
            IssueCounts = state.IssueCounts.ToDictionary(kv => kv.Key, kv => kv.Value),
        };

        _logger.LogInformation(
            "Scan completed in {Elapsed}: {Files} files, {Dirs} directories, {Bytes} bytes, {Issues} issues",
            result.Elapsed, root.FileCount, root.DirectoryCount, root.Size, result.TotalIssueCount);
        return result;
    }

    /// <summary>Returns a full path; drive roots keep their trailing separator, other paths lose it.</summary>
    public static string NormalizeRoot(string path)
    {
        string full = Path.GetFullPath(path.Trim());
        string? pathRoot = Path.GetPathRoot(full);
        if (!string.Equals(full, pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            full = Path.TrimEndingDirectorySeparator(full);
        }

        return full;
    }

    private async Task WorkerAsync(Channel<FileSystemNode> channel, ScanState state, CancellationToken ct)
    {
        await foreach (FileSystemNode directory in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            // ReadAllAsync only observes the token while waiting, not while items are available.
            ct.ThrowIfCancellationRequested();
            try
            {
                ProcessDirectory(directory, channel.Writer, state, ct);
            }
            finally
            {
                if (Interlocked.Decrement(ref state.Pending) == 0)
                {
                    channel.Writer.TryComplete();
                }
            }
        }
    }

    private void ProcessDirectory(
        FileSystemNode directory,
        ChannelWriter<FileSystemNode> writer,
        ScanState state,
        CancellationToken ct)
    {
        string path = directory.FullPath;
        state.CurrentPath = path;
        Interlocked.Increment(ref state.Directories);

        IEnumerator<DirectoryEntry>? enumerator = null;
        try
        {
            enumerator = _reader.Read(path).GetEnumerator();
            int sinceCheck = 0;
            while (true)
            {
                DirectoryEntry entry;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    entry = enumerator.Current;
                }
                catch (UnauthorizedAccessException ex)
                {
                    MarkDirectory(directory, NodeFlags.AccessDenied, ScanIssueKind.AccessDenied, path, ex, state);
                    break;
                }
                catch (DirectoryNotFoundException ex)
                {
                    MarkDirectory(directory, NodeFlags.Vanished, ScanIssueKind.Vanished, path, ex, state);
                    break;
                }
                catch (PathTooLongException ex)
                {
                    MarkDirectory(directory, NodeFlags.Error, ScanIssueKind.PathTooLong, path, ex, state);
                    break;
                }
                catch (IOException ex)
                {
                    MarkDirectory(directory, NodeFlags.Error, ScanIssueKind.IoError, path, ex, state);
                    break;
                }

                if (++sinceCheck >= 512)
                {
                    sinceCheck = 0;
                    ct.ThrowIfCancellationRequested();
                }

                if (entry.IsDirectory)
                {
                    var child = new FileSystemNode(entry.Name, directory, isDirectory: true)
                    {
                        LastWriteTimeUtc = entry.LastWriteTimeUtc,
                    };
                    directory.AddChild(child);

                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 &&
                        state.Options.SkipLinkedDirectories &&
                        _reader.IsLink(Path.Join(path, entry.Name)))
                    {
                        child.Flags |= NodeFlags.ReparsePointSkipped;
                        state.AddIssue(new ScanIssue(
                            Path.Join(path, entry.Name),
                            ScanIssueKind.ReparsePointSkipped,
                            "Symbolic link, junction or mount point - not followed."));
                        continue;
                    }

                    Interlocked.Increment(ref state.Pending);
                    writer.TryWrite(child);
                }
                else
                {
                    long allocated = state.Allocation.Estimate(Path.Join(path, entry.Name), entry.Length, entry.Attributes);
                    var file = new FileSystemNode(entry.Name, directory, isDirectory: false)
                    {
                        Size = entry.Length,
                        AllocatedSize = allocated,
                        FileCount = 1,
                        LastWriteTimeUtc = entry.LastWriteTimeUtc,
                        Flags = AllocationEstimator.IsCloudOrOffline(entry.Attributes) ? NodeFlags.CloudOrOffline : NodeFlags.None,
                    };
                    directory.AddChild(file);
                    Interlocked.Increment(ref state.Files);
                    Interlocked.Add(ref state.Bytes, entry.Length);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // GetEnumerator itself may throw for some reader implementations.
            var kind = ex is UnauthorizedAccessException ? ScanIssueKind.AccessDenied
                : ex is DirectoryNotFoundException ? ScanIssueKind.Vanished
                : ScanIssueKind.IoError;
            var flag = kind switch
            {
                ScanIssueKind.AccessDenied => NodeFlags.AccessDenied,
                ScanIssueKind.Vanished => NodeFlags.Vanished,
                _ => NodeFlags.Error,
            };
            MarkDirectory(directory, flag, kind, path, ex, state);
        }
        finally
        {
            enumerator?.Dispose();
            directory.TrimChildren();
        }
    }

    private void MarkDirectory(
        FileSystemNode directory,
        NodeFlags flag,
        ScanIssueKind kind,
        string path,
        Exception ex,
        ScanState state)
    {
        directory.Flags |= flag;
        state.AddIssue(new ScanIssue(path, kind, ex.Message));
        _logger.LogDebug("Scan issue {Kind} at {Path}: {Message}", kind, path, ex.Message);
    }

    private static async Task ReportProgressAsync(
        ScanState state,
        Stopwatch stopwatch,
        IProgress<ScanProgress> progress,
        TimeSpan interval,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(150));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            progress.Report(new ScanProgress(
                Interlocked.Read(ref state.Files),
                Interlocked.Read(ref state.Directories),
                Interlocked.Read(ref state.Bytes),
                state.TotalIssues,
                state.CurrentPath,
                stopwatch.Elapsed));
        }
    }

    private sealed class ScanState(ScanOptions options, AllocationEstimator allocation)
    {
        private long _recorded;

        public long Pending;
        public long Files;
        public long Directories;
        public long Bytes;
        public volatile string? CurrentPath;

        public ScanOptions Options { get; } = options;

        public AllocationEstimator Allocation { get; } = allocation;

        public ConcurrentQueue<ScanIssue> Issues { get; } = new();

        public ConcurrentDictionary<ScanIssueKind, long> IssueCounts { get; } = new();

        public long TotalIssues => IssueCounts.Values.Sum();

        public void AddIssue(ScanIssue issue)
        {
            IssueCounts.AddOrUpdate(issue.Kind, 1, static (_, count) => count + 1);
            if (Interlocked.Increment(ref _recorded) <= Options.MaxRecordedIssues)
            {
                Issues.Enqueue(issue);
            }
        }
    }
}
