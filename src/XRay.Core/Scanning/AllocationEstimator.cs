using System.Runtime.InteropServices;

namespace XRay.Core.Scanning;

/// <summary>
/// Estimates the on-disk allocation of files. Ordinary files are rounded up to the volume cluster size;
/// compressed, sparse and cloud placeholder files query the file system for their real allocation.
/// </summary>
internal sealed class AllocationEstimator
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private const FileAttributes QueryAttributes =
        FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    private AllocationEstimator(long clusterSize) => ClusterSize = clusterSize;

    public long ClusterSize { get; }

    public static AllocationEstimator ForPath(string path)
    {
        long cluster = 4096;
        if (OperatingSystem.IsWindows())
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(root) &&
                NativeMethods.GetDiskFreeSpaceW(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _) &&
                sectorsPerCluster > 0 && bytesPerSector > 0)
            {
                cluster = (long)sectorsPerCluster * bytesPerSector;
            }
        }

        return new AllocationEstimator(cluster);
    }

    /// <summary>True if the attributes indicate the file's data may not be stored locally.</summary>
    public static bool IsCloudOrOffline(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    public long Estimate(string fullPath, long length, FileAttributes attributes)
    {
        if ((attributes & QueryAttributes) != 0 && OperatingSystem.IsWindows())
        {
            uint low = NativeMethods.GetCompressedFileSizeW(ToExtendedPath(fullPath), out uint high);
            if (low != uint.MaxValue || Marshal.GetLastWin32Error() == 0)
            {
                long actual = ((long)high << 32) | low;
                return RoundUp(actual);
            }
        }

        return RoundUp(length);
    }

    /// <summary>Adds the \\?\ prefix to paths that exceed MAX_PATH so Win32 APIs accept them.</summary>
    internal static string ToExtendedPath(string path)
    {
        if (path.Length < 260 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    private long RoundUp(long length) =>
        length <= 0 ? 0 : (length + ClusterSize - 1) / ClusterSize * ClusterSize;

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceW(
            string lpRootPathName,
            out uint lpSectorsPerCluster,
            out uint lpBytesPerSector,
            out uint lpNumberOfFreeClusters,
            out uint lpTotalNumberOfClusters);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);
    }
}
