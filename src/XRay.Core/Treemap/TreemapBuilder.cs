using XRay.Core.Scanning;

namespace XRay.Core.Treemap;

/// <summary>One rectangle in a built treemap.</summary>
public sealed class TreemapItem
{
    public required TreemapRect Rect { get; init; }

    /// <summary>The represented node, or null for an aggregate of small items.</summary>
    public FileSystemNode? Node { get; init; }

    /// <summary>Nesting level relative to the displayed root (top-level children = 1).</summary>
    public required int Level { get; init; }

    /// <summary>Index of the enclosing directory item in the item list, or -1 for top-level items.</summary>
    public required int ParentIndex { get; init; }

    /// <summary>True if this rectangle stands for several items too small to draw individually.</summary>
    public bool IsAggregate => Node is null;

    public int AggregateCount { get; init; }

    public long AggregateSize { get; init; }

    /// <summary>The directory whose small children are aggregated (only for aggregates).</summary>
    public FileSystemNode? AggregateOwner { get; init; }

    /// <summary>True if the item's children were laid out inside it.</summary>
    public bool HasNestedItems { get; internal set; }

    /// <summary>Height of the label band reserved at the top of a nested directory, or 0.</summary>
    public double HeaderHeight { get; internal set; }

    public long Size(SizeMetric metric) => Node?.GetSize(metric) ?? AggregateSize;
}

/// <summary>Options for <see cref="TreemapBuilder"/>.</summary>
public sealed record TreemapBuildOptions
{
    public SizeMetric Metric { get; init; } = SizeMetric.Logical;

    /// <summary>How many directory levels are drawn nested inside each other (1 = only direct children).</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Items whose rectangle would be smaller than this area (in square units) are merged into an aggregate.</summary>
    public double MinItemArea { get; init; } = 9;

    /// <summary>Upper bound on rectangles per directory level.</summary>
    public int MaxItemsPerLevel { get; init; } = 2000;

    /// <summary>Upper bound on rectangles in the whole map.</summary>
    public int MaxTotalItems { get; init; } = 25000;

    /// <summary>Gap between nested content and the directory border.</summary>
    public double Padding { get; init; } = 2;

    /// <summary>Height of a directory's label band, shown when the directory is large enough.</summary>
    public double HeaderHeight { get; init; } = 18;

    /// <summary>Minimum width and height of a directory before its children are nested inside it.</summary>
    public double MinNestSize { get; init; } = 28;
}

/// <summary>Builds a flat list of treemap rectangles for a directory, nesting sub-directories up to a depth.</summary>
public static class TreemapBuilder
{
    /// <summary>
    /// Builds treemap items for the children of <paramref name="root"/> inside <paramref name="bounds"/>.
    /// Items are emitted parent-before-child so they can be drawn in list order.
    /// </summary>
    public static List<TreemapItem> Build(FileSystemNode root, TreemapRect bounds, TreemapBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new TreemapBuildOptions();
        var items = new List<TreemapItem>();
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            return items;
        }

        // Breadth-first so that, if the global item limit is hit, shallow levels are complete.
        var queue = new Queue<(FileSystemNode Dir, TreemapRect Area, int Level, int ParentIndex)>();
        queue.Enqueue((root, bounds, 1, -1));
        while (queue.Count > 0 && items.Count < options.MaxTotalItems)
        {
            var (dir, area, level, parentIndex) = queue.Dequeue();
            LayoutChildren(dir, area, level, parentIndex, options, items, queue);
        }

        return items;
    }

    /// <summary>Returns the index of the deepest item containing the point, or -1.</summary>
    public static int HitTest(IReadOnlyList<TreemapItem> items, double x, double y)
    {
        // Children are emitted after their parents, so the last match is the deepest.
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Rect.Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    private static void LayoutChildren(
        FileSystemNode dir,
        TreemapRect area,
        int level,
        int parentIndex,
        TreemapBuildOptions options,
        List<TreemapItem> items,
        Queue<(FileSystemNode, TreemapRect, int, int)> queue)
    {
        var metric = options.Metric;
        var children = new List<FileSystemNode>(dir.Children.Count);
        long total = 0;
        foreach (FileSystemNode child in dir.Children)
        {
            long size = child.GetSize(metric);
            if (size > 0)
            {
                children.Add(child);
                total += size;
            }
        }

        if (children.Count == 0 || total <= 0 || area.Area < 1)
        {
            return;
        }

        children.Sort((a, b) =>
        {
            int c = b.GetSize(metric).CompareTo(a.GetSize(metric));
            return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        // Keep items that get a visible rectangle; merge the tail into one aggregate.
        double unitsPerByte = area.Area / total;
        int budget = Math.Min(options.MaxItemsPerLevel, Math.Max(1, options.MaxTotalItems - items.Count));
        int keep = 0;
        while (keep < children.Count && keep < budget &&
               children[keep].GetSize(metric) * unitsPerByte >= options.MinItemArea)
        {
            keep++;
        }

        // Always show at least the largest item so the map is never empty.
        keep = Math.Max(keep, 1);

        long restSize = 0;
        for (int i = keep; i < children.Count; i++)
        {
            restSize += children[i].GetSize(metric);
        }

        int restCount = children.Count - keep;
        var weights = new List<double>(keep + 1);
        for (int i = 0; i < keep; i++)
        {
            weights.Add(children[i].GetSize(metric));
        }

        if (restCount > 0)
        {
            weights.Add(restSize);
        }

        TreemapRect[] rects = SquarifiedLayout.Layout(weights, area);
        for (int i = 0; i < rects.Length; i++)
        {
            if (i < keep)
            {
                FileSystemNode node = children[i];
                var item = new TreemapItem
                {
                    Rect = rects[i],
                    Node = node,
                    Level = level,
                    ParentIndex = parentIndex,
                };
                items.Add(item);
                int index = items.Count - 1;

                if (node.IsDirectory && node.HasChildren && level < options.MaxDepth &&
                    rects[i].Width >= options.MinNestSize && rects[i].Height >= options.MinNestSize)
                {
                    double header = rects[i].Height >= options.HeaderHeight * 2.5 && rects[i].Width >= 48
                        ? options.HeaderHeight
                        : 0;
                    TreemapRect inner = rects[i].Deflate(options.Padding, options.Padding + header, options.Padding, options.Padding);
                    if (inner.Width >= 4 && inner.Height >= 4)
                    {
                        item.HasNestedItems = true;
                        item.HeaderHeight = header;
                        queue.Enqueue((node, inner, level + 1, index));
                    }
                }
            }
            else
            {
                items.Add(new TreemapItem
                {
                    Rect = rects[i],
                    Node = null,
                    Level = level,
                    ParentIndex = parentIndex,
                    AggregateCount = restCount,
                    AggregateSize = restSize,
                    AggregateOwner = dir,
                });
            }
        }
    }
}
