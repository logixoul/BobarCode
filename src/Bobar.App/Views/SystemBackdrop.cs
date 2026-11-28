using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Bobar.App.Views;

/// <summary>Windows 11's own Mica (DWM's system backdrop) under a window, as opposed to Avalonia's imitation of it.
/// Avalonia's window has no redirection surface, so the backdrop shows wherever Avalonia draws see-through: no frame
/// extension is needed (extending the frame over the title row would make DWM draw its own caption buttons there).</summary>
[SupportedOSPlatform("windows")]
public static class SystemBackdrop
{
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_AUTO = 0, DWMSBT_MAINWINDOW = 2;

    /// <summary>Mica on, or back to letting Windows decide (none, for our windows). False if Windows refused (before
    /// Windows 11 22H2 it has no system backdrops) or the window has no handle yet.</summary>
    public static bool Set(Window window, bool mica)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return false;
        var value = mica ? DWMSBT_MAINWINDOW : DWMSBT_AUTO;
        return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int)) >= 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
