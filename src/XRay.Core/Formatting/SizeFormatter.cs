using System.Globalization;

namespace XRay.Core.Formatting;

/// <summary>How byte counts are converted into human-readable units.</summary>
public enum SizeUnitMode
{
    /// <summary>Powers of 1024 with IEC labels (KiB, MiB, GiB).</summary>
    Binary,

    /// <summary>Powers of 1000 with SI labels (kB, MB, GB).</summary>
    Decimal,

    /// <summary>Powers of 1024 labelled KB, MB, GB, as Windows Explorer does.</summary>
    Windows,
}

/// <summary>Formats byte counts as short, human-readable strings.</summary>
public static class SizeFormatter
{
    private static readonly string[] BinaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    private static readonly string[] DecimalUnits = ["B", "kB", "MB", "GB", "TB", "PB", "EB"];
    private static readonly string[] WindowsUnits = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>
    /// Formats <paramref name="bytes"/> using three significant digits, e.g. "1.53 GiB", "15.3 MiB", "153 KiB".
    /// Values below one kilo-unit are shown as whole bytes.
    /// </summary>
    public static string Format(long bytes, SizeUnitMode mode = SizeUnitMode.Binary)
    {
        if (bytes < 0)
        {
            // long.MinValue cannot be negated; clamp it to the nearest representable magnitude.
            long magnitude = bytes == long.MinValue ? long.MaxValue : -bytes;
            return "-" + Format(magnitude, mode);
        }

        double divisor = mode == SizeUnitMode.Decimal ? 1000d : 1024d;
        string[] units = mode switch
        {
            SizeUnitMode.Decimal => DecimalUnits,
            SizeUnitMode.Windows => WindowsUnits,
            _ => BinaryUnits,
        };

        if (bytes < divisor)
        {
            return bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";
        }

        double value = bytes;
        int unit = 0;
        while (value >= divisor && unit < units.Length - 1)
        {
            value /= divisor;
            unit++;
        }

        // Rounding can push e.g. 1023.6 KiB to "1024 KiB"; promote to the next unit instead.
        if (Math.Round(value, value < 10 ? 2 : value < 100 ? 1 : 0) >= divisor && unit < units.Length - 1)
        {
            value /= divisor;
            unit++;
        }

        string number = value switch
        {
            < 10 => value.ToString("0.00", CultureInfo.InvariantCulture),
            < 100 => value.ToString("0.0", CultureInfo.InvariantCulture),
            _ => value.ToString("0", CultureInfo.InvariantCulture),
        };
        return number + " " + units[unit];
    }

    /// <summary>Formats a size difference with an explicit sign, e.g. "+1.20 GiB", "-340 MiB", "0 B".</summary>
    public static string FormatDelta(long delta, SizeUnitMode mode = SizeUnitMode.Binary)
    {
        if (delta == 0)
        {
            return "0 B";
        }

        return delta > 0 ? "+" + Format(delta, mode) : Format(delta, mode);
    }

    /// <summary>Formats an exact byte count with digit grouping, e.g. "1,234,567 bytes".</summary>
    public static string FormatExact(long bytes) =>
        bytes.ToString("N0", CultureInfo.InvariantCulture) + (bytes == 1 ? " byte" : " bytes");

    /// <summary>Formats a per-second transfer rate, e.g. "12.5 MiB/s".</summary>
    public static string FormatRate(double bytesPerSecond, SizeUnitMode mode = SizeUnitMode.Binary) =>
        Format((long)Math.Max(0, bytesPerSecond), mode) + "/s";
}
