using System.Runtime.InteropServices;

namespace XRay.Core.Duplicates;

/// <summary>Moves a file to the Recycle Bin. Throws when the file could not be recycled.</summary>
public interface IFileRecycler
{
    void Recycle(string path);
}

/// <summary>
/// Locations whose files are never offered for removal (Windows, Program Files, ProgramData): duplicates there are
/// normally required by installed software.
/// </summary>
public sealed class ProtectedLocations
{
    private readonly string[] _roots;

    public ProtectedLocations(IEnumerable<string> roots)
    {
        _roots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)) + Path.DirectorySeparatorChar)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static ProtectedLocations Default { get; } = new(
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    ]);

    public bool IsProtected(string path)
    {
        string full = Path.GetFullPath(path);
        return _roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase));
    }
}

public enum CleanupOutcome
{
    Recycled,

    /// <summary>The file's size or modification time differs from the scan; it may no longer be a duplicate.</summary>
    SkippedChanged,

    SkippedMissing,

    /// <summary>Removing it would leave no remaining copy in its group.</summary>
    SkippedLastCopy,

    SkippedProtected,

    /// <summary>The drive has no Recycle Bin (network or removable), so removal would be permanent.</summary>
    SkippedNoRecycleBin,

    Failed,
}

public sealed record CleanupItemResult(string Path, long Size, CleanupOutcome Outcome, string? Message);

/// <summary>Summary of a cleanup run.</summary>
public sealed class CleanupReport(IReadOnlyList<CleanupItemResult> items)
{
    public IReadOnlyList<CleanupItemResult> Items { get; } = items;

    public int RecycledCount => Items.Count(i => i.Outcome == CleanupOutcome.Recycled);

    public long RecycledBytes => Items.Where(i => i.Outcome == CleanupOutcome.Recycled).Sum(i => i.Size);

    public IEnumerable<CleanupItemResult> NotRecycled => Items.Where(i => i.Outcome != CleanupOutcome.Recycled);
}

/// <summary>
/// Moves user-selected duplicate files to the Recycle Bin with strict safeguards: at least one existing copy of
/// every group is always kept, protected locations are refused, files that changed since the scan are skipped,
/// and nothing is ever deleted permanently.
/// </summary>
public sealed class DuplicateCleaner
{
    private readonly IFileRecycler _recycler;
    private readonly ProtectedLocations _protected;
    private readonly Func<string, bool> _hasRecycleBin;

    public DuplicateCleaner(IFileRecycler recycler, ProtectedLocations? protectedLocations = null, Func<string, bool>? hasRecycleBin = null)
    {
        _recycler = recycler;
        _protected = protectedLocations ?? ProtectedLocations.Default;
        _hasRecycleBin = hasRecycleBin ?? HasRecycleBin;
    }

    /// <summary>Returns human-readable problems with a selection; empty if it is safe to proceed.</summary>
    public IReadOnlyList<string> Validate(IEnumerable<DuplicateGroup> groups, IReadOnlySet<string> selectedPaths)
    {
        var problems = new List<string>();
        foreach (DuplicateGroup g in groups)
        {
            int selected = g.Files.Count(f => selectedPaths.Contains(f.FullPath));
            if (selected > 0 && selected >= g.Files.Count)
            {
                problems.Add($"All copies of '{g.Files[0].Name}' are selected. At least one copy must be kept.");
            }

            foreach (DuplicateFile f in g.Files.Where(f => selectedPaths.Contains(f.FullPath) && _protected.IsProtected(f.FullPath)))
            {
                problems.Add($"'{f.FullPath}' is in a protected system or program location.");
            }
        }

        return problems;
    }

    public bool IsProtected(string path) => _protected.IsProtected(path);

    public CleanupReport Clean(IEnumerable<DuplicateGroup> groups, IReadOnlySet<string> selectedPaths, CancellationToken cancellationToken = default)
    {
        var results = new List<CleanupItemResult>();
        foreach (DuplicateGroup group in groups)
        {
            List<DuplicateFile> remove = group.Files.Where(f => selectedPaths.Contains(f.FullPath)).ToList();
            if (remove.Count == 0)
            {
                continue;
            }

            // Re-check at removal time that an unchanged, unselected copy still exists on disk.
            bool keptCopyExists = group.Files
                .Where(f => !selectedPaths.Contains(f.FullPath))
                .Any(f => IsUnchanged(f, out _));
            if (!keptCopyExists)
            {
                results.AddRange(remove.Select(f => new CleanupItemResult(f.FullPath, f.Size, CleanupOutcome.SkippedLastCopy,
                    "No unselected copy of this file still exists unchanged, so nothing was removed from this group.")));
                continue;
            }

            foreach (DuplicateFile file in remove)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(CleanOne(file));
            }
        }

        return new CleanupReport(results);
    }

    private CleanupItemResult CleanOne(DuplicateFile file)
    {
        if (_protected.IsProtected(file.FullPath))
        {
            return new(file.FullPath, file.Size, CleanupOutcome.SkippedProtected, "Protected system or program location.");
        }

        if (!_hasRecycleBin(file.FullPath))
        {
            return new(file.FullPath, file.Size, CleanupOutcome.SkippedNoRecycleBin, "This drive has no Recycle Bin; removal would be permanent.");
        }

        if (!IsUnchanged(file, out string? reason))
        {
            return new(file.FullPath, file.Size, reason is null ? CleanupOutcome.SkippedMissing : CleanupOutcome.SkippedChanged,
                reason ?? "The file no longer exists.");
        }

        try
        {
            _recycler.Recycle(file.FullPath);
            return new(file.FullPath, file.Size, CleanupOutcome.Recycled, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            return new(file.FullPath, file.Size, CleanupOutcome.Failed, ex.Message);
        }
    }

    /// <summary>True if the file exists with the size and modification time recorded by the scan.</summary>
    private static bool IsUnchanged(DuplicateFile file, out string? reason)
    {
        reason = null;
        var info = new FileInfo(file.FullPath);
        if (!info.Exists)
        {
            return false;
        }

        if (info.Length != file.Size || Math.Abs((info.LastWriteTimeUtc - file.LastWriteTimeUtc).TotalSeconds) > 2)
        {
            reason = "The file was modified after the scan; it may no longer be identical.";
            return false;
        }

        return true;
    }

    /// <summary>Only fixed local drives have a Recycle Bin; network and removable drives delete permanently.</summary>
    public static bool HasRecycleBin(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>Recycles files with the Windows shell (SHFileOperation with FOF_ALLOWUNDO).</summary>
public sealed class ShellRecycler : IFileRecycler
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofWantNukeWarning = 0x4000;

    public void Recycle(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = Path.GetFullPath(path) + "\0\0",
            // FOF_WANTNUKEWARNING makes Windows ask before deleting permanently (e.g. a file too large for the
            // Recycle Bin) instead of silently doing so.
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofWantNukeWarning),
        };
        int result = SHFileOperationW(ref op);
        if (op.fAnyOperationsAborted)
        {
            throw new OperationCanceledException("Removal was cancelled.");
        }

        if (result != 0 || File.Exists(path))
        {
            throw new IOException($"Windows could not move the file to the Recycle Bin (code 0x{result:X}).");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);
}
