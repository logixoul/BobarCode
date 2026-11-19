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
}

/// <summary>What a reviewer factory gets.</summary>
public sealed record ReviewerContext(string Shorthand, ReviewerConfig Config, KeysEnv Keys, BobarPaths Paths);

/// <summary>Thrown inside a command to end the run as an error.</summary>
internal sealed class RunErrorException(string message) : Exception(message);

/// <summary>The CLI's commands (§8), as a library. The loop's end is decided in code (§5).</summary>
public sealed class ReviewEngine(EngineOptions options)
{
    public const string FakeShorthand = "fake";
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly EngineOptions o = options;
    private BobarPaths Paths => o.Paths;

    // ───────────────────────────── start ─────────────────────────────

    /// <param name="from">Review the commits from..HEAD (inclusive) plus anything uncommitted; their messages
    /// become the description and <paramref name="description"/> is optional extra notes. Null: uncommitted only.</param>
    public async Task<CommandResult> StartAsync(string reviewer, string? description, string repoDir, CancellationToken ct, string? from = null)
    {
        AppConfig config;
        try { config = ConfigStore.Load(Paths); }
        catch (ConfigException ex) { return Fail(ex.Message); }
        var keys = KeysEnv.Load(Paths.KeysFile);
        var redactor = keys.Redactor();

        if (!TryReviewerConfig(config, reviewer, out var rcfg))
            return Fail($"Unknown reviewer '{reviewer}'. Configured: {string.Join(", ", config.Reviewers.Keys)} (edit {Paths.ConfigFile}).");
        if (from is null && string.IsNullOrWhiteSpace(description)) return Fail("The description (--desc) is empty.");

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

        var run = Paths.Run(NewRunId(repo));
        Directory.CreateDirectory(run.Dir);
        using var lk = RunLock.TryAcquire(run) ?? throw new IOException($"could not lock the new run {run.RunId}");
        var ctx = new Ctx(this, run, config, keys, redactor);
        RunFiles.WriteText(run.Desc, description!.Trim() + "\n", redactor);
        ctx.Append(new RunStarted { T = Now, Repo = repo, Base = baseRev, Seed = seed, Commits = commits, Reviewer = reviewer, ReviewerConfig = rcfg, MaxRounds = config.MaxRounds });
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
            RunFiles.WriteJson(run.Round(k).Response, response, BobarJson.Indented.ResponseFile, ctx.Redactor);
            var sevById = review.Findings.ToDictionary(f => f.Id, f => f.Severity);
            ctx.Append(new AuthorResponded
            {
                T = Now,
                Round = k,
                Fix = SeverityCounts.Of(response.Responses.Where(x => x.Action == AuthorAction.Fix).Select(x => sevById[x.Id])),
                Rejected = response.Responses.Count(x => x.Action == AuthorAction.Reject),
            });

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
            var why = s.Finished is null ? "the run hasn't finished yet" : "the run was cancelled";
            return Fail($"No hindsight now: {why}. The run's current state:\n\n{Show(run).Render()}", run.RunId);
        }

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
        switch (s.Phase)
        {
            case RunPhase.Finished:
                if (s.Finished!.Outcome == Outcome.Decided && RunReader.LoadDecisions(run) is { } d)
                    return CliOutput.Decided(s, RunReader.LoadRounds(run, s), d);
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
            sb.Append($"| {r.RunId} | {RunStatus.Of(s, RunLock.HeldByLiveCli(r), o.Time.GetLocalNow()).Label()} | {s.Rounds.Count} | {s.Started?.Reviewer} | {h} |\n");
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

        var rejectedLast = k >= 2
            ? earlier[^1].Response?.Responses.Where(x => x.Action == AuthorAction.Reject).Select(x => x.Id).ToList() ?? []
            : [];
        var claudeKind = started.ReviewerConfig.Kind == ReviewerKinds.Claude;
        string FreshPrompt() => PromptBuilder.Build(new PromptBuilder.Inputs(k, started.Repo, RunFiles.ReadShared(run.Desc) ?? "", rp.Diff, deltaPath, historyPath, rejectedLast,
            claudeKind, started.Commits.Count > 0, ctx.Config.ReviewerInstructions));
        // Round k ≥ 2 continues the reviewer's previous session, so it doesn't explore the repo again.
        var resumeId = k >= 2 ? ctx.State.Rounds.FirstOrDefault(r => r.Number == k - 1)?.Review?.SessionId : null;
        var prompt = resumeId is null ? FreshPrompt() : PromptBuilder.Resume(k, rp.Diff, deltaPath!, historyPath!, earlier[^1], rejectedLast, claudeKind, ctx.Config.ReviewerInstructions);
        RunFiles.WriteText(rp.Prompt, prompt, ctx.Redactor);

        var earlierIds = earlier.Where(r => r.Review is not null).SelectMany(r => r.Review!.Findings.Select(f => f.Id)).ToHashSet();
        using var rawLog = new RawLog(rp.ReviewerLog, ctx.Redactor);
        var call = new ReviewerCall
        {
            Round = k,
            Prompt = prompt,
            RepoDir = started.Repo,
            RunDir = run.Dir,
            RoundDir = rp.Dir,
            RawLog = rawLog.Write,
            PriorRounds = earlier,
            DiffText = diff,
            ResumeSessionId = resumeId,
        };
        var reviewer = o.ReviewerFactory(new ReviewerContext(started.Reviewer, started.ReviewerConfig, ctx.Keys, Paths));
        var activity = new SyncProgress();
        var timeout = TimeSpan.FromMinutes(ctx.Config.ReviewerTimeoutMinutes);
        var usage = new TokenUsage();
        var sw = Stopwatch.StartNew();
        var duration = TimeSpan.Zero;

        ReviewerResult res;
        var attempt = Stopwatch.StartNew();
        try
        {
            res = await CallWithRetry(ctx, k, c => reviewer.RunAsync(call, activity, c), timeout, ct);
        }
        catch (ReviewerException ex) when (call.ResumeSessionId is not null && !ex.TimedOut && !ex.Final)
        {
            // E.g. the session file is gone. Redo the round in a fresh session with the full prompt.
            // Quota and already-retried failures are Final; other errors aren't matched by vendor text.
            usage += ex.Usage;
            duration += attempt.Elapsed;
            ctx.Append(new ReviewerRetry { T = Now, Round = k, Reason = ex.Message, Fresh = true });
            call = call with { Prompt = FreshPrompt(), ResumeSessionId = null };
            RunFiles.WriteText(rp.Prompt, call.Prompt, ctx.Redactor);
            res = await CallWithRetry(ctx, k, c => reviewer.RunAsync(call, activity, c), timeout, ct);
        }
        usage += res.Usage;
        duration += res.Duration;
        var v = Validation.Review(res.Parsed, earlierIds);
        var repaired = false;
        if (!v.Ok)
        {
            ctx.Append(new ReviewerRepair { T = Now, Round = k, Error = v.ErrorText });
            var repairPrompt = PromptBuilder.Repair(v.ErrorText);
            var prev = res;
            res = await CallWithRetry(ctx, k, c => reviewer.RepairAsync(prev, repairPrompt, activity, c), timeout, ct);
            usage += res.Usage;
            duration += res.Duration;
            v = Validation.Review(res.Parsed, earlierIds);
            repaired = true;
            if (!v.Ok) throw new RunErrorException($"the reviewer's output was still invalid after a repair turn:\n{v.ErrorText}");
        }

        var raw = v.Value!;
        var (origins, withdrawn) = LoopRules.Classify(raw.Select(f => f.PreviousId).ToList(), rejectedLast);
        var review = new ReviewFile
        {
            Round = k,
            Withdrawn = withdrawn,
            Findings = raw.Select((f, i) =>
            {
                var (refs, warnings) = NormalizeRefs(f.Refs, started.Repo);
                return new StoredFinding
                {
                    Id = $"R{k}.{i + 1}",
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
            CostUsd = Pricing.Cost(started.ReviewerConfig, models, usage, ctx.Config.Prices),
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

    /// <summary>One reviewer call with the timeout and the single transient retry (§9).</summary>
    private async Task<ReviewerResult> CallWithRetry(Ctx ctx, int k, Func<CancellationToken, Task<ReviewerResult>> call, TimeSpan timeout, CancellationToken ct)
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
            ctx.Append(new ReviewerRetry { T = Now, Round = k, Reason = ex.Message, DelaySeconds = (int)Math.Ceiling(delay.TotalSeconds) });
            await o.Delay(delay, ct);
            try
            {
                return await WithTimeout(call, timeout, ct);
            }
            catch (ReviewerException ex2)
            {
                throw new ReviewerException($"the reviewer failed twice; last error: {ex2.Message}", false, null, ex2.TimedOut, ex2.Usage, final: true);
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

    private string NewRunId(string repo)
    {
        var name = Regex.Replace(Path.GetFileName(repo.TrimEnd('\\', '/')), @"[^A-Za-z0-9._-]", "_");
        if (name.Length == 0) name = "repo";
        return $"{Now:yyyyMMdd-HHmmss}-{name}-{Random.Shared.Next(0x10000):x4}";
    }

    private void TryLaunchGui(RunPaths run)
    {
        try { o.LaunchGui?.Invoke(run); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            o.Progress.WriteLine($"(could not open BobarCode: {ex.Message})");
        }
    }

    /// <summary>Repo-relative, forward slashes; warns (never fails) when a path doesn't exist.</summary>
    internal static (List<string> Refs, List<string> Warnings) NormalizeRefs(IEnumerable<string> refs, string repo)
    {
        var outRefs = new List<string>();
        var warnings = new List<string>();
        var repoFull = Path.GetFullPath(repo).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
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
                    if (full.StartsWith(repoFull, StringComparison.OrdinalIgnoreCase)) path = full[repoFull.Length..];
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
                var full = Path.Combine(repo, path);
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

        public void Append(RunEvent ev)
        {
            EventLog.Append(Run, ev, Redactor);
            State = RunReader.LoadState(Run);
            var lines = Summary.Lines(State);
            for (; _printed < lines.Count; _printed++) _e.o.Progress.WriteLine(lines[_printed].ToString());
            _e.o.Progress.Flush();
        }
    }

    private sealed class SyncProgress : IProgress<ReviewerActivity>
    {
        public void Report(ReviewerActivity value) { }
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
