using System.Text;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;
using Bobar.Core.State;
using static Bobar.Core.Loop.CommandResult;

namespace Bobar.Core.Loop;

/// <summary>Renders the Markdown results Claude reads (§8).</summary>
public static class CliOutput
{
    public const string Cli = "bobar-cli";

    public static CommandResult Findings(RunState s, ReviewFile review)
    {
        var sb = new StringBuilder();
        var counts = SeverityCounts.Of(review.Findings.Select(f => f.Severity));
        sb.Append($"## Round {review.Round}: the reviewer returned {Summary.Plural(review.Findings.Count, "finding")} ({Summary.CountsText(counts)})");
        if (review.Withdrawn.Count > 0) sb.Append($"; withdrew {review.Withdrawn.Count} ({string.Join(", ", review.Withdrawn)})");
        sb.Append("\n\n");
        foreach (var f in review.Findings) Rendering.AppendFinding(sb, f, "###");
        sb.Append("---\n\n");
        sb.Append("For each finding, decide honestly: fix real problems; reject noise, misreadings and taste disagreements with a concrete reason (cite path:line). Apply the fixes (and run the tests if relevant), then send your response, answering every id exactly once:\n\n");
        sb.Append(NextCommand(s.RunId, review.Findings.Select(f => f.Id)));
        sb.Append($"\nThis was review round {review.Round} of at most {s.MaxRounds}. Don't commit during the loop.\n");
        return new(Statuses.Findings, s.RunId, review.Round, sb.ToString());
    }

    public static string NextCommand(string runId, IEnumerable<string> ids)
    {
        var sb = new StringBuilder();
        sb.Append($"```\n{Cli} review next {runId} --response - <<'EOF'\n{{\"responses\": [\n");
        var list = ids.ToList();
        for (var i = 0; i < list.Count; i++)
            sb.Append($"  {{\"id\": \"{list[i]}\", \"action\": \"fix|reject\", \"reasoning\": \"...\"}}{(i < list.Count - 1 ? "," : "")}\n");
        sb.Append("]}\nEOF\n```\n");
        return sb.ToString();
    }

    public static CommandResult InvalidResponse(RunState s, int round, IEnumerable<string> ids, string errors)
        => new(Statuses.InvalidResponse, s.RunId, round,
            $"The response was not accepted (no round was consumed):\n{errors}\n\nFix the JSON and run `next` again:\n\n{NextCommand(s.RunId, ids)}", 2);

    public static CommandResult Awaiting(RunState s, bool decisionsWritten)
    {
        var a = s.Awaiting!;
        var why = a.Reason == AwaitReason.Deadlock
            ? $"The reviewer maintained {Summary.Plural(a.Ids.Count, "finding")} you rejected ({string.Join(", ", a.Ids)}), so the loop is deadlocked."
            : $"The round cap ({s.MaxRounds}) was reached with {Summary.Plural(a.Ids.Count, "rejected finding")} ({string.Join(", ", a.Ids)}).";
        var body = decisionsWritten
            ? $"{why}\n\nThe user has submitted their decisions. Run `{Cli} review wait {s.RunId}` to collect them."
            : $"{why}\n\nThe user is deciding in BobarCode. Run `{Cli} review wait {s.RunId}` in the background (don't poll; you'll be notified when it returns), then act on the decisions it prints.";
        return new(Statuses.AwaitingDecisions, s.RunId, a.Round, body);
    }

    public static CommandResult Decided(RunState s, IReadOnlyList<RoundData> rounds, DecisionsFile d)
    {
        var sb = new StringBuilder();
        var round = s.Awaiting?.Round ?? s.CurrentRound?.Number ?? 0;
        var last = rounds.FirstOrDefault(r => r.Number == round);
        sb.Append("## The user's decisions\n\n");
        foreach (var item in d.Decisions)
        {
            var f = last?.Review?.Findings.FirstOrDefault(x => x.Id == item.Id);
            var verdict = item.Decision == UserDecision.Fix ? "FIX IT (the user sides with the reviewer)" : "DISMISS (the user sides with you)";
            sb.Append($"### {item.Id} {(f is null ? "" : $"[{f.Severity.Name()}] {f.Title}")}: {verdict}\n");
            if (!string.IsNullOrWhiteSpace(item.Comment)) sb.Append($"User's comment: {item.Comment.Trim()}\n");
            if (f is not null)
            {
                if (f.Refs.Count > 0) sb.Append(string.Join(", ", f.Refs)).Append('\n');
                if (item.Decision == UserDecision.Fix) sb.Append(f.Explanation.Trim()).Append('\n');
            }
            sb.Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(d.Comment)) sb.Append($"Overall comment from the user: {d.Comment.Trim()}\n\n");
        var fixes = d.Decisions.Count(x => x.Decision == UserDecision.Fix);
        sb.Append(fixes > 0
            ? $"Apply the findings marked FIX IT (and follow the comments){(s.Hindsight is null ? ", then send your hindsight (below)" : "")}, then report to the user. The review run has ended: don't call `next`.\n"
            : $"Nothing to fix (follow any comments). {(s.Hindsight is null ? "Send your hindsight (below), then report" : "Report")} to the user. The review run has ended.\n");
        AppendHindsight(sb, s);
        return new(Statuses.Decided, s.RunId, round, sb.ToString());
    }

    public static CommandResult Finished(RunState s, string? extra = null)
    {
        var f = s.Finished!;
        var round = s.CurrentRound?.Number;
        var feed = string.Join("\n", Summary.Lines(s).Select(l => l.ToString()));
        var (status, title, exit) = f.Outcome switch
        {
            Outcome.Clean => (Statuses.Done, $"## Review finished: clean (round {round})", 0),
            Outcome.Cap => (Statuses.Done, $"## Review finished: round cap ({s.MaxRounds}) reached; the last fixes were not re-reviewed", 0),
            Outcome.Decided => (Statuses.Decided, "## Review finished: the user decided", 0),
            Outcome.Cancelled => (Statuses.Cancelled, "## Review cancelled\n\nThis was deliberate (the reason is in the last line below), not a failure: don't retry or restart the run.", 0),
            _ => (Statuses.Error, $"## Review ended with an error\n\n{f.Message}", 1),
        };
        var sb = new StringBuilder($"{title}\n\n{(extra is null ? "" : extra + "\n\n")}```\n{feed}\n```\n\n");
        if (s.WantsHindsight && s.Hindsight is null) sb.Append("First send your hindsight (below), then report to the user.\n");
        AppendHindsight(sb, s);
        sb.Append("\n" + ReportLine(s));
        return new(status, s.RunId, round, sb.ToString(), exit);
    }

    private static string ReportLine(RunState s)
        => "Report to the user in a few lines: rounds, what you fixed, what you rejected and why, and how it ended"
           + (s.WantsHindsight ? "; also your usefulness rating and the findings your hindsight lists as accepted or rejected wrongly (nothing is reverted)." : ".");

    /// <summary>The hindsight request (or a note that it's in), for a finished run that wants one.</summary>
    private static void AppendHindsight(StringBuilder sb, RunState s)
    {
        if (!s.WantsHindsight) return;
        if (s.Hindsight is { } h)
        {
            sb.Append($"\nYour hindsight is in ({Hindsight.Rating(h.Usefulness)}). Sending it again (`{Cli} review hindsight {s.RunId} --input -`) replaces it.\n");
            return;
        }
        sb.Append('\n').Append(HindsightRequest(s.RunId));
    }

    public static string HindsightRequest(string runId)
    {
        var sb = new StringBuilder();
        sb.Append("## Hindsight\n\n");
        sb.Append("The run is over. Look back on it as a whole and say, candidly, how useful it was, which of your calls you would now make differently, and what BobarCode should change. This is for improving BobarCode: nothing is re-reviewed or reverted.\n\n");
        sb.Append($"- `usefulness`: {Hindsight.Min}–{Hindsight.Max} for the whole run. Count how many significant or severe issues it caught, not just the worst one. Wrong findings and churn pull it down; cost doesn't count.\n");
        for (var i = 0; i < Hindsight.Anchors.Count; i++) sb.Append($"  - {i + 1}: {Hindsight.Anchors[i]}\n");
        sb.Append($"  - Severe: {Hindsight.Severe}. Significant: {Hindsight.Significant}.\n");
        sb.Append("- `acceptedWrongly`: findings you answered `fix` but in hindsight shouldn't have (needless, wrong, or churn), each with why.\n");
        sb.Append("- `rejectedWrongly`: findings you answered `reject` that in hindsight were right, each with why. Include ones the reviewer then withdrew, and ones the user sided with you on.\n");
        sb.Append("- `notes`: Markdown. Why this rating, and what (if anything) should change in BobarCode to make it more useful, e.g. the reviewer's prompt, the round cap, how severities are handled, friction in the loop. Write \"Nothing to change.\" if nothing.\n\n");
        sb.Append($"Send it (it returns at once; no need to run it in the background):\n\n");
        sb.Append(HindsightCommand(runId));
        return sb.ToString();
    }

    public static string HindsightCommand(string runId)
        => $"```\n{Cli} review hindsight {runId} --input - <<'EOF'\n{{\"usefulness\": <1-10>,\n \"acceptedWrongly\": [{{\"id\": \"...\", \"why\": \"...\"}}],\n \"rejectedWrongly\": [{{\"id\": \"...\", \"why\": \"...\"}}],\n \"notes\": \"...\"}}\nEOF\n```\nUse `[]` for a list with nothing in it.\n";

    public static CommandResult HindsightSaved(RunState s, HindsightFile h)
    {
        var sb = new StringBuilder();
        sb.Append($"Hindsight saved: {Hindsight.Rating(h.Usefulness)} ({Hindsight.Anchor(h.Usefulness)}); {h.AcceptedWrongly.Count} accepted wrongly, {h.RejectedWrongly.Count} rejected wrongly.\n\n");
        sb.Append(ReportLine(s)).Append('\n');
        return new(Statuses.HindsightSaved, s.RunId, null, sb.ToString());
    }

    public static CommandResult InvalidHindsight(RunState s, string errors)
        => new(Statuses.InvalidHindsight, s.RunId, null,
            $"The hindsight was not accepted:\n{errors}\n\nFix the JSON and run it again:\n\n{HindsightCommand(s.RunId)}", 2);

    public static CommandResult Running(RunState s, bool liveHolder)
    {
        var r = s.CurrentRound;
        var feed = string.Join("\n", Summary.Lines(s, DateTimeOffset.Now).Select(l => l.ToString()));
        var note = liveHolder
            ? "A bobar-cli process is working on it."
            : $"No bobar-cli process is working on it: the process was probably killed. `{Cli} review cancel {s.RunId}` ends the run.";
        return new(Statuses.Running, s.RunId, r?.Number, $"The reviewer is on round {r?.Number ?? 1}. {note}\n\n```\n{feed}\n```\n");
    }

    public static CommandResult Busy(RunPaths run, int? pid)
        => new(Statuses.Busy, run.RunId, null,
            $"Another bobar-cli process{(pid is null ? "" : $" (pid {pid})")} is working on this run right now. Wait for it to finish, or `{Cli} review cancel {run.RunId}`.", 1);
}
