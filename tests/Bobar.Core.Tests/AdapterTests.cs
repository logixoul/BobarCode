using System.Text.Json.Nodes;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Proc;
using Bobar.Core.Reviewers;

namespace Bobar.Core.Tests;

/// <summary>Adapters against canned streams and a fake `pi` script. No model calls.</summary>
public class AdapterTests
{
    private static JsonObject J(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Fact]
    public void Pi_stream_sums_message_end_usage_only()
    {
        var p = new PiStream();
        p.Record(J("""{"type":"session","id":"abc"}"""));
        p.Record(J("""{"type":"message_update","message":{"role":"assistant","usage":{"input":999,"output":999}}}"""));
        p.Record(J("""{"type":"message_end","message":{"role":"user","content":"hi"}}"""));
        p.Record(J("""{"type":"message_end","message":{"role":"assistant","provider":"lithos","model":"m","thinkingLevel":"high","usage":{"input":10,"output":5,"cacheRead":3,"cacheWrite":0},"content":[{"type":"toolCall","name":"read","arguments":{"path":"a"}}]}}"""));
        p.Record(J("""{"type":"message_end","message":{"role":"assistant","provider":"lithos","model":"m","usage":{"input":20,"output":7,"cacheRead":0,"cacheWrite":1},"stopReason":"stop","content":[{"type":"text","text":"{\"findings\":[]}"}]}}"""));
        var acts = p.Record(J("""{"type":"agent_settled","aborted":false}""")).ToList();
        Assert.Equal("abc", p.SessionId);
        Assert.Equal(new TokenUsage { Input = 30, Output = 12, CacheRead = 3, CacheWrite = 1 }, p.Usage);
        Assert.Equal(["lithos/m"], p.Models);
        Assert.Equal("high", p.ThinkingLevel);
        Assert.True(p.Settled);
        Assert.Equal("{\"findings\":[]}", CliReviewer.TextParts(p.LastAssistant!["content"]));
        Assert.Single(acts);
    }

    [Fact]
    public void Claude_stream_rejects_an_api_key_and_reports_quota()
    {
        var c = new ClaudeStream();
        c.Record(J("""{"type":"system","subtype":"init","model":"claude-haiku-4-5","apiKeySource":"ANTHROPIC_API_KEY"}"""));
        Assert.Contains("apiKeySource=ANTHROPIC_API_KEY", c.FatalError);

        var ok = new ClaudeStream();
        ok.Record(J("""{"type":"system","subtype":"init","model":"claude-opus-5","apiKeySource":"none"}"""));
        Assert.Null(ok.FatalError);
        ok.Record(J("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1800000000}}"""));
        Assert.StartsWith("Claude quota exhausted until ", ok.QuotaMessage);

        var overage = new ClaudeStream();
        overage.Record(J("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","overageStatus":"allowed","isUsingOverage":true}}"""));
        Assert.Null(overage.QuotaMessage);
    }

    [Fact]
    public void Claude_stream_reads_usage_and_models_from_the_result()
    {
        var c = new ClaudeStream();
        c.Record(J("""{"type":"system","subtype":"init","model":"claude-opus-5","apiKeySource":"none"}"""));
        var acts = c.Record(J("""{"type":"assistant","message":{"model":"claude-opus-5","content":[{"type":"tool_use","name":"Read","input":{"file_path":"x"}}]}}""")).ToList();
        Assert.Contains("[Read]", acts.Single().Text);
        c.Record(J("""{"type":"result","subtype":"success","is_error":false,"result":"{}","structured_output":{"findings":[]},"usage":{"input_tokens":5,"output_tokens":6,"cache_read_input_tokens":7,"cache_creation_input_tokens":8},"modelUsage":{"claude-opus-5":{},"claude-haiku-4-5":{}}}"""));
        Assert.Equal(new TokenUsage { Input = 5, Output = 6, CacheRead = 7, CacheWrite = 8 }, c.Usage);
        Assert.Equal(["claude-opus-5", "claude-haiku-4-5"], c.Models);
    }

    [Fact]
    public void Claude_args_are_read_only_and_never_bare()
    {
        var args = ClaudeReviewer.Args(new ReviewerConfig { Kind = "claude", Model = "haiku", Effort = "low" }, @"C:\runs\x", "id-1", resume: false);
        Assert.DoesNotContain("--bare", args);
        Assert.Equal("Read,Grep,Glob", args[args.IndexOf("--tools") + 1]);
        Assert.Equal("", args[args.IndexOf("--setting-sources") + 1]);
        Assert.Equal(@"C:\runs\x", args[args.IndexOf("--add-dir") + 1]);
        Assert.Equal("id-1", args[args.IndexOf("--session-id") + 1]);
        var resume = ClaudeReviewer.Args(new ReviewerConfig { Model = "haiku" }, "d", "id-1", resume: true);
        Assert.Equal("id-1", resume[resume.IndexOf("-r") + 1]);
        Assert.DoesNotContain("--session-id", resume);
    }

    [Fact]
    public void Child_env_passes_only_the_listed_keys()
    {
        var keys = KeysEnv.Parse("LITHOS_API_KEY=lithos-secret\nOPENAI_API_KEY=openai-secret\n");
        var env = CliReviewer.ChildEnv(new ReviewerConfig { Env = ["LITHOS_API_KEY"] }, keys);
        Assert.Equal("lithos-secret", env["LITHOS_API_KEY"]);
        Assert.Null(env["OPENAI_API_KEY"]);
        Assert.Null(env["ANTHROPIC_API_KEY"]);
        Assert.Null(env["ANTHROPIC_BASE_URL"]);
        Assert.Null(env["ANTHROPIC_AUTH_TOKEN"]);
        var ex = Assert.Throws<ReviewerException>(() => CliReviewer.ChildEnv(new ReviewerConfig { Env = ["MISSING_KEY"] }, keys));
        Assert.Contains("MISSING_KEY", ex.Message);
        Assert.False(ex.Transient);
    }

    [Theory]
    [InlineData("503 Service Unavailable", true)]
    [InlineData("provider_overloaded", true)]
    [InlineData("fetch failed", true)]
    [InlineData("402 insufficient_quota", false)]
    [InlineData("401 invalid api key", false)]
    [InlineData("the model refused", false)]
    public void Transient_classification(string msg, bool transient) => Assert.Equal(transient, CliReviewer.IsTransient(msg));

    [Fact]
    public void Pi_launcher_is_resolved_from_the_npm_shim()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bobar-shim-" + Guid.NewGuid().ToString("N")[..8]);
        var js = Path.Combine(dir, "node_modules", "@x", "pi", "dist", "cli.js");
        Directory.CreateDirectory(Path.GetDirectoryName(js)!);
        File.WriteAllText(js, "");
        var shim = Path.Combine(dir, "pi.cmd");
        File.WriteAllText(shim, "@ECHO off\r\n\"%_prog%\"  \"%dp0%\\node_modules\\@x\\pi\\dist\\cli.js\" %*\r\n");
        var (_, resolved) = PiReviewer.ResolveLauncher(shim);
        Assert.Equal(Path.GetFullPath(js), resolved);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Pi_launcher_is_resolved_through_the_npm_symlink_on_unix()
    {
        if (OperatingSystem.IsWindows() || ProcessRunner.FindOnPath("node") is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "bobar-link-" + Guid.NewGuid().ToString("N")[..8]);
        var js = Path.Combine(dir, "lib", "node_modules", "@x", "pi", "dist", "cli.js");
        Directory.CreateDirectory(Path.GetDirectoryName(js)!);
        File.WriteAllText(js, "");
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        var link = Path.Combine(dir, "bin", "pi");
        File.CreateSymbolicLink(link, "../lib/node_modules/@x/pi/dist/cli.js");
        var (_, resolved) = PiReviewer.ResolveLauncher(link);
        Assert.Equal(new FileInfo(js).FullName, new FileInfo(resolved).FullName);
        Directory.Delete(dir, true);
    }

    /// <summary>A fake Pi in JS: echoes argv/env facts and stdin into a canned stream.</summary>
    private const string FakePi = """
        const lines = [];
        let input = "";
        process.stdin.on("data", d => input += d);
        process.stdin.on("end", () => {
          const out = o => process.stdout.write(JSON.stringify(o) + "\n");
          const args = process.argv.slice(2);
          out({ type: "session", id: "sess-1" });
          const text = JSON.stringify({ findings: [{ severity: "nit", title: "t", explanation: args.join(" ") + " | " + (process.env.LITHOS_API_KEY || "nokey") + " | " + (process.env.ANTHROPIC_API_KEY || "noanthropic") + " | " + process.env.PI_CODING_AGENT_DIR + " | " + input.length, refs: [] }] });
          out({ type: "message_end", message: { role: "assistant", provider: "lithos", model: "m", thinkingLevel: "high", stopReason: process.env.FAKE_STOP || "stop", errorMessage: "boom 503", usage: { input: 100, output: 50, cacheRead: 10, cacheWrite: 0 }, content: [{ type: "text", text: "```json\n" + text + "\n```" }] } });
          out({ type: "agent_settled", aborted: false });
        });
        """;

    [Fact]
    public async Task Pi_adapter_end_to_end_with_a_fake_pi()
    {
        if (ProcessRunner.FindOnPath("node") is null) return; // node is needed for Pi anyway
        using var env = new TestEnv(keys: "LITHOS_API_KEY=lithos-secret-123\n");
        var js = Path.Combine(env.Root, "fake-pi.js");
        File.WriteAllText(js, FakePi);
        var cfg = new ReviewerConfig { Kind = "pi", Provider = "lithos", Model = "m", Thinking = "high", Env = ["LITHOS_API_KEY"], Command = js };
        var reviewer = new PiReviewer(new ReviewerContext("ds", cfg, KeysEnv.Load(env.Paths.KeysFile), env.Paths));
        var raw = new List<string>();
        var call = new ReviewerCall { Round = 1, Prompt = "hello prompt", RepoDir = env.Repo.Dir, RunDir = env.Root, RoundDir = env.Root, RawLog = raw.Add };

        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "should-not-pass");
        try
        {
            var res = await reviewer.RunAsync(call, new Progress<ReviewerActivity>(), CancellationToken.None);
            var expl = res.Parsed!["findings"]![0]!["explanation"]!.GetValue<string>();
            Assert.Contains("--mode json --provider lithos --model m --thinking high --no-extensions --no-skills --no-prompt-templates --no-mcp --no-context-files --tools read,grep,find,ls --session-dir", expl);
            Assert.Contains("| lithos-secret-123 | noanthropic | " + env.Paths.PiAgentDir + " | 12", expl);
            Assert.Equal("sess-1", res.SessionId);
            Assert.Equal(new TokenUsage { Input = 100, Output = 50, CacheRead = 10 }, res.Usage);
            Assert.Equal("high", res.EffectiveReasoning);
            Assert.True(File.Exists(Path.Combine(env.Paths.PiAgentDir, "models.json")));
            Assert.Contains(raw, l => l.Contains("\"bobar\":\"call\""));

            var repaired = await reviewer.RepairAsync(res, "fix it", new Progress<ReviewerActivity>(), CancellationToken.None);
            Assert.Contains("--session sess-1", repaired.Parsed!["findings"]![0]!["explanation"]!.GetValue<string>());

            Environment.SetEnvironmentVariable("FAKE_STOP", "error");
            var ex = await Assert.ThrowsAsync<ReviewerException>(() => reviewer.RunAsync(call, new Progress<ReviewerActivity>(), CancellationToken.None));
            Assert.Contains("stopReason=error: boom 503", ex.Message);
            Assert.True(ex.Transient);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("FAKE_STOP", null);
        }
    }

    [Fact]
    public void Pi_stream_picks_up_successful_report_finding_calls_only()
    {
        var ok = J("""{"type":"tool_execution_end","toolCallId":"c1","toolName":"report_finding","isError":false,"result":{"content":[{"type":"text","text":"Recorded as R1.2."}],"details":{"n":2,"finding":{"severity":"bug","title":"t","explanation":"e","refs":[]}}}}""");
        var f = PiStream.ReportedFinding(ok)!;
        Assert.Equal(2, f.N);
        Assert.Equal("t", f.Finding["title"]!.GetValue<string>());
        Assert.Null(PiStream.ReportedFinding(J("""{"type":"tool_execution_end","toolName":"report_finding","isError":true,"result":{"content":[{"type":"text","text":"previousId bad"}],"details":{}}}""")));
        Assert.Null(PiStream.ReportedFinding(J("""{"type":"tool_execution_end","toolName":"read","isError":false,"result":{"details":{"n":1,"finding":{}}}}""")));
        Assert.Null(PiStream.ReportedFinding(J("""{"type":"tool_execution_start","toolName":"report_finding","args":{}}""")));
    }

    [Fact]
    public void Pi_args_add_the_report_tool_only_when_streaming()
    {
        var cfg = new ReviewerConfig { Kind = "pi", Provider = "p", Model = "m" };
        Assert.DoesNotContain("-e", PiReviewer.Args(cfg, "s", null));
        var args = PiReviewer.Args(cfg, "s", "sess", @"C:\x\ext.js");
        Assert.Equal("read,grep,find,ls,report_finding", args[args.IndexOf("--tools") + 1]);
        Assert.Equal(@"C:\x\ext.js", args[args.IndexOf("-e") + 1]);
        Assert.Contains("--no-extensions", args);
    }

    /// <summary>Loads the real report_finding extension with a stub `pi` and calls the tool.</summary>
    private const string ExtensionHarness = """
        let tool;
        require(process.argv[2])({ registerTool: t => { tool = t; } });
        (async () => {
          const out = [];
          const call = async p => { try { out.push((await tool.execute("id", p)).details); } catch (e) { out.push({ error: e.message }); } };
          await call({ severity: "bug", title: " A ", explanation: "why", refs: ["a.cs:1", " ", 3] });
          await call({ severity: "nit", title: "", explanation: "x" });
          await call({ severity: "nit", title: "B", explanation: "x", previousId: "R9.9" });
          await call({ severity: "risk", title: "C", explanation: "x", previousId: "R1.2" });
          await call({ severity: "huge", title: "D", explanation: "x" });
          process.stdout.write(JSON.stringify({ name: tool.name, required: tool.parameters.required, out }));
        })();
        """;

    [Fact]
    public async Task Report_extension_numbers_and_checks_findings()
    {
        if (ProcessRunner.FindOnPath("node") is not { } node) return;
        var dir = Path.Combine(Path.GetTempPath(), "bobar-ext-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var ext = Path.Combine(dir, PiReviewer.ReportExtensionFile);
            File.WriteAllText(ext, PiReviewer.ReportExtension);
            var harness = Path.Combine(dir, "harness.js");
            File.WriteAllText(harness, ExtensionHarness);
            var o = await ProcessRunner.RunAsync(new ProcessSpec(node, [harness, ext], dir)
            {
                Env = new Dictionary<string, string?> { ["BOBAR_ROUND"] = "2", ["BOBAR_EARLIER_IDS"] = "R1.1,R1.2" },
            }, null, CancellationToken.None);
            var r = J(o.Stdout);
            Assert.Equal("report_finding", r["name"]!.GetValue<string>());
            var outs = r["out"]!.AsArray();
            Assert.Equal(1, outs[0]!["n"]!.GetValue<int>());
            Assert.Equal("""{"severity":"bug","title":"A","explanation":"why","refs":["a.cs:1"]}""", outs[0]!["finding"]!.ToJsonString());
            Assert.Contains("title must be", outs[1]!["error"]!.GetValue<string>());
            Assert.Contains("\"R9.9\" does not name an earlier finding (valid: R1.1, R1.2)", outs[2]!["error"]!.GetValue<string>());
            Assert.Equal(2, outs[3]!["n"]!.GetValue<int>());
            Assert.Equal("R1.2", outs[3]!["finding"]!["previousId"]!.GetValue<string>());
            Assert.Contains("severity must be one of bug, risk, design, nit (got \"huge\")", outs[4]!["error"]!.GetValue<string>());
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A fake streaming Pi: reports two findings (one rejected by the tool), then says DONE.</summary>
    private const string FakeStreamingPi = """
        let input = "";
        process.stdin.on("data", d => input += d);
        process.stdin.on("end", () => {
          const out = o => process.stdout.write(JSON.stringify(o) + "\n");
          const args = process.argv.slice(2).join(" ");
          out({ type: "session", id: "sess-2" });
          out({ type: "tool_execution_end", toolCallId: "a", toolName: "report_finding", isError: false,
                result: { content: [], details: { n: 1, finding: { severity: "bug", title: "t", explanation: args + " | " + process.env.BOBAR_ROUND + " | " + process.env.BOBAR_EARLIER_IDS, refs: [] } } } });
          out({ type: "tool_execution_end", toolCallId: "b", toolName: "report_finding", isError: true, result: { content: [{ type: "text", text: "bad" }] } });
          out({ type: "message_end", message: { role: "assistant", provider: "p", model: "m", stopReason: "stop", usage: { input: 1, output: 1 }, content: [{ type: "text", text: "DONE" }] } });
          out({ type: "agent_settled", aborted: false });
        });
        """;

    [Fact]
    public async Task Pi_adapter_streams_reported_findings()
    {
        if (ProcessRunner.FindOnPath("node") is null) return;
        using var env = new TestEnv();
        var js = Path.Combine(env.Root, "fake-pi-stream.js");
        File.WriteAllText(js, FakeStreamingPi);
        var cfg = new ReviewerConfig { Kind = "pi", Provider = "p", Model = "m", Command = js };
        var reviewer = new PiReviewer(new ReviewerContext("ds", cfg, KeysEnv.Load(env.Paths.KeysFile), env.Paths));
        var got = new List<StreamedFinding>();
        var call = new ReviewerCall
        {
            Round = 3, Prompt = "p", RepoDir = env.Repo.Dir, RunDir = env.Root, RoundDir = env.Root, RawLog = _ => { },
            OnFinding = got.Add, EarlierIds = ["R1.1", "R2.1"],
        };
        var res = await reviewer.RunAsync(call, new Progress<ReviewerActivity>(), CancellationToken.None);
        Assert.Equal("DONE", res.FinalText);
        var f = Assert.Single(got);
        Assert.Equal(1, f.N);
        var expl = f.Finding["explanation"]!.GetValue<string>();
        Assert.Contains("--tools read,grep,find,ls,report_finding --session-dir", expl);
        Assert.Contains("-e " + Path.Combine(env.Paths.PiAgentDir, PiReviewer.ReportExtensionFile), expl);
        Assert.EndsWith(" | 3 | R1.1,R2.1", expl);
        Assert.Equal(PiReviewer.ReportExtension, File.ReadAllText(Path.Combine(env.Paths.PiAgentDir, PiReviewer.ReportExtensionFile)));
    }

    [Fact]
    public void Log_formatter_renders_both_streams()
    {
        var text = string.Join("\n",
            """{"bobar":"call","kind":"pi","what":"run","t":"2026-11-17T14:00:00+00:00","argv":[]}""",
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"done"}]}}""",
            """{"type":"tool_execution_end","toolName":"read","isError":true,"result":{"content":[{"type":"text","text":"ENOENT"}]}}""",
            """{"type":"result","subtype":"success","usage":{"input_tokens":1,"output_tokens":2}}""");
        var r = ReviewerLogFormatter.Render(text);
        Assert.Contains("── run (pi) at", r);
        Assert.Contains("[thinking] hmm", r);
        Assert.Contains("[read result, error] ENOENT", r);
        Assert.Contains("── result: success, 1 in / 2 out", r);
    }

    [Fact]
    public void Log_formatter_folds_bulky_content_into_a_detail()
    {
        var body = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i}"));
        var result = new JsonObject
        {
            ["type"] = "tool_execution_end", ["toolName"] = "read",
            ["result"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = body }) },
        };
        var a = Assert.Single(ReviewerLogFormatter.Describe(result));
        Assert.Equal(ActivityKind.ToolResult, a.Kind);
        Assert.Equal("[read result] line 1 …", a.Text);
        Assert.Equal(body, a.Detail);

        var code = "x\n" + new string('y', 200);
        var edit = new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_use", ["name"] = "edit", ["input"] = new JsonObject { ["path"] = "a.cs", ["new"] = code },
            }) },
        };
        var call = Assert.Single(ReviewerLogFormatter.Describe(edit));
        Assert.StartsWith("[edit] {", call.Text);
        Assert.Equal("path: a.cs\nnew: \n" + code, call.Detail);

        var shortCall = JsonNode.Parse("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"read","input":{"path":"a.cs"}}]}}""")!.AsObject();
        Assert.Null(Assert.Single(ReviewerLogFormatter.Describe(shortCall)).Detail);
    }
}
