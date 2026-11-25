using System.Diagnostics;
using System.Text.RegularExpressions;
using Bobar.Core.IO;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;
using Bobar.Core.Runs;
using Bobar.Core.State;
using static Bobar.Core.Loop.CommandResult;

namespace Bobar.Core.Loop;

/// <summary>Discussions (§8b): several models answer a question, then revise over a fixed number of rounds; the author reports.</summary>
public sealed partial class ReviewEngine
{
    public const int DefaultDiscussRounds = 4;
    public const int MinDiscussRounds = 2;
    public const int MaxDiscussRounds = 10;

    private static readonly Regex SlugRe = new("^[a-z0-9-]{1,40}$");
    private static readonly Regex ShorthandRe = new("^[A-Za-z0-9_-]+$");
    private static readonly Regex ReservedRe = new("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase);

    /// <summary>Why <paramref name="models"/> can't be a discussion's participants, or null. Shorthands become dir names (round-k/&lt;shorthand&gt;).</summary>
    internal static string? CheckDiscussers(IReadOnlyList<string> models)
    {
        if (models.Count < 2) return "A discussion needs at least two participants (--models a,b).";
        foreach (var m in models)
        {
            if (!ShorthandRe.IsMatch(m)) return $"'{m}' can't be a participant: shorthands in a discussion may contain only letters, digits, '_' and '-'.";
            if (ReservedRe.IsMatch(m)) return $"'{m}' can't be a participant: it is a reserved file name on Windows.";
        }
        var dup = models.GroupBy(m => m, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return dup is null ? null : $"'{dup.Key}' is listed more than once (shorthands are compared ignoring case).";
    }

    // ───────────────────────────── start ─────────────────────────────

    /// <param name="dir">The folder the question is about (the discussers' cwd, with read-only file tools), or null for none.</param>
    public async Task<CommandResult> DiscussStartAsync(IReadOnlyList<string> models, string question, string slug, string? dir, int rounds, CancellationToken ct)
    {
        AppConfig config;
        try { config = ConfigStore.Load(Paths); }
        catch (ConfigException ex) { return Fail(ex.Message); }
        var keys = KeysEnv.Load(Paths.KeysFile);
        var redactor = keys.Redactor();

        if (string.IsNullOrWhiteSpace(question)) return Fail("The question (--prompt) is empty.");
        if (!SlugRe.IsMatch(slug)) return Fail($"--slug '{slug}' must be 1 to 40 characters of a-z, 0-9 and '-'.");
        if (rounds is < MinDiscussRounds or > MaxDiscussRounds) return Fail($"--rounds must be from {MinDiscussRounds} to {MaxDiscussRounds} (got {rounds}).");
        if (CheckDiscussers(models) is { } bad) return Fail(bad);
        var discussers = new List<Discusser>();
        foreach (var m in models)
        {
            if (!TryReviewerConfig(config, m, out var rcfg))
                return Fail($"Unknown model '{m}'. Configured: {string.Join(", ", config.Reviewers.Keys)} (edit {Paths.ConfigFile}).");
            discussers.Add(new Discusser { Shorthand = m, Config = rcfg });
        }
        string? folder = null;
        if (dir is not null)
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            if (!Directory.Exists(folder)) return Fail($"--dir {folder} does not exist.");
        }

        var run = NewRun(slug);
        using var lk = RunLock.TryAcquire(run) ?? throw new IOException($"could not lock the new run {run.RunId}");
        var ctx = new Ctx(this, run, config, keys, redactor);
        RunFiles.WriteText(run.Prompt, question.Trim() + "\n", redactor);
        if (folder is null) Directory.CreateDirectory(run.Cwd);
        ctx.Append(new RunStarted
        {
            T = Now,
            Repo = folder ?? "",
            Reviewer = string.Join(",", models),
            MaxRounds = rounds,
            Kind = RunKinds.Discuss,
            Name = slug,
            Discussers = discussers,
        });
        using var watch = new CancelWatch(run, ct, o.CancelPollInterval);
        return await Guarded(ctx, 1, () => DiscussRoundsAsync(ctx, watch.Token), watch.Token);
    }

    private sealed record TurnOutcome(string Discusser, DiscussTurnFile? Turn, string? SessionId);

    /// <summary>All rounds: each live discusser in parallel, a barrier between rounds. Ends early with fewer than two left.</summary>
    private async Task<CommandResult> DiscussRoundsAsync(Ctx ctx, CancellationToken ct)
    {
        var started = ctx.State.Started!;
        var participants = started.Discussers.Select(d => d.Shorthand).ToList();
        var total = started.MaxRounds;
        var live = participants.ToList();
        var latest = new Dictionary<string, (int Round, DiscussTurnFile Turn)>();
        var sessions = new Dictionary<string, string?>();
        var dropped = new Dictionary<string, int>();
        List<TurnOutcome> previous = [];
        var k = 1;
        for (; k <= total; k++)
        {
            ctx.Append(new DiscussRoundStarted { T = Now, Round = k, Live = live.ToList() });
            // Every message sent in round k-1 (all participants see all of them); its recipient may be gone.
            var messages = previous.Where(p => p.Turn is not null)
                .SelectMany(p => p.Turn!.Messages.Select(m => new DiscussPrompts.Message(p.Discusser, m.Key, m.Value, live.Contains(m.Key) && m.Key != p.Discusser)))
                .ToList();
            var snapshot = latest.ToDictionary(kv => kv.Key, kv => kv.Value);
            var droppedNow = dropped.ToDictionary(kv => kv.Key, kv => kv.Value);
            var round = k;
            var tasks = live.Select(sh =>
            {
                var resume = sessions.GetValueOrDefault(sh);
                return Task.Run(() => DiscussTurnAsync(ctx, round, sh, participants, snapshot, droppedNow, messages, resume, ct), CancellationToken.None);
            }).ToList();
            var results = await Task.WhenAll(tasks);
            ct.ThrowIfCancellationRequested();
            foreach (var r in results)
            {
                if (r.Turn is null) dropped[r.Discusser] = k;
                else
                {
                    latest[r.Discusser] = (k, r.Turn);
                    sessions[r.Discusser] = r.SessionId;
                }
            }
            live = live.Where(sh => !dropped.ContainsKey(sh)).ToList();
            previous = results.ToList();
            if (live.Count < 2) break;
        }
        var last = Math.Min(k, total);
        if (latest.Count == 0)
        {
            ctx.Append(new RunFinished { T = Now, Outcome = Outcome.Error, Message = "no participant produced a solution" });
            return CliOutput.Finished(ctx.State);
        }
        ctx.Append(new DiscussionEnded { T = Now, Round = last, Live = live, Early = last < total });
        return CliOutput.Discussed(ctx.State, ctx.Run);
    }

    /// <summary>One discusser's turn in round k, with the retry, a fresh session after a failed resume, and one repair turn.
    /// A failure it can't recover from drops it out (null turn); a cancel propagates.</summary>
    private async Task<TurnOutcome> DiscussTurnAsync(Ctx ctx, int k, string sh, IReadOnlyList<string> participants,
        IReadOnlyDictionary<string, (int Round, DiscussTurnFile Turn)> latest, IReadOnlyDictionary<string, int> dropped,
        IReadOnlyList<DiscussPrompts.Message> messages, string? resumeId, CancellationToken ct)
    {
        var run = ctx.Run;
        var started = ctx.State.Started!;
        var cfg = started.Discussers.First(d => d.Shorthand == sh).Config;
        var dp = run.Round(k).Discusser(sh);
        Directory.CreateDirectory(dp.Dir);
        var folder = started.Repo.Length == 0 ? null : started.Repo;
        var setup = new DiscussPrompts.Setup(sh, participants, started.MaxRounds, RunFiles.ReadShared(run.Prompt) ?? "", folder, cfg.Kind == ReviewerKinds.Claude);
        var withMessages = k >= 2 && k < started.MaxRounds;
        var others = participants.Where(p => p != sh && latest.ContainsKey(p))
            .Select(p => new DiscussPrompts.Solution(p, latest[p].Round, latest[p].Turn.Solution, dropped.TryGetValue(p, out var d) ? d : null))
            .ToList();
        var mine = latest.TryGetValue(sh, out var m) ? new DiscussPrompts.Solution(sh, m.Round, m.Turn.Solution) : null;
        string FreshPrompt() => k == 1 ? DiscussPrompts.Round1(setup) : DiscussPrompts.Fresh(setup, k, mine, others, messages);

        var prompt = resumeId is null ? FreshPrompt() : DiscussPrompts.Resume(setup, k, others, messages);
        RunFiles.WriteText(dp.Prompt, prompt, ctx.Redactor);
        using var rawLog = new RawLog(dp.ReviewerLog, ctx.Redactor);
        var call = new ReviewerCall
        {
            Round = k,
            Prompt = prompt,
            RepoDir = folder ?? run.Cwd,
            RunDir = run.Dir,
            RoundDir = dp.Dir,
            RawLog = rawLog.Write,
            ResumeSessionId = resumeId,
            FileTools = folder is not null,
            JsonSchema = Validation.DiscussSchema(withMessages),
        };
        var timeout = TimeSpan.FromMinutes(ctx.Config.ReviewerTimeoutMinutes);
        var activity = new SyncProgress();
        try
        {
            var reviewer = o.ReviewerFactory(ReviewerContextFor(sh, cfg, ctx.Config, ctx.Keys));
            var usage = new TokenUsage();
            var searches = 0;
            var duration = TimeSpan.Zero;
            var attempt = Stopwatch.StartNew();
            ReviewerResult res;
            try
            {
                res = await CallWithRetry(ctx, k, t => reviewer.RunAsync(call, activity, t), timeout, ct, sh);
            }
            catch (ReviewerException ex) when (call.ResumeSessionId is not null && !ex.TimedOut && !ex.Final)
            {
                usage += ex.Usage;
                searches += ex.WebSearches;
                duration += attempt.Elapsed;
                ctx.Append(new DiscusserRetry { T = Now, Round = k, Discusser = sh, Reason = ex.Message, Fresh = true });
                call = call with { Prompt = FreshPrompt(), ResumeSessionId = null };
                RunFiles.WriteText(dp.Prompt, call.Prompt, ctx.Redactor);
                res = await CallWithRetry(ctx, k, t => reviewer.RunAsync(call, activity, t), timeout, ct, sh);
            }
            usage += res.Usage;
            searches += res.WebSearches;
            duration += res.Duration;
            var v = Validation.DiscussTurn(res.Parsed, withMessages);
            var repaired = false;
            if (!v.Ok)
            {
                ctx.Append(new DiscusserRepair { T = Now, Round = k, Discusser = sh, Error = v.ErrorText });
                var prev = res;
                res = await CallWithRetry(ctx, k, t => reviewer.RepairAsync(prev, DiscussPrompts.Repair(v.ErrorText, withMessages), activity, t), timeout, ct, sh);
                usage += res.Usage;
                searches += res.WebSearches;
                duration += res.Duration;
                v = Validation.DiscussTurn(res.Parsed, withMessages);
                repaired = true;
                if (!v.Ok) throw new RunErrorException($"its output was still invalid after a repair turn:\n{v.ErrorText}");
            }

            var turn = v.Value!;
            RunFiles.WriteJson(dp.Output, turn, BobarJson.Indented.DiscussTurnFile, ctx.Redactor);
            RunFiles.WriteText(dp.Solution, Rendering.DiscussTurn(sh, k, turn), ctx.Redactor);
            var models = res.ResolvedModels.Count > 0 ? res.ResolvedModels : [res.RequestedModel];
            ctx.Append(new DiscusserReplied
            {
                T = Now,
                Round = k,
                Discusser = sh,
                DurationMs = (long)duration.TotalMilliseconds,
                Usage = usage,
                CostUsd = Pricing.Cost(cfg, models, usage, ctx.Config.Prices, searches, ctx.Config.WebSearch.PricePerCall),
                RequestedModel = string.IsNullOrEmpty(res.RequestedModel) ? cfg.Model : res.RequestedModel,
                ResolvedModels = res.ResolvedModels,
                RequestedReasoning = res.RequestedReasoning ?? cfg.Reasoning,
                EffectiveReasoning = res.EffectiveReasoning,
                Repaired = repaired,
                SessionId = res.SessionId,
                WebSearches = searches,
                SolutionChars = turn.Solution.Length,
                MessagesTo = turn.Messages.Keys.ToList(),
            });
            return new TurnOutcome(sh, turn, res.SessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            ctx.Append(new DiscusserDropped { T = Now, Round = k, Discusser = sh, Reason = ctx.Redactor.Apply(ex.Message) });
            return new TurnOutcome(sh, null, null);
        }
    }

    // ───────────────────────────── ask / answers ─────────────────────────────

    /// <summary>After the rounds: the disagreements the author can't settle, for the user to answer in the GUI. Once per run.</summary>
    public CommandResult DiscussAsk(string runRef, string text)
    {
        var run = RunReader.Resolve(Paths, runRef, out var err);
        if (run is null) return Fail(err!);
        AppConfig config;
        try { config = ConfigStore.Load(Paths); }
        catch (ConfigException ex) { return Fail(ex.Message, run.RunId); }
        using var lk = RunLock.TryAcquire(run);
        if (lk is null) return CliOutput.Busy(run, RunLock.ReadPid(run));
        var s = RunReader.LoadState(run);
        if (!s.IsDiscuss) return Fail($"{run.RunId} is not a discussion.", run.RunId);
        if (s.Questions is not null) return Fail($"Questions were already asked in this discussion (once per run). {(s.Answers is null ? $"Wait for the answers with `{CliOutput.Cli} review wait {run.RunId}`." : $"They are answered: `{CliOutput.Cli} review show {run.RunId}` prints them.")}", run.RunId);
        if (s.Phase != RunPhase.AwaitingReport) return Fail($"There is nothing to ask about now. The run's current state:\n\n{Show(run).Render()}", run.RunId);
        var v = Validation.Questions(text);
        var redactor = KeysEnv.Load(Paths.KeysFile).Redactor();
        if (!v.Ok) return CliOutput.InvalidQuestions(s, redactor.Apply(v.ErrorText));
        RunFiles.WriteJson(run.Questions, v.Value!, BobarJson.Indented.QuestionsFile, redactor);
        EventLog.Append(run, new QuestionsAsked { T = Now, Count = v.Value!.Questions.Count }, redactor);
        var opened = config.OpenGuiOnDecisions && TryLaunchGui(run);
        return CliOutput.AwaitingAnswers(RunReader.LoadState(run), opened);
    }

    /// <summary>`review wait` on a discussion: blocks until the user's answers exist (or the run ends).</summary>
    private async Task<CommandResult> WaitAnswersAsync(RunPaths run, CancellationToken ct)
    {
        while (true)
        {
            var s = RunReader.LoadState(run);
            if (s.Finished is not null) return CliOutput.Finished(s);
            if (s.Answers is not null && LoadAnswered(run) is { } done) return CliOutput.Answered(s, done.Questions, done.Answers);
            if (s.Phase != RunPhase.AwaitingAnswers)
            {
                var shown = Show(run);
                return shown with { Body = $"The discussion is not awaiting the user's answers, so there is nothing to wait for.\n\n{shown.Body}", ExitCode = 1 };
            }
            if (File.Exists(run.Answers))
            {
                var questions = RunFiles.ReadJson(run.Questions, BobarJson.Compact.QuestionsFile);
                if (questions is null) return Fail("questions.json is missing or unreadable", run.RunId);
                var v = Validation.Answers(RunFiles.ReadJson(run.Answers, BobarJson.Compact.AnswersFile), questions);
                if (!v.Ok) return Fail($"answers.json is invalid:\n{v.ErrorText}", run.RunId);
                var redactor = KeysEnv.Load(Paths.KeysFile).Redactor();
                using (var lk = await RunLock.AcquireAsync(run, RunActions.LockPatience, ct))
                {
                    if (lk is null) return CliOutput.Busy(run, RunLock.ReadPid(run));
                    var locked = RunReader.LoadState(run);
                    if (locked.Finished is not null) return CliOutput.Finished(locked);
                    if (locked.Answers is null)
                        EventLog.Append(run, new AnswersSubmitted { T = Now, Count = v.Value!.Answers.Count }, redactor);
                }
                return CliOutput.Answered(RunReader.LoadState(run), questions, v.Value!);
            }
            await Task.Delay(o.WaitPollInterval, ct);
        }
    }

    private static (QuestionsFile Questions, AnswersFile Answers)? LoadAnswered(RunPaths run)
        => RunFiles.ReadJson(run.Questions, BobarJson.Compact.QuestionsFile) is { } q && RunFiles.ReadJson(run.Answers, BobarJson.Compact.AnswersFile) is { } a ? (q, a) : null;

    // ───────────────────────────── report ─────────────────────────────

    /// <summary>The author's final report (Markdown). Ends the discussion as reported.</summary>
    public CommandResult DiscussReport(string runRef, string markdown)
    {
        var run = RunReader.Resolve(Paths, runRef, out var err);
        if (run is null) return Fail(err!);
        using var lk = RunLock.TryAcquire(run);
        if (lk is null) return CliOutput.Busy(run, RunLock.ReadPid(run));
        var s = RunReader.LoadState(run);
        if (!s.IsDiscuss) return Fail($"{run.RunId} is not a discussion.", run.RunId);
        if (s.Phase == RunPhase.AwaitingAnswers)
            return Fail($"The user hasn't answered your questions yet: run `{CliOutput.Cli} review wait {run.RunId}` in the background and write the report once it returns.", run.RunId);
        if (s.Phase != RunPhase.AwaitingReport) return Fail($"The discussion isn't waiting for a report. Its current state:\n\n{Show(run).Render()}", run.RunId);
        if (string.IsNullOrWhiteSpace(markdown)) return Fail("The report is empty.", run.RunId);
        var redactor = KeysEnv.Load(Paths.KeysFile).Redactor();
        RunFiles.WriteJson(run.Report, new ReportFile { Markdown = markdown.Trim() }, BobarJson.Indented.ReportFile, redactor);
        EventLog.Append(run, new RunFinished { T = Now, Outcome = Outcome.Reported, Message = "the author sent its report" }, redactor);
        return CliOutput.Finished(RunReader.LoadState(run));
    }

    /// <summary>A discussion's hindsight: one 1–10 score, why, and what BobarCode should change.</summary>
    private CommandResult DiscussHindsight(RunPaths run, string text)
    {
        var s = RunReader.LoadState(run);
        var redactor = KeysEnv.Load(Paths.KeysFile).Redactor();
        var v = Validation.DiscussHindsight(text);
        if (!v.Ok) return CliOutput.InvalidHindsight(s, redactor.Apply(v.ErrorText));
        var h = v.Value!;
        try
        {
            RunFiles.WriteJson(run.Hindsight, h, BobarJson.Indented.DiscussHindsightFile, redactor);
            EventLog.Append(run, new HindsightSubmitted { T = Now, Usefulness = h.Score }, redactor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(redactor.Apply($"Could not save the hindsight: {ex.Message}"), run.RunId);
        }
        return new(Statuses.HindsightSaved, run.RunId, null, $"Hindsight saved: {State.Hindsight.Rating(h.Score)}.\n\nTell the user your score in one line.\n");
    }
}
