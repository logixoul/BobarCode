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
            if (DataContext is MainViewModel vm)
            {
                vm.DetailsRequested += OpenDetails;
                vm.SettingsRequested += svm => _ = new SettingsWindow { DataContext = svm }.ShowDialog(this);
                vm.ReviewersRequested += rvm => _ = new ReviewersWindow { DataContext = rvm }.ShowDialog(this);
            }
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

    private async void OnCopyExchange(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not RunDetailViewModel vm || Clipboard is not { } clipboard) return;
        const string failed = "Could not copy: another program is holding the clipboard. Try again.";
        try
        {
            await clipboard.SetTextAsync(vm.ExchangeText());
            if (vm.Notice == failed) vm.Notice = "";
        }
        catch (Exception) { vm.Notice = failed; }
    }

    private void OpenDetails(RunPaths run)
    {
        var w = new DetailsWindow { DataContext = new DetailsViewModel(run) };
        _ = w.ShowDialog(this);
    }
}
