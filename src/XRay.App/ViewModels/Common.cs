using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using XRay.App.Controls;
using XRay.App.Services;
using XRay.Core.Scanning;

namespace XRay.App.ViewModels;

/// <summary>Base class for the pages shown in the main navigation.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Segoe Fluent Icons glyph for the navigation item.</summary>
    public abstract string Glyph { get; }

    /// <summary>Called when the page becomes visible.</summary>
    public virtual void OnActivated()
    {
    }

    /// <summary>Called when another page becomes visible.</summary>
    public virtual void OnDeactivated()
    {
    }
}

/// <summary>Bindable live progress of a running scan; shared by every screen that scans.</summary>
public sealed partial class ScanProgressState : ObservableObject
{
    [ObservableProperty]
    private string _files = "0";

    [ObservableProperty]
    private string _folders = "0";

    [ObservableProperty]
    private string _bytes = "0 B";

    [ObservableProperty]
    private string _elapsed = "0.0s";

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private string _issues = "0";

    [ObservableProperty]
    private bool _hasIssues;

    public void Reset() => Update(new ScanProgress(0, 0, 0, 0, null, TimeSpan.Zero));

    public void Update(ScanProgress p)
    {
        Files = Format.Count(p.FilesScanned);
        Folders = Format.Count(p.DirectoriesScanned);
        Bytes = Format.Size(p.BytesFound);
        Elapsed = Format.Duration(p.Elapsed);
        CurrentPath = p.CurrentPath ?? string.Empty;
        Issues = Format.Count(p.IssueCount);
        HasIssues = p.IssueCount > 0;
    }
}

/// <summary>A row in a list of files and folders.</summary>
public sealed class NodeRow
{
    public NodeRow(FileSystemNode node, SizeMetric metric, long referenceSize)
    {
        Node = node;
        long size = node.GetSize(metric);
        SizeText = Format.Size(size);
        Percent = referenceSize > 0 ? Math.Clamp(100.0 * size / referenceSize, 0, 100) : 0;
        PercentText = referenceSize > 0 ? (Percent >= 0.1 ? $"{Percent:0.0}%" : "<0.1%") : "-";
        Swatch = CategoryPalette.BrushFor(node);
    }

    public FileSystemNode Node { get; }

    public string Name => Node.Name;

    public bool IsDirectory => Node.IsDirectory;

    public string Glyph => Node.IsReparsePoint ? "" : Node.IsUnreadable ? "" : Node.IsDirectory ? "" : "";

    public string SizeText { get; }

    public double Percent { get; }

    public string PercentText { get; }

    public string FilesText => Node.IsDirectory ? Format.Count(Node.FileCount) : string.Empty;

    public string ModifiedText => Format.Date(Node.LastWriteTimeUtc);

    public string FolderText => Node.Parent?.FullPath ?? string.Empty;

    public Brush Swatch { get; }

    /// <summary>Short warning shown next to the name, or null.</summary>
    public string? Note =>
        Node.IsReparsePoint ? "Link - not followed"
        : (Node.Flags & NodeFlags.AccessDenied) != 0 ? "Access denied"
        : (Node.Flags & NodeFlags.Vanished) != 0 ? "Removed during scan"
        : (Node.Flags & NodeFlags.Error) != 0 ? "Read error"
        : (Node.Flags & NodeFlags.IncompleteDescendants) != 0 ? "Partly unreadable"
        : (Node.Flags & NodeFlags.CloudOrOffline) != 0 ? "Cloud / offline file"
        : null;

    public bool HasNote => Note is not null;
}
