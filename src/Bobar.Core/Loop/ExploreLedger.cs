using System.Text.RegularExpressions;
using Bobar.Core.Model;

namespace Bobar.Core.Loop;

/// <summary>One idea an earlier exploration proposed and the author rejected.</summary>
public sealed record LedgerRecord(string Run, string Finding, IReadOnlyList<string> Paths, string Title, string Reason);

/// <summary>One finished explore run's files, as the ledger needs them.</summary>
public sealed record ExploreRunData(string RunId, ReviewFile? Review, ResponseFile? Response, LedgerFile? Ledger);

/// <summary>
/// The memory of rejected cleanup ideas that every exploration's prompt carries. Built from the runs' own files,
/// never stored on its own. Pure.
/// </summary>
public static class ExploreLedger
{
    /// <summary>
    /// The rejected findings of <paramref name="runs"/> (oldest first). A finding that re-proposes an earlier
    /// record (previousId "P{n}", resolved through that run's ledger.json) replaces it: the newer verdict wins,
    /// so a re-proposal the author accepted drops the old rejection, and one it rejected again is kept instead.
    /// </summary>
    public static List<LedgerRecord> Build(IEnumerable<ExploreRunData> runs)
    {
        var records = new List<LedgerRecord>();
        // A re-proposed finding → the one that re-proposed it, so a later run citing the same (now replaced)
        // record still reaches the latest one, e.g. when two explorations started from the same ledger.
        var successor = new Dictionary<(string, string), (string, string)>();
        foreach (var run in runs)
        {
            if (run.Review is null || run.Response is null) continue;
            var pIds = run.Ledger?.Entries.ToDictionary(e => e.Id, e => (e.Run, e.Finding)) ?? [];
            foreach (var f in run.Review.Findings)
            {
                if (f.PreviousId is { } prev && pIds.TryGetValue(prev, out var old))
                {
                    var seen = new HashSet<(string, string)>();
                    while (successor.TryGetValue(old, out var next) && seen.Add(old)) old = next;
                    records.RemoveAll(r => r.Run == old.Item1 && r.Finding == old.Item2);
                    successor[old] = (run.RunId, f.Id);
                }
                var answer = run.Response.Responses.FirstOrDefault(x => x.Id == f.Id);
                if (answer?.Action == AuthorAction.Reject)
                    records.Add(new LedgerRecord(run.RunId, f.Id, PathsOf(f.Refs), OneLine(f.Title), OneLine(answer.Reasoning)));
            }
        }
        return records;
    }

    /// <summary>The ledger.json for a prompt listing <paramref name="records"/> as P1, P2, …</summary>
    public static LedgerFile File(IReadOnlyList<LedgerRecord> records) => new()
    {
        Entries = records.Select((r, i) => new LedgerEntry { Id = PId(i), Run = r.Run, Finding = r.Finding }).ToList(),
    };

    public static string PId(int index) => $"P{index + 1}";

    /// <summary>The prompt's lines: `P3  src/Foo.cs, src/Bar.cs  Title - rejected: reason`.</summary>
    public static string Lines(IReadOnlyList<LedgerRecord> records)
        => string.Concat(records.Select((r, i) =>
            $"{PId(i)}  {(r.Paths.Count == 0 ? "(no paths)" : string.Join(", ", r.Paths))}  {r.Title} - rejected: {r.Reason}\n"));

    /// <summary>Refs without their line numbers (they drift), each path once.</summary>
    internal static List<string> PathsOf(IEnumerable<string> refs)
        => refs.Select(r => Regex.Replace(r.Trim(), @"(?::\d+(?:[-–]\d+)?){1,2}$", "")).Where(p => p.Length > 0).Distinct().ToList();

    private static string OneLine(string s) => Regex.Replace(s.Trim(), @"\s*\n\s*", " ");
}
