using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XRay.Core.Storage;

/// <summary>Small helpers for reading and atomically writing JSON files.</summary>
public static class JsonFile
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static JsonSerializerOptions CompactOptions { get; } = new(Options) { WriteIndented = false };

    /// <summary>Writes <paramref name="value"/> to a temporary file and then moves it over <paramref name="path"/>.</summary>
    public static void WriteAtomic<T>(string path, T value, bool gzip = false)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("Path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (gzip)
            {
                using var zip = new GZipStream(stream, CompressionLevel.Optimal);
                JsonSerializer.Serialize(zip, value, CompactOptions);
            }
            else
            {
                JsonSerializer.Serialize(stream, value, Options);
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads a JSON file; throws <see cref="JsonException"/> for malformed content.</summary>
    public static T? Read<T>(string path, bool gzip = false)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (gzip)
        {
            using var zip = new GZipStream(stream, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<T>(zip, Options);
        }

        return JsonSerializer.Deserialize<T>(stream, Options);
    }
}
