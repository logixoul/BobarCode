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
}

/// <summary>The reviewer prompt (prompt.md).</summary>
public static class PromptBuilder
{
    public sealed record Inputs(int Round, string RepoDir, string Description, string DiffPath, string? DeltaPath, string? HistoryPath, IReadOnlyList<string> RejectedLastRound, bool ClaudeKind, bool Committed = false);

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

            ## What to report

            Report real problems in the change: what is wrong, why it matters, and what to do instead. Be concrete and check your claims against the code before reporting them. Don't report things that are fine, and don't pad the list.

            Severity:
            - `bug`: it is wrong: incorrect behaviour, a crash, data loss, a broken contract.
            - `risk`: it may go wrong: edge cases, concurrency, security, error handling, performance cliffs.
            - `design`: it works but is structured in a way that will cost later: wrong abstraction, duplication, unclear ownership.
            - `nit`: small things: naming, comments, style, dead code.

            `refs` are repo-relative `path:line` locations (may be empty).

            """);
        if (i.Round >= 2)
        {
            var rejected = i.RejectedLastRound.Count == 0 ? "none" : string.Join(", ", i.RejectedLastRound);
            sb.Append($"""
                ## Earlier findings

                The author rejected these findings in round {i.Round - 1}: {rejected}. For each, read the author's reasoning in the history:
                - If you still think it is a real problem, report it again with `"previousId"` set to its id. You may rewrite the explanation to answer the author's reasoning.
                - If the reasoning convinces you, leave it out. Leaving it out withdraws it.

                If a finding the author said they fixed is not actually fixed, report it again with its `previousId`. Don't re-report findings that were fixed correctly. Problems that are new (including ones the fixes introduced) get no `previousId`.

                """);
        }
        sb.Append(i.ClaudeKind
            ? """
              ## Output

              Return your findings as the structured output, in this shape:

              """
            : """
              ## Output

              Your final message must contain only a JSON object, with no prose before or after it and no code fence, in this shape:

              """);
        sb.Append("""
            {"findings": [{"severity": "bug|risk|design|nit", "title": "short summary", "explanation": "what is wrong, why, and what to do instead", "refs": ["src/Foo.cs:42"], "previousId": "R1.3 (only when repeating an earlier finding; omit otherwise)"}]}

            If you find no problems, return {"findings": []}.

            ## The author's description of the change

            """);
        sb.Append(i.Description.Trim()).Append('\n');
        return sb.ToString();
    }

    public static string Repair(string errorText) => $$"""
        Your previous output was not valid:
        {{errorText}}

        Reply again with only the corrected JSON object ({"findings": [...]}), with no prose and no code fence.
        """;
}
