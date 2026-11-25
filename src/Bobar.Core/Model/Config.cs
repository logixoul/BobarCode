using System.Text.Json.Serialization;

namespace Bobar.Core.Model;

/// <summary>%APPDATA%\BobarCode\config.json (§4). Read fresh on every CLI call.</summary>
public sealed class AppConfig
{
    public int MaxRounds { get; set; } = 5;
    /// <summary>A double so tests (and impatient users) can use fractions of a minute.</summary>
    public double ReviewerTimeoutMinutes { get; set; } = 30;
    public bool OpenGuiOnDecisions { get; set; } = true;
    /// <summary>The user's standing instructions for every review (e.g. "Prefer lightweight designs."), added to the
    /// reviewer prompt. Project-specific rules belong in the repo's CLAUDE.md / AGENTS.md, which win on conflict.</summary>
    public string ReviewerInstructions { get; set; } = "";
    public Dictionary<string, ReviewerConfig> Reviewers { get; set; } = new();
    /// <summary>USD per 1M tokens, keyed "provider/model".</summary>
    public Dictionary<string, Price> Prices { get; set; } = new();
    /// <summary>Web search for Pi reviewers (Brave Search API). Claude reviewers use their built-in WebSearch.</summary>
    /// <summary>A config without a webSearch section gets the default price; a section without pricePerCall has none (searches then make the cost unknown).</summary>
    public WebSearchConfig WebSearch { get; set; } = new() { PricePerCall = 0.005 };

    public static AppConfig CreateDefault() => new()
    {
        Reviewers = new()
        {
            ["ds"] = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "lithos", Model = "deepseek-ai/DeepSeek-V4.1-Flash-ultra", Thinking = "high", Env = ["LITHOS_API_KEY"] },
            ["sol"] = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "openrouter", Model = "openai/gpt-6.2-sol", Thinking = "medium", Env = ["OPENROUTER_API_KEY"] },
            ["hydrogen"] = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "openrouter", Model = "google/gemini-4-hydrogen", Thinking = "medium", Env = ["OPENROUTER_API_KEY"] },
            ["qwen4"] = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "openrouter", Model = "qwen/qwen4-max", Thinking = "high", Env = ["OPENROUTER_API_KEY"] },
            ["glm"] = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "openrouter", Model = "z-ai/glm-5.3", Thinking = "high", Env = ["OPENROUTER_API_KEY"] },
            ["terra"] = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "openrouter", Model = "openai/gpt-6.1-terra", Thinking = "medium", Env = ["OPENROUTER_API_KEY"] },
            ["luna"] = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "openrouter", Model = "openai/gpt-6-luna", Thinking = "medium", Env = ["OPENROUTER_API_KEY"] },
            ["neon"] = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "openrouter", Model = "google/gemini-4-neon", Thinking = "medium", Env = ["OPENROUTER_API_KEY"] },
            ["opus"] = new ReviewerConfig { Kind = ReviewerKinds.Claude, Model = "opus", Effort = "medium" },
        },
        Prices = new()
        {
            ["lithos/deepseek-ai/DeepSeek-V4.1-Flash-ultra"] = new Price { Input = 0.35, Output = 1.4, CacheRead = 0.007 },
            ["openrouter/openai/gpt-6.2-sol"] = new Price { Input = 2, Output = 10, CacheRead = 0.1, CacheWrite = 2.5 },
            ["openrouter/google/gemini-4-hydrogen"] = new Price { Input = 1.5, Output = 7.5, CacheRead = 0.075 },
            ["openrouter/qwen/qwen4-max"] = new Price { Input = 1.425, Output = 4.275, CacheRead = 0.07125 },
            ["openrouter/z-ai/glm-5.3"] = new Price { Input = 1.4, Output = 4.4, CacheRead = 0.26 },
            ["openrouter/openai/gpt-6.1-terra"] = new Price { Input = 1, Output = 5, CacheRead = 0.05, CacheWrite = 1.25 },
            ["openrouter/openai/gpt-6-luna"] = new Price { Input = 0.1, Output = 0.5, CacheRead = 0.01, CacheWrite = 0.125 },
            ["openrouter/google/gemini-4-neon"] = new Price { Input = 3, Output = 15, CacheRead = 0.15 },
        },
    };
}

public static class ReviewerKinds
{
    public const string Pi = "pi";
    public const string Claude = "claude";
    public const string Fake = "fake";
}

/// <summary>One reviewer; kind-specific fields are simply optional (kept flat on purpose).</summary>
public sealed class ReviewerConfig
{
    public string Kind { get; set; } = ReviewerKinds.Pi;
    /// <summary>Pi only.</summary>
    public string? Provider { get; set; }
    public string Model { get; set; } = "";
    /// <summary>Pi only: off|minimal|low|medium|high|xhigh|max.</summary>
    public string? Thinking { get; set; }
    /// <summary>Claude only: low|medium|high|xhigh|max.</summary>
    public string? Effort { get; set; }
    /// <summary>keys.env entries passed to this reviewer's process, and only those.</summary>
    public List<string> Env { get; set; } = [];
    /// <summary>Optional override of the executable ("pi" / "claude" on PATH by default).</summary>
    public string? Command { get; set; }
    /// <summary>Pi only: the provider's service tier, e.g. "flex" (OpenAI: half price, slower). Null: the provider's default.</summary>
    public string? ServiceTier { get; set; }

    /// <summary>"@flex" for a reviewer on a service tier, else empty: price keys carry it, so a tier never falls back to standard prices.</summary>
    [JsonIgnore] public string TierSuffix => string.IsNullOrWhiteSpace(ServiceTier) ? "" : "@" + ServiceTier.Trim();

    /// <summary>The "provider/model" key into the price table ("provider/model@tier" on a service tier).</summary>
    [JsonIgnore] public string PriceKey => (Kind == ReviewerKinds.Claude ? $"anthropic/{Model}" : $"{Provider}/{Model}") + TierSuffix;

    /// <summary>Short model name for display, e.g. DeepSeek-V4.1-Flash-ultra.</summary>
    [JsonIgnore] public string ModelShort => Model.Contains('/') ? Model[(Model.LastIndexOf('/') + 1)..] : Model;

    [JsonIgnore] public string? Reasoning => Kind == ReviewerKinds.Claude ? Effort : Thinking;
}

/// <summary>
/// The `web_search` tool Pi reviewers get: the Brave Search API, with its key read from the keys.env entry
/// named by <see cref="Env"/> and passed to every Pi child. Without that key, Pi reviewers only get `web_fetch`.
/// </summary>
public sealed class WebSearchConfig
{
    public string? Env { get; set; } = "BRAVE_API_KEY";
    /// <summary>USD per search call, added to the reviewer's cost. Null: a call that searched has an unknown cost.</summary>
    public double? PricePerCall { get; set; }
}

public sealed class Price
{
    public double Input { get; set; }
    public double Output { get; set; }
    public double? CacheRead { get; set; }
    public double? CacheWrite { get; set; }
}
