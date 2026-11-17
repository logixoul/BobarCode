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
    public static double? Cost(ReviewerConfig reviewer, IEnumerable<string> resolvedModels, TokenUsage u, IReadOnlyDictionary<string, Price> prices)
    {
        if (reviewer.Kind is ReviewerKinds.Claude or ReviewerKinds.Fake) return 0;
        Price? p = null;
        foreach (var m in resolvedModels)
            if (prices.TryGetValue(m, out p)) break;
        if (p is null && !prices.TryGetValue(reviewer.PriceKey, out p)) return null;
        return (u.Input * p.Input
                + u.Output * p.Output
                + u.CacheRead * (p.CacheRead ?? p.Input)
                + u.CacheWrite * (p.CacheWrite ?? p.Input)) / 1_000_000d;
    }
}
