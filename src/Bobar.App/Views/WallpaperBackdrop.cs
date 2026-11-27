using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Win32;

namespace Bobar.App.Views;

/// <summary>Windows: the blurred wallpaper under this control's spot on the screen, then <see cref="Tint"/>. Acrylic's
/// look, but sampled from the wallpaper like Mica, so the windows behind don't show through. Draws nothing (and stays
/// <see cref="IsActive"/> false) until the wallpaper is known, or anywhere but Windows.</summary>
public sealed class WallpaperBackdrop : Control
{
    public static readonly StyledProperty<IBrush?> TintProperty = AvaloniaProperty.Register<WallpaperBackdrop, IBrush?>(nameof(Tint));

    public IBrush? Tint { get => GetValue(TintProperty); set => SetValue(TintProperty, value); }

    /// <summary>True once it draws the wallpaper; it then keeps the last good one even if a later refresh fails.</summary>
    public bool IsActive => _bitmap is not null;

    public event EventHandler? IsActiveChanged;

    static WallpaperBackdrop() => AffectsRender<WallpaperBackdrop>(TintProperty);

    public WallpaperBackdrop() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);

    private Window? _window;
    private Bitmap? _bitmap;
    private DesktopWallpaper.Snapshot? _snapshot;
    private bool _refreshing, _again;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!OperatingSystem.IsWindows() || TopLevel.GetTopLevel(this) is not Window window) return;
        _window = window;
        window.PositionChanged += OnMoved;
        // Not every wallpaper change is broadcast (e.g. a slideshow's): coming back to the window re-checks, cheaply.
        window.Activated += OnActivated;
        Win32Properties.AddWndProcHookCallback(window, WndProc);
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is null) return;
        _window.PositionChanged -= OnMoved;
        _window.Activated -= OnActivated;
        Win32Properties.RemoveWndProcHookCallback(_window, WndProc);
        _window = null;
    }

    private void OnMoved(object? sender, PixelPointEventArgs e) => InvalidateVisual();

    private void OnActivated(object? sender, EventArgs e) => Refresh();

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const uint WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E, SPI_SETDESKWALLPAPER = 0x0014;
        if ((msg == WM_SETTINGCHANGE && wParam == (nint)SPI_SETDESKWALLPAPER) || msg == WM_DISPLAYCHANGE) Refresh();
        return IntPtr.Zero;
    }

    /// <summary>Rebuilds off the UI thread when the wallpaper or the monitors changed; one at a time, coalesced.</summary>
    private void Refresh()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_refreshing) { _again = true; return; }
        _refreshing = true;
        var old = _snapshot?.Key;
        Task.Run(() =>
        {
            try { return !OperatingSystem.IsWindows() || DesktopWallpaper.CurrentKey() == old ? null : DesktopWallpaper.Build(); }
            catch (Exception) { return null; } // keep what we have: a broken wallpaper file isn't worth a crash
        }).ContinueWith(t =>
        {
            _refreshing = false;
            if (t.Result is { } snapshot) Apply(snapshot);
            if (_again) { _again = false; Refresh(); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Apply(DesktopWallpaper.Snapshot snapshot)
    {
        var wasActive = IsActive;
        var handle = GCHandle.Alloc(snapshot.Pixels, GCHandleType.Pinned);
        try
        {
            // Copies the pixels. 96 dpi, so the bitmap's units are its pixels.
            var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(), snapshot.Size,
                                    new Vector(96, 96), snapshot.Size.Width * 4);
            _bitmap?.Dispose();
            _bitmap = bitmap;
            _snapshot = snapshot with { Pixels = [] };
        }
        finally { handle.Free(); }
        InvalidateVisual();
        if (!wasActive) IsActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap is not { } bitmap || _snapshot is not { } s || TopLevel.GetTopLevel(this) is not { } top) return;
        var bounds = new Rect(Bounds.Size);
        // Composite pixels per DIP of this control.
        var k = top.RenderScaling * s.Scale;
        var origin = this.PointToScreen(default);
        var source = new Rect((origin.X - s.Desktop.X) * s.Scale, (origin.Y - s.Desktop.Y) * s.Scale, bounds.Width * k, bounds.Height * k);
        var visible = source.Intersect(new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height));
        if (visible.Width < source.Width || visible.Height < source.Height)
            context.FillRectangle(new SolidColorBrush(Color.FromUInt32(s.Background)), bounds); // partly off the desktop
        if (visible.Width > 0 && visible.Height > 0)
            context.DrawImage(bitmap, visible, new Rect((visible.X - source.X) / k, (visible.Y - source.Y) / k, visible.Width / k, visible.Height / k));
        if (Tint is { } tint) context.FillRectangle(tint, bounds);
    }
}
