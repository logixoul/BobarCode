using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using SkiaSharp;

namespace Bobar.App.Views;

/// <summary>The desktop's wallpaper, laid out over the whole virtual screen the way Windows lays it out, small and
/// pre-blurred: an acrylic look whose source is the wallpaper (like Mica) rather than the windows behind. Built once per
/// wallpaper/monitor change; moving the window is then only a crop of it (<see cref="WallpaperBackdrop"/>).</summary>
public static class DesktopWallpaper
{
    /// <summary>The composite is this many times smaller than the screen: it gets blurred anyway, so it's cheap to keep.</summary>
    private const int DownScale = 4;
    /// <summary>The blur's sigma in screen pixels: acrylic-like, much tighter than Mica's.</summary>
    private const float BlurSigma = 40;
    /// <summary>Blurring averages colours toward grey; a little extra saturation keeps the result vivid.</summary>
    private const float Saturation = 1.3f;

    /// <param name="Pixels">BGRA premultiplied, <see cref="Size"/> pixels, <c>Size.Width * 4</c> bytes per row.</param>
    /// <param name="Desktop">The virtual screen the composite covers, in physical screen pixels.</param>
    /// <param name="Scale">Composite pixels per screen pixel.</param>
    /// <param name="Background">The desktop's background colour (shows where the wallpaper doesn't reach), as ARGB.</param>
    /// <param name="Key">Identifies the inputs; equal keys mean an identical composite.</param>
    public sealed record Snapshot(byte[] Pixels, PixelSize Size, PixelRect Desktop, double Scale, uint Background, string Key);

    /// <summary>What the composite would be built from, cheaply: compare with <see cref="Snapshot.Key"/> before building.</summary>
    [SupportedOSPlatform("windows")]
    public static string CurrentKey() => Describe(Read());

    /// <summary>Null if Windows won't say what the wallpaper is (or there are no monitors).</summary>
    [SupportedOSPlatform("windows")]
    public static Snapshot? Build()
    {
        var layout = Read();
        if (layout is null || layout.Monitors.Count == 0) return null;
        var desktop = layout.Monitors.Select(m => m.Rect).Aggregate((a, b) => a.Union(b));
        const double scale = 1.0 / DownScale;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Ceiling(desktop.Width * scale)), Math.Max(1, (int)Math.Ceiling(desktop.Height * scale)),
                                   SKColorType.Bgra8888, SKAlphaType.Premul);
        var background = new SKColor(0xFF000000 | layout.Background);

        using var composite = new SKBitmap(info);
        using (var canvas = new SKCanvas(composite))
        {
            canvas.Clear(background);
            canvas.Scale((float)scale);
            canvas.Translate(-desktop.X, -desktop.Y);
            var images = new Dictionary<string, SKImage?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var m in layout.Monitors)
                {
                    if (!images.TryGetValue(m.Path, out var image)) images[m.Path] = image = Load(m.Path);
                    if (image is null) continue;
                    canvas.Save();
                    canvas.ClipRect(ToSk(m.Rect));
                    // Span: one image filled across every monitor's bounding box.
                    Place(canvas, image, layout.Position == Position.Span ? desktop : m.Rect, layout.Position);
                    canvas.Restore();
                }
            }
            finally
            {
                foreach (var image in images.Values) image?.Dispose();
            }
        }

        using var blurred = new SKBitmap(info);
        using (var canvas = new SKCanvas(blurred))
        using (var source = SKImage.FromBitmap(composite))
        using (var blur = SKImageFilter.CreateBlur(BlurSigma * (float)scale, BlurSigma * (float)scale, SKShaderTileMode.Clamp))
        using (var saturate = SKColorFilter.CreateColorMatrix(SaturationMatrix(Saturation)))
        using (var paint = new SKPaint { ImageFilter = blur, ColorFilter = saturate })
        {
            canvas.Clear(background);
            canvas.DrawImage(source, 0, 0, paint);
        }
        var pixels = new byte[info.BytesSize];
        Marshal.Copy(blurred.GetPixels(), pixels, 0, pixels.Length);
        return new Snapshot(pixels, new PixelSize(info.Width, info.Height), desktop, scale, 0xFF000000 | layout.Background, Describe(layout));
    }

    private static SKImage? Load(string path)
    {
        try
        {
            if (path.Length == 0 || !File.Exists(path)) return null;
            using var data = SKData.Create(path);
            return data is null ? null : SKImage.FromEncodedData(data)?.ToRasterImage(ensurePixelData: true);
        }
        catch (Exception) { return null; } // an unreadable or unsupported file: that monitor shows the background colour
    }

    private static void Place(SKCanvas canvas, SKImage image, PixelRect area, Position position)
    {
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        float w = image.Width, h = image.Height;
        var r = ToSk(area);
        switch (position)
        {
            case Position.Tile:
                using (var shader = SKShader.CreateImage(image, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, sampling,
                                                         SKMatrix.CreateTranslation(r.Left, r.Top)))
                using (var paint = new SKPaint { Shader = shader })
                    canvas.DrawRect(r, paint);
                return;
            case Position.Stretch:
                canvas.DrawImage(image, r, sampling);
                return;
            default:
                var k = position switch
                {
                    Position.Center => 1f,
                    Position.Fit => Math.Min(r.Width / w, r.Height / h),
                    _ => Math.Max(r.Width / w, r.Height / h), // Fill, Span
                };
                var dest = SKRect.Create(r.MidX - w * k / 2, r.MidY - h * k / 2, w * k, h * k);
                canvas.DrawImage(image, dest, sampling);
                return;
        }
    }

    private static float[] SaturationMatrix(float s)
    {
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float r = (1 - s) * lr, g = (1 - s) * lg, b = (1 - s) * lb;
        return
        [
            r + s, g, b, 0, 0,
            r, g + s, b, 0, 0,
            r, g, b + s, 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    private static SKRect ToSk(PixelRect r) => SKRect.Create(r.X, r.Y, r.Width, r.Height);

    // ---- What Windows says the desktop looks like ----

    private enum Position { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }

    private sealed record Monitor(PixelRect Rect, string Path);

    private sealed record Layout(IReadOnlyList<Monitor> Monitors, Position Position, uint Background);

    /// <summary>Includes each file's write time: Windows rewrites TranscodedWallpaper in place.</summary>
    private static string Describe(Layout? layout)
    {
        if (layout is null) return "";
        var sb = new StringBuilder().Append(layout.Position).Append('|').Append(layout.Background);
        foreach (var m in layout.Monitors)
        {
            var stamp = m.Path.Length > 0 && File.Exists(m.Path) ? File.GetLastWriteTimeUtc(m.Path).Ticks : 0;
            sb.Append('|').Append(m.Rect).Append(';').Append(m.Path).Append(';').Append(stamp);
        }
        return sb.ToString();
    }

    [SupportedOSPlatform("windows")]
    private static Layout? Read()
    {
        IDesktopWallpaper? dw = null;
        try
        {
            dw = (IDesktopWallpaper)new DesktopWallpaperCoClass();
            var monitors = new List<Monitor>();
            var count = dw.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    var id = dw.GetMonitorDevicePathAt(i);
                    var rc = dw.GetMonitorRECT(id); // throws for a monitor that's attached but not part of the desktop
                    if (rc.Right <= rc.Left || rc.Bottom <= rc.Top) continue;
                    var path = dw.GetWallpaper(id) ?? "";
                    // Empty for e.g. Spotlight; Windows keeps the picture it actually shows here.
                    if (path.Length == 0 || !File.Exists(path)) path = Transcoded;
                    monitors.Add(new Monitor(new PixelRect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top), path));
                }
                catch (COMException) { }
            }
            var position = dw.GetPosition();
            // COLORREF is 0x00BBGGRR; we want 0x00RRGGBB.
            var c = dw.GetBackgroundColor();
            var background = ((c & 0xFF) << 16) | (c & 0xFF00) | ((c >> 16) & 0xFF);
            return new Layout(monitors, Enum.IsDefined(position) ? position : Position.Fill, background);
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return null; }
        finally
        {
            if (dw is not null) Marshal.ReleaseComObject(dw);
        }
    }

    private static string Transcoded => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperCoClass;

    /// <summary>shobjidl_core.h; only the methods up to GetPosition, in vtable order.</summary>
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string? GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
        uint GetMonitorDevicePathCount();
        Rect GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(Position position);
        Position GetPosition();
    }
}
