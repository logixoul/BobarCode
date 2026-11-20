using System.Text.Json.Nodes;
using Bobar.Core.IO;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;
using Bobar.Core.State;

namespace Bobar.Core.Runs;

/// <summary>
/// The last round before it's settled: what a streaming reviewer has reported so far (findings.jsonl) and the
/// author's verdicts declared before its response (verdicts.jsonl). Both are provisional; review.json and
/// response.json win once they exist.
/// </summary>
public sealed record LiveRound(int Number, IReadOnlyList<StoredFinding> Streamed, IReadOnlyDictionary<string, AuthorAction> Verdicts)
{
    /// <summary>The last round, while it has no response and the run hasn't finished; null otherwise.</summary>
    public static LiveRound? Load(RunPaths run, RunState s, IReadOnlyList<RoundData> rounds)
    {
        if (s.Finished is not null || s.CurrentRound is not { Response: null } r) return null;
        var rp = run.Round(r.Number);
        var rejected = rounds.FirstOrDefault(d => d.Number == r.Number - 1)?.Response?.Responses
            .Where(x => x.Action == AuthorAction.Reject).Select(x => x.Id).ToHashSet() ?? [];
        var streamed = ParseStreamed(RunFiles.ReadShared(rp.Findings), rejected, out var restartAt);
        return new LiveRound(r.Number, streamed, ParseVerdicts(RunFiles.ReadShared(rp.Verdicts), restartAt));
    }

    /// <summary>
    /// findings.jsonl folded: only what follows the last restart line, a re-reported id replacing the earlier
    /// report, in id order. A torn or malformed line is skipped.
    /// </summary>
    public static List<StoredFinding> ParseStreamed(string? text, IReadOnlyCollection<string> rejectedLastRound, out DateTimeOffset? restartAt)
    {
        restartAt = null;
        var found = new SortedDictionary<int, StoredFinding>();
        foreach (var o in Lines(text))
        {
            if (o["restart"] is not null)
            {
                found.Clear();
                restartAt = Time(o) ?? restartAt;
                continue;
            }
            var id = o.Str("id") ?? "";
            if (!int.TryParse(id[(id.IndexOf('.') + 1)..], out var n) || !EnumText.TryParseSeverity(o.Str("severity"), out var sev)) continue;
            var prev = o.Str("previousId");
            found[n] = new StoredFinding
            {
                Id = id,
                Severity = sev,
                Title = o.Str("title") ?? "",
                Explanation = o.Str("explanation") ?? "",
                Refs = o["refs"] is JsonArray refs ? [.. refs.Select(x => x?.ToString() ?? "")] : [],
                PreviousId = prev,
                Origin = LoopRules.Classify([prev], rejectedLastRound).Origins[0],
            };
        }
        return [.. found.Values];
    }

    /// <summary>verdicts.jsonl folded: a later line overrides an earlier one per id; lines from before <paramref name="since"/> are dropped.</summary>
    public static Dictionary<string, AuthorAction> ParseVerdicts(string? text, DateTimeOffset? since)
    {
        var v = new Dictionary<string, AuthorAction>();
        foreach (var o in Lines(text))
        {
            if (since is { } t && (Time(o) is not { } at || at < t)) continue;
            foreach (var (key, action) in new[] { ("fix", AuthorAction.Fix), ("reject", AuthorAction.Reject) })
                if (o[key] is JsonArray ids)
                    foreach (var id in ids.Select(x => x?.ToString()).OfType<string>()) v[id] = action;
        }
        return v;
    }

    /// <summary>One verdicts.jsonl line.</summary>
    public static string VerdictLine(DateTimeOffset t, IEnumerable<string> fix, IEnumerable<string> reject) => new JsonObject
    {
        ["t"] = t.ToString("O"),
        ["fix"] = new JsonArray([.. fix.Select(x => (JsonNode?)x)]),
        ["reject"] = new JsonArray([.. reject.Select(x => (JsonNode?)x)]),
    }.ToJsonString();

    private static DateTimeOffset? Time(JsonObject o) => DateTimeOffset.TryParse(o.Str("t"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private static IEnumerable<JsonObject> Lines(string? text)
    {
        foreach (var line in (text ?? "").Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            JsonObject? o;
            try { o = JsonNode.Parse(line) as JsonObject; }
            catch (System.Text.Json.JsonException) { continue; }
            if (o is not null) yield return o;
        }
    }
}
