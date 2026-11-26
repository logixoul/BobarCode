using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Proc;

namespace Bobar.Core.Reviewers;

/// <summary>Claude Code (`kind: "claude"`), `claude -p` on the subscription login (§9, INFRA_FINDINGS §2).</summary>
public sealed class ClaudeReviewer(ReviewerContext ctx) : IReviewer
{
    public Task<ReviewerResult> RunAsync(ReviewerCall call, CancellationToken ct)
        => InvokeAsync(call, call.ResumeSessionId, call.Prompt, call.ResumeSessionId is null ? "run" : "resume", ct);

    public Task<ReviewerResult> RepairAsync(ReviewerResult previous, string error, CancellationToken ct)
        => InvokeAsync(previous.Call, previous.SessionId, error, "repair", ct);

    public const string FileTools = "Read,Grep,Glob";
    /// <summary>Also pre-approved with --allowedTools: dontAsk would otherwise deny them (they ask for permission).</summary>
    public const string WebTools = "WebSearch,WebFetch";

    /// <summary>Never `--bare`: it forces API-key auth and ignores the subscription login.</summary>
    /// <param name="fileTools">Read-only file tools as well as the web tools (off for a discussion without a folder).</param>
    /// <param name="jsonSchema">The output schema (default: review findings).</param>
    public static List<string> Args(ReviewerConfig cfg, string runDir, string sessionId, bool resume, bool fileTools = true, string? jsonSchema = null)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--model", cfg.Model, "--effort", cfg.Effort ?? "medium" };
        args.AddRange(resume ? ["-r", sessionId] : ["--session-id", sessionId]);
        args.AddRange([
            "--tools", fileTools ? $"{FileTools},{WebTools}" : WebTools, "--allowedTools", WebTools, "--permission-mode", "dontAsk",
            "--setting-sources", "", "--strict-mcp-config", "--add-dir", runDir,
            "--json-schema", (jsonSchema ?? Validation.ReviewerSchema).Trim(),
        ]);
        return args;
    }

    private async Task<ReviewerResult> InvokeAsync(ReviewerCall call, string? resumeId, string prompt, string what, CancellationToken ct)
    {
        var cfg = ctx.Config;
        var exe = ProcessRunner.FindOnPath(string.IsNullOrWhiteSpace(cfg.Command) ? "claude" : cfg.Command)
                  ?? throw new ReviewerException($"could not find {cfg.Command ?? "claude"} on PATH");
        // Claude gets no keys.env values at all, and never the Anthropic API env.
        var env = CliReviewer.ChildEnv(new ReviewerConfig { Kind = cfg.Kind, Env = [] }, ctx.Keys);
        var sessionId = resumeId ?? Guid.NewGuid().ToString();
        var args = Args(cfg, call.RunDir, sessionId, resumeId is not null, call.FileTools, call.JsonSchema);
        CliReviewer.LogCall(call, ReviewerKinds.Claude, what, exe, args);

        var parser = new ClaudeStream();
        using var fatal = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ProcessOutcome outcome;
        try
        {
            outcome = await ProcessRunner.RunAsync(new ProcessSpec(exe, args, call.RepoDir) { Env = env, Stdin = prompt }, line =>
            {
                call.RawLog(line);
                if (CliReviewer.ParseLine(line) is not { } o) return;
                parser.Record(o);
                if (parser.FatalError is not null) fatal.Cancel(); // kills the tree
            }, fatal.Token);
        }
        catch (OperationCanceledException) when (parser.FatalError is not null && !ct.IsCancellationRequested)
        {
            throw new ReviewerException(parser.FatalError, usage: parser.Usage, final: true);
        }
        CliReviewer.LogEnd(call, outcome);

        if (parser.Result is not { } r)
        {
            var detail = $"claude exited (code {outcome.ExitCode}) without a result{(outcome.StderrTail.Trim().Length > 0 ? "; stderr: " + outcome.StderrTail.Trim() : "")}";
            throw new ReviewerException(parser.QuotaMessage ?? detail, parser.QuotaMessage is null && CliReviewer.IsTransient(outcome.StderrTail), usage: parser.Usage, final: parser.QuotaMessage is not null || CliReviewer.IsNotRetryable(outcome.StderrTail));
        }
        var isError = r["is_error"] is JsonValue ie && ie.TryGetValue<bool>(out var b) && b;
        var subtype = r.Str("subtype");
        if (isError || (subtype is not null && subtype != "success"))
        {
            var msg = string.Join(": ", new[] { subtype, r.Str("result"), r["errors"]?.ToJsonString() }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (msg.Length == 0) msg = "Claude Code reported an error";
            var status = r.Num("api_error_status");
            // Quota: never retried (INFRA_FINDINGS §2).
            if (parser.QuotaMessage is not null || status == 429 || Regex.IsMatch(msg, "usage limit|rate limit|limit reached|spend limit", RegexOptions.IgnoreCase))
                throw new ReviewerException(parser.QuotaMessage ?? $"Claude quota exhausted: {msg}", usage: parser.Usage, final: true);
            throw new ReviewerException($"claude: {msg}", status >= 500 || (isError && CliReviewer.IsTransient(msg)), usage: parser.Usage,
                final: status is 401 or 402 or 403 || CliReviewer.IsNotRetryable(msg));
        }

        var text = r.Str("result") ?? "";
        var structured = r["structured_output"]?.DeepClone();
        return CliReviewer.Result(call, text, structured, parser.Usage, cfg.Model, parser.Models, cfg.Effort, null, sessionId, outcome.Duration);
    }
}

/// <summary>Claude Code stream-json → result data (INFRA_FINDINGS §2 "Stream").</summary>
public sealed class ClaudeStream
{
    public JsonObject? Result { get; private set; }
    public TokenUsage Usage { get; private set; } = new();
    public List<string> Models { get; } = [];
    /// <summary>Set when the stream must be stopped (wrong auth source).</summary>
    public string? FatalError { get; private set; }
    public string? QuotaMessage { get; private set; }

    public void Record(JsonObject o)
    {
        switch (o.Str("type"))
        {
            case "system" when o.Str("subtype") == "init":
                AddModel(o.Str("model"));
                var src = o["apiKeySource"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o["apiKeySource"]?.ToJsonString() ?? "missing";
                if (src != "none")
                    FatalError = $"Claude Code reported apiKeySource={src}; BobarCode only runs Claude on the subscription login (expected \"none\")";
                break;
            case "rate_limit_event":
                var info = o["rate_limit_info"];
                var status = info.Str("status") ?? "";
                var overage = info.Str("overageStatus");
                var usingOverage = info is JsonObject io && io["isUsingOverage"] is JsonValue ov && ov.TryGetValue<bool>(out var u) && u;
                if (!status.StartsWith("allowed", StringComparison.Ordinal) && !usingOverage && overage != "allowed")
                {
                    var resets = info.Num("resetsAt");
                    QuotaMessage = resets > 0
                        ? $"Claude quota exhausted until {DateTimeOffset.FromUnixTimeSeconds(resets).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}"
                        : "Claude quota exhausted";
                }
                break;
            case "assistant":
                AddModel(o["message"].Str("model"));
                break;
            case "result":
                Result = o;
                var us = o["usage"];
                Usage = new TokenUsage
                {
                    Input = us.Num("input_tokens"),
                    Output = us.Num("output_tokens"),
                    CacheRead = us.Num("cache_read_input_tokens"),
                    CacheWrite = us.Num("cache_creation_input_tokens"),
                };
                if (o["modelUsage"] is JsonObject mu)
                    foreach (var (k, _) in mu) AddModel(k);
                break;
        }
    }

    private void AddModel(string? m)
    {
        if (!string.IsNullOrEmpty(m) && !Models.Contains(m)) Models.Add(m);
    }
}
