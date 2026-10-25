using System.Windows;
using XRay.App.Services;
using XRay.App.ViewModels;

namespace XRay.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel, ThemeService theme)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => theme.ApplyTitleBar(this);
    }
}
