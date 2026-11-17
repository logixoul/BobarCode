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
        var engine = new ReviewEngine(new EngineOptions
        {
            Paths = paths,
            Progress = stderr,
            LaunchGui = LaunchGui,
        });

        var runArg = new Argument<string>("run") { Description = "Run id (or a unique part of it)" };

        var reviewerOpt = new Option<string>("--reviewer") { Description = "Reviewer shorthand from config.json (e.g. ds, sol, opus; 'fake' for testing)", Required = true };
        var descOpt = new Option<string>("--desc") { Description = "File with the change description, or - for stdin", Required = true };
        var repoOpt = new Option<string?>("--repo") { Description = "Repository directory (default: current directory)" };
        var start = new Command("start", "Create a run and run review 1") { reviewerOpt, descOpt, repoOpt };
        start.SetAction(async (pr, ct) =>
        {
            var desc = await ReadInput(pr.GetValue(descOpt)!);
            var r = await engine.StartAsync(pr.GetValue(reviewerOpt)!, desc, pr.GetValue(repoOpt) ?? Environment.CurrentDirectory, ct);
            return Print(stdout, r);
        });

        var responseOpt = new Option<string>("--response") { Description = "File with the response JSON, or - for stdin", Required = true };
        var next = new Command("next", "Record the author's response and run the next review if there is one") { runArg, responseOpt };
        next.SetAction(async (pr, ct) =>
        {
            var text = await ReadInput(pr.GetValue(responseOpt)!);
            return Print(stdout, await engine.NextAsync(pr.GetValue(runArg)!, text, ct));
        });

        var wait = new Command("wait", "Block until the user's decisions exist (or the run is cancelled)") { runArg };
        wait.SetAction(async (pr, ct) => Print(stdout, await engine.WaitAsync(pr.GetValue(runArg)!, ct)));

        var cancel = new Command("cancel", "Kill the run's active process and end the run") { runArg };
        cancel.SetAction(async (pr, ct) => Print(stdout, await engine.CancelAsync(pr.GetValue(runArg)!)));

        var show = new Command("show", "Print the run's current state") { runArg };
        show.SetAction(pr => Print(stdout, engine.Show(pr.GetValue(runArg)!)));

        var review = new Command("review", "The review loop") { start, next, wait, cancel, show };

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
