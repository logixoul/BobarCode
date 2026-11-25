using System.Globalization;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;

namespace Bobar.Core.State;

/// <summary>The author's look back on a finished run: the rating scale and its wording.</summary>
public static class Hindsight
{
    public const int Min = 1;
    public const int Max = 10;

    /// <summary>The anchor for each rating; index 0 is rating 1. Counts matter as well as severity.</summary>
    public static readonly IReadOnlyList<string> Anchors =
    [
        "Net harm: wasted time or pushed me into worse code",
        "Nothing useful, only noise",
        "Only nits",
        "One or two minor improvements",
        "Several minor improvements",
        "Made useful suggestions",
        "Caught a significant issue",
        "Caught several significant issues",
        "Caught a severe issue",
        "Caught several severe issues, or a severe one plus significant ones",
    ];

    public const string Severe = "wrong results, data loss, a security hole, or a crash on a main path";
    public const string Significant = "a real bug or design flaw worth fixing before merge";

    public static string Anchor(int usefulness) => usefulness is >= Min and <= Max ? Anchors[usefulness - 1] : "";

    public static string Rating(int usefulness) => $"{usefulness}/{Max}";
}

/// <summary>
/// A finding the author regrets its call on. Severity, title, explanation and refs come from review.json,
/// AuthorReasoning from response.json, UserDecision from decisions.json; empty/null when not found.
/// </summary>
public sealed record HindsightRow(string Id, Severity? Severity, string Title, string Why,
    string Explanation = "", string Refs = "", string AuthorReasoning = "", string UserDecision = "");

/// <summary>A hindsight as the GUI shows it. Pure.</summary>
public sealed record HindsightView(int Usefulness, string Anchor, IReadOnlyList<HindsightRow> AcceptedWrongly, IReadOnlyList<HindsightRow> RejectedWrongly, string Notes)
{
    public static HindsightView Build(HindsightFile h, IReadOnlyList<RoundData> rounds, DecisionsFile? decisions = null)
    {
        var byId = rounds.Where(r => r.Review is not null).SelectMany(r => r.Review!.Findings).ToDictionary(f => f.Id);
        var answers = rounds.Where(r => r.Response is not null).SelectMany(r => r.Response!.Responses).ToDictionary(x => x.Id);
        HindsightRow Row(HindsightItem i)
        {
            var author = !answers.TryGetValue(i.Id, out var a) ? ""
                : (a.Action == AuthorAction.Fix ? "fix" : "reject") + (string.IsNullOrWhiteSpace(a.Reasoning) ? "" : $": {a.Reasoning.Trim()}");
            var dec = SummaryTable.DecisionText(decisions, i.Id);
            return byId.TryGetValue(i.Id, out var f)
                ? new HindsightRow(i.Id, f.Severity, f.Title, i.Why, f.Explanation, string.Join(", ", f.Refs), author, dec)
                : new HindsightRow(i.Id, null, "", i.Why, AuthorReasoning: author, UserDecision: dec);
        }
        List<HindsightRow> Rows(List<HindsightItem> items) => items.Select(Row).ToList();
        return new(h.Usefulness, Hindsight.Anchor(h.Usefulness), Rows(h.AcceptedWrongly), Rows(h.RejectedWrongly), h.Notes);
    }
}

/// <summary>One reviewer (shorthand + model) across all its finished runs.</summary>
public sealed record ReviewerStatsRow(
    string Reviewer,
    string Model,
    int Runs,
    int Rated,
    double? Usefulness,
    string CostPerRun,
    string TimePerRun,
    string AcceptedWrongly,
    string RejectedWrongly)
{
    public string UsefulnessText => Usefulness is { } u ? u.ToString("0.0", CultureInfo.InvariantCulture) : "–";
}

/// <summary>The Reviewers window: how useful each reviewer has been, next to what it cost. Pure.</summary>
public static class ReviewerStats
{
    /// <summary>Finished runs only, cancelled ones excluded. Rates count only runs with a hindsight.</summary>
    public static List<ReviewerStatsRow> Build(IEnumerable<RunState> runs)
    {
        return runs
            // A discussion's score rates the discussion, not a reviewer.
            .Where(s => s.Started is not null && s.WantsHindsight && !s.IsDiscuss)
            // An exploration (one call over the whole repo) and a review loop cost and take very different amounts.
            .GroupBy(s => (s.Started!.Reviewer, s.Started.ReviewerConfig.Kind, s.Started.ReviewerConfig.Provider, s.Started.ReviewerConfig.Model, s.IsExplore))
            .Select(g => Row(g.Key.Reviewer + (g.Key.IsExplore ? " (explore)" : ""), string.IsNullOrEmpty(g.Key.Provider) ? g.Key.Model : $"{g.Key.Provider}/{g.Key.Model}", g.ToList()))
            .OrderByDescending(r => r.Runs).ThenBy(r => r.Reviewer, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Model, StringComparer.Ordinal)
            .ToList();
    }

    private static ReviewerStatsRow Row(string reviewer, string model, List<RunState> runs)
    {
        var started = runs[0].Started;
        var rated = runs.Where(s => s.Hindsight is not null).ToList();
        double? usefulness = rated.Count > 0 ? rated.Average(s => s.Hindsight!.Usefulness) : null;

        // Error runs lose the time and cost of the failed call (it isn't in any event), so they'd look cheap and fast.
        var complete = runs.Where(s => s.Finished!.Outcome != Outcome.Error).ToList();
        string cost, time;
        if (started?.ReviewerConfig.Kind == ReviewerKinds.Claude) cost = "subscription";
        else
        {
            var known = complete.Select(RunCost).OfType<double>().ToList();
            cost = known.Count == 0 ? "cost unknown" : Summary.Cost(known.Average(), started);
        }
        time = complete.Count == 0 ? "–" : Summary.Duration(TimeSpan.FromMilliseconds(complete.Average(s => (double)Reviews(s).Sum(r => r.DurationMs))));

        string accepted = "–", rejected = "–";
        if (rated.Count > 0)
        {
            var fixes = rated.Sum(s => s.Rounds.Sum(r => r.Response?.Fix.Total ?? 0));
            var rejects = rated.Sum(s => s.Rounds.Sum(r => r.Response?.Rejected ?? 0));
            accepted = $"{rated.Sum(s => s.Hindsight!.AcceptedWrongly)} of {(fixes == 1 ? "1 fix" : $"{fixes} fixes")}";
            rejected = $"{rated.Sum(s => s.Hindsight!.RejectedWrongly)} of {Summary.Plural(rejects, "reject")}";
        }
        return new ReviewerStatsRow(reviewer, model, runs.Count, rated.Count, usefulness, cost, time, accepted, rejected);
    }

    private static IEnumerable<ReviewReceived> Reviews(RunState s) => s.Rounds.Select(r => r.Review).OfType<ReviewReceived>();

    /// <summary>The run's reviewer cost, or null when any round's cost is unknown.</summary>
    private static double? RunCost(RunState s)
    {
        var reviews = Reviews(s).ToList();
        return reviews.Any(r => r.CostUsd is null) ? null : reviews.Sum(r => r.CostUsd!.Value);
    }
}
