using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using XRay.Core.Settings;

namespace XRay.App.Services;

/// <summary>Swaps the colour dictionary and keeps the native title bar in sync with the theme.</summary>
public sealed class ThemeService
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    public AppTheme Current { get; private set; } = AppTheme.Dark;

    public void Apply(AppTheme theme)
    {
        Current = theme;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var uri = new Uri(theme == AppTheme.Light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        dictionaries[0] = new ResourceDictionary { Source = uri };

        foreach (Window window in Application.Current.Windows)
        {
            ApplyTitleBar(window);
        }
    }

    /// <summary>Colours the native caption to match the sidebar (Windows 11; silently ignored elsewhere).</summary>
    public void ApplyTitleBar(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int dark = Current == AppTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));

        if (Application.Current.TryFindResource("C.Sidebar") is Color caption)
        {
            int colorRef = caption.R | (caption.G << 8) | (caption.B << 16);
            DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref colorRef, sizeof(int));
        }

        if (Application.Current.TryFindResource("Text.Primary") is SolidColorBrush text)
        {
            int textRef = text.Color.R | (text.Color.G << 8) | (text.Color.B << 16);
            DwmSetWindowAttribute(hwnd, DwmTextColor, ref textRef, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
