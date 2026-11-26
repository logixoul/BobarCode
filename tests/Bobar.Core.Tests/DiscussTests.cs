using System.Text.Json;
using Bobar.Core.IO;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;
using Bobar.Core.Runs;
using Bobar.Core.State;

namespace Bobar.Core.Tests;

/// <summary>Discussions (`discuss start` / `ask` / `report`, `review wait` and `hindsight` on them) and the run id format.</summary>
public class DiscussTests
{
    private const string Question = "Which speakers should I buy?";

    private static FakeStep Turn(string solution, object? messages = null)
        => FakeStep.Json(JsonSerializer.Serialize(messages is null ? new { solution } : (object)new { solution, messages }));

    private static Dictionary<int, IEnumerable<FakeStep>> Steps(params (int Round, FakeStep Step)[] steps)
        => steps.GroupBy(s => s.Round).ToDictionary(g => g.Key, g => (IEnumerable<FakeStep>)g.Select(s => s.Step).ToList());

    private static TestEnv Env(Dictionary<string, Dictionary<int, IEnumerable<FakeStep>>?> discussers) => new(discussers: discussers);

    private static Task<CommandResult> Start(TestEnv env, string models, int rounds = 4, string? dir = null, string slug = "speakers")
        => env.Engine.DiscussStartAsync(models.Split(','), Question, slug, dir, rounds, CancellationToken.None);

    private static RunState State(TestEnv env, CommandResult r) => RunReader.LoadState(env.Run(r));

    [Fact]
    public async Task A_discussion_runs_every_round_and_routes_solutions_and_messages()
    {
        using var env = Env(new()
        {
            ["a"] = Steps((1, Turn("A1", new { b = "ignored in round 1" })), (2, Turn("A2", new { b = "B, your plan ignores the budget.", zed = "hello?", a = "note to self" })), (4, Turn("A4", new { b = "ignored: final round" }))),
            ["b"] = Steps((1, Turn("B1")), (2, Turn("B2"))),
            ["c"] = Steps((1, Turn("C1"))),
        });
        var calls = new List<ReviewerCall>();
        foreach (var f in env.Fakes.Values) f.OnCall = c => { lock (calls) calls.Add(c); };

        var r = await Start(env, "a,b,c");
        Assert.Equal("discussed", r.Status);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_speakers$", r.RunId);
        var run = env.Run(r);
        foreach (var sh in new[] { "a", "b", "c" }) Assert.Contains($"- {sh}: round 4: `{run.Round(4).Discusser(sh).Solution}`", r.Body);
        Assert.Contains("discuss ask", r.Body);
        Assert.Contains("discuss report", r.Body);

        var s = State(env, r);
        Assert.True(s.IsDiscuss);
        Assert.Equal(RunPhase.AwaitingReport, s.Phase);
        Assert.Equal(4, s.DiscussRounds.Count);
        Assert.False(s.DiscussionEnded!.Early);
        Assert.Equal(RunState.NoFolder, s.RepoName);
        Assert.Equal("speakers", s.Name);

        // No folder: an empty cwd in the run dir, and no file tools. Every turn has the discussion schema.
        Assert.All(calls, c => Assert.Equal(run.Cwd, c.RepoDir));
        Assert.All(calls, c => Assert.False(c.FileTools));
        Assert.All(calls.Where(c => c.Round is 1 or 4), c => Assert.Equal(Validation.DiscussSchema(false), c.JsonSchema));
        Assert.All(calls.Where(c => c.Round is 2 or 3), c => Assert.Equal(Validation.DiscussSchema(true), c.JsonSchema));

        // Messages: none from round 1 or the final round; the round-2 ones all kept, shown (undelivered where the recipient isn't there) in round 3.
        Assert.Empty(RunFiles.ReadJson(run.Round(1).Discusser("a").Output, BobarJson.Compact.DiscussTurnFile)!.Messages);
        Assert.Empty(RunFiles.ReadJson(run.Round(4).Discusser("a").Output, BobarJson.Compact.DiscussTurnFile)!.Messages);
        Assert.Equal(3, RunFiles.ReadJson(run.Round(2).Discusser("a").Output, BobarJson.Compact.DiscussTurnFile)!.Messages.Count);
        Assert.Contains("## To b", File.ReadAllText(run.Round(2).Discusser("a").Solution));

        var b1 = File.ReadAllText(run.Round(1).Discusser("b").Prompt);
        Assert.Contains("You are \"b\", one of 3 participants", b1);
        Assert.Contains(Question, b1);
        Assert.Contains("no file tools", b1);
        Assert.DoesNotContain("\"messages\"", b1);
        var b2 = File.ReadAllText(run.Round(2).Discusser("b").Prompt);
        Assert.Contains("### a's solution (round 1)\n\nA1", b2);
        Assert.Contains("### c's solution (round 1)\n\nC1", b2);
        Assert.DoesNotContain("B1", b2); // its own solution is in its session
        Assert.DoesNotContain("Messages from round", b2);
        Assert.Contains("\"messages\"", b2);
        var b3 = File.ReadAllText(run.Round(3).Discusser("b").Prompt);
        Assert.Contains("### a's solution (round 2)\n\nA2", b3);
        Assert.Contains("**a → b**\n\nB, your plan ignores the budget.", b3);
        Assert.Contains("**a → zed** (undelivered: zed isn't in the discussion)", b3);
        Assert.Contains("**a → a** (undelivered", b3);
        var b4 = File.ReadAllText(run.Round(4).Discusser("b").Prompt);
        Assert.Contains("(the final round)", b4);
        Assert.Contains("no messages", b4);
        Assert.DoesNotContain("\"messages\"", b4);

        // Each participant resumes its own session.
        Assert.Equal([null, "fake-session-1", "fake-session-1", "fake-session-1"], env.Fakes["a"].ResumeIds);

        // `next` and a report before... no: ask, wait for the answers, report, hindsight.
        Assert.Contains("discuss ask", (await env.Engine.NextAsync(r.RunId!, "{}", CancellationToken.None)).Body);
        Assert.Equal("error", env.Engine.Hindsight(r.RunId!, """{"score": 5, "reason": "r", "notes": "n"}""").Status);
        var bad = env.Engine.DiscussAsk(r.RunId!, """{"questions": [{"id": "Q1", "title": "Budget?", "options": [{"label": "only one"}]}]}""");
        Assert.Equal(("invalid_questions", 2), (bad.Status, bad.ExitCode));
        Assert.Contains("needs 2 to 4 options", bad.Body);

        var ask = env.Engine.DiscussAsk(r.RunId!, """{"questions": [{"id": "Q1", "title": "Budget?", "context": "a says 300, b says 800.", "options": [{"label": "300"}, {"label": "800", "description": "b's plan"}]}], "decided": "Brand: everyone agreed."}""");
        Assert.Equal("awaiting_answers", ask.Status);
        Assert.Contains("BobarCode is open on the run", ask.Body);
        Assert.Equal([r.RunId!], env.GuiLaunches);
        Assert.Equal(RunPhase.AwaitingAnswers, State(env, r).Phase);
        Assert.Equal("error", env.Engine.DiscussAsk(r.RunId!, "{}").Status); // once per run
        Assert.Contains("hasn't answered", env.Engine.DiscussReport(r.RunId!, "# Report").Body);

        var wait = env.Engine.WaitAsync(r.RunId!, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        RunFiles.WriteJson(run.Answers, new AnswersFile { Answers = [new AnswerItem { Id = "Q1", Option = 1, Comment = "Go big." }], Comment = "Thanks." }, BobarJson.Indented.AnswersFile, Redactor.None);
        var answered = await wait;
        Assert.Equal("answered", answered.Status);
        Assert.Contains("### Q1. Budget?\nChose: 800\nComment: Go big.", answered.Body);
        Assert.Contains("Overall comment from the user: Thanks.", answered.Body);
        Assert.Equal(1, State(env, r).Answers!.Count);
        Assert.Equal("answered", env.Engine.Show(r.RunId!).Status);

        var rep = env.Engine.DiscussReport(r.RunId!, "# Final solutions\n...");
        Assert.Equal("reported", rep.Status);
        Assert.Contains("## Hindsight", rep.Body);
        Assert.Equal(Outcome.Reported, State(env, r).Finished!.Outcome);
        Assert.Equal("# Final solutions\n...", RunFiles.ReadJson(run.Report, BobarJson.Compact.ReportFile)!.Markdown);

        var badH = env.Engine.Hindsight(r.RunId!, """{"score": 11, "reason": "", "notes": "n"}""");
        Assert.Equal(("invalid_hindsight", 2), (badH.Status, badH.ExitCode));
        Assert.Contains("score must be", badH.Body);
        Assert.Contains("reason must be", badH.Body);
        var h = env.Engine.Hindsight(r.RunId!, """{"score": 8, "reason": "b's budget point changed the outcome.", "notes": "Nothing to change."}""");
        Assert.Equal("hindsight_saved", h.Status);
        Assert.Equal(8, State(env, r).Hindsight!.Usefulness);
        Assert.Equal(8, RunFiles.ReadJson(run.Hindsight, BobarJson.Compact.DiscussHindsightFile)!.Score);

        var feed = string.Join("\n", Summary.Lines(State(env, r)).Select(l => l.Text));
        Assert.Contains("Round 1 of 4: a, b, c answering…", feed);
        Assert.Contains("a revised its solution (3 messages: b, zed, a).", feed);
        Assert.Contains("The author asked you 1 question.", feed);
        Assert.Contains("The author sent its report. Done.", feed);
    }

    [Fact]
    public async Task A_folder_is_the_cwd_with_file_tools()
    {
        using var env = Env(new() { ["a"] = null, ["b"] = null });
        var calls = new List<ReviewerCall>();
        foreach (var f in env.Fakes.Values) f.OnCall = c => { lock (calls) calls.Add(c); };
        var r = await Start(env, "a,b", rounds: 2, dir: env.Repo.Dir);
        Assert.Equal("discussed", r.Status);
        Assert.All(calls, c => Assert.True(c.FileTools));
        Assert.All(calls, c => Assert.Equal(Path.GetFullPath(env.Repo.Dir).TrimEnd('\\', '/'), c.RepoDir));
        var s = State(env, r);
        Assert.Equal(Path.GetFileName(env.Repo.Dir), s.RepoName);
        Assert.Contains("is the project the question is about", File.ReadAllText(env.Run(r).Round(1).Discusser("a").Prompt));
        Assert.False(Directory.Exists(env.Run(r).Cwd));
        // Two rounds: round 2 is the final one, so nobody ever writes messages.
        Assert.All(calls, c => Assert.Equal(Validation.DiscussSchema(false), c.JsonSchema));
    }

    [Fact]
    public async Task Dropouts_continue_with_the_rest_and_end_early_below_two()
    {
        using var env = Env(new()
        {
            ["a"] = Steps((1, FakeStep.Error("bad key 401"))),
            // Round 2 resumes a session: a failure is redone once in a fresh session, which fails too.
            ["b"] = Steps((1, Turn("B1")), (2, FakeStep.Error("still 401")), (2, FakeStep.Error("still 401"))),
            ["c"] = Steps((1, Turn("C1")), (2, Turn("C2"))),
        });
        var r = await Start(env, "a,b,c");
        Assert.Equal("discussed", r.Status);
        Assert.Contains("ended early, after round 2 of 4", r.Body);
        Assert.Contains("- a: dropped out in round 1 (bad key 401): no solution", r.Body);
        Assert.Contains($"- b: dropped out in round 2 (still 401); its last solution is from round 1: `{env.Run(r).Round(1).Discusser("b").Solution}`", r.Body);
        Assert.Contains($"- c: round 2: `{env.Run(r).Round(2).Discusser("c").Solution}`", r.Body);
        var s = State(env, r);
        Assert.True(s.DiscussionEnded!.Early);
        Assert.Equal(["b", "c"], s.DiscussRounds[1].Live);
        Assert.Equal(2, s.DiscussRounds.Count);
        // c's round-2 prompt has b's round-1 solution but nothing from a.
        var c2 = File.ReadAllText(env.Run(r).Round(2).Discusser("c").Prompt);
        Assert.Contains("### b's solution (round 1)", c2);
        Assert.DoesNotContain("a's solution", c2);
        Assert.Contains("b dropped out in round 2", string.Join("\n", Summary.Lines(s).Select(l => l.Text)));

        // Every turn that ran opens in the GUI (a dropped one shows its activity); a participant who never ran in a round doesn't.
        var table = DiscussTableModel.Build(s, DateTimeOffset.Now);
        var a1 = table.Rows[0].Cells.Single(c => c.Discusser == "a");
        Assert.Equal(DiscussCellState.Dropped, a1.State);
        Assert.True(a1.CanOpen);
        Assert.False(table.Rows[1].Cells.Single(c => c.Discusser == "a").CanOpen);
        Assert.True(table.Rows[1].Cells.Single(c => c.Discusser == "c").CanOpen);
    }

    [Fact]
    public async Task Everyone_dropping_out_after_round_1_still_hands_over_their_solutions()
    {
        using var env = Env(new()
        {
            ["a"] = Steps((1, Turn("A1")), (2, FakeStep.Error("boom")), (2, FakeStep.Error("boom"))),
            ["b"] = Steps((1, Turn("B1")), (2, FakeStep.Error("boom")), (2, FakeStep.Error("boom"))),
        });
        var r = await Start(env, "a,b", rounds: 3);
        Assert.Equal("discussed", r.Status);
        Assert.Contains("its last solution is from round 1", r.Body);
        Assert.True(State(env, r).DiscussionEnded!.Early);
    }

    [Fact]
    public async Task No_solution_at_all_is_an_error_without_a_hindsight()
    {
        using var env = Env(new() { ["a"] = Steps((1, FakeStep.Error("boom"))), ["b"] = Steps((1, FakeStep.Error("boom"))) });
        var r = await Start(env, "a,b");
        Assert.Equal(("error", 1), (r.Status, r.ExitCode));
        Assert.Contains("no participant produced a solution", r.Body);
        var s = State(env, r);
        Assert.False(s.WantsHindsight);
        Assert.DoesNotContain("## Hindsight", r.Body);
    }

    [Fact]
    public async Task Invalid_output_gets_one_repair_turn_and_transient_errors_a_retry()
    {
        using var env = Env(new()
        {
            ["a"] = Steps((1, FakeStep.Json("I think you should buy KEF.")), (1, Turn("A1 fixed"))),
            ["b"] = Steps((1, FakeStep.Error("503 overloaded", transient: true)), (1, Turn("B1"))),
        });
        var r = await Start(env, "a,b", rounds: 2);
        Assert.Equal("discussed", r.Status);
        var s = State(env, r);
        Assert.True(s.DiscussRounds[0].Replies["a"].Repaired);
        Assert.Single(s.DiscussRounds[0].Repairs);
        Assert.Single(s.DiscussRounds[0].Retries, x => x.Discusser == "b");
        Assert.Contains("Reply again with only the corrected JSON object ({\"solution\": \"...\"})", env.Fakes["a"].Calls[1].Prompt);
        Assert.Equal("A1 fixed", RunFiles.ReadJson(env.Run(r).Round(1).Discusser("a").Output, BobarJson.Compact.DiscussTurnFile)!.Solution);
    }

    [Fact]
    public async Task A_solution_containing_a_code_fence_still_parses()
    {
        var solution = "Use this:\n```python\nprint(1)\n```\nDone.";
        using var env = Env(new() { ["a"] = Steps((1, Turn(solution))), ["b"] = null });
        var r = await Start(env, "a,b", rounds: 2);
        Assert.Equal("discussed", r.Status);
        Assert.Empty(State(env, r).DiscussRounds[0].Repairs);
        Assert.Equal(solution, RunFiles.ReadJson(env.Run(r).Round(1).Discusser("a").Output, BobarJson.Compact.DiscussTurnFile)!.Solution);
    }

    [Fact]
    public async Task A_failed_resume_is_redone_in_a_fresh_session_with_the_full_context()
    {
        using var env = Env(new()
        {
            ["a"] = Steps((1, Turn("A1")), (2, FakeStep.Error("No session found matching 'fake-session-1'")), (2, Turn("A2"))),
            ["b"] = Steps((1, Turn("B1"))),
        });
        var r = await Start(env, "a,b", rounds: 2);
        Assert.Equal("discussed", r.Status);
        Assert.Single(State(env, r).DiscussRounds[1].Retries, x => x.Fresh && x.Discusser == "a");
        var fresh = File.ReadAllText(env.Run(r).Round(2).Discusser("a").Prompt);
        Assert.Contains("## The question", fresh);
        Assert.Contains("## Your own latest solution\n\nFrom round 1:\n\nA1", fresh);
        Assert.Contains("### b's solution (round 1)", fresh);
        Assert.Equal([null, "fake-session-1", null], env.Fakes["a"].ResumeIds);
    }

    [Fact]
    public async Task Concurrent_replies_all_land_in_the_event_log()
    {
        const int n = 6;
        var names = Enumerable.Range(0, n).Select(i => $"p{i}").ToList();
        using var env = Env(names.ToDictionary(x => x, _ => (Dictionary<int, IEnumerable<FakeStep>>?)null));
        using var barrier = new Barrier(n);
        foreach (var f in env.Fakes.Values) f.OnCall = c => { if (c.Round == 1) barrier.SignalAndWait(TimeSpan.FromSeconds(10)); };
        var r = await Start(env, string.Join(",", names), rounds: 2);
        Assert.Equal("discussed", r.Status);
        var s = State(env, r);
        Assert.All(s.DiscussRounds, round => Assert.Equal(n, round.Replies.Count));
        Assert.Equal(1 + 2 * (1 + n) + 1, EventLog.Read(env.Run(r)).Count);
    }

    [Theory]
    [InlineData("a", "at least two participants")]
    [InlineData("a,A", "listed more than once")]
    [InlineData("a,con", "reserved file name")]
    [InlineData("a,x/y", "only letters, digits")]
    [InlineData("a,nobody", "Unknown model 'nobody'")]
    public async Task Bad_participants_are_refused_before_a_run_exists(string models, string error)
    {
        using var env = Env(new() { ["a"] = null, ["A"] = null, ["con"] = null });
        var r = await Start(env, models);
        Assert.Equal("error", r.Status);
        Assert.Null(r.RunId);
        Assert.Contains(error, r.Body);
        Assert.Empty(RunReader.ListRuns(env.Paths));
    }

    [Fact]
    public async Task Bad_slug_rounds_and_dir_are_refused()
    {
        using var env = Env(new() { ["a"] = null, ["b"] = null });
        Assert.Contains("--slug", (await Start(env, "a,b", slug: "Big Speakers")).Body);
        Assert.Contains("--rounds", (await Start(env, "a,b", rounds: 1)).Body);
        Assert.Contains("does not exist", (await Start(env, "a,b", dir: Path.Combine(env.Root, "nope"))).Body);
    }

    [Fact]
    public void Answers_must_cover_each_question_with_an_option_or_a_comment()
    {
        var q = new QuestionsFile { Questions = [new QuestionItem { Id = "Q1", Title = "t", Options = [new() { Label = "x" }, new() { Label = "y" }] }, new QuestionItem { Id = "Q2", Title = "t", Options = [new() { Label = "x" }, new() { Label = "y" }] }] };
        Assert.True(Validation.Answers(new AnswersFile { Answers = [new() { Id = "Q1", Option = 0 }, new() { Id = "Q2", Comment = "neither" }] }, q).Ok);
        var v = Validation.Answers(new AnswersFile { Answers = [new() { Id = "Q1", Option = 5 }, new() { Id = "Q3", Option = 0 }] }, q);
        Assert.Contains("no answer for Q2", v.ErrorText);
        Assert.Contains("Q1: option 5 is out of range", v.ErrorText);
        Assert.Contains("Q3 is not a question", v.ErrorText);
        Assert.Contains("pick an option or write a comment", Validation.Answers(new AnswersFile { Answers = [new() { Id = "Q1" }, new() { Id = "Q2", Option = 1 }] }, q).ErrorText);
    }

    [Fact]
    public void Run_ids_carry_the_time_and_name_and_sort_across_both_formats()
    {
        var now = new DateTimeOffset(2026, 11, 25, 14, 30, 5, TimeSpan.Zero);
        Assert.Equal("2026-11-25_14-30-05_my_repo", RunIds.New("my repo", now, _ => false));
        var taken = new HashSet<string> { "2026-11-25_14-30-05_x", "2026-11-25_14-30-05_x-2" };
        Assert.Equal("2026-11-25_14-30-05_x-3", RunIds.New("x", now, taken.Contains));
        Assert.Equal(new DateTime(2026, 11, 25, 14, 30, 5), RunIds.StartTime("2026-11-25_14-30-05_x-3"));
        Assert.Equal(new DateTime(2026, 11, 17, 14, 23, 1), RunIds.StartTime("20261117-142301-Fractory-a7f2"));
        Assert.Null(RunIds.StartTime("whatever"));

        var ids = new[] { "20261124-090000-old-aaaa", "2026-11-25_08-00-00_new", "20261126-010000-later-bbbb", "junk", "2026-11-25_08-00-00_new-2" };
        Assert.Equal(["20261126-010000-later-bbbb", "2026-11-25_08-00-00_new-2", "2026-11-25_08-00-00_new", "20261124-090000-old-aaaa", "junk"], RunIds.NewestFirst(ids, x => x).ToList());
    }

    [Fact]
    public async Task A_new_run_lists_before_many_old_format_runs()
    {
        using var env = new TestEnv();
        for (var i = 0; i < 12; i++)
        {
            var old = env.Paths.Run($"2099010{i % 9 + 1}-000000-old-{i:x4}".Replace("2099", "2020"));
            Directory.CreateDirectory(old.Dir);
            EventLog.Append(old, new RunStarted { T = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), Repo = env.Repo.Dir }, Redactor.None);
        }
        var r = await env.Start();
        Assert.Equal(r.RunId, RunReader.ListRuns(env.Paths)[0].RunId);
        Assert.Equal(Path.GetFileName(env.Repo.Dir), RunReader.LoadState(env.Run(r)).Started!.Name);
    }
}
