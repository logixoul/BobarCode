using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bobar.App.ViewModels;

namespace Bobar.App.Views;

/// <summary>Raw reviewer activity per round, re-read every second while open (tailing live).</summary>
public partial class DetailsWindow : Window
{
    private readonly DispatcherTimer _timer;

    public DetailsWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        Opened += (_, _) =>
        {
            Tick();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Tick()
    {
        if (DataContext is not DetailsViewModel vm || !vm.Refresh()) return;
        if (this.FindControl<CheckBox>("Follow")?.IsChecked == true)
            Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("Scroll")?.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
