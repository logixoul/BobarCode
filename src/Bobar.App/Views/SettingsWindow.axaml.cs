using Avalonia.Controls;
using Avalonia.Interactivity;
using Bobar.App.ViewModels;

namespace Bobar.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.Save()) Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SettingsViewModel)?.Closed();
        base.OnClosed(e);
    }
}
