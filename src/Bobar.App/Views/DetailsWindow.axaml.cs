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

    /// <summary>Follows the tail only if the user was already at the bottom before new content arrived
    /// (content shorter than the viewport counts as the bottom). Checked before the refresh, since appending
    /// moves the end away. Expanding an entry doesn't go through here, so it never yanks the view.</summary>
    private void Tick()
    {
        if (DataContext is not DetailsViewModel vm) return;
        var scroll = this.FindControl<ScrollViewer>("Scroll");
        var atBottom = scroll is null || scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 4;
        if (!vm.Refresh() || !atBottom) return;
        // Loaded runs after layout (so the new extent is known) but before input, so no scroll or expand click can slip in between.
        Dispatcher.UIThread.Post(() => scroll?.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private async void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DetailsViewModel vm || Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(vm.PlainText()); }
        catch (Exception) { } // the clipboard can be locked by another process; nothing to do but let the user retry
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
