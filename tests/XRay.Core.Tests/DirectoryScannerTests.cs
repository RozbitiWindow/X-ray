using XRay.Core.Scanning;

namespace XRay.Core.Tests;

public class DirectoryScannerTests
{
    [Fact]
    public async Task Scan_aggregates_sizes_and_counts()
    {
        using var dir = new TestDirectory();
        dir.CreateFile("a.bin", 1000);
        dir.CreateFile(@"sub\b.bin", 2000);
        dir.CreateFile(@"sub\deep\c.bin", 3000);
        dir.CreateDirectory("empty");

        ScanResult result = await new DirectoryScanner().ScanAsync(dir.Path);

        FileSystemNode root = result.Root;
        Assert.Equal(6000, root.Size);
        Assert.Equal(3, root.FileCount);
        Assert.Equal(3, root.DirectoryCount); // sub, sub\deep, empty
        FileSystemNode sub = root.Children.Single(c => c.Name == "sub");
        Assert.Equal(5000, sub.Size);
        Assert.Equal(2, sub.FileCount);
        Assert.Equal(1, sub.DirectoryCount);
        FileSystemNode empty = root.Children.Single(c => c.Name == "empty");
        Assert.Equal(0, empty.Size);
        Assert.False(empty.IsIncomplete);
        Assert.Equal(0, result.TotalIssueCount);
    }

    [Fact]
    public async Task Allocated_size_is_cluster_rounded_and_not_smaller_than_logical()
    {
        using var dir = new TestDirectory();
        dir.CreateFile("small.bin", 10);
        dir.CreateFile("large.bin", 100_000);

        ScanResult result = await new DirectoryScanner().ScanAsync(dir.Path);

        Assert.True(result.ClusterSize > 0);
        foreach (FileSystemNode file in result.Root.Children)
        {
            Assert.True(file.AllocatedSize >= file.Size);
            Assert.Equal(0, file.AllocatedSize % result.ClusterSize);
        }
    }

    [Fact]
    public async Task Full_path_and_relative_path_are_reconstructed()
    {
        using var dir = new TestDirectory();
        string file = dir.CreateFile(@"x\y\z.txt", 5);

        ScanResult result = await new DirectoryScanner().ScanAsync(dir.Path);
        FileSystemNode z = TreeQueries.EnumerateFiles(result.Root).Single();

        Assert.Equal(file, z.FullPath, ignoreCase: true);
        Assert.Equal(@"x\y\z.txt", z.RelativePath);
        Assert.Same(z, TreeQueries.FindByPath(result.Root, file));
    }

    [Fact]
    public async Task Missing_root_throws_directory_not_found()
    {
        string missing = Path.Combine(Path.GetTempPath(), "xray-tests", Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new DirectoryScanner().ScanAsync(missing));
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        var reader = new FakeReader(slow: true);
        for (int i = 0; i < 200; i++)
        {
            reader.Add(@"C:\root", new DirectoryEntry($"d{i}", true, 0, FileAttributes.Directory, DateTime.UtcNow));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var scanner = new DirectoryScanner(reader);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(RealTempRoot(reader), cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Access_denied_directories_are_flagged_not_treated_as_empty()
    {
        using var dir = new TestDirectory();
        var reader = new FakeReader();
        reader.Add(dir.Path, new DirectoryEntry("ok", true, 0, FileAttributes.Directory, DateTime.UtcNow));
        reader.Add(dir.Path, new DirectoryEntry("locked", true, 0, FileAttributes.Directory, DateTime.UtcNow));
        reader.Add(dir.Path, new DirectoryEntry("f.bin", false, 100, FileAttributes.Normal, DateTime.UtcNow));
        reader.Add(Path.Join(dir.Path, "ok"), new DirectoryEntry("g.bin", false, 50, FileAttributes.Normal, DateTime.UtcNow));
        reader.Fail(Path.Join(dir.Path, "locked"), new UnauthorizedAccessException("Access to the path is denied."));

        ScanResult result = await new DirectoryScanner(reader).ScanAsync(dir.Path);

        FileSystemNode locked = result.Root.Children.Single(c => c.Name == "locked");
        Assert.True(locked.IsUnreadable);
        Assert.True((locked.Flags & NodeFlags.AccessDenied) != 0);
        Assert.True(result.Root.IsIncomplete);
        Assert.False(result.Root.Children.Single(c => c.Name == "ok").IsIncomplete);
        Assert.Equal(150, result.Root.Size);
        Assert.Equal(1, result.Count(ScanIssueKind.AccessDenied));
        Assert.True(result.IsIncomplete);
        Assert.Contains(result.Issues, i => i.Kind == ScanIssueKind.AccessDenied && i.Path.EndsWith("locked"));
    }

    [Fact]
    public async Task Directory_vanishing_mid_scan_is_recorded()
    {
        using var dir = new TestDirectory();
        var reader = new FakeReader();
        reader.Add(dir.Path, new DirectoryEntry("gone", true, 0, FileAttributes.Directory, DateTime.UtcNow));
        reader.Fail(Path.Join(dir.Path, "gone"), new DirectoryNotFoundException("gone"));

        ScanResult result = await new DirectoryScanner(reader).ScanAsync(dir.Path);

        Assert.Equal(1, result.Count(ScanIssueKind.Vanished));
        Assert.True((result.Root.Children[0].Flags & NodeFlags.Vanished) != 0);
    }

    [Fact]
    public async Task Error_after_partial_listing_keeps_entries_read_so_far()
    {
        using var dir = new TestDirectory();
        var reader = new FakeReader();
        reader.Add(dir.Path, new DirectoryEntry("a.bin", false, 10, FileAttributes.Normal, DateTime.UtcNow));
        reader.FailAfterEntries(dir.Path, new IOException("The network name is no longer available."));

        ScanResult result = await new DirectoryScanner(reader).ScanAsync(dir.Path);

        Assert.Equal(10, result.Root.Size);
        Assert.Equal(1, result.Count(ScanIssueKind.IoError));
        Assert.True(result.Root.IsUnreadable);
    }

    [Fact]
    public async Task Linked_directories_are_not_followed()
    {
        using var dir = new TestDirectory();
        var reader = new FakeReader();
        reader.Add(dir.Path, new DirectoryEntry("loop", true, 0, FileAttributes.Directory | FileAttributes.ReparsePoint, DateTime.UtcNow));
        // If the scanner followed the link, it would recurse into the root forever.
        reader.Add(Path.Join(dir.Path, "loop"), new DirectoryEntry("loop", true, 0, FileAttributes.Directory | FileAttributes.ReparsePoint, DateTime.UtcNow));
        reader.Links.Add(Path.Join(dir.Path, "loop"));

        ScanResult result = await new DirectoryScanner(reader).ScanAsync(dir.Path);

        FileSystemNode loop = result.Root.Children.Single();
        Assert.True(loop.IsReparsePoint);
        Assert.False(loop.HasChildren);
        Assert.Equal(1, result.Count(ScanIssueKind.ReparsePointSkipped));
        Assert.False(result.IsIncomplete);
    }

    [Fact]
    public async Task Real_junction_is_not_followed()
    {
        using var dir = new TestDirectory();
        dir.CreateFile(@"target\data.bin", 4096);
        string link = Path.Combine(dir.Path, "link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(dir.Path, "target"));
        }
        catch (IOException)
        {
            return; // Symbolic links need Developer Mode or elevation; the fake-reader test covers the logic.
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        ScanResult result = await new DirectoryScanner().ScanAsync(dir.Path);

        Assert.Equal(4096, result.Root.Size);
        Assert.True(result.Root.Children.Single(c => c.Name == "link").IsReparsePoint);
    }

    [Fact]
    public async Task Progress_is_reported()
    {
        using var dir = new TestDirectory();
        for (int i = 0; i < 20; i++)
        {
            dir.CreateFile($@"d{i % 4}\f{i}.bin", 100);
        }

        var reports = new List<ScanProgress>();
        var progress = new SyncProgress<ScanProgress>(reports.Add);
        ScanResult result = await new DirectoryScanner().ScanAsync(dir.Path, progress: progress);

        Assert.NotEmpty(reports);
        ScanProgress last = reports[^1];
        Assert.Equal(20, last.FilesScanned);
        Assert.Equal(2000, last.BytesFound);
        Assert.Equal(result.Root.Size, last.BytesFound);
    }

    [Fact]
    public async Task Issue_detail_list_is_capped_but_counts_are_complete()
    {
        using var dir = new TestDirectory();
        var reader = new FakeReader();
        for (int i = 0; i < 30; i++)
        {
            string name = $"d{i}";
            reader.Add(dir.Path, new DirectoryEntry(name, true, 0, FileAttributes.Directory, DateTime.UtcNow));
            reader.Fail(Path.Join(dir.Path, name), new UnauthorizedAccessException());
        }

        ScanResult result = await new DirectoryScanner(reader).ScanAsync(dir.Path, new ScanOptions { MaxRecordedIssues = 10 });

        Assert.Equal(10, result.Issues.Count);
        Assert.Equal(30, result.Count(ScanIssueKind.AccessDenied));
    }

    [Fact]
    public void Normalize_root_keeps_drive_separator_only()
    {
        Assert.Equal(@"C:\", DirectoryScanner.NormalizeRoot(@"C:\"));
        Assert.Equal(@"C:\Users", DirectoryScanner.NormalizeRoot(@"C:\Users\"));
    }

    private static string RealTempRoot(FakeReader reader)
    {
        // The scanner checks that the root exists on disk; map the fake root onto a real temp folder.
        string temp = Path.Combine(Path.GetTempPath(), "xray-tests");
        Directory.CreateDirectory(temp);
        reader.Alias(temp, @"C:\root");
        return temp;
    }

    /// <summary>Progress implementation that invokes the callback synchronously (Progress&lt;T&gt; posts asynchronously).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly object _gate = new();

        public void Report(T value)
        {
            lock (_gate)
            {
                handler(value);
            }
        }
    }

    private sealed class FakeReader(bool slow = false) : IDirectoryReader
    {
        private readonly Dictionary<string, List<DirectoryEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _failAfter = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string dir, DirectoryEntry entry)
        {
            if (!_entries.TryGetValue(dir, out var list))
            {
                _entries[dir] = list = [];
            }

            list.Add(entry);
        }

        public void Fail(string dir, Exception ex) => _failures[dir] = ex;

        public void FailAfterEntries(string dir, Exception ex) => _failAfter[dir] = ex;

        public void Alias(string real, string fake) => _aliases[real] = fake;

        public bool IsLink(string path) => Links.Contains(path);

        public IEnumerable<DirectoryEntry> Read(string directoryPath)
        {
            if (_aliases.TryGetValue(directoryPath, out string? fake))
            {
                directoryPath = fake;
            }
            else
            {
                foreach (var (real, alias) in _aliases)
                {
                    if (directoryPath.StartsWith(real + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        directoryPath = alias + directoryPath[real.Length..];
                    }
                }
            }

            return Enumerate(directoryPath);
        }

        private IEnumerable<DirectoryEntry> Enumerate(string directoryPath)
        {
            if (_failures.TryGetValue(directoryPath, out Exception? ex))
            {
                throw ex;
            }

            if (slow)
            {
                Thread.Sleep(20);
            }

            if (_entries.TryGetValue(directoryPath, out var list))
            {
                foreach (DirectoryEntry e in list)
                {
                    yield return e;
                }
            }
            else if (slow)
            {
                // Every directory has many children in slow mode, making the tree effectively endless.
                for (int i = 0; i < 5; i++)
                {
                    yield return new DirectoryEntry($"n{i}", true, 0, FileAttributes.Directory, DateTime.UtcNow);
                }
            }

            if (_failAfter.TryGetValue(directoryPath, out Exception? after))
            {
                throw after;
            }
        }
    }
}
