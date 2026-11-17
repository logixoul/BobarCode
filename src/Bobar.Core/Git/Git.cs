using Bobar.Core.Proc;

namespace Bobar.Core.Git;

public sealed class GitException(string message) : Exception(message);

/// <summary>Thin git wrapper. Never touches the user's index, stash or branches.</summary>
public static class GitCli
{
    /// <summary>git's well-known empty tree, used as the base in a repo with no commits.</summary>
    public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    public static async Task<string> RunAsync(string repo, IEnumerable<string> args, CancellationToken ct, string? indexFile = null)
    {
        var argList = args.ToList();
        var env = new Dictionary<string, string?>();
        if (indexFile is not null) env["GIT_INDEX_FILE"] = indexFile;
        var outcome = await ProcessRunner.RunAsync(new ProcessSpec("git", argList, repo) { Env = env }, null, ct);
        if (outcome.ExitCode != 0)
            throw new GitException($"git {string.Join(' ', argList)} failed (exit {outcome.ExitCode}): {outcome.StderrTail.Trim()}");
        return outcome.Stdout;
    }

    public static async Task<string> TopLevelAsync(string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) throw new GitException($"directory not found: {dir}");
        try
        {
            var top = (await RunAsync(dir, ["rev-parse", "--show-toplevel"], ct)).Trim();
            return Path.GetFullPath(top);
        }
        catch (GitException)
        {
            throw new GitException($"not inside a git repository: {dir}");
        }
    }

    /// <summary>HEAD's commit, or the empty tree when the repo has no commits yet.</summary>
    public static async Task<string> HeadOrEmptyAsync(string repo, CancellationToken ct)
    {
        try
        {
            return (await RunAsync(repo, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], ct)).Trim();
        }
        catch (GitException)
        {
            return EmptyTree;
        }
    }

    /// <summary>
    /// Snapshots the working tree into a tree object via a temporary index (§6):
    /// read-tree base, add -A, write-tree. The temp index is deleted afterwards.
    /// </summary>
    public static async Task<string> SnapshotAsync(string repo, string baseRev, string indexFile, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(indexFile)!);
        try
        {
            if (baseRev == EmptyTree) await RunAsync(repo, ["read-tree", "--empty"], ct, indexFile);
            else await RunAsync(repo, ["read-tree", baseRev], ct, indexFile);
            await RunAsync(repo, ["add", "-A"], ct, indexFile);
            return (await RunAsync(repo, ["write-tree"], ct, indexFile)).Trim();
        }
        finally
        {
            TryDelete(indexFile);
            TryDelete(indexFile + ".lock");
        }
    }

    /// <summary>Stable tree-to-tree diff (autocrlf off, CR at EOL ignored).</summary>
    public static Task<string> DiffAsync(string repo, string from, string to, CancellationToken ct)
        => RunAsync(repo, ["-c", "core.autocrlf=false", "diff", "--ignore-cr-at-eol", from, to], ct);

    private static void TryDelete(string f)
    {
        try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
