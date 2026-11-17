using Bobar.Core.Model;

namespace Bobar.Core.Loop;

public enum AfterResponse
{
    NextRound,
    AwaitDecisions,
    DoneCap,
}

/// <summary>§5 as pure functions. The loop's end is decided here and nowhere else.</summary>
public static class LoopRules
{
    /// <summary>The stop table's "after response k" rows, top to bottom.</summary>
    public static (AfterResponse Step, AwaitReason? Reason) Decide(int round, int maxRounds, IReadOnlyList<StoredFinding> findings, IReadOnlyList<AuthorResponseItem> responses)
    {
        var rejected = responses.Count(r => r.Action == AuthorAction.Reject);
        var accepted = responses.Count(r => r.Action == AuthorAction.Fix);
        if (round >= maxRounds)
            return rejected > 0 ? (AfterResponse.AwaitDecisions, AwaitReason.Cap) : (AfterResponse.DoneCap, null);
        if (accepted >= 1) return (AfterResponse.NextRound, null);
        if (findings.Count > 0 && findings.All(f => f.Origin == FindingOrigin.Maintained))
            return (AfterResponse.AwaitDecisions, AwaitReason.Deadlock);
        return (AfterResponse.NextRound, null);
    }

    /// <summary>
    /// Classifies round k's findings against earlier rounds and lists the withdrawn ones.
    /// <paramref name="rejectedLastRound"/> are the ids the author rejected in round k−1.
    /// </summary>
    public static (List<FindingOrigin> Origins, List<string> Withdrawn) Classify(IReadOnlyList<string?> previousIds, IReadOnlyCollection<string> rejectedLastRound)
    {
        var origins = previousIds
            .Select(p => p is null ? FindingOrigin.New : rejectedLastRound.Contains(p) ? FindingOrigin.Maintained : FindingOrigin.Repeated)
            .ToList();
        var referenced = previousIds.Where(p => p is not null).ToHashSet();
        var withdrawn = rejectedLastRound.Where(id => !referenced.Contains(id)).ToList();
        return (origins, withdrawn);
    }
}
