using Bobar.Core.Model;
using Bobar.Core.Reviewers;

namespace Bobar.Core.State;

/// <summary>A severity count in a round, e.g. "2 risk" or "1 risk · maintained" (origin is null for new findings).</summary>
public sealed record SeverityChip(Severity Severity, int Count, string? Origin)
{
    public string Text => Origin is null ? $"{Count} {Severity.Name()}" : $"{Count} {Severity.Name()} · {Origin}";
}

/// <summary>
/// One finding in an expanded round; click it for the rest. Action is "fixed", "rejected", or null before the author answered.
/// Refs includes the finding's warnings; Origin is e.g. "maintained from R1.3"; UserDecision is e.g. "fix it: comment". Empty when absent.
/// </summary>
public sealed record FindingRow(string Id, Severity Severity, string Title, string? Action,
    string Explanation = "", string Refs = "", string Origin = "", string AuthorReasoning = "", string UserDecision = "");

public enum RowState { Done, Reviewing, AuthorActing }

/// <summary>One round of the Summary table.</summary>
public sealed record RoundRow(
    int Number,
    RowState State,
    IReadOnlyList<SeverityChip> Chips,
    string ReviewerNote,
    int Fix,
    int Reject,
    string AuthorNote,
    string Time,
    string Cost,
    IReadOnlyList<FindingRow> Findings);

/// <summary>The "You" row: waiting for, or showing, the user's decisions. Comment is the overall comment, once submitted.</summary>
public sealed record DecisionRow(string Text, string Note, string Time, bool Pending, string Comment = "");

public sealed record TotalRow(string Findings, string Author, string Time, string Cost);

public sealed record SummaryTableModel(IReadOnlyList<RoundRow> Rounds, DecisionRow? Decision, TotalRow Total, string Outcome, Outcome? OutcomeKind);

/// <summary>The GUI's Summary tab: one row per round, plus a decisions row, totals and the outcome. Pure.</summary>
public static class SummaryTable
{
    /// <param name="rounds">review.json / response.json per round, for finding titles and per-finding actions (may be empty).</param>
    /// <param name="now">For live elapsed times.</param>
    /// <param name="lastActivity">Last reviewer activity in the round in progress (reviewer.jsonl's mtime).</param>
    /// <param name="liveCost">What the round in progress has cost so far (<see cref="Pricing.LiveCost"/>), when known.</param>
    /// <param name="decisions">decisions.json, for each finding's "You decided" line.</param>
    public static SummaryTableModel Build(RunState s, IReadOnlyList<RoundData> rounds, DateTimeOffset now, DateTimeOffset? lastActivity = null,
        double? liveCost = null, DecisionsFile? decisions = null)
    {
        var started = s.Started;
        var finished = s.Finished is not null;
        var rows = new List<RoundRow>();
        var last = s.Rounds.Count == 0 ? null : s.Rounds[^1];

        foreach (var r in s.Rounds)
        {
            var data = rounds.FirstOrDefault(d => d.Number == r.Number);
            var review = r.Review;
            var notes = new List<string>();
            if (r.Retries.Count > 0) notes.Add(r.Retries.Count == 1 ? "retried once" : $"retried {r.Retries.Count}×");
            if (r.Repairs.Count > 0 || review?.Repaired == true) notes.Add("output repaired");

            if (review is null)
            {
                var reviewing = !finished;
                var note = reviewing
                    ? "Reviewing…" + (lastActivity is { } la && la >= r.RequestedAt ? $" last activity {Summary.Ago(now - la)} ago" : "")
                    : "no review";
                if (notes.Count > 0) note += " · " + string.Join(", ", notes);
                rows.Add(new RoundRow(r.Number, reviewing ? RowState.Reviewing : RowState.Done, [], note, 0, 0, reviewing ? "waiting" : "",
                    reviewing ? Summary.Duration(now - r.RequestedAt) : "",
                    !reviewing ? "" : liveCost is { } c ? Summary.Cost(c, started) + "…" : "…", []));
                continue;
            }

            var chips = (review.Findings.Count > 0
                    ? review.Findings.GroupBy(f => (f.Severity, f.Origin)).Select(g => new SeverityChip(g.Key.Severity, g.Count(), OriginLabel(g.Key.Origin)))
                    : Enum.GetValues<Severity>().Where(v => review.Counts[v] > 0).Select(v => new SeverityChip(v, review.Counts[v], null)))
                .OrderBy(c => c.Severity).ThenBy(c => c.Origin is null ? 1 : 0)
                .ToList();
            var withdrawn = data?.Review?.Withdrawn ?? [];
            if (withdrawn.Count > 0) notes.Insert(0, $"withdrew {string.Join(", ", withdrawn)}");
            else if (review.Withdrawn > 0) notes.Insert(0, $"withdrew {review.Withdrawn}");
            if (review.Counts.Total == 0 && chips.Count == 0) notes.Insert(0, "no findings");

            var resp = r.Response;
            var acting = resp is null && review.Counts.Total > 0 && !finished && s.Awaiting is null;
            var authorNote = acting ? "acting on them…"
                : r == last && resp is not null && s.Finished?.Outcome == Outcome.Cap ? "not re-reviewed"
                : "";

            var answers = data?.Response?.Responses.ToDictionary(x => x.Id) ?? [];
            var findings = (data?.Review?.Findings ?? [])
                .Select(f =>
                {
                    var a = answers.GetValueOrDefault(f.Id);
                    return new FindingRow(f.Id, f.Severity, f.Title,
                        a is null ? null : a.Action == AuthorAction.Fix ? "fixed" : "rejected",
                        f.Explanation.Trim(),
                        string.Join(", ", f.Refs) + string.Concat(f.Warnings.Select(w => $"   ({w})")),
                        f.PreviousId is null ? "" : $"{f.Origin.Name()} from {f.PreviousId}",
                        a?.Reasoning.Trim() ?? "",
                        DecisionText(decisions, f.Id));
                })
                .ToList();

            rows.Add(new RoundRow(r.Number, acting ? RowState.AuthorActing : RowState.Done, chips, string.Join(" · ", notes),
                resp?.Fix.Total ?? 0, resp?.Rejected ?? 0, authorNote,
                Summary.Duration(TimeSpan.FromMilliseconds(review.DurationMs)), Summary.Cost(review.CostUsd, started), findings));
        }

        DecisionRow? decision = null;
        if (s.Awaiting is { } aw)
        {
            var n = Summary.Plural(aw.Ids.Count, "finding");
            var text = aw.Reason == AwaitReason.Deadlock ? $"Reviewer and author disagree on {n}" : $"Round cap reached with {n} rejected";
            // From decisions.json, which can exist before its event does.
            var comment = decisions?.Comment?.Trim() ?? "";
            if (s.Decisions is { } d)
            {
                var parts = new List<string>();
                if (d.Fix > 0) parts.Add($"fix {d.Fix}");
                if (d.Dismiss > 0) parts.Add($"dismiss {d.Dismiss}");
                decision = new DecisionRow(text, $"you decided: {(parts.Count == 0 ? "nothing" : string.Join(", ", parts))}", "", false, comment);
            }
            else
            {
                var pending = !finished;
                decision = new DecisionRow(text, pending ? "decide below" : "", pending ? Summary.Duration(now - aw.T) : "", pending, comment);
            }
        }

        var reviews = s.Rounds.Select(r => r.Review).OfType<ReviewReceived>().ToList();
        var total = new TotalRow(
            Summary.Plural(reviews.Sum(r => r.Counts.Total), "finding") + (finished ? "" : " so far"),
            $"{s.Rounds.Sum(r => r.Response?.Fix.Total ?? 0)} fixed · {s.Rounds.Sum(r => r.Response?.Rejected ?? 0)} rejected",
            Summary.Duration(TimeSpan.FromMilliseconds(reviews.Sum(r => r.DurationMs))),
            TotalCost(reviews, started, finished ? null : liveCost));

        var outcome = s.Finished?.Outcome switch
        {
            Outcome.Clean => "The review is clean.",
            Outcome.Cap => $"Round cap ({s.MaxRounds}) reached. The author's last fixes were not re-reviewed.",
            Outcome.Decided => "Your decisions went to the author. Done.",
            Outcome.Cancelled => string.IsNullOrWhiteSpace(s.Finished.Message) ? "Cancelled." : $"Cancelled: {s.Finished.Message}",
            Outcome.Error => $"Error: {s.Finished.Message}",
            _ => "",
        };
        return new SummaryTableModel(rows, decision, total, outcome, s.Finished?.Outcome);
    }

    private static string? OriginLabel(FindingOrigin o) => o == FindingOrigin.New ? null : o.Name();

    /// <summary>The user's call on a finding, e.g. "fix it: comment"; empty when there is none.</summary>
    public static string DecisionText(DecisionsFile? decisions, string id) =>
        decisions?.Decisions.FirstOrDefault(d => d.Id == id) is { } d
            ? (d.Decision == UserDecision.Fix ? "fix it" : "dismiss") + (string.IsNullOrWhiteSpace(d.Comment) ? "" : $": {d.Comment.Trim()}")
            : "";


    private static string TotalCost(List<ReviewReceived> reviews, RunStarted? started, double? live)
    {
        if (started?.ReviewerConfig.Kind == ReviewerKinds.Claude) return "subscription";
        if (reviews.Count == 0 && live is null) return "";
        if (reviews.Any(r => r.CostUsd is null)) return "cost unknown";
        return Summary.Cost(reviews.Sum(r => r.CostUsd!.Value) + (live ?? 0), started);
    }
}
