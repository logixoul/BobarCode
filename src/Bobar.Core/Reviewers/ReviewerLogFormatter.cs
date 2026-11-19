using System.Text;
using System.Text.Json.Nodes;

namespace Bobar.Core.Reviewers;

/// <summary>
/// Turns raw reviewer.jsonl lines (Pi, Claude, fake, and BobarCode's own markers) into readable
/// activity, for the GUI's Details modal. Line types don't overlap between Pi and Claude, so no
/// kind is needed. Bulky content (tool results, thinking, long tool arguments) is folded into a
/// one-line headline plus a <see cref="ReviewerActivity.Detail"/> that the GUI shows collapsed.
/// </summary>
public static class ReviewerLogFormatter
{
    private const int MaxChars = 1500;
    private const int HeadChars = 160;
    private const int MaxDetailChars = 20_000;

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
                if (!string.IsNullOrEmpty(err)) yield return Fold(ActivityKind.Error, "stderr:", err);
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
                yield return Fold(isError ? ActivityKind.Error : ActivityKind.ToolResult, $"[{o.Str("toolName")} result{(isError ? ", error" : "")}]", text);
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
                    yield return Fold(isError ? ActivityKind.Error : ActivityKind.ToolResult, $"[result{(isError ? ", error" : "")}]", CliReviewer.TextParts(p["content"]));
                }
                break;
            case "result":
                var u = o["usage"];
                yield return new(o["is_error"]?.ToJsonString() == "true" ? ActivityKind.Error : ActivityKind.Info,
                    $"── result: {o.Str("subtype")}, {u.Num("input_tokens")} in / {u.Num("output_tokens")} out / {u.Num("cache_read_input_tokens")} cached ──");
                if (o["is_error"]?.ToJsonString() == "true") yield return Fold(ActivityKind.Error, "", o.Str("result") ?? "");
                break;

            // ── fake ──
            case "fake_step":
                yield return o.Str("fail") is { } f ? new(ActivityKind.Error, $"[fake failure] {f}") : Fold(ActivityKind.Text, "[fake output]", o.Str("output") ?? "");
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
                    yield return Fold(ActivityKind.Thinking, "[thinking]", p.Str("thinking")!);
                    break;
                case "toolCall":
                    yield return Call(p.Str("name"), p["arguments"]);
                    break;
                case "tool_use":
                    yield return Call(p.Str("name"), p["input"]);
                    break;
            }
        }
    }

    /// <summary>A tool call: compact JSON on the headline; when that's too long, the arguments as
    /// "key: value" lines with strings unescaped, so a multi-line argument reads as it was written.</summary>
    private static ReviewerActivity Call(string? name, JsonNode? args)
    {
        var detail = args is JsonObject obj
            ? string.Join("\n", obj.Select(kv =>
                $"{kv.Key}: {(kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? (s.Contains('\n') ? "\n" + s : s) : kv.Value?.ToJsonString())}"))
            : null;
        return Fold(ActivityKind.ToolCall, $"[{name}]", args?.ToJsonString() ?? "", detail);
    }

    /// <summary>The label and the content's first line; the whole content (or <paramref name="detail"/>)
    /// becomes the detail when it doesn't fit on that line.</summary>
    private static ReviewerActivity Fold(ActivityKind kind, string label, string content, string? detail = null)
    {
        content = content.Trim();
        var firstLine = content.Split('\n', 2)[0].TrimEnd('\r');
        var fits = firstLine.Length == content.Length && content.Length <= HeadChars;
        var preview = fits ? content : (firstLine.Length > HeadChars ? firstLine[..HeadChars] : firstLine) + " …";
        return new(kind, $"{label} {preview}".Trim(), fits ? null : Cut(detail ?? content, MaxDetailChars));
    }

    /// <summary>The whole file as activities. Non-JSON lines (shouldn't happen) are shown as they are.</summary>
    public static IEnumerable<ReviewerActivity> Parse(string fileText)
    {
        foreach (var raw in fileText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (CliReviewer.ParseLine(line) is { } o)
                foreach (var a in Describe(o)) yield return a;
            else
                yield return new(ActivityKind.Info, Cut(line, 300));
        }
    }

    /// <summary>The whole file as plain text, details indented under their headlines.</summary>
    public static string Render(string fileText)
    {
        var sb = new StringBuilder();
        foreach (var a in Parse(fileText))
        {
            sb.Append(a.Text).Append('\n');
            if (a.Detail is null) continue;
            foreach (var l in a.Detail.Split('\n')) sb.Append("    ").Append(l.TrimEnd('\r')).Append('\n');
        }
        return sb.ToString();
    }

    private static string Cut(string s, int max = MaxChars) => s.Length <= max ? s : s[..max] + $"… [{s.Length - max} more chars]";

    private static string Time(string? iso) => DateTimeOffset.TryParse(iso, out var t) ? t.ToLocalTime().ToString("HH:mm:ss") : iso ?? "?";
}
