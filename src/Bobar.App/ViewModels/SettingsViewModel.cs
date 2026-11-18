using Bobar.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bobar.App.ViewModels;

/// <summary>The settings window: the reviewer instructions from config.json. The rest of config.json is edited by hand.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly BobarPaths _paths;

    [ObservableProperty] public partial string Instructions { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial bool CanSave { get; set; }

    public SettingsViewModel(BobarPaths paths)
    {
        _paths = paths;
        try
        {
            Instructions = ConfigStore.Load(paths).ReviewerInstructions ?? "";
            CanSave = true;
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            Error = $"Can't read the config, so nothing can be saved here: {ex.Message}";
        }
    }

    /// <summary>True when saved; otherwise Error says why.</summary>
    public bool Save()
    {
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
}
