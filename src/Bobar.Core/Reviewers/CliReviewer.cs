using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Proc;

namespace Bobar.Core.Reviewers;

/// <summary>Shared plumbing for the Pi and Claude adapters: env, spawning, raw log, error classes.</summary>
public static class CliReviewer
{
    /// <summary>Never passed to any reviewer child (Claude would bill the API key instead of the subscription).</summary>
    public static readonly string[] AnthropicEnv = ["ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN"];

    /// <summary>
    /// Env changes for a reviewer child: removes the Anthropic trio and every keys.env name, then
    /// adds back only the names this reviewer lists in `env`.
    /// </summary>
    public static Dictionary<string, string?> ChildEnv(ReviewerConfig cfg, KeysEnv keys)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in AnthropicEnv) env[k] = null;
        foreach (var k in keys.Values.Keys) env[k] = null;
        foreach (var name in cfg.Env)
        {
            if (!keys.Values.TryGetValue(name, out var v) || string.IsNullOrEmpty(v))
                throw new ReviewerException($"keys.env has no value for {name}, which this reviewer needs (add a line {name}=... to keys.env)");
            env[name] = v;
        }
        return env;
    }

    public static void LogCall(ReviewerCall call, string kind, string what, string file, IEnumerable<string> args)
        => call.RawLog(JsonSerializer.Serialize(new JsonObject
        {
            ["bobar"] = "call",
            ["kind"] = kind,
            ["what"] = what,
            ["t"] = DateTimeOffset.Now.ToString("O"),
            ["argv"] = new JsonArray([file, .. args.Select(a => (JsonNode?)JsonValue.Create(a))]),
        }));

    public static void LogEnd(ReviewerCall call, ProcessOutcome o)
        => call.RawLog(JsonSerializer.Serialize(new JsonObject
        {
            ["bobar"] = "exit",
            ["code"] = o.ExitCode,
            ["timedOut"] = o.TimedOut,
            ["durationMs"] = (long)o.Duration.TotalMilliseconds,
            ["stderr"] = o.StderrTail.Length > 4000 ? o.StderrTail[^4000..] : o.StderrTail,
        }));

    private static readonly Regex TransientRe = new(
        @"\b(429|5\d\d)\b|overloaded|rate.?limit|too many requests|timed? ?out|ECONNRESET|ECONNREFUSED|ETIMEDOUT|ENOTFOUND|EAI_AGAIN|socket hang up|fetch failed|network|temporarily unavailable|bad gateway|service unavailable|internal server error",
        RegexOptions.IgnoreCase);
    private static readonly Regex NotRetryableRe = new(@"insufficient_quota|quota exceeded|billing|\b402\b|\b401\b|\b403\b|invalid.?api.?key|unauthori[sz]ed", RegexOptions.IgnoreCase);

    /// <summary>Provider errors that may go away on their own (Pi's own retries already ran).</summary>
    public static bool IsTransient(string message) => TransientRe.IsMatch(message) && !NotRetryableRe.IsMatch(message);

    public static JsonObject? ParseLine(string line)
    {
        if (line.Length == 0 || line[0] != '{') return null;
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static string? Str(this JsonNode? n, string name) => n is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long Num(this JsonNode? n, string name)
    {
        if (n is not JsonObject o || o[name] is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return 0;
    }

    /// <summary>Joins the text parts of a content array (or returns a string as is).</summary>
    public static string TextParts(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (content is not JsonArray arr) return "";
        return string.Concat(arr.OfType<JsonObject>().Where(p => p.Str("type") == "text").Select(p => p.Str("text") ?? ""));
    }

    public static ReviewerResult Result(ReviewerCall call, string finalText, JsonNode? parsed, TokenUsage usage, string requestedModel,
        List<string> resolved, string? reqReasoning, string? effReasoning, string? sessionId, TimeSpan duration) => new()
    {
        Call = call,
        FinalText = finalText,
        Parsed = parsed ?? Validation.ParseModelJson(finalText),
        Usage = usage,
        RequestedModel = requestedModel,
        ResolvedModels = resolved,
        RequestedReasoning = reqReasoning,
        EffectiveReasoning = effReasoning,
        SessionId = sessionId,
        Duration = duration,
    };
}
