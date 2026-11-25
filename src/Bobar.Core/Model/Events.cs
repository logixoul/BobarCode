using System.Text.Json.Serialization;

namespace Bobar.Core.Model;

/// <summary>One line of events.jsonl (§10). Append-only; state is folded from it.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStarted), "run_started")]
[JsonDerivedType(typeof(ReviewRequested), "review_requested")]
[JsonDerivedType(typeof(ReviewerRetry), "reviewer_retry")]
[JsonDerivedType(typeof(ReviewerRepair), "reviewer_repair")]
[JsonDerivedType(typeof(ReviewReceived), "review_received")]
[JsonDerivedType(typeof(AuthorResponded), "author_responded")]
[JsonDerivedType(typeof(AwaitingDecisions), "awaiting_decisions")]
[JsonDerivedType(typeof(DecisionsSubmitted), "decisions_submitted")]
[JsonDerivedType(typeof(RunFinished), "run_finished")]
[JsonDerivedType(typeof(HindsightSubmitted), "hindsight_submitted")]
[JsonDerivedType(typeof(DiscussRoundStarted), "discuss_round_started")]
[JsonDerivedType(typeof(DiscusserReplied), "discusser_replied")]
[JsonDerivedType(typeof(DiscusserRetry), "discusser_retry")]
[JsonDerivedType(typeof(DiscusserRepair), "discusser_repair")]
[JsonDerivedType(typeof(DiscusserDropped), "discusser_dropped")]
[JsonDerivedType(typeof(DiscussionEnded), "discussion_ended")]
[JsonDerivedType(typeof(QuestionsAsked), "questions_asked")]
[JsonDerivedType(typeof(AnswersSubmitted), "answers_submitted")]
public abstract record RunEvent
{
    public DateTimeOffset T { get; init; }
}

public sealed record RunStarted : RunEvent
{
    public string Repo { get; init; } = "";
    /// <summary>The diff base: HEAD at start, or the parent of --from (§6).</summary>
    public string Base { get; init; } = "";
    /// <summary>HEAD at start; seeds every snapshot's temp index. Empty in runs from before --from (then Base is HEAD).</summary>
    public string Seed { get; init; } = "";
    /// <summary>The commits under review (--from), oldest first. Empty for an uncommitted-only review.</summary>
    public List<CommitInfo> Commits { get; init; } = [];
    public string Reviewer { get; init; } = "";
    public ReviewerConfig ReviewerConfig { get; init; } = new();
    public int MaxRounds { get; init; }
    /// <summary><see cref="RunKinds.Explore"/> for a one-shot exploration; null for a review (and in older runs).</summary>
    public string? Kind { get; init; }
    /// <summary>A review only: the explore run whose findings this change implements (`review start --explore-run`).</summary>
    public string? ExploreRun { get; init; }
    /// <summary>The run id's name part: the repo dir name, or a discussion's topic slug. Null in older runs.</summary>
    public string? Name { get; init; }
    /// <summary>A discussion only: its participants, in --models order. (Reviewer is then their shorthands joined with commas.)</summary>
    public List<Discusser> Discussers { get; init; } = [];
}

public sealed record Discusser
{
    public string Shorthand { get; init; } = "";
    public ReviewerConfig Config { get; init; } = new();
}

public static class RunKinds
{
    /// <summary>One reviewer call over the whole repo, no diff, no rounds after it (`explore start`).</summary>
    public const string Explore = "explore";
    /// <summary>Several models discuss a question over a fixed number of rounds; the author synthesizes a report (`discuss start`).
    /// Repo is the discussion's folder (--dir), or empty without one; MaxRounds is its number of rounds.</summary>
    public const string Discuss = "discuss";
}

public sealed record CommitInfo
{
    public string Sha { get; init; } = "";
    public string Message { get; init; } = "";
    [JsonIgnore] public string Subject => Message.Split('\n', 2)[0].Trim();
}

public sealed record ReviewRequested : RunEvent
{
    public int Round { get; init; }
}

public sealed record ReviewerRetry : RunEvent
{
    public int Round { get; init; }
    public string Reason { get; init; } = "";
    public int DelaySeconds { get; init; }
    /// <summary>The previous round's session couldn't be resumed; the call is redone in a fresh session with the full prompt.</summary>
    public bool Fresh { get; init; }
}

/// <summary>Not in the spec's table: the repair turn (§7) made visible in the feed.</summary>
public sealed record ReviewerRepair : RunEvent
{
    public int Round { get; init; }
    public string Error { get; init; } = "";
}

public sealed record ReviewReceived : RunEvent
{
    public int Round { get; init; }
    public SeverityCounts Counts { get; init; } = new();
    public int Maintained { get; init; }
    public int New { get; init; }
    public int Repeated { get; init; }
    public int Withdrawn { get; init; }
    /// <summary>Per-finding id/severity/origin, so the feed can say "1 risk (maintained)".</summary>
    public List<FindingBrief> Findings { get; init; } = [];
    public long DurationMs { get; init; }
    public TokenUsage Usage { get; init; } = new();
    public double? CostUsd { get; init; }
    public string RequestedModel { get; init; } = "";
    public List<string> ResolvedModels { get; init; } = [];
    public string? RequestedReasoning { get; init; }
    public string? EffectiveReasoning { get; init; }
    public bool Repaired { get; init; }
    /// <summary>The reviewer's session; the next round resumes it.</summary>
    public string? SessionId { get; init; }
    /// <summary>Pi `web_search` calls, included in CostUsd at webSearch.pricePerCall each.</summary>
    public int WebSearches { get; init; }
}

public sealed record FindingBrief
{
    public string Id { get; init; } = "";
    public Severity Severity { get; init; }
    public FindingOrigin Origin { get; init; }
}

public sealed record AuthorResponded : RunEvent
{
    public int Round { get; init; }
    public SeverityCounts Fix { get; init; } = new();
    public int Rejected { get; init; }
}

public sealed record AwaitingDecisions : RunEvent
{
    public int Round { get; init; }
    public List<string> Ids { get; init; } = [];
    public AwaitReason Reason { get; init; }
}

public sealed record DecisionsSubmitted : RunEvent
{
    public int Fix { get; init; }
    public int Dismiss { get; init; }
}

public sealed record RunFinished : RunEvent
{
    public Outcome Outcome { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>After the run ended: the author's hindsight (hindsight.json). A later one replaces an earlier one.</summary>
public sealed record HindsightSubmitted : RunEvent
{
    public int Usefulness { get; init; }
    public int AcceptedWrongly { get; init; }
    public int RejectedWrongly { get; init; }
}

// ── Discussions (§8b) ──

public sealed record DiscussRoundStarted : RunEvent
{
    public int Round { get; init; }
    /// <summary>The discussers called in this round (those that haven't dropped out).</summary>
    public List<string> Live { get; init; } = [];
}

public sealed record DiscusserReplied : RunEvent
{
    public int Round { get; init; }
    public string Discusser { get; init; } = "";
    public long DurationMs { get; init; }
    public TokenUsage Usage { get; init; } = new();
    public double? CostUsd { get; init; }
    public string RequestedModel { get; init; } = "";
    public List<string> ResolvedModels { get; init; } = [];
    public string? RequestedReasoning { get; init; }
    public string? EffectiveReasoning { get; init; }
    public bool Repaired { get; init; }
    public string? SessionId { get; init; }
    public int WebSearches { get; init; }
    public int SolutionChars { get; init; }
    /// <summary>Who it sent messages to (as written, so possibly undelivered names).</summary>
    public List<string> MessagesTo { get; init; } = [];
}

public sealed record DiscusserRetry : RunEvent
{
    public int Round { get; init; }
    public string Discusser { get; init; } = "";
    public string Reason { get; init; } = "";
    public int DelaySeconds { get; init; }
    /// <summary>Its previous session couldn't be resumed; the call is redone in a fresh session with the full context.</summary>
    public bool Fresh { get; init; }
}

public sealed record DiscusserRepair : RunEvent
{
    public int Round { get; init; }
    public string Discusser { get; init; } = "";
    public string Error { get; init; } = "";
}

/// <summary>A discusser failed for good (after the retry and the repair turn); it takes no further part.</summary>
public sealed record DiscusserDropped : RunEvent
{
    public int Round { get; init; }
    public string Discusser { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>The rounds are over: after the last one, or early when fewer than two discussers were left.</summary>
public sealed record DiscussionEnded : RunEvent
{
    public int Round { get; init; }
    public List<string> Live { get; init; } = [];
    public bool Early { get; init; }
}

public sealed record QuestionsAsked : RunEvent
{
    public int Count { get; init; }
}

public sealed record AnswersSubmitted : RunEvent
{
    public int Count { get; init; }
}

public sealed record SeverityCounts
{
    public int Bug { get; init; }
    public int Risk { get; init; }
    public int Design { get; init; }
    public int Nit { get; init; }

    [JsonIgnore] public int Total => Bug + Risk + Design + Nit;

    public int this[Severity s] => s switch
    {
        Severity.Bug => Bug,
        Severity.Risk => Risk,
        Severity.Design => Design,
        _ => Nit,
    };

    public static SeverityCounts Of(IEnumerable<Severity> items)
    {
        int b = 0, r = 0, d = 0, n = 0;
        foreach (var s in items)
        {
            switch (s)
            {
                case Severity.Bug: b++; break;
                case Severity.Risk: r++; break;
                case Severity.Design: d++; break;
                default: n++; break;
            }
        }
        return new SeverityCounts { Bug = b, Risk = r, Design = d, Nit = n };
    }
}

public sealed record TokenUsage
{
    public long Input { get; init; }
    public long Output { get; init; }
    public long CacheRead { get; init; }
    public long CacheWrite { get; init; }

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new()
    {
        Input = a.Input + b.Input,
        Output = a.Output + b.Output,
        CacheRead = a.CacheRead + b.CacheRead,
        CacheWrite = a.CacheWrite + b.CacheWrite,
    };
}
