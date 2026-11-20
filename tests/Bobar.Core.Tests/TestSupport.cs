using System.Text.Json;
using Bobar.Core;
using Bobar.Core.Git;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;

namespace Bobar.Core.Tests;

/// <summary>A throwaway git repo with one commit.</summary>
public sealed class TempRepo : IDisposable
{
    public string Dir { get; }

    public TempRepo()
    {
        Dir = Path.Combine(Path.GetTempPath(), "bobar-test-repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Dir);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "core.autocrlf", "false");
        Write("src/app.txt", "line 1\nline 2\n");
        Write(".gitignore", "*.log\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "initial");
    }

    public void Write(string rel, string text)
    {
        var p = Path.Combine(Dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    public string Git(params string[] args) => GitCli.RunAsync(Dir, args, CancellationToken.None).GetAwaiter().GetResult();

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Temp config/data dirs, a repo with a change, and an engine wired to a FakeReviewer.</summary>
public sealed class TestEnv : IDisposable
{
    public TempRepo Repo { get; } = new();
    public string Root { get; }
    public BobarPaths Paths { get; }
    public FakeReviewer Fake { get; private set; }
    public List<TimeSpan> Delays { get; } = [];
    public List<string> GuiLaunches { get; } = [];
    public StringWriter Progress { get; } = new();
    public ReviewEngine Engine { get; private set; }

    public TestEnv(Dictionary<int, IEnumerable<FakeStep>>? script = null, int maxRounds = 5, double timeoutMinutes = 30, string? keys = null, string instructions = "", bool streams = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "bobar-test-home-" + Guid.NewGuid().ToString("N")[..8]);
        Paths = new BobarPaths(Path.Combine(Root, "config"), Path.Combine(Root, "data"));
        var cfg = AppConfig.CreateDefault();
        cfg.MaxRounds = maxRounds;
        cfg.ReviewerTimeoutMinutes = timeoutMinutes;
        cfg.ReviewerInstructions = instructions;
        cfg.Prices["fake/fake"] = new Price { Input = 1, Output = 1 };
        Directory.CreateDirectory(Paths.ConfigDir);
        File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(cfg, BobarJson.Indented.AppConfig));
        if (keys is not null) File.WriteAllText(Paths.KeysFile, keys);
        Repo.Write("src/app.txt", "line 1\nline 2 changed\n");
        Fake = new FakeReviewer(script, streams);
        Engine = MakeEngine();
    }

    public ReviewEngine MakeEngine() => new(new EngineOptions
    {
        Paths = Paths,
        ReviewerFactory = _ => Fake,
        Delay = (t, ct) => { Delays.Add(t); return Task.CompletedTask; },
        Progress = Progress,
        LaunchGui = r => GuiLaunches.Add(r.RunId),
        WaitPollInterval = TimeSpan.FromMilliseconds(20),
    });

    public Task<CommandResult> Start() => Engine.StartAsync("fake", "Changed line 2.", Repo.Dir, CancellationToken.None);

    public Task<CommandResult> Next(CommandResult prev, string json) => Engine.NextAsync(prev.RunId!, json, CancellationToken.None);

    public RunPaths Run(CommandResult r) => Paths.Run(r.RunId!);

    public void Dispose()
    {
        Repo.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class F
{
    public static object Finding(string severity = "bug", string title = "Something", string? previousId = null, params string[] refs)
        => previousId is null
            ? new { severity, title, explanation = "Because.", refs }
            : new { severity, title, explanation = "Still.", refs, previousId };

    public static FakeStep Out(params object[] findings) => FakeStep.Findings(findings);

    public static string Resp(params (string Id, string Action)[] items)
        => JsonSerializer.Serialize(new { responses = items.Select(i => new { id = i.Id, action = i.Action, reasoning = i.Action == "reject" ? "Not a problem: see Foo.cs:1." : "Fixed." }) });
}
