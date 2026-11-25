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
    /// <summary>The GUI's own state (zoom). Kept out of config.json, which is shared with the CLI and rewritten by Settings.</summary>
    public string GuiStateFile => Path.Combine(DataDir, "gui.json");
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

    // ── Discussions (§8b) ──
    /// <summary>The discussion's question, as the author wrote it.</summary>
    public string Prompt => Path.Combine(Dir, "prompt.md");
    /// <summary>The discussers' empty working dir when the discussion has no folder.</summary>
    public string Cwd => Path.Combine(Dir, "cwd");
    public string Questions => Path.Combine(Dir, "questions.json");
    public string Answers => Path.Combine(Dir, "answers.json");
    public string Report => Path.Combine(Dir, "report.json");
}

/// <summary>Run ids (§4): `yyyy-MM-dd_HH-mm-ss_&lt;name&gt;`, with `-2`, `-3`… on a collision. Older runs are `yyyyMMdd-HHmmss-&lt;name&gt;-&lt;hex&gt;`.</summary>
public static class RunIds
{
    private static readonly System.Text.RegularExpressions.Regex NewFormat = new(@"^(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})_");
    private static readonly System.Text.RegularExpressions.Regex OldFormat = new(@"^(\d{8}-\d{6})-");

    /// <param name="exists">Is this id taken already?</param>
    public static string New(string name, DateTimeOffset now, Func<string, bool> exists)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9._-]", "_").Trim('.');
        if (clean.Length == 0) clean = "run";
        var id = $"{now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture)}_{clean}";
        if (!exists(id)) return id;
        for (var n = 2; ; n++)
            if (!exists($"{id}-{n}")) return $"{id}-{n}";
    }

    /// <summary>The start time encoded in a run id (local time, either format); null for anything else.</summary>
    public static DateTime? StartTime(string runId)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var m = NewFormat.Match(runId);
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd_HH-mm-ss", inv, System.Globalization.DateTimeStyles.None, out var t)) return t;
        m = OldFormat.Match(runId);
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd-HHmmss", inv, System.Globalization.DateTimeStyles.None, out t)) return t;
        return null;
    }

    /// <summary>Newest first, across both formats; ids with no time last; the id itself breaks ties.</summary>
    public static IOrderedEnumerable<T> NewestFirst<T>(IEnumerable<T> items, Func<T, string> id)
        => items.OrderByDescending(x => StartTime(id(x)) ?? DateTime.MinValue).ThenByDescending(x => id(x), StringComparer.Ordinal);
}

/// <summary>One discusser's files in a discussion round: round-k/&lt;shorthand&gt;/.</summary>
public sealed class DiscusserPaths(string dir)
{
    public string Dir { get; } = dir;
    public string Prompt => Path.Combine(Dir, "prompt.md");
    public string ReviewerLog => Path.Combine(Dir, "reviewer.jsonl");
    public string Output => Path.Combine(Dir, "output.json");
    /// <summary>Rendered from output.json, for the author to read.</summary>
    public string Solution => Path.Combine(Dir, "solution.md");
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
    public DiscusserPaths Discusser(string shorthand) => new(Path.Combine(Dir, shorthand));
}
