using Bobar.Core.Model;

namespace Bobar.Core;

/// <summary>Cost = tokens × the config price table (§9). Vendor cost fields are ignored.</summary>
public static class Pricing
{
    /// <summary>
    /// Null when the price is unknown (never a guess). Claude on the subscription costs 0 here.
    /// Input excludes cached input (that's how both Pi and Claude report it), so the parts add up.
    /// A missing cache price falls back to the input price.
    /// </summary>
    /// <param name="webSearches">Pi `web_search` calls, priced at <paramref name="pricePerSearch"/> each; with searches but no price the cost is unknown.</param>
    public static double? Cost(ReviewerConfig reviewer, IEnumerable<string> resolvedModels, TokenUsage u, IReadOnlyDictionary<string, Price> prices,
        int webSearches = 0, double? pricePerSearch = null)
    {
        if (reviewer.Kind is ReviewerKinds.Claude or ReviewerKinds.Fake) return 0;
        Price? p = null;
        foreach (var m in resolvedModels)
            if (prices.TryGetValue(m + reviewer.TierSuffix, out p)) break;
        if (p is null && !prices.TryGetValue(reviewer.PriceKey, out p)) return null;
        if (webSearches > 0 && pricePerSearch is null) return null;
        return (u.Input * p.Input
                + u.Output * p.Output
                + u.CacheRead * (p.CacheRead ?? p.Input)
                + u.CacheWrite * (p.CacheWrite ?? p.Input)) / 1_000_000d
               + webSearches * (pricePerSearch ?? 0);
    }

    /// <summary>
    /// What the round in progress has cost so far, from its reviewer.jsonl (the GUI tails it). Pi only: it logs
    /// usage per message. Null for other kinds or an unknown price. Includes attempts that were retried, which the
    /// final ReviewReceived cost doesn't.
    /// </summary>
    public static double? LiveCost(ReviewerConfig reviewer, string reviewerLog, IReadOnlyDictionary<string, Price> prices, double? pricePerSearch = null)
    {
        if (reviewer.Kind != ReviewerKinds.Pi) return null;
        var stream = new Reviewers.PiStream();
        foreach (var raw in reviewerLog.Split('\n'))
            if (Reviewers.CliReviewer.ParseLine(raw.TrimEnd('\r')) is { } o)
                stream.Record(o);
        return Cost(reviewer, stream.Models, stream.Usage, prices, stream.WebSearches, pricePerSearch);
    }
}
