using Bobar.Core.IO;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;
using Bobar.Core.State;

namespace Bobar.Core.Runs;

/// <summary>Read-side helpers shared by the CLI and the GUI.</summary>
public static class RunReader
{
    public static RunState LoadState(RunPaths run) => RunState.Fold(run.RunId, EventLog.Read(run));

    public static RoundData LoadRound(RunPaths run, int k)
    {
        var rp = run.Round(k);
        return new RoundData(k,
            RunFiles.ReadJson(rp.Review, BobarJson.Compact.ReviewFile),
            RunFiles.ReadJson(rp.Response, BobarJson.Compact.ResponseFile));
    }

    public static List<RoundData> LoadRounds(RunPaths run, RunState state)
        => state.Rounds.Select(r => LoadRound(run, r.Number)).ToList();

    public static DecisionsFile? LoadDecisions(RunPaths run) => RunFiles.ReadJson(run.Decisions, BobarJson.Compact.DecisionsFile);

    public static HindsightFile? LoadHindsight(RunPaths run) => RunFiles.ReadJson(run.Hindsight, BobarJson.Compact.HindsightFile);

    /// <summary>Run dirs under the runs folder, newest first (run ids start with a sortable timestamp).</summary>
    public static List<RunPaths> ListRuns(BobarPaths paths)
    {
        if (!Directory.Exists(paths.RunsDir)) return [];
        return Directory.EnumerateDirectories(paths.RunsDir)
            .Select(d => Path.GetFileName(d))
            .Where(n => File.Exists(Path.Combine(paths.RunsDir, n, "events.jsonl")))
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .Select(paths.Run)
            .ToList();
    }

    /// <summary>Exact id, or a unique prefix/suffix of one.</summary>
    public static RunPaths? Resolve(BobarPaths paths, string idOrPart, out string? error)
    {
        error = null;
        var exact = paths.Run(idOrPart);
        if (File.Exists(exact.Events)) return exact;
        var matches = ListRuns(paths).Where(r => r.RunId.StartsWith(idOrPart, StringComparison.OrdinalIgnoreCase) || r.RunId.EndsWith(idOrPart, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        error = matches.Count == 0 ? $"no run '{idOrPart}' in {paths.RunsDir}" : $"'{idOrPart}' matches {matches.Count} runs; give the full id";
        return null;
    }

    /// <summary>
    /// The thread of a finding back through earlier rounds via previousId: earliest first,
    /// each with the author's response in that round (if any).
    /// </summary>
    public static List<(StoredFinding Finding, AuthorResponseItem? Response)> Thread(IReadOnlyList<RoundData> rounds, string id)
    {
        var byId = new Dictionary<string, (StoredFinding, AuthorResponseItem?)>();
        foreach (var r in rounds)
        {
            if (r.Review is null) continue;
            foreach (var f in r.Review.Findings)
                byId[f.Id] = (f, r.Response?.Responses.FirstOrDefault(x => x.Id == f.Id));
        }
        var chain = new List<(StoredFinding, AuthorResponseItem?)>();
        var seen = new HashSet<string>();
        var cur = id;
        while (cur is not null && seen.Add(cur) && byId.TryGetValue(cur, out var item))
        {
            chain.Add(item);
            cur = item.Item1.PreviousId;
        }
        chain.Reverse();
        return chain;
    }
}
