using XRay.Core.Classification;
using XRay.Core.Collections;
using XRay.Core.Formatting;
using XRay.Core.Settings;
using XRay.Core.Storage;

namespace XRay.Core.Tests;

public class SettingsAndUtilityTests
{
    [Fact]
    public void Missing_settings_file_yields_defaults()
    {
        using var dir = new TestDirectory();
        AppSettings s = new SettingsStore(Path.Combine(dir.Path, "settings.json")).Load();
        Assert.Equal(AppTheme.Dark, s.Theme);
        Assert.Equal(1000, s.PollingIntervalMs);
    }

    [Fact]
    public void Malformed_settings_file_is_backed_up_and_defaults_are_used()
    {
        using var dir = new TestDirectory();
        string path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{ \"theme\": \"Dark\", oops");

        AppSettings s = new SettingsStore(path).Load();

        Assert.Equal(AppTheme.Dark, s.Theme);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(dir.Path, "settings.corrupt-*.json"));
    }

    [Fact]
    public void Invalid_enum_value_in_settings_is_handled()
    {
        using var dir = new TestDirectory();
        string path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{ \"theme\": \"Purple\" }");

        AppSettings s = new SettingsStore(path).Load();

        Assert.Equal(AppTheme.Dark, s.Theme);
    }

    [Fact]
    public void Settings_round_trip_and_out_of_range_values_are_clamped()
    {
        using var dir = new TestDirectory();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var settings = new AppSettings
        {
            Theme = AppTheme.Light,
            SizeUnits = SizeUnitMode.Decimal,
            TreemapDepth = 42,
            PollingIntervalMs = 1700,
            HistoryRetentionDays = 12,
            DefaultScanPath = "   ",
        };

        Assert.True(store.TrySave(settings));
        AppSettings loaded = store.Load();

        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.Equal(SizeUnitMode.Decimal, loaded.SizeUnits);
        Assert.Equal(4, loaded.TreemapDepth);
        Assert.Equal(2000, loaded.PollingIntervalMs);
        Assert.Equal(0, loaded.HistoryRetentionDays);
        Assert.Null(loaded.DefaultScanPath);
    }

    [Fact]
    public void App_state_tolerates_garbage_and_tracks_recent_folders()
    {
        using var dir = new TestDirectory();
        string path = Path.Combine(dir.Path, "state.json");
        File.WriteAllText(path, "garbage");
        var store = new AppStateStore(path);
        AppState state = store.Load();
        Assert.Null(state.LastScan);

        state.AddRecentFolder(@"C:\a");
        state.AddRecentFolder(@"C:\b");
        state.AddRecentFolder(@"c:\A");
        store.TrySave(state);

        Assert.Equal([@"c:\A", @"C:\b"], store.Load().RecentFolders);
    }

    [Fact]
    public void Ring_buffer_keeps_latest_items_in_order()
    {
        var buffer = new RingBuffer<int>(3);
        for (int i = 1; i <= 5; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal([3, 4, 5], buffer);
        Assert.Equal(5, buffer.Latest);
        Assert.Equal(3, buffer.Count);
        buffer.Clear();
        Assert.Empty(buffer);
    }

    [Theory]
    [InlineData(".MP4", FileCategory.Video)]
    [InlineData("zip", FileCategory.Archive)]
    [InlineData(".dll", FileCategory.System)]
    [InlineData(".unknownext", FileCategory.Other)]
    [InlineData("", FileCategory.Other)]
    public void File_types_are_classified_deterministically(string ext, FileCategory expected) =>
        Assert.Equal(expected, FileTypeClassifier.Classify(ext));
}
