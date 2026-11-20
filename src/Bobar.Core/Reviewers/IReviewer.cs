using System.Text.Json.Nodes;
using Bobar.Core.Model;

namespace Bobar.Core.Reviewers;

/// <summary>One reviewer adapter (§9). A new instance is created per round; RunAsync continues
/// <see cref="ReviewerCall.ResumeSessionId"/> when it is set.</summary>
public interface IReviewer
{
    Task<ReviewerResult> RunAsync(ReviewerCall call, IProgress<ReviewerActivity> activity, CancellationToken ct);
    Task<ReviewerResult> RepairAsync(ReviewerResult previous, string error, IProgress<ReviewerActivity> activity, CancellationToken ct);

    /// <summary>The reviewer reports each finding through a `report_finding` tool as it finds it
    /// (<see cref="ReviewerCall.OnFinding"/>) instead of returning them all in its final JSON (§9).</summary>
    bool StreamsFindings => false;
}

/// <summary>One finding reported mid-review: <paramref name="N"/> is its number in the round (id R{k}.{N}),
/// <paramref name="Finding"/> the raw finding object (severity, title, explanation, refs, previousId).</summary>
public sealed record StreamedFinding(int N, JsonObject Finding);

public sealed record ReviewerCall
{
    public required int Round { get; init; }
    public required string Prompt { get; init; }
    /// <summary>cwd for the reviewer.</summary>
    public required string RepoDir { get; init; }
    public required string RunDir { get; init; }
    public required string RoundDir { get; init; }
    /// <summary>Receives every raw stream line (the engine redacts it into reviewer.jsonl).</summary>
    public required Action<string> RawLog { get; init; }
    /// <summary>Earlier rounds as data; only FakeReviewer looks at this.</summary>
    public IReadOnlyList<RoundData> PriorRounds { get; init; } = [];
    public string? DiffText { get; init; }
    /// <summary>The previous round's session to continue (round ≥ 2), or null for a fresh session.</summary>
    public string? ResumeSessionId { get; init; }
    /// <summary>Set when the reviewer streams findings: called once per successful `report_finding` call.</summary>
    public Action<StreamedFinding>? OnFinding { get; init; }
    /// <summary>The ids a finding's previousId may name (all earlier rounds' findings).</summary>
    public IReadOnlyCollection<string> EarlierIds { get; init; } = [];
    /// <summary>The number the next reported finding gets (a retry that resumes a session continues after what it already reported).</summary>
    public int FirstFindingNumber { get; init; } = 1;
}

public sealed record ReviewerResult
{
    public required ReviewerCall Call { get; init; }
    public string FinalText { get; init; } = "";
    /// <summary>The parsed JSON object, or null when the final text wasn't JSON.</summary>
    public JsonNode? Parsed { get; init; }
    public TokenUsage Usage { get; init; } = new();
    public string RequestedModel { get; init; } = "";
    public List<string> ResolvedModels { get; init; } = [];
    public string? RequestedReasoning { get; init; }
    public string? EffectiveReasoning { get; init; }
    public string? SessionId { get; init; }
    public TimeSpan Duration { get; init; }
}

public enum ActivityKind { Text, Thinking, ToolCall, ToolResult, Error, Retry, Info }

/// <summary>One line of reviewer activity. <paramref name="Detail"/> is the full content when it
/// doesn't fit on that line (a tool result, thinking, long tool arguments); the GUI shows it collapsed.</summary>
public sealed record ReviewerActivity(ActivityKind Kind, string Text, string? Detail = null);

/// <summary>A failed reviewer call. Transient ones are retried once by the engine (§9).</summary>
public sealed class ReviewerException : Exception
{
    public bool Transient { get; }
    public TimeSpan? RetryAfter { get; }
    public bool TimedOut { get; }
    public TokenUsage Usage { get; }
    /// <summary>A fresh session wouldn't help either (quota exhausted, or the call already used its retry),
    /// so a failed resume isn't redone in a fresh session.</summary>
    public bool Final { get; }

    public ReviewerException(string message, bool transient = false, TimeSpan? retryAfter = null, bool timedOut = false, TokenUsage? usage = null, bool final = false)
        : base(message)
    {
        Transient = transient;
        RetryAfter = retryAfter;
        TimedOut = timedOut;
        Usage = usage ?? new TokenUsage();
        Final = final;
    }
}

/// <summary>One earlier round's files, loaded from the run dir.</summary>
public sealed record RoundData(int Number, ReviewFile? Review, ResponseFile? Response);
