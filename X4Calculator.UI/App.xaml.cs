using System.Windows;
using X4Calculator.UI.ViewModels;

namespace X4Calculator.UI;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        var viewModel = new MainViewModel();
        mainWindow.DataContext = viewModel;
        mainWindow.Show();

        await viewModel.InitializeAsync();
    }
}

