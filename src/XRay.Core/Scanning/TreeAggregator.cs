namespace XRay.Core.Scanning;

/// <summary>Computes recursive directory sizes and counts for a tree whose files already carry their own sizes.</summary>
public static class TreeAggregator
{
    /// <summary>
    /// Aggregates sizes, file counts, directory counts and incompleteness flags bottom-up.
    /// Uses an explicit stack so arbitrarily deep trees cannot overflow the call stack.
    /// </summary>
    public static void Aggregate(FileSystemNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.IsDirectory)
        {
            return;
        }

        // Post-order traversal: push (node, visited) pairs.
        var stack = new Stack<(FileSystemNode Node, bool ChildrenDone)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (node, childrenDone) = stack.Pop();
            if (!childrenDone)
            {
                stack.Push((node, true));
                foreach (FileSystemNode child in node.Children)
                {
                    if (child.IsDirectory)
                    {
                        stack.Push((child, false));
                    }
                }

                continue;
            }

            long size = 0, allocated = 0, files = 0, dirs = 0;
            bool incomplete = false;
            DateTime newest = node.LastWriteTimeUtc;
            foreach (FileSystemNode child in node.Children)
            {
                size += child.Size;
                allocated += child.AllocatedSize;
                if (child.IsDirectory)
                {
                    files += child.FileCount;
                    dirs += child.DirectoryCount + 1;
                    incomplete |= child.IsIncomplete;
                }
                else
                {
                    files += 1;
                }

                if (child.LastWriteTimeUtc > newest)
                {
                    newest = child.LastWriteTimeUtc;
                }
            }

            node.Size = size;
            node.AllocatedSize = allocated;
            node.FileCount = files;
            node.DirectoryCount = dirs;
            node.LastWriteTimeUtc = newest;
            node.Flags = incomplete
                ? node.Flags | NodeFlags.IncompleteDescendants
                : node.Flags & ~NodeFlags.IncompleteDescendants;
        }
    }
}
