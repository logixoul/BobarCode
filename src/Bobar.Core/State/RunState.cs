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

    private RunState(string runId, IReadOnlyList<RunEvent> events)
    {
        RunId = runId;
        Events = events;
    }

    public DateTimeOffset? StartedAt => Events.Count > 0 ? Events[0].T : null;
    public DateTimeOffset? LastEventAt => Events.Count > 0 ? Events[^1].T : null;
    public RoundState? CurrentRound => Rounds.Count > 0 ? Rounds[^1] : null;
    public string Repo => Started?.Repo ?? "";
    public string RepoName => Started is null ? "?" : Path.GetFileName(Started.Repo.TrimEnd('\\', '/'));
    public int MaxRounds => Started?.MaxRounds ?? 0;
    /// <summary>A one-shot exploration (`explore start`), not a review loop.</summary>
    public bool IsExplore => Started?.Kind == RunKinds.Explore;
    /// <summary>A finished run the author should send a hindsight for: any outcome but cancelled, and not an
    /// error before the reviewer returned anything (out of credits, bad key), since there is nothing to rate.</summary>
    public bool WantsHindsight => NoHindsightReason is null;

    /// <summary>Why no hindsight is wanted now; null when one is.</summary>
    public string? NoHindsightReason => Finished switch
    {
        null => "the run hasn't finished yet",
        { Outcome: Outcome.Cancelled } => "the run was cancelled",
        { Outcome: Outcome.Error } when !Rounds.Any(r => r.Review is not null) => "the reviewer failed before returning a review, so there is nothing to rate",
        _ => null,
    };

    public RunPhase Phase
    {
        get
        {
            if (Finished is not null) return RunPhase.Finished;
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
        }
    }
}
