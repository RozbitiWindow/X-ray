using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XRay.App.ViewModels;

namespace XRay.App.Views
{
    public partial class OverviewView : UserControl
    {
        public OverviewView() => InitializeComponent();
    }

    public partial class PerformanceView : UserControl
    {
        public PerformanceView() => InitializeComponent();
    }

    public partial class DuplicatesView : UserControl
    {
        public DuplicatesView() => InitializeComponent();
    }

    public partial class HistoryView : UserControl
    {
        public HistoryView() => InitializeComponent();
    }

    public partial class SettingsView : UserControl
    {
        public SettingsView() => InitializeComponent();
    }

    public partial class LargeFilesView : UserControl
    {
        public LargeFilesView() => InitializeComponent();

        private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(ResultsGrid, source) is DataGridRow { Item: LargeFileRow row } &&
                DataContext is LargeFilesViewModel vm)
            {
                vm.RevealCommand.Execute(row);
            }
        }
    }
}
