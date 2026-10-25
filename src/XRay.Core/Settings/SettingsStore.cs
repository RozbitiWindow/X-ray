using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XRay.Core.Storage;

namespace XRay.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/>; tolerates missing or malformed files.</summary>
public sealed class SettingsStore
{
    private readonly ILogger _logger;

    public SettingsStore(string filePath, ILogger<SettingsStore>? logger = null)
    {
        FilePath = filePath;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string FilePath { get; }

    /// <summary>
    /// Loads settings. A missing file yields defaults. A malformed file is preserved as
    /// <c>settings.corrupt-{timestamp}.json</c> and defaults are returned.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            return (JsonFile.Read<AppSettings>(FilePath) ?? new AppSettings()).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            string backup = Path.Combine(
                Path.GetDirectoryName(FilePath)!,
                $"settings.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            try
            {
                File.Move(FilePath, backup, overwrite: true);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not back up malformed settings file: {Message}", moveEx.Message);
            }

            _logger.LogWarning("Settings file was malformed and has been reset to defaults: {Message}", ex.Message);
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Settings file could not be read, using defaults: {Message}", ex.Message);
            return new AppSettings();
        }
    }

    public bool TrySave(AppSettings settings)
    {
        try
        {
            JsonFile.WriteAtomic(FilePath, settings.Normalize());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Could not save settings: {Message}", ex.Message);
            return false;
        }
    }
}
