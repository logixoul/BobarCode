using Bobar.Core.Model;

namespace Bobar.Core.State;

public enum DiscussCellState { Replied, Working, Dropped, Absent }

/// <summary>One participant in one round: time, cost and messages once it replied; a live timer while it works.</summary>
public sealed record DiscussCell(string Discusser, int Round, DiscussCellState State, string Text, string Detail = "")
{
    public bool CanOpen => State == DiscussCellState.Replied;
}

public sealed record DiscussRow(int Round, IReadOnlyList<DiscussCell> Cells);

/// <summary>The discussion's table: rounds × participants, per-participant totals, and how it stands. Pure.</summary>
public sealed record DiscussTableModel(IReadOnlyList<string> Discussers, IReadOnlyList<DiscussRow> Rows, IReadOnlyList<string> Totals, string TotalTime, string TotalCost, string Outcome, Outcome? OutcomeKind)
{
    public static DiscussTableModel Build(RunState s, DateTimeOffset now)
    {
        var discussers = s.Discussers;
        var configs = s.Started?.Discussers.ToDictionary(d => d.Shorthand, d => d.Config) ?? [];
        var finished = s.Finished is not null;
        var rows = new List<DiscussRow>();
        foreach (var r in s.DiscussRounds)
        {
            var cells = new List<DiscussCell>();
            foreach (var sh in discussers)
            {
                if (r.Replies.TryGetValue(sh, out var rep))
                {
                    var parts = new List<string> { Summary.Duration(TimeSpan.FromMilliseconds(rep.DurationMs)), Summary.Cost(rep.CostUsd, configs.GetValueOrDefault(sh)) };
                    if (rep.MessagesTo.Count > 0) parts.Add(Summary.Plural(rep.MessagesTo.Count, "message"));
                    if (rep.Repaired) parts.Add("repaired");
                    var retries = r.Retries.Count(x => x.Discusser == sh);
                    if (retries > 0) parts.Add(retries == 1 ? "1 retry" : $"{retries} retries");
                    cells.Add(new DiscussCell(sh, r.Number, DiscussCellState.Replied, string.Join(" · ", parts),
                        rep.MessagesTo.Count > 0 ? $"messages to {string.Join(", ", rep.MessagesTo)}" : ""));
                }
                else if (r.Dropped.FirstOrDefault(d => d.Discusser == sh) is { } drop)
                    cells.Add(new DiscussCell(sh, r.Number, DiscussCellState.Dropped, "dropped out", drop.Reason));
                else if (r.Live.Contains(sh) && !finished)
                    cells.Add(new DiscussCell(sh, r.Number, DiscussCellState.Working, $"working… {Summary.Duration(now - r.StartedAt)}"));
                else
                    cells.Add(new DiscussCell(sh, r.Number, DiscussCellState.Absent, r.Live.Contains(sh) ? "stopped" : ""));
            }
            rows.Add(new DiscussRow(r.Number, cells));
        }

        var replies = s.DiscussRounds.SelectMany(r => r.Replies.Values).ToList();
        var totals = discussers.Select(sh =>
        {
            var mine = replies.Where(x => x.Discusser == sh).ToList();
            if (mine.Count == 0) return "";
            var cfg = configs.GetValueOrDefault(sh);
            var cost = mine.Any(x => x.CostUsd is null) ? null : (double?)mine.Sum(x => x.CostUsd!.Value);
            return $"{Summary.Duration(TimeSpan.FromMilliseconds(mine.Sum(x => x.DurationMs)))} · {Summary.Cost(cost, cfg)}";
        }).ToList();
        // Rounds run in parallel, so the discussion took as long as each round's slowest reply.
        var wall = s.DiscussRounds.Sum(r => r.Replies.Count == 0 ? 0 : r.Replies.Values.Max(x => x.DurationMs));
        var paid = replies.Where(x => configs.GetValueOrDefault(x.Discusser)?.Kind != ReviewerKinds.Claude).ToList();
        var totalCost = paid.Count == 0 ? (replies.Count == 0 ? "" : "subscription")
            : paid.Any(x => x.CostUsd is null) ? "cost unknown"
            : Summary.Cost(paid.Sum(x => x.CostUsd!.Value), (ReviewerConfig?)null);

        return new DiscussTableModel(discussers, rows, totals, replies.Count == 0 ? "" : Summary.Duration(TimeSpan.FromMilliseconds(wall)), totalCost, OutcomeText(s), s.Finished?.Outcome);
    }

    private static string OutcomeText(RunState s)
    {
        if (s.Finished is { } f)
            return f.Outcome switch
            {
                Model.Outcome.Reported => "The author sent its report (below).",
                Model.Outcome.Cancelled => $"Cancelled{(string.IsNullOrWhiteSpace(f.Message) ? "." : $": {f.Message}")}",
                _ => $"Error: {f.Message}",
            };
        if (s.DiscussionEnded is { } e)
        {
            var ended = e.Early ? $"The discussion ended early, after round {e.Round}: fewer than two participants were left." : $"The discussion ended after round {e.Round}.";
            return s.Phase == RunPhase.AwaitingAnswers
                ? $"{ended} The author has questions for you (below)."
                : $"{ended} Waiting for the author's report…";
        }
        return "";
    }
}
