using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using MicroCom.Runtime;

namespace Bobar.App.Views;

/// <summary>Windows 11: makes Avalonia's Mica the bare blurred wallpaper. Avalonia builds its Mica from the same brush
/// Windows' Mica starts from (the wallpaper, blurred by Windows), then blends it with an opaque grey that drains its
/// colour. We swap that result for the bare brush and draw our own tint over it in XAML. Windows composes the brush, so
/// it stays put on the screen while the window moves, with no lag.
///
/// This reaches into Avalonia's internals (the WinUI compositor's shared brushes), so it is checked step by step: if
/// anything isn't where we expect it (an Avalonia update, another composition mode, Windows 10), nothing changes and
/// the window keeps Avalonia's own Mica.</summary>
[SupportedOSPlatform("windows")]
public static class WallpaperBrush
{
    /// <summary>True once Avalonia's Mica is the bare wallpaper.</summary>
    public static bool IsInstalled { get; private set; }

    /// <summary>Call after Avalonia's platform is up and before any window is created (windows take the brush when
    /// they're made).</summary>
    public static void TryInstall()
    {
        try { IsInstalled = Install(); }
        catch (Exception) { IsInstalled = false; } // see the summary: anything unexpected leaves Avalonia's Mica alone
    }

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static bool Install()
    {
        if (Environment.OSVersion.Version.Build < 22000) return false;
        var win32 = typeof(Win32PlatformOptions).Assembly;
        // The WinUI compositor connection registers itself in Avalonia's service locator as the windows' surface factory.
        if (win32.GetType("Avalonia.Win32.IWindowsSurfaceFactory") is not { } factoryType
            || typeof(AvaloniaLocator).GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) is not { } locator)
            return false;
        var connection = locator.GetType().GetMethod("GetService", Instance, [typeof(Type)])?.Invoke(locator, [factoryType]);
        if (connection?.GetType().FullName != "Avalonia.Win32.WinRT.Composition.WinUiCompositorConnection") return false;
        var shared = connection.GetType().GetField("_shared", Instance)?.GetValue(connection);
        if (shared?.GetType().GetProperty("Compositor", Instance)?.GetValue(shared) is not MicroComProxyBase compositor
            || shared.GetType().GetProperty("SyncRoot", Instance)?.GetValue(shared) is not { } syncRoot
            || win32.GetType("Avalonia.Win32.WinRT.ICompositionBrush") is not { } brushType)
            return false;
        var fields = new[] { "<MicaBrushDark>k__BackingField", "<MicaBrushLight>k__BackingField" }
            .Select(name => shared.GetType().GetField(name, Instance)).ToArray();
        if (fields.Any(f => f is null || !f.FieldType.IsAssignableFrom(brushType))) return false;

        lock (syncRoot)
        {
            var brush = CreateBlurredWallpaperBrush(compositor.NativePointer);
            if (brush == 0) return false;
            try
            {
                foreach (var field in fields)
                {
                    Marshal.AddRef(brush); // the proxy owns this reference
                    var proxy = MicroComRuntime.CreateProxyFor(brushType, brush, true);
                    (field!.GetValue(shared) as IDisposable)?.Dispose();
                    field.SetValue(shared, proxy);
                }
            }
            finally { Marshal.Release(brush); }
        }
        return true;
    }

    // Windows.UI.Composition, as in Avalonia's winrt.idl.
    private static readonly Guid IidCompositorWithBlurredWallpaperBackdropBrush = new("0D8FB190-F122-5B8D-9FDD-543B0D8EB7F3");
    private static readonly Guid IidCompositionBrush = new("AB0D7608-30C0-40E9-B568-B60A6BD1FB46");

    /// <summary>ICompositorWithBlurredWallpaperBackdropBrush::TryCreateBlurredWallpaperBackdropBrush: the first method
    /// after IInspectable's six.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TryCreateBlurredWallpaperBackdropBrushFn(IntPtr self, out IntPtr result);

    /// <summary>An ICompositionBrush (one reference, the caller's), or 0.</summary>
    private static IntPtr CreateBlurredWallpaperBrush(IntPtr compositor)
    {
        if (Marshal.QueryInterface(compositor, in IidCompositorWithBlurredWallpaperBackdropBrush, out var withWallpaper) < 0) return 0;
        try
        {
            var vtable = Marshal.ReadIntPtr(withWallpaper);
            var create = Marshal.GetDelegateForFunctionPointer<TryCreateBlurredWallpaperBackdropBrushFn>(Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size));
            if (create(withWallpaper, out var backdrop) < 0 || backdrop == 0) return 0;
            try { return Marshal.QueryInterface(backdrop, in IidCompositionBrush, out var brush) < 0 ? 0 : brush; }
            finally { Marshal.Release(backdrop); }
        }
        finally { Marshal.Release(withWallpaper); }
    }
}
