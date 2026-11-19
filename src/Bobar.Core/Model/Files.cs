namespace Bobar.Core.Model;

/// <summary>round-k/review.json: the validated reviewer output plus CLI-assigned ids.</summary>
public sealed class ReviewFile
{
    public int Round { get; set; }
    public List<StoredFinding> Findings { get; set; } = [];
    /// <summary>Findings the author rejected in round k−1 that this review did not repeat.</summary>
    public List<string> Withdrawn { get; set; } = [];
}

public sealed class StoredFinding
{
    public string Id { get; set; } = "";
    public Severity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public List<string> Refs { get; set; } = [];
    public string? PreviousId { get; set; }
    public FindingOrigin Origin { get; set; }
    /// <summary>E.g. "path not found: src/Foo.cs". Shown, never enforced.</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>round-k/response.json and the JSON Claude sends on stdin.</summary>
public sealed class ResponseFile
{
    public List<AuthorResponseItem> Responses { get; set; } = [];
}

public sealed class AuthorResponseItem
{
    public string Id { get; set; } = "";
    public AuthorAction Action { get; set; }
    public string Reasoning { get; set; } = "";
}

/// <summary>decisions.json, written by the GUI.</summary>
public sealed class DecisionsFile
{
    public List<DecisionItem> Decisions { get; set; } = [];
    public string? Comment { get; set; }
}

public sealed class DecisionItem
{
    public string Id { get; set; } = "";
    public UserDecision Decision { get; set; }
    public string? Comment { get; set; }
}

/// <summary>hindsight.json and the JSON the author sends after the run ended: its look back on the whole run.</summary>
public sealed class HindsightFile
{
    /// <summary>1..10, by the anchors in <see cref="State.Hindsight.Anchors"/>.</summary>
    public int Usefulness { get; set; }
    /// <summary>Findings the author fixed but in hindsight shouldn't have.</summary>
    public List<HindsightItem> AcceptedWrongly { get; set; } = [];
    /// <summary>Findings the author rejected but in hindsight were right (withdrawn ones included).</summary>
    public List<HindsightItem> RejectedWrongly { get; set; } = [];
    /// <summary>Markdown: why this rating, and what BobarCode should change.</summary>
    public string Notes { get; set; } = "";
}

public sealed class HindsightItem
{
    public string Id { get; set; } = "";
    public string Why { get; set; } = "";
}
