namespace XRay.Core.Scanning;

/// <summary>Keys for ordering nodes in lists.</summary>
public enum NodeSortKey
{
    Size,
    Name,
    FileCount,
    Modified,
}

/// <summary>Sorting and querying helpers over a scanned tree. None of these touch the file system.</summary>
public static class TreeQueries
{
    /// <summary>
    /// Sorts nodes by the given key. Ties are broken by name so the order is deterministic.
    /// </summary>
    public static List<FileSystemNode> Sort(
        IEnumerable<FileSystemNode> nodes,
        NodeSortKey key,
        bool descending,
        SizeMetric metric = SizeMetric.Logical)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var list = nodes.ToList();
        Comparison<FileSystemNode> primary = key switch
        {
            NodeSortKey.Name => (a, b) => CompareNames(a, b),
            NodeSortKey.FileCount => (a, b) => a.FileCount.CompareTo(b.FileCount),
            NodeSortKey.Modified => (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc),
            _ => (a, b) => a.GetSize(metric).CompareTo(b.GetSize(metric)),
        };

        list.Sort((a, b) =>
        {
            int c = primary(a, b);
            if (descending)
            {
                c = -c;
            }

            return c != 0 ? c : CompareNames(a, b);
        });
        return list;
    }

    /// <summary>Returns the <paramref name="count"/> largest files below <paramref name="root"/>, largest first.</summary>
    public static List<FileSystemNode> LargestFiles(FileSystemNode root, int count, SizeMetric metric = SizeMetric.Logical)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (count <= 0)
        {
            return [];
        }

        // Min-heap of the best candidates seen so far keeps memory at O(count).
        var heap = new PriorityQueue<FileSystemNode, long>(count + 1);
        foreach (FileSystemNode file in EnumerateFiles(root))
        {
            long size = file.GetSize(metric);
            if (heap.Count < count)
            {
                heap.Enqueue(file, size);
            }
            else if (heap.TryPeek(out _, out long smallest) && size > smallest)
            {
                heap.EnqueueDequeue(file, size);
            }
        }

        var result = new List<FileSystemNode>(heap.Count);
        while (heap.TryDequeue(out FileSystemNode? node, out _))
        {
            result.Add(node);
        }

        result.Reverse();
        return result;
    }

    /// <summary>Enumerates all files below <paramref name="root"/> without recursion.</summary>
    public static IEnumerable<FileSystemNode> EnumerateFiles(FileSystemNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.IsDirectory)
        {
            yield return root;
            yield break;
        }

        var stack = new Stack<FileSystemNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            FileSystemNode dir = stack.Pop();
            foreach (FileSystemNode child in dir.Children)
            {
                if (child.IsDirectory)
                {
                    stack.Push(child);
                }
                else
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>Enumerates all directories below <paramref name="root"/> (including it) that could not be read.</summary>
    public static IEnumerable<FileSystemNode> EnumerateUnreadable(FileSystemNode root)
    {
        var stack = new Stack<FileSystemNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            FileSystemNode dir = stack.Pop();
            if (dir.IsUnreadable)
            {
                yield return dir;
            }

            if ((dir.Flags & NodeFlags.IncompleteDescendants) == 0)
            {
                continue;
            }

            foreach (FileSystemNode child in dir.Children)
            {
                if (child.IsDirectory)
                {
                    stack.Push(child);
                }
            }
        }
    }

    /// <summary>
    /// Finds the node for <paramref name="fullPath"/> inside the tree rooted at <paramref name="root"/>,
    /// or null if the path lies outside it or was not recorded.
    /// </summary>
    public static FileSystemNode? FindByPath(FileSystemNode root, string fullPath)
    {
        string rootPath = DirectoryScanner.NormalizeRoot(root.FullPath);
        string target = DirectoryScanner.NormalizeRoot(fullPath);
        if (string.Equals(rootPath, target, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        string prefix = Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        FileSystemNode current = root;
        foreach (string part in target[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            FileSystemNode? next = null;
            foreach (FileSystemNode child in current.Children)
            {
                if (string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static int CompareNames(FileSystemNode a, FileSystemNode b) =>
        StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
}
