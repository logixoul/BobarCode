using System.Text.Json;
using Bobar.Core.IO;
using Bobar.Core.Model;

namespace Bobar.Core;

public sealed class ConfigException(string message) : Exception(message);

public static class ConfigStore
{
    /// <summary>Writes config.json with defaults if it doesn't exist yet.</summary>
    public static void EnsureExists(BobarPaths paths)
    {
        if (File.Exists(paths.ConfigFile)) return;
        RunFiles.WriteTextAtomic(paths.ConfigFile, JsonSerializer.Serialize(AppConfig.CreateDefault(), BobarJson.Indented.AppConfig) + "\n", Redactor.None);
    }

    /// <summary>Read fresh on every CLI call (§4).</summary>
    public static AppConfig Load(BobarPaths paths)
    {
        EnsureExists(paths);
        return Parse(File.ReadAllText(paths.ConfigFile), paths.ConfigFile);
    }

    /// <summary>Parses and validates config.json's text, with no side effects (the GUI reads it this way).</summary>
    public static AppConfig Parse(string text, string file)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize(text, BobarJson.Compact.AppConfig) ?? throw new ConfigException($"{file} is empty");
            Validate(cfg, file);
            return cfg;
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{file} is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Sets reviewerInstructions and rewrites config.json (the GUI's settings window). Throws
    /// ConfigException if the file is invalid, so a broken file is never overwritten.</summary>
    public static void SaveReviewerInstructions(BobarPaths paths, string instructions)
    {
        var cfg = Load(paths);
        cfg.ReviewerInstructions = (instructions ?? "").Trim();
        RunFiles.WriteTextAtomic(paths.ConfigFile, JsonSerializer.Serialize(cfg, BobarJson.Indented.AppConfig) + "\n", Redactor.None);
    }

    private static void Validate(AppConfig cfg, string file)
    {
        if (cfg.MaxRounds < 1) throw new ConfigException($"{file}: maxRounds must be ≥ 1");
        if (cfg.ReviewerTimeoutMinutes <= 0) throw new ConfigException($"{file}: reviewerTimeoutMinutes must be > 0");
        foreach (var (name, r) in cfg.Reviewers)
        {
            if (r.Kind is not (ReviewerKinds.Pi or ReviewerKinds.Claude or ReviewerKinds.Fake))
                throw new ConfigException($"{file}: reviewer '{name}' has unknown kind '{r.Kind}' (pi | claude | fake)");
            if (r.Kind != ReviewerKinds.Fake && string.IsNullOrWhiteSpace(r.Model))
                throw new ConfigException($"{file}: reviewer '{name}' needs a model");
            if (r.Kind == ReviewerKinds.Pi && string.IsNullOrWhiteSpace(r.Provider))
                throw new ConfigException($"{file}: pi reviewer '{name}' needs a provider");
        }
    }
}
