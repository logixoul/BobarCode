using System.Diagnostics;
using Bobar.Core.Git;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Proc;
using Bobar.Core.Reviewers;
using Bobar.Core.State;

namespace Bobar.Core.Tests;

public class LoopRulesTests
{
    private static StoredFinding F(string id, FindingOrigin o) => new() { Id = id, Origin = o };
    private static AuthorResponseItem R(string id, AuthorAction a) => new() { Id = id, Action = a, Reasoning = "x" };

    [Fact]
    public void Cap_with_a_rejection_awaits_decisions()
        => Assert.Equal((AfterResponse.AwaitDecisions, AwaitReason.Cap),
            LoopRules.Decide(5, 5, [F("R5.1", FindingOrigin.New)], [R("R5.1", AuthorAction.Reject)]));

    [Fact]
    public void Cap_without_rejections_is_done()
        => Assert.Equal((AfterResponse.DoneCap, null),
            LoopRules.Decide(5, 5, [F("R5.1", FindingOrigin.New)], [R("R5.1", AuthorAction.Fix)]));

    [Fact]
    public void Any_fix_goes_to_the_next_round()
        => Assert.Equal((AfterResponse.NextRound, null),
            LoopRules.Decide(2, 5, [F("R2.1", FindingOrigin.Maintained), F("R2.2", FindingOrigin.Maintained)], [R("R2.1", AuthorAction.Fix), R("R2.2", AuthorAction.Reject)]));

    [Fact]
    public void All_maintained_and_rejected_is_a_deadlock()
        => Assert.Equal((AfterResponse.AwaitDecisions, AwaitReason.Deadlock),
            LoopRules.Decide(2, 5, [F("R2.1", FindingOrigin.Maintained)], [R("R2.1", AuthorAction.Reject)]));

    [Theory]
    [InlineData(FindingOrigin.New)]
    [InlineData(FindingOrigin.Repeated)]
    public void A_new_or_repeated_finding_gets_another_round(FindingOrigin other)
        => Assert.Equal((AfterResponse.NextRound, null),
            LoopRules.Decide(2, 5, [F("R2.1", FindingOrigin.Maintained), F("R2.2", other)], [R("R2.1", AuthorAction.Reject), R("R2.2", AuthorAction.Reject)]));

    [Fact]
    public void Classify_counts_maintained_repeated_new_and_withdrawn()
    {
        var (origins, withdrawn) = LoopRules.Classify(["R1.1", "R1.3", null], ["R1.1", "R1.2"]);
        Assert.Equal([FindingOrigin.Maintained, FindingOrigin.Repeated, FindingOrigin.New], origins);
        Assert.Equal(["R1.2"], withdrawn);
    }
}

public class ValidationTests
{
    [Fact]
    public void Previous_id_must_name_an_earlier_finding()
    {
        var node = Validation.ParseModelJson("""{"findings":[{"severity":"bug","title":"t","explanation":"e","refs":[],"previousId":"R1.4"}]}""");
        var v = Validation.Review(node, new HashSet<string> { "R1.1", "R1.2" });
        Assert.False(v.Ok);
        Assert.Contains("R1.4", v.ErrorText);
        Assert.Contains("valid: R1.1, R1.2", v.ErrorText);
        Assert.True(Validation.Review(node, new HashSet<string> { "R1.4" }).Ok);
    }

    [Fact]
    public void Missing_refs_are_allowed_but_fields_are_required()
    {
        var ok = Validation.Review(Validation.ParseModelJson("""{"findings":[{"severity":"nit","title":"t","explanation":"e"}]}"""), new HashSet<string>());
        Assert.True(ok.Ok);
        var bad = Validation.Review(Validation.ParseModelJson("""{"findings":[{"severity":"major","explanation":""}]}"""), new HashSet<string>());
        Assert.Equal(3, bad.Errors.Count);
    }

    [Theory]
    [InlineData("```json\n{\"findings\":[]}\n```")]
    [InlineData("Here are my findings:\n{\"findings\":[]}\nThanks!")]
    [InlineData("{\"findings\":[]}")]
    public void Model_json_is_extracted_leniently(string text)
        => Assert.NotNull(Validation.ParseModelJson(text));

    [Fact]
    public void Response_keeps_the_round_order()
    {
        var v = Validation.Response("""{"responses":[{"id":"R1.2","action":"fix"},{"id":"R1.1","action":"reject","reasoning":"no"}]}""", ["R1.1", "R1.2"]);
        Assert.True(v.Ok);
        Assert.Equal(["R1.1", "R1.2"], v.Value!.Responses.Select(r => r.Id));
    }

    [Fact]
    public void Ids_sort_numerically()
        => Assert.Equal(["R1.2", "R1.10", "R2.1"], new[] { "R2.1", "R1.10", "R1.2" }.Order(IdComparer.Instance));
}

public class SnapshotTests
{
    [Fact]
    public async Task Untracked_included_ignored_excluded_real_index_untouched()
    {
        using var repo = new TempRepo();
        repo.Write("src/app.txt", "changed\n");
        repo.Write("new.txt", "untracked\n");
        repo.Write("debug.log", "ignored\n");
        repo.Write("staged.txt", "staged\n");
        repo.Git("add", "staged.txt");
        var indexFile = Path.Combine(repo.Dir, ".git", "index");
        var indexBefore = File.ReadAllBytes(indexFile);
        var statusBefore = repo.Git("status", "--porcelain");

        var head = await GitCli.HeadOrEmptyAsync(repo.Dir, default);
        var tmp = Path.Combine(Path.GetTempPath(), "bobar-idx-" + Guid.NewGuid().ToString("N"));
        var tree = await GitCli.SnapshotAsync(repo.Dir, head, tmp, default);
        var diff = await GitCli.DiffAsync(repo.Dir, head, tree, default);

        Assert.Contains("new.txt", diff);
        Assert.Contains("staged.txt", diff);
        Assert.Contains("+changed", diff);
        Assert.DoesNotContain("debug.log", diff);
        Assert.Equal(indexBefore, File.ReadAllBytes(indexFile));
        Assert.Equal(statusBefore, repo.Git("status", "--porcelain"));
        Assert.False(File.Exists(tmp));
        Assert.Empty(repo.Git("stash", "list"));
    }

    [Fact]
    public async Task Works_in_a_repo_without_commits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bobar-empty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await GitCli.RunAsync(dir, ["init", "-q"], default);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello\n");
        var head = await GitCli.HeadOrEmptyAsync(dir, default);
        Assert.Equal(GitCli.EmptyTree, head);
        var tree = await GitCli.SnapshotAsync(dir, head, Path.Combine(dir, "..", Path.GetFileName(dir) + ".idx"), default);
        Assert.Contains("+hello", await GitCli.DiffAsync(dir, head, tree, default));
    }
}

public class SummaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 14, 23, 1, TimeSpan.Zero);

    private static ReviewReceived Received(int round, int ms, double cost, int withdrawn, params (Severity S, FindingOrigin O)[] f) => new()
    {
        T = T0,
        Round = round,
        Counts = SeverityCounts.Of(f.Select(x => x.S)),
        Findings = f.Select((x, i) => new FindingBrief { Id = $"R{round}.{i + 1}", Severity = x.S, Origin = x.O }).ToList(),
        Withdrawn = withdrawn,
        DurationMs = ms,
        CostUsd = cost,
    };

    [Fact]
    public void Reviewer_stats_group_by_reviewer_and_model()
    {
        var ds = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "lithos", Model = "deepseek-ai/DeepSeek-V4.1-Flash-ultra" };
        var opus = new ReviewerConfig { Kind = ReviewerKinds.Claude, Model = "opus" };
        RunState Run(string id, string reviewer, ReviewerConfig cfg, Outcome outcome, int fixes, int rejects, double cost, int? usefulness = null, int accW = 0, int rejW = 0)
        {
            var ev = new List<RunEvent>
            {
                new RunStarted { T = T0, Reviewer = reviewer, ReviewerConfig = cfg, MaxRounds = 5 },
                new ReviewRequested { T = T0, Round = 1 },
                Received(1, 60_000, cost, 0, (Severity.Bug, FindingOrigin.New)),
                new AuthorResponded { T = T0, Round = 1, Fix = new SeverityCounts { Bug = fixes }, Rejected = rejects },
                new RunFinished { T = T0, Outcome = outcome },
            };
            if (usefulness is { } u) ev.Add(new HindsightSubmitted { T = T0, Usefulness = u, AcceptedWrongly = accW, RejectedWrongly = rejW });
            return RunState.Fold(id, ev);
        }

        var rows = ReviewerStats.Build(
        [
            Run("a", "ds", ds, Outcome.Clean, 3, 1, 0.004, 8, accW: 1),
            Run("b", "ds", ds, Outcome.Decided, 1, 2, 0.002, 5, rejW: 1),
            Run("c", "ds", ds, Outcome.Error, 0, 0, 0.003),
            Run("d", "ds", ds, Outcome.Cancelled, 0, 0, 1.0, 1),
            Run("e", "opus", opus, Outcome.Clean, 1, 0, 0, 9),
        ]);
        Assert.Equal(2, rows.Count);
        var r = rows[0];
        Assert.Equal(("ds", "lithos/" + ds.Model, 3, 2), (r.Reviewer, r.Model, r.Runs, r.Rated));
        Assert.Equal("6.5", r.UsefulnessText);
        Assert.Equal("$0.003", r.CostPerRun);
        Assert.Equal("1m00s", r.TimePerRun);
        Assert.Equal("1 of 4 fixes", r.AcceptedWrongly);
        Assert.Equal("1 of 3 rejects", r.RejectedWrongly);
        Assert.Equal(("opus", "subscription", "0 of 1 fix"), (rows[1].Reviewer, rows[1].CostPerRun, rows[1].AcceptedWrongly));
    }

    [Fact]
    public void Hindsight_view_resolves_finding_titles()
    {
        var rounds = new List<RoundData>
        {
            new(1,
                new ReviewFile { Round = 1, Findings =
                [
                    new StoredFinding { Id = "R1.1", Severity = Severity.Risk, Title = "Leak", Explanation = "It leaks.", Refs = ["a.cs:1", "b.cs:2"] },
                    new StoredFinding { Id = "R1.2", Severity = Severity.Nit, Title = "Name", Explanation = "Rename it." },
                ] },
                new ResponseFile { Responses =
                [
                    new AuthorResponseItem { Id = "R1.1", Action = AuthorAction.Fix },
                    new AuthorResponseItem { Id = "R1.2", Action = AuthorAction.Reject, Reasoning = "Taste." },
                ] }),
        };
        var decisions = new DecisionsFile { Decisions = [new DecisionItem { Id = "R1.2", Decision = UserDecision.Dismiss, Comment = "Agree." }] };
        var v = HindsightView.Build(new HindsightFile
        {
            Usefulness = 7,
            AcceptedWrongly = [new HindsightItem { Id = "R1.1", Why = "Churn." }],
            RejectedWrongly = [new HindsightItem { Id = "R1.2", Why = "It was right." }, new HindsightItem { Id = "R9.9", Why = "?" }],
            Notes = "n",
        }, rounds, decisions);
        Assert.Equal("Caught a significant issue", v.Anchor);
        Assert.Equal(new HindsightRow("R1.1", Severity.Risk, "Leak", "Churn.", "It leaks.", "a.cs:1, b.cs:2", "fix"), v.AcceptedWrongly.Single());
        Assert.Equal(new HindsightRow("R1.2", Severity.Nit, "Name", "It was right.", "Rename it.", "", "reject: Taste.", "dismiss: Agree."), v.RejectedWrongly[0]);
        Assert.Null(v.RejectedWrongly[1].Severity);
    }

    [Fact]
    public void Golden_feed()
    {
        var cfg = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "lithos", Model = "deepseek-ai/DeepSeek-V4.1-Flash-ultra" };
        const Severity B = Severity.Bug, R = Severity.Risk, D = Severity.Design, N = Severity.Nit;
        const FindingOrigin New = FindingOrigin.New, M = FindingOrigin.Maintained;
        var events = new List<RunEvent>
        {
            new RunStarted { T = T0, Reviewer = "ds", ReviewerConfig = cfg, MaxRounds = 5, Repo = @"D:\Fractory" },
            new ReviewRequested { T = T0, Round = 1 },
            Received(1, 108_000, 0.004, 0, (B, New), (R, New), (R, New), (D, New), (N, New), (N, New)),
            new AuthorResponded { T = T0, Round = 1, Fix = new SeverityCounts { Bug = 1, Risk = 1, Nit = 1 }, Rejected = 3 },
            new ReviewRequested { T = T0, Round = 2 },
            Received(2, 123_000, 0.005, 2, (R, M), (N, New)),
            new AuthorResponded { T = T0, Round = 2, Rejected = 2 },
            new ReviewRequested { T = T0, Round = 3 },
            Received(3, 55_000, 0.003, 1, (R, M)),
            new AuthorResponded { T = T0, Round = 3, Rejected = 1 },
            new AwaitingDecisions { T = T0, Round = 3, Ids = ["R3.1"], Reason = AwaitReason.Deadlock },
            new DecisionsSubmitted { T = T0, Fix = 1 },
            new RunFinished { T = T0, Outcome = Outcome.Decided },
        };
        var text = string.Join("\n", Summary.Lines(RunState.Fold("x", events)).Select(l => l.ToString()));
        Assert.Equal("""
            Requesting review from ds (DeepSeek-V4.1-Flash-ultra)…
            Reviewer returned 6 findings: 1 bug, 2 risk, 1 design, 2 nit.   1m48s · $0.004
            Author will fix 1 bug, 1 risk, 1 nit; rejects 3.
            Sending the author's response to the reviewer and requesting re-review…
            Reviewer returned 2 findings: 1 risk (maintained), 1 nit (new); withdrew 2.   2m03s · $0.005
            Author will fix none; rejects 2.
            Sending the author's response to the reviewer and requesting re-review…
            Reviewer returned 1 finding: 1 risk (maintained); withdrew 1.   0m55s · $0.003
            Author will fix none; rejects 1.
            Reviewer and author disagree on 1 finding. Waiting for your decision…
            You decided: fix 1. Ending review session.
            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Summary_table_rows_totals_and_findings()
    {
        var cfg = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "lithos", Model = "m" };
        const Severity R = Severity.Risk, N = Severity.Nit;
        const FindingOrigin New = FindingOrigin.New, M = FindingOrigin.Maintained;
        var events = new List<RunEvent>
        {
            new RunStarted { T = T0, Reviewer = "ds", ReviewerConfig = cfg, MaxRounds = 5 },
            new ReviewRequested { T = T0, Round = 1 },
            Received(1, 47_000, 0.034, 0, (R, New), (R, New), (N, New)),
            new AuthorResponded { T = T0, Round = 1, Fix = new SeverityCounts { Risk = 1, Nit = 1 }, Rejected = 1 },
            new ReviewRequested { T = T0, Round = 2 },
            new ReviewerRetry { T = T0, Round = 2, Reason = "429", DelaySeconds = 30 },
            Received(2, 48_000, 0.037, 0, (R, M), (N, New)),
            new AuthorResponded { T = T0, Round = 2, Rejected = 1, Fix = new SeverityCounts { Nit = 1 } },
            new AwaitingDecisions { T = T0, Round = 2, Ids = ["R2.1"], Reason = AwaitReason.Deadlock },
        };
        var rounds = new List<RoundData>
        {
            new(1, null, null),
            new(2,
                new ReviewFile { Round = 2, Findings = [new StoredFinding { Id = "R2.1", Severity = R, Title = "Retry loop", Origin = M, PreviousId = "R1.1", Explanation = " Loops forever. ", Refs = ["a.cs:3"], Warnings = ["stale ref"] }, new StoredFinding { Id = "R2.2", Severity = N, Title = "Typo" }] },
                new ResponseFile { Responses = [new AuthorResponseItem { Id = "R2.1", Action = AuthorAction.Reject, Reasoning = "It's bounded." }, new AuthorResponseItem { Id = "R2.2", Action = AuthorAction.Fix }] }),
        };
        var t = SummaryTable.Build(RunState.Fold("x", events), rounds, T0.AddMinutes(4));

        Assert.Equal(2, t.Rounds.Count);
        var r1 = t.Rounds[0];
        Assert.Equal(["2 risk", "1 nit"], r1.Chips.Select(c => c.Text));
        Assert.Equal((1 + 1, 1), (r1.Fix, r1.Reject));
        Assert.Equal(("0m47s", "$0.034"), (r1.Time, r1.Cost));
        Assert.Empty(r1.Findings); // no review.json: the row just can't expand

        var r2 = t.Rounds[1];
        Assert.Equal(["1 risk · maintained", "1 nit"], r2.Chips.Select(c => c.Text));
        Assert.Equal("retried once", r2.ReviewerNote);
        Assert.Equal([("R2.1", "rejected"), ("R2.2", "fixed")], r2.Findings.Select(f => (f.Id, f.Action!)));
        Assert.Equal(new FindingRow("R2.1", R, "Retry loop", "rejected", "Loops forever.", "a.cs:3   (stale ref)", "maintained from R1.1", "It's bounded.", ""), r2.Findings[0]);

        Assert.Equal(new DecisionRow("Reviewer and author disagree on 1 finding", "decide below", "4m00s", true), t.Decision);
        Assert.Equal(new TotalRow("5 findings so far", "3 fixed · 2 rejected", "1m35s", "$0.071"), t.Total);
        Assert.Equal("", t.Outcome);

        events.Add(new DecisionsSubmitted { T = T0, Fix = 1 });
        events.Add(new RunFinished { T = T0, Outcome = Outcome.Decided });
        var decisions = new DecisionsFile { Decisions = [new DecisionItem { Id = "R2.1", Decision = UserDecision.Fix, Comment = "cap it" }] };
        t = SummaryTable.Build(RunState.Fold("x", events), rounds, T0.AddMinutes(5), decisions: decisions);
        Assert.Equal("you decided: fix 1", t.Decision!.Note);
        Assert.Equal("fix it: cap it", t.Rounds[1].Findings[0].UserDecision);
        Assert.Equal("5 findings", t.Total.Findings);
        Assert.Equal(Outcome.Decided, t.OutcomeKind);
    }

    [Fact]
    public void Summary_table_live_rows()
    {
        var events = new List<RunEvent>
        {
            new RunStarted { T = T0, Reviewer = "opus", ReviewerConfig = new ReviewerConfig { Kind = ReviewerKinds.Claude, Model = "opus" }, MaxRounds = 5 },
            new ReviewRequested { T = T0, Round = 1 },
        };
        var t = SummaryTable.Build(RunState.Fold("x", events), [], T0.AddSeconds(75), T0.AddSeconds(70));
        Assert.Equal(RowState.Reviewing, t.Rounds[0].State);
        Assert.Equal("Reviewing… last activity 5s ago", t.Rounds[0].ReviewerNote);
        Assert.Equal("1m15s", t.Rounds[0].Time);

        events.Add(Received(1, 1000, 0, 0, (Severity.Nit, FindingOrigin.New)));
        t = SummaryTable.Build(RunState.Fold("x", events), [], T0.AddSeconds(75));
        Assert.Equal(RowState.AuthorActing, t.Rounds[0].State);
        Assert.Equal("acting on them…", t.Rounds[0].AuthorNote);
        Assert.Equal("subscription", t.Total.Cost);
    }

    [Fact]
    public void Live_cost_of_the_round_in_progress()
    {
        var cfg = new ReviewerConfig { Kind = ReviewerKinds.Pi, Provider = "lithos", Model = "m" };
        var prices = new Dictionary<string, Price> { ["lithos/m"] = new Price { Input = 1, Output = 10 } };
        static string End(int input, int output) =>
            """{"type":"message_end","message":{"role":"assistant","provider":"lithos","model":"m","usage":{"input":""" + input + ""","output":""" + output + "}}}";
        // A message from a first attempt, BobarCode's call marker for the retry, the retry's message, then a
        // user message_end (not counted) and a half-written last line (skipped).
        var log = string.Join("\n", End(100_000, 1_000), """{"bobar":"call","what":"review","kind":"pi"}""", End(200_000, 2_000),
            """{"type":"message_end","message":{"role":"user"}}""", """{"type":"message_en""");
        var live = Pricing.LiveCost(cfg, log, prices);
        Assert.Equal(0.33, live!.Value, 6);
        Assert.Null(Pricing.LiveCost(cfg, log, new Dictionary<string, Price>()));
        Assert.Null(Pricing.LiveCost(new ReviewerConfig { Kind = ReviewerKinds.Claude, Model = "opus" }, log, prices));

        var events = new List<RunEvent>
        {
            new RunStarted { T = T0, Reviewer = "ds", ReviewerConfig = cfg, MaxRounds = 5 },
            new ReviewRequested { T = T0, Round = 1 },
            Received(1, 1000, 0.5, 0, (Severity.Nit, FindingOrigin.New)),
            new AuthorResponded { T = T0, Round = 1, Fix = new SeverityCounts { Nit = 1 } },
            new ReviewRequested { T = T0, Round = 2 },
        };
        var t = SummaryTable.Build(RunState.Fold("x", events), [], T0.AddSeconds(75), null, live);
        Assert.Equal("$0.330…", t.Rounds[1].Cost);
        Assert.Equal("$0.830", t.Total.Cost);
        t = SummaryTable.Build(RunState.Fold("x", events), [], T0.AddSeconds(75));
        Assert.Equal("…", t.Rounds[1].Cost);
        Assert.Equal("$0.500", t.Total.Cost);
    }

    [Fact]
    public void In_progress_lines_are_live()
    {
        var events = new List<RunEvent>
        {
            new RunStarted { T = T0, Reviewer = "opus", ReviewerConfig = new ReviewerConfig { Kind = ReviewerKinds.Claude, Model = "opus" }, MaxRounds = 5 },
            new ReviewRequested { T = T0, Round = 1 },
            Received(1, 1000, 0, 0, (Severity.Nit, FindingOrigin.New)),
            new AuthorResponded { T = T0, Round = 1, Fix = new SeverityCounts { Nit = 1 } },
        };
        var lines = Summary.Lines(RunState.Fold("x", events), T0.AddSeconds(75), T0.AddSeconds(70));
        Assert.Equal("Reviewer returned 1 finding: 1 nit.   0m01s · subscription", lines[1].ToString());
        Assert.Equal("Author will fix 1 nit; rejects none (and is now acting on them…)", lines[2].Text);

        events.Add(new ReviewRequested { T = T0.AddSeconds(10), Round = 2 });
        lines = Summary.Lines(RunState.Fold("x", events), T0.AddSeconds(75), T0.AddSeconds(70));
        Assert.Equal("Author will fix 1 nit; rejects none.", lines[2].Text);
        Assert.True(lines[3].InProgress);
        Assert.Equal("1m05s · last activity 5s ago", lines[3].Detail);
    }

    [Fact]
    public void Fold_tolerates_torn_and_unknown_lines()
    {
        var text = """
            {"type":"run_started","t":"2026-11-17T14:23:01+00:00","repo":"r","base":"b","reviewer":"ds","reviewerConfig":{"kind":"pi","model":"m"},"maxRounds":5}
            {"type":"something_new","t":"2026-11-17T14:23:02+00:00"}
            {"t":"2026-11-17T14:23:03+00:00","type":"review_requested","round":1}
            {"type":"review_rec
            """;
        var events = Bobar.Core.IO.EventLog.Parse(text);
        Assert.Equal(2, events.Count);
        var s = RunState.Fold("x", events);
        Assert.Equal(RunPhase.Reviewing, s.Phase);
        Assert.Equal("ds", s.Started!.Reviewer);
    }
}

public class MiscTests
{
    [Fact]
    public void Redactor_masks_every_value_longest_first()
    {
        var keys = KeysEnv.Parse("A=abcdefgh\nexport B=\"abcdefghXYZ\"\n# C=commented\nD=123\n");
        Assert.Equal(3, keys.Values.Count);
        var r = keys.Redactor();
        Assert.Equal("x *** y *** z 123", r.Apply("x abcdefghXYZ y abcdefgh z 123"));
    }

    [Theory]
    [InlineData("429 rate_limit_exceeded, retry-after: 12", 12)]
    [InlineData("Retry-After 3", 3)]
    [InlineData("Please try again in 1m20s.", 80)]
    public void Retry_after_parsing(string text, double seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), ReviewEngine.RetryAfterFromText(text));

    [Fact]
    public void Retry_after_absent() => Assert.Null(ReviewEngine.RetryAfterFromText("overloaded"));

    [Fact]
    public void Pricing_uses_the_table_and_unknown_is_null()
    {
        var prices = AppConfig.CreateDefault().Prices;
        var ds = AppConfig.CreateDefault().Reviewers["ds"];
        var usage = new TokenUsage { Input = 1_000_000, Output = 1_000_000, CacheRead = 1_000_000 };
        Assert.Equal(0.35 + 1.4 + 0.007, Pricing.Cost(ds, [], usage, prices)!.Value, 6);
        Assert.Null(Pricing.Cost(new ReviewerConfig { Provider = "x", Model = "y" }, [], usage, prices));
        Assert.Equal(0, Pricing.Cost(AppConfig.CreateDefault().Reviewers["opus"], [], usage, prices));
    }

    [Fact]
    public void Refs_are_normalized_and_missing_paths_warned()
    {
        using var repo = new TempRepo();
        var abs = Path.Combine(repo.Dir, "src", "app.txt") + ":12";
        var (refs, warnings) = ReviewEngine.NormalizeRefs([abs, "./src/app.txt:3-5", "nope.cs:1"], repo.Dir);
        Assert.Equal(["src/app.txt:12", "src/app.txt:3-5", "nope.cs:1"], refs);
        Assert.Equal(["path not found: nope.cs"], warnings);
    }

    [Fact]
    public void Config_defaults_are_written_and_reloaded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bobar-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new BobarPaths(dir, dir);
        var cfg = ConfigStore.Load(paths);
        Assert.True(File.Exists(paths.ConfigFile));
        Assert.Equal(["ds", "sol", "hydrogen", "qwen4", "glm", "terra", "neon", "opus"], cfg.Reviewers.Keys);
        Assert.Equal("lithos", cfg.Reviewers["ds"].Provider);
        Assert.DoesNotContain("priceKey", File.ReadAllText(paths.ConfigFile));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Saving_reviewer_instructions_keeps_the_rest_and_never_overwrites_a_broken_config()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bobar-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new BobarPaths(dir, dir);
        var cfg = ConfigStore.Load(paths);
        cfg.MaxRounds = 3;
        File.WriteAllText(paths.ConfigFile, System.Text.Json.JsonSerializer.Serialize(cfg, BobarJson.Indented.AppConfig));

        ConfigStore.SaveReviewerInstructions(paths, "  Prefer lightweight designs.\nLine two.\n");
        var back = ConfigStore.Load(paths);
        Assert.Equal("Prefer lightweight designs.\nLine two.", back.ReviewerInstructions);
        Assert.Equal(3, back.MaxRounds);
        Assert.Equal(["ds", "sol", "hydrogen", "qwen4", "glm", "terra", "neon", "opus"], back.Reviewers.Keys);

        File.WriteAllText(paths.ConfigFile, "{ broken");
        Assert.Throws<ConfigException>(() => ConfigStore.SaveReviewerInstructions(paths, "x"));
        Assert.Equal("{ broken", File.ReadAllText(paths.ConfigFile));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Timeout_kills_the_whole_process_tree()
    {
        // powershell starts a long-lived child (ping), writes its pid, and waits on it.
        var pidFile = Path.Combine(Path.GetTempPath(), "bobar-child-" + Guid.NewGuid().ToString("N") + ".txt");
        var script = $"$p = Start-Process -FilePath ping -ArgumentList '-n','120','127.0.0.1' -PassThru -WindowStyle Hidden; Set-Content -Path '{pidFile}' -Value $p.Id; Wait-Process -Id $p.Id";
        var spec = new ProcessSpec("powershell", ["-NoProfile", "-NonInteractive", "-Command", script], Path.GetTempPath()) { Timeout = TimeSpan.FromSeconds(8) };
        var outcome = await ProcessRunner.RunAsync(spec, _ => { }, CancellationToken.None);
        Assert.True(outcome.TimedOut);
        Assert.True(File.Exists(pidFile), "the child never started");
        var childPid = int.Parse(File.ReadAllText(pidFile).Trim());
        await Task.Delay(500);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(childPid));
        File.Delete(pidFile);
    }

    [Fact]
    public async Task Stdin_goes_to_the_child_and_stdout_lines_come_back()
    {
        var lines = new List<string>();
        var spec = new ProcessSpec("git", ["hash-object", "--stdin"], Path.GetTempPath()) { Stdin = "hello\n" };
        var outcome = await ProcessRunner.RunAsync(spec, lines.Add, CancellationToken.None);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(["ce013625030ba8dba906f756967f9e9ca394464a"], lines);
    }
}
