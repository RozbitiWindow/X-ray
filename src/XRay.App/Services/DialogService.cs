using System.Windows;
using Microsoft.Win32;
using XRay.App.Views;

namespace XRay.App.Services;

/// <summary>Folder pickers and themed message/confirmation dialogs.</summary>
public sealed class DialogService
{
    public string? PickFolder(string? initialFolder, string title = "Select a folder to scan")
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
        {
            dialog.InitialDirectory = initialFolder;
        }

        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }

    /// <summary>Asks for confirmation. Destructive confirmations default to Cancel.</summary>
    public bool Confirm(string title, string message, string confirmText, string? detail = null, bool destructive = false) =>
        DialogWindow.Show(Application.Current.MainWindow, title, message, detail, confirmText, "Cancel", destructive,
            destructive ? "" : "");

    public void ShowError(string title, string message, string? detail = null) =>
        DialogWindow.Show(Application.Current.MainWindow, title, message, detail, "OK", null, false, "");

    public void ShowInfo(string title, string message, string? detail = null) =>
        DialogWindow.Show(Application.Current.MainWindow, title, message, detail, "OK", null, false, "");
}
