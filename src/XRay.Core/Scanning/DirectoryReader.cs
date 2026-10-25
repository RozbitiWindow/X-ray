using System.IO.Enumeration;

namespace XRay.Core.Scanning;

/// <summary>Metadata of a single directory entry, captured without opening the file.</summary>
public readonly record struct DirectoryEntry(
    string Name,
    bool IsDirectory,
    long Length,
    FileAttributes Attributes,
    DateTime LastWriteTimeUtc);

/// <summary>Lists the immediate entries of a directory. Exceptions propagate to the caller.</summary>
public interface IDirectoryReader
{
    IEnumerable<DirectoryEntry> Read(string directoryPath);

    /// <summary>Returns true if the directory at <paramref name="path"/> is a symbolic link, junction or mount point.</summary>
    bool IsLink(string path);
}

/// <summary>
/// Reads directories with <see cref="FileSystemEnumerable{TResult}"/>, which returns metadata straight from the
/// directory listing without allocating <see cref="FileInfo"/> objects or opening files.
/// </summary>
public sealed class FileSystemDirectoryReader : IDirectoryReader
{
    private static readonly EnumerationOptions Options = new()
    {
        // Surface access errors instead of silently treating directories as empty.
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        BufferSize = 64 * 1024,
    };

    public static FileSystemDirectoryReader Instance { get; } = new();

    public IEnumerable<DirectoryEntry> Read(string directoryPath) =>
        new FileSystemEnumerable<DirectoryEntry>(
            directoryPath,
            static (ref FileSystemEntry e) => new DirectoryEntry(
                e.FileName.ToString(),
                e.IsDirectory,
                e.IsDirectory ? 0 : e.Length,
                e.Attributes,
                e.LastWriteTimeUtc.UtcDateTime),
            Options);

    public bool IsLink(string path)
    {
        try
        {
            // LinkTarget is non-null for symbolic links, junctions and volume mount points.
            // Cloud-file placeholders (e.g. OneDrive folders) also carry a reparse point but return null,
            // so their contents are still scanned.
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If the reparse point cannot be inspected, err on the side of not following it.
            return true;
        }
    }
}
