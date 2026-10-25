using XRay.Core.Scanning;

namespace XRay.Core.Tests;

public class TreeQueriesTests
{
    private static FileSystemNode BuildTree()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\data");
        FileSystemNode docs = root.AddDirectory("Docs");
        docs.AddFile("b.pdf", 300, lastWriteUtc: new DateTime(2024, 1, 1));
        docs.AddFile("a.pdf", 300, lastWriteUtc: new DateTime(2023, 1, 1));
        FileSystemNode media = root.AddDirectory("media");
        media.AddFile("movie.mkv", 5000, allocatedSize: 8192);
        media.AddFile("clip.mp4", 50);
        root.AddFile("zeta.txt", 10, allocatedSize: 4096);
        TreeAggregator.Aggregate(root);
        return root;
    }

    [Fact]
    public void Aggregation_counts_files_and_directories_recursively()
    {
        FileSystemNode root = BuildTree();
        Assert.Equal(5660, root.Size);
        Assert.Equal(5, root.FileCount);
        Assert.Equal(2, root.DirectoryCount);
        Assert.Equal(8192 + 4096 + 300 + 300 + 50, root.AllocatedSize);
    }

    [Fact]
    public void Aggregation_propagates_incomplete_flag_to_ancestors_only()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\r");
        FileSystemNode a = root.AddDirectory("a");
        a.AddDirectory("locked", NodeFlags.AccessDenied);
        FileSystemNode b = root.AddDirectory("b");
        TreeAggregator.Aggregate(root);

        Assert.True(root.IsIncomplete);
        Assert.True(a.IsIncomplete);
        Assert.False(a.IsUnreadable);
        Assert.False(b.IsIncomplete);
        Assert.Single(TreeQueries.EnumerateUnreadable(root));
    }

    [Fact]
    public void Sort_by_size_descending_breaks_ties_by_name()
    {
        FileSystemNode docs = BuildTree().Children.Single(c => c.Name == "Docs");
        var sorted = TreeQueries.Sort(docs.Children, NodeSortKey.Size, descending: true);
        Assert.Equal(["a.pdf", "b.pdf"], sorted.Select(n => n.Name));
    }

    [Fact]
    public void Sort_by_name_is_case_insensitive()
    {
        var sorted = TreeQueries.Sort(BuildTree().Children, NodeSortKey.Name, descending: false);
        Assert.Equal(["Docs", "media", "zeta.txt"], sorted.Select(n => n.Name));
    }

    [Fact]
    public void Sort_by_file_count_and_metric()
    {
        FileSystemNode root = BuildTree();
        var byCount = TreeQueries.Sort(root.Children, NodeSortKey.FileCount, descending: true);
        Assert.Equal("Docs", byCount[0].Name); // 2 files, tie with media broken by name
        var byAllocated = TreeQueries.Sort(root.Children, NodeSortKey.Size, descending: false, SizeMetric.Allocated);
        Assert.Equal("Docs", byAllocated[0].Name);
        Assert.Equal("media", byAllocated[^1].Name);
    }

    [Fact]
    public void Sort_by_modified()
    {
        FileSystemNode docs = BuildTree().Children.Single(c => c.Name == "Docs");
        var oldestFirst = TreeQueries.Sort(docs.Children, NodeSortKey.Modified, descending: false);
        Assert.Equal("a.pdf", oldestFirst[0].Name);
    }

    [Fact]
    public void Largest_files_returns_top_n_in_order()
    {
        var top = TreeQueries.LargestFiles(BuildTree(), 3);
        Assert.Equal(["movie.mkv", "a.pdf", "b.pdf"], top.Select(n => n.Name).Take(1).Concat(top.Skip(1).Select(n => n.Name).Order()));
        Assert.Equal(5000, top[0].Size);
        Assert.Empty(TreeQueries.LargestFiles(BuildTree(), 0));
    }

    [Fact]
    public void Find_by_path_is_case_insensitive_and_scoped()
    {
        FileSystemNode root = BuildTree();
        Assert.Equal("movie.mkv", TreeQueries.FindByPath(root, @"c:\DATA\Media\movie.mkv")?.Name);
        Assert.Same(root, TreeQueries.FindByPath(root, @"C:\data\"));
        Assert.Null(TreeQueries.FindByPath(root, @"C:\other\x"));
        Assert.Null(TreeQueries.FindByPath(root, @"C:\database"));
        Assert.Null(TreeQueries.FindByPath(root, @"C:\data\missing"));
    }

    [Fact]
    public void Ancestry_and_descendant_checks()
    {
        FileSystemNode root = BuildTree();
        FileSystemNode movie = TreeQueries.FindByPath(root, @"C:\data\media\movie.mkv")!;
        Assert.Equal(["C:\\data", "media", "movie.mkv"], movie.GetAncestry().Select(n => n.Name));
        Assert.True(movie.IsDescendantOf(root));
        Assert.False(root.IsDescendantOf(movie));
        Assert.Equal(@"C:\data\media\movie.mkv", movie.FullPath);
    }
}
