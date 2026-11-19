using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bobar.Core.IO;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Proc;

namespace Bobar.Core.Reviewers;

/// <summary>Pi (`kind: "pi"`), spawned as node + Pi's JS entry (§9, INFRA_FINDINGS §3).</summary>
public sealed class PiReviewer(ReviewerContext ctx) : IReviewer
{
    public static readonly string[] Tools = ["read", "grep", "find", "ls"];

    /// <summary>Pi's own provider- and agent-level retries (INFRA_FINDINGS §3).</summary>
    public const string AgentSettings = """
        {
          "retry": {
            "enabled": true,
            "maxRetries": 4,
            "baseDelayMs": 2000,
            "maxAgentDelayMs": 60000,
            "provider": { "maxRetries": 4, "maxRetryDelayMs": 60000 }
          }
        }

        """;

    /// <summary>
    /// Providers Pi doesn't ship (copied from Fractory's PI_MODELS). `$LITHOS_API_KEY` is Pi's env
    /// interpolation: the file holds no secret. Lithos rejects undeclared request fields with 400,
    /// hence every optional OpenAI field off. The `openai` entry adds GPT-6.2 Sol to Pi's built-in
    /// provider (Pi 1.5.2's catalog lacks it; metadata copied from 6.1 Sol), and the `google` entry adds
    /// Gemini 4 Hydrogen the same way. Drop each once Pi ships it.
    /// </summary>
    public const string Models = """
        {
          "providers": {
            "lithos": {
              "name": "LithosAI",
              "baseUrl": "https://api.lithosai.cloud/v1",
              "api": "openai-completions",
              "apiKey": "$LITHOS_API_KEY",
              "authHeader": true,
              "compat": {
                "supportsStore": false,
                "supportsDeveloperRole": false,
                "supportsReasoningEffort": true,
                "supportsUsageInStreaming": true,
                "maxTokensField": "max_tokens",
                "thinkingFormat": "openai",
                "sendSessionAffinityHeaders": false,
                "supportsLongCacheRetention": false
              },
              "models": [
                {
                  "id": "deepseek-ai/DeepSeek-V4.1-Flash-ultra",
                  "name": "DeepSeek V4.1 Flash Ultra (LithosAI)",
                  "reasoning": true,
                  "input": ["text"],
                  "contextWindow": 1048576,
                  "maxTokens": 262144,
                  "thinkingLevelMap": { "off": "none", "minimal": null, "low": "low", "medium": null, "high": "high", "xhigh": null, "max": "max" },
                  "cost": { "input": 0.35, "output": 1.4, "cacheRead": 0.007, "cacheWrite": 0 }
                }
              ]
            },
            "openai": {
              "models": [
                {
                  "id": "gpt-6.2-sol",
                  "name": "GPT-6.2 Sol",
                  "api": "openai-responses",
                  "reasoning": true,
                  "input": ["text", "image"],
                  "contextWindow": 272000,
                  "maxTokens": 128000,
                  "thinkingLevelMap": { "off": null, "minimal": null, "low": "low", "medium": "medium", "high": "high", "xhigh": "xhigh", "max": "max" },
                  "cost": { "input": 2, "output": 10, "cacheRead": 0.1, "cacheWrite": 2.5 }
                }
              ]
            },
            "google": {
              "models": [
                {
                  "id": "gemini-4-hydrogen",
                  "name": "Gemini 4 Hydrogen",
                  "api": "google-generative-ai",
                  "reasoning": true,
                  "input": ["text", "image"],
                  "contextWindow": 1048576,
                  "maxTokens": 65536,
                  "thinkingLevelMap": { "off": null, "minimal": null, "low": "low", "medium": "medium", "high": "high", "xhigh": "xhigh", "max": null },
                  "cost": { "input": 1.5, "output": 7.5, "cacheRead": 0.075, "cacheWrite": 0 }
                }
              ]
            }
          }
        }

        """;

    public Task<ReviewerResult> RunAsync(ReviewerCall call, IProgress<ReviewerActivity> activity, CancellationToken ct)
        => InvokeAsync(call, null, call.Prompt, "run", activity, ct);

    public Task<ReviewerResult> RepairAsync(ReviewerResult previous, string error, IProgress<ReviewerActivity> activity, CancellationToken ct)
        => InvokeAsync(previous.Call, previous.SessionId, error, "repair", activity, ct);

    public static List<string> Args(ReviewerConfig cfg, string sessionDir, string? sessionId)
    {
        var args = new List<string>
        {
            "--mode", "json", "--provider", cfg.Provider ?? "", "--model", cfg.Model, "--thinking", cfg.Thinking ?? "medium",
            "--no-extensions", "--no-skills", "--no-prompt-templates", "--no-mcp", "--no-context-files",
            "--tools", string.Join(',', Tools), "--session-dir", sessionDir,
        };
        if (sessionId is not null) args.AddRange(["--session", sessionId]);
        return args;
    }

    private async Task<ReviewerResult> InvokeAsync(ReviewerCall call, string? sessionId, string prompt, string what, IProgress<ReviewerActivity> activity, CancellationToken ct)
    {
        var cfg = ctx.Config;
        var (node, js) = ResolveLauncher(cfg.Command);
        EnsureAgentDir(ctx.Paths.PiAgentDir);
        Directory.CreateDirectory(ctx.Paths.PiSessionsDir);
        var env = CliReviewer.ChildEnv(cfg, ctx.Keys);
        env["PI_CODING_AGENT_DIR"] = ctx.Paths.PiAgentDir;

        List<string> args = [js, .. Args(cfg, ctx.Paths.PiSessionsDir, sessionId)];
        CliReviewer.LogCall(call, ReviewerKinds.Pi, what, node, args);
        var parser = new PiStream();
        var outcome = await ProcessRunner.RunAsync(new ProcessSpec(node, args, call.RepoDir) { Env = env, Stdin = prompt }, line =>
        {
            call.RawLog(line);
            if (CliReviewer.ParseLine(line) is { } o)
                foreach (var a in parser.Record(o)) activity.Report(a);
        }, ct);
        CliReviewer.LogEnd(call, outcome);

        var requested = $"{cfg.Provider}/{cfg.Model}";
        if (outcome.TimedOut) throw new ReviewerException("pi timed out", timedOut: true, usage: parser.Usage);
        var last = parser.LastAssistant;
        if (last is not null)
        {
            var stop = last.Str("stopReason");
            if (stop is "error" or "aborted" or "length")
            {
                var msg = $"pi stopReason={stop}: {last.Str("errorMessage") ?? "no error message"}";
                throw new ReviewerException(msg, stop == "error" && CliReviewer.IsTransient(msg), usage: parser.Usage);
            }
        }
        if (!parser.Settled || last is null)
        {
            var detail = $"pi exited (code {outcome.ExitCode?.ToString() ?? "none"}) without a final result{(outcome.StderrTail.Trim().Length > 0 ? "; stderr: " + outcome.StderrTail.Trim() : "")}";
            throw new ReviewerException(detail, CliReviewer.IsTransient(outcome.StderrTail), usage: parser.Usage);
        }
        if (parser.Aborted) throw new ReviewerException("pi reported the agent run as aborted", usage: parser.Usage);

        var text = CliReviewer.TextParts(last["content"]);
        return CliReviewer.Result(call, text, null, parser.Usage, requested, parser.Models, cfg.Thinking, parser.ThinkingLevel, parser.SessionId ?? sessionId, outcome.Duration);
    }

    public static void EnsureAgentDir(string dir)
    {
        Directory.CreateDirectory(dir);
        WriteIfChanged(Path.Combine(dir, "settings.json"), AgentSettings);
        WriteIfChanged(Path.Combine(dir, "models.json"), Models);
    }

    private static void WriteIfChanged(string file, string want)
    {
        string? have = null;
        try { have = File.ReadAllText(file); } catch (IOException) { }
        if (have != want) RunFiles.WriteTextAtomic(file, want, Redactor.None);
    }

    /// <summary>
    /// `pi` is an npm .cmd shim; going through cmd.exe mangles arguments. Resolve it to node + the
    /// JS entry it runs. `command` may also point at the .cmd or the .js directly.
    /// </summary>
    public static (string Node, string Js) ResolveLauncher(string? command)
    {
        var name = string.IsNullOrWhiteSpace(command) ? "pi" : command;
        if (name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
            return (FindNode(null), Path.GetFullPath(name));
        var shim = name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? ProcessRunner.FindOnPath(name)
            : ProcessRunner.FindOnPath(name + ".cmd");
        if (shim is null) throw new ReviewerException($"could not find {name}.cmd on PATH (is Pi installed? `npm i -g @earendil-works/pi-coding-agent`)");
        var dir = Path.GetDirectoryName(shim)!;
        var m = Regex.Match(File.ReadAllText(shim), @"%(?:dp0|~dp0)%\\?(?<rel>[^""%]+?\.m?js)", RegexOptions.IgnoreCase);
        if (!m.Success) throw new ReviewerException($"could not find Pi's JS entry in {shim}");
        var js = Path.GetFullPath(Path.Combine(dir, m.Groups["rel"].Value.TrimStart('\\')));
        if (!File.Exists(js)) throw new ReviewerException($"Pi's JS entry {js} (from {shim}) does not exist");
        return (FindNode(dir), js);
    }

    private static string FindNode(string? shimDir)
    {
        if (shimDir is not null)
        {
            var local = Path.Combine(shimDir, "node.exe");
            if (File.Exists(local)) return local;
        }
        return ProcessRunner.FindOnPath("node") ?? throw new ReviewerException("could not find node on PATH");
    }
}

/// <summary>Pi `--mode json` stream → result data and activity (INFRA_FINDINGS §3 "Stream").</summary>
public sealed class PiStream
{
    public string? SessionId { get; private set; }
    public JsonObject? LastAssistant { get; private set; }
    public TokenUsage Usage { get; private set; } = new();
    public List<string> Models { get; } = [];
    public string? ThinkingLevel { get; private set; }
    public bool Settled { get; private set; }
    public bool Aborted { get; private set; }

    public IEnumerable<ReviewerActivity> Record(JsonObject o)
    {
        switch (o.Str("type"))
        {
            case "session":
                SessionId = o.Str("id");
                break;
            case "message_end":
                if (o["message"] is JsonObject m && m.Str("role") == "assistant")
                {
                    LastAssistant = m;
                    var u = m["usage"];
                    // Sum per message_end; message_update usage is cumulative and would double-count.
                    Usage += new TokenUsage { Input = u.Num("input"), Output = u.Num("output"), CacheRead = u.Num("cacheRead"), CacheWrite = u.Num("cacheWrite") };
                    var key = $"{m.Str("provider") ?? "unknown"}/{m.Str("model") ?? "unknown"}";
                    if (!Models.Contains(key)) Models.Add(key);
                    if (m.Str("thinkingLevel") is { } t) ThinkingLevel = t;
                }
                break;
            case "agent_settled":
                Settled = true;
                Aborted = o["aborted"] is JsonValue av && av.TryGetValue<bool>(out var ab) && ab;
                break;
        }
        return ReviewerLogFormatter.Describe(o);
    }
}
