using Avalonia.Controls;
using Avalonia.Input;
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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.DetailsRequested += OpenDetails;
                vm.SettingsRequested += svm => _ = new SettingsWindow { DataContext = svm }.ShowDialog(this);
                vm.ReviewersRequested += rvm => _ = new ReviewersWindow { DataContext = rvm }.ShowDialog(this);
                _zoom = Math.Clamp(vm.Zoom, MinZoom, MaxZoom);
                ApplyZoom();
            }
        };
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();

        // Zoom the run's body only. Tunnel, so a focused TextBox or the ScrollViewer doesn't take the keys/wheel first.
        AddHandler(KeyDownEvent, OnZoomKey, RoutingStrategies.Tunnel);
        DetailScroll.AddHandler(PointerWheelChangedEvent, OnZoomWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        DetailScroll.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) ApplyZoom(); };
    }

    private const double MinZoom = 0.5, MaxZoom = 3.0, ZoomStep = 0.1;
    /// <summary>The detail column's unzoomed width at the window's MinWidth: zoomed in past it, the body scrolls sideways instead of squeezing.</summary>
    private const double MinBodyWidth = 590;
    private double _zoom = 1.0;

    private static KeyModifiers CommandKey => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private void OnZoomKey(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & CommandKey) == 0) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: SetZoom(_zoom + ZoomStep); break;
            case Key.OemMinus or Key.Subtract: SetZoom(_zoom - ZoomStep); break;
            case Key.D0 or Key.NumPad0: SetZoom(1.0); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnZoomWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & CommandKey) == 0 || e.Delta.Y == 0) return;
        SetZoom(_zoom + Math.Sign(e.Delta.Y) * ZoomStep);
        e.Handled = true;
    }

    private void SetZoom(double z)
    {
        z = Math.Clamp(Math.Round(z, 1), MinZoom, MaxZoom);
        if (z == _zoom) return;
        _zoom = z;
        if (DataContext is MainViewModel vm) vm.Zoom = z;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        ZoomRoot.LayoutTransform = _zoom == 1.0 ? null : new ScaleTransform(_zoom, _zoom);
        // A finite width, so text wraps and * columns work; never narrower than the unzoomed minimum.
        ZoomRoot.Width = Math.Max(DetailScroll.Bounds.Width, MinBodyWidth * _zoom);
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
