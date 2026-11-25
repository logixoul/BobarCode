using System.Text;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;

namespace Bobar.Core.Loop;

/// <summary>Markdown/plain text rendered from the JSON files (never the other way round).</summary>
public static class Rendering
{
    /// <summary>history.md for round k: all earlier rounds' findings with the author's action and reasoning.</summary>
    public static string History(IReadOnlyList<RoundData> earlier)
    {
        var sb = new StringBuilder();
        sb.Append("# Review history\n\n");
        foreach (var r in earlier)
        {
            sb.Append($"## Round {r.Number}\n\n");
            if (r.Review is null) { sb.Append("(no review recorded)\n\n"); continue; }
            if (r.Review.Withdrawn.Count > 0)
                sb.Append($"Withdrawn by the reviewer this round: {string.Join(", ", r.Review.Withdrawn)}\n\n");
            if (r.Review.Findings.Count == 0) sb.Append("No findings.\n\n");
            foreach (var f in r.Review.Findings)
            {
                AppendFinding(sb, f, "###");
                var resp = r.Response?.Responses.FirstOrDefault(x => x.Id == f.Id);
                if (resp is null) sb.Append("**Author:** (no response)\n\n");
                else sb.Append($"**Author: {(resp.Action == AuthorAction.Fix ? "fix" : "reject")}.** {resp.Reasoning}\n\n");
            }
        }
        return sb.ToString();
    }

    public static void AppendFinding(StringBuilder sb, StoredFinding f, string heading)
    {
        sb.Append($"{heading} {f.Id} [{f.Severity.Name()}] {f.Title}");
        if (f.PreviousId is not null) sb.Append($"   ({(f.Origin == FindingOrigin.Maintained ? "maintained" : "repeated")} from {f.PreviousId})");
        sb.Append('\n');
        if (f.Refs.Count > 0) sb.Append(string.Join(", ", f.Refs)).Append('\n');
        foreach (var w in f.Warnings) sb.Append($"(warning: {w})\n");
        sb.Append(f.Explanation.Trim()).Append("\n\n");
    }

    /// <summary>The Exchange tab: every round's findings with the author's responses, plain text.</summary>
    public static string Exchange(IReadOnlyList<RoundData> rounds, DecisionsFile? decisions)
    {
        var sb = new StringBuilder();
        foreach (var r in rounds)
        {
            sb.Append($"══ Round {r.Number} ══\n\n");
            if (r.Review is null) { sb.Append("(review in progress)\n\n"); continue; }
            if (r.Review.Withdrawn.Count > 0) sb.Append($"Reviewer withdrew: {string.Join(", ", r.Review.Withdrawn)}\n\n");
            if (r.Review.Findings.Count == 0) sb.Append("No findings.\n\n");
            foreach (var f in r.Review.Findings)
            {
                sb.Append($"{f.Id} [{f.Severity.Name()}] {f.Title}");
                if (f.PreviousId is not null) sb.Append($"   ({f.Origin.Name()} from {f.PreviousId})");
                sb.Append('\n');
                if (f.Refs.Count > 0) sb.Append("  ").Append(string.Join(", ", f.Refs)).Append('\n');
                foreach (var w in f.Warnings) sb.Append($"  (warning: {w})\n");
                sb.Append(Indent(f.Explanation.Trim(), "  ")).Append('\n');
                var resp = r.Response?.Responses.FirstOrDefault(x => x.Id == f.Id);
                if (resp is not null)
                    sb.Append($"  → Author: {(resp.Action == AuthorAction.Fix ? "FIX" : "REJECT")}. {resp.Reasoning}\n");
                var dec = decisions?.Decisions.FirstOrDefault(d => d.Id == f.Id);
                if (dec is not null)
                    sb.Append($"  → You: {(dec.Decision == UserDecision.Fix ? "FIX IT" : "DISMISS")}.{(string.IsNullOrWhiteSpace(dec.Comment) ? "" : " " + dec.Comment)}\n");
                sb.Append('\n');
            }
        }
        if (!string.IsNullOrWhiteSpace(decisions?.Comment)) sb.Append($"Your overall comment: {decisions!.Comment}\n");
        return sb.ToString();
    }

    private static string Indent(string text, string pad) => pad + text.Replace("\n", "\n" + pad);

    /// <summary>A discusser's solution.md for one round: the solution, then any messages it sent.</summary>
    public static string DiscussTurn(string discusser, int round, DiscussTurnFile turn)
    {
        var sb = new StringBuilder();
        sb.Append($"# {discusser}'s solution (round {round})\n\n").Append(turn.Solution.Trim()).Append('\n');
        if (turn.Messages.Count > 0)
        {
            sb.Append($"\n# Messages {discusser} sent in round {round}\n\n");
            foreach (var (to, text) in turn.Messages) sb.Append($"## To {to}\n\n").Append(text.Trim()).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>A discussion as plain text: the question, every round's solutions and messages, the user's answers, the report.</summary>
    public static string DiscussExchange(string question, IReadOnlyList<(int Round, string Discusser, DiscussTurnFile? Turn, string? Dropped)> turns,
        QuestionsFile? questions, AnswersFile? answers, ReportFile? report)
    {
        var sb = new StringBuilder();
        sb.Append("══ The question ══\n\n").Append(question.Trim()).Append("\n\n");
        foreach (var g in turns.GroupBy(t => t.Round))
        {
            sb.Append($"══ Round {g.Key} ══\n\n");
            foreach (var (_, who, turn, dropped) in g)
            {
                if (turn is null)
                {
                    sb.Append($"── {who}: {dropped ?? "no reply"} ──\n\n");
                    continue;
                }
                sb.Append($"── {who} ──\n\n").Append(turn.Solution.Trim()).Append("\n\n");
                foreach (var (to, text) in turn.Messages) sb.Append($"  → message to {to}:\n").Append(Indent(text.Trim(), "    ")).Append("\n\n");
            }
        }
        if (questions is not null)
        {
            sb.Append("══ Questions for you ══\n\n");
            foreach (var q in questions.Questions)
            {
                sb.Append($"{q.Id}. {q.Title}\n");
                var a = answers?.Answers.FirstOrDefault(x => x.Id == q.Id);
                if (a?.Option is { } o && o >= 0 && o < q.Options.Count) sb.Append($"  → You chose: {q.Options[o].Label}\n");
                if (!string.IsNullOrWhiteSpace(a?.Comment)) sb.Append($"  → Your comment: {a!.Comment!.Trim()}\n");
                sb.Append('\n');
            }
            if (!string.IsNullOrWhiteSpace(answers?.Comment)) sb.Append($"Your overall comment: {answers!.Comment!.Trim()}\n\n");
        }
        if (report is not null) sb.Append("══ The report ══\n\n").Append(report.Markdown.Trim()).Append('\n');
        return sb.ToString();
    }
}

/// <summary>The reviewer prompt (prompt.md).</summary>
public static class PromptBuilder
{
    /// <summary>How the reviewer returns findings: final JSON text (Pi without streaming), Claude's
    /// structured output, or one report_finding call per finding (streaming).</summary>
    public enum OutputStyle { Json, Structured, Tool }

    public sealed record Inputs(int Round, string RepoDir, string Description, string DiffPath, string? DeltaPath, string? HistoryPath, IReadOnlyList<string> RejectedLastRound, OutputStyle Output, bool Committed = false, string Instructions = "");

    public static string Build(Inputs i)
    {
        var sb = new StringBuilder();
        var what = i.Committed ? "the recent commits (and any uncommitted changes) described below" : "the uncommitted changes described below";
        sb.Append($"""
            You are a code reviewer. Another engineer (the author, an AI coding agent) made {what}, in the repository at {i.RepoDir} (your current directory). Review the changes and report the problems you find. This is review round {i.Round}.

            ## Inputs

            - The author's description of the change is at the end of this prompt.
            - The full diff of the change against the base commit: `{i.DiffPath}`. Read it in full first.

            """);
        if (i.Round >= 2)
        {
            sb.Append($"""
                - What changed since your previous review (the author's fixes): `{i.DeltaPath}`.
                - All earlier findings with the author's action and reasoning for each: `{i.HistoryPath}`. Read it.

                """);
        }
        sb.Append("""
            - If the repository has a `CLAUDE.md` or `AGENTS.md` (at its root, or in directories the change touches), read them: they state the project's rules.
            - You may read any file in the repository for context. Your tools are read-only.
            - You also have web tools (fetching a page, and searching the web where available). Use them for API docs, specs or facts the repository doesn't contain.

            ## What to report

            Report real problems in the change: what is wrong, why it matters, and what to do instead. Be concrete and check your claims against the code before reporting them. Don't report things that are fine, and don't pad the list.

            Severity:
            - `bug`: it is wrong: incorrect behaviour, a crash, data loss, a broken contract.
            - `risk`: it may go wrong: edge cases, concurrency, security, error handling, performance cliffs.
            - `design`: it works but is structured in a way that will cost later: wrong abstraction, duplication, unclear ownership.
            - `nit`: small things: naming, comments, style, dead code.

            `refs` are repo-relative `path:line` locations (may be empty).

            """);
        if (!string.IsNullOrWhiteSpace(i.Instructions))
        {
            sb.Append($"""
                ## The user's instructions

                The user gives these instructions for every review. Where they conflict with the repository's `CLAUDE.md` / `AGENTS.md`, the repository wins. They can't change the output format.

                {i.Instructions.Trim()}

                """);
        }
        if (i.Round >= 2) AppendEarlierFindings(sb, i.Round, i.RejectedLastRound, "in the history");
        AppendOutput(sb, i.Output);
        sb.Append("""

            ## The author's description of the change

            """);
        sb.Append(i.Description.Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// An exploration's prompt (`explore start`): the author's task over the whole repo, no diff, plus the ideas
    /// earlier explorations proposed and the author rejected (<see cref="ExploreLedger.Lines"/>), by P id.
    /// </summary>
    public static string Explore(string repoDir, string task, string ledgerLines, OutputStyle output, string instructions = "")
    {
        var sb = new StringBuilder();
        sb.Append($"""
            You are exploring the repository at {repoDir} (your current directory) for another engineer (the author, an AI coding agent), who will read your findings, decide on each, and act on the ones it accepts. Your tools are read-only. You also have web tools (fetching a page, and searching the web where available), for API docs, specs or facts the repository doesn't contain.

            ## The task

            {task.Trim()}

            ## How to report

            - If the repository has a `CLAUDE.md` or `AGENTS.md` (at its root, or in directories you look at), read them: they state the project's rules.
            - Check every claim against the code before reporting it. Each finding should stand on its own, so the author can combine several into one change.
            - Name the symbol a finding is about in its title (e.g. "`Foo.Bar`: merge the two builders"): line numbers drift, and titles are how later explorations recognize an idea.
            - `refs` are repo-relative `path:line` locations (may be empty).
            - Severity: `design` for structural improvements, `nit` for small ones; `bug` or `risk` only for a real defect you happen to notice.

            """);
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            sb.Append($"""
                ## The user's instructions

                The user gives these instructions for every review. Where they conflict with the repository's `CLAUDE.md` / `AGENTS.md`, the repository wins. They can't change the output format.

                {instructions.Trim()}

                """);
        }
        sb.Append("""
            ## Ideas rejected before

            Earlier explorations proposed these, and the author rejected them for the reason given (paths, title, reason). Don't propose them again unless you have a new argument. If you do, set `"previousId"` to its P id and answer the reason in your explanation.


            """);
        sb.Append(ledgerLines.Length == 0 ? "None yet.\n\n" : ledgerLines + "\n");
        AppendOutput(sb, output, explore: true);
        return sb.ToString();
    }

    /// <summary>
    /// Round k ≥ 2 sent into the reviewer's round k-1 session: it already has the instructions, the
    /// description and what it read, so this only carries what's new: the delta and the author's responses.
    /// </summary>
    public static string Resume(int round, string diffPath, string deltaPath, string historyPath, RoundData last, IReadOnlyList<string> rejectedLast, OutputStyle output, string instructions = "")
    {
        var sb = new StringBuilder();
        sb.Append($"""
            This is review round {round}. The author has answered your round {round - 1} findings and changed the code. Review the change again, with the same instructions as before.

            - What changed since your previous review (the author's fixes): `{deltaPath}`. Read it in full first.
            - The full diff of the change against the base commit, updated: `{diffPath}`.
            - Files you read earlier may have changed since; re-read a file before relying on its contents.
            - If the delta touches directories the change didn't touch before, check them for a `CLAUDE.md` or `AGENTS.md` (even if you browsed them earlier) and read it: it states the project's rules.
            - All rounds' findings and responses, if you need them: `{historyPath}`.

            ## The author's responses to round {round - 1}

            BobarCode gave your findings these ids.


            """);
        if (last.Review is not null)
            foreach (var f in last.Review.Findings)
            {
                sb.Append($"### {f.Id} [{f.Severity.Name()}] {f.Title}\n");
                var resp = last.Response?.Responses.FirstOrDefault(x => x.Id == f.Id);
                sb.Append(resp is null ? "**Author:** (no response)\n\n" : $"**Author: {(resp.Action == AuthorAction.Fix ? "fix" : "reject")}.** {resp.Reasoning}\n\n");
            }
        // Config is read fresh each round, so the current instructions replace whatever the session saw.
        sb.Append(string.IsNullOrWhiteSpace(instructions)
            ? """
              ## The user's instructions

              None for this round: disregard any user instructions given in earlier rounds.


              """
            : $"""
              ## The user's instructions

              These replace any user instructions given in earlier rounds. Where they conflict with the repository's `CLAUDE.md` / `AGENTS.md`, the repository wins. They can't change the output format.

              {instructions.Trim()}


              """);
        AppendEarlierFindings(sb, round, rejectedLast, "above");
        AppendOutput(sb, output);
        return sb.ToString();
    }

    private static void AppendEarlierFindings(StringBuilder sb, int round, IReadOnlyList<string> rejectedLast, string where)
    {
        var rejected = rejectedLast.Count == 0 ? "none" : string.Join(", ", rejectedLast);
        sb.Append($"""
            ## Earlier findings

            The author rejected these findings in round {round - 1}: {rejected}. For each, read the author's reasoning {where}:
            - If you still think it is a real problem, report it again with `"previousId"` set to its id. You may rewrite the explanation to answer the author's reasoning.
            - If the reasoning convinces you, leave it out. Leaving it out withdraws it.

            If a finding the author said they fixed is not actually fixed, report it again with its `previousId`. Don't re-report findings that were fixed correctly. Problems that are new (including ones the fixes introduced) get no `previousId`.

            Later rounds are not a reason to dig deeper: a new finding should be as serious as one you'd have reported in round 1, not a smaller and smaller edge case. Problems the fixes introduced still count.

            """);
    }

    private static void AppendOutput(StringBuilder sb, OutputStyle style, bool explore = false)
    {
        if (style == OutputStyle.Tool)
        {
            var why = explore ? "the author starts reading each finding the moment you report it" : "the author starts fixing each finding the moment you report it, while you keep reviewing";
            sb.Append($"""
                ## Output

                Report each finding by calling the `{Reviewers.PiReviewer.ReportTool}` tool, once per finding, as soon as you have checked it. Don't save them up for the end: {why}. A reported finding can't be taken back, so check it against the code first. The tool takes the finding's `severity` (bug|risk|design|nit), `title`, `explanation`, `refs` and, only when {(explore ? "re-proposing a rejected idea" : "repeating an earlier finding")}, `previousId`. It replies with the finding's id. If it reports an error, correct the call and make it again.

                When you have reported everything, your final message is just `DONE`. If you find no problems, report nothing and reply `DONE`.

                """);
            return;
        }
        sb.Append(style == OutputStyle.Structured
            ? """
              ## Output

              Return your findings as the structured output, in this shape:

              """
            : """
              ## Output

              Your final message must contain only a JSON object, with no prose before or after it and no code fence, in this shape:

              """);
        sb.Append(explore
            ? """
              {"findings": [{"severity": "bug|risk|design|nit", "title": "`Symbol`: short summary", "explanation": "what to change, why, and how big the change is", "refs": ["src/Foo.cs:42"], "previousId": "P3 (only when re-proposing a rejected idea; omit otherwise)"}]}

              If you find nothing worth reporting, return {"findings": []}.

              """
            : """
              {"findings": [{"severity": "bug|risk|design|nit", "title": "short summary", "explanation": "what is wrong, why, and what to do instead", "refs": ["src/Foo.cs:42"], "previousId": "R1.3 (only when repeating an earlier finding; omit otherwise)"}]}

              If you find no problems, return {"findings": []}.

              """);
    }

    /// <summary>For a streaming reviewer that didn't end with DONE.</summary>
    public static string RepairStreaming(string errorText) => $$"""
        Your previous output was not valid:
        {{errorText}}

        Findings you already reported with `{{Reviewers.PiReviewer.ReportTool}}` count; don't report them again. If your review isn't finished, finish it now, reporting each remaining problem with `{{Reviewers.PiReviewer.ReportTool}}`. Then reply with just `DONE`. If you found no problems, reply with just `DONE`.
        """;

    public static string Repair(string errorText) => $$"""
        Your previous output was not valid:
        {{errorText}}

        Reply again with only the corrected JSON object ({"findings": [...]}), with no prose and no code fence.
        """;
}
