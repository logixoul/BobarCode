using System.Globalization;
using Bobar.Core.Model;

namespace Bobar.Core.State;

/// <summary>One line of the summary feed: text, plus a right-hand detail (time, cost).</summary>
public sealed record SummaryLine(string Text, string? Detail = null, bool InProgress = false)
{
    public override string ToString() => Detail is null ? Text : $"{Text}   {Detail}";
}

/// <summary>§10: turns a run's state into the human-readable feed. Pure.</summary>
public static class Summary
{
    /// <param name="now">When given, in-progress lines carry a live elapsed time.</param>
    /// <param name="lastActivity">Last reviewer activity (e.g. reviewer.jsonl's mtime), for spotting a stuck model.</param>
    public static List<SummaryLine> Lines(RunState s, DateTimeOffset? now = null, DateTimeOffset? lastActivity = null)
    {
        var lines = new List<SummaryLine>();
        var started = s.Started;
        var reviewerName = started is null ? "the reviewer" : $"{started.Reviewer} ({started.ReviewerConfig.ModelShort})";
        var finished = s.Finished is not null;

        for (var i = 0; i < s.Events.Count; i++)
        {
            var ev = s.Events[i];
            var isLast = i == s.Events.Count - 1;
            switch (ev)
            {
                case ReviewRequested e:
                {
                    var text = s.IsExplore ? $"Asking {reviewerName} to explore the repository…"
                        : e.Round == 1 ? $"Requesting review from {(started is null ? "the reviewer" : reviewerName)}…"
                        : "Sending the author's response to the reviewer and requesting re-review…";
                    var round = s.Rounds.FirstOrDefault(r => r.Number == e.Round);
                    var pending = round?.Review is null && !finished;
                    lines.Add(new SummaryLine(text, pending ? Live(e.T, now, lastActivity) : null, pending));
                    break;
                }
                case ReviewerRetry { Fresh: true } e:
                    lines.Add(new SummaryLine($"Reviewer call in the resumed session failed ({Shorten(e.Reason)}); trying a fresh session…"));
                    break;
                case ReviewerRetry e:
                    lines.Add(new SummaryLine($"Reviewer call failed ({Shorten(e.Reason)}); retrying in {Duration(TimeSpan.FromSeconds(e.DelaySeconds))}…"));
                    break;
                case ReviewerRepair e:
                    lines.Add(new SummaryLine($"Reviewer output was invalid ({Shorten(e.Error)}); asking it to correct it…"));
                    break;
                case ReviewReceived e:
                    lines.Add(new SummaryLine(ReviewText(e, s.IsExplore ? "Explorer" : "Reviewer"), $"{Duration(TimeSpan.FromMilliseconds(e.DurationMs))} · {Cost(e.CostUsd, started)}"));
                    break;
                case AuthorResponded e when s.IsExplore:
                    lines.Add(new SummaryLine($"Author accepted {(e.Fix.Total == 0 ? "none" : CountsText(e.Fix))}; rejected {(e.Rejected == 0 ? "none" : e.Rejected.ToString(CultureInfo.InvariantCulture))}."));
                    break;
                case AuthorResponded e:
                {
                    var fix = e.Fix.Total == 0 ? "none" : CountsText(e.Fix);
                    var rej = e.Rejected == 0 ? "none" : e.Rejected.ToString(CultureInfo.InvariantCulture);
                    var acting = isLast && !finished && now is not null; // live views only
                    lines.Add(new SummaryLine($"Author will fix {fix}; rejects {rej}{(acting ? " (and is now acting on them…)" : ".")}", null, acting));
                    break;
                }
                case AwaitingDecisions e:
                {
                    var n = e.Ids.Count;
                    var text = e.Reason == AwaitReason.Deadlock
                        ? $"Reviewer and author disagree on {Plural(n, "finding")}. Waiting for your decision…"
                        : $"Round cap ({s.MaxRounds}) reached with {Plural(n, "rejected finding")}. Waiting for your decision…";
                    var pending = s.Decisions is null && !finished;
                    lines.Add(new SummaryLine(text, pending ? Live(e.T, now, null) : null, pending));
                    break;
                }
                case DecisionsSubmitted e:
                {
                    var parts = new List<string>();
                    if (e.Fix > 0) parts.Add($"fix {e.Fix}");
                    if (e.Dismiss > 0) parts.Add($"dismiss {e.Dismiss}");
                    lines.Add(new SummaryLine($"You decided: {(parts.Count == 0 ? "nothing" : string.Join(", ", parts))}. Ending review session."));
                    break;
                }
                case RunFinished e:
                    switch (e.Outcome)
                    {
                        case Outcome.Explored: lines.Add(new SummaryLine($"{Capitalize(e.Message)}. Done.")); break;
                        case Outcome.Clean when s.IsExplore: lines.Add(new SummaryLine("Nothing to report. Done.")); break;
                        case Outcome.Clean: lines.Add(new SummaryLine("The review is clean. Done.")); break;
                        case Outcome.Cap: lines.Add(new SummaryLine($"Round cap ({s.MaxRounds}) reached; the author's last fixes were not re-reviewed. Done.")); break;
                        case Outcome.Cancelled: lines.Add(new SummaryLine(string.IsNullOrWhiteSpace(e.Message) ? "Cancelled." : $"Cancelled: {e.Message}")); break;
                        case Outcome.Error: lines.Add(new SummaryLine($"Error: {e.Message}")); break;
                    }
                    break;
            }
        }
        return lines;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static string ReviewText(ReviewReceived e, string who = "Reviewer")
    {
        var n = e.Findings.Count > 0 ? e.Findings.Count : e.Counts.Total;
        var withdrew = e.Withdrawn > 0 ? $"; withdrew {e.Withdrawn}" : "";
        if (n == 0) return $"{who} returned no findings{withdrew}.";
        string detail;
        if (e.Round == 1 || e.Findings.Count == 0)
        {
            detail = CountsText(e.Counts);
        }
        else
        {
            detail = string.Join(", ", e.Findings
                .GroupBy(f => (f.Severity, f.Origin))
                .OrderBy(g => g.Key.Severity).ThenBy(g => OriginOrder(g.Key.Origin))
                .Select(g => $"{g.Count()} {g.Key.Severity.Name()} ({g.Key.Origin.Name()})"));
        }
        return $"{who} returned {Plural(n, "finding")}: {detail}{withdrew}.";
    }

    public static string CountsText(SeverityCounts c)
        => string.Join(", ", Enum.GetValues<Severity>().Where(s => c[s] > 0).Select(s => $"{c[s]} {s.Name()}"));

    private static int OriginOrder(FindingOrigin o) => o switch { FindingOrigin.Maintained => 0, FindingOrigin.Repeated => 1, _ => 2 };

    public static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var m = (int)t.TotalMinutes;
        return $"{m}m{t.Seconds:00}s";
    }

    public static string Cost(double? usd, RunStarted? started)
    {
        if (started?.ReviewerConfig.Kind == ReviewerKinds.Claude) return "subscription";
        if (usd is null) return "cost unknown";
        return "$" + usd.Value.ToString(usd.Value >= 1 ? "0.00" : "0.000", CultureInfo.InvariantCulture);
    }

    private static string? Live(DateTimeOffset since, DateTimeOffset? now, DateTimeOffset? lastActivity)
    {
        if (now is null) return null;
        var text = Duration(now.Value - since);
        if (lastActivity is { } la && la >= since) text += $" · last activity {Ago(now.Value - la)} ago";
        return text;
    }

    public static string Ago(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m";
        if (t.TotalHours < 48) return $"{(int)t.TotalHours}h";
        return $"{(int)t.TotalDays}d";
    }

    private static string Shorten(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= 160 ? s : s[..157] + "…";
    }
}
