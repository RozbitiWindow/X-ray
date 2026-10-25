namespace XRay.Core.Treemap;

/// <summary>An axis-aligned rectangle in device-independent units.</summary>
public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double Area => Width * Height;

    public bool Contains(double px, double py) => px >= X && px < Right && py >= Y && py < Bottom;

    /// <summary>Shrinks the rectangle on all sides; never produces negative sizes.</summary>
    public TreemapRect Deflate(double left, double top, double right, double bottom)
    {
        double w = Math.Max(0, Width - left - right);
        double h = Math.Max(0, Height - top - bottom);
        return new TreemapRect(X + Math.Min(left, Width), Y + Math.Min(top, Height), w, h);
    }
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing and van Wijk, 2000). Produces rectangles whose areas are
/// proportional to the input weights while keeping aspect ratios close to 1.
/// </summary>
public static class SquarifiedLayout
{
    /// <summary>
    /// Lays out <paramref name="weights"/> inside <paramref name="bounds"/>.
    /// Weights should be sorted in descending order for best results; non-positive weights get empty rectangles.
    /// The returned array is index-aligned with <paramref name="weights"/>.
    /// </summary>
    public static TreemapRect[] Layout(IReadOnlyList<double> weights, TreemapRect bounds)
    {
        ArgumentNullException.ThrowIfNull(weights);
        int n = weights.Count;
        var result = new TreemapRect[n];
        if (n == 0)
        {
            return result;
        }

        double total = 0;
        for (int i = 0; i < n; i++)
        {
            if (weights[i] > 0)
            {
                total += weights[i];
            }
        }

        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            for (int i = 0; i < n; i++)
            {
                result[i] = new TreemapRect(bounds.X, bounds.Y, 0, 0);
            }

            return result;
        }

        // Convert weights into target areas.
        double scale = bounds.Area / total;
        var areas = new double[n];
        for (int i = 0; i < n; i++)
        {
            areas[i] = weights[i] > 0 ? weights[i] * scale : 0;
        }

        double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
        int start = 0;

        // Skip leading zero weights (they still receive empty rectangles at the end).
        while (start < n)
        {
            if (areas[start] <= 0)
            {
                result[start] = new TreemapRect(x, y, 0, 0);
                start++;
                continue;
            }

            double shortSide = Math.Min(w, h);
            double rowSum = areas[start];
            double rowMin = rowSum, rowMax = rowSum;
            double worst = Worst(rowSum, rowMin, rowMax, shortSide);
            int end = start + 1;

            while (end < n && areas[end] > 0)
            {
                double a = areas[end];
                double newSum = rowSum + a;
                double newMin = Math.Min(rowMin, a);
                double newMax = Math.Max(rowMax, a);
                double newWorst = Worst(newSum, newMin, newMax, shortSide);
                if (newWorst > worst)
                {
                    break;
                }

                rowSum = newSum;
                rowMin = newMin;
                rowMax = newMax;
                worst = newWorst;
                end++;
            }

            bool isLastRow = end >= n || AllZero(areas, end);
            if (w >= h)
            {
                // Place the row as a column along the left edge.
                double colWidth = isLastRow ? w : Math.Min(w, rowSum / h);
                double cy = y;
                for (int i = start; i < end; i++)
                {
                    double ih = i == end - 1 ? (y + h) - cy : areas[i] / colWidth;
                    result[i] = new TreemapRect(x, cy, colWidth, Math.Max(0, ih));
                    cy += ih;
                }

                x += colWidth;
                w -= colWidth;
            }
            else
            {
                // Place the row along the top edge.
                double rowHeight = isLastRow ? h : Math.Min(h, rowSum / w);
                double cx = x;
                for (int i = start; i < end; i++)
                {
                    double iw = i == end - 1 ? (x + w) - cx : areas[i] / rowHeight;
                    result[i] = new TreemapRect(cx, y, Math.Max(0, iw), rowHeight);
                    cx += iw;
                }

                y += rowHeight;
                h -= rowHeight;
            }

            start = end;
            w = Math.Max(0, w);
            h = Math.Max(0, h);
        }

        return result;
    }

    /// <summary>The worst (largest) aspect ratio in a row of total area <paramref name="sum"/> along a side of length <paramref name="side"/>.</summary>
    private static double Worst(double sum, double min, double max, double side)
    {
        if (sum <= 0 || min <= 0 || side <= 0)
        {
            return double.MaxValue;
        }

        double s2 = sum * sum;
        double w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private static bool AllZero(double[] areas, int from)
    {
        for (int i = from; i < areas.Length; i++)
        {
            if (areas[i] > 0)
            {
                return false;
            }
        }

        return true;
    }
}
