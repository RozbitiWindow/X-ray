using XRay.Core.Scanning;
using XRay.Core.Treemap;

namespace XRay.Core.Tests;

public class TreemapTests
{
    private const double Eps = 1e-6;

    public static TheoryData<double[], double, double> Layouts => new()
    {
        { new double[] { 6, 6, 4, 3, 2, 2, 1 }, 600, 400 },
        { new double[] { 1 }, 300, 100 },
        { new double[] { 5, 5 }, 100, 300 },
        { new double[] { 1000, 1, 1, 1 }, 800, 600 },
        { Enumerable.Range(1, 500).Select(i => (double)(501 - i)).ToArray(), 1200, 700 },
        { new double[] { 3, 0, 0 }, 200, 200 },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Layout_fills_bounds_without_overlap_and_proportional_areas(double[] weights, double width, double height)
    {
        var bounds = new TreemapRect(10, 20, width, height);
        TreemapRect[] rects = SquarifiedLayout.Layout(weights, bounds);

        Assert.Equal(weights.Length, rects.Length);
        double total = weights.Sum();
        double areaSum = 0;
        for (int i = 0; i < rects.Length; i++)
        {
            TreemapRect r = rects[i];
            Assert.True(r.Width >= 0 && r.Height >= 0);
            Assert.True(r.X >= bounds.X - Eps && r.Right <= bounds.Right + Eps, $"rect {i} exceeds horizontal bounds");
            Assert.True(r.Y >= bounds.Y - Eps && r.Bottom <= bounds.Bottom + Eps, $"rect {i} exceeds vertical bounds");
            double expectedArea = weights[i] / total * bounds.Area;
            Assert.Equal(expectedArea, r.Area, precision: 3);
            areaSum += r.Area;
        }

        Assert.Equal(bounds.Area, areaSum, precision: 3);

        for (int i = 0; i < rects.Length; i++)
        {
            for (int j = i + 1; j < rects.Length; j++)
            {
                Assert.False(Overlaps(rects[i], rects[j]), $"rects {i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void Squarified_layout_keeps_aspect_ratios_reasonable()
    {
        double[] weights = [6, 6, 4, 3, 2, 2, 1];
        TreemapRect[] rects = SquarifiedLayout.Layout(weights, new TreemapRect(0, 0, 600, 400));
        foreach (TreemapRect r in rects)
        {
            double ratio = Math.Max(r.Width / r.Height, r.Height / r.Width);
            Assert.True(ratio < 4, $"aspect ratio {ratio} is too elongated");
        }
    }

    [Fact]
    public void Empty_or_degenerate_input_produces_empty_rects()
    {
        Assert.Empty(SquarifiedLayout.Layout([], new TreemapRect(0, 0, 100, 100)));
        TreemapRect[] zero = SquarifiedLayout.Layout([0, 0], new TreemapRect(0, 0, 100, 100));
        Assert.All(zero, r => Assert.Equal(0, r.Area));
        TreemapRect[] noSpace = SquarifiedLayout.Layout([1, 2], new TreemapRect(0, 0, 0, 100));
        Assert.All(noSpace, r => Assert.Equal(0, r.Area));
    }

    [Fact]
    public void Builder_aggregates_tiny_items_and_respects_limits()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\data");
        root.AddFile("huge.bin", 1_000_000_000);
        for (int i = 0; i < 5000; i++)
        {
            root.AddFile($"tiny{i}.txt", 10);
        }

        TreeAggregator.Aggregate(root);
        List<TreemapItem> items = TreemapBuilder.Build(root, new TreemapRect(0, 0, 800, 600));

        Assert.True(items.Count < 100, "tiny files should be merged into one aggregate");
        TreemapItem aggregate = Assert.Single(items, i => i.IsAggregate);
        Assert.Equal(5000, aggregate.AggregateCount);
        Assert.Equal(50_000, aggregate.AggregateSize);
        Assert.Contains(items, i => i.Node?.Name == "huge.bin");
    }

    [Fact]
    public void Builder_handles_thousands_of_visible_items_with_bounded_output()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\data");
        for (int i = 0; i < 10_000; i++)
        {
            root.AddFile($"f{i}.bin", 1000);
        }

        TreeAggregator.Aggregate(root);
        List<TreemapItem> items = TreemapBuilder.Build(root, new TreemapRect(0, 0, 4000, 4000),
            new TreemapBuildOptions { MaxItemsPerLevel = 2000 });

        Assert.True(items.Count <= 2001);
        Assert.Equal(1, items.Count(i => i.IsAggregate));
    }

    [Fact]
    public void Builder_nests_directories_inside_their_parent_rectangle()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\data");
        FileSystemNode a = root.AddDirectory("a");
        a.AddFile("a1.bin", 600);
        a.AddFile("a2.bin", 400);
        FileSystemNode b = root.AddDirectory("b");
        b.AddFile("b1.bin", 500);
        root.AddFile("loose.bin", 500);
        TreeAggregator.Aggregate(root);

        List<TreemapItem> items = TreemapBuilder.Build(root, new TreemapRect(0, 0, 800, 600), new TreemapBuildOptions { MaxDepth = 3 });

        for (int i = 0; i < items.Count; i++)
        {
            TreemapItem item = items[i];
            if (item.ParentIndex < 0)
            {
                Assert.Equal(1, item.Level);
                continue;
            }

            Assert.True(item.ParentIndex < i, "parents must precede children");
            TreemapRect parent = items[item.ParentIndex].Rect;
            Assert.True(item.Rect.X >= parent.X - Eps && item.Rect.Right <= parent.Right + Eps);
            Assert.True(item.Rect.Y >= parent.Y - Eps && item.Rect.Bottom <= parent.Bottom + Eps);
            Assert.Equal(items[item.ParentIndex].Level + 1, item.Level);
        }

        Assert.Contains(items, i => i.Node?.Name == "a1.bin" && i.Level == 2);
    }

    [Fact]
    public void Depth_one_draws_only_direct_children()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\data");
        FileSystemNode a = root.AddDirectory("a");
        a.AddFile("x.bin", 100);
        root.AddFile("y.bin", 100);
        TreeAggregator.Aggregate(root);

        List<TreemapItem> items = TreemapBuilder.Build(root, new TreemapRect(0, 0, 400, 400), new TreemapBuildOptions { MaxDepth = 1 });

        Assert.All(items, i => Assert.Equal(1, i.Level));
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void Hit_test_returns_deepest_item()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\data");
        FileSystemNode a = root.AddDirectory("a");
        a.AddFile("only.bin", 100);
        TreeAggregator.Aggregate(root);

        List<TreemapItem> items = TreemapBuilder.Build(root, new TreemapRect(0, 0, 400, 400));
        int hit = TreemapBuilder.HitTest(items, 200, 300);

        Assert.True(hit >= 0);
        Assert.Equal("only.bin", items[hit].Node?.Name);
        Assert.Equal(-1, TreemapBuilder.HitTest(items, 500, 500));
    }

    [Fact]
    public void Zero_size_items_are_not_drawn()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\data");
        root.AddFile("empty.txt", 0);
        root.AddFile("data.bin", 10);
        TreeAggregator.Aggregate(root);

        List<TreemapItem> items = TreemapBuilder.Build(root, new TreemapRect(0, 0, 100, 100));

        Assert.Single(items);
        Assert.Equal("data.bin", items[0].Node!.Name);
    }

    private static bool Overlaps(TreemapRect a, TreemapRect b)
    {
        double w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return w > Eps && h > Eps;
    }
}
