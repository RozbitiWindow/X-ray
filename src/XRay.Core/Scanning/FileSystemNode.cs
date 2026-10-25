namespace XRay.Core.Scanning;

/// <summary>Which size metric is shown and used for layout.</summary>
public enum SizeMetric
{
    /// <summary>The logical file length (what applications see).</summary>
    Logical,

    /// <summary>Estimated space allocated on disk (cluster-rounded, compression and sparse files aware).</summary>
    Allocated,
}

/// <summary>Conditions recorded for a node during scanning.</summary>
[Flags]
public enum NodeFlags
{
    None = 0,

    /// <summary>The directory could not be listed because access was denied.</summary>
    AccessDenied = 1 << 0,

    /// <summary>The directory is a symbolic link, junction or mount point and was not followed.</summary>
    ReparsePointSkipped = 1 << 1,

    /// <summary>The item disappeared while it was being scanned.</summary>
    Vanished = 1 << 2,

    /// <summary>Another I/O error prevented reading the directory.</summary>
    Error = 1 << 3,

    /// <summary>At least one descendant could not be read; aggregated sizes are lower bounds.</summary>
    IncompleteDescendants = 1 << 4,

    /// <summary>The file is a cloud placeholder or offline file; its data may not be stored locally.</summary>
    CloudOrOffline = 1 << 5,
}

/// <summary>
/// A file or directory in a scanned tree. Directory sizes and counts are aggregated over all descendants.
/// Full paths are computed on demand from the parent chain to keep memory usage low.
/// </summary>
public sealed class FileSystemNode
{
    private static readonly IReadOnlyList<FileSystemNode> NoChildren = Array.Empty<FileSystemNode>();
    private List<FileSystemNode>? _children;

    internal FileSystemNode(string name, FileSystemNode? parent, bool isDirectory)
    {
        Name = name;
        Parent = parent;
        IsDirectory = isDirectory;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    /// <summary>Item name. For the scan root this is the full root path.</summary>
    public string Name { get; }

    public FileSystemNode? Parent { get; }

    public bool IsDirectory { get; }

    /// <summary>Distance from the scan root (root = 0).</summary>
    public int Depth { get; }

    /// <summary>Logical size in bytes (recursive for directories).</summary>
    public long Size { get; internal set; }

    /// <summary>Estimated allocated size on disk in bytes (recursive for directories).</summary>
    public long AllocatedSize { get; internal set; }

    /// <summary>Number of files contained (recursive). For a file this is 1.</summary>
    public long FileCount { get; internal set; }

    /// <summary>Number of sub-directories contained (recursive).</summary>
    public long DirectoryCount { get; internal set; }

    public DateTime LastWriteTimeUtc { get; internal set; }

    public NodeFlags Flags { get; internal set; }

    public IReadOnlyList<FileSystemNode> Children => _children ?? NoChildren;

    public bool HasChildren => _children is { Count: > 0 };

    /// <summary>True if the directory itself could not be fully read.</summary>
    public bool IsUnreadable => (Flags & (NodeFlags.AccessDenied | NodeFlags.Error | NodeFlags.Vanished)) != 0;

    /// <summary>True if this node or any descendant could not be read, so sizes are lower bounds.</summary>
    public bool IsIncomplete => IsUnreadable || (Flags & NodeFlags.IncompleteDescendants) != 0;

    public bool IsReparsePoint => (Flags & NodeFlags.ReparsePointSkipped) != 0;

    /// <summary>Lower-case file extension including the dot, or an empty string.</summary>
    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name).ToLowerInvariant();

    /// <summary>Absolute path of the item.</summary>
    public string FullPath
    {
        get
        {
            if (Parent is null)
            {
                return Name;
            }

            // Collect names up to the root, then join once.
            var parts = new Stack<string>();
            FileSystemNode? current = this;
            while (current.Parent is not null)
            {
                parts.Push(current.Name);
                current = current.Parent;
            }

            string path = current.Name;
            while (parts.Count > 0)
            {
                path = Path.Join(path, parts.Pop());
            }

            return path;
        }
    }

    /// <summary>Path relative to the scan root, using '\' separators; empty for the root.</summary>
    public string RelativePath
    {
        get
        {
            if (Parent is null)
            {
                return string.Empty;
            }

            var parts = new Stack<string>();
            FileSystemNode? current = this;
            while (current.Parent is not null)
            {
                parts.Push(current.Name);
                current = current.Parent;
            }

            return string.Join('\\', parts);
        }
    }

    public FileSystemNode Root
    {
        get
        {
            FileSystemNode node = this;
            while (node.Parent is not null)
            {
                node = node.Parent;
            }

            return node;
        }
    }

    /// <summary>Returns the size according to <paramref name="metric"/>.</summary>
    public long GetSize(SizeMetric metric) => metric == SizeMetric.Allocated ? AllocatedSize : Size;

    /// <summary>Returns true if <paramref name="ancestor"/> is this node or one of its ancestors.</summary>
    public bool IsDescendantOf(FileSystemNode ancestor)
    {
        for (FileSystemNode? n = this; n is not null; n = n.Parent)
        {
            if (ReferenceEquals(n, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Enumerates the chain from the root down to this node (inclusive).</summary>
    public IReadOnlyList<FileSystemNode> GetAncestry()
    {
        var list = new List<FileSystemNode>(Depth + 1);
        for (FileSystemNode? n = this; n is not null; n = n.Parent)
        {
            list.Add(n);
        }

        list.Reverse();
        return list;
    }

    internal void AddChild(FileSystemNode child) => (_children ??= new List<FileSystemNode>()).Add(child);

    internal void TrimChildren() => _children?.TrimExcess();

    /// <summary>Creates a directory node; intended for tests and tree construction outside the scanner.</summary>
    public static FileSystemNode CreateRoot(string path) => new(path, null, isDirectory: true);

    /// <summary>Adds a child file with the given sizes; intended for tests and synthetic trees.</summary>
    public FileSystemNode AddFile(string name, long size, long? allocatedSize = null, DateTime? lastWriteUtc = null)
    {
        var file = new FileSystemNode(name, this, isDirectory: false)
        {
            Size = size,
            AllocatedSize = allocatedSize ?? size,
            FileCount = 1,
            LastWriteTimeUtc = lastWriteUtc ?? DateTime.UtcNow,
        };
        AddChild(file);
        return file;
    }

    /// <summary>Adds a child directory; intended for tests and synthetic trees.</summary>
    public FileSystemNode AddDirectory(string name, NodeFlags flags = NodeFlags.None)
    {
        var dir = new FileSystemNode(name, this, isDirectory: true) { Flags = flags };
        AddChild(dir);
        return dir;
    }

    public override string ToString() => Name;
}
