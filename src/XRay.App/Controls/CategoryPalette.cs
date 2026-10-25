using System.Windows.Media;
using XRay.Core.Classification;
using XRay.Core.Scanning;

namespace XRay.App.Controls;

/// <summary>Deterministic, muted colours for file categories, shared by the treemap, legend and lists.</summary>
public static class CategoryPalette
{
    private static readonly Dictionary<FileCategory, Color> Colors = new()
    {
        [FileCategory.Video] = Color.FromRgb(0x7A, 0x6C, 0xD9),
        [FileCategory.Audio] = Color.FromRgb(0xB8, 0x66, 0xC7),
        [FileCategory.Image] = Color.FromRgb(0x3F, 0xA8, 0x9D),
        [FileCategory.Archive] = Color.FromRgb(0xC9, 0x97, 0x3E),
        [FileCategory.DiskImage] = Color.FromRgb(0xC8, 0x6E, 0x3E),
        [FileCategory.Executable] = Color.FromRgb(0xC9, 0x5F, 0x6A),
        [FileCategory.Document] = Color.FromRgb(0x4E, 0x8C, 0xC9),
        [FileCategory.Code] = Color.FromRgb(0x7C, 0xA8, 0x4E),
        [FileCategory.Data] = Color.FromRgb(0x96, 0x86, 0x6A),
        [FileCategory.System] = Color.FromRgb(0x62, 0x72, 0x88),
        [FileCategory.Other] = Color.FromRgb(0x58, 0x60, 0x6C),
    };

    private static readonly Dictionary<FileCategory, SolidColorBrush> Brushes = Colors.ToDictionary(kv => kv.Key, kv => Freeze(new SolidColorBrush(kv.Value)));

    public static Color DirectoryFill { get; } = Color.FromRgb(0x1B, 0x1F, 0x25);

    public static Color DirectoryHeader { get; } = Color.FromRgb(0x29, 0x2E, 0x36);

    public static Color AggregateFill { get; } = Color.FromRgb(0x33, 0x38, 0x41);

    /// <summary>Folder drawn as a single tile because it is too small or deeper than the nesting limit.</summary>
    public static Color CollapsedDirectory { get; } = Color.FromRgb(0x2A, 0x4A, 0x55);

    public static SolidColorBrush DirectoryBrush { get; } = Freeze(new SolidColorBrush(Color.FromRgb(0x2F, 0x8F, 0xA8)));

    public static Color For(FileCategory category) => Colors[category];

    public static Color For(FileSystemNode node) => For(FileTypeClassifier.Classify(node.Extension));

    public static SolidColorBrush BrushFor(FileCategory category) => Brushes[category];

    /// <summary>Swatch brush for a list row: directories use a neutral accent, files their category colour.</summary>
    public static SolidColorBrush BrushFor(FileSystemNode node) =>
        node.IsDirectory ? DirectoryBrush : BrushFor(FileTypeClassifier.Classify(node.Extension));

    public static Color Shade(Color c, double factor)
    {
        static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        return factor >= 0
            ? Color.FromRgb(Clamp(c.R + (255 - c.R) * factor), Clamp(c.G + (255 - c.G) * factor), Clamp(c.B + (255 - c.B) * factor))
            : Color.FromRgb(Clamp(c.R * (1 + factor)), Clamp(c.G * (1 + factor)), Clamp(c.B * (1 + factor)));
    }

    /// <summary>Relative luminance in [0, 1], used to pick readable label colours.</summary>
    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static T Freeze<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
