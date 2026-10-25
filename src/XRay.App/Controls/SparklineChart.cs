using System.Windows;
using System.Windows.Media;

namespace XRay.App.Controls;

/// <summary>
/// Lightweight line/area chart for session history. Draws one or two series from a list of values using a single
/// <see cref="StreamGeometry"/> per series. Re-renders when <see cref="Revision"/> changes.
/// </summary>
public sealed class SparklineChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = Register(nameof(Values), typeof(IReadOnlyList<double>), null);
    public static readonly DependencyProperty SecondaryValuesProperty = Register(nameof(SecondaryValues), typeof(IReadOnlyList<double>), null);
    public static readonly DependencyProperty RevisionProperty = Register(nameof(Revision), typeof(long), 0L);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), typeof(double), 100.0);
    public static readonly DependencyProperty AutoScaleProperty = Register(nameof(AutoScale), typeof(bool), false);
    public static readonly DependencyProperty CapacityProperty = Register(nameof(Capacity), typeof(int), 120);
    public static readonly DependencyProperty StrokeProperty = Register(nameof(Stroke), typeof(Brush), Brushes.DeepSkyBlue);
    public static readonly DependencyProperty FillProperty = Register(nameof(Fill), typeof(Brush), null);
    public static readonly DependencyProperty SecondaryStrokeProperty = Register(nameof(SecondaryStroke), typeof(Brush), Brushes.MediumPurple);
    public static readonly DependencyProperty SecondaryFillProperty = Register(nameof(SecondaryFill), typeof(Brush), null);
    public static readonly DependencyProperty GridBrushProperty = Register(nameof(GridBrush), typeof(Brush), null);

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public IReadOnlyList<double>? SecondaryValues { get => (IReadOnlyList<double>?)GetValue(SecondaryValuesProperty); set => SetValue(SecondaryValuesProperty, value); }

    /// <summary>Bump to force a redraw after the underlying buffers changed.</summary>
    public long Revision { get => (long)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }

    /// <summary>Upper bound of the value axis when <see cref="AutoScale"/> is false.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>Scale the axis to the largest visible value (used for throughput).</summary>
    public bool AutoScale { get => (bool)GetValue(AutoScaleProperty); set => SetValue(AutoScaleProperty, value); }

    /// <summary>Number of samples the full width represents.</summary>
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public Brush? SecondaryStroke { get => (Brush?)GetValue(SecondaryStrokeProperty); set => SetValue(SecondaryStrokeProperty, value); }

    public Brush? SecondaryFill { get => (Brush?)GetValue(SecondaryFillProperty); set => SetValue(SecondaryFillProperty, value); }

    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4)
        {
            return;
        }

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (GridBrush is { } grid)
        {
            var pen = new Pen(grid, 1);
            pen.Freeze();
            for (int i = 1; i < 4; i++)
            {
                double y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            }
        }

        double max = Maximum;
        if (AutoScale)
        {
            max = Math.Max(MaxOf(Values), MaxOf(SecondaryValues));
            max = max <= 0 ? 1 : max * 1.15;
        }

        DrawSeries(dc, SecondaryValues, SecondaryStroke, SecondaryFill, max, w, h);
        DrawSeries(dc, Values, Stroke, Fill, max, w, h);
    }

    private void DrawSeries(DrawingContext dc, IReadOnlyList<double>? values, Brush? stroke, Brush? fill, double max, double w, double h)
    {
        if (values is null || values.Count < 2 || max <= 0)
        {
            return;
        }

        int capacity = Math.Max(Capacity, values.Count);
        double step = w / (capacity - 1);
        double startX = w - (values.Count - 1) * step;

        Point PointAt(int i) => new(startX + i * step, h - 1 - Math.Clamp(values[i] / max, 0, 1) * (h - 2));

        if (fill is not null)
        {
            var area = new StreamGeometry();
            using (StreamGeometryContext ctx = area.Open())
            {
                ctx.BeginFigure(new Point(startX, h), isFilled: true, isClosed: true);
                for (int i = 0; i < values.Count; i++)
                {
                    ctx.LineTo(PointAt(i), isStroked: false, isSmoothJoin: false);
                }

                ctx.LineTo(new Point(w, h), isStroked: false, isSmoothJoin: false);
            }

            area.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        var line = new StreamGeometry();
        using (StreamGeometryContext ctx = line.Open())
        {
            ctx.BeginFigure(PointAt(0), isFilled: false, isClosed: false);
            for (int i = 1; i < values.Count; i++)
            {
                ctx.LineTo(PointAt(i), isStroked: true, isSmoothJoin: true);
            }
        }

        line.Freeze();
        var pen = new Pen(stroke ?? Brushes.DeepSkyBlue, 1.5) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    private static double MaxOf(IReadOnlyList<double>? values)
    {
        double max = 0;
        if (values is not null)
        {
            for (int i = 0; i < values.Count; i++)
            {
                max = Math.Max(max, values[i]);
            }
        }

        return max;
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(SparklineChart),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}
