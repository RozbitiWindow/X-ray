using XRay.Core.History;
using XRay.Core.Scanning;

namespace XRay.Core.Tests;

public class SnapshotTests
{
    private static ScanResult Result(FileSystemNode root) => new()
    {
        Root = root,
        StartedUtc = DateTime.UtcNow,
        Elapsed = TimeSpan.FromSeconds(1),
        Options = ScanOptions.Default,
        ClusterSize = 4096,
        Issues = [],
        IssueCounts = new Dictionary<ScanIssueKind, long>(),
    };

    private static StorageSnapshot Snapshot(Action<FileSystemNode> build, DateTime created, string rootPath = @"C:\data", int depth = 6)
    {
        FileSystemNode root = FileSystemNode.CreateRoot(rootPath);
        build(root);
        TreeAggregator.Aggregate(root);
        return SnapshotBuilder.Create(Result(root), depth, createdUtc: created);
    }

    private static readonly DateTime T1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Builder_records_directories_up_to_depth()
    {
        StorageSnapshot s = Snapshot(r =>
        {
            FileSystemNode a = r.AddDirectory("a");
            a.AddFile("f", 10);
            a.AddDirectory("b").AddDirectory("c").AddFile("g", 5);
        }, T1, depth: 2);

        Assert.Equal(["", "a", @"a\b"], s.Entries.Select(e => e.Path));
        Assert.Equal(15, s.Entries[0].Size);
        Assert.Equal(5, s.Entries[2].Size);
        Assert.Equal(2, s.Summary.MaxDepth);
        Assert.False(s.Summary.Truncated);
    }

    [Fact]
    public void Compare_detects_growth_shrinkage_additions_and_removals()
    {
        StorageSnapshot older = Snapshot(r =>
        {
            r.AddDirectory("grow").AddFile("f", 100);
            r.AddDirectory("shrink").AddFile("f", 500);
            r.AddDirectory("same").AddFile("f", 42);
            r.AddDirectory("old").AddFile("f", 7);
        }, T1);
        StorageSnapshot newer = Snapshot(r =>
        {
            r.AddDirectory("grow").AddFile("f", 1100);
            r.AddDirectory("shrink").AddFile("f", 200);
            r.AddDirectory("same").AddFile("f", 42);
            r.AddDirectory("new").AddFile("f", 9);
        }, T2);

        // Argument order should not matter.
        SnapshotComparison cmp = SnapshotComparer.Compare(newer, older);

        Assert.True(cmp.SameScope);
        Assert.Equal(T1, cmp.Older.CreatedUtc);
        Assert.Equal(ChangeKind.Grown, Kind(cmp, "grow"));
        Assert.Equal(1000, cmp.Entries.Single(e => e.Path == "grow").Delta);
        Assert.Equal(ChangeKind.Shrunk, Kind(cmp, "shrink"));
        Assert.Equal(-300, cmp.Entries.Single(e => e.Path == "shrink").Delta);
        Assert.Equal(ChangeKind.Unchanged, Kind(cmp, "same"));
        Assert.Equal(ChangeKind.Removed, Kind(cmp, "old"));
        Assert.Equal(ChangeKind.Added, Kind(cmp, "new"));
        Assert.Equal((1100 + 200 + 42 + 9) - (100 + 500 + 42 + 7), cmp.TotalDelta);
        Assert.Equal("grow", cmp.Entries[0].Path); // entries are ordered by absolute change
        Assert.Empty(cmp.Warnings);
    }

    [Fact]
    public void Different_roots_are_never_reported_as_measured_changes()
    {
        StorageSnapshot a = Snapshot(r => r.AddDirectory("x").AddFile("f", 1), T1, @"C:\one");
        StorageSnapshot b = Snapshot(r => r.AddDirectory("x").AddFile("f", 100), T2, @"D:\two");

        SnapshotComparison cmp = SnapshotComparer.Compare(a, b);

        Assert.False(cmp.SameScope);
        Assert.Null(cmp.TotalDelta);
        Assert.All(cmp.Entries, e => Assert.Equal(ChangeKind.NotComparable, e.Kind));
        Assert.NotEmpty(cmp.Warnings);
    }

    [Fact]
    public void Root_paths_compare_case_and_separator_insensitively()
    {
        StorageSnapshot a = Snapshot(r => r.AddFile("f", 1), T1, @"C:\Data");
        StorageSnapshot b = Snapshot(r => r.AddFile("f", 2), T2, @"c:\data\");
        Assert.True(SnapshotComparer.Compare(a, b).SameScope);
    }

    [Fact]
    public void Incomplete_directories_are_not_comparable()
    {
        StorageSnapshot older = Snapshot(r =>
        {
            FileSystemNode p = r.AddDirectory("p");
            p.AddDirectory("locked", NodeFlags.AccessDenied);
            p.AddFile("f", 10);
        }, T1);
        StorageSnapshot newer = Snapshot(r =>
        {
            FileSystemNode p = r.AddDirectory("p");
            p.AddDirectory("locked").AddDirectory("inner").AddFile("g", 50);
            p.AddFile("f", 10);
        }, T2);

        SnapshotComparison cmp = SnapshotComparer.Compare(older, newer);

        Assert.Equal(ChangeKind.NotComparable, Kind(cmp, "p"));
        Assert.Equal(ChangeKind.NotComparable, Kind(cmp, @"p\locked"));
        // The inner folder was hidden by the unreadable parent, so it is not "added".
        Assert.Equal(ChangeKind.NotComparable, Kind(cmp, @"p\locked\inner"));
        Assert.Contains(cmp.Warnings, w => w.Contains("could not be read"));
    }

    [Fact]
    public void Different_depths_compare_only_common_levels()
    {
        StorageSnapshot shallow = Snapshot(r => r.AddDirectory("a").AddDirectory("b").AddDirectory("c").AddFile("f", 1), T1, depth: 2);
        StorageSnapshot deep = Snapshot(r => r.AddDirectory("a").AddDirectory("b").AddDirectory("c").AddFile("f", 1), T2, depth: 6);

        SnapshotComparison cmp = SnapshotComparer.Compare(shallow, deep);

        Assert.Equal(2, cmp.ComparedDepth);
        Assert.DoesNotContain(cmp.Entries, e => e.Path == @"a\b\c");
        Assert.All(cmp.Entries, e => Assert.Equal(ChangeKind.Unchanged, e.Kind));
    }

    [Fact]
    public void Truncated_snapshots_do_not_report_missing_entries_as_removed()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\data");
        for (int i = 0; i < 10; i++)
        {
            root.AddDirectory($"d{i}").AddFile("f", 1);
        }

        TreeAggregator.Aggregate(root);
        StorageSnapshot full = SnapshotBuilder.Create(Result(root), createdUtc: T1);
        StorageSnapshot truncated = SnapshotBuilder.Create(Result(root), maxEntries: 4, createdUtc: T2);

        SnapshotComparison cmp = SnapshotComparer.Compare(full, truncated);

        Assert.True(truncated.Summary.Truncated);
        Assert.DoesNotContain(cmp.Entries, e => e.Kind == ChangeKind.Removed);
        Assert.Contains(cmp.Entries, e => e.Kind == ChangeKind.NotComparable);
    }

    [Fact]
    public void Store_round_trips_lists_and_deletes()
    {
        using var dir = new TestDirectory();
        var store = new SnapshotStore(dir.Path);
        StorageSnapshot s = Snapshot(r => r.AddDirectory("a").AddFile("f", 123), T1);

        store.Save(s);
        SnapshotSummary listed = Assert.Single(store.List());
        Assert.Equal(s.Summary.Id, listed.Id);
        StorageSnapshot? loaded = store.Load(s.Summary.Id);
        Assert.NotNull(loaded);
        Assert.Equal(s.Entries.Count, loaded.Entries.Count);
        Assert.Equal(123, loaded.Entries.Single(e => e.Path == "a").Size);

        store.Delete(s.Summary.Id);
        Assert.Empty(store.List());
        Assert.Null(store.Load(s.Summary.Id));
    }

    [Fact]
    public void Store_skips_corrupt_files_and_prunes_by_age()
    {
        using var dir = new TestDirectory();
        var store = new SnapshotStore(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "broken.meta.json"), "{ not json");
        StorageSnapshot old = Snapshot(r => r.AddFile("f", 1), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        StorageSnapshot recent = Snapshot(r => r.AddFile("f", 1), T2);
        store.Save(old);
        store.Save(recent);

        Assert.Equal(2, store.List().Count);
        int removed = store.Prune(TimeSpan.FromDays(90), nowUtc: T2.AddDays(1));

        Assert.Equal(1, removed);
        Assert.Equal(recent.Summary.Id, Assert.Single(store.List()).Id);
    }

    private static ChangeKind Kind(SnapshotComparison cmp, string path) => cmp.Entries.Single(e => e.Path == path).Kind;
}
