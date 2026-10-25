namespace XRay.Core.Classification;

/// <summary>Broad file type groups used for treemap colouring and the legend.</summary>
public enum FileCategory
{
    Other,
    Video,
    Audio,
    Image,
    Archive,
    DiskImage,
    Executable,
    Document,
    Code,
    Data,
    System,
}

/// <summary>Maps file extensions to <see cref="FileCategory"/> values. Deterministic and allocation-free per lookup.</summary>
public static class FileTypeClassifier
{
    private static readonly Dictionary<string, FileCategory> Map = Build();

    /// <summary>Categories in legend order.</summary>
    public static IReadOnlyList<FileCategory> LegendOrder { get; } =
    [
        FileCategory.Video, FileCategory.Audio, FileCategory.Image, FileCategory.Archive, FileCategory.DiskImage,
        FileCategory.Executable, FileCategory.Document, FileCategory.Code, FileCategory.Data, FileCategory.System,
        FileCategory.Other,
    ];

    /// <summary>Classifies an extension such as ".mp4" (case-insensitive, dot optional).</summary>
    public static FileCategory Classify(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return FileCategory.Other;
        }

        if (extension[0] != '.')
        {
            extension = "." + extension;
        }

        return Map.TryGetValue(extension, out FileCategory category) ? category : FileCategory.Other;
    }

    public static string DisplayName(FileCategory category) => category switch
    {
        FileCategory.DiskImage => "Disk images",
        FileCategory.Executable => "Programs",
        FileCategory.Document => "Documents",
        FileCategory.Code => "Source code",
        FileCategory.Data => "Data & databases",
        FileCategory.System => "System & libraries",
        FileCategory.Archive => "Archives",
        FileCategory.Image => "Images",
        FileCategory.Video => "Video",
        FileCategory.Audio => "Audio",
        _ => "Other",
    };

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts)
        {
            foreach (string e in exts)
            {
                map["." + e] = c;
            }
        }

        Add(FileCategory.Video, "mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "mpg", "mpeg", "flv", "ts", "m2ts", "3gp", "vob");
        Add(FileCategory.Audio, "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus", "aiff", "mid", "midi");
        Add(FileCategory.Image, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "raw", "cr2", "nef", "arw", "dng", "psd", "svg", "ico", "avif", "exr", "hdr", "tga", "dds");
        Add(FileCategory.Archive, "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab", "lz", "lzma", "nupkg", "jar", "apk", "aab", "ipa");
        Add(FileCategory.DiskImage, "iso", "img", "vhd", "vhdx", "vmdk", "vdi", "qcow2", "dmg", "wim", "esd");
        Add(FileCategory.Executable, "exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "bat", "cmd", "ps1", "com", "scr");
        Add(FileCategory.Document, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "txt", "md", "epub", "csv", "one", "pst", "ost", "eml", "msg");
        Add(FileCategory.Code, "cs", "c", "cpp", "h", "hpp", "java", "js", "ts", "tsx", "jsx", "py", "rb", "go", "rs", "php", "swift", "kt", "html", "css", "scss", "xml", "json", "yaml", "yml", "xaml", "sql", "sh", "lua", "gd", "shader", "hlsl", "glsl", "csproj", "sln", "slnx");
        // ".ts" is ambiguous (MPEG transport stream vs TypeScript); size-wise video dominates, keep it as video.
        map[".ts"] = FileCategory.Video;
        Add(FileCategory.Data, "db", "sqlite", "sqlite3", "mdf", "ldf", "bak", "dat", "bin", "pak", "assets", "unity3d", "bundle", "blob", "log", "etl", "dmp", "mdmp", "hdmp", "tmp", "cache", "pdb", "ldb", "parquet", "npy", "pt", "safetensors", "ckpt", "gguf", "onnx");
        Add(FileCategory.System, "dll", "sys", "drv", "ocx", "mui", "cat", "inf", "efi", "lib", "a", "so", "dylib", "winmd", "node", "pyd");
        return map;
    }
}
