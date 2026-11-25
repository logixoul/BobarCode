using System.CommandLine;
using System.Diagnostics;
using System.Text;
using Bobar.Core;
using Bobar.Core.IO;
using Bobar.Core.Loop;

namespace Bobar.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        var paths = BobarPaths.FromEnvironment();
        try { ConfigStore.EnsureExists(paths); }
        catch (IOException ex) { stderr.WriteLine($"(could not write the default config: {ex.Message})"); }
        var engine = new ReviewEngine(new EngineOptions
        {
            Paths = paths,
            Progress = stderr,
            LaunchGui = LaunchGui,
        });

        var runArg = new Argument<string>("run") { Description = "Run id (or a unique part of it)" };

        var reviewerOpt = new Option<string>("--reviewer") { Description = "Reviewer shorthand from config.json (e.g. ds, sol, opus; 'fake' for testing)", Required = true };
        var descOpt = new Option<string?>("--desc") { Description = "File with the change description, or - for stdin (required unless --from; with --from, optional extra notes)" };
        var fromOpt = new Option<string?>("--from") { Description = "Review the commits from this one (inclusive) to HEAD, plus anything uncommitted; e.g. HEAD~2 for the last 3. Their messages are the description." };
        var repoOpt = new Option<string?>("--repo") { Description = "Repository directory (default: current directory)" };
        var exploreRunOpt = new Option<string?>("--explore-run") { Description = "The explore run whose findings this change implements (shown in BobarCode)" };
        var start = new Command("start", "Create a run and run review 1") { reviewerOpt, descOpt, fromOpt, repoOpt, exploreRunOpt };
        start.SetAction(async (pr, ct) =>
        {
            var descArg = pr.GetValue(descOpt);
            var from = pr.GetValue(fromOpt);
            if (descArg is null && from is null) return Print(stdout, CommandResult.Fail("Pass --desc, --from, or both."));
            string? desc = null;
            if (descArg is not null)
            {
                desc = await TryReadInput(stdout, descArg);
                if (desc is null) return 1;
            }
            var r = await engine.StartAsync(pr.GetValue(reviewerOpt)!, desc, pr.GetValue(repoOpt) ?? Environment.CurrentDirectory, ct, from, pr.GetValue(exploreRunOpt));
            return Print(stdout, r);
        });

        var promptOpt = new Option<string>("--prompt") { Description = "File with the exploration task, or - for stdin", Required = true };
        var exploreStart = new Command("start", "Create an explore run: one reviewer call over the whole repo with your task") { reviewerOpt, promptOpt, repoOpt };
        exploreStart.SetAction(async (pr, ct) =>
        {
            if (await TryReadInput(stdout, pr.GetValue(promptOpt)!) is not { } task) return 1;
            return Print(stdout, await engine.ExploreStartAsync(pr.GetValue(reviewerOpt)!, task, pr.GetValue(repoOpt) ?? Environment.CurrentDirectory, ct));
        });
        var exploreResponseOpt = new Option<string>("--response") { Description = "File with the answer JSON (responses + chosen), or - for stdin", Required = true };
        var exploreRunArg = new Argument<string>("run") { Description = "Run id (or a unique part of it)" };
        var exploreFinish = new Command("finish", "Answer an exploration (accept/reject each finding, and the ones you chose) and end it") { exploreRunArg, exploreResponseOpt };
        exploreFinish.SetAction(async (pr, ct) =>
        {
            if (await TryReadInput(stdout, pr.GetValue(exploreResponseOpt)!) is not { } text) return 1;
            return Print(stdout, await engine.ExploreFinishAsync(pr.GetValue(exploreRunArg)!, text, ct));
        });
        var explore = new Command("explore", "One-shot explorations of the whole repo (stream, verdicts, cancel, show and hindsight are under `review`)") { exploreStart, exploreFinish };

        var modelsOpt = new Option<string>("--models") { Description = "Comma-separated shorthands from config.json, at least two (e.g. ds,opus)", Required = true };
        var questionOpt = new Option<string>("--prompt") { Description = "File with the question, or - for stdin", Required = true };
        var slugOpt = new Option<string>("--slug") { Description = "Short topic name for the run id: a-z, 0-9 and '-', at most 40 characters", Required = true };
        var dirOpt = new Option<string?>("--dir") { Description = "The folder the question is about (the participants' read-only cwd). Omit when there is none." };
        var roundsOpt = new Option<int>("--rounds") { Description = $"Number of rounds ({ReviewEngine.MinDiscussRounds}-{ReviewEngine.MaxDiscussRounds})", DefaultValueFactory = _ => ReviewEngine.DefaultDiscussRounds };
        var discussStart = new Command("start", "Run a whole discussion: each model answers, then all revise over the rounds") { modelsOpt, questionOpt, slugOpt, dirOpt, roundsOpt };
        discussStart.SetAction(async (pr, ct) =>
        {
            if (await TryReadInput(stdout, pr.GetValue(questionOpt)!) is not { } question) return 1;
            var models = pr.GetValue(modelsOpt)!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Print(stdout, await engine.DiscussStartAsync(models, question, pr.GetValue(slugOpt)!, pr.GetValue(dirOpt), pr.GetValue(roundsOpt), ct));
        });
        var discussRunArg = new Argument<string>("run") { Description = "Run id (or a unique part of it)" };
        var questionsOpt = new Option<string>("--questions") { Description = "File with the questions JSON, or - for stdin", Required = true };
        var discussAsk = new Command("ask", "Ask the user, in BobarCode, about the disagreements you can't settle (once per discussion)") { discussRunArg, questionsOpt };
        discussAsk.SetAction(async (pr, ct) =>
        {
            if (await TryReadInput(stdout, pr.GetValue(questionsOpt)!) is not { } text) return 1;
            return Print(stdout, engine.DiscussAsk(pr.GetValue(discussRunArg)!, text));
        });
        var reportOpt = new Option<string>("--input") { Description = "File with the report (Markdown), or - for stdin", Required = true };
        var discussReport = new Command("report", "Send your final report and end the discussion") { discussRunArg, reportOpt };
        discussReport.SetAction(async (pr, ct) =>
        {
            if (await TryReadInput(stdout, pr.GetValue(reportOpt)!) is not { } text) return 1;
            return Print(stdout, engine.DiscussReport(pr.GetValue(discussRunArg)!, text));
        });
        var discuss = new Command("discuss", "Discussions between several models (wait, cancel, show and hindsight are under `review`)") { discussStart, discussAsk, discussReport };

        var responseOpt = new Option<string>("--response") { Description = "File with the response JSON, or - for stdin", Required = true };
        var next = new Command("next", "Record the author's response and run the next review if there is one") { runArg, responseOpt };
        next.SetAction(async (pr, ct) =>
        {
            if (await TryReadInput(stdout, pr.GetValue(responseOpt)!) is not { } text) return 1;
            return Print(stdout, await engine.NextAsync(pr.GetValue(runArg)!, text, ct));
        });

        var wait = new Command("wait", "Block until the user's decisions exist (or the run is cancelled)") { runArg };
        wait.SetAction(async (pr, ct) => Print(stdout, await engine.WaitAsync(pr.GetValue(runArg)!, ct)));

        var streamRunArg = new Argument<string?>("run") { Description = "Run id (or a unique part of it); default: the run just started in --repo", Arity = ArgumentArity.ZeroOrOne };
        var roundOpt = new Option<int?>("--round") { Description = "Round to stream (default: 1 without a run, else the run's latest round)" };
        var streamRepoOpt = new Option<string?>("--repo") { Description = "Repository directory, to find the run when none is given (default: current directory)" };
        var stream = new Command("stream", "Print a round's findings as the reviewer reports them, then END (for the author's Monitor; read-only)") { streamRunArg, roundOpt, streamRepoOpt };
        stream.SetAction(async (pr, ct) => await engine.StreamAsync(pr.GetValue(streamRunArg), pr.GetValue(roundOpt), pr.GetValue(streamRepoOpt) ?? Environment.CurrentDirectory, stdout, ct));

        var cancel = new Command("cancel", "Ask the CLI working on the run to stop (killing it after 10 s) and end the run") { runArg };
        cancel.SetAction(async (pr, ct) => Print(stdout, await engine.CancelAsync(pr.GetValue(runArg)!)));

        var show = new Command("show", "Print the run's current state") { runArg };
        show.SetAction(pr => Print(stdout, engine.Show(pr.GetValue(runArg)!)));

        var inputOpt = new Option<string>("--input") { Description = "File with the hindsight JSON, or - for stdin", Required = true };
        var hindsight = new Command("hindsight", "After the run finished: record the author's look back on it (usefulness, regretted calls, notes)") { runArg, inputOpt };
        hindsight.SetAction(async (pr, ct) =>
        {
            if (await TryReadInput(stdout, pr.GetValue(inputOpt)!) is not { } text) return 1;
            return Print(stdout, engine.Hindsight(pr.GetValue(runArg)!, text));
        });

        var fixOpt = new Option<string[]>("--fix") { Description = "Finding ids you will fix (comma- or space-separated)", AllowMultipleArgumentsPerToken = true };
        var rejectOpt = new Option<string[]>("--reject") { Description = "Finding ids you reject (comma- or space-separated)", AllowMultipleArgumentsPerToken = true };
        var verdicts = new Command("verdicts", "Before applying fixes: declare which of the round's findings you'll fix and which you reject, for the GUI (lock-free; `next` still counts)") { runArg, fixOpt, rejectOpt };
        verdicts.SetAction(pr =>
        {
            static List<string> Ids(string[]? v) => (v ?? []).SelectMany(x => x.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
            return Print(stdout, engine.Verdicts(pr.GetValue(runArg)!, Ids(pr.GetValue(fixOpt)), Ids(pr.GetValue(rejectOpt))));
        });

        var review = new Command("review", "The review loop") { start, next, stream, verdicts, wait, cancel, show, hindsight };

        var runs = new Command("runs", "List recent runs");
        runs.SetAction(pr =>
        {
            stdout.Write(engine.Runs());
            return 0;
        });

        var root = new RootCommand("BobarCode: a review loop between a reviewer model and your Claude Code session") { review, explore, discuss, runs };
        try
        {
            return await root.Parse(args).InvokeAsync();
        }
        catch (ConfigException ex)
        {
            return Print(stdout, CommandResult.Fail(ex.Message));
        }
    }

    private static int Print(TextWriter w, CommandResult r)
    {
        w.Write(r.Render());
        return r.ExitCode;
    }

    /// <summary>ReadInput, but a missing or unreadable file prints a STATUS: error result and returns null.</summary>
    private static async Task<string?> TryReadInput(TextWriter w, string fileOrDash)
    {
        try { return await ReadInput(fileOrDash); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Print(w, CommandResult.Fail($"Could not read {fileOrDash}: {ex.Message}"));
            return null;
        }
    }

    private static async Task<string> ReadInput(string fileOrDash)
    {
        if (fileOrDash == "-")
        {
            using var r = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            return await r.ReadToEndAsync();
        }
        return await File.ReadAllTextAsync(fileOrDash);
    }

    /// <summary>
    /// Opens bobar.exe (next to bobar-cli.exe) on the run, so that the GUI doesn't inherit our stdout pipe
    /// (Claude's Bash would otherwise wait for the GUI to exit): ShellExecute on Windows; on macOS `open -n`
    /// on the app bundle we live in (a second instance hands the run to the first and exits), else the
    /// bare binary with its stdio redirected.
    /// </summary>
    private static void LaunchGui(RunPaths run)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "bobar.exe" : "bobar");
        if (!File.Exists(exe)) throw new FileNotFoundException($"{exe} does not exist");
        ProcessStartInfo psi;
        var bundle = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        if (OperatingSystem.IsWindows())
            psi = new ProcessStartInfo(exe) { UseShellExecute = true };
        else if (OperatingSystem.IsMacOS() && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add("-n");
            // launchd starts the app, so our environment doesn't reach it unless passed on.
            foreach (var v in new[] { BobarPaths.ConfigDirEnv, BobarPaths.DataDirEnv })
                if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } val)
                {
                    psi.ArgumentList.Add("--env");
                    psi.ArgumentList.Add($"{v}={val}");
                }
            foreach (var a in new[] { "-a", bundle, "--args" }) psi.ArgumentList.Add(a);
        }
        else
            psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--run");
        psi.ArgumentList.Add(run.RunId);
        using var p = Process.Start(psi);
        if (p is not null && psi.RedirectStandardInput) p.StandardInput.Close();
    }
}
