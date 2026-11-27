using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Win32;

namespace Bobar.App.Views;

/// <summary>Windows: the blurred wallpaper under this control's spot on the screen, then <see cref="Tint"/>. Acrylic's
/// look, but sampled from the wallpaper like Mica, so the windows behind don't show through. Draws nothing (and stays
/// <see cref="IsActive"/> false) until the wallpaper is known, or anywhere but Windows. Every backdrop shares one
/// wallpaper build (<see cref="DesktopWallpaper"/>), at each <see cref="BlurLevel"/>.
///
/// The app draws a frame or two after Windows moves the window, so following a drag would make the backdrop trail and
/// shimmer. Instead it holds still while you drag or resize (it moves with the window) and crossfades to the wallpaper
/// under the new spot when you let go.</summary>
public sealed class WallpaperBackdrop : Control
{
    public static readonly StyledProperty<IBrush?> TintProperty = AvaloniaProperty.Register<WallpaperBackdrop, IBrush?>(nameof(Tint));
    public static readonly StyledProperty<int> BlurLevelProperty = AvaloniaProperty.Register<WallpaperBackdrop, int>(nameof(BlurLevel));

    public IBrush? Tint { get => GetValue(TintProperty); set => SetValue(TintProperty, value); }

    /// <summary>0 is acrylic-like; level n blurs n+1 times as much (up to <see cref="DesktopWallpaper.BlurLevels"/> - 1).</summary>
    public int BlurLevel { get => GetValue(BlurLevelProperty); set => SetValue(BlurLevelProperty, value); }

    /// <summary>True once the wallpaper is known; the last good one is kept even if a later refresh fails.</summary>
    public static bool IsActive => _cache is not null;

    /// <summary>Raised once, when the wallpaper first becomes known.</summary>
    public static event EventHandler? IsActiveChanged;

    static WallpaperBackdrop() => AffectsRender<WallpaperBackdrop>(TintProperty, BlurLevelProperty);

    public WallpaperBackdrop() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);

    // ---- The shared wallpaper, on the UI thread ----

    private sealed record Cache(Bitmap[] Bitmaps, DesktopWallpaper.Snapshot Snapshot);

    private static Cache? _cache;
    private static bool _refreshing, _again;
    /// <summary>Backdrops to redraw when the wallpaper changes.</summary>
    private static readonly HashSet<WallpaperBackdrop> Attached = [];

    /// <summary>Rebuilds off the UI thread when the wallpaper or the monitors changed; one at a time, coalesced.</summary>
    private static void Refresh()
    {
        if (!OperatingSystem.IsWindows() || !Attached.Any(b => b.IsVisible)) return;
        if (_refreshing) { _again = true; return; }
        _refreshing = true;
        var old = _cache?.Snapshot.Key;
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

    private static void Apply(DesktopWallpaper.Snapshot snapshot)
    {
        var wasActive = IsActive;
        // Copies the pixels. 96 dpi, so the bitmaps' units are their pixels.
        var bitmaps = snapshot.Blurs.Select(pixels =>
        {
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(), snapshot.Size,
                                  new Vector(96, 96), snapshot.Size.Width * 4);
            }
            finally { handle.Free(); }
        }).ToArray();
        if (_cache is not null) foreach (var b in _cache.Bitmaps) b.Dispose();
        _cache = new Cache(bitmaps, snapshot with { Blurs = [] });
        foreach (var b in Attached) b.InvalidateVisual();
        if (!wasActive) IsActiveChanged?.Invoke(null, EventArgs.Empty);
    }

    // ---- This backdrop ----

    private Window? _window;
    /// <summary>The screen point the crop is held at during a drag or resize.</summary>
    private PixelPoint? _held;
    /// <summary>After a drag: the crop it held, fading out over <see cref="FadeTime"/> from <see cref="_fadeStart"/>.</summary>
    private PixelPoint? _fadeFrom;
    private long _fadeStart;
    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(250);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!OperatingSystem.IsWindows() || TopLevel.GetTopLevel(this) is not Window window) return;
        _window = window;
        Attached.Add(this);
        window.PositionChanged += OnMoved;
        // Not every wallpaper change is broadcast (e.g. a slideshow's): coming back to the window re-checks, cheaply.
        window.Activated += OnActivated;
        Win32Properties.AddWndProcHookCallback(window, WndProc);
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible) Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is null) return;
        Attached.Remove(this);
        _window.PositionChanged -= OnMoved;
        _window.Activated -= OnActivated;
        Win32Properties.RemoveWndProcHookCallback(_window, WndProc);
        _window = null;
    }

    private void OnMoved(object? sender, PixelPointEventArgs e) => InvalidateVisual();

    private void OnActivated(object? sender, EventArgs e) => Refresh();

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const uint WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E, SPI_SETDESKWALLPAPER = 0x0014,
                   WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
        switch (msg)
        {
            case WM_SETTINGCHANGE when wParam == (nint)SPI_SETDESKWALLPAPER:
            case WM_DISPLAYCHANGE:
                Refresh();
                break;
            case WM_ENTERSIZEMOVE:
                _fadeFrom = null;
                _held = Origin();
                break;
            case WM_EXITSIZEMOVE when _held is { } held:
                _held = null;
                if (held != Origin())
                {
                    _fadeFrom = held;
                    _fadeStart = Stopwatch.GetTimestamp();
                    TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFadeFrame);
                }
                InvalidateVisual();
                break;
        }
        return IntPtr.Zero;
    }

    private PixelPoint Origin() => this.PointToScreen(default);

    private void OnFadeFrame(TimeSpan _)
    {
        if (_fadeFrom is null) return;
        if (Stopwatch.GetElapsedTime(_fadeStart) >= FadeTime) _fadeFrom = null;
        else TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFadeFrame);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_cache is not { } cache || TopLevel.GetTopLevel(this) is not { } top) return;
        var bitmap = cache.Bitmaps[Math.Clamp(BlurLevel, 0, cache.Bitmaps.Length - 1)];
        var bounds = new Rect(Bounds.Size);
        var scaling = top.RenderScaling;
        if (_fadeFrom is { } from)
        {
            var t = Math.Clamp(Stopwatch.GetElapsedTime(_fadeStart) / FadeTime, 0, 1);
            DrawCrop(context, bitmap, cache.Snapshot, from, scaling, bounds);
            using (context.PushOpacity(t * t * (3 - 2 * t))) DrawCrop(context, bitmap, cache.Snapshot, Origin(), scaling, bounds);
        }
        else DrawCrop(context, bitmap, cache.Snapshot, _held ?? Origin(), scaling, bounds);
        if (Tint is { } tint) context.FillRectangle(tint, bounds);
    }

    /// <summary>The blurred wallpaper under <paramref name="origin"/> (a screen point), filling <paramref name="bounds"/>.</summary>
    private static void DrawCrop(DrawingContext context, Bitmap bitmap, DesktopWallpaper.Snapshot s, PixelPoint origin, double scaling, Rect bounds)
    {
        // Composite pixels per DIP of this control.
        var k = scaling * s.Scale;
        var source = new Rect((origin.X - s.Desktop.X) * s.Scale, (origin.Y - s.Desktop.Y) * s.Scale, bounds.Width * k, bounds.Height * k);
        var visible = source.Intersect(new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height));
        if (visible.Width < source.Width || visible.Height < source.Height)
            context.FillRectangle(new SolidColorBrush(Color.FromUInt32(s.Background)), bounds); // partly off the desktop
        if (visible.Width > 0 && visible.Height > 0)
            context.DrawImage(bitmap, visible, new Rect((visible.X - source.X) / k, (visible.Y - source.Y) / k, visible.Width / k, visible.Height / k));
    }
}
