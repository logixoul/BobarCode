using Bobar.Core.IO;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;
using Bobar.Core.Runs;
using Bobar.Core.State;
using static Bobar.Core.Tests.F;

namespace Bobar.Core.Tests;

public class EngineTests
{
    private static Dictionary<int, IEnumerable<FakeStep>> Script(params (int Round, FakeStep[] Steps)[] rounds)
        => rounds.ToDictionary(r => r.Round, r => (IEnumerable<FakeStep>)r.Steps);

    private static List<RunEvent> Events(TestEnv env, CommandResult r) => EventLog.Read(env.Run(r));

    private static RunState State(TestEnv env, CommandResult r) => RunReader.LoadState(env.Run(r));

    [Fact]
    public async Task Clean_in_round_1()
    {
        using var env = new TestEnv(Script((1, [Out()])));
        var r = await env.Start();
        Assert.Equal("done", r.Status);
        Assert.StartsWith($"STATUS: done run={r.RunId} round=1", r.Render());
        var types = Events(env, r).Select(e => e.GetType().Name).ToList();
        Assert.Equal(["RunStarted", "ReviewRequested", "ReviewReceived", "RunFinished"], types);
        Assert.Equal(Outcome.Clean, State(env, r).Finished!.Outcome);
        var run = env.Run(r);
        foreach (var f in new[] { run.Desc, run.Round(1).Tree, run.Round(1).Diff, run.Round(1).Prompt, run.Round(1).ReviewerLog, run.Round(1).Review })
            Assert.True(File.Exists(f), f);
        Assert.Contains("line 2 changed", File.ReadAllText(run.Round(1).Diff));
        Assert.Contains(run.Round(1).Diff, File.ReadAllText(run.Round(1).Prompt));
        Assert.Contains("Requesting review from fake (fake)…", env.Progress.ToString());
    }

    [Fact]
    public async Task Reviewer_instructions_go_into_every_prompt()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"))]), (2, [Out()])), instructions: "Prefer lightweight designs.");
        var r1 = await env.Start();
        var r2 = await env.Next(r1, Resp(("R1.1", "fix")));
        Assert.Equal("done", r2.Status);
        foreach (var k in new[] { 1, 2 })
        {
            var prompt = File.ReadAllText(env.Run(r1).Round(k).Prompt);
            Assert.Contains("## The user's instructions", prompt);
            Assert.Contains("Prefer lightweight designs.", prompt);
        }
    }

    [Fact]
    public async Task No_instructions_section_when_empty()
    {
        using var env = new TestEnv(Script((1, [Out()])));
        var r = await env.Start();
        Assert.DoesNotContain("## The user's instructions", File.ReadAllText(env.Run(r).Round(1).Prompt));
    }

    [Fact]
    public async Task Fix_then_clean()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"))]), (2, [Out()])));
        var r1 = await env.Start();
        Assert.Equal("findings", r1.Status);
        Assert.Contains("### R1.1 [bug] A", r1.Body);
        Assert.Contains($"bobar-cli review next {r1.RunId} --response -", r1.Body);

        env.Repo.Write("src/app.txt", "line 1\nline 2 fixed\n");
        var r2 = await env.Next(r1, Resp(("R1.1", "fix")));
        Assert.Equal("done", r2.Status);
        Assert.Equal(2, r2.Round);
        var rd = env.Run(r1).Round(2);
        Assert.Contains("line 2 fixed", File.ReadAllText(rd.Delta));
        Assert.Contains("R1.1", File.ReadAllText(rd.History));
        Assert.Contains("**Author: fix.** Fixed.", File.ReadAllText(rd.History));
        Assert.Contains(rd.Delta, File.ReadAllText(rd.Prompt));
    }

    [Fact]
    public async Task Reject_then_maintained_is_a_deadlock()
    {
        using var env = new TestEnv(Script(
            (1, [Out(Finding("bug", "A"))]),
            (2, [Out(Finding("bug", "A", previousId: "R1.1"))])));
        var r1 = await env.Start();
        // Round 1: accepted 0 but the finding is new → next round, so the reviewer can defend it.
        var r2 = await env.Next(r1, Resp(("R1.1", "reject")));
        Assert.Equal("findings", r2.Status);
        Assert.Contains("(maintained from R1.1)", r2.Body);
        var rr = Events(env, r1).OfType<ReviewReceived>().Last();
        Assert.Equal(1, rr.Maintained);
        Assert.Equal(0, rr.Withdrawn);

        var r3 = await env.Next(r2, Resp(("R2.1", "reject")));
        Assert.Equal("awaiting_decisions", r3.Status);
        Assert.Contains($"bobar-cli review wait {r1.RunId}", r3.Body);
        var aw = State(env, r1).Awaiting!;
        Assert.Equal(AwaitReason.Deadlock, aw.Reason);
        Assert.Equal(["R2.1"], aw.Ids);
        Assert.Equal([r1.RunId!], env.GuiLaunches);
    }

    [Fact]
    public async Task Rejected_and_not_repeated_counts_as_withdrawn()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"), Finding("nit", "B"))]), (2, [Out()])));
        var r1 = await env.Start();
        var r2 = await env.Next(r1, Resp(("R1.1", "reject"), ("R1.2", "fix")));
        Assert.Equal("done", r2.Status);
        var rr = Events(env, r1).OfType<ReviewReceived>().Last();
        Assert.Equal(1, rr.Withdrawn);
        var review = RunReader.LoadRound(env.Run(r1), 2).Review!;
        Assert.Equal(["R1.1"], review.Withdrawn);
    }

    [Fact]
    public async Task No_fixes_but_a_new_finding_goes_to_the_next_round()
    {
        using var env = new TestEnv(Script(
            (1, [Out(Finding("bug", "A"))]),
            (2, [Out(Finding("bug", "A", previousId: "R1.1"), Finding("nit", "New"))]),
            (3, [Out()])));
        var r1 = await env.Start();
        var r2 = await env.Next(r1, Resp(("R1.1", "reject")));
        Assert.Equal("findings", r2.Status);
        var r3 = await env.Next(r2, Resp(("R2.1", "reject"), ("R2.2", "reject")));
        Assert.Equal("done", r3.Status);
        Assert.Equal(3, r3.Round);
    }

    [Fact]
    public async Task Repeating_an_accepted_finding_is_not_maintained()
    {
        // Round 3 has no script: the fake's default maintains every finding rejected in round 2.
        using var env = new TestEnv(Script(
            (1, [Out(Finding("bug", "A"))]),
            (2, [Out(Finding("bug", "A not fixed", previousId: "R1.1"))])));
        var r1 = await env.Start();
        var r2 = await env.Next(r1, Resp(("R1.1", "fix")));
        Assert.Contains("(repeated from R1.1)", r2.Body);
        Assert.Equal(1, Events(env, r1).OfType<ReviewReceived>().Last().Repeated);

        var r3 = await env.Next(r2, Resp(("R2.1", "reject")));
        Assert.Equal("findings", r3.Status); // repeated ≠ maintained, so no deadlock yet
        var r4 = await env.Next(r3, Resp(("R3.1", "reject")));
        Assert.Equal("awaiting_decisions", r4.Status);
        Assert.Equal(AwaitReason.Deadlock, State(env, r1).Awaiting!.Reason);
    }

    [Fact]
    public async Task Cap_with_rejections_awaits_decisions()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"))]), (2, [Out(Finding("risk", "B"))])), maxRounds: 2);
        var r1 = await env.Start();
        var r2 = await env.Next(r1, Resp(("R1.1", "fix")));
        var r3 = await env.Next(r2, Resp(("R2.1", "reject")));
        Assert.Equal("awaiting_decisions", r3.Status);
        Assert.Equal(AwaitReason.Cap, State(env, r1).Awaiting!.Reason);
    }

    [Fact]
    public async Task Cap_without_rejections_is_done_cap()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"))])), maxRounds: 1);
        var r1 = await env.Start();
        var r2 = await env.Next(r1, Resp(("R1.1", "fix")));
        Assert.Equal("done", r2.Status);
        Assert.Equal(Outcome.Cap, State(env, r1).Finished!.Outcome);
        Assert.Contains("not re-reviewed", r2.Body);
        Assert.Single(env.Fake.Calls);
    }

    [Theory]
    [InlineData("""{"responses":[{"id":"R1.9","action":"fix","reasoning":""}]}""", "not a finding of this round")]
    [InlineData("""{"responses":[]}""", "missing responses for: R1.1")]
    [InlineData("""{"responses":[{"id":"R1.1","action":"fix"},{"id":"R1.1","action":"fix"}]}""", "more than once")]
    [InlineData("""{"responses":[{"id":"R1.1","action":"maybe"}]}""", "must be \"fix\" or \"reject\"")]
    [InlineData("""{"responses":[{"id":"R1.1","action":"reject","reasoning":" "}]}""", "a reject needs a reasoning")]
    [InlineData("not json", "not valid JSON")]
    public async Task Invalid_response_exits_2_and_consumes_no_round(string json, string error)
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"))]), (2, [Out()])));
        var r1 = await env.Start();
        var before = Events(env, r1).Count;
        var bad = await env.Next(r1, json);
        Assert.Equal("invalid_response", bad.Status);
        Assert.Equal(2, bad.ExitCode);
        Assert.Contains(error, bad.Body);
        Assert.Equal(before, Events(env, r1).Count);
        Assert.False(File.Exists(env.Run(r1).Round(1).Response));

        var ok = await env.Next(r1, Resp(("R1.1", "fix")));
        Assert.Equal("done", ok.Status);
    }

    [Fact]
    public async Task Invalid_output_gets_one_repair_turn()
    {
        using var env = new TestEnv(Script((1, [FakeStep.Json("Here you go: not json"), Out(Finding("nit", "A"))])));
        var r = await env.Start();
        Assert.Equal("findings", r.Status);
        Assert.Equal(2, env.Fake.Calls.Count);
        Assert.Contains("not valid", env.Fake.Calls[1].Prompt);
        Assert.Single(Events(env, r).OfType<ReviewerRepair>());
        Assert.True(Events(env, r).OfType<ReviewReceived>().Single().Repaired);
    }

    [Fact]
    public async Task Second_invalid_output_is_an_error()
    {
        using var env = new TestEnv(Script((1, [FakeStep.Json("{}"), FakeStep.Json("""{"findings":[{"severity":"huge"}]}""")])));
        var r = await env.Start();
        Assert.Equal("error", r.Status);
        Assert.Equal(1, r.ExitCode);
        var fin = State(env, r).Finished!;
        Assert.Equal(Outcome.Error, fin.Outcome);
        Assert.Contains("still invalid", fin.Message);
    }

    [Fact]
    public async Task Unknown_previousId_is_a_validation_error()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A", previousId: "R9.9")), Out(Finding("bug", "A"))])));
        var r = await env.Start();
        Assert.Equal("findings", r.Status);
        Assert.Contains("R9.9", Events(env, r).OfType<ReviewerRepair>().Single().Error);
    }

    [Fact]
    public async Task Transient_failure_is_retried_once_after_60s()
    {
        using var env = new TestEnv(Script((1, [FakeStep.Error("503 overloaded", transient: true), Out()])));
        var r = await env.Start();
        Assert.Equal("done", r.Status);
        Assert.Equal([TimeSpan.FromSeconds(60)], env.Delays);
        var retry = Events(env, r).OfType<ReviewerRetry>().Single();
        Assert.Equal(60, retry.DelaySeconds);
        Assert.Contains("retrying in 1m00s", env.Progress.ToString());
    }

    [Fact]
    public async Task Retry_after_is_honoured_and_capped_at_15_minutes()
    {
        using var env = new TestEnv(Script((1, [FakeStep.Error("429", transient: true, retryAfter: 5000), Out()])));
        await env.Start();
        Assert.Equal([TimeSpan.FromMinutes(15)], env.Delays);
    }

    [Fact]
    public async Task Retry_after_from_error_text()
    {
        using var env = new TestEnv(Script((1, [FakeStep.Error("rate_limit_exceeded; retry-after: 7", transient: true), Out()])));
        await env.Start();
        Assert.Equal([TimeSpan.FromSeconds(7)], env.Delays);
    }

    [Fact]
    public async Task Failing_twice_is_an_error()
    {
        using var env = new TestEnv(Script((1, [FakeStep.Error("overloaded", true), FakeStep.Error("overloaded again", true)])));
        var r = await env.Start();
        Assert.Equal("error", r.Status);
        Assert.Contains("failed twice", State(env, r).Finished!.Message);
    }

    [Fact]
    public async Task Non_transient_failure_is_not_retried()
    {
        using var env = new TestEnv(Script((1, [FakeStep.Error("invalid api key")])));
        var r = await env.Start();
        Assert.Equal("error", r.Status);
        Assert.Empty(env.Delays);
        Assert.Single(env.Fake.Calls);
    }

    [Fact]
    public async Task Timeout_is_an_error()
    {
        using var env = new TestEnv(Script((1, [new FakeStep { Hang = true }])), timeoutMinutes: 0.001);
        var r = await env.Start();
        Assert.Equal("error", r.Status);
        Assert.Contains("timed out", State(env, r).Finished!.Message);
    }

    [Fact]
    public async Task Ctrl_c_cancels_the_run()
    {
        using var env = new TestEnv(Script((1, [new FakeStep { Hang = true }])));
        using var cts = new CancellationTokenSource();
        var task = env.Engine.StartAsync("fake", "desc", env.Repo.Dir, cts.Token);
        while (env.Fake.Calls.Count == 0) await Task.Delay(10);
        cts.Cancel();
        var r = await task;
        Assert.Equal("cancelled", r.Status);
        Assert.Equal(Outcome.Cancelled, State(env, r).Finished!.Outcome);
    }

    [Fact]
    public async Task Start_fails_clearly_without_changes()
    {
        using var env = new TestEnv();
        env.Repo.Write("src/app.txt", "line 1\nline 2\n");
        var r = await env.Start();
        Assert.Equal("error", r.Status);
        Assert.Null(r.RunId);
        Assert.Contains("no uncommitted changes", r.Body);
        Assert.Empty(RunReader.ListRuns(env.Paths));
    }

    [Fact]
    public async Task Unknown_reviewer_is_an_error()
    {
        using var env = new TestEnv();
        var r = await env.Engine.StartAsync("nope", "d", env.Repo.Dir, CancellationToken.None);
        Assert.Equal("error", r.Status);
        Assert.Contains("Unknown reviewer 'nope'", r.Body);
    }

    [Fact]
    public async Task A_locked_run_fails_at_once()
    {
        using var env = new TestEnv(Script((1, [Out(Finding())])));
        var r1 = await env.Start();
        using (var held = RunLock.TryAcquire(env.Run(r1)))
        {
            Assert.NotNull(held);
            var r = await env.Next(r1, Resp(("R1.1", "fix")));
            Assert.Equal("busy", r.Status);
            Assert.Equal(1, r.ExitCode);
        }
    }

    [Fact]
    public async Task Next_on_a_finished_run_shows_its_state()
    {
        using var env = new TestEnv(Script((1, [Out()])));
        var r1 = await env.Start();
        var r = await env.Next(r1, Resp());
        Assert.Equal("done", r.Status);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("nothing to respond to", r.Body);
    }

    [Fact]
    public async Task Wait_returns_the_decisions_and_ends_the_run()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"), Finding("nit", "B"))])), maxRounds: 1);
        var r1 = await env.Start();
        var r2 = await env.Next(r1, Resp(("R1.1", "reject"), ("R1.2", "reject")));
        Assert.Equal("awaiting_decisions", r2.Status);

        var wait = env.Engine.WaitAsync(r1.RunId!, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);

        var err = await RunActions.SubmitDecisionsAsync(env.Run(r1), new DecisionsFile
        {
            Decisions = [new DecisionItem { Id = "R1.1", Decision = UserDecision.Fix, Comment = "Keep the old overload." }, new DecisionItem { Id = "R1.2", Decision = UserDecision.Dismiss }],
            Comment = "Thanks",
        }, Redactor.None, TimeProvider.System);
        Assert.Null(err);

        var r3 = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("decided", r3.Status);
        Assert.Contains("R1.1 [bug] A: FIX IT", r3.Body);
        Assert.Contains("Keep the old overload.", r3.Body);
        Assert.Contains("Overall comment from the user: Thanks", r3.Body);
        var s = State(env, r1);
        Assert.Equal(Outcome.Decided, s.Finished!.Outcome);
        Assert.Equal(1, s.Decisions!.Fix);
        Assert.Contains("You decided: fix 1, dismiss 1. Ending review session.", Summary.Lines(s).Select(l => l.Text));

        // Calling wait again just reprints the decisions.
        Assert.Equal("decided", (await env.Engine.WaitAsync(r1.RunId!, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Wait_appends_decisions_submitted_if_the_gui_could_not()
    {
        using var env = new TestEnv(Script((1, [Out(Finding())])), maxRounds: 1);
        var r1 = await env.Start();
        await env.Next(r1, Resp(("R1.1", "reject")));
        RunFiles.WriteJson(env.Run(r1).Decisions, new DecisionsFile { Decisions = [new DecisionItem { Id = "R1.1", Decision = UserDecision.Dismiss }] }, BobarJson.Indented.DecisionsFile, Redactor.None);
        var r = await env.Engine.WaitAsync(r1.RunId!, CancellationToken.None);
        Assert.Equal("decided", r.Status);
        Assert.Single(Events(env, r1).OfType<DecisionsSubmitted>());
    }

    private static string Hind(int usefulness, string accepted = "[]", string rejected = "[]", string notes = "\"Nothing to change.\"")
        => $$"""{"usefulness": {{usefulness}}, "acceptedWrongly": {{accepted}}, "rejectedWrongly": {{rejected}}, "notes": {{notes}}}""";

    [Fact]
    public async Task Hindsight_after_a_finished_run_is_saved()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"), Finding("nit", "B"))]), (2, [Out()])));
        var r1 = await env.Start();
        var r2 = await env.Next(r1, Resp(("R1.1", "fix"), ("R1.2", "reject")));
        Assert.Equal("done", r2.Status);
        Assert.Contains("## Hindsight", r2.Body);
        Assert.Contains($"bobar-cli review hindsight {r1.RunId} --input -", r2.Body);
        Assert.Contains("Caught several severe issues", r2.Body);

        var h = env.Engine.Hindsight(r1.RunId!, Hind(7, """[{"id": "R1.1", "why": "Churn."}]""", """[{"id": "R1.2", "why": "It was right."}]"""));
        Assert.Equal("hindsight_saved", h.Status);
        Assert.Contains("7/10", h.Body);
        var s = State(env, r1);
        Assert.Equal(Outcome.Clean, s.Finished!.Outcome);
        Assert.Equal(7, s.Hindsight!.Usefulness);
        Assert.Equal(1, s.Hindsight.AcceptedWrongly);
        Assert.Equal(1, s.Hindsight.RejectedWrongly);
        var file = RunReader.LoadHindsight(env.Run(r1))!;
        Assert.Equal("R1.1", file.AcceptedWrongly.Single().Id);
        Assert.Equal("Nothing to change.", file.Notes);

        // show now says it's in; a second one replaces the first.
        var shown = env.Engine.Show(r1.RunId!);
        Assert.DoesNotContain("## Hindsight", shown.Body);
        Assert.Contains("Your hindsight is in (7/10)", shown.Body);
        Assert.Equal("hindsight_saved", env.Engine.Hindsight(r1.RunId!, Hind(4)).Status);
        Assert.Equal(4, State(env, r1).Hindsight!.Usefulness);
        Assert.Contains("| 4/10 |", env.Engine.Runs());
    }

    [Theory]
    [InlineData(0, "[]", "[]", "usefulness must be a whole number from 1 to 10")]
    [InlineData(11, "[]", "[]", "usefulness must be")]
    [InlineData(5, """[{"id": "R1.2", "why": "x"}]""", "[]", "is not a finding you answered \"fix\"")]
    [InlineData(5, "[]", """[{"id": "R1.1", "why": "x"}]""", "is not a finding you answered \"reject\"")]
    [InlineData(5, """[{"id": "R1.1"}]""", "[]", "why is missing")]
    [InlineData(5, """[{"id": "R1.1", "why": "x"}, {"id": "R1.1", "why": "y"}]""", "[]", "listed more than once")]
    public async Task Invalid_hindsight_exits_2(int usefulness, string accepted, string rejected, string error)
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"), Finding("nit", "B"))]), (2, [Out()])));
        var r1 = await env.Start();
        await env.Next(r1, Resp(("R1.1", "fix"), ("R1.2", "reject")));
        var h = env.Engine.Hindsight(r1.RunId!, Hind(usefulness, accepted, rejected));
        Assert.Equal("invalid_hindsight", h.Status);
        Assert.Equal(2, h.ExitCode);
        Assert.Contains(error, h.Body);
        Assert.Null(State(env, r1).Hindsight);
        Assert.False(File.Exists(env.Run(r1).Hindsight));
    }

    [Fact]
    public async Task Hindsight_needs_notes()
    {
        using var env = new TestEnv(Script((1, [Out()])));
        var r = await env.Start();
        var h = env.Engine.Hindsight(r.RunId!, Hind(3, notes: "\"  \""));
        Assert.Equal("invalid_hindsight", h.Status);
        Assert.Contains("notes must be a non-empty string", h.Body);
    }

    [Fact]
    public async Task Duplicate_properties_are_invalid_not_a_crash()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "A"))]), (2, [Out()])));
        var r1 = await env.Start();
        var bad = await env.Next(r1, """{"responses": [{"id": "R1.1", "id": "R1.1", "action": "fix", "reasoning": "x"}]}""");
        Assert.Equal("invalid_response", bad.Status);
        await env.Next(r1, Resp(("R1.1", "fix")));
        var h = env.Engine.Hindsight(r1.RunId!, """{"usefulness": 5, "usefulness": 6, "acceptedWrongly": [], "rejectedWrongly": [], "notes": "n"}""");
        Assert.Equal("invalid_hindsight", h.Status);
        Assert.Equal(2, h.ExitCode);
        h = env.Engine.Hindsight(r1.RunId!, Hind(5, """[{"id": "R1.1", "why": "a", "why": "b"}]"""));
        Assert.Equal("invalid_hindsight", h.Status);
    }

    [Fact]
    public async Task Invalid_hindsight_errors_are_redacted()
    {
        using var env = new TestEnv(Script((1, [Out()])), keys: "SOME_API_KEY=sk-very-secret-value-123456\n");
        var r = await env.Start();
        var h = env.Engine.Hindsight(r.RunId!, Hind(5, """[{"id": "sk-very-secret-value-123456", "why": "x"}]"""));
        Assert.Equal("invalid_hindsight", h.Status);
        Assert.DoesNotContain("sk-very-secret-value-123456", h.Render());
    }

    [Fact]
    public async Task No_hindsight_before_the_end_or_after_a_cancel()
    {
        using var env = new TestEnv(Script((1, [Out(Finding())])));
        var r1 = await env.Start();
        var early = env.Engine.Hindsight(r1.RunId!, Hind(5));
        Assert.Equal(1, early.ExitCode);
        Assert.Equal("error", early.Status);
        Assert.Contains("the run hasn't finished yet", early.Body);

        var c = await env.Engine.CancelAsync(r1.RunId!);
        Assert.DoesNotContain("## Hindsight", c.Body);
        var late = env.Engine.Hindsight(r1.RunId!, Hind(5));
        Assert.Equal(1, late.ExitCode);
        Assert.Equal("error", late.Status);
        Assert.Contains("the run was cancelled", late.Body);
        Assert.Null(State(env, r1).Hindsight);
    }

    [Fact]
    public async Task Decided_asks_for_hindsight_and_user_sided_rejects_count()
    {
        using var env = new TestEnv(Script((1, [Out(Finding())])), maxRounds: 1);
        var r1 = await env.Start();
        await env.Next(r1, Resp(("R1.1", "reject")));
        RunFiles.WriteJson(env.Run(r1).Decisions, new DecisionsFile { Decisions = [new DecisionItem { Id = "R1.1", Decision = UserDecision.Fix }] }, BobarJson.Indented.DecisionsFile, Redactor.None);
        var r = await env.Engine.WaitAsync(r1.RunId!, CancellationToken.None);
        Assert.Equal("decided", r.Status);
        Assert.Contains("## Hindsight", r.Body);
        Assert.Equal("hindsight_saved", env.Engine.Hindsight(r1.RunId!, Hind(6, rejected: """[{"id": "R1.1", "why": "The user was right."}]""")).Status);
    }

    [Fact]
    public async Task Cancel_ends_the_run_and_releases_wait()
    {
        using var env = new TestEnv(Script((1, [Out(Finding())])), maxRounds: 1);
        var r1 = await env.Start();
        await env.Next(r1, Resp(("R1.1", "reject")));
        var wait = env.Engine.WaitAsync(r1.RunId!, CancellationToken.None);
        var c = await env.Engine.CancelAsync(r1.RunId!);
        Assert.Equal("cancelled", c.Status);
        var w = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cancelled", w.Status);
        Assert.Equal("cancelled", (await env.Engine.CancelAsync(r1.RunId!)).Status);
    }

    [Fact]
    public async Task Secrets_are_redacted_in_the_run_dir()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("bug", "leaks supersecretvalue123"))])), keys: "# comment\nSECRET_KEY=supersecretvalue123\n");
        var r = await env.Engine.StartAsync("fake", "uses key supersecretvalue123", env.Repo.Dir, CancellationToken.None);
        foreach (var f in Directory.EnumerateFiles(env.Run(r).Dir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("supersecretvalue123", File.ReadAllText(f));
        Assert.Contains("uses key ***", File.ReadAllText(env.Run(r).Desc));
    }

    [Fact]
    public async Task A_mid_run_commit_is_handled()
    {
        using var env = new TestEnv(Script((1, [Out(Finding())]), (2, [Out()])));
        var r1 = await env.Start();
        env.Repo.Git("commit", "-q", "-am", "oops, committed mid-run");
        env.Repo.Write("src/other.txt", "new file\n");
        await env.Next(r1, Resp(("R1.1", "fix")));
        var diff2 = File.ReadAllText(env.Run(r1).Round(2).Diff);
        Assert.Contains("line 2 changed", diff2); // still diffed against the original base
        Assert.Contains("src/other.txt", diff2);
        var delta = File.ReadAllText(env.Run(r1).Round(2).Delta);
        Assert.DoesNotContain("line 2 changed", delta);
        Assert.Contains("src/other.txt", delta);
    }

    [Fact]
    public async Task Show_reprints_findings_for_a_lost_session()
    {
        using var env = new TestEnv(Script((1, [Out(Finding("risk", "Shown again", null, "src/app.txt:2", "missing.cs:1"))])));
        var r1 = await env.Start();
        var s = env.Engine.Show(r1.RunId![..18]); // unique prefix
        Assert.Equal("findings", s.Status);
        Assert.Contains("### R1.1 [risk] Shown again", s.Body);
        Assert.Contains("(warning: path not found: missing.cs)", s.Body);
        Assert.Contains("| " + r1.RunId, env.Engine.Runs());
    }

    [Fact]
    public async Task Default_fake_reviewer_runs_end_to_end()
    {
        using var env = new TestEnv();
        var r1 = await env.Start();
        Assert.Equal("findings", r1.Status);
        Assert.Contains("src/app.txt:1", r1.Body);
        var r2 = await env.Next(r1, Resp(("R1.1", "fix"), ("R1.2", "reject")));
        Assert.Equal("findings", r2.Status);
        Assert.Contains("(maintained from R1.2)", r2.Body);
        var r3 = await env.Next(r2, Resp(("R2.1", "reject")));
        Assert.Equal("awaiting_decisions", r3.Status);
    }

    [Fact]
    public async Task Thread_follows_previous_ids_back_through_rounds()
    {
        using var env = new TestEnv(Script(
            (1, [Out(Finding("bug", "A"))]),
            (2, [Out(Finding("bug", "A again", previousId: "R1.1"), Finding("nit", "N"))])));
        var r1 = await env.Start();
        var r2 = await env.Next(r1, Resp(("R1.1", "reject")));
        var r3 = await env.Next(r2, Resp(("R2.1", "reject"), ("R2.2", "reject")));
        Assert.Equal("findings", r3.Status); // R2.2 was new, so round 3 (default fake: maintains both)
        var r4 = await env.Next(r3, Resp(("R3.1", "reject"), ("R3.2", "reject")));
        Assert.Equal("awaiting_decisions", r4.Status);
        var s = State(env, r1);
        var thread = RunReader.Thread(RunReader.LoadRounds(env.Run(r1), s), "R3.1");
        Assert.Equal(["R1.1", "R2.1", "R3.1"], thread.Select(t => t.Finding.Id));
        Assert.All(thread, t => Assert.Equal(AuthorAction.Reject, t.Response!.Action));
    }

    [Fact]
    public async Task Submitting_decisions_requires_every_awaited_id()
    {
        using var env = new TestEnv(Script((1, [Out(Finding(), Finding("nit"))])), maxRounds: 1);
        var r1 = await env.Start();
        await env.Next(r1, Resp(("R1.1", "reject"), ("R1.2", "reject")));
        var err = await RunActions.SubmitDecisionsAsync(env.Run(r1), new DecisionsFile { Decisions = [new DecisionItem { Id = "R1.1" }] }, Redactor.None, TimeProvider.System);
        Assert.Contains("no decision for R1.2", err);
        Assert.False(File.Exists(env.Run(r1).Decisions));
    }
}

public class CommitRangeTests
{
    private static Dictionary<int, IEnumerable<FakeStep>> Script(params (int Round, FakeStep[] Steps)[] rounds)
        => rounds.ToDictionary(r => r.Round, r => (IEnumerable<FakeStep>)r.Steps);

    /// <summary>initial ← "Add feature" (feat.txt) ← "Force-add a log" (keep.log, which .gitignore matches); app.txt dirty.</summary>
    private static TestEnv WithTwoCommits(Dictionary<int, IEnumerable<FakeStep>> script)
    {
        var env = new TestEnv(script);
        env.Repo.Write("feat.txt", "feature\n");
        env.Repo.Git("add", "feat.txt");
        env.Repo.Git("commit", "-q", "-m", "Add feature\n\nWhy: because.");
        env.Repo.Write("keep.log", "kept\n");
        env.Repo.Git("add", "-f", "keep.log");
        env.Repo.Git("commit", "-q", "-m", "Force-add a log");
        return env;
    }

    private static Task<CommandResult> StartFrom(TestEnv env, string from, string? notes = null)
        => env.Engine.StartAsync("fake", notes, env.Repo.Dir, CancellationToken.None, from);

    [Fact]
    public async Task Range_reviews_commits_plus_worktree_with_messages_as_description()
    {
        using var env = WithTwoCommits(Script((1, [Out(Finding("bug", "A"))]), (2, [Out()])));
        var r1 = await StartFrom(env, "HEAD~1", "Extra note.");
        Assert.Equal("findings", r1.Status);
        var run = env.Run(r1);

        var desc = File.ReadAllText(run.Desc);
        Assert.Contains("2 commits ending at HEAD, plus uncommitted changes", desc);
        Assert.True(desc.IndexOf("Add feature") < desc.IndexOf("Force-add a log"));
        Assert.Contains("Why: because.", desc);
        Assert.Contains("Extra note.", desc);
        Assert.DoesNotContain("initial", desc);

        var diff1 = File.ReadAllText(run.Round(1).Diff);
        Assert.Contains("+feature", diff1);
        Assert.Contains("+kept", diff1);
        Assert.Contains("+line 2 changed", diff1);
        Assert.Contains("recent commits", File.ReadAllText(run.Round(1).Prompt));

        var started = RunReader.LoadState(run).Started!;
        Assert.Equal(env.Repo.Git("rev-parse", "HEAD~2").Trim(), started.Base);
        Assert.Equal(env.Repo.Git("rev-parse", "HEAD").Trim(), started.Seed);
        Assert.Equal(["Add feature", "Force-add a log"], started.Commits.Select(c => c.Subject));

        // Round 2: the snapshot is seeded from HEAD, so the committed-but-ignored keep.log stays in the diff.
        env.Repo.Write("feat.txt", "feature fixed\n");
        var r2 = await env.Next(r1, Resp(("R1.1", "fix")));
        Assert.Equal("done", r2.Status);
        var diff2 = File.ReadAllText(run.Round(2).Diff);
        Assert.Contains("+kept", diff2);
        Assert.DoesNotContain("-kept", diff2);
        Assert.Contains("+feature fixed", File.ReadAllText(run.Round(2).Delta));
        Assert.Equal("", env.Repo.Git("diff", "HEAD", "--", "keep.log"));
    }

    [Fact]
    public async Task Range_without_notes_and_clean_worktree()
    {
        using var env = WithTwoCommits(Script((1, [Out()])));
        env.Repo.Git("checkout", "-q", "--", "src/app.txt");
        var r = await StartFrom(env, "HEAD");
        Assert.Equal("done", r.Status);
        var desc = File.ReadAllText(env.Run(r).Desc);
        Assert.StartsWith("The change under review is 1 commit ending at HEAD. ", desc);
        Assert.DoesNotContain("Notes from the author", desc);
        var diff = File.ReadAllText(env.Run(r).Round(1).Diff);
        Assert.Contains("+kept", diff);
        Assert.DoesNotContain("feature", diff);
    }

    [Fact]
    public async Task Range_from_the_root_commit_diffs_against_the_empty_tree()
    {
        using var env = WithTwoCommits(Script((1, [Out()])));
        var root = env.Repo.Git("rev-list", "--max-parents=0", "HEAD").Trim();
        var r = await StartFrom(env, root);
        Assert.Equal("done", r.Status);
        Assert.Equal(Bobar.Core.Git.GitCli.EmptyTree, RunReader.LoadState(env.Run(r)).Started!.Base);
        Assert.Contains("+line 1", File.ReadAllText(env.Run(r).Round(1).Diff));
        Assert.Contains("3 commits", File.ReadAllText(env.Run(r).Desc));
    }

    [Theory]
    [InlineData("nosuchrev", "is not a commit")]
    [InlineData("side", "is not HEAD or an ancestor")]
    public async Task Bad_from_fails_without_creating_a_run(string from, string error)
    {
        using var env = WithTwoCommits(Script((1, [Out()])));
        env.Repo.Git("checkout", "-q", "-b", "side");
        env.Repo.Write("side.txt", "x\n");
        env.Repo.Git("add", "side.txt");
        env.Repo.Git("commit", "-q", "-m", "side");
        env.Repo.Git("checkout", "-q", "main");
        var r = await StartFrom(env, from);
        Assert.Equal("error", r.Status);
        Assert.Contains(error, r.Body);
        Assert.Null(r.RunId);
    }

    [Fact]
    public async Task Range_with_no_net_change_fails()
    {
        using var env = new TestEnv(Script((1, [Out()])));
        env.Repo.Git("checkout", "-q", "--", "src/app.txt");
        env.Repo.Write("tmp.txt", "x\n");
        env.Repo.Git("add", "tmp.txt");
        env.Repo.Git("commit", "-q", "-m", "add tmp");
        env.Repo.Git("rm", "-q", "tmp.txt");
        env.Repo.Git("commit", "-q", "-m", "remove tmp");
        var r = await StartFrom(env, "HEAD~1");
        Assert.Equal("error", r.Status);
        Assert.Contains("no net change", r.Body);
    }
}
