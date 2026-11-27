using Bobar.Core;
using Bobar.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bobar.App.ViewModels;

/// <summary>The settings window: the theme (gui.json) and the reviewer instructions from config.json. The rest of
/// config.json is edited by hand. A picked theme shows at once; Save keeps it, closing without saving puts the old one back.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly BobarPaths _paths;
    private readonly MainViewModel _main;

    public static IReadOnlyList<string> ThemeNames { get; } = ["System", "Light", "Dark"];
    /// <summary>Index into <see cref="ThemeNames"/>, in <see cref="GuiTheme"/>'s order.</summary>
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    private GuiTheme Theme => (GuiTheme)Math.Clamp(ThemeIndex, 0, ThemeNames.Count - 1);
    partial void OnThemeIndexChanged(int value) => MainViewModel.ApplyTheme(Theme);

    [ObservableProperty] public partial string Instructions { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    /// <summary>False when config.json couldn't be read: the instructions can't be edited (the theme still can).</summary>
    [ObservableProperty] public partial bool ConfigLoaded { get; set; }

    public SettingsViewModel(BobarPaths paths, MainViewModel main)
    {
        _paths = paths;
        _main = main;
        ThemeIndex = (int)main.Theme;
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

    /// <summary>True when saved; otherwise Error says why.</summary>
    public bool Save()
    {
        _main.Theme = Theme;
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

    /// <summary>The window closed: shows the saved theme again (a no-op after Save).</summary>
    public void Closed() => MainViewModel.ApplyTheme(_main.Theme);
}
