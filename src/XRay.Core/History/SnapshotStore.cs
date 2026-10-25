using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XRay.Core.Storage;

namespace XRay.Core.History;

/// <summary>
/// Persists snapshots as two files per snapshot in a local folder: a small summary (<c>{id}.meta.json</c>)
/// and the compressed directory entries (<c>{id}.data.json.gz</c>).
/// </summary>
public sealed class SnapshotStore
{
    private const string MetaSuffix = ".meta.json";
    private const string DataSuffix = ".data.json.gz";
    private readonly ILogger _logger;

    public SnapshotStore(string directory, ILogger<SnapshotStore>? logger = null)
    {
        Directory = directory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string Directory { get; }

    /// <summary>Raised after a snapshot was saved or deleted.</summary>
    public event EventHandler? Changed;

    public void Save(StorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Guid id = snapshot.Summary.Id;
        // Write the data first so a summary never points at missing entries.
        JsonFile.WriteAtomic(DataPath(id), snapshot.Entries, gzip: true);
        JsonFile.WriteAtomic(MetaPath(id), snapshot.Summary);
        _logger.LogInformation("Snapshot {Id} saved ({Entries} entries)", id, snapshot.Entries.Count);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lists all readable snapshot summaries, newest first. Corrupt files are skipped and logged.</summary>
    public List<SnapshotSummary> List()
    {
        var list = new List<SnapshotSummary>();
        if (!System.IO.Directory.Exists(Directory))
        {
            return list;
        }

        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*" + MetaSuffix))
        {
            try
            {
                SnapshotSummary? summary = JsonFile.Read<SnapshotSummary>(file);
                if (summary is not null && summary.Id != Guid.Empty && File.Exists(DataPath(summary.Id)))
                {
                    list.Add(summary);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Skipping unreadable snapshot file {File}: {Message}", Path.GetFileName(file), ex.Message);
            }
        }

        list.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return list;
    }

    /// <summary>Loads a full snapshot; returns null if it does not exist or is corrupt.</summary>
    public StorageSnapshot? Load(Guid id)
    {
        try
        {
            SnapshotSummary? summary = JsonFile.Read<SnapshotSummary>(MetaPath(id));
            List<SnapshotEntry>? entries = JsonFile.Read<List<SnapshotEntry>>(DataPath(id), gzip: true);
            if (summary is null || entries is null)
            {
                return null;
            }

            return new StorageSnapshot { Summary = summary, Entries = entries };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning("Could not load snapshot {Id}: {Message}", id, ex.Message);
            return null;
        }
    }

    public void Delete(Guid id)
    {
        TryDelete(MetaPath(id));
        TryDelete(DataPath(id));
        _logger.LogInformation("Snapshot {Id} deleted", id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes snapshots older than <paramref name="maxAge"/>. Returns the number removed.</summary>
    public int Prune(TimeSpan maxAge, DateTime? nowUtc = null)
    {
        DateTime cutoff = (nowUtc ?? DateTime.UtcNow) - maxAge;
        int removed = 0;
        foreach (SnapshotSummary s in List())
        {
            if (s.CreatedUtc < cutoff)
            {
                TryDelete(MetaPath(s.Id));
                TryDelete(DataPath(s.Id));
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Retention removed {Count} snapshot(s) older than {Days} days", removed, maxAge.TotalDays);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    private string MetaPath(Guid id) => Path.Combine(Directory, id.ToString("N") + MetaSuffix);

    private string DataPath(Guid id) => Path.Combine(Directory, id.ToString("N") + DataSuffix);

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete {File}: {Message}", Path.GetFileName(path), ex.Message);
        }
    }
}
