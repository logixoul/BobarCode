using System.Diagnostics;
using Avalonia;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Bobar.App.ViewModels;
using Bobar.Core;

namespace Bobar.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Mica on Windows 11, vibrancy on macOS; elsewhere TransparencyBackgroundFallback paints under the sidebar.
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
        // The client area runs under the title bar: keep clear of the system's own buttons.
        if (OperatingSystem.IsMacOS()) { SidebarTitle.Margin = new Thickness(80, 0, 0, 0); TitleToolbar.Margin = new Thickness(4, 0, 12, 0); }
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.DetailsRequested += OpenDetails;
                vm.SettingsRequested += svm => _ = new SettingsWindow { DataContext = svm }.ShowDialog(this);
                vm.ReviewersRequested += rvm => _ = new ReviewersWindow { DataContext = rvm }.ShowDialog(this);
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Zoom)) ApplyZoom(); };
                ApplyZoom();
            }
        };
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();

        // Zoom the run's body only. Tunnel, so a focused TextBox or the ScrollViewer doesn't take the keys/wheel first.
        AddHandler(KeyDownEvent, OnZoomKey, RoutingStrategies.Tunnel);
        DetailScroll.AddHandler(PointerWheelChangedEvent, OnZoomWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        DetailScroll.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) ApplyZoom(); };

        // The discussion inspector's activity log: follow its end while you're there, else offer to jump to it.
        ActivityScroll.DataContextChanged += (_, _) =>
        {
            if (_discuss is not null) _discuss.ActivityChanged -= OnActivityChanged;
            _discuss = ActivityScroll.DataContext as DiscussViewModel;
            if (_discuss is not null) _discuss.ActivityChanged += OnActivityChanged;
        };
        ActivityScroll.ScrollChanged += (_, _) => JumpLatest.IsVisible = !AtBottom(ActivityScroll);
    }

    private DiscussViewModel? _discuss;

    /// <summary>Content shorter than the viewport counts as the bottom.</summary>
    private static bool AtBottom(ScrollViewer s) => s.Offset.Y >= s.Extent.Height - s.Viewport.Height - 4;

    /// <summary>Raised right after the entries changed, before layout, so the extent is still the old one: Follow scrolls
    /// only if you were at the bottom. Expanding an entry doesn't come through here, so it never yanks the view.</summary>
    private void OnActivityChanged(ActivityScroll how)
    {
        var scroll = ActivityScroll;
        if (how == ViewModels.ActivityScroll.Follow && !AtBottom(scroll)) return;
        // Loaded runs after layout (so the new extent is known) but before input.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (how == ViewModels.ActivityScroll.Top) scroll.ScrollToHome();
            else scroll.ScrollToEnd();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnJumpLatest(object? sender, RoutedEventArgs e) => ActivityScroll.ScrollToEnd();

    private async void OnCopyActivity(object? sender, RoutedEventArgs e)
    {
        if (_discuss is null || Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(_discuss.ActivityText()); }
        catch (Exception) { } // the clipboard can be locked by another process; nothing to do but let the user retry
    }

    /// <summary>At most one wheel step per interval: see <see cref="OnZoomWheel"/>.</summary>
    private static readonly TimeSpan WheelInterval = TimeSpan.FromMilliseconds(80);
    private long _lastWheelStep;

    private static KeyModifiers CommandKey => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>The level lives in the view model; the view only turns input into steps and applies it.</summary>
    private void StepZoom(double delta)
    {
        if (DataContext is MainViewModel vm) vm.Zoom = delta == 0 ? 1.0 : vm.Zoom + delta;
    }

    private void OnZoomKey(object? sender, KeyEventArgs e)
    {
        // Extra modifiers are allowed on purpose: Ctrl+Shift+= is how a US keyboard types Ctrl++.
        if ((e.KeyModifiers & CommandKey) == 0) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: StepZoom(MainViewModel.ZoomStep); break;
            case Key.OemMinus or Key.Subtract: StepZoom(-MainViewModel.ZoomStep); break;
            case Key.D0 or Key.NumPad0: StepZoom(0); break;
            default: return;
        }
        // Handled even at a limit: these chords belong to zoom, nothing else in the window wants them.
        e.Handled = true;
    }

    private void OnZoomWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & CommandKey) == 0 || e.Delta.Y == 0) return;
        // A modified wheel is a zoom gesture: swallow it even when no step is applied (throttled, or at a limit),
        // or the body scrolls while you zoom.
        e.Handled = true;
        // Deltas have no portable unit (Windows: 1 per notch; macOS: a fraction per notch, or many small ones per
        // trackpad gesture), so step by time instead of by delta: one step per mouse notch, a bounded zoom per swipe.
        if (Stopwatch.GetElapsedTime(_lastWheelStep) < WheelInterval) return;
        _lastWheelStep = Stopwatch.GetTimestamp();
        StepZoom(Math.Sign(e.Delta.Y) * MainViewModel.ZoomStep);
    }

    private void ApplyZoom()
    {
        var z = (DataContext as MainViewModel)?.Zoom ?? 1.0;
        ZoomRoot.LayoutTransform = z == 1.0 ? null : new ScaleTransform(z, z);
        // A finite width, so text wraps and * columns work (the ScrollViewer would measure at infinite width).
        // The LTC lays its child out at Width / z, so the body is max(viewport / z, MinWidth) wide unscaled: it fills
        // the viewport at any zoom and scrolls sideways rather than squeezing below its minimum. Bounds is the usable
        // width because Fluent's scrollbars overlay the content.
        ZoomRoot.Width = Math.Max(DetailScroll.Bounds.Width, ZoomContent.MinWidth * z);
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

    private void OnDeleteRun(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RunItemViewModel item && DataContext is MainViewModel vm) vm.DeleteRun(item);
    }

    /// <summary>A card's history menu: its earlier days under This week / Last week / Earlier, each opening its runs.
    /// Built on each click, so it's never stale.</summary>
    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button || button.DataContext is not ProjectCardViewModel card || DataContext is not MainViewModel vm) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        string? group = null;
        foreach (var day in card.OlderByDay())
        {
            if (day.Group != group)
            {
                if (group is not null) menu.Items.Add(new Separator());
                group = day.Group;
                menu.Items.Add(new MenuItem { Header = group, IsEnabled = false, FontSize = 11.5 });
            }
            var dayItem = new MenuItem { Header = Spread(new TextBlock { Text = day.Label }, Muted(day.Runs.Count.ToString(CultureInfo.InvariantCulture)), 150) };
            foreach (var run in day.Runs)
            {
                var title = new TextBlock { Text = run.Name, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis };
                var dot = new Ellipse { Width = 7, Height = 7, Fill = run.BadgeBrush, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                var runItem = new MenuItem
                {
                    Header = Spread(new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, title } }, Muted(run.StartTime), 220),
                };
                ToolTip.SetTip(runItem, run.Tip);
                runItem.Click += (_, _) => vm.SelectedRun = run;
                dayItem.Items.Add(runItem);
            }
            menu.Items.Add(dayItem);
        }
        menu.ShowAt(button);
    }

    /// <summary>A label on the left and a muted note on the right, at least minWidth apart.</summary>
    private static Grid Spread(Control left, Control right, double minWidth)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinWidth = minWidth };
        right.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(right, 1);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    private static TextBlock Muted(string text) => new() { Text = text, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };

    private void OpenDetails(RunPaths run)
    {
        var w = new DetailsWindow { DataContext = new DetailsViewModel(run) };
        _ = w.ShowDialog(this);
    }
}
