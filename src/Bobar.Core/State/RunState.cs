using Bobar.Core.Model;

namespace Bobar.Core.State;

public enum RunPhase
{
    /// <summary>A review is requested and not yet received (or the run has just been created).</summary>
    Reviewing,
    /// <summary>The reviewer returned findings; the author has to respond with `next`.</summary>
    WaitingForAuthor,
    AwaitingDecisions,
    Finished,
    /// <summary>A discussion's rounds are over and the author asked the user questions (`discuss ask`); no answers yet.</summary>
    AwaitingAnswers,
    /// <summary>A discussion's rounds are over; the author is synthesizing its report (`discuss report`).</summary>
    AwaitingReport,
}

/// <summary>One round of a discussion: who was called, and how each call went.</summary>
public sealed class DiscussRoundState
{
    public int Number { get; init; }
    public DateTimeOffset StartedAt { get; set; }
    public List<string> Live { get; set; } = [];
    public Dictionary<string, DiscusserReplied> Replies { get; } = new();
    public List<DiscusserRetry> Retries { get; } = [];
    public List<DiscusserRepair> Repairs { get; } = [];
    public List<DiscusserDropped> Dropped { get; } = [];
}

public sealed class RoundState
{
    public int Number { get; init; }
    public DateTimeOffset RequestedAt { get; set; }
    public List<ReviewerRetry> Retries { get; } = [];
    public List<ReviewerRepair> Repairs { get; } = [];
    public ReviewReceived? Review { get; set; }
    public AuthorResponded? Response { get; set; }
}

/// <summary>A run's state, rebuilt by folding events.jsonl. Both exes use this one fold.</summary>
public sealed class RunState
{
    public string RunId { get; }
    public IReadOnlyList<RunEvent> Events { get; }
    public RunStarted? Started { get; private set; }
    public List<RoundState> Rounds { get; } = [];
    public AwaitingDecisions? Awaiting { get; private set; }
    public DecisionsSubmitted? Decisions { get; private set; }
    public RunFinished? Finished { get; private set; }
    /// <summary>The author's latest hindsight, sent after the run finished.</summary>
    public HindsightSubmitted? Hindsight { get; private set; }
    /// <summary>A discussion's rounds (a review's are in <see cref="Rounds"/>).</summary>
    public List<DiscussRoundState> DiscussRounds { get; } = [];
    public DiscussionEnded? DiscussionEnded { get; private set; }
    public QuestionsAsked? Questions { get; private set; }
    public AnswersSubmitted? Answers { get; private set; }

    private RunState(string runId, IReadOnlyList<RunEvent> events)
    {
        RunId = runId;
        Events = events;
    }

    public DateTimeOffset? StartedAt => Events.Count > 0 ? Events[0].T : null;
    public DateTimeOffset? LastEventAt => Events.Count > 0 ? Events[^1].T : null;
    public RoundState? CurrentRound => Rounds.Count > 0 ? Rounds[^1] : null;
    public string Repo => Started?.Repo ?? "";
    public const string NoFolder = "No folder";
    /// <summary>
    /// The run's group in the GUI: the project (so a repo's worktrees share one group), else the repo's (a
    /// discussion's folder's) name, or <see cref="NoFolder"/> for a discussion without one.
    /// </summary>
    public string RepoName => Started is null ? "?" : IsDiscuss && Started.Repo.Length == 0 ? NoFolder : Started.Project ?? ProjectFromPath(Started.Repo);

    /// <summary>
    /// For runs from before <see cref="RunStarted.Project"/>: the folder name, except that a Claude app worktree
    /// (&lt;checkout&gt;/.claude/worktrees/&lt;name&gt;) maps to its checkout's name.
    /// </summary>
    public static string ProjectFromPath(string repo)
    {
        var parts = repo.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 3; i >= 1; i--)
            if (parts[i].Equals(".claude", StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals("worktrees", StringComparison.OrdinalIgnoreCase))
                return parts[i - 1];
        return parts.Length > 0 ? parts[^1] : "";
    }
    /// <summary>The id's name part (repo dir name or a discussion's slug); older runs: the repo's own folder name, even in a worktree.</summary>
    public string Name => Started?.Name ?? (Started is { Repo.Length: > 0 } s ? Path.GetFileName(s.Repo.TrimEnd('\\', '/')) : RepoName);
    /// <summary>"2026-11-25 14:30:05 - speakers": from the start time and name, never parsed from the run id.</summary>
    public string Label => StartedAt is { } t ? $"{t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} - {Name}" : RunId;
    public int MaxRounds => Started?.MaxRounds ?? 0;
    /// <summary>A one-shot exploration (`explore start`), not a review loop.</summary>
    public bool IsExplore => Started?.Kind == RunKinds.Explore;
    /// <summary>A discussion between several models (`discuss start`).</summary>
    public bool IsDiscuss => Started?.Kind == RunKinds.Discuss;
    public DiscussRoundState? CurrentDiscussRound => DiscussRounds.Count > 0 ? DiscussRounds[^1] : null;
    /// <summary>A finished run the author should send a hindsight for: any outcome but cancelled, and not an
    /// error before the reviewer returned anything (out of credits, bad key), since there is nothing to rate.</summary>
    public bool WantsHindsight => NoHindsightReason is null;

    /// <summary>Why no hindsight is wanted now; null when one is.</summary>
    public string? NoHindsightReason => Finished switch
    {
        null => "the run hasn't finished yet",
        { Outcome: Outcome.Cancelled } => "the run was cancelled",
        { Outcome: Outcome.Error } when IsDiscuss => "the discussion ended with an error before the report, so there is nothing to rate",
        { Outcome: Outcome.Error } when !Rounds.Any(r => r.Review is not null) => "the reviewer failed before returning a review, so there is nothing to rate",
        _ => null,
    };

    public RunPhase Phase
    {
        get
        {
            if (Finished is not null) return RunPhase.Finished;
            if (IsDiscuss)
            {
                if (DiscussionEnded is null) return RunPhase.Reviewing;
                return Questions is not null && Answers is null ? RunPhase.AwaitingAnswers : RunPhase.AwaitingReport;
            }
            if (Awaiting is not null) return RunPhase.AwaitingDecisions;
            var r = CurrentRound;
            if (r is null || r.Review is null) return RunPhase.Reviewing;
            if (r.Response is null) return r.Review.Counts.Total > 0 ? RunPhase.WaitingForAuthor : RunPhase.Reviewing;
            return RunPhase.Reviewing;
        }
    }

    public static RunState Fold(string runId, IEnumerable<RunEvent> events)
    {
        var s = new RunState(runId, events.ToList());
        foreach (var ev in s.Events) s.Apply(ev);
        return s;
    }

    private RoundState Round(int k)
    {
        var r = Rounds.FirstOrDefault(x => x.Number == k);
        if (r is null)
        {
            r = new RoundState { Number = k };
            Rounds.Add(r);
        }
        return r;
    }

    private void Apply(RunEvent ev)
    {
        switch (ev)
        {
            case RunStarted e: Started = e; break;
            case ReviewRequested e: Round(e.Round).RequestedAt = e.T; break;
            case ReviewerRetry e: Round(e.Round).Retries.Add(e); break;
            case ReviewerRepair e: Round(e.Round).Repairs.Add(e); break;
            case ReviewReceived e: Round(e.Round).Review = e; break;
            case AuthorResponded e: Round(e.Round).Response = e; break;
            case AwaitingDecisions e: Awaiting = e; break;
            case DecisionsSubmitted e: Decisions = e; break;
            case RunFinished e: Finished ??= e; break;
            case HindsightSubmitted e: Hindsight = e; break;
            case DiscussRoundStarted e:
                var dr = DiscussRound(e.Round);
                dr.StartedAt = e.T;
                dr.Live = e.Live;
                break;
            case DiscusserReplied e: DiscussRound(e.Round).Replies[e.Discusser] = e; break;
            case DiscusserRetry e: DiscussRound(e.Round).Retries.Add(e); break;
            case DiscusserRepair e: DiscussRound(e.Round).Repairs.Add(e); break;
            case DiscusserDropped e: DiscussRound(e.Round).Dropped.Add(e); break;
            case DiscussionEnded e: DiscussionEnded ??= e; break;
            case QuestionsAsked e: Questions ??= e; break;
            case AnswersSubmitted e: Answers ??= e; break;
        }
    }

    private DiscussRoundState DiscussRound(int k)
    {
        var r = DiscussRounds.FirstOrDefault(x => x.Number == k);
        if (r is null)
        {
            r = new DiscussRoundState { Number = k };
            DiscussRounds.Add(r);
        }
        return r;
    }

    /// <summary>A discussion's participants (shorthands, in order).</summary>
    public IReadOnlyList<string> Discussers => Started?.Discussers.Select(d => d.Shorthand).ToList() ?? [];

    /// <summary>The discussers that dropped out, with the round they dropped out in.</summary>
    public Dictionary<string, int> DroppedOut()
        => DiscussRounds.SelectMany(r => r.Dropped).GroupBy(d => d.Discusser).ToDictionary(g => g.Key, g => g.Min(d => d.Round));

    /// <summary>The last round in which each discusser produced a solution (absent: none).</summary>
    public Dictionary<string, int> LastSolutionRound()
    {
        var d = new Dictionary<string, int>();
        foreach (var r in DiscussRounds)
            foreach (var sh in r.Replies.Keys) d[sh] = r.Number;
        return d;
    }
}
