using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Bobar.App.ViewModels;
using Bobar.Core;
using Bobar.Core.Model;

namespace Bobar.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ApplyBackdrop();
        WallpaperBackdrop.IsActiveChanged += OnWallpaperActive;
        // The client area runs under the title bar: keep clear of the system's own buttons.
        if (OperatingSystem.IsMacOS()) { SidebarTitle.Margin = new Thickness(80, 0, 0, 0); TitleToolbar.Margin = new Thickness(4, 0, 12, 0); }
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.DetailsRequested += OpenDetails;
                vm.SettingsRequested += svm => _ = new SettingsWindow { DataContext = svm }.ShowDialog(this);
                vm.ReviewersRequested += rvm => _ = new ReviewersWindow { DataContext = rvm }.ShowDialog(this);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.Zoom)) ApplyZoom();
                    if (e.PropertyName == nameof(MainViewModel.ShownBackdrop)) ApplyBackdrop();
                };
                ApplyZoom();
                ApplyBackdrop();
            }
        };
        Closed += (_, _) =>
        {
            WallpaperBackdrop.IsActiveChanged -= OnWallpaperActive;
            (DataContext as IDisposable)?.Dispose();
        };

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

    private void OnWallpaperActive(object? sender, EventArgs e) => ApplyBackdrop();

    /// <summary>Windows: the blurred wallpaper (with its own tints) once it is known, else Mica or Acrylic under the
    /// panes' tints. macOS: vibrancy; elsewhere TransparencyBackgroundFallback paints under them.</summary>
    private void ApplyBackdrop()
    {
        var pick = OperatingSystem.IsWindows() ? (DataContext as MainViewModel)?.ShownBackdrop ?? GuiBackdrop.Wallpaper : GuiBackdrop.Mica;
        Backdrop.IsVisible = MainBackdrop.IsVisible = pick == GuiBackdrop.Wallpaper;
        var wallpaper = Backdrop.IsVisible && WallpaperBackdrop.IsActive;
        SidebarTint.IsVisible = MainTint.IsVisible = !wallpaper;
        TransparencyLevelHint = wallpaper ? [WindowTransparencyLevel.None]
            : pick == GuiBackdrop.Acrylic ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur]
            : [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
    }

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

    private void OpenDetails(RunPaths run)
    {
        var w = new DetailsWindow { DataContext = new DetailsViewModel(run) };
        _ = w.ShowDialog(this);
    }
}
