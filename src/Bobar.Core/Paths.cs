namespace Bobar.Core;

/// <summary>Where config, secrets and runs live (§4). Overridable for tests and the smoke script.</summary>
public sealed class BobarPaths
{
    public const string ConfigDirEnv = "BOBAR_CONFIG_DIR";
    public const string DataDirEnv = "BOBAR_DATA_DIR";

    public string ConfigDir { get; }
    public string DataDir { get; }

    public BobarPaths(string configDir, string dataDir)
    {
        ConfigDir = configDir;
        DataDir = dataDir;
    }

    /// <summary>%APPDATA%\BobarCode and %LOCALAPPDATA%\BobarCode, unless BOBAR_CONFIG_DIR / BOBAR_DATA_DIR say otherwise.</summary>
    public static BobarPaths FromEnvironment()
    {
        var cfg = Environment.GetEnvironmentVariable(ConfigDirEnv);
        var data = Environment.GetEnvironmentVariable(DataDirEnv);
        return new BobarPaths(
            string.IsNullOrWhiteSpace(cfg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BobarCode") : cfg,
            string.IsNullOrWhiteSpace(data) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BobarCode") : data);
    }

    public string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public string KeysFile => Path.Combine(ConfigDir, "keys.env");
    public string RunsDir => Path.Combine(DataDir, "runs");
    public string PiAgentDir => Path.Combine(DataDir, "pi-agent");
    public string PiSessionsDir => Path.Combine(DataDir, "pi-sessions");

    public RunPaths Run(string runId) => new(runId, Path.Combine(RunsDir, runId));
}

/// <summary>The files of one run directory (§4).</summary>
public sealed class RunPaths(string runId, string dir)
{
    public string RunId { get; } = runId;
    public string Dir { get; } = dir;
    public string Events => Path.Combine(Dir, "events.jsonl");
    public string Lock => Path.Combine(Dir, "run.lock");
    public string Desc => Path.Combine(Dir, "desc.md");
    public string Decisions => Path.Combine(Dir, "decisions.json");
    public string Hindsight => Path.Combine(Dir, "hindsight.json");
    public RoundPaths Round(int k) => new(k, Path.Combine(Dir, $"round-{k}"));
}

public sealed class RoundPaths(int round, string dir)
{
    public int Number { get; } = round;
    public string Dir { get; } = dir;
    public string Tree => Path.Combine(Dir, "tree.txt");
    public string Diff => Path.Combine(Dir, "diff.patch");
    public string Delta => Path.Combine(Dir, "delta.patch");
    public string History => Path.Combine(Dir, "history.md");
    public string Prompt => Path.Combine(Dir, "prompt.md");
    public string ReviewerLog => Path.Combine(Dir, "reviewer.jsonl");
    public string Review => Path.Combine(Dir, "review.json");
    public string Response => Path.Combine(Dir, "response.json");
    public string Index => Path.Combine(Dir, "index");
}
