using System.Windows.Controls;
using System.Windows.Input;
using XRay.App.ViewModels;

namespace XRay.App.Views;

public partial class DiskAnalyzerView : UserControl
{
    public DiskAnalyzerView() => InitializeComponent();

    private DiskAnalyzerViewModel? ViewModel => DataContext as DiskAnalyzerViewModel;

    // Pure view plumbing: forward double-click / Enter on a row to the view model's command.
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.DependencyObject source &&
            ItemsControl.ContainerFromElement(ItemsGrid, source) is DataGridRow { Item: NodeRow row })
        {
            ViewModel?.OpenRowCommand.Execute(row);
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ItemsGrid.SelectedItem is NodeRow row)
        {
            ViewModel?.OpenRowCommand.Execute(row);
            e.Handled = true;
        }
    }
}
