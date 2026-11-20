using Bobar.Core.Loop;
using Bobar.Core.Reviewers;
using Bobar.Core.Model;
using Bobar.Core.Runs;
using Bobar.Core.State;
using static Bobar.Core.Tests.F;

namespace Bobar.Core.Tests;

/// <summary>Streaming reviewers: findings reported one by one, a snapshot to review, `review stream`.</summary>
public class StreamingTests
{
    private static Dictionary<int, IEnumerable<FakeStep>> Script(params (int Round, FakeStep[] Steps)[] rounds)
        => rounds.ToDictionary(r => r.Round, r => (IEnumerable<FakeStep>)r.Steps);

    [Fact]
    public async Task A_streaming_round_reviews_a_snapshot_and_logs_each_finding()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"), Finding("nit", "B"))])), streams: true);
        env.Repo.Write("debug.log", "ignored\n");
        var statusBefore = env.Repo.Git("status", "--porcelain");
        string? dir = null, text = null;
        var logCopied = true;
        env.Fake.OnCall = c =>
        {
            dir = c.RepoDir;
            text = File.ReadAllText(Path.Combine(c.RepoDir, "src", "app.txt"));
            logCopied = File.Exists(Path.Combine(c.RepoDir, "debug.log"));
        };

        var r = await env.Start();
        Assert.Equal("findings", r.Status);
        var run = env.Run(r);
        Assert.Equal(run.Snapshot, dir);
        Assert.Equal("line 1\nline 2 changed\n", text);
        Assert.False(logCopied);
        Assert.False(Directory.Exists(run.Snapshot));
        Assert.Equal(statusBefore, env.Repo.Git("status", "--porcelain"));

        var lines = File.ReadAllLines(run.Round(1).Findings);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("{\"id\":\"R1.1\",\"severity\":\"bug\",\"title\":\"A\"", lines[0]);
        Assert.StartsWith("{\"id\":\"R1.2\"", lines[1]);
        var review = RunReader.LoadRound(run, 1).Review!;
        Assert.Equal(["R1.1", "R1.2"], review.Findings.Select(f => f.Id));
        Assert.Equal(["A", "B"], review.Findings.Select(f => f.Title));

        var prompt = File.ReadAllText(run.Round(1).Prompt);
        Assert.Contains("`report_finding` tool", prompt);
        Assert.Contains("your final message is just `DONE`", prompt);
        Assert.Contains(run.Snapshot, prompt);
    }

    [Fact]
    public async Task A_streaming_round_sweeps_snapshots_left_by_dead_runs()
    {
        using var env = new TestEnv(Script((1, [Out()])), streams: true);
        var stale = env.Paths.Run("19990101-000000-old-dead").Snapshot;
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "secret.txt"), "x");
        // Another installation's (data dir's) snapshot is never touched, live or not.
        var other = new BobarPaths(env.Paths.ConfigDir, env.Paths.DataDir + "-other").Run("19990101-000000-old-dead").Snapshot;
        Directory.CreateDirectory(other);
        try
        {
            Assert.NotEqual(stale, other);
            Assert.Equal("done", (await env.Start()).Status);
            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(other));
        }
        finally { Directory.Delete(other, true); }
    }

    [Fact]
    public async Task No_reported_findings_is_a_clean_round()
    {
        using var env = new TestEnv(Script((1, [Out()])), streams: true);
        var r = await env.Start();
        Assert.Equal("done", r.Status);
        Assert.Empty(File.ReadAllText(env.Run(r).Round(1).Findings));
    }

    [Fact]
    public async Task Stream_prints_each_finding_then_END()
    {
        using var env = new TestEnv(Script(
            (1, [Out(Finding("bug", "A", null, "src/app.txt:2"), Finding("nit", "B"))]),
            (2, [Out(Finding("nit", "B", previousId: "R1.2"))])), streams: true);
        // Started before the run exists: it waits for the run to appear in the repo.
        var early = new StringWriter();
        var earlyTask = env.Engine.StreamAsync(null, null, env.Repo.Dir, early, CancellationToken.None);
        var r1 = await env.Start();
        Assert.Equal(0, await earlyTask);
        var outText = early.ToString();
        Assert.StartsWith($"RUN {r1.RunId} round=1", outText);
        Assert.Contains("### R1.1 [bug] A\nsrc/app.txt:2\nBecause.\n", outText.Replace("\r\n", "\n"));
        Assert.Contains("### R1.2 [nit] B", outText);
        Assert.Contains("END round=1: the reviewer finished round 1 (2 findings).", outText);

        var r2 = await env.Next(r1, Resp(("R1.1", "fix"), ("R1.2", "reject")));
        Assert.Equal("findings", r2.Status);
        var later = new StringWriter();
        Assert.Equal(0, await env.Engine.StreamAsync(r1.RunId, 2, env.Repo.Dir, later, CancellationToken.None));
        Assert.Contains("### R2.1 [nit] B   (repeats R1.2)", later.ToString());
        Assert.Contains("END round=2:", later.ToString());
    }

    [Fact]
    public async Task A_retried_call_marks_a_restart_and_reuses_ids()
    {
        var failing = Out(Finding("bug", "A")) with { Fail = "503 service unavailable", Transient = true };
        using var env = new TestEnv(Script((1, [failing, Out(Finding("nit", "B"))])), streams: true);
        var r = await env.Start();
        Assert.Equal("findings", r.Status);
        var run = env.Run(r);
        var lines = File.ReadAllLines(run.Round(1).Findings);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"title\":\"A\"", lines[0]);
        Assert.StartsWith("{\"restart\":true", lines[1]);
        Assert.Contains("\"id\":\"R1.1\",\"severity\":\"nit\",\"title\":\"B\"", lines[2]);
        Assert.Equal(["B"], RunReader.LoadRound(run, 1).Review!.Findings.Select(f => f.Title));

        var o = new StringWriter();
        await env.Engine.StreamAsync(r.RunId, 1, env.Repo.Dir, o, CancellationToken.None);
        var text = o.ToString();
        Assert.True(text.IndexOf("### R1.1 [bug] A") < text.IndexOf("RESTART:") && text.IndexOf("RESTART:") < text.IndexOf("### R1.1 [nit] B"), text);
    }

    [Fact]
    public async Task A_final_message_that_is_neither_DONE_nor_findings_gets_a_repair()
    {
        using var env = new TestEnv(Script((1, [FakeStep.Json("I can't review this."), Out(Finding("bug", "A"))])), streams: true);
        var r = await env.Start();
        Assert.Equal("findings", r.Status);
        Assert.Contains("report_finding", env.Fake.Calls[1].Prompt);
        Assert.Contains("reply with just `DONE`", env.Fake.Calls[1].Prompt);
        Assert.Equal(["A"], RunReader.LoadRound(env.Run(r), 1).Review!.Findings.Select(f => f.Title));
        Assert.Single(RunReader.LoadState(env.Run(r)).Rounds[0].Repairs);

        using var env2 = new TestEnv(Script((1, [FakeStep.Json("No."), FakeStep.Json("Still no.")])), streams: true);
        Assert.Equal("error", (await env2.Start()).Status);
    }

    [Fact]
    public async Task Reports_without_DONE_get_a_repair_that_continues_the_numbering()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A")) with { Final = "I couldn't read the rest of the diff." }, Out(Finding("nit", "B"))])), streams: true);
        var r = await env.Start();
        Assert.Equal("findings", r.Status);
        Assert.Contains("you reported 1 finding", env.Fake.Calls[1].Prompt);
        Assert.Contains("don't report them again", env.Fake.Calls[1].Prompt);
        var review = RunReader.LoadRound(env.Run(r), 1).Review!;
        Assert.Equal(["A", "B"], review.Findings.Select(f => f.Title));
        Assert.Equal(["R1.1", "R1.2"], review.Findings.Select(f => f.Id));
        Assert.DoesNotContain("restart", File.ReadAllText(env.Run(r).Round(1).Findings));
    }

    [Fact]
    public async Task A_retry_that_resumes_the_session_keeps_its_reports()
    {
        var failing = Out(Finding("bug", "B")) with { Fail = "503 service unavailable", Transient = true };
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"))]), (2, [failing, Out(Finding("nit", "C"))])), streams: true);
        var r1 = await env.Start();
        var r2 = await env.Next(r1, Resp(("R1.1", "fix")));
        Assert.Equal("findings", r2.Status);
        Assert.Equal(["fake-session-1", "fake-session-1"], env.Fake.ResumeIds.Skip(1));
        var review = RunReader.LoadRound(env.Run(r1), 2).Review!;
        Assert.Equal(["B", "C"], review.Findings.Select(f => f.Title));
        Assert.Equal(["R2.1", "R2.2"], review.Findings.Select(f => f.Id));
        Assert.DoesNotContain("restart", File.ReadAllText(env.Run(r1).Round(2).Findings));
    }

    [Fact]
    public async Task Done_with_markdown_or_a_full_stop_is_a_clean_round()
    {
        Assert.True(ReviewEngine.IsDone("**Done.**\n"));
        Assert.True(ReviewEngine.IsDone("`DONE`"));
        Assert.False(ReviewEngine.IsDone("DONE, but I couldn't inspect the rest of the diff."));
        Assert.False(ReviewEngine.IsDone("DONEISH"));
        Assert.False(ReviewEngine.IsDone("Done reviewing; no tool available."));
        using var env = new TestEnv(Script((1, [FakeStep.Json("Done.")])), streams: true);
        Assert.Equal("done", (await env.Start()).Status);
    }

    [Fact]
    public async Task Stream_for_a_round_that_wont_happen_ends_when_the_run_awaits_decisions()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"))])), maxRounds: 1, streams: true);
        var r1 = await env.Start();
        var r2 = await env.Next(r1, Resp(("R1.1", "reject")));
        Assert.Equal("awaiting_decisions", r2.Status);
        var o = new StringWriter();
        Assert.Equal(0, await env.Engine.StreamAsync(r1.RunId, 2, env.Repo.Dir, o, CancellationToken.None));
        Assert.Contains("END round=2: there is no round 2: the run is awaiting the user's decisions.", o.ToString());
    }

    [Fact]
    public async Task Stream_without_a_run_ignores_runs_that_started_before_it()
    {
        using var env = new TestEnv(Script((1, [Out()])), streams: true);
        Assert.Equal("done", (await env.Start()).Status);
        var engine = new ReviewEngine(new EngineOptions
        {
            Paths = env.Paths,
            ReviewerFactory = _ => env.Fake,
            StreamPollInterval = TimeSpan.FromMilliseconds(20),
            StreamFindRunTimeout = TimeSpan.FromMilliseconds(300),
        });
        var o = new StringWriter();
        Assert.Equal(1, await engine.StreamAsync(null, null, env.Repo.Dir, o, CancellationToken.None));
        Assert.Contains("END error: no run started in", o.ToString());
    }

    [Fact]
    public void Findings_log_redacts_values_normalizes_refs_and_marks_restarts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bobar-flog-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var rp = new RoundPaths(2, dir);
            const string secret = "sk+abc/def\"ghi\\jkl";
            var redactor = new Redactor([secret]);
            using (var log = new FindingsLog(rp, redactor, refs => refs.Select(r => r.Replace(@"C:\snap\", "")).ToList()))
            {
                log.BeginAttempt();
                log.BeginAttempt(); // nothing reported yet: no restart line
                var f = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
                    System.Text.Json.JsonSerializer.Serialize(new { severity = "bug", title = "t", explanation = "leaks " + secret, refs = new[] { @"C:\snap\src\a.cs:3" } }))!;
                log.Add(new Reviewers.StreamedFinding(1, f));
                log.BeginAttempt();
                Assert.Equal(0, log.Count);
            }
            var text = File.ReadAllText(rp.Findings);
            Assert.DoesNotContain("abc/def", text);
            Assert.DoesNotContain("abc\\/def", text);
            Assert.Contains("leaks ***", text);
            Assert.Contains("\"refs\":[\"src\\\\a.cs:3\"]", text);
            var lines = text.TrimEnd('\n').Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("{\"id\":\"R2.1\"", lines[0]);
            Assert.StartsWith("{\"restart\":true", lines[1]);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Stream_ends_when_the_run_ends_without_a_review()
    {
        using var env = new TestEnv(Script((1, [FakeStep.Error("boom")])), streams: true);
        var r = await env.Start();
        Assert.Equal("error", r.Status);
        var o = new StringWriter();
        Assert.Equal(0, await env.Engine.StreamAsync(r.RunId, 1, env.Repo.Dir, o, CancellationToken.None));
        Assert.Contains("END round=1: the run ended (error:", o.ToString());
    }

    [Fact]
    public async Task Verdicts_are_recorded_for_the_GUI_and_override_per_id()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"), Finding("nit", "B"), Finding("risk", "C"))]), (2, [Out()])), streams: true);
        CommandResult? early = null;
        // While the reviewer works (start holds run.lock), nothing is reported yet: lock-free, and it says so.
        env.Fake.OnCall = c => early ??= env.Engine.Verdicts(RunReader.ListRuns(env.Paths)[0].RunId, ["R1.1"], []);
        var r1 = await env.Start();
        Assert.Equal("invalid_verdicts", early!.Status);
        Assert.Contains("none so far", early.Body);
        Assert.Contains($"bobar-cli review verdicts {r1.RunId} --fix", r1.Body);

        Assert.Equal("verdicts_saved", env.Engine.Verdicts(r1.RunId!, ["R1.1", "R1.2"], ["R1.3"]).Status);
        Assert.Equal("verdicts_saved", env.Engine.Verdicts(r1.RunId!, [], ["R1.2"]).Status);
        Assert.Equal("invalid_verdicts", env.Engine.Verdicts(r1.RunId!, ["R1.9"], []).Status);
        Assert.Equal("invalid_verdicts", env.Engine.Verdicts(r1.RunId!, ["R1.1"], ["R1.1"]).Status);

        var run = env.Run(r1);
        var s = RunReader.LoadState(run);
        var live = LiveRound.Load(run, s, RunReader.LoadRounds(run, s))!;
        Assert.Equal([("R1.1", AuthorAction.Fix), ("R1.2", AuthorAction.Reject), ("R1.3", AuthorAction.Reject)],
            live.Verdicts.OrderBy(v => v.Key).Select(v => (v.Key, v.Value)));
        var row = SummaryTable.Build(s, RunReader.LoadRounds(run, s), DateTimeOffset.Now, live: live).Rounds[0];
        Assert.Equal((1, 2, "fixing…"), (row.Fix, row.Reject, row.AuthorNote));
        Assert.Equal(["fixing", "rejected", "rejected"], row.Findings.Select(f => f.Action));

        // Once answered, the round is settled: no more verdicts, and the response is what shows.
        Assert.Equal("done", (await env.Next(r1, Resp(("R1.1", "fix"), ("R1.2", "fix"), ("R1.3", "reject")))).Status);
        Assert.Equal("invalid_verdicts", env.Engine.Verdicts(r1.RunId!, ["R1.1"], []).Status);
    }
}
