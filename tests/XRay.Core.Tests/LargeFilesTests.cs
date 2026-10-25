using XRay.Core.LargeFiles;
using XRay.Core.Scanning;

namespace XRay.Core.Tests;

public class LargeFilesTests
{
    private const long MiB = 1024 * 1024;

    private static FileSystemNode BuildTree()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\Users\test");
        FileSystemNode downloads = root.AddDirectory("Downloads");
        downloads.AddFile("ubuntu.iso", 4000 * MiB, lastWriteUtc: new DateTime(2022, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        downloads.AddFile("tool-setup.exe", 80 * MiB, lastWriteUtc: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        downloads.AddFile("photos.zip", 300 * MiB, lastWriteUtc: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        FileSystemNode project = root.AddDirectory("source").AddDirectory("App").AddDirectory("obj");
        project.AddFile("app.dll", 20 * MiB);
        FileSystemNode logs = root.AddDirectory("logs");
        logs.AddFile("big.log", 50 * MiB);
        logs.AddFile("small.log", 1 * MiB);
        root.AddFile("notes.txt", 1024);
        root.AddFile("crash.dmp", 200 * MiB);
        TreeAggregator.Aggregate(root);
        return root;
    }

    [Fact]
    public void Minimum_size_filters_and_results_are_largest_first()
    {
        LargeFileSearchResult result = LargeFileFinder.Find(BuildTree(), new LargeFileQuery { MinimumSize = 100 * MiB });

        Assert.Equal(["ubuntu.iso", "photos.zip", "crash.dmp"], result.Matches.Select(m => m.Name));
        Assert.Equal(3, result.TotalMatches);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void Extension_filter_accepts_user_input_variants()
    {
        var query = new LargeFileQuery { Extensions = LargeFileQuery.ParseExtensions("iso, *.ZIP; log") };
        var names = LargeFileFinder.Find(BuildTree(), query).Matches.Select(m => m.Name).ToHashSet();

        Assert.Equal(new HashSet<string> { "ubuntu.iso", "photos.zip", "big.log", "small.log" }, names);
        Assert.Null(LargeFileQuery.ParseExtensions("  "));
    }

    [Fact]
    public void Age_filter_excludes_recent_files()
    {
        var query = new LargeFileQuery
        {
            MinimumSize = 50 * MiB,
            ModifiedBeforeUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var names = LargeFileFinder.Find(BuildTree(), query).Matches.Select(m => m.Name).ToList();

        Assert.Equal(["ubuntu.iso", "tool-setup.exe"], names);
    }

    [Fact]
    public void Result_limit_truncates_but_counts_all_matches()
    {
        LargeFileSearchResult result = LargeFileFinder.Find(BuildTree(), new LargeFileQuery { MaxResults = 2 });

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(8, result.TotalMatches);
        Assert.True(result.IsTruncated);
        Assert.Equal("ubuntu.iso", result.Matches[0].Name);
    }

    [Fact]
    public void Category_filter_uses_heuristics()
    {
        var installers = LargeFileFinder.Find(BuildTree(), new LargeFileQuery { Category = ReviewCategory.Installer });
        Assert.Equal("tool-setup.exe", Assert.Single(installers.Matches).Name);

        var suggested = LargeFileFinder.Find(BuildTree(), new LargeFileQuery { OnlySuggested = true });
        Assert.DoesNotContain(suggested.Matches, m => m.Name == "notes.txt");
        Assert.DoesNotContain(suggested.Matches, m => m.Name == "small.log");
    }

    [Theory]
    [InlineData(@"C:\Users\x\Downloads\ubuntu.iso", 1L, ReviewCategory.DiskImage)]
    [InlineData(@"C:\Users\x\Downloads\setup.exe", 1L, ReviewCategory.Installer)]
    [InlineData(@"C:\Users\x\Desktop\app.msi", 1L, ReviewCategory.Installer)]
    [InlineData(@"C:\Users\x\Desktop\game.exe", 1L, ReviewCategory.None)]
    [InlineData(@"C:\Users\x\AppData\Local\CrashDumps\app.exe.123.dmp", 1L, ReviewCategory.CrashDump)]
    [InlineData(@"C:\Users\x\source\repos\App\obj\Debug\app.dll", 1L, ReviewCategory.BuildOutput)]
    [InlineData(@"C:\Users\x\source\repos\App\bin\Release\app.exe", 1L, ReviewCategory.BuildOutput)]
    [InlineData(@"C:\Program Files\Tool\bin\tool.dll", 1L, ReviewCategory.None)]
    [InlineData(@"C:\Users\x\builds\game.apk", 1L, ReviewCategory.BuildOutput)]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\file.dat", 1L, ReviewCategory.Temporary)]
    [InlineData(@"C:\Users\x\Documents\~$report.docx", 1L, ReviewCategory.Temporary)]
    [InlineData(@"C:\logs\service.log", 20L * 1024 * 1024, ReviewCategory.LargeLog)]
    [InlineData(@"C:\logs\service.log", 1024L, ReviewCategory.None)]
    [InlineData(@"C:\data\db.bak", 1L, ReviewCategory.Backup)]
    [InlineData(@"C:\data\photos.7z", 1L, ReviewCategory.Archive)]
    [InlineData(@"C:\data\report.pdf", 1L, ReviewCategory.None)]
    [InlineData(@"C:\Windows\Installer\29629d.msi", 1L, ReviewCategory.None)]
    [InlineData(@"C:\Windows\WinSxS\x\package.cab", 1L, ReviewCategory.None)]
    [InlineData(@"C:\Windows\Temp\setup.log", 20L * 1024 * 1024, ReviewCategory.LargeLog)]
    public void Heuristics_classify_paths(string path, long size, ReviewCategory expected) =>
        Assert.Equal(expected, ReviewHeuristics.Evaluate(path, size).Category);

    [Fact]
    public void Suggestions_never_claim_files_are_safe_to_delete()
    {
        foreach (string path in new[] { @"C:\a\x.iso", @"C:\a\x.msi", @"C:\a\x.dmp", @"C:\a\x.bak", @"C:\Temp\x.tmp" })
        {
            string reason = ReviewHeuristics.Evaluate(path, 1).Reason;
            Assert.DoesNotContain("safe", reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Sorting_orders_results()
    {
        var matches = LargeFileFinder.Find(BuildTree(), new LargeFileQuery { MinimumSize = 50 * MiB }).Matches;

        Assert.Equal("ubuntu.iso", LargeFileFinder.Sort(matches, LargeFileSort.OldestFirst)[0].Name);
        Assert.Equal("big.log", LargeFileFinder.Sort(matches, LargeFileSort.SizeAscending)[0].Name);
        Assert.Equal("big.log", LargeFileFinder.Sort(matches, LargeFileSort.Name)[0].Name);
        Assert.Equal("ubuntu.iso", LargeFileFinder.Sort(matches, LargeFileSort.SizeDescending)[0].Name);
    }

    [Fact]
    public void Search_honours_cancellation()
    {
        FileSystemNode root = FileSystemNode.CreateRoot(@"C:\many");
        for (int i = 0; i < 50_000; i++)
        {
            root.AddFile($"f{i}", i);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => LargeFileFinder.Find(root, new LargeFileQuery(), cts.Token));
    }
}
