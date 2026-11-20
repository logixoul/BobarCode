using Bobar.Core.Model;
using Bobar.Core.Proc;

namespace Bobar.Core.Git;

public sealed class GitException(string message) : Exception(message);

/// <summary>Thin git wrapper. Never touches the user's index, stash or branches.</summary>
public static class GitCli
{
    /// <summary>git's well-known empty tree, used as the base in a repo with no commits.</summary>
    public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    public static async Task<string> RunAsync(string repo, IEnumerable<string> args, CancellationToken ct, string? indexFile = null, IReadOnlyDictionary<string, string?>? extraEnv = null)
    {
        var argList = args.ToList();
        var env = new Dictionary<string, string?>();
        if (indexFile is not null) env["GIT_INDEX_FILE"] = indexFile;
        if (extraEnv is not null) foreach (var (k, v) in extraEnv) env[k] = v;
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
    /// Resolves <paramref name="from"/> for a commit-range review (spec §6): it must be a commit that is HEAD
    /// or an ancestor of it. Returns the diff base (from's first parent, or the empty tree for a root commit)
    /// and the commits in from..HEAD, oldest first, following first parents.
    /// </summary>
    public static async Task<(string Base, List<CommitInfo> Commits)> RangeAsync(string repo, string from, CancellationToken ct)
    {
        string sha;
        try { sha = (await RunAsync(repo, ["rev-parse", "--verify", "--quiet", "--end-of-options", from + "^{commit}"], ct)).Trim(); }
        catch (GitException) { throw new GitException($"'{from}' is not a commit in {repo}."); }
        try { await RunAsync(repo, ["merge-base", "--is-ancestor", sha, "HEAD"], ct); }
        catch (GitException) { throw new GitException($"'{from}' is not HEAD or an ancestor of HEAD, so it can't start a range ending at HEAD."); }

        string baseRev;
        try { baseRev = (await RunAsync(repo, ["rev-parse", "--verify", "--quiet", sha + "^1^{commit}"], ct)).Trim(); }
        catch (GitException) { baseRev = EmptyTree; }

        List<string> range = baseRev == EmptyTree ? ["HEAD"] : [$"{baseRev}..HEAD"];
        var log = await RunAsync(repo, ["log", "--reverse", "--first-parent", "--format=%H%x00%B%x1e", .. range], ct);
        var commits = log.Split('\x1e')
            .Select(e => e.Trim('\r', '\n'))
            .Where(e => e.Length > 0)
            .Select(e =>
            {
                var z = e.IndexOf('\0');
                return new CommitInfo { Sha = e[..z], Message = e[(z + 1)..].Trim() };
            })
            .ToList();
        return (baseRev, commits);
    }

    /// <summary>The tree of a commit (or the empty tree passed through).</summary>
    public static async Task<string> TreeOfAsync(string repo, string rev, CancellationToken ct)
        => rev == EmptyTree ? EmptyTree : (await RunAsync(repo, ["rev-parse", rev + "^{tree}"], ct)).Trim();

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

    /// <summary>
    /// Writes the files of <paramref name="tree"/> into <paramref name="dir"/> (emptied first), through a
    /// temporary index: read-tree, then checkout-index with a prefix. The real index and worktree are untouched.
    /// Git LFS files stay pointer files (GIT_LFS_SKIP_SMUDGE), so a round never downloads LFS content.
    /// Submodules are not checked out: the copy has their commit pointers only.
    /// </summary>
    public static async Task MaterializeAsync(string repo, string tree, string dir, string indexFile, CancellationToken ct)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(indexFile)!);
        try
        {
            await RunAsync(repo, ["read-tree", tree], ct, indexFile);
            var prefix = Path.GetFullPath(dir).Replace('\\', '/').TrimEnd('/') + "/";
            await RunAsync(repo, ["checkout-index", "-a", "-f", "--prefix=" + prefix], ct, indexFile, new Dictionary<string, string?> { ["GIT_LFS_SKIP_SMUDGE"] = "1" });
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
