using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XRay.App.Services;
using XRay.Core.Classification;
using XRay.Core.Scanning;
using XRay.Core.Treemap;

namespace XRay.App.Controls;

/// <summary>
/// Interactive squarified treemap. All tiles are drawn into a single <see cref="DrawingVisual"/>, so thousands of
/// items cost one visual instead of thousands of elements. Hover and selection are drawn on a separate overlay
/// visual so pointer movement never re-renders the map.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(FileSystemNode), typeof(TreemapControl), new PropertyMetadata(null, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(SizeMetric), typeof(TreemapControl), new PropertyMetadata(SizeMetric.Logical, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MaxDepthProperty = DependencyProperty.Register(
        nameof(MaxDepth), typeof(int), typeof(TreemapControl), new PropertyMetadata(3, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(TreemapControl), new PropertyMetadata(true, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ShadingProperty = DependencyProperty.Register(
        nameof(Shading), typeof(bool), typeof(TreemapControl), new PropertyMetadata(true, OnLayoutPropertyChanged));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(FileSystemNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TreemapControl)d).DrawOverlay()));

    public static readonly DependencyProperty NavigateCommandProperty = DependencyProperty.Register(
        nameof(NavigateCommand), typeof(ICommand), typeof(TreemapControl));

    public static readonly DependencyProperty BackgroundProperty = Panel.BackgroundProperty.AddOwner(
        typeof(TreemapControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface LabelFaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly DrawingVisual _content = new();
    private readonly DrawingVisual _overlay = new();
    private readonly VisualCollection _visuals;
    private readonly Dictionary<(Color, bool), Brush> _brushCache = new();
    private readonly Popup _popup;
    private readonly TextBlock _tipTitle = new();
    private readonly TextBlock _tipPath = new();
    private readonly TextBlock _tipSize = new();
    private List<TreemapItem> _items = [];
    private int _hoverIndex = -1;
    private bool _rebuildPending;
    private FileSystemNode? _lastClickedFile;

    public TreemapControl()
    {
        _visuals = new VisualCollection(this) { _content, _overlay };
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        _popup = CreatePopup();
        SizeChanged += (_, _) => ScheduleRebuild();
        Unloaded += (_, _) => _popup.IsOpen = false;
    }

    public FileSystemNode? Root
    {
        get => (FileSystemNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public SizeMetric Metric
    {
        get => (SizeMetric)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public int MaxDepth
    {
        get => (int)GetValue(MaxDepthProperty);
        set => SetValue(MaxDepthProperty, value);
    }

    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    public bool Shading
    {
        get => (bool)GetValue(ShadingProperty);
        set => SetValue(ShadingProperty, value);
    }

    public FileSystemNode? SelectedNode
    {
        get => (FileSystemNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    /// <summary>Executed with a directory node when the user clicks a directory.</summary>
    public ICommand? NavigateCommand
    {
        get => (ICommand?)GetValue(NavigateCommandProperty);
        set => SetValue(NavigateCommandProperty, value);
    }

    /// <summary>Number of rectangles currently drawn (for diagnostics and tests).</summary>
    public int ItemCount => _items.Count;

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnRender(DrawingContext dc)
    {
        // Background that also makes the whole area hit-testable.
        dc.DrawRectangle(Background ?? Brushes.Black, null, new Rect(RenderSize));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        int index = TreemapBuilder.HitTest(_items, p.X, p.Y);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            DrawOverlay();
            UpdateTooltip();
        }

        if (_hoverIndex >= 0)
        {
            _popup.HorizontalOffset = p.X + 16;
            _popup.VerticalOffset = p.Y + 18;
            _popup.IsOpen = true;
        }
        else
        {
            _popup.IsOpen = false;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        _popup.IsOpen = false;
        DrawOverlay();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        int index = TreemapBuilder.HitTest(_items, e.GetPosition(this).X, e.GetPosition(this).Y);
        if (index < 0)
        {
            return;
        }

        TreemapItem item = _items[index];
        if (item.IsAggregate)
        {
            // Zoom into the folder that owns the small items so they get room to be drawn.
            if (e.ClickCount == 1 && item.AggregateOwner is { } owner && !ReferenceEquals(owner, Root))
            {
                Navigate(owner);
            }

            return;
        }

        FileSystemNode node = item.Node!;
        if (node.IsDirectory)
        {
            if (e.ClickCount == 1)
            {
                Navigate(node);
            }

            _lastClickedFile = null;
        }
        else if (e.ClickCount == 2 && ReferenceEquals(node, _lastClickedFile))
        {
            // Double-click on a file opens its folder.
            if (node.Parent is { } parent && !ReferenceEquals(parent, Root))
            {
                Navigate(parent);
            }

            _lastClickedFile = null;
        }
        else
        {
            SelectedNode = node;
            _lastClickedFile = node;
        }

        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        int index = TreemapBuilder.HitTest(_items, e.GetPosition(this).X, e.GetPosition(this).Y);
        if (index >= 0 && _items[index].Node is { } node)
        {
            SelectedNode = node;
        }

        _popup.IsOpen = false;
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TreemapControl)d).ScheduleRebuild();

    private void Navigate(FileSystemNode directory)
    {
        _popup.IsOpen = false;
        if (NavigateCommand?.CanExecute(directory) == true)
        {
            NavigateCommand.Execute(directory);
        }
    }

    private void ScheduleRebuild()
    {
        if (_rebuildPending)
        {
            return;
        }

        _rebuildPending = true;
        // Coalesce bursts of size/property changes (e.g. while resizing) into one layout pass.
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _rebuildPending = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        _hoverIndex = -1;
        FileSystemNode? root = Root;
        if (root is null || ActualWidth < 2 || ActualHeight < 2)
        {
            _items = [];
        }
        else
        {
            var options = new TreemapBuildOptions
            {
                Metric = Metric,
                MaxDepth = Math.Clamp(MaxDepth, 1, 4),
                HeaderHeight = ShowLabels ? 18 : 0,
                Padding = 2,
                MinItemArea = 12,
            };
            _items = TreemapBuilder.Build(root, new TreemapRect(1, 1, ActualWidth - 2, ActualHeight - 2), options);
        }

        DrawContent();
        DrawOverlay();
    }

    private void DrawContent()
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        using DrawingContext dc = _content.RenderOpen();
        foreach (TreemapItem item in _items)
        {
            Rect r = Snap(item.Rect);
            if (r.Width < 0.5 || r.Height < 0.5)
            {
                continue;
            }

            if (item.IsAggregate)
            {
                Color c = CategoryPalette.AggregateFill;
                dc.DrawRectangle(GetBrush(c, gradient: false), null, r);
                if (ShowLabels && r.Width >= 60 && r.Height >= 18)
                {
                    DrawLabel(dc, $"{item.AggregateCount:N0} small items", null, r, c, pixelsPerDip, bold: false);
                }

                continue;
            }

            FileSystemNode node = item.Node!;
            if (node.IsDirectory && item.HasNestedItems)
            {
                dc.DrawRectangle(GetBrush(CategoryPalette.DirectoryFill, gradient: false), null, r);
                if (item.HeaderHeight > 0)
                {
                    var header = new Rect(r.X, r.Y, r.Width, Math.Min(item.HeaderHeight + 1, r.Height));
                    dc.DrawRectangle(GetBrush(CategoryPalette.DirectoryHeader, gradient: false), null, header);
                    if (ShowLabels)
                    {
                        DrawHeader(dc, node, header, pixelsPerDip);
                    }
                }
            }
            else
            {
                Color c = node.IsDirectory ? CategoryPalette.CollapsedDirectory : CategoryPalette.For(node);
                dc.DrawRectangle(GetBrush(c, Shading), null, r);
                if (ShowLabels && r.Width >= 54 && r.Height >= 18)
                {
                    string? size = r.Height >= 34 ? Format.Size(node.GetSize(Metric)) : null;
                    DrawLabel(dc, node.Name, size, r, c, pixelsPerDip, bold: node.IsDirectory);
                }
            }
        }
    }

    private void DrawHeader(DrawingContext dc, FileSystemNode node, Rect header, double pixelsPerDip)
    {
        if (header.Width < 40)
        {
            return;
        }

        string text = $"{node.Name}  ·  {Format.Size(node.GetSize(Metric))}";
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFaceBold, 11,
            new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE2)), pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, header.Width - 10),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, new Point(header.X + 6, header.Y + (header.Height - ft.Height) / 2));
    }

    private static void DrawLabel(DrawingContext dc, string name, string? size, Rect r, Color fill, double pixelsPerDip, bool bold)
    {
        Brush textBrush = CategoryPalette.Luminance(fill) > 0.62 ? DarkText : LightText;
        var ft = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? LabelFaceBold : LabelFace, 11,
            textBrush, pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, r.Width - 10),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, new Point(r.X + 5, r.Y + 3));
        if (size is not null)
        {
            var st = new FormattedText(size, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5,
                textBrush, pixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, r.Width - 10),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.PushOpacity(0.75);
            dc.DrawText(st, new Point(r.X + 5, r.Y + 3 + ft.Height));
            dc.Pop();
        }
    }

    private static readonly Brush LightText = FrozenBrush(Color.FromRgb(0xF4, 0xF6, 0xF8));
    private static readonly Brush DarkText = FrozenBrush(Color.FromRgb(0x14, 0x17, 0x1C));

    private static Brush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void DrawOverlay()
    {
        using DrawingContext dc = _overlay.RenderOpen();
        Brush accent = TryFindResource("Accent") as Brush ?? Brushes.Cyan;

        FileSystemNode? selected = SelectedNode;
        if (selected is not null)
        {
            foreach (TreemapItem item in _items)
            {
                if (ReferenceEquals(item.Node, selected))
                {
                    var pen = new Pen(accent, 2);
                    Rect r = Snap(item.Rect);
                    r.Inflate(-1, -1);
                    if (r.Width > 0 && r.Height > 0)
                    {
                        dc.DrawRectangle(null, pen, r);
                    }

                    break;
                }
            }
        }

        if (_hoverIndex >= 0 && _hoverIndex < _items.Count)
        {
            Rect r = Snap(_items[_hoverIndex].Rect);
            r.Inflate(-0.5, -0.5);
            if (r.Width > 0 && r.Height > 0)
            {
                dc.DrawRectangle(HoverFill, new Pen(HoverStroke, 1), r);
            }
        }
    }

    private static readonly Brush HoverFill = FrozenBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly Brush HoverStroke = FrozenBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));

    private void UpdateTooltip()
    {
        if (_hoverIndex < 0 || _hoverIndex >= _items.Count)
        {
            return;
        }

        TreemapItem item = _items[_hoverIndex];
        if (item.IsAggregate)
        {
            _tipTitle.Text = $"{item.AggregateCount:N0} smaller items";
            _tipPath.Text = item.AggregateOwner is { } owner
                ? $"Too small to draw individually in {owner.Name}. They are listed in the table; click to zoom in."
                : "Too small to draw individually.";
            _tipSize.Text = Format.Size(item.AggregateSize) + " combined";
            return;
        }

        FileSystemNode node = item.Node!;
        _tipTitle.Text = node.Name;
        _tipPath.Text = node.FullPath;
        string kind = node.IsDirectory
            ? $"Folder · {node.FileCount:N0} files"
            : FileTypeClassifier.DisplayName(FileTypeClassifier.Classify(node.Extension));
        long parentSize = node.Parent?.GetSize(Metric) ?? 0;
        string share = parentSize > 0 ? $" · {100.0 * node.GetSize(Metric) / parentSize:0.#}% of parent" : string.Empty;
        string flags = node.IsIncomplete ? " · partly unreadable" : node.IsReparsePoint ? " · link (not followed)" : string.Empty;
        _tipSize.Text = $"{Format.Size(node.GetSize(Metric))} · {kind}{share}{flags}";
    }

    private Popup CreatePopup()
    {
        _tipTitle.FontWeight = FontWeights.SemiBold;
        _tipTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _tipTitle.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        _tipPath.FontSize = 11.5;
        _tipPath.TextWrapping = TextWrapping.Wrap;
        _tipPath.Margin = new Thickness(0, 2, 0, 4);
        _tipPath.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _tipSize.FontSize = 12;
        _tipSize.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Text");

        var panel = new StackPanel();
        panel.Children.Add(_tipTitle);
        panel.Children.Add(_tipPath);
        panel.Children.Add(_tipSize);

        var border = new Border
        {
            Child = panel,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            MaxWidth = 420,
        };
        border.SetResourceReference(Border.BackgroundProperty, "Bg.SurfaceRaised");
        border.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
        TextElement.SetFontFamily(border, new FontFamily("Segoe UI Variable Text, Segoe UI"));
        TextElement.SetFontSize(border, 12.5);

        return new Popup
        {
            Child = border,
            PlacementTarget = this,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Focusable = false,
        };
    }

    private Brush GetBrush(Color c, bool gradient)
    {
        if (_brushCache.TryGetValue((c, gradient), out Brush? brush))
        {
            return brush;
        }

        brush = gradient
            ? new LinearGradientBrush(CategoryPalette.Shade(c, 0.10), CategoryPalette.Shade(c, -0.18), 90)
            : new SolidColorBrush(c);
        brush.Freeze();
        _brushCache[(c, gradient)] = brush;
        return brush;
    }

    /// <summary>Rounds to whole device-independent pixels and leaves a 1 px gap between neighbours.</summary>
    private static Rect Snap(TreemapRect r)
    {
        double x1 = Math.Round(r.X), y1 = Math.Round(r.Y);
        double x2 = Math.Round(r.Right), y2 = Math.Round(r.Bottom);
        double w = Math.Max(0, x2 - x1 - 1), h = Math.Max(0, y2 - y1 - 1);
        return new Rect(x1, y1, w, h);
    }
}
