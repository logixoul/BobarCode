using System.Text;

namespace Bobar.Core.Loop;

/// <summary>A discusser's prompts (§8b): round 1, rounds ≥ 2 into its own session, and a fresh session's full context.</summary>
public static class DiscussPrompts
{
    /// <summary>Another discusser's latest solution. <paramref name="DroppedAfter"/>: the round it dropped out in, if it did.</summary>
    public sealed record Solution(string Discusser, int Round, string Text, int? DroppedIn = null);

    /// <summary>A message from the previous round; <paramref name="Delivered"/> is false when its recipient isn't (or is no longer) in the discussion.</summary>
    public sealed record Message(string From, string To, string Text, bool Delivered);

    public sealed record Setup(string Me, IReadOnlyList<string> Participants, int Rounds, string Question, string? Dir, bool Structured);

    public static string Round1(Setup s)
    {
        var sb = new StringBuilder();
        AppendIntro(sb, s);
        sb.Append("""
            This is round 1. Each participant answers the question below independently: you won't see the others' answers yet. Think it through and write your solution: a complete, self-contained answer in Markdown. Where it really helps, lay out more than one option, and say which you would pick and why.


            """);
        AppendOutput(sb, s.Structured, messages: false);
        AppendQuestion(sb, s.Question);
        return sb.ToString();
    }

    /// <summary>Round k ≥ 2, sent into the discusser's own session: it already has the question and its earlier solutions.</summary>
    public static string Resume(Setup s, int round, IReadOnlyList<Solution> others, IReadOnlyList<Message> messages)
    {
        var sb = new StringBuilder();
        AppendRound(sb, s, round, others, messages);
        return sb.ToString();
    }

    /// <summary>Round k ≥ 2 in a fresh session (its own session couldn't be resumed): the full context, including its own latest solution.</summary>
    public static string Fresh(Setup s, int round, Solution? mine, IReadOnlyList<Solution> others, IReadOnlyList<Message> messages)
    {
        var sb = new StringBuilder();
        AppendIntro(sb, s);
        AppendQuestion(sb, s.Question);
        sb.Append("\n## Your own latest solution\n\n");
        sb.Append(mine is null ? "(none: you have not written one yet)\n\n" : $"From round {mine.Round}:\n\n{mine.Text.Trim()}\n\n");
        AppendRound(sb, s, round, others, messages);
        return sb.ToString();
    }

    public static string Repair(string errorText, bool messages) => $$"""
        Your previous output was not valid:
        {{errorText}}

        Reply again with only the corrected JSON object ({{(messages ? "{\"solution\": \"...\", \"messages\": {...}}" : "{\"solution\": \"...\"}")}}), with no prose and no code fence. Inside the JSON strings, escape newlines as \n and quotes as \".
        """;

    private static void AppendIntro(StringBuilder sb, Setup s)
    {
        var others = s.Participants.Where(p => p != s.Me).ToList();
        sb.Append($"You are \"{s.Me}\", one of {s.Participants.Count} participants in a discussion; the others are {Join(others)}. Each participant is a different AI model. ");
        sb.Append($"The discussion has {s.Rounds} rounds: in round 1 each of you answers independently; from round 2 on, each of you sees everyone's latest solutions{(s.Rounds > 2 ? " and the messages participants sent each other" : "")}, and revises their own. ");
        sb.Append("At the end, the user's assistant reads the final solutions and writes a report for the user.\n\n");
        sb.Append(s.Dir is null
            ? "The question isn't about a particular folder, so you have no file tools.\n\n"
            : $"Your current directory, {s.Dir}, is the project the question is about. If it has a `CLAUDE.md` or `AGENTS.md`, read it: it states the project's rules. You may read any file in it; your file tools are read-only.\n\n");
        sb.Append("You also have web tools (fetching a page, and searching the web where available). Use them for facts, prices, documentation or anything that may have changed recently.\n\n");
    }

    private static void AppendQuestion(StringBuilder sb, string question)
        => sb.Append("## The question\n\n").Append(question.Trim()).Append('\n');

    private static void AppendRound(StringBuilder sb, Setup s, int round, IReadOnlyList<Solution> others, IReadOnlyList<Message> messages)
    {
        var final = round == s.Rounds;
        sb.Append($"\n## Round {round} of {s.Rounds}{(final ? " (the final round)" : "")}\n\n");
        sb.Append("These are the other participants' latest solutions");
        sb.Append(round >= 3 ? $", and the messages sent in round {round - 1}.\n\n" : ".\n\n");
        if (others.Count == 0) sb.Append("(none)\n\n");
        foreach (var o in others)
        {
            sb.Append($"### {o.Discusser}'s solution (round {o.Round})");
            if (o.DroppedIn is { } d) sb.Append($". {o.Discusser} dropped out in round {d}, so this is its last one");
            sb.Append("\n\n").Append(o.Text.Trim()).Append("\n\n");
        }
        if (round >= 3)
        {
            sb.Append($"### Messages from round {round - 1}\n\n");
            if (messages.Count == 0) sb.Append("(none)\n\n");
            foreach (var m in messages)
            {
                sb.Append($"**{m.From} → {m.To}**{(m.Delivered ? "" : $" (undelivered: {m.To} isn't in the discussion)")}\n\n");
                sb.Append(m.Text.Trim()).Append("\n\n");
            }
        }
        sb.Append($"""
            ## Your task

            1. Consider their viewpoints seriously{(round >= 3 ? ", including the messages addressed to you" : "")}.
            2. Adopt into your own solution whatever is useful. Adopt an idea only when you are actually persuaded: keeping a distinct alternative alive is valuable, so don't merge just to agree.
            3. Write your full revised solution, self-contained (not a list of changes against your earlier one).

            """);
        if (!final)
            sb.Append("4. Optionally, write messages to other participants: to argue a point, ask a question, or point out a flaw they should address. Every participant sees every message, in the next round.\n");
        else
            sb.Append("\nThis is the final round: no messages. Your solution now is your final word.\n");
        sb.Append('\n');
        AppendOutput(sb, s.Structured, messages: !final);
    }

    private static void AppendOutput(StringBuilder sb, bool structured, bool messages)
    {
        sb.Append(structured
            ? "## Output\n\nReturn it as the structured output, in this shape:\n\n"
            : "## Output\n\nYour final message must contain only a JSON object, with no prose before or after it and no code fence, in this shape:\n\n");
        sb.Append(messages
            ? "{\"solution\": \"<your full solution, in Markdown>\", \"messages\": {\"<participant>\": \"<your message to them, in Markdown>\"}}\n\n`messages` may be empty or left out. Its keys are other participants' names.\n\n"
            : "{\"solution\": \"<your full solution, in Markdown>\"}\n\n");
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "none",
        1 => $"\"{names[0]}\"",
        _ => string.Join(", ", names.Take(names.Count - 1).Select(n => $"\"{n}\"")) + $" and \"{names[^1]}\"",
    };
}
