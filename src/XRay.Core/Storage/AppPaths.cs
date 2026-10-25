namespace XRay.Core.Storage;

/// <summary>Locations of X-RAY's local data. Everything lives under %LOCALAPPDATA%\X-RAY.</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-RAY");
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string StateFile => Path.Combine(Root, "state.json");

    public string SnapshotsDirectory => Path.Combine(Root, "Snapshots");

    public string LogsDirectory => Path.Combine(Root, "Logs");
}
