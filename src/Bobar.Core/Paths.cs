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

    /// <summary>
    /// %APPDATA%\BobarCode and %LOCALAPPDATA%\BobarCode on Windows, ~/Library/Application Support/BobarCode for
    /// both on macOS, unless BOBAR_CONFIG_DIR / BOBAR_DATA_DIR say otherwise.
    /// </summary>
    public static BobarPaths FromEnvironment()
    {
        var cfg = Environment.GetEnvironmentVariable(ConfigDirEnv);
        var data = Environment.GetEnvironmentVariable(DataDirEnv);
        return new BobarPaths(
            string.IsNullOrWhiteSpace(cfg) ? DefaultDir(Environment.SpecialFolder.ApplicationData) : cfg,
            string.IsNullOrWhiteSpace(data) ? DefaultDir(Environment.SpecialFolder.LocalApplicationData) : data);
    }

    private static string DefaultDir(Environment.SpecialFolder windowsFolder) => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "BobarCode")
        : Path.Combine(Environment.GetFolderPath(windowsFolder), "BobarCode");

    public string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public string KeysFile => Path.Combine(ConfigDir, "keys.env");
    public string RunsDir => Path.Combine(DataDir, "runs");
    /// <summary>Runs deleted from the GUI: kept here (so Undo can move them back) and purged after a week.</summary>
    public string TrashDir => Path.Combine(DataDir, "trash");
    public string PiAgentDir => Path.Combine(DataDir, "pi-agent");
    public string PiSessionsDir => Path.Combine(DataDir, "pi-sessions");

    /// <summary>
    /// Streaming reviewers' snapshots (§5): in %TEMP%, since they hold the repo's files unredacted, one
    /// folder per data dir (a short hash of its path), so a run only ever sweeps its own installation's.
    /// </summary>
    public string SnapshotsDir => Path.Combine(Path.GetTempPath(), "bobar-snapshots",
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(DataDir).TrimEnd('\\', '/').ToLowerInvariant())))[..8]);

    public RunPaths Run(string runId) => new(runId, Path.Combine(RunsDir, runId), SnapshotsDir);
}

/// <summary>The files of one run directory (§4).</summary>
public sealed class RunPaths(string runId, string dir, string snapshotsDir)
{
    public string RunId { get; } = runId;
    public string Dir { get; } = dir;
    public string Events => Path.Combine(Dir, "events.jsonl");
    public string Lock => Path.Combine(Dir, "run.lock");
    /// <summary>The lock holder's pid on macOS/Linux, where run.lock is locked exclusively (see RunLock).</summary>
    public string Pid => Path.Combine(Dir, "run.pid");
    /// <summary>Written by a cancel while a CLI holds run.lock; the holder notices it, stops and ends the run itself.</summary>
    public string CancelRequest => Path.Combine(Dir, "cancel.request");
    public string Desc => Path.Combine(Dir, "desc.md");
    public string Decisions => Path.Combine(Dir, "decisions.json");
    public string Hindsight => Path.Combine(Dir, "hindsight.json");
    /// <summary>A streaming reviewer's working copy of the round's tree, so the author can fix the real
    /// working tree while it reviews. One fixed path per run, refreshed each round (Pi resumes a session only
    /// in its cwd). Under <see cref="BobarPaths.SnapshotsDir"/>, not the run dir: it holds the repo's files unredacted.</summary>
    public string Snapshot => Path.Combine(snapshotsDir, RunId);
    public string SnapshotIndex => Path.Combine(snapshotsDir, RunId + ".index");
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
    /// <summary>Streamed findings as the reviewer reports them, one JSON object per line ({"id", ...finding}).</summary>
    public string Findings => Path.Combine(Dir, "findings.jsonl");
    /// <summary>The author's verdicts declared before its response (`review verdicts`), one line per call ({"t", "fix": [...], "reject": [...]}).</summary>
    public string Verdicts => Path.Combine(Dir, "verdicts.jsonl");
    public string Response => Path.Combine(Dir, "response.json");
    /// <summary>Explore runs: the past rejections listed in the prompt, by their P ids.</summary>
    public string Ledger => Path.Combine(Dir, "ledger.json");
    public string Index => Path.Combine(Dir, "index");
}
