using System.Text.Json.Nodes;
using Bobar.Core.IO;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Proc;
using Bobar.Core.Reviewers;
using Bobar.Core.Runs;
using Bobar.Core.State;
using static Bobar.Core.Tests.F;

namespace Bobar.Core.Tests;

/// <summary>Explore runs (`explore start` / `explore finish`), their ledger of past rejections, and service tiers.</summary>
public class ExploreTests
{
    private const string Task = "Explore the codebase for cleanup opportunities.";

    private static Dictionary<int, IEnumerable<FakeStep>> Script(params FakeStep[] steps) => new() { [1] = steps };

    private static Task<CommandResult> Explore(TestEnv env) => env.Engine.ExploreStartAsync("fake", Task, env.Repo.Dir, CancellationToken.None);

    private static Task<CommandResult> Finish(TestEnv env, CommandResult r, string json) => env.Engine.ExploreFinishAsync(r.RunId!, json, CancellationToken.None);

    private static string Answer(string chosen, params (string Id, string Action)[] items)
        => $$"""{"responses": [{{string.Join(", ", items.Select(i => $$"""{"id": "{{i.Id}}", "action": "{{i.Action}}", "reasoning": "{{(i.Action == "reject" ? $"Not worth it ({i.Id})." : "Agreed.")}}"}"""))}}], "chosen": {{chosen}}}""";

    private static RunState State(TestEnv env, CommandResult r) => RunReader.LoadState(env.Run(r));

    [Fact]
    public async Task An_exploration_returns_E_ids_and_ends_with_finish()
    {
        using var env = new TestEnv(Script(Out(Finding("design", "`A.B`: merge", null, "src/app.txt:1"), Finding("nit", "`C`: rename"), Finding("design", "`D`: drop"))));
        var r = await Explore(env);
        Assert.Equal("findings", r.Status);
        Assert.Contains("### E.1 [design] `A.B`: merge", r.Body);
        Assert.Contains($"bobar-cli explore finish {r.RunId} --response -", r.Body);
        Assert.DoesNotContain("review next", r.Body);

        var s = State(env, r);
        Assert.True(s.IsExplore);
        Assert.Equal(RunKinds.Explore, s.Started!.Kind);
        var prompt = File.ReadAllText(env.Run(r).Round(1).Prompt);
        Assert.Contains(Task, prompt);
        Assert.Contains("## Ideas rejected before", prompt);
        Assert.Contains("None yet.", prompt);
        Assert.DoesNotContain("diff", prompt);
        Assert.Contains("Asking fake (fake) to explore the repository…", env.Progress.ToString());

        // `review next` and `show` point at `explore finish`; verdicts are display only.
        var next = await env.Engine.NextAsync(r.RunId!, Resp(("E.1", "fix")), CancellationToken.None);
        Assert.Equal("error", next.Status);
        Assert.Contains("explore finish", next.Body);
        Assert.Contains("explore finish", env.Engine.Show(r.RunId!).Body);
        var v = env.Engine.Verdicts(r.RunId!, ["E.1"], ["E.3"]);
        Assert.Equal("verdicts_saved", v.Status);
        Assert.Contains("accept E.1", v.Body);
        Assert.DoesNotContain("apply the fixes", v.Body);

        // Invalid answers keep it waiting and repeat the finish command.
        var bad = await Finish(env, r, Resp(("E.1", "fix"), ("E.2", "fix"), ("E.3", "reject")));
        Assert.Equal(("invalid_response", 2), (bad.Status, bad.ExitCode));
        Assert.Contains("missing \"chosen\"", bad.Body);
        Assert.Contains("explore finish", bad.Body);
        bad = await Finish(env, r, Answer("""["E.3"]""", ("E.1", "fix"), ("E.2", "fix"), ("E.3", "reject")));
        Assert.Contains("\"E.3\" is not one you accepted", bad.Body);

        var done = await Finish(env, r, Answer("""["E.1"]""", ("E.1", "fix"), ("E.2", "fix"), ("E.3", "reject")));
        Assert.Equal("done", done.Status);
        Assert.Contains("## Exploration finished: the author chose E.1", done.Body);
        Assert.Contains("## Hindsight", done.Body);
        s = State(env, r);
        Assert.Equal(Outcome.Explored, s.Finished!.Outcome);
        Assert.Equal(["E.1"], RunReader.LoadRound(env.Run(r), 1).Response!.Chosen);

        // A second finish fails; the hindsight may regret an accepted finding that wasn't chosen.
        var again = await Finish(env, r, Answer("[]", ("E.1", "fix"), ("E.2", "fix"), ("E.3", "reject")));
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("not waiting for the author", again.Body);
        var h = env.Engine.Hindsight(r.RunId!, """{"usefulness": 6, "acceptedWrongly": [{"id": "E.2", "why": "Churn."}], "rejectedWrongly": [], "notes": "Fine."}""");
        Assert.Equal("hindsight_saved", h.Status);

        var table = SummaryTable.Build(State(env, r), RunReader.LoadRounds(env.Run(r), State(env, r)), DateTimeOffset.Now);
        Assert.Equal(["chosen", "accepted", "rejected"], table.Rounds[0].Findings.Select(f => f.Action));
        Assert.Equal("Exploration answered: the author chose E.1.", table.Outcome);
    }

    [Fact]
    public async Task Finishing_a_cancelled_exploration_or_a_review_run_fails()
    {
        using var env = new TestEnv(Script(Out(Finding("design", "A"))));
        var r = await Explore(env);
        await env.Engine.CancelAsync(r.RunId!);
        var f = await Finish(env, r, Answer("[]", ("E.1", "reject")));
        Assert.Equal(1, f.ExitCode);
        Assert.Equal(Outcome.Cancelled, State(env, r).Finished!.Outcome);

        var review = await env.Start();
        f = await Finish(env, review, Answer("[]", ("R1.1", "reject")));
        Assert.Equal("error", f.Status);
        Assert.Contains("review next", f.Body);
    }

    [Fact]
    public async Task Rejections_carry_over_to_later_explorations_and_a_re_proposal_replaces_them()
    {
        using var env = new TestEnv(new Dictionary<int, IEnumerable<FakeStep>>
        {
            [1] =
            [
                Out(Finding("design", "`A`: merge", null, "src/app.txt:3", "src/app.txt:9"), Finding("nit", "`B`: rename")),
                Out(Finding("design", "`A`: merge, again", "P1", "src/app.txt:4")),
                Out(),
            ],
        });
        var r1 = await Explore(env);
        await Finish(env, r1, Answer("[]", ("E.1", "reject"), ("E.2", "fix")));

        IReadOnlyCollection<string>? earlier = null;
        env.Fake.OnCall = c => earlier = c.EarlierIds;
        var r2 = await Explore(env);
        Assert.Equal(["P1"], earlier);
        var p2 = File.ReadAllText(env.Run(r2).Round(1).Prompt);
        Assert.Contains("P1  src/app.txt  `A`: merge - rejected: Not worth it (E.1).\n", p2);
        Assert.DoesNotContain("`B`: rename", p2);
        var ledger = RunFiles.ReadJson(env.Run(r2).Round(1).Ledger, BobarJson.Compact.LedgerFile)!;
        Assert.Equal(("P1", r1.RunId, "E.1"), (ledger.Entries[0].Id, ledger.Entries[0].Run, ledger.Entries[0].Finding));
        Assert.Contains("(repeated from P1)", r2.Body);
        await Finish(env, r2, Answer("[]", ("E.1", "reject")));

        var r3 = await Explore(env);
        var p3 = File.ReadAllText(env.Run(r3).Round(1).Prompt);
        Assert.Contains("P1  src/app.txt  `A`: merge, again - rejected: Not worth it (E.1).\n", p3);
        Assert.DoesNotContain("P2", p3);
        Assert.Equal("done", r3.Status); // no findings
    }

    [Fact]
    public void Ledger_drops_a_rejection_the_author_later_accepted()
    {
        static ReviewFile Review(params StoredFinding[] f) => new() { Round = 1, Findings = [.. f] };
        static ResponseFile Resp(params (string Id, AuthorAction A)[] r) => new() { Responses = [.. r.Select(x => new AuthorResponseItem { Id = x.Id, Action = x.A, Reasoning = "No.\nReally." })], Chosen = [] };
        var runs = new List<ExploreRunData>
        {
            new("r1", Review(new StoredFinding { Id = "E.1", Title = "A", Refs = ["a.cs:1-3", "a.cs:9", "b.cs"] }), Resp(("E.1", AuthorAction.Reject)), null),
            new("r2", Review(new StoredFinding { Id = "E.1", Title = "A2", PreviousId = "P1" }), Resp(("E.1", AuthorAction.Fix)),
                new LedgerFile { Entries = [new LedgerEntry { Id = "P1", Run = "r1", Finding = "E.1" }] }),
        };
        var one = ExploreLedger.Build(runs.Take(1));
        Assert.Equal(["a.cs", "b.cs"], one[0].Paths);
        Assert.Equal("No. Really.", one[0].Reason);
        Assert.Empty(ExploreLedger.Build(runs));

        // Two explorations from the same ledger both re-propose P1: the latest verdict wins, with no duplicate.
        var p1 = new LedgerFile { Entries = [new LedgerEntry { Id = "P1", Run = "r1", Finding = "E.1" }] };
        var both = new List<ExploreRunData>
        {
            runs[0],
            new("r2", Review(new StoredFinding { Id = "E.1", Title = "A2", PreviousId = "P1" }), Resp(("E.1", AuthorAction.Reject)), p1),
            new("r3", Review(new StoredFinding { Id = "E.1", Title = "A3", PreviousId = "P1" }), Resp(("E.1", AuthorAction.Reject)), p1),
        };
        Assert.Equal(["A3"], ExploreLedger.Build(both).Select(r => r.Title));
        both[2] = both[2] with { Response = Resp(("E.1", AuthorAction.Fix)) };
        Assert.Empty(ExploreLedger.Build(both));
    }

    [Fact]
    public async Task A_streaming_exploration_reports_E_ids()
    {
        using var env = new TestEnv(Script(Out(Finding("design", "A"), Finding("nit", "B"))), streams: true);
        string? prefix = null;
        env.Fake.OnCall = c => prefix = c.IdPrefix;
        var r = await Explore(env);
        Assert.Equal("findings", r.Status);
        var lines = File.ReadAllLines(env.Run(r).Round(1).Findings);
        Assert.StartsWith("{\"id\":\"E.1\"", lines[0]);
        Assert.StartsWith("{\"id\":\"E.2\"", lines[1]);
        Assert.Equal("E", prefix);
        Assert.Contains("the author starts reading each finding", File.ReadAllText(env.Run(r).Round(1).Prompt));
    }

    [Fact]
    public async Task A_review_records_the_exploration_it_implements()
    {
        using var env = new TestEnv(Script(Out(Finding("design", "A")), Out()));
        var ex = await Explore(env);
        await Finish(env, ex, Answer("""["E.1"]""", ("E.1", "fix")));
        var r = await env.Engine.StartAsync("fake", "Cleanup.", env.Repo.Dir, CancellationToken.None, exploreRun: ex.RunId![^4..]);
        Assert.Equal(ex.RunId, State(env, r).Started!.ExploreRun);
        var bad = await env.Engine.StartAsync("fake", "Cleanup.", env.Repo.Dir, CancellationToken.None, exploreRun: r.RunId);
        Assert.Contains("is a review run", bad.Body);
    }

    [Fact]
    public void Ids_order_by_number_in_every_scheme()
    {
        Assert.Equal(["E.2", "E.10"], new[] { "E.10", "E.2" }.Order(IdComparer.Instance));
        Assert.Equal(["P2", "P10"], new[] { "P10", "P2" }.Order(IdComparer.Instance));
        Assert.Equal(["R1.2", "R2.1", "R2.10"], new[] { "R2.10", "R1.2", "R2.1" }.Order(IdComparer.Instance));
    }

    [Fact]
    public void Reviewer_stats_keep_explorations_apart()
    {
        var cfg = new ReviewerConfig { Kind = "pi", Provider = "p", Model = "m" };
        RunState Run(string id, string? kind) => RunState.Fold(id,
        [
            new RunStarted { Reviewer = "x", ReviewerConfig = cfg, MaxRounds = 1, Kind = kind },
            new RunFinished { Outcome = Outcome.Clean },
        ]);
        var rows = ReviewerStats.Build([Run("a", null), Run("b", RunKinds.Explore), Run("c", null)]);
        Assert.Equal([("x", 2), ("x (explore)", 1)], rows.Select(r => (r.Reviewer, r.Runs)));
    }

    [Fact]
    public void A_service_tier_has_its_own_prices_and_extension()
    {
        var flex = new ReviewerConfig { Kind = "pi", Provider = "openai", Model = "m", ServiceTier = "flex" };
        var usage = new TokenUsage { Input = 1_000_000 };
        var prices = new Dictionary<string, Price> { ["openai/m"] = new() { Input = 2, Output = 8 } };
        Assert.Equal("openai/m@flex", flex.PriceKey);
        Assert.Null(Pricing.Cost(flex, ["openai/m"], usage, prices)); // never the standard price
        prices["openai/m@flex"] = new() { Input = 1, Output = 4 };
        Assert.Equal(1, Pricing.Cost(flex, ["openai/m"], usage, prices));

        var args = PiReviewer.Args(flex, "s", null, null, @"C:\x\tier.js");
        Assert.Equal(@"C:\x\tier.js", args[args.IndexOf("-e") + 1]);
    }

    /// <summary>Loads the real service-tier extension with a stub `pi` and runs its hook on sample payloads.</summary>
    private const string TierHarness = """
        let hook;
        require(process.argv[2])({ on: (name, h) => { if (name === "before_provider_request") hook = h; } });
        const out = [
          hook({ payload: { model: "gpt", input: [] } }),
          hook({ payload: { model: "gemini", contents: [], config: { temperature: 0 } } }),
        ];
        process.stdout.write(JSON.stringify(out));
        """;

    [Fact]
    public async Task Tier_extension_sets_the_tier_on_each_payload()
    {
        if (ProcessRunner.FindOnPath("node") is not { } node) return;
        var dir = Path.Combine(Path.GetTempPath(), "bobar-tier-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var ext = Path.Combine(dir, PiReviewer.TierExtensionFile);
            File.WriteAllText(ext, PiReviewer.TierExtension);
            var harness = Path.Combine(dir, "harness.js");
            File.WriteAllText(harness, TierHarness);
            var o = await ProcessRunner.RunAsync(new ProcessSpec(node, [harness, ext], dir)
            {
                Env = new Dictionary<string, string?> { ["BOBAR_SERVICE_TIER"] = "flex" },
            }, null, CancellationToken.None);
            var r = JsonNode.Parse(o.Stdout)!.AsArray();
            Assert.Equal("""{"model":"gpt","input":[],"service_tier":"flex"}""", r[0]!.ToJsonString());
            Assert.Equal("""{"model":"gemini","contents":[],"config":{"temperature":0,"serviceTier":"flex"}}""", r[1]!.ToJsonString());
        }
        finally { Directory.Delete(dir, true); }
    }
}
