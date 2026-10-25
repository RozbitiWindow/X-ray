using XRay.Core.Formatting;
using XRay.Core.Scanning;

namespace XRay.Core.Settings;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>User preferences, persisted as JSON. All values are validated by <see cref="Normalize"/>.</summary>
public sealed class AppSettings
{
    public static readonly int[] AllowedPollingIntervals = [500, 1000, 2000, 5000];
    public static readonly int[] AllowedRetentionDays = [0, 30, 90, 180, 365];

    public int Version { get; set; } = 1;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public SizeUnitMode SizeUnits { get; set; } = SizeUnitMode.Binary;

    /// <summary>Whether sizes and the treemap use logical size or estimated size on disk.</summary>
    public SizeMetric SizeMetric { get; set; } = SizeMetric.Logical;

    /// <summary>Folder pre-filled for new scans; null uses the user profile folder.</summary>
    public string? DefaultScanPath { get; set; }

    /// <summary>Nested directory levels drawn in the treemap (1-4).</summary>
    public int TreemapDepth { get; set; } = 3;

    public bool TreemapShowLabels { get; set; } = true;

    /// <summary>Subtle vertical shading on treemap tiles.</summary>
    public bool TreemapShading { get; set; } = true;

    public int PollingIntervalMs { get; set; } = 1000;

    /// <summary>Snapshots older than this many days are deleted at startup; 0 keeps them forever.</summary>
    public int HistoryRetentionDays { get; set; }

    /// <summary>Directory levels recorded in each snapshot.</summary>
    public int SnapshotDepth { get; set; } = 6;

    /// <summary>Register X-RAY to start when the user signs in. Only applied after an explicit user action.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Clamps out-of-range values to safe defaults. Returns the same instance.</summary>
    public AppSettings Normalize()
    {
        if (!Enum.IsDefined(Theme))
        {
            Theme = AppTheme.Dark;
        }

        if (!Enum.IsDefined(SizeUnits))
        {
            SizeUnits = SizeUnitMode.Binary;
        }

        if (!Enum.IsDefined(SizeMetric))
        {
            SizeMetric = SizeMetric.Logical;
        }

        TreemapDepth = Math.Clamp(TreemapDepth, 1, 4);
        SnapshotDepth = Math.Clamp(SnapshotDepth, 2, 12);
        if (!AllowedPollingIntervals.Contains(PollingIntervalMs))
        {
            PollingIntervalMs = AllowedPollingIntervals.MinBy(v => Math.Abs(v - PollingIntervalMs));
        }

        if (!AllowedRetentionDays.Contains(HistoryRetentionDays))
        {
            HistoryRetentionDays = 0;
        }

        if (string.IsNullOrWhiteSpace(DefaultScanPath))
        {
            DefaultScanPath = null;
        }

        return this;
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
