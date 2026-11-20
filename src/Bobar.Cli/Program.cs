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
        var start = new Command("start", "Create a run and run review 1") { reviewerOpt, descOpt, fromOpt, repoOpt };
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
            var r = await engine.StartAsync(pr.GetValue(reviewerOpt)!, desc, pr.GetValue(repoOpt) ?? Environment.CurrentDirectory, ct, from);
            return Print(stdout, r);
        });

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

        var root = new RootCommand("BobarCode: a review loop between a reviewer model and your Claude Code session") { review, runs };
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
    /// Opens bobar.exe (next to bobar-cli.exe) on the run. ShellExecute, so the GUI doesn't inherit
    /// our stdout pipe (Claude's Bash would otherwise wait for the GUI to exit).
    /// </summary>
    private static void LaunchGui(RunPaths run)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "bobar.exe" : "bobar");
        if (!File.Exists(exe)) return;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
        psi.ArgumentList.Add("--run");
        psi.ArgumentList.Add(run.RunId);
        Process.Start(psi)?.Dispose();
    }
}
