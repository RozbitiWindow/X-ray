using System.Runtime.InteropServices;
using XRay.Core.Duplicates;
using XRay.Core.Scanning;

namespace XRay.Core.Tests;

public class DuplicatesTests
{
    private static readonly DuplicateOptions AnySize = new() { MinimumSize = 1 };

    private static string Write(TestDirectory dir, string relative, byte[] content)
    {
        string full = Path.Combine(dir.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    private static byte[] Pattern(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static async Task<DuplicateSearchResult> Find(TestDirectory dir, DuplicateOptions? options = null, IFileContentSource? source = null)
    {
        ScanResult scan = await new DirectoryScanner().ScanAsync(dir.Path);
        return await new DuplicateFinder(source).FindAsync(scan.Root, options ?? AnySize);
    }

    [Fact]
    public async Task Identical_files_are_grouped_and_wasted_space_is_counted()
    {
        using var dir = new TestDirectory();
        byte[] data = Pattern(200_000, 1);
        Write(dir, @"a\photo.jpg", data);
        Write(dir, @"b\photo (copy).jpg", data);
        Write(dir, @"c\photo.jpg", data);
        Write(dir, "unique.bin", Pattern(200_000, 2));

        DuplicateSearchResult result = await Find(dir);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal(3, group.Files.Count);
        Assert.Equal(200_000, group.FileSize);
        Assert.Equal(400_000, group.WastedBytes);
        Assert.Equal(400_000, result.WastedBytes);
        Assert.Equal(2, result.DuplicateFileCount);
    }

    [Fact]
    public async Task Same_size_and_same_edges_but_different_middle_is_not_a_duplicate()
    {
        using var dir = new TestDirectory();
        byte[] a = Pattern(500_000, 3);
        byte[] b = (byte[])a.Clone();
        b[250_000] ^= 0xFF; // differs only in the middle, outside the quick-hash windows
        Write(dir, "a.bin", a);
        Write(dir, "b.bin", b);

        DuplicateSearchResult result = await Find(dir);

        Assert.Empty(result.Groups);
        Assert.True(result.BytesHashed >= 1_000_000, "full content must be hashed to tell them apart");
    }

    [Fact]
    public async Task Small_identical_files_use_the_quick_hash_as_full_hash()
    {
        using var dir = new TestDirectory();
        Write(dir, "x.txt", "hello world"u8.ToArray());
        Write(dir, "y.txt", "hello world"u8.ToArray());
        Write(dir, "z.txt", "hello there"u8.ToArray());

        DuplicateSearchResult result = await Find(dir);

        Assert.Equal(2, Assert.Single(result.Groups).Files.Count);
        Assert.Equal(0, result.BytesHashed);
    }

    [Fact]
    public async Task Minimum_size_and_empty_files_are_ignored()
    {
        using var dir = new TestDirectory();
        Write(dir, "e1", []);
        Write(dir, "e2", []);
        Write(dir, "s1", Pattern(100, 4));
        Write(dir, "s2", Pattern(100, 4));

        Assert.DoesNotContain((await Find(dir)).Groups, g => g.FileSize == 0);
        Assert.Empty((await Find(dir, new DuplicateOptions { MinimumSize = 1000 })).Groups);
    }

    [Fact]
    public async Task Hard_links_are_not_reported_as_duplicates()
    {
        using var dir = new TestDirectory();
        string original = Write(dir, "original.bin", Pattern(50_000, 5));
        string link = Path.Combine(dir.Path, "link.bin");
        if (!CreateHardLinkW(link, original, IntPtr.Zero))
        {
            return; // File system without hard links; nothing to verify.
        }

        DuplicateSearchResult result = await Find(dir);

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.Count(DuplicateSkipReason.HardLink));
    }

    [Fact]
    public async Task Unreadable_files_are_skipped_and_reported()
    {
        using var dir = new TestDirectory();
        byte[] data = Pattern(10_000, 6);
        Write(dir, "a.bin", data);
        Write(dir, "b.bin", data);
        string locked = Write(dir, "c.bin", data);

        DuplicateSearchResult result = await Find(dir, source: new FailingSource(locked));

        Assert.Equal(2, Assert.Single(result.Groups).Files.Count);
        Assert.Equal(1, result.Count(DuplicateSkipReason.Unreadable));
        Assert.Contains(result.Skipped, s => s.Path == locked);
    }

    [Fact]
    public async Task Cloud_placeholders_are_never_read()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\does-not-exist");
        FileSystemNode a = root.AddFile("a.bin", 100);
        FileSystemNode b = root.AddFile("b.bin", 100);
        a.Flags = NodeFlags.CloudOrOffline;
        b.Flags = NodeFlags.CloudOrOffline;
        TreeAggregator.Aggregate(root);

        DuplicateSearchResult result = await new DuplicateFinder(new FailingSource(null)).FindAsync(root, AnySize);

        Assert.Empty(result.Groups);
        Assert.Equal(2, result.Count(DuplicateSkipReason.CloudOrOffline));
    }

    [Fact]
    public async Task Search_honours_cancellation()
    {
        using var dir = new TestDirectory();
        for (int i = 0; i < 20; i++)
        {
            Write(dir, $"f{i}.bin", Pattern(100_000, 7));
        }

        ScanResult scan = await new DirectoryScanner().ScanAsync(dir.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DuplicateFinder().FindAsync(scan.Root, AnySize, null, cts.Token));
    }

    // ---- Cleanup ----

    private static async Task<(TestDirectory Dir, DuplicateGroup Group)> GroupOfThree()
    {
        var dir = new TestDirectory();
        byte[] data = Pattern(30_000, 8);
        Write(dir, @"keep\a.bin", data);
        Write(dir, @"x\b.bin", data);
        Write(dir, @"y\c.bin", data);
        DuplicateSearchResult result = await Find(dir);
        return (dir, Assert.Single(result.Groups));
    }

    private static DuplicateCleaner Cleaner(FakeRecycler recycler, ProtectedLocations? protectedLocations = null, bool hasRecycleBin = true) =>
        new(recycler, protectedLocations ?? new ProtectedLocations([]), _ => hasRecycleBin);

    private static HashSet<string> Select(DuplicateGroup g, params int[] indexes) =>
        indexes.Select(i => g.Files[i].FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task Cleanup_recycles_selected_copies_and_keeps_the_rest()
    {
        var (dir, group) = await GroupOfThree();
        using (dir)
        {
            var recycler = new FakeRecycler();
            CleanupReport report = Cleaner(recycler).Clean([group], Select(group, 1, 2));

            Assert.Equal(2, report.RecycledCount);
            Assert.Equal(60_000, report.RecycledBytes);
            Assert.True(File.Exists(group.Files[0].FullPath));
            Assert.False(File.Exists(group.Files[1].FullPath));
            Assert.Equal(2, recycler.Recycled.Count);
        }
    }

    [Fact]
    public async Task Selecting_every_copy_is_rejected_and_nothing_is_removed()
    {
        var (dir, group) = await GroupOfThree();
        using (dir)
        {
            var recycler = new FakeRecycler();
            DuplicateCleaner cleaner = Cleaner(recycler);
            HashSet<string> all = Select(group, 0, 1, 2);

            Assert.NotEmpty(cleaner.Validate([group], all));
            CleanupReport report = cleaner.Clean([group], all);

            Assert.Empty(recycler.Recycled);
            Assert.All(report.Items, i => Assert.Equal(CleanupOutcome.SkippedLastCopy, i.Outcome));
            Assert.All(group.Files, f => Assert.True(File.Exists(f.FullPath)));
        }
    }

    [Fact]
    public async Task Nothing_is_removed_when_the_kept_copy_has_disappeared()
    {
        var (dir, group) = await GroupOfThree();
        using (dir)
        {
            File.Delete(group.Files[0].FullPath); // the only unselected copy vanishes before cleanup
            var recycler = new FakeRecycler();

            CleanupReport report = Cleaner(recycler).Clean([group], Select(group, 1, 2));

            Assert.Empty(recycler.Recycled);
            Assert.True(File.Exists(group.Files[1].FullPath));
            Assert.All(report.Items, i => Assert.Equal(CleanupOutcome.SkippedLastCopy, i.Outcome));
        }
    }

    [Fact]
    public async Task Files_modified_after_the_scan_are_skipped()
    {
        var (dir, group) = await GroupOfThree();
        using (dir)
        {
            File.AppendAllText(group.Files[1].FullPath, "changed");
            var recycler = new FakeRecycler();

            CleanupReport report = Cleaner(recycler).Clean([group], Select(group, 1));

            Assert.Empty(recycler.Recycled);
            Assert.Equal(CleanupOutcome.SkippedChanged, Assert.Single(report.Items).Outcome);
        }
    }

    [Fact]
    public async Task Protected_locations_and_drives_without_recycle_bin_are_refused()
    {
        var (dir, group) = await GroupOfThree();
        using (dir)
        {
            var recycler = new FakeRecycler();
            var guard = new ProtectedLocations([Path.Combine(dir.Path, "x")]);
            DuplicateCleaner cleaner = Cleaner(recycler, guard);

            Assert.Contains(cleaner.Validate([group], Select(group, 1)), p => p.Contains("protected"));
            Assert.Equal(CleanupOutcome.SkippedProtected, Assert.Single(cleaner.Clean([group], Select(group, 1)).Items).Outcome);

            CleanupReport noBin = Cleaner(recycler, hasRecycleBin: false).Clean([group], Select(group, 2));
            Assert.Equal(CleanupOutcome.SkippedNoRecycleBin, Assert.Single(noBin.Items).Outcome);
            Assert.Empty(recycler.Recycled);
        }
    }

    [Fact]
    public async Task Recycler_failures_are_reported_per_file()
    {
        var (dir, group) = await GroupOfThree();
        using (dir)
        {
            var recycler = new FakeRecycler { FailOn = group.Files[2].FullPath };

            CleanupReport report = Cleaner(recycler).Clean([group], Select(group, 1, 2));

            Assert.Equal(1, report.RecycledCount);
            Assert.Equal(CleanupOutcome.Failed, report.Items.Single(i => i.Path == group.Files[2].FullPath).Outcome);
        }
    }

    [Fact]
    public void Protected_locations_match_by_folder_prefix_only()
    {
        var guard = new ProtectedLocations([@"C:\Windows"]);
        Assert.True(guard.IsProtected(@"C:\Windows\System32\x.dll"));
        Assert.True(guard.IsProtected(@"c:\windows\a.txt"));
        Assert.False(guard.IsProtected(@"C:\WindowsBackup\a.txt"));
        Assert.False(guard.IsProtected(@"D:\Windows\a.txt"));
    }

    private sealed class FakeRecycler : IFileRecycler
    {
        public List<string> Recycled { get; } = [];

        public string? FailOn { get; init; }

        public void Recycle(string path)
        {
            if (string.Equals(path, FailOn, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            // Tests must not touch the real Recycle Bin; deleting the synthetic temp file simulates it.
            File.Delete(path);
            Recycled.Add(path);
        }
    }

    private sealed class FailingSource(string? failPath) : IFileContentSource
    {
        public FileStream OpenRead(string path)
        {
            if (failPath is null || string.Equals(path, failPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            return FileContentSource.Instance.OpenRead(path);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr securityAttributes);
}
