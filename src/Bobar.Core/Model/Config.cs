using System.Text.Json.Serialization;

namespace Bobar.Core.Model;

/// <summary>%APPDATA%\BobarCode\config.json (§4). Read fresh on every CLI call.</summary>
public sealed class AppConfig
{
    public int MaxRounds { get; set; } = 5;
    /// <summary>A double so tests (and impatient users) can use fractions of a minute.</summary>
    public double ReviewerTimeoutMinutes { get; set; } = 30;
    public bool OpenGuiOnDecisions { get; set; } = true;
    public Dictionary<string, ReviewerConfig> Reviewers { get; set; } = new();
    /// <summary>USD per 1M tokens, keyed "provider/model".</summary>
    public Dictionary<string, Price> Prices { get; set; } = new();

    public static AppConfig CreateDefault() => new()
    {
        Reviewers = new()
        {
            ["ds"] = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "lithos", Model = "deepseek-ai/DeepSeek-V4.1-Flash-ultra", Thinking = "high", Env = ["LITHOS_API_KEY"] },
            ["sol"] = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "openai", Model = "gpt-6.1-sol", Thinking = "medium", Env = ["OPENAI_API_KEY"] },
            ["opus"] = new ReviewerConfig { Kind = ReviewerKinds.Claude, Model = "opus", Effort = "medium" },
        },
        Prices = new()
        {
            ["lithos/deepseek-ai/DeepSeek-V4.1-Flash-ultra"] = new Price { Input = 0.35, Output = 1.4, CacheRead = 0.007 },
            ["openai/gpt-6.1-sol"] = new Price { Input = 2, Output = 10, CacheRead = 0.1, CacheWrite = 2.5 },
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

    /// <summary>The "provider/model" key into the price table.</summary>
    [JsonIgnore] public string PriceKey => Kind == ReviewerKinds.Claude ? $"anthropic/{Model}" : $"{Provider}/{Model}";

    /// <summary>Short model name for display, e.g. DeepSeek-V4.1-Flash-ultra.</summary>
    [JsonIgnore] public string ModelShort => Model.Contains('/') ? Model[(Model.LastIndexOf('/') + 1)..] : Model;

    [JsonIgnore] public string? Reasoning => Kind == ReviewerKinds.Claude ? Effort : Thinking;
}

public sealed class Price
{
    public double Input { get; set; }
    public double Output { get; set; }
    public double? CacheRead { get; set; }
    public double? CacheWrite { get; set; }
}
