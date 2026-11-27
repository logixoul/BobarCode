using Bobar.Core.IO;
using Bobar.Core.Model;
using Bobar.Core.Proc;
using Bobar.Core.State;

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

    /// <summary>
    /// The project <paramref name="dir"/> belongs to: the main checkout's folder name, the same from any of its
    /// worktrees (git lists the main worktree first). Null when <paramref name="dir"/> is not in a repo.
    /// </summary>
    public static async Task<string?> ProjectNameAsync(string dir, CancellationToken ct)
    {
        string list;
        string[] revs;
        try
        {
            // Fails in a bare repo (no top level): then the worktree list below names it.
            try
            {
                revs = (await RunAsync(dir, ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir", "--show-toplevel"], ct))
                    .Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            catch (GitException) { revs = []; }
            // In the main checkout itself its own top level is right, even with a separate git dir or in a submodule
            // (where git's main-worktree path below is the git dir, not the checkout).
            if (revs.Length == 3 && revs[0] == revs[1]) return Path.GetFileName(Path.TrimEndingDirectorySeparator(revs[2])) is { Length: > 0 } top ? top : null;
            list = await RunAsync(dir, ["worktree", "list", "--porcelain"], ct);
        }
        // Win32Exception: git isn't installed (a discussion's folder needn't be a repo, so git isn't required).
        catch (Exception ex) when (ex is GitException or System.ComponentModel.Win32Exception) { return null; }
        // A linked worktree: the first record (up to the first blank line) is the main worktree.
        var first = list.Replace("\r", "").Split("\n\n")[0].Split('\n');
        var main = first.FirstOrDefault(l => l.StartsWith("worktree ", StringComparison.Ordinal));
        if (main is null) return null;
        var mainPath = Path.TrimEndingDirectorySeparator(main["worktree ".Length..].Trim());
        var bare = first.Any(l => l == "bare");
        // With a separate git dir, git reports the git dir as the main worktree: name a Claude app worktree
        // after its checkout by path instead, as for older runs.
        if (!bare && revs.Length == 3 && SamePath(mainPath, revs[1])) return RunState.ProjectFromPath(revs[2]) is { Length: > 0 } p ? p : null;
        var leaf = Path.GetFileName(mainPath);
        // A bare repo's "main worktree" is the repo itself, often name.git.
        var name = bare && leaf.Length > 4 && leaf.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? leaf[..^4] : leaf;
        return name.Length > 0 ? name : null;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

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
            RunFiles.TryDeleteFile(indexFile);
            RunFiles.TryDeleteFile(indexFile + ".lock");
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
            RunFiles.TryDeleteFile(indexFile);
            RunFiles.TryDeleteFile(indexFile + ".lock");
        }
    }

    /// <summary>Stable tree-to-tree diff (autocrlf off, CR at EOL ignored).</summary>
    public static Task<string> DiffAsync(string repo, string from, string to, CancellationToken ct)
        => RunAsync(repo, ["-c", "core.autocrlf=false", "diff", "--ignore-cr-at-eol", from, to], ct);
}
