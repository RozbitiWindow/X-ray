using System.Windows;

namespace XRay.App.Views;

/// <summary>Themed message and confirmation dialog.</summary>
public partial class DialogWindow : Window
{
    public DialogWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => (Application.Current as App)?.ApplyTitleBar(this);
    }

    public static bool Show(Window? owner, string title, string message, string? detail, string okText, string? cancelText, bool danger, string iconGlyph)
    {
        var dialog = new DialogWindow
        {
            Owner = owner is { IsVisible: true } ? owner : null,
        };
        if (dialog.Owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.IconText.Text = iconGlyph;
        dialog.OkButton.Content = okText;
        if (danger)
        {
            dialog.OkButton.Style = (Style)dialog.FindResource("Button.Danger");
            // Destructive actions must not be the default on Enter.
            dialog.OkButton.IsDefault = false;
            dialog.CancelButton.IsDefault = true;
        }

        if (cancelText is null)
        {
            dialog.CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.CancelButton.Content = cancelText;
        }

        if (!string.IsNullOrEmpty(detail))
        {
            dialog.DetailText.Text = detail;
            dialog.DetailBorder.Visibility = Visibility.Visible;
        }

        return dialog.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
