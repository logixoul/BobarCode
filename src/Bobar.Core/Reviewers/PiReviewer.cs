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
    /// hence every optional OpenAI field off. The `openrouter` entry adds GPT-6.2 Sol, Gemini 4 Hydrogen and Neon
    /// to Pi's built-in OpenRouter provider (Pi 1.5.2's OpenRouter catalog lacks them; metadata and compat copied
    /// from its 6.1 Sol and Gemini 4 Argon entries, Gemini max output a guess), and Qwen4 Max (compat from Qwen3.8 Max).
    /// The GLM-5.3 entry overrides Pi's own to pin the host (`openRouterRouting`): Z.AI, else Together, never the
    /// cheap fp4 hosts. Drop each once Pi ships it (keep GLM's pinning).
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
            "openrouter": {
              "models": [
                {
                  "id": "openai/gpt-6.2-sol",
                  "name": "OpenAI: GPT-6.2 Sol",
                  "api": "openai-completions",
                  "reasoning": true,
                  "input": ["text", "image"],
                  "contextWindow": 1050000,
                  "maxTokens": 128000,
                  "compat": { "thinkingFormat": "openrouter", "supportsStrictMode": true, "sendSessionAffinityHeaders": true, "supportsMidConvoSystemMessages": true },
                  "thinkingLevelMap": { "off": null, "minimal": null, "low": "low", "medium": "medium", "high": "high", "xhigh": "xhigh", "max": "max" },
                  "cost": { "input": 2, "output": 10, "cacheRead": 0.1, "cacheWrite": 2.5 }
                },
                {
                  "id": "google/gemini-4-hydrogen",
                  "name": "Google: Gemini 4 Hydrogen",
                  "api": "openai-completions",
                  "reasoning": true,
                  "input": ["text", "image"],
                  "contextWindow": 1048576,
                  "maxTokens": 65536,
                  "compat": { "supportsDeveloperRole": false, "thinkingFormat": "openrouter", "supportsStrictMode": true, "sendSessionAffinityHeaders": true },
                  "thinkingLevelMap": { "off": null, "minimal": null, "low": "low", "medium": "medium", "high": "high", "xhigh": null, "max": null },
                  "cost": { "input": 1.5, "output": 7.5, "cacheRead": 0.075, "cacheWrite": 0 }
                },
                {
                  "id": "google/gemini-4-neon",
                  "name": "Google: Gemini 4 Neon",
                  "api": "openai-completions",
                  "reasoning": true,
                  "input": ["text", "image"],
                  "contextWindow": 1048576,
                  "maxTokens": 65536,
                  "compat": { "supportsDeveloperRole": false, "thinkingFormat": "openrouter", "supportsStrictMode": true, "sendSessionAffinityHeaders": true },
                  "thinkingLevelMap": { "off": null, "minimal": null, "low": "low", "medium": "medium", "high": "high", "xhigh": null, "max": null },
                  "cost": { "input": 3, "output": 15, "cacheRead": 0.15, "cacheWrite": 0 }
                },
                {
                  "id": "qwen/qwen4-max",
                  "name": "Qwen: Qwen4 Max",
                  "api": "openai-completions",
                  "reasoning": true,
                  "input": ["text", "image"],
                  "contextWindow": 1000000,
                  "maxTokens": 131072,
                  "compat": { "supportsDeveloperRole": false, "thinkingFormat": "openrouter", "supportsStrictMode": true, "sendSessionAffinityHeaders": true },
                  "thinkingLevelMap": { "off": null, "minimal": "minimal", "low": "low", "medium": "medium", "high": "high", "xhigh": "xhigh", "max": null },
                  "cost": { "input": 1.425, "output": 4.275, "cacheRead": 0.07125, "cacheWrite": 0 }
                },
                {
                  "id": "z-ai/glm-5.3",
                  "name": "Z.ai: GLM 5.3 (Z.AI, else Together)",
                  "api": "openai-completions",
                  "reasoning": true,
                  "input": ["text"],
                  "contextWindow": 1048576,
                  "maxTokens": 131072,
                  "compat": {
                    "supportsDeveloperRole": false, "thinkingFormat": "openrouter", "supportsStrictMode": true, "sendSessionAffinityHeaders": true,
                    "openRouterRouting": { "order": ["z-ai", "together"], "allow_fallbacks": false }
                  },
                  "thinkingLevelMap": { "off": null, "minimal": null, "low": "low", "medium": null, "high": "high", "xhigh": null, "max": "max" },
                  "cost": { "input": 1.4, "output": 4.4, "cacheRead": 0.26, "cacheWrite": 0 }
                }
              ]
            }
          }
        }

        """;

    public const string ReportTool = "report_finding";
    public const string ReportExtensionFile = "bobar-report-finding.js";

    /// <summary>
    /// The `report_finding` tool, loaded with `-e` (Pi's extension API, INFRA_FINDINGS §3). It writes
    /// nothing: it checks the finding, numbers it and returns it in `details`, and the CLI picks it up
    /// from the `tool_execution_end` event (so the CLI owns findings.jsonl and redacts it). A thrown
    /// error goes back to the model as a failed call, which it can correct.
    /// </summary>
    public const string ReportExtension = """
        // Written by BobarCode (bobar-cli). The report_finding tool for streaming reviews.
        const prefix = process.env.BOBAR_ID_PREFIX || `R${process.env.BOBAR_ROUND || "1"}`;
        const earlier = (process.env.BOBAR_EARLIER_IDS || "").split(",").filter(Boolean);
        let n = Math.max(0, (parseInt(process.env.BOBAR_FIRST_N || "1", 10) || 1) - 1);
        module.exports = function (pi) {
          pi.registerTool({
            name: "report_finding",
            label: "Report finding",
            description: "Report one review finding to the author. Call it once per finding, as soon as you have checked it; the author starts on it right away. Returns the finding's id.",
            parameters: {
              type: "object",
              properties: {
                severity: { type: "string", enum: ["bug", "risk", "design", "nit"] },
                title: { type: "string", description: "short summary" },
                explanation: { type: "string", description: "what is wrong, why, and what to do instead" },
                refs: { type: "array", items: { type: "string" }, description: "repo-relative path:line locations (may be empty)" },
                previousId: { type: "string", description: "only when repeating an earlier finding: its id, e.g. R1.3; omit otherwise" },
              },
              required: ["severity", "title", "explanation"],
            },
            async execute(_toolCallId, p) {
              if (!["bug", "risk", "design", "nit"].includes(p.severity)) throw new Error(`severity must be one of bug, risk, design, nit (got ${JSON.stringify(p.severity)})`);
              const title = String(p.title || "").trim();
              const explanation = String(p.explanation || "").trim();
              if (!title) throw new Error("title must be a non-empty string");
              if (!explanation) throw new Error("explanation must be a non-empty string");
              const previousId = typeof p.previousId === "string" ? p.previousId.trim() : "";
              if (previousId && !earlier.includes(previousId))
                throw new Error(earlier.length === 0
                  ? `previousId "${previousId}": there are no earlier findings in round 1; omit previousId`
                  : `previousId "${previousId}" does not name an earlier finding (valid: ${earlier.join(", ")})`);
              const refs = Array.isArray(p.refs) ? p.refs.filter((r) => typeof r === "string" && r.trim()).map((r) => r.trim()) : [];
              const finding = { severity: p.severity, title, explanation, refs };
              if (previousId) finding.previousId = previousId;
              n += 1;
              const id = `${prefix}.${n}`;
              return { content: [{ type: "text", text: `Recorded as ${id}.` }], details: { n, finding } };
            },
          });
        };

        """;

    public const string TierExtensionFile = "bobar-service-tier.js";

    /// <summary>
    /// Sets the provider's service tier (`serviceTier` in config, passed as BOBAR_SERVICE_TIER), which Pi has no
    /// flag or setting for. Its `before_provider_request` hook rewrites each request's payload: `service_tier`
    /// for OpenAI's APIs, `config.serviceTier` for Google's (INFRA_FINDINGS §3).
    /// </summary>
    public const string TierExtension = """
        // Written by BobarCode (bobar-cli). Sets the provider's service tier on every request.
        const tier = process.env.BOBAR_SERVICE_TIER || "";
        module.exports = function (pi) {
          pi.on("before_provider_request", (event) => {
            const p = event.payload;
            if (!tier || !p || typeof p !== "object") return undefined;
            if (p.config && typeof p.config === "object" && Array.isArray(p.contents)) return { ...p, config: { ...p.config, serviceTier: tier } };
            return { ...p, service_tier: tier };
          });
        };

        """;

    public bool StreamsFindings => true;

    public Task<ReviewerResult> RunAsync(ReviewerCall call, IProgress<ReviewerActivity> activity, CancellationToken ct)
        => InvokeAsync(call, call.ResumeSessionId, call.Prompt, call.ResumeSessionId is null ? "run" : "resume", activity, ct);

    public Task<ReviewerResult> RepairAsync(ReviewerResult previous, string error, IProgress<ReviewerActivity> activity, CancellationToken ct)
        => InvokeAsync(previous.Call, previous.SessionId, error, "repair", activity, ct);

    /// <param name="reportExtension">The report_finding extension file, when the call streams findings.</param>
    /// <param name="tierExtension">The service-tier extension file, when the reviewer has a <see cref="ReviewerConfig.ServiceTier"/>.</param>
    public static List<string> Args(ReviewerConfig cfg, string sessionDir, string? sessionId, string? reportExtension = null, string? tierExtension = null)
    {
        var tools = reportExtension is null ? Tools : [.. Tools, ReportTool];
        var args = new List<string>
        {
            "--mode", "json", "--provider", cfg.Provider ?? "", "--model", cfg.Model, "--thinking", cfg.Thinking ?? "medium",
            "--no-extensions", "--no-skills", "--no-prompt-templates", "--no-mcp", "--no-context-files",
            "--tools", string.Join(',', tools), "--session-dir", sessionDir,
        };
        if (reportExtension is not null) args.AddRange(["-e", reportExtension]);
        if (tierExtension is not null) args.AddRange(["-e", tierExtension]);
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
        string? ext = null;
        if (call.OnFinding is not null)
        {
            ext = Path.Combine(ctx.Paths.PiAgentDir, ReportExtensionFile);
            env["BOBAR_ROUND"] = call.Round.ToString(System.Globalization.CultureInfo.InvariantCulture);
            env["BOBAR_ID_PREFIX"] = call.IdPrefix.Length > 0 ? call.IdPrefix : $"R{call.Round}";
            env["BOBAR_EARLIER_IDS"] = string.Join(',', call.EarlierIds);
            env["BOBAR_FIRST_N"] = call.FirstFindingNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        string? tierExt = null;
        if (!string.IsNullOrWhiteSpace(cfg.ServiceTier))
        {
            tierExt = Path.Combine(ctx.Paths.PiAgentDir, TierExtensionFile);
            env["BOBAR_SERVICE_TIER"] = cfg.ServiceTier.Trim();
        }

        List<string> args = [js, .. Args(cfg, ctx.Paths.PiSessionsDir, sessionId, ext, tierExt)];
        CliReviewer.LogCall(call, ReviewerKinds.Pi, what, node, args);
        var parser = new PiStream();
        var outcome = await ProcessRunner.RunAsync(new ProcessSpec(node, args, call.RepoDir) { Env = env, Stdin = prompt }, line =>
        {
            call.RawLog(line);
            if (CliReviewer.ParseLine(line) is { } o)
            {
                foreach (var a in parser.Record(o)) activity.Report(a);
                if (call.OnFinding is not null && PiStream.ReportedFinding(o) is { } f) call.OnFinding(f);
            }
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
                throw new ReviewerException(msg, stop == "error" && CliReviewer.IsTransient(msg), usage: parser.Usage, final: CliReviewer.IsNotRetryable(msg));
            }
        }
        if (!parser.Settled || last is null)
        {
            var detail = $"pi exited (code {outcome.ExitCode?.ToString() ?? "none"}) without a final result{(outcome.StderrTail.Trim().Length > 0 ? "; stderr: " + outcome.StderrTail.Trim() : "")}";
            throw new ReviewerException(detail, CliReviewer.IsTransient(outcome.StderrTail), usage: parser.Usage, final: CliReviewer.IsNotRetryable(outcome.StderrTail));
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
        WriteIfChanged(Path.Combine(dir, ReportExtensionFile), ReportExtension);
        WriteIfChanged(Path.Combine(dir, TierExtensionFile), TierExtension);
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

    /// <summary>A successful report_finding call (its `tool_execution_end`), or null for any other line.</summary>
    public static StreamedFinding? ReportedFinding(JsonObject o)
    {
        if (o.Str("type") != "tool_execution_end" || o.Str("toolName") != PiReviewer.ReportTool) return null;
        if (o["isError"] is JsonValue e && e.TryGetValue<bool>(out var isError) && isError) return null;
        if (o["result"]?["details"] is not JsonObject d || d["finding"] is not JsonObject f) return null;
        var n = (int)d.Num("n");
        return n > 0 ? new StreamedFinding(n, (JsonObject)f.DeepClone()) : null;
    }

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
