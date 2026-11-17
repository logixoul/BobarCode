using Avalonia.Controls;
using Bobar.App.ViewModels;
using Bobar.Core;

namespace Bobar.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.DetailsRequested += OpenDetails;
        };
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    /// <summary>Brought to the front when a second launch (e.g. the CLI's `--run`) hands over.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        // Windows may refuse focus to a background process; a topmost flip still raises the window.
        Topmost = true;
        Topmost = false;
    }

    private void OpenDetails(RunPaths run)
    {
        var w = new DetailsWindow { DataContext = new DetailsViewModel(run) };
        _ = w.ShowDialog(this);
    }
}
