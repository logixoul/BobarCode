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
    /// <summary>Explore runs only: the accepted findings the author chose to implement (may be empty).</summary>
    public List<string>? Chosen { get; set; }
}

/// <summary>
/// An explore run's round-1/ledger.json: which earlier rejection each `P{n}` in its prompt stands for, so a
/// later run can follow a finding's previousId back to the rejection it re-proposes.
/// </summary>
public sealed class LedgerFile
{
    public List<LedgerEntry> Entries { get; set; } = [];
}

public sealed class LedgerEntry
{
    /// <summary>"P3".</summary>
    public string Id { get; set; } = "";
    public string Run { get; set; } = "";
    public string Finding { get; set; } = "";
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

// ── Discussions (§8b) ──

/// <summary>round-k/&lt;shorthand&gt;/output.json: one discusser's validated turn.</summary>
public sealed class DiscussTurnFile
{
    /// <summary>Markdown: its full solution as of this round.</summary>
    public string Solution { get; set; } = "";
    /// <summary>Recipient shorthand → Markdown message (only in rounds that have messages). Every discusser sees every message.</summary>
    public Dictionary<string, string> Messages { get; set; } = new();
}

/// <summary>questions.json (`discuss ask`): the disagreements the author couldn't settle, for the user.</summary>
public sealed class QuestionsFile
{
    public List<QuestionItem> Questions { get; set; } = [];
    /// <summary>Markdown: the disagreements the author settled itself, and how, so the user sees them too.</summary>
    public string? Decided { get; set; }
}

public sealed class QuestionItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Markdown: the disagreement, who holds which view, and why the author can't pick.</summary>
    public string Context { get; set; } = "";
    public List<QuestionOption> Options { get; set; } = [];
}

public sealed class QuestionOption
{
    public string Label { get; set; } = "";
    public string? Description { get; set; }
}

/// <summary>answers.json, written by the GUI: per question an option (0-based) and/or a comment.</summary>
public sealed class AnswersFile
{
    public List<AnswerItem> Answers { get; set; } = [];
    public string? Comment { get; set; }
}

public sealed class AnswerItem
{
    public string Id { get; set; } = "";
    public int? Option { get; set; }
    public string? Comment { get; set; }
}

/// <summary>report.json (`discuss report`): the author's final report, Markdown.</summary>
public sealed class ReportFile
{
    public string Markdown { get; set; } = "";
}

/// <summary>A discussion's hindsight.json: one unanchored 1–10 score, why, and what BobarCode should change.</summary>
public sealed class DiscussHindsightFile
{
    public int Score { get; set; }
    public string Reason { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>gui.json (data dir): the GUI's own state, which the CLI never reads. Zoom is the run body's scale.</summary>
public sealed class GuiStateFile
{
    public double Zoom { get; set; } = 1.0;
}
