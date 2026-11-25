using System.Text;
using Bobar.Core.Model;
using Bobar.Core.State;
using static Bobar.Core.Loop.CommandResult;

namespace Bobar.Core.Loop;

/// <summary>A discussion's results (§8b).</summary>
public static partial class CliOutput
{
    /// <summary>The rounds are over: where each participant's latest solution is, and what the author does next.</summary>
    public static CommandResult Discussed(RunState s, RunPaths run)
    {
        var end = s.DiscussionEnded;
        var sb = new StringBuilder();
        sb.Append(end is null ? "## The discussion is over\n\n"
            : end.Early ? $"## The discussion is over: it ended early, after round {end.Round} of {s.MaxRounds}, with fewer than two participants left\n\n"
            : $"## The discussion is over ({end.Round} rounds)\n\n");
        sb.Append($"```\n{Feed(s)}\n```\n\n");
        sb.Append("Each participant's latest solution (read them all, in full):\n\n");
        var lastRound = s.LastSolutionRound();
        var dropped = s.DroppedOut();
        foreach (var sh in s.Discussers)
        {
            var why = s.DiscussRounds.SelectMany(r => r.Dropped).FirstOrDefault(d => d.Discusser == sh)?.Reason;
            var gone = dropped.TryGetValue(sh, out var dr) ? $"dropped out in round {dr} ({Shorten(why)})" : null;
            if (lastRound.TryGetValue(sh, out var k))
                sb.Append($"- {sh}: {(gone is null ? "" : gone + "; its last solution is from ")}round {k}: `{run.Round(k).Discusser(sh).Solution}`\n");
            else sb.Append($"- {sh}: {gone ?? "no reply"}: no solution\n");
        }
        sb.Append($"""

            Next:
            1. List the disagreements left between the final solutions, and settle each one you safely can.
            2. If some can't be settled safely, ask the user about them (once; BobarCode opens for them). List the ones you settled under `decided`:

            {AskCommand(s.RunId)}
            3. Otherwise (or once the answers are in), write the report and send it:

            {ReportCommand(s.RunId)}
            """);
        return new(Statuses.Discussed, s.RunId, end?.Round, sb.ToString());
    }

    public static string AskCommand(string runId)
        => $"```\n{Cli} discuss ask {runId} --questions - <<'EOF'\n{{\"questions\": [{{\"id\": \"Q1\", \"title\": \"...\", \"context\": \"<the disagreement, who holds which view, and why you can't pick>\",\n   \"options\": [{{\"label\": \"...\", \"description\": \"...\"}}, {{\"label\": \"...\", \"description\": \"...\"}}]}}],\n \"decided\": \"<the disagreements you settled yourself, and how>\"}}\nEOF\n```\n";

    public static string ReportCommand(string runId)
        => $"```\n{Cli} discuss report {runId} --input - <<'EOF'\n<your report, Markdown>\nEOF\n```\n";

    public static CommandResult InvalidQuestions(RunState s, string errors)
        => new(Statuses.InvalidQuestions, s.RunId, null, $"The questions were not accepted:\n{errors}\n\nFix the JSON and run it again:\n\n{AskCommand(s.RunId)}", 2);

    /// <param name="opened">Whether BobarCode was launched on the run; null when unknown (`show`).</param>
    public static CommandResult AwaitingAnswers(RunState s, bool? opened)
    {
        var where = opened switch
        {
            true => "BobarCode is open on the run: tell the user their answers are wanted there.",
            false => $"BobarCode was not opened (openGuiOnDecisions is off, or it couldn't be launched): tell the user to open BobarCode and select the run \"{s.Label}\".",
            null => "The user is answering in BobarCode.",
        };
        return new(Statuses.AwaitingAnswers, s.RunId, null,
            $"{where}\n\nRun `{Cli} review wait {s.RunId}` in the background (don't poll; you'll be notified when it returns), then write the report with the answers in mind.\n");
    }

    public static CommandResult Answered(RunState s, QuestionsFile questions, AnswersFile answers)
    {
        var sb = new StringBuilder("## The user's answers\n\n");
        foreach (var q in questions.Questions)
        {
            var a = answers.Answers.FirstOrDefault(x => x.Id == q.Id);
            sb.Append($"### {q.Id}. {q.Title}\n");
            if (a?.Option is { } o && o >= 0 && o < q.Options.Count) sb.Append($"Chose: {q.Options[o].Label}\n");
            else sb.Append("Chose none of the options.\n");
            if (!string.IsNullOrWhiteSpace(a?.Comment)) sb.Append($"Comment: {a!.Comment!.Trim()}\n");
            sb.Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(answers.Comment)) sb.Append($"Overall comment from the user: {answers.Comment.Trim()}\n\n");
        sb.Append($"Now write the report, following these answers, and send it:\n\n{ReportCommand(s.RunId)}");
        return new(Statuses.Answered, s.RunId, null, sb.ToString());
    }

    private static CommandResult DiscussFinished(RunState s, string? extra, string feed)
    {
        var f = s.Finished!;
        var (status, title, exit) = f.Outcome switch
        {
            Outcome.Reported => (Statuses.Reported, "## Discussion finished: your report is in", 0),
            Outcome.Cancelled => (Statuses.Cancelled, "## Discussion cancelled\n\nThis was deliberate (the reason is in the last line below), not a failure: don't retry or restart the run.", 0),
            _ => (Statuses.Error, $"## Discussion ended with an error\n\n{f.Message}", 1),
        };
        var sb = new StringBuilder($"{title}\n\n{(extra is null ? "" : extra + "\n\n")}```\n{feed}\n```\n");
        if (s.WantsHindsight && s.Hindsight is null) sb.Append('\n').Append(DiscussHindsightRequest(s.RunId));
        else if (s.Hindsight is { } h) sb.Append($"\nYour hindsight is in ({Hindsight.Rating(h.Usefulness)}). Sending it again replaces it.\n");
        if (f.Outcome == Outcome.Reported) sb.Append("\nShow the user the report (in full, or summarized with a pointer to it in BobarCode if it's very long).\n");
        return new(status, s.RunId, null, sb.ToString(), exit);
    }

    public static string DiscussHindsightRequest(string runId)
        => $"""
            ## Hindsight

            Look back on the discussion as a whole. Rate it from {Hindsight.Min} to {Hindsight.Max}, on your own judgement (there's no fixed scale): how productive the discussion was, and how much the differing viewpoints enriched the final result. Give the reason for your score (any length), and what BobarCode should change ("Nothing to change." if nothing). Send it (it returns at once):

            {DiscussHindsightCommand(runId)}
            """;

    public static string DiscussHindsightCommand(string runId)
        => $"```\n{Cli} review hindsight {runId} --input - <<'EOF'\n{{\"score\": <1-10>,\n \"reason\": \"...\",\n \"notes\": \"...\"}}\nEOF\n```\n";

    private static string Feed(RunState s) => string.Join("\n", Summary.Lines(s).Select(l => l.ToString()));

    private static string Shorten(string? s)
    {
        s = (s ?? "unknown reason").ReplaceLineEndings(" ").Trim();
        return s.Length <= 200 ? s : s[..197] + "…";
    }
}
