using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Bobar.App.Views;

/// <summary>Windows 11's own Mica (DWM's system backdrop) under a window, as opposed to Avalonia's imitation of it.</summary>
[SupportedOSPlatform("windows")]
public static class SystemBackdrop
{
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_AUTO = 0, DWMSBT_MAINWINDOW = 2;

    /// <summary>Mica on, or back to letting Windows decide (none, for our windows). A no-op before Windows 11 22H2.</summary>
    public static void Set(Window window, bool mica)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;
        var value = mica ? DWMSBT_MAINWINDOW : DWMSBT_AUTO;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int)); // fails harmlessly on older Windows
        // DWM draws the backdrop only where the frame reaches: all of the window ("sheet of glass").
        if (mica) { var all = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 }; _ = DwmExtendFrameIntoClientArea(hwnd, ref all); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
