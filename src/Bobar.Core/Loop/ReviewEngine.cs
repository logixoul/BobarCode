using System.Diagnostics;
using System.Text.RegularExpressions;
using Bobar.Core.Git;
using Bobar.Core.IO;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;
using Bobar.Core.Runs;
using Bobar.Core.State;
using static Bobar.Core.Loop.CommandResult;

namespace Bobar.Core.Loop;

public sealed class EngineOptions
{
    public required BobarPaths Paths { get; init; }
    /// <summary>Creates the reviewer for one round. Tests inject FakeReviewers here.</summary>
    public Func<ReviewerContext, IReviewer> ReviewerFactory { get; init; } = Reviewers.ReviewerFactory.Create;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, ct) => Task.Delay(t, ct);
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>Progress lines (the summary feed) go here: stderr in the CLI.</summary>
    public TextWriter Progress { get; init; } = TextWriter.Null;
    /// <summary>Called when a run starts awaiting decisions and openGuiOnDecisions is on.</summary>
    public Action<RunPaths>? LaunchGui { get; init; }
    public TimeSpan WaitPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>How often a command holding run.lock checks for cancel.request.</summary>
    public TimeSpan CancelPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>How often `review stream` looks for new findings.</summary>
    public TimeSpan StreamPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>How long `review stream` without a run waits for one to start.</summary>
    public TimeSpan StreamFindRunTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>What a reviewer factory gets.</summary>
public sealed record ReviewerContext(string Shorthand, ReviewerConfig Config, KeysEnv Keys, BobarPaths Paths, WebSearchConfig? WebSearch = null);

/// <summary>Thrown inside a command to end the run as an error.</summary>
internal sealed class RunErrorException(string message) : Exception(message);

/// <summary>The CLI's commands (§8), as a library. The loop's end is decided in code (§5).</summary>
public sealed partial class ReviewEngine(EngineOptions options)
{
    public const string FakeShorthand = "fake";
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly EngineOptions o = options;
    private BobarPaths Paths => o.Paths;

    // ───────────────────────────── start ─────────────────────────────

    /// <param name="from">Review the commits from..HEAD (inclusive) plus anything uncommitted; their messages
    /// become the description and <paramref name="description"/> is optional extra notes. Null: uncommitted only.</param>
    /// <param name="exploreRun">The explore run whose findings this change implements (shown in the GUI).</param>
    public async Task<CommandResult> StartAsync(string reviewer, string? description, string repoDir, CancellationToken ct, string? from = null, string? exploreRun = null)
    {
        AppConfig config;
        try { config = ConfigStore.Load(Paths); }
        catch (ConfigException ex) { return Fail(ex.Message); }
        var keys = KeysEnv.Load(Paths.KeysFile);
        var redactor = keys.Redactor();

        if (!TryReviewerConfig(config, reviewer, out var rcfg))
            return Fail($"Unknown reviewer '{reviewer}'. Configured: {string.Join(", ", config.Reviewers.Keys)} (edit {Paths.ConfigFile}).");
        if (from is null && string.IsNullOrWhiteSpace(description)) return Fail("The description (--desc) is empty.");
        if (exploreRun is not null)
        {
            var er = RunReader.Resolve(Paths, exploreRun, out var erErr);
            if (er is null) return Fail($"--explore-run: {erErr}");
            if (!RunReader.LoadState(er).IsExplore) return Fail($"--explore-run: {er.RunId} is a review run, not an explore run.");
            exploreRun = er.RunId;
        }

        string repo, baseRev, seed, tree, diff;
        List<CommitInfo> commits = [];
        var dirty = false;
        try
        {
            repo = await GitCli.TopLevelAsync(Path.GetFullPath(repoDir), ct);
            seed = await GitCli.HeadOrEmptyAsync(repo, ct);
            baseRev = seed;
            if (from is not null)
            {
                if (seed == GitCli.EmptyTree) return Fail($"{repo} has no commits, so there is no range to review.");
                (baseRev, commits) = await GitCli.RangeAsync(repo, from, ct);
            }
            var tmpIndex = Path.Combine(Path.GetTempPath(), $"bobar-index-{Guid.NewGuid():N}");
            tree = await GitCli.SnapshotAsync(repo, seed, tmpIndex, ct);
            diff = await GitCli.DiffAsync(repo, baseRev, tree, ct);
            if (from is not null) dirty = tree != await GitCli.TreeOfAsync(repo, seed, ct);
        }
        catch (GitException ex) { return Fail(ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(Statuses.Cancelled, null, null, "Interrupted before the run was created.", 1); }
        if (string.IsNullOrWhiteSpace(diff))
            return Fail(from is null
                ? $"There are no uncommitted changes in {repo} to review."
                : $"The commits from {from} to HEAD (plus the working tree) make no net change in {repo}.");
        if (from is not null) description = CommitsDescription(commits, dirty, description);

        var run = NewRun(RepoName(repo));
        using var lk = RunLock.TryAcquire(run) ?? throw new IOException($"could not lock the new run {run.RunId}");
        var ctx = new Ctx(this, run, config, keys, redactor);
        RunFiles.WriteText(run.Desc, description!.Trim() + "\n", redactor);
        ctx.Append(new RunStarted { T = Now, Repo = repo, Base = baseRev, Seed = seed, Commits = commits, Reviewer = reviewer, ReviewerConfig = rcfg, MaxRounds = config.MaxRounds, ExploreRun = exploreRun, Name = RepoName(repo) });
        var r1 = run.Round(1);
        RunFiles.WriteText(r1.Tree, tree + "\n", redactor);
        RunFiles.WriteText(r1.Diff, diff, redactor);
        using var watch = new CancelWatch(run, ct, o.CancelPollInterval);
        return await Guarded(ctx, 1, () => ReviewRoundAsync(ctx, 1, watch.Token), watch.Token);
    }

    /// <summary>desc.md for a --from run: the commit messages, oldest first, then the author's optional notes.</summary>
    internal static string CommitsDescription(IReadOnlyList<CommitInfo> commits, bool dirty, string? notes)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"The change under review is {commits.Count} commit{(commits.Count == 1 ? "" : "s")} ending at HEAD");
        sb.Append(dirty ? ", plus uncommitted changes in the working tree" : "").Append(". Their messages, oldest first:\n\n");
        foreach (var c in commits)
        {
            sb.Append($"### {c.Sha[..Math.Min(10, c.Sha.Length)]} {c.Subject}\n");
            var body = c.Message.Split('\n', 2) is [_, var rest] ? rest.Trim() : "";
            if (body.Length > 0) sb.Append('\n').Append(body).Append('\n');
            sb.Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(notes)) sb.Append("### Notes from the author\n\n").Append(notes.Trim()).Append('\n');
        return sb.ToString();
    }

    // ───────────────────────────── explore ─────────────────────────────

    /// <summary>
    /// An exploration: one reviewer call over the repo as it is (no diff), with <paramref name="task"/> as the
    /// task and the ideas earlier explorations of the repo had rejected. It ends with `explore finish`.
    /// </summary>
    public async Task<CommandResult> ExploreStartAsync(string reviewer, string task, string repoDir, CancellationToken ct)
    {
        AppConfig config;
        try { config = ConfigStore.Load(Paths); }
        catch (ConfigException ex) { return Fail(ex.Message); }
        var keys = KeysEnv.Load(Paths.KeysFile);
        var redactor = keys.Redactor();
        if (!TryReviewerConfig(config, reviewer, out var rcfg))
            return Fail($"Unknown reviewer '{reviewer}'. Configured: {string.Join(", ", config.Reviewers.Keys)} (edit {Paths.ConfigFile}).");
        if (string.IsNullOrWhiteSpace(task)) return Fail("The task (--prompt) is empty.");

        string repo, seed, tree;
        try
        {
            repo = await GitCli.TopLevelAsync(Path.GetFullPath(repoDir), ct);
            seed = await GitCli.HeadOrEmptyAsync(repo, ct);
            // The working state as it is (normally HEAD's tree): what a streaming reviewer's snapshot holds.
            tree = await GitCli.SnapshotAsync(repo, seed, Path.Combine(Path.GetTempPath(), $"bobar-index-{Guid.NewGuid():N}"), ct);
        }
        catch (GitException ex) { return Fail(ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(Statuses.Cancelled, null, null, "Interrupted before the run was created.", 1); }

        var records = ExploreLedger.Build(LoadExploreRuns(repo));
        var run = NewRun(RepoName(repo));
        using var lk = RunLock.TryAcquire(run) ?? throw new IOException($"could not lock the new run {run.RunId}");
        var ctx = new Ctx(this, run, config, keys, redactor)
        {
            Exploration = (ExploreLedger.Lines(records), records.Select((_, i) => ExploreLedger.PId(i)).ToList()),
        };
        RunFiles.WriteText(run.Desc, task.Trim() + "\n", redactor);
        ctx.Append(new RunStarted { T = Now, Repo = repo, Base = seed, Seed = seed, Reviewer = reviewer, ReviewerConfig = rcfg, MaxRounds = 1, Kind = RunKinds.Explore, Name = RepoName(repo) });
        var r1 = run.Round(1);
        RunFiles.WriteText(r1.Tree, tree + "\n", redactor);
        RunFiles.WriteJson(r1.Ledger, ExploreLedger.File(records), BobarJson.Indented.LedgerFile, redactor);
        using var watch = new CancelWatch(run, ct, o.CancelPollInterval);
        return await Guarded(ctx, 1, () => ReviewRoundAsync(ctx, 1, watch.Token), watch.Token);
    }

    /// <summary>The finished explore runs of <paramref name="repo"/> the author answered, oldest first.</summary>
    private List<ExploreRunData> LoadExploreRuns(string repo)
    {
        var want = Path.GetFullPath(repo).TrimEnd('\\', '/');
        var list = new List<(DateTimeOffset At, ExploreRunData Data)>();
        foreach (var r in RunReader.ListRuns(Paths))
        {
            var s = RunReader.LoadState(r);
            if (!s.IsExplore || s.Finished?.Outcome != Outcome.Explored) continue;
            if (!string.Equals(Path.GetFullPath(s.Started!.Repo).TrimEnd('\\', '/'), want, StringComparison.OrdinalIgnoreCase)) continue;
            var d = RunReader.LoadRound(r, 1);
            list.Add((s.StartedAt!.Value, new ExploreRunData(r.RunId, d.Review, d.Response, RunFiles.ReadJson(r.Round(1).Ledger, BobarJson.Compact.LedgerFile))));
        }
        // By start time: run ids made in the same second sort by their random suffix.
        return list.OrderBy(x => x.At).Select(x => x.Data).ToList();
    }

    /// <summary>The author's answer to an exploration: accept/reject per finding and the ones it chose. Ends the run.</summary>
    public async Task<CommandResult> ExploreFinishAsync(string runRef, string responseText, CancellationToken ct)
    {
        var run = RunReader.Resolve(Paths, runRef, out var err);
        if (run is null) return Fail(err!);
        AppConfig config;
        try { config = ConfigStore.Load(Paths); }
        catch (ConfigException ex) { return Fail(ex.Message, run.RunId); }
        var keys = KeysEnv.Load(Paths.KeysFile);

        using var lk = RunLock.TryAcquire(run);
        if (lk is null) return CliOutput.Busy(run, RunLock.ReadPid(run));
        var ctx = new Ctx(this, run, config, keys, keys.Redactor());
        var s = ctx.State;
        if (!s.IsExplore) return Fail($"{run.RunId} is a review run: answer it with `{CliOutput.Cli} review next`.", run.RunId);
        if (s.Phase != RunPhase.WaitingForAuthor)
        {
            var shown = Show(run);
            return shown with { Body = $"There is nothing to answer: the exploration is not waiting for the author.\n\n{shown.Body}", ExitCode = 1 };
        }

        var review = RunReader.LoadRound(run, 1).Review;
        if (review is null) return Fail("round-1/review.json is missing or unreadable", run.RunId, 1);
        var ids = review.Findings.Select(f => f.Id).ToList();
        var v = Validation.ExploreResponse(responseText, ids);
        if (!v.Ok) return CliOutput.InvalidExploreResponse(s, ids, ctx.Redactor.Apply(v.ErrorText));
        var response = v.Value!;

        using var watch = new CancelWatch(run, ct, o.CancelPollInterval);
        return await Guarded(ctx, 1, () =>
        {
            RecordResponse(ctx, 1, response, review);
            var chosen = response.Chosen ?? [];
            ctx.Append(new RunFinished { T = Now, Outcome = Outcome.Explored, Message = chosen.Count == 0 ? "the author chose nothing" : $"the author chose {string.Join(", ", chosen)}" });
            return Task.FromResult(CliOutput.Finished(ctx.State));
        }, watch.Token);
    }

    /// <summary>The author's answer to round k's findings: response.json, then the author_responded tally.</summary>
    private void RecordResponse(Ctx ctx, int k, ResponseFile response, ReviewFile review)
    {
        RunFiles.WriteJson(ctx.Run.Round(k).Response, response, BobarJson.Indented.ResponseFile, ctx.Redactor);
        var sevById = review.Findings.ToDictionary(f => f.Id, f => f.Severity);
        ctx.Append(new AuthorResponded
        {
            T = Now,
            Round = k,
            Fix = SeverityCounts.Of(response.Responses.Where(x => x.Action == AuthorAction.Fix).Select(x => sevById[x.Id])),
            Rejected = response.Responses.Count(x => x.Action == AuthorAction.Reject),
        });
    }

    // ───────────────────────────── next ─────────────────────────────

    public async Task<CommandResult> NextAsync(string runRef, string responseText, CancellationToken ct)
    {
        var run = RunReader.Resolve(Paths, runRef, out var err);
        if (run is null) return Fail(err!);
        AppConfig config;
        try { config = ConfigStore.Load(Paths); }
        catch (ConfigException ex) { return Fail(ex.Message, run.RunId); }
        var keys = KeysEnv.Load(Paths.KeysFile);

        using var lk = RunLock.TryAcquire(run);
        if (lk is null) return CliOutput.Busy(run, RunLock.ReadPid(run));
        var ctx = new Ctx(this, run, config, keys, keys.Redactor());
        var s = ctx.State;
        if (s.IsExplore) return Fail($"{run.RunId} is an explore run: answer it with `{CliOutput.Cli} explore finish`, not `next`.", run.RunId);
        if (s.IsDiscuss) return Fail($"{run.RunId} is a discussion: it takes `{CliOutput.Cli} discuss ask` and `discuss report`, not `next`.", run.RunId);
        if (s.Phase != RunPhase.WaitingForAuthor)
        {
            var shown = Show(run);
            return shown with { Body = $"There is nothing to respond to: the run is not waiting for the author.\n\n{shown.Body}", ExitCode = 1 };
        }

        var k = s.CurrentRound!.Number;
        var review = RunReader.LoadRound(run, k).Review;
        if (review is null) return Fail($"round-{k}/review.json is missing or unreadable", run.RunId, k);
        var ids = review.Findings.Select(f => f.Id).ToList();
        var v = Validation.Response(responseText, ids);
        if (!v.Ok) return CliOutput.InvalidResponse(s, k, ids, ctx.Redactor.Apply(v.ErrorText));
        var response = v.Value!;

        using var watch = new CancelWatch(run, ct, o.CancelPollInterval);
        ct = watch.Token;
        return await Guarded(ctx, k, async () =>
        {
            RecordResponse(ctx, k, response, review);

            var (step, reason) = LoopRules.Decide(k, ctx.State.MaxRounds, review.Findings, response.Responses);
            switch (step)
            {
                case AfterResponse.AwaitDecisions:
                    ctx.Append(new AwaitingDecisions
                    {
                        T = Now,
                        Round = k,
                        Reason = reason!.Value,
                        Ids = response.Responses.Where(x => x.Action == AuthorAction.Reject).Select(x => x.Id).ToList(),
                    });
                    if (config.OpenGuiOnDecisions) TryLaunchGui(run);
                    return CliOutput.Awaiting(ctx.State, decisionsWritten: false);
                case AfterResponse.DoneCap:
                    ctx.Append(new RunFinished { T = Now, Outcome = Outcome.Cap, Message = "round cap reached; the last fixes were not re-reviewed" });
                    return CliOutput.Finished(ctx.State);
                default:
                    return await ReviewRoundAsync(ctx, k + 1, ct);
            }
        }, ct);
    }

    // ───────────────────────────── wait ─────────────────────────────

    public async Task<CommandResult> WaitAsync(string runRef, CancellationToken ct)
    {
        var run = RunReader.Resolve(Paths, runRef, out var err);
        if (run is null) return Fail(err!);
        if (RunReader.LoadState(run).IsDiscuss) return await WaitAnswersAsync(run, ct);
        while (true)
        {
            var s = RunReader.LoadState(run);
            if (s.Finished is not null)
            {
                if (s.Finished.Outcome == Outcome.Decided && RunReader.LoadDecisions(run) is { } done)
                    return CliOutput.Decided(s, RunReader.LoadRounds(run, s), done);
                return CliOutput.Finished(s);
            }
            if (s.Phase != RunPhase.AwaitingDecisions)
            {
                var shown = Show(run);
                return shown with { Body = $"The run is not awaiting decisions, so there is nothing to wait for.\n\n{shown.Body}", ExitCode = 1 };
            }
            if (File.Exists(run.Decisions))
            {
                var d = RunReader.LoadDecisions(run);
                var v = Validation.Decisions(d, s.Awaiting!.Ids);
                if (!v.Ok) return Fail($"decisions.json is invalid:\n{v.ErrorText}", run.RunId, s.Awaiting.Round);
                var redactor = KeysEnv.Load(Paths.KeysFile).Redactor();
                using (var lk = await RunLock.AcquireAsync(run, RunActions.LockPatience, ct))
                {
                    if (lk is null) return CliOutput.Busy(run, RunLock.ReadPid(run));
                    s = RunReader.LoadState(run);
                    if (s.Finished is not null) continue;
                    if (s.Decisions is null) EventLog.Append(run, RunActions.Submitted(d!, o.Time), redactor);
                    EventLog.Append(run, new RunFinished { T = Now, Outcome = Outcome.Decided, Message = "the user decided" }, redactor);
                }
                s = RunReader.LoadState(run);
                return CliOutput.Decided(s, RunReader.LoadRounds(run, s), d!);
            }
            await Task.Delay(o.WaitPollInterval, ct);
        }
    }

    // ───────────────────────────── stream ─────────────────────────────

    /// <summary>
    /// Prints round k's findings as a streaming reviewer reports them (one block each, for the author's
    /// Monitor), then an `END` line once the round's review is in or the run stopped. Read-only and
    /// lock-free, so it runs beside the start/next command doing the review, and killing it is harmless.
    /// Without a run: the first run of <paramref name="repoDir"/> that starts after this call (so arm it
    /// before `start`), round 1. Without a round: the run's latest round.
    /// </summary>
    public async Task<int> StreamAsync(string? runRef, int? round, string repoDir, TextWriter output, CancellationToken ct)
    {
        var since = Now;
        RunPaths? run;
        if (runRef is not null)
        {
            run = RunReader.Resolve(Paths, runRef, out var err);
            if (run is null) return End(output, $"END error: {err}", 1);
        }
        else
        {
            string repo;
            try { repo = await GitCli.TopLevelAsync(Path.GetFullPath(repoDir), ct); }
            catch (GitException ex) { return End(output, $"END error: {ex.Message}", 1); }
            var deadline = since + o.StreamFindRunTimeout;
            while ((run = FirstRunOf(repo, since)) is null)
            {
                if (Now > deadline) return End(output, $"END error: no run started in {repo} after this stream did. Arm the stream before `review start`, or pass the run id.", 1);
                await Task.Delay(o.StreamPollInterval, ct);
            }
            round ??= 1;
            // The author has no run id yet (`start` hasn't returned), and `review verdicts` needs one.
            output.WriteLine($"RUN {run.RunId} round={round}\n");
            output.Flush();
        }

        long pos = 0;
        while (true)
        {
            var s = RunReader.LoadState(run);
            var k = round ?? s.CurrentRound?.Number ?? 1;
            // Read the state before the file: findings written before the round ended are then all printed below.
            var rs = s.Rounds.FirstOrDefault(r => r.Number == k);
            var notComing = rs is null && s.Phase == RunPhase.AwaitingDecisions;
            var over = rs?.Review is not null || s.Finished is not null || s.Rounds.Any(r => r.Number > k) || notComing;
            pos = PrintNewFindings(run.Round(k).Findings, pos, output);
            if (over)
            {
                var why = rs?.Review is not null
                    ? $"the reviewer finished round {k} ({Summary.Plural(rs.Review.Findings.Count, "finding")}). Its full result comes from the background `review start`/`next` command."
                    : s.Finished is not null ? $"the run ended ({s.Finished.Outcome.ToString().ToLowerInvariant()}: {s.Finished.Message})."
                    : notComing ? $"there is no round {k}: the run is awaiting the user's decisions." : $"round {k} is over.";
                return End(output, $"END round={k}: {why}", 0);
            }
            await Task.Delay(o.StreamPollInterval, ct);
        }
    }

    /// <summary>The oldest run of <paramref name="repo"/> that started at or after <paramref name="since"/>.</summary>
    private RunPaths? FirstRunOf(string repo, DateTimeOffset since)
    {
        var want = Path.GetFullPath(repo).TrimEnd('\\', '/');
        RunPaths? found = null;
        DateTimeOffset? first = null;
        // The 10 newest reviews and explorations (discussions have no repo).
        foreach (var (r, st) in RunReader.ListRuns(Paths).Select(r => (r, RunReader.LoadState(r))).Where(x => !x.Item2.IsDiscuss).Take(10))
        {
            if (st.Started is null || st.StartedAt is not { } at || at < since) continue;
            if (!string.Equals(Path.GetFullPath(st.Started.Repo).TrimEnd('\\', '/'), want, StringComparison.OrdinalIgnoreCase)) continue;
            if (first is null || at < first) (found, first) = (r, at);
        }
        return found;
    }

    /// <summary>
    /// Prints the complete lines appended to findings.jsonl since byte <paramref name="pos"/> and returns the
    /// position after the last of them. An incomplete last line is left for the next call.
    /// </summary>
    private static long PrintNewFindings(string file, long pos, TextWriter output)
    {
        byte[] chunk;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = fs.Length;
            if (len <= pos) return pos;
            fs.Seek(pos, SeekOrigin.Begin);
            chunk = new byte[len - pos];
            fs.ReadExactly(chunk);
        }
        catch (FileNotFoundException) { return pos; }
        catch (DirectoryNotFoundException) { return pos; }
        var lastNl = Array.LastIndexOf(chunk, (byte)'\n');
        if (lastNl < 0) return pos;
        foreach (var line in RunFiles.Utf8.GetString(chunk, 0, lastNl).Split('\n'))
        {
            System.Text.Json.Nodes.JsonObject? f;
            try { f = System.Text.Json.Nodes.JsonNode.Parse(line) as System.Text.Json.Nodes.JsonObject; }
            catch (System.Text.Json.JsonException) { continue; }
            if (f is null) continue;
            if (f["restart"] is not null)
            {
                output.WriteLine("RESTART: the reviewer started this round over; the findings above no longer count, and their ids will be reused.\n");
                continue;
            }
            var sb = new System.Text.StringBuilder();
            sb.Append($"### {f.Str("id")} [{f.Str("severity")}] {f.Str("title")}");
            if (f.Str("previousId") is { } prev) sb.Append($"   (repeats {prev})");
            sb.Append('\n');
            if (f["refs"] is System.Text.Json.Nodes.JsonArray refs && refs.Count > 0)
                sb.Append(string.Join(", ", refs.Select(x => x?.ToString()))).Append('\n');
            sb.Append(f.Str("explanation")?.Trim()).Append('\n');
            output.WriteLine(sb.ToString());
        }
        output.Flush();
        return pos + lastNl + 1;
    }

    private static int End(TextWriter output, string line, int code)
    {
        output.WriteLine(line);
        output.Flush();
        return code;
    }

    // ───────────────────────────── verdicts ─────────────────────────────

    /// <summary>
    /// The author declares which of the current round's findings it will fix and which it rejects, before
    /// applying the fixes, so the GUI can show them. Appended to verdicts.jsonl; a later call overrides an
    /// earlier one per id. Lock-free, so it works while a streaming reviewer is still on the round. It never
    /// affects the loop: `next --response` is still the author's answer.
    /// </summary>
    public CommandResult Verdicts(string runRef, IReadOnlyList<string> fix, IReadOnlyList<string> reject)
    {
        var run = RunReader.Resolve(Paths, runRef, out var err);
        if (run is null) return Fail(err!);
        var s = RunReader.LoadState(run);
        var final = s.IsExplore ? "`explore finish`" : "`next --response`";
        CommandResult Invalid(string why) => new(Statuses.InvalidVerdicts, run.RunId, s.CurrentRound?.Number,
            $"The verdicts were not recorded: {why}\n\nThey only feed the GUI, so carry on either way; {final} is what counts.\n", 2);

        if (fix.Count + reject.Count == 0) return Invalid("pass --fix and/or --reject with finding ids.");
        if (s.Finished is not null || s.Awaiting is not null || s.CurrentRound is not { Response: null } r)
            return Invalid("no round is waiting for your answer.");
        var known = r.Review is not null
            ? RunReader.LoadRound(run, r.Number).Review?.Findings.Select(f => f.Id).ToList() ?? []
            : LiveRound.Load(run, s, RunReader.LoadRounds(run, s))?.Streamed.Select(f => f.Id).ToList() ?? [];
        var unknown = fix.Concat(reject).Where(id => !known.Contains(id)).Distinct().ToList();
        if (unknown.Count > 0)
            return Invalid($"round {r.Number} has no finding {string.Join(", ", unknown)}{(known.Count == 0 ? " (none so far)" : $" (it has {string.Join(", ", known)})")}.");
        var both = fix.Intersect(reject).ToList();
        if (both.Count > 0) return Invalid($"{string.Join(", ", both)} is both fixed and rejected.");

        var redactor = KeysEnv.Load(Paths.KeysFile).Redactor();
        try { RunFiles.AppendLine(run.Round(r.Number).Verdicts, LiveRound.VerdictLine(Now, fix.Distinct(), reject.Distinct()), redactor); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid(redactor.Apply(ex.Message));
        }
        var parts = new List<string>();
        if (fix.Count > 0) parts.Add($"{(s.IsExplore ? "accept" : "fix")} {string.Join(", ", fix.Distinct())}");
        if (reject.Count > 0) parts.Add($"reject {string.Join(", ", reject.Distinct())}");
        var then = s.IsExplore
            ? $"This is for display only: edit nothing until `{CliOutput.Cli} explore finish` returns `done`."
            : "Now apply the fixes.";
        return new(Statuses.VerdictsSaved, run.RunId, r.Number, $"Recorded: {string.Join("; ", parts)}. {then}\n");
    }

    // ───────────────────────────── hindsight ─────────────────────────────

    /// <summary>
    /// After the run finished (any outcome but cancelled): the author's look back on it. Stored in
    /// hindsight.json plus a hindsight_submitted event; sending it again replaces it. Never affects the outcome.
    /// </summary>
    public CommandResult Hindsight(string runRef, string text)
    {
        var run = RunReader.Resolve(Paths, runRef, out var err);
        if (run is null) return Fail(err!);
        using var lk = RunLock.TryAcquire(run);
        if (lk is null) return CliOutput.Busy(run, RunLock.ReadPid(run));
        var s = RunReader.LoadState(run);
        if (!s.WantsHindsight)
        {
            return Fail($"No hindsight now: {s.NoHindsightReason}. The run's current state:\n\n{Show(run).Render()}", run.RunId);
        }
        if (s.IsDiscuss) return DiscussHindsight(run, text);

        var responses = RunReader.LoadRounds(run, s).Where(r => r.Response is not null).SelectMany(r => r.Response!.Responses).ToList();
        var v = Validation.Hindsight(text,
            responses.Where(x => x.Action == AuthorAction.Fix).Select(x => x.Id).ToHashSet(),
            responses.Where(x => x.Action == AuthorAction.Reject).Select(x => x.Id).ToHashSet());
        var redactor = KeysEnv.Load(Paths.KeysFile).Redactor();
        if (!v.Ok) return CliOutput.InvalidHindsight(s, redactor.Apply(v.ErrorText));
        var h = v.Value!;

        try
        {
            RunFiles.WriteJson(run.Hindsight, h, BobarJson.Indented.HindsightFile, redactor);
            EventLog.Append(run, new HindsightSubmitted { T = Now, Usefulness = h.Usefulness, AcceptedWrongly = h.AcceptedWrongly.Count, RejectedWrongly = h.RejectedWrongly.Count }, redactor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(redactor.Apply($"Could not save the hindsight: {ex.Message}"), run.RunId);
        }
        return CliOutput.HindsightSaved(RunReader.LoadState(run), h);
    }

    // ───────────────────────────── cancel / show / runs ─────────────────────────────

    public async Task<CommandResult> CancelAsync(string runRef)
    {
        var run = RunReader.Resolve(Paths, runRef, out var err);
        if (run is null) return Fail(err!);
        var redactor = KeysEnv.Load(Paths.KeysFile).Redactor();
        var r = await RunActions.CancelAsync(run, redactor, o.Time);
        var s = RunReader.LoadState(run);
        if (!r.Ok && s.Finished is null) return Fail($"Could not cancel: {r.Message}", run.RunId);
        return CliOutput.Finished(s, r.Ok ? $"BobarCode {r.Message}." : $"Nothing to cancel: {r.Message}.");
    }

    public CommandResult Show(string runRef)
    {
        var run = RunReader.Resolve(Paths, runRef, out var err);
        return run is null ? Fail(err!) : Show(run);
    }

    public static CommandResult Show(RunPaths run)
    {
        var s = RunReader.LoadState(run);
        if (s.IsDiscuss)
        {
            switch (s.Phase)
            {
                case RunPhase.Finished: return CliOutput.Finished(s);
                case RunPhase.AwaitingAnswers: return CliOutput.AwaitingAnswers(s, opened: null);
                case RunPhase.AwaitingReport:
                    return s.Answers is not null && LoadAnswered(run) is { } a ? CliOutput.Answered(s, a.Questions, a.Answers) : CliOutput.Discussed(s, run);
                default: return CliOutput.Running(s, RunLock.HeldByLiveCli(run));
            }
        }
        switch (s.Phase)
        {
            case RunPhase.Finished:
                if (s.Finished!.Outcome == Outcome.Decided && RunReader.LoadDecisions(run) is { } d)
                    return CliOutput.Decided(s, RunReader.LoadRounds(run, s), d);
                // An answered exploration: its findings and the author's answers, so a lost session knows what it chose.
                if (s.Finished.Outcome == Outcome.Explored && RunReader.LoadRound(run, 1) is { Review: not null } ex)
                    return CliOutput.Finished(s, $"Chosen: {(ex.Response?.Chosen is { Count: > 0 } c ? string.Join(", ", c) : "nothing")}.\n\n{Rendering.History([ex]).Trim()}");
                return CliOutput.Finished(s);
            case RunPhase.AwaitingDecisions:
                return CliOutput.Awaiting(s, File.Exists(run.Decisions));
            case RunPhase.WaitingForAuthor:
                var review = RunReader.LoadRound(run, s.CurrentRound!.Number).Review;
                return review is null ? Fail("review.json is missing", run.RunId) : CliOutput.Findings(s, review);
            default:
                return CliOutput.Running(s, RunLock.HeldByLiveCli(run));
        }
    }

    public string Runs(int limit = 20)
    {
        var runs = RunReader.ListRuns(Paths).Take(limit).ToList();
        if (runs.Count == 0) return $"No runs yet in {Paths.RunsDir}.\n";
        var sb = new System.Text.StringBuilder();
        sb.Append("| run | status | rounds | reviewer | hindsight |\n|---|---|---|---|---|\n");
        foreach (var r in runs)
        {
            var s = RunReader.LoadState(r);
            var h = s.Hindsight is { } hs ? State.Hindsight.Rating(hs.Usefulness) : s.WantsHindsight ? "pending" : "";
            var rounds = s.IsDiscuss ? s.DiscussRounds.Count : s.Rounds.Count;
            sb.Append($"| {r.RunId} | {RunStatus.Of(s, RunLock.HeldByLiveCli(r), o.Time.GetLocalNow()).Label()} | {rounds} | {s.Started?.Reviewer} | {h} |\n");
        }
        return sb.ToString();
    }

    // ───────────────────────────── the round ─────────────────────────────

    /// <summary>Review k: snapshot (k ≥ 2), prompt, reviewer call with retry/repair, ids, stop check.</summary>
    private async Task<CommandResult> ReviewRoundAsync(Ctx ctx, int k, CancellationToken ct)
    {
        var run = ctx.Run;
        var rp = run.Round(k);
        var started = ctx.State.Started!;
        ctx.Append(new ReviewRequested { T = Now, Round = k });

        string? deltaPath = null, historyPath = null;
        var earlier = Enumerable.Range(1, k - 1).Select(i => RunReader.LoadRound(run, i)).ToList();
        string diff;
        if (k >= 2)
        {
            var tree = await GitCli.SnapshotAsync(started.Repo, started.Seed is "" ? started.Base : started.Seed, rp.Index, ct);
            diff = await GitCli.DiffAsync(started.Repo, started.Base, tree, ct);
            var prevTree = (RunFiles.ReadShared(run.Round(k - 1).Tree) ?? "").Trim();
            if (prevTree.Length == 0) throw new RunErrorException($"round-{k - 1}/tree.txt is missing");
            var delta = await GitCli.DiffAsync(started.Repo, prevTree, tree, ct);
            RunFiles.WriteText(rp.Tree, tree + "\n", ctx.Redactor);
            RunFiles.WriteText(rp.Diff, diff, ctx.Redactor);
            RunFiles.WriteText(rp.Delta, delta, ctx.Redactor);
            RunFiles.WriteText(rp.History, Rendering.History(earlier), ctx.Redactor);
            deltaPath = rp.Delta;
            historyPath = rp.History;
        }
        else
        {
            diff = RunFiles.ReadShared(rp.Diff) ?? "";
        }

        var reviewer = o.ReviewerFactory(ReviewerContextFor(started.Reviewer, started.ReviewerConfig, ctx.Config, ctx.Keys));
        // A streaming reviewer reviews a copy of the round's tree, so the author can fix the real
        // working tree while it still works. The copy is removed when the reviewer is done.
        var streams = reviewer.StreamsFindings;
        try
        {
            var reviewDir = started.Repo;
            if (streams)
            {
                SweepSnapshots(run);
                var tree = (RunFiles.ReadShared(rp.Tree) ?? "").Trim();
                if (tree.Length == 0) throw new RunErrorException($"round-{k}/tree.txt is missing");
                await GitCli.MaterializeAsync(started.Repo, tree, run.Snapshot, run.SnapshotIndex, ct);
                reviewDir = run.Snapshot;
            }
            return await ReviewInDirAsync(ctx, k, reviewer, reviewDir, diff, deltaPath, historyPath, earlier, ct);
        }
        finally
        {
            if (streams) RunFiles.TryDeleteDir(run.Snapshot);
        }
    }

    private async Task<CommandResult> ReviewInDirAsync(Ctx ctx, int k, IReviewer reviewer, string reviewDir, string diff, string? deltaPath, string? historyPath,
        List<RoundData> earlier, CancellationToken ct)
    {
        var run = ctx.Run;
        var rp = run.Round(k);
        var started = ctx.State.Started!;
        var streams = reviewer.StreamsFindings;
        var rejectedLast = k >= 2
            ? earlier[^1].Response?.Responses.Where(x => x.Action == AuthorAction.Reject).Select(x => x.Id).ToList() ?? []
            : [];
        var style = streams ? PromptBuilder.OutputStyle.Tool
            : started.ReviewerConfig.Kind == ReviewerKinds.Claude ? PromptBuilder.OutputStyle.Structured
            : PromptBuilder.OutputStyle.Json;
        var explore = ctx.Exploration;
        string FreshPrompt() => explore is { } ex
            ? PromptBuilder.Explore(reviewDir, RunFiles.ReadShared(run.Desc) ?? "", ex.Lines, style, ctx.Config.ReviewerInstructions)
            : PromptBuilder.Build(new PromptBuilder.Inputs(k, reviewDir, RunFiles.ReadShared(run.Desc) ?? "", rp.Diff, deltaPath, historyPath, rejectedLast,
                style, started.Commits.Count > 0, ctx.Config.ReviewerInstructions));
        var idPrefix = explore is null ? $"R{k}" : "E";
        // Round k ≥ 2 continues the reviewer's previous session, so it doesn't explore the repo again.
        var resumeId = k >= 2 ? ctx.State.Rounds.FirstOrDefault(r => r.Number == k - 1)?.Review?.SessionId : null;
        var prompt = resumeId is null ? FreshPrompt() : PromptBuilder.Resume(k, rp.Diff, deltaPath!, historyPath!, earlier[^1], rejectedLast, style, ctx.Config.ReviewerInstructions);
        RunFiles.WriteText(rp.Prompt, prompt, ctx.Redactor);

        // An exploration's previousId names one of the past rejections listed in its prompt.
        var earlierIds = explore is { } e2 ? e2.Ids.ToHashSet()
            : earlier.Where(r => r.Review is not null).SelectMany(r => r.Review!.Findings.Select(f => f.Id)).ToHashSet();
        using var rawLog = new RawLog(rp.ReviewerLog, ctx.Redactor);
        using var stream = streams ? new FindingsLog(rp, idPrefix, ctx.Redactor, refs => NormalizeRefs(refs, started.Repo, reviewDir).Refs) : null;
        var call = new ReviewerCall
        {
            Round = k,
            Prompt = prompt,
            RepoDir = reviewDir,
            RunDir = run.Dir,
            RoundDir = rp.Dir,
            RawLog = rawLog.Write,
            PriorRounds = earlier,
            DiffText = diff,
            ResumeSessionId = resumeId,
            OnFinding = stream is null ? null : stream.Add,
            IdPrefix = idPrefix,
            EarlierIds = earlierIds.Order(IdComparer.Instance).ToList(),
        };
        // A call in a fresh session (round 1, a transient retry of it, a fresh session after a failed resume)
        // starts the round's findings over. A call that resumes a session keeps them: that session already
        // holds the earlier report_finding calls, so numbering continues after them.
        ReviewerCall Prepare(ReviewerCall c, bool fresh)
        {
            if (stream is null) return c;
            if (fresh) stream.BeginAttempt();
            return c with { FirstFindingNumber = stream.MaxNumber + 1 };
        }
        Func<CancellationToken, Task<ReviewerResult>> Attempt(ReviewerCall c) => t => reviewer.RunAsync(Prepare(c, c.ResumeSessionId is null), t);
        var timeout = TimeSpan.FromMinutes(ctx.Config.ReviewerTimeoutMinutes);
        var sw = Stopwatch.StartNew();
        // A streaming reviewer's review is what it reported, once it says DONE. Without DONE: a findings
        // object (it ignored the tool and reported nothing) is used like any reviewer's; anything else, even
        // after some reports (it may not have finished), gets the repair turn. The repair resumes the session
        // with the tool on: what was reported still counts, and numbering continues.
        Validated<List<RawFinding>> Check(ReviewerResult r)
        {
            if (stream is null) return Validation.Review(r.Parsed, earlierIds);
            if (IsDone(r.FinalText))
            {
                var sv = Validation.Review(stream.AsReviewJson(), earlierIds);
                if (!sv.Ok) throw new RunErrorException($"the reviewer's reported findings are invalid:\n{sv.ErrorText}");
                return sv;
            }
            if (stream.Count == 0 && r.Parsed is System.Text.Json.Nodes.JsonObject o && o["findings"] is System.Text.Json.Nodes.JsonArray)
                return Validation.Review(r.Parsed, earlierIds);
            return new(null, [stream.Count > 0
                ? $"your final message was not `DONE`, so your review may be unfinished (you reported {Summary.Plural(stream.Count, "finding")})"
                : "your final message was neither `DONE` (after reporting your findings with report_finding) nor a {\"findings\": [...]} object"]);
        }
        var (res, raw, usage, searches, duration, repaired) = await CallChecked(ctx, k, null, call, rp.Prompt, FreshPrompt, Attempt, Check,
            (prev, error) =>
            {
                var repairPrompt = stream is null ? PromptBuilder.Repair(error) : PromptBuilder.RepairStreaming(error);
                return c => reviewer.RepairAsync(prev with { Call = Prepare(prev.Call, fresh: false) }, repairPrompt, c);
            }, timeout, ct);
        var (origins, withdrawn) = LoopRules.Classify(raw.Select(f => f.PreviousId).ToList(), rejectedLast);
        var review = new ReviewFile
        {
            Round = k,
            Withdrawn = withdrawn,
            Findings = raw.Select((f, i) =>
            {
                var (refs, warnings) = NormalizeRefs(f.Refs, started.Repo, reviewDir);
                return new StoredFinding
                {
                    Id = $"{idPrefix}.{i + 1}",
                    Severity = f.Severity,
                    Title = f.Title,
                    Explanation = f.Explanation,
                    Refs = refs,
                    PreviousId = f.PreviousId,
                    Origin = origins[i],
                    Warnings = warnings,
                };
            }).ToList(),
        };
        RunFiles.WriteJson(rp.Review, review, BobarJson.Indented.ReviewFile, ctx.Redactor);
        var models = res.ResolvedModels.Count > 0 ? res.ResolvedModels : [res.RequestedModel];
        ctx.Append(new ReviewReceived
        {
            T = Now,
            Round = k,
            Counts = SeverityCounts.Of(review.Findings.Select(f => f.Severity)),
            Maintained = origins.Count(x => x == FindingOrigin.Maintained),
            New = origins.Count(x => x == FindingOrigin.New),
            Repeated = origins.Count(x => x == FindingOrigin.Repeated),
            Withdrawn = withdrawn.Count,
            Findings = review.Findings.Select(f => new FindingBrief { Id = f.Id, Severity = f.Severity, Origin = f.Origin }).ToList(),
            DurationMs = (long)(duration > TimeSpan.Zero ? duration : sw.Elapsed).TotalMilliseconds,
            Usage = usage,
            CostUsd = Pricing.Cost(started.ReviewerConfig, models, usage, ctx.Config.Prices, searches, ctx.Config.WebSearch.PricePerCall),
            WebSearches = searches,
            RequestedModel = string.IsNullOrEmpty(res.RequestedModel) ? started.ReviewerConfig.Model : res.RequestedModel,
            ResolvedModels = res.ResolvedModels,
            RequestedReasoning = res.RequestedReasoning ?? started.ReviewerConfig.Reasoning,
            EffectiveReasoning = res.EffectiveReasoning,
            Repaired = repaired,
            SessionId = res.SessionId,
        });

        if (review.Findings.Count == 0)
        {
            ctx.Append(new RunFinished { T = Now, Outcome = Outcome.Clean, Message = $"no findings in round {k}" });
            return CliOutput.Finished(ctx.State);
        }
        return CliOutput.Findings(ctx.State, review);
    }

    private sealed record Checked<T>(ReviewerResult Res, T Value, TokenUsage Usage, int Searches, TimeSpan Duration, bool Repaired);

    /// <summary>A reviewer call with <see cref="CallWithRetry"/>; when resuming a session fails, once more in a fresh session
    /// with the full prompt; then <paramref name="check"/>, with one repair turn if it fails. Usage, searches and time add up
    /// over all attempts.</summary>
    /// <param name="discusser">In a discussion: whose call it is (its events are then discusser_*).</param>
    private async Task<Checked<T>> CallChecked<T>(Ctx ctx, int k, string? discusser, ReviewerCall call, string promptPath, Func<string> freshPrompt,
        Func<ReviewerCall, Func<CancellationToken, Task<ReviewerResult>>> attempt, Func<ReviewerResult, Validated<T>> check,
        Func<ReviewerResult, string, Func<CancellationToken, Task<ReviewerResult>>> repair, TimeSpan timeout, CancellationToken ct)
    {
        var usage = new TokenUsage();
        var searches = 0;
        var duration = TimeSpan.Zero;
        ReviewerResult res;
        var first = Stopwatch.StartNew();
        try
        {
            res = await CallWithRetry(ctx, k, attempt(call), timeout, ct, discusser);
        }
        catch (ReviewerException ex) when (call.ResumeSessionId is not null && !ex.TimedOut && !ex.Final)
        {
            // E.g. the session file is gone. Redo the round in a fresh session with the full prompt.
            // Quota and already-retried failures are Final; other errors aren't matched by vendor text.
            usage += ex.Usage;
            searches += ex.WebSearches;
            duration += first.Elapsed;
            ctx.Append(discusser is null
                ? new ReviewerRetry { T = Now, Round = k, Reason = ex.Message, Fresh = true }
                : new DiscusserRetry { T = Now, Round = k, Discusser = discusser, Reason = ex.Message, Fresh = true });
            call = call with { Prompt = freshPrompt(), ResumeSessionId = null };
            RunFiles.WriteText(promptPath, call.Prompt, ctx.Redactor);
            res = await CallWithRetry(ctx, k, attempt(call), timeout, ct, discusser);
        }
        usage += res.Usage;
        searches += res.WebSearches;
        duration += res.Duration;
        var v = check(res);
        var repaired = false;
        if (!v.Ok)
        {
            ctx.Append(discusser is null
                ? new ReviewerRepair { T = Now, Round = k, Error = v.ErrorText }
                : new DiscusserRepair { T = Now, Round = k, Discusser = discusser, Error = v.ErrorText });
            res = await CallWithRetry(ctx, k, repair(res, v.ErrorText), timeout, ct, discusser);
            usage += res.Usage;
            searches += res.WebSearches;
            duration += res.Duration;
            v = check(res);
            repaired = true;
            if (!v.Ok) throw new RunErrorException($"{(discusser is null ? "the reviewer's" : "its")} output was still invalid after a repair turn:\n{v.ErrorText}");
        }
        return new(res, v.Value!, usage, searches, duration, repaired);
    }

    /// <summary>One reviewer call with the timeout and the single transient retry (§9).</summary>
    /// <param name="discusser">In a discussion: whose call it is (the retry is then a discusser_retry).</param>
    private async Task<ReviewerResult> CallWithRetry(Ctx ctx, int k, Func<CancellationToken, Task<ReviewerResult>> call, TimeSpan timeout, CancellationToken ct, string? discusser = null)
    {
        try
        {
            return await WithTimeout(call, timeout, ct);
        }
        catch (ReviewerException ex) when (ex.Transient)
        {
            var delay = ex.RetryAfter ?? RetryAfterFromText(ex.Message) ?? DefaultRetryDelay;
            if (delay > MaxRetryDelay) delay = MaxRetryDelay;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            var seconds = (int)Math.Ceiling(delay.TotalSeconds);
            ctx.Append(discusser is null
                ? new ReviewerRetry { T = Now, Round = k, Reason = ex.Message, DelaySeconds = seconds }
                : new DiscusserRetry { T = Now, Round = k, Discusser = discusser, Reason = ex.Message, DelaySeconds = seconds });
            await o.Delay(delay, ct);
            try
            {
                // The failed attempt's searches are billed too.
                var res = await WithTimeout(call, timeout, ct);
                return res with { WebSearches = res.WebSearches + ex.WebSearches };
            }
            catch (ReviewerException ex2)
            {
                throw new ReviewerException($"the reviewer failed twice; last error: {ex2.Message}", false, null, ex2.TimedOut, ex2.Usage, final: true, webSearches: ex.WebSearches + ex2.WebSearches);
            }
        }
    }

    private static async Task<ReviewerResult> WithTimeout(Func<CancellationToken, Task<ReviewerResult>> call, TimeSpan timeout, CancellationToken ct)
    {
        using var tcs = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, tcs.Token);
        try
        {
            return await call(linked.Token);
        }
        catch (OperationCanceledException) when (tcs.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ReviewerException($"the reviewer timed out after {Summary.Duration(timeout)} (reviewerTimeoutMinutes)", timedOut: true);
        }
    }

    /// <summary>"retry-after: 30", "retry after 30s", "Please try again in 1m20s"…</summary>
    public static TimeSpan? RetryAfterFromText(string text)
    {
        var m = Regex.Match(text, @"retry[- ]after[^0-9]{0,5}(\d+(?:\.\d+)?)\s*(ms|s|sec|seconds)?", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var n))
            return m.Groups[2].Value.Equals("ms", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromMilliseconds(n) : TimeSpan.FromSeconds(n);
        m = Regex.Match(text, @"try again in\s*(?:(\d+)m)?\s*(\d+(?:\.\d+)?)s", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var mins = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            return TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        return null;
    }

    /// <summary>
    /// Runs a command body; any failure ends the run as an error. A cancel (ct: Ctrl+C or a cancel.request,
    /// whose text is the reason) ends it as cancelled, also when it lands just after the body finished
    /// without ending the run, so the author never gets findings for a run that is being cancelled.
    /// </summary>
    private async Task<CommandResult> Guarded(Ctx ctx, int round, Func<Task<CommandResult>> body, CancellationToken ct)
    {
        string message;
        try
        {
            var r = await body();
            return ct.IsCancellationRequested && ctx.State.Finished is null ? Cancelled(ctx) : r;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Cancelled(ctx);
        }
        catch (ReviewerException ex) { message = $"reviewer failed: {ex.Message}"; }
        catch (RunErrorException ex) { message = ex.Message; }
        catch (GitException ex) { message = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ConfigException)
        {
            message = $"{ex.GetType().Name}: {ex.Message}";
        }
        ctx.Append(new RunFinished { T = Now, Outcome = Outcome.Error, Message = message });
        var s = ctx.State;
        return CliOutput.Finished(s) with { Round = s.CurrentRound?.Number ?? round };
    }

    private CommandResult Cancelled(Ctx ctx)
    {
        var why = RunFiles.ReadShared(ctx.Run.CancelRequest)?.Trim() is { Length: > 0 } m ? m : "interrupted (Ctrl+C)";
        ctx.Append(new RunFinished { T = Now, Outcome = Outcome.Cancelled, Message = why });
        return CliOutput.Finished(ctx.State);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private DateTimeOffset Now => o.Time.GetLocalNow();

    /// <summary>A streaming reviewer's closing message: just `DONE`, give or take case, markdown and a full stop.
    /// Anything more ("DONE, but I couldn't finish") isn't, and gets the repair turn.</summary>
    internal static bool IsDone(string text) => text.Trim().Trim('*', '`', '#', '.', '!', ' ', '\n', '\r').Equals("DONE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes snapshots that a killed or crashed CLI left behind: a snapshot is only in use while a CLI
    /// holds its run's lock, so any other run's snapshot without a live holder can go.
    /// </summary>
    private void SweepSnapshots(RunPaths current)
    {
        var root = Path.GetDirectoryName(current.Snapshot)!;
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(dir);
            if (id == current.RunId || RunLock.HeldByLiveCli(Paths.Run(id))) continue;
            RunFiles.TryDeleteDir(dir);
            RunFiles.TryDeleteFile(dir + ".index");
        }
    }


    private bool _warnedNoSearch;

    /// <summary>The reviewer's context; for a Pi reviewer without the web search key, also a one-time warning on the progress feed.</summary>
    internal ReviewerContext ReviewerContextFor(string shorthand, ReviewerConfig rcfg, AppConfig config, KeysEnv keys)
    {
        var c = new ReviewerContext(shorthand, rcfg, keys, Paths, config.WebSearch);
        if (rcfg.Kind == ReviewerKinds.Pi && !_warnedNoSearch && PiReviewer.SearchKey(c) is null && config.WebSearch.Env is { Length: > 0 } env)
        {
            _warnedNoSearch = true;
            o.Progress.WriteLine($"(no web search for Pi reviewers: keys.env has no {env}; they can still fetch pages)");
            o.Progress.Flush();
        }
        return c;
    }

    private static bool TryReviewerConfig(AppConfig config, string shorthand, out ReviewerConfig rcfg)
    {
        if (config.Reviewers.TryGetValue(shorthand, out rcfg!)) return true;
        if (shorthand == FakeShorthand)
        {
            rcfg = new ReviewerConfig { Kind = ReviewerKinds.Fake, Model = "fake" };
            return true;
        }
        return false;
    }

    private static string RepoName(string repo) => Path.GetFileName(repo.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : "repo";

    /// <summary>A new run id for <paramref name="name"/> (repo dir name or topic slug), created as an empty run dir.</summary>
    private RunPaths NewRun(string name)
    {
        var run = Paths.Run(RunIds.New(name, Now, id => Directory.Exists(Paths.Run(id).Dir)));
        Directory.CreateDirectory(run.Dir);
        return run;
    }

    /// <summary>True when the GUI was launched (or brought to the front) on the run.</summary>
    private bool TryLaunchGui(RunPaths run)
    {
        if (o.LaunchGui is null) return false;
        try
        {
            o.LaunchGui(run);
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            o.Progress.WriteLine($"(could not open BobarCode: {ex.Message})");
            return false;
        }
    }

    /// <summary>Repo-relative, forward slashes; warns (never fails) when a path doesn't exist.</summary>
    /// <param name="reviewDir">Where the reviewer ran (the snapshot, for a streaming reviewer): absolute paths under it are made
    /// relative too, and paths are checked against it, since that is what the reviewer read.</param>
    internal static (List<string> Refs, List<string> Warnings) NormalizeRefs(IEnumerable<string> refs, string repo, string? reviewDir = null)
    {
        var outRefs = new List<string>();
        var warnings = new List<string>();
        string[] roots = [.. new[] { repo, reviewDir ?? repo }.Distinct().Select(d => Path.GetFullPath(d).TrimEnd('\\', '/') + Path.DirectorySeparatorChar)];
        foreach (var r0 in refs)
        {
            var m = Regex.Match(r0.Trim(), @"^(?<path>.+?)(?<loc>(?::\d+(?:[-–]\d+)?){1,2})?$");
            var path = m.Groups["path"].Value;
            var loc = m.Groups["loc"].Value;
            try
            {
                if (Path.IsPathRooted(path))
                {
                    var full = Path.GetFullPath(path);
                    if (roots.FirstOrDefault(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) is { } root) path = full[root.Length..];
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            path = path.Replace('\\', '/');
            if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            outRefs.Add(path + loc);
            bool exists;
            try
            {
                var full = Path.Combine(reviewDir ?? repo, path);
                exists = File.Exists(full) || Directory.Exists(full);
            }
            catch (ArgumentException) { exists = false; }
            if (!exists) warnings.Add($"path not found: {path}");
        }
        return (outRefs, warnings);
    }

    /// <summary>Per-command run context: appends events and echoes new summary lines to Progress.</summary>
    private sealed class Ctx
    {
        private readonly ReviewEngine _e;
        private int _printed;
        public RunPaths Run { get; }
        public AppConfig Config { get; }
        public KeysEnv Keys { get; }
        public Redactor Redactor { get; }
        public RunState State { get; private set; }
        /// <summary>Set by `explore start`: the prompt's past-rejection lines and their P ids.</summary>
        public (string Lines, List<string> Ids)? Exploration { get; init; }

        public Ctx(ReviewEngine e, RunPaths run, AppConfig config, KeysEnv keys, Redactor redactor)
        {
            _e = e;
            Run = run;
            Config = config;
            Keys = keys;
            Redactor = redactor;
            State = RunReader.LoadState(run);
            _printed = Summary.Lines(State).Count;
        }

        private readonly object _gate = new();

        /// <summary>Thread-safe: a discussion's parallel discusser calls all append through here, one at a time.</summary>
        public void Append(RunEvent ev)
        {
            lock (_gate)
            {
                EventLog.Append(Run, ev, Redactor);
                State = RunReader.LoadState(Run);
                var lines = Summary.Lines(State);
                for (; _printed < lines.Count; _printed++) _e.o.Progress.WriteLine(lines[_printed].ToString());
                _e.o.Progress.Flush();
            }
        }
    }
}

/// <summary>
/// round-k/findings.jsonl, append-only: what a streaming reviewer has reported, one line per finding
/// ({"id": "R{k}.{n}", ...finding, "t": when it arrived}), flushed per line so `review stream` can tail it.
/// A {"restart": true} line means the reviewer call was redone and the findings before it no longer count.
/// String values are redacted before serializing, so JSON escaping can't hide a secret from the redactor.
/// </summary>
internal sealed class FindingsLog : IDisposable
{
    private readonly object _gate = new();
    private readonly SortedDictionary<int, System.Text.Json.Nodes.JsonObject> _found = [];
    private readonly RoundPaths _rp;
    private readonly string _idPrefix;
    private readonly Redactor _redactor;
    private readonly Func<IEnumerable<string>, List<string>> _normalizeRefs;
    private readonly FileStream _fs;
    private bool _wroteThisAttempt;

    /// <param name="normalizeRefs">Makes refs repo-relative, so the author is never pointed into the snapshot.</param>
    /// <param name="idPrefix">"R{k}" in a review round, "E" in an exploration.</param>
    public FindingsLog(RoundPaths rp, string idPrefix, Redactor redactor, Func<IEnumerable<string>, List<string>> normalizeRefs)
    {
        _rp = rp;
        _idPrefix = idPrefix;
        _redactor = redactor;
        _normalizeRefs = normalizeRefs;
        Directory.CreateDirectory(rp.Dir);
        _fs = new FileStream(rp.Findings, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    public int Count { get { lock (_gate) return _found.Count; } }

    /// <summary>The highest number reported so far in this attempt (0: none).</summary>
    public int MaxNumber { get { lock (_gate) return _found.Count == 0 ? 0 : _found.Keys.Max(); } }

    /// <summary>A reviewer call starts: earlier reports no longer count (a restart line marks that, if there were any).</summary>
    public void BeginAttempt()
    {
        lock (_gate)
        {
            _found.Clear();
            if (_wroteThisAttempt) WriteLine(new() { ["restart"] = true, ["t"] = DateTimeOffset.Now.ToString("O") });
            _wroteThisAttempt = false;
        }
    }

    public void Add(StreamedFinding f)
    {
        lock (_gate)
        {
            var finding = (System.Text.Json.Nodes.JsonObject)Redact(f.Finding);
            if (finding["refs"] is System.Text.Json.Nodes.JsonArray refs)
                finding["refs"] = new System.Text.Json.Nodes.JsonArray([.. _normalizeRefs(refs.Select(r => r?.ToString() ?? "")).Select(r => (System.Text.Json.Nodes.JsonNode?)r)]);
            _found[f.N] = finding;
            var line = new System.Text.Json.Nodes.JsonObject { ["id"] = $"{_idPrefix}.{f.N}" };
            foreach (var (key, val) in finding) if (key is not ("id" or "t" or "restart")) line[key] = val?.DeepClone();
            line["t"] = DateTimeOffset.Now.ToString("O");
            WriteLine(line);
            _wroteThisAttempt = true;
        }
    }

    private void WriteLine(System.Text.Json.Nodes.JsonObject line)
    {
        _fs.Write(RunFiles.Utf8.GetBytes(line.ToJsonString() + "\n"));
        _fs.Flush();
    }

    private System.Text.Json.Nodes.JsonNode Redact(System.Text.Json.Nodes.JsonNode n) => n switch
    {
        System.Text.Json.Nodes.JsonObject o => new System.Text.Json.Nodes.JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, kv.Value is null ? null : Redact(kv.Value)))),
        System.Text.Json.Nodes.JsonArray a => new System.Text.Json.Nodes.JsonArray([.. a.Select(x => x is null ? null : Redact(x))]),
        System.Text.Json.Nodes.JsonValue v when v.TryGetValue<string>(out var s) => System.Text.Json.Nodes.JsonValue.Create(_redactor.Apply(s)),
        _ => n.DeepClone(),
    };

    /// <summary>The reported findings, in id order, as the reviewer's {"findings": [...]} output.</summary>
    public System.Text.Json.Nodes.JsonObject AsReviewJson()
    {
        lock (_gate)
            return new() { ["findings"] = new System.Text.Json.Nodes.JsonArray([.. _found.Values.Select(v => (System.Text.Json.Nodes.JsonNode?)v.DeepClone())]) };
    }

    public void Dispose()
    {
        lock (_gate) _fs.Dispose();
    }
}

/// <summary>reviewer.jsonl writer: one open handle per round, redacted, flushed per line so the GUI can tail it.</summary>
internal sealed class RawLog : IDisposable
{
    private readonly Redactor _r;
    private readonly FileStream _fs;
    private readonly object _gate = new();

    public RawLog(string path, Redactor r)
    {
        _r = r;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    public void Write(string line)
    {
        var bytes = RunFiles.Utf8.GetBytes(_r.Apply(line) + "\n");
        lock (_gate)
        {
            _fs.Write(bytes);
            _fs.Flush();
        }
    }

    public void Dispose() => _fs.Dispose();
}
