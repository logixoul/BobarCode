using System.Diagnostics;
using System.Text;

namespace Bobar.Core.Proc;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args, string WorkingDirectory)
{
    /// <summary>Env changes: a value sets the variable, null removes it.</summary>
    public IReadOnlyDictionary<string, string?> Env { get; init; } = new Dictionary<string, string?>();
    public string? Stdin { get; init; }
}

public sealed record ProcessOutcome(int ExitCode, string Stdout, string StderrTail, TimeSpan Duration);

/// <summary>
/// Spawns with ArgumentList (never a command string), sends stdin, streams stdout lines, and kills
/// the whole tree on cancellation.
/// </summary>
public static class ProcessRunner
{
    private const int StderrTailChars = 8000;

    /// <param name="onStdoutLine">Called per stdout line; when null, stdout is captured into the outcome instead.</param>
    /// <exception cref="OperationCanceledException">ct was cancelled (the tree has been killed).</exception>
    public static async Task<ProcessOutcome> RunAsync(ProcessSpec spec, Action<string>? onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Env)
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }

        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        p.Start();

        var captured = onStdoutLine is null ? new StringBuilder() : null;
        var stderr = new StringBuilder();
        // ReadLineAsync splits on \r, \n and \r\n but never on U+2028/2029, and JSON lines can't hold raw CR/LF.
        var outTask = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                if (captured is not null) captured.Append(line).Append('\n');
                else onStdoutLine!(line);
            }
        });
        var errTask = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
            {
                lock (stderr)
                {
                    stderr.Append(line).Append('\n');
                    if (stderr.Length > StderrTailChars * 2) stderr.Remove(0, stderr.Length - StderrTailChars);
                }
            }
        });

        try
        {
            if (spec.Stdin is not null) await p.StandardInput.WriteAsync(spec.Stdin);
            p.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child exited without reading stdin; its exit code tells the story.
        }

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            KillTree(p);
            await p.WaitForExitAsync(CancellationToken.None);
        }
        // Grandchildren may keep the pipes open briefly; don't hang on them.
        await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
        ct.ThrowIfCancellationRequested();

        string tail;
        lock (stderr) tail = stderr.Length > StderrTailChars ? stderr.ToString(stderr.Length - StderrTailChars, StderrTailChars) : stderr.ToString();
        return new ProcessOutcome(p.ExitCode, captured?.ToString() ?? "", tail, sw.Elapsed);
    }

    public static void KillTree(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Finds an executable on PATH (with PATHEXT on Windows). Null if not found.</summary>
    public static string? FindOnPath(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        var hasExt = Path.HasExtension(name);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (hasExt)
                {
                    var f = Path.Combine(dir, name);
                    if (File.Exists(f)) return f;
                    continue;
                }
                foreach (var e in exts)
                {
                    var f = Path.Combine(dir, name + e);
                    if (File.Exists(f)) return f;
                }
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
