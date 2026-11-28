using Bobar.Core;
using Bobar.Core.Model;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bobar.App.ViewModels;

/// <summary>The settings window: the theme and backdrop (gui.json) and the reviewer instructions from config.json. The
/// rest of config.json is edited by hand. A picked theme or backdrop shows at once; Save keeps it, closing without saving
/// puts the old one back.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly BobarPaths _paths;
    private readonly MainViewModel _main;

    public static IReadOnlyList<string> ThemeNames { get; } = ["System", "Light", "Dark"];
    /// <summary>Index into <see cref="ThemeNames"/>, in <see cref="GuiTheme"/>'s order.</summary>
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    private GuiTheme Theme => (GuiTheme)Math.Clamp(ThemeIndex, 0, ThemeNames.Count - 1);
    partial void OnThemeIndexChanged(int value) => MainViewModel.ApplyTheme(Theme);

    public static IReadOnlyList<string> BackdropNames { get; } = ["Blurred wallpaper", "Mica", "Acrylic"];
    /// <summary>Index into <see cref="BackdropNames"/>, in <see cref="GuiBackdrop"/>'s order.</summary>
    [ObservableProperty] public partial int BackdropIndex { get; set; }
    private GuiBackdrop Backdrop => (GuiBackdrop)Math.Clamp(BackdropIndex, 0, BackdropNames.Count - 1);
    partial void OnBackdropIndexChanged(int value) => _main.ShownBackdrop = Backdrop;
    /// <summary>Only Windows has the choice; macOS always uses its vibrancy.</summary>
    public static bool HasBackdrops => OperatingSystem.IsWindows();

    [ObservableProperty] public partial string Instructions { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    /// <summary>False when config.json couldn't be read: the instructions can't be edited (the theme still can).</summary>
    [ObservableProperty] public partial bool ConfigLoaded { get; set; }

    public SettingsViewModel(BobarPaths paths, MainViewModel main)
    {
        _paths = paths;
        _main = main;
        ThemeIndex = (int)main.Theme;
        BackdropIndex = (int)main.Backdrop;
        try
        {
            Instructions = ConfigStore.Load(paths).ReviewerInstructions ?? "";
            ConfigLoaded = true;
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            Error = $"Can't read the config, so the reviewer instructions can't be edited here: {ex.Message}";
        }
    }

    /// <summary>Opens config.json with the system's default app for .json, creating it with defaults first if missing.</summary>
    [RelayCommand]
    private void OpenConfig()
    {
        try
        {
            ConfigStore.EnsureExists(_paths);
            Process.Start(new ProcessStartInfo(_paths.ConfigFile) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Error = $"Could not open {_paths.ConfigFile}: {ex.Message}";
        }
    }

    /// <summary>True when saved; otherwise Error says why.</summary>
    public bool Save()
    {
        _main.Theme = Theme;
        _main.Backdrop = Backdrop;
        if (!ConfigLoaded) return true;
        try
        {
            ConfigStore.SaveReviewerInstructions(_paths, Instructions);
            return true;
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            Error = $"Could not save: {ex.Message}";
            return false;
        }
    }

    /// <summary>The window closed: shows the saved theme and backdrop again (a no-op after Save).</summary>
    public void Closed()
    {
        MainViewModel.ApplyTheme(_main.Theme);
        _main.ShownBackdrop = _main.Backdrop;
    }
}
