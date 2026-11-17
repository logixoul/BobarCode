using System.Text;
using System.Text.Json.Nodes;

namespace Bobar.Core.Reviewers;

/// <summary>
/// Turns raw reviewer.jsonl lines (Pi, Claude, fake, and BobarCode's own markers) into readable
/// activity, for the GUI's Details modal. Line types don't overlap between Pi and Claude, so no
/// kind is needed.
/// </summary>
public static class ReviewerLogFormatter
{
    private const int MaxChars = 1500;

    public static IEnumerable<ReviewerActivity> Describe(JsonObject o)
    {
        if (o.Str("bobar") is { } marker)
        {
            if (marker == "call") yield return new(ActivityKind.Info, $"── {o.Str("what")} ({o.Str("kind")}) at {Time(o.Str("t"))} ──");
            else if (marker == "exit")
            {
                var timedOut = o["timedOut"] is JsonValue tv && tv.TryGetValue<bool>(out var t) && t;
                var err = o.Str("stderr")?.Trim();
                yield return new(ActivityKind.Info, $"── process {(timedOut ? "timed out" : $"exited with code {o["code"]?.ToJsonString() ?? "?"}")} after {o.Num("durationMs") / 1000}s ──");
                if (!string.IsNullOrEmpty(err)) yield return new(ActivityKind.Error, "stderr: " + Cut(err));
            }
            yield break;
        }

        switch (o.Str("type"))
        {
            // ── Pi ──
            case "session":
                yield return new(ActivityKind.Info, $"session {o.Str("id")}");
                break;
            case "message_end" when o["message"] is JsonObject m && m.Str("role") == "assistant":
                foreach (var a in Parts(m["content"])) yield return a;
                var stop = m.Str("stopReason");
                if (stop is "error" or "aborted" or "length")
                    yield return new(ActivityKind.Error, $"stopReason={stop}: {m.Str("errorMessage")}");
                break;
            case "tool_execution_end":
            {
                var isError = o["isError"] is JsonValue iv && iv.TryGetValue<bool>(out var ie) && ie;
                var text = CliReviewer.TextParts(o["result"]?["content"]);
                yield return new(isError ? ActivityKind.Error : ActivityKind.ToolResult, $"[{o.Str("toolName")} result{(isError ? ", error" : "")}] {Cut(text, 300)}");
                break;
            }
            case "auto_retry_start":
                yield return new(ActivityKind.Retry, $"[retry {o.Num("attempt")}/{o.Num("maxAttempts")} in {o.Num("delayMs") / 1000.0:0.#}s] {o.Str("errorMessage")}");
                break;
            case "auto_retry_end":
                yield return new(ActivityKind.Retry, $"[retry {o.Num("attempt")} {(o["success"]?.ToJsonString() == "true" ? "succeeded" : "failed")}] {o.Str("finalError")}");
                break;
            case "agent_settled":
                yield return new(ActivityKind.Info, $"── settled{(o["aborted"]?.ToJsonString() == "true" ? " (aborted)" : "")} ──");
                break;

            // ── Claude ──
            case "system" when o.Str("subtype") == "init":
                yield return new(ActivityKind.Info, $"init: model {o.Str("model")}, apiKeySource {o["apiKeySource"]?.ToJsonString()}");
                break;
            case "rate_limit_event":
                var info = o["rate_limit_info"];
                var overage = info.Str("overageStatus");
                yield return new(ActivityKind.Info, $"rate limit: {info.Str("status")}{(overage is null ? "" : $", overage {overage}")}");
                break;
            case "assistant":
                foreach (var a in Parts(o["message"]?["content"])) yield return a;
                break;
            case "user" when o["message"] is JsonObject um && um["content"] is JsonArray content:
                foreach (var p in content.OfType<JsonObject>().Where(p => p.Str("type") == "tool_result"))
                {
                    var isError = p["is_error"] is JsonValue ev && ev.TryGetValue<bool>(out var e) && e;
                    yield return new(isError ? ActivityKind.Error : ActivityKind.ToolResult, $"[result{(isError ? ", error" : "")}] {Cut(CliReviewer.TextParts(p["content"]), 300)}");
                }
                break;
            case "result":
                var u = o["usage"];
                yield return new(o["is_error"]?.ToJsonString() == "true" ? ActivityKind.Error : ActivityKind.Info,
                    $"── result: {o.Str("subtype")}, {u.Num("input_tokens")} in / {u.Num("output_tokens")} out / {u.Num("cache_read_input_tokens")} cached ──");
                if (o["is_error"]?.ToJsonString() == "true") yield return new(ActivityKind.Error, Cut(o.Str("result") ?? ""));
                break;

            // ── fake ──
            case "fake_step":
                yield return new(ActivityKind.Text, o.Str("fail") is { } f ? $"[fake failure] {f}" : $"[fake output] {Cut(o.Str("output") ?? "")}");
                break;
        }
    }

    private static IEnumerable<ReviewerActivity> Parts(JsonNode? content)
    {
        if (content is not JsonArray arr) yield break;
        foreach (var p in arr.OfType<JsonObject>())
        {
            switch (p.Str("type"))
            {
                case "text" when !string.IsNullOrWhiteSpace(p.Str("text")):
                    yield return new(ActivityKind.Text, p.Str("text")!.Trim());
                    break;
                case "thinking" when !string.IsNullOrWhiteSpace(p.Str("thinking")):
                    yield return new(ActivityKind.Thinking, "[thinking] " + Cut(p.Str("thinking")!.Trim()));
                    break;
                case "toolCall":
                    yield return new(ActivityKind.ToolCall, $"[{p.Str("name")}] {Cut(p["arguments"]?.ToJsonString() ?? "", 400)}");
                    break;
                case "tool_use":
                    yield return new(ActivityKind.ToolCall, $"[{p.Str("name")}] {Cut(p["input"]?.ToJsonString() ?? "", 400)}");
                    break;
            }
        }
    }

    /// <summary>The whole file, rendered. Non-JSON lines (shouldn't happen) are shown as they are.</summary>
    public static string Render(string fileText)
    {
        var sb = new StringBuilder();
        foreach (var raw in fileText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var o = CliReviewer.ParseLine(line);
            if (o is null)
            {
                sb.Append(Cut(line, 300)).Append('\n');
                continue;
            }
            foreach (var a in Describe(o)) sb.Append(a.Text).Append('\n');
        }
        return sb.ToString();
    }

    private static string Cut(string s, int max = MaxChars) => s.Length <= max ? s : s[..max] + $"… [{s.Length - max} more chars]";

    private static string Time(string? iso) => DateTimeOffset.TryParse(iso, out var t) ? t.ToLocalTime().ToString("HH:mm:ss") : iso ?? "?";
}
