using Bobar.Core.IO;

namespace Bobar.Core.Runs;

/// <summary>
/// Deleting a run from the GUI: its dir moves to <see cref="BobarPaths.TrashDir"/>, so Undo can move it back.
/// Trashed runs are purged after <see cref="KeepFor"/>. A run a bobar-cli is working on can't be deleted.
/// </summary>
public static class RunTrash
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    /// <summary>Null on success, else why it couldn't be deleted.</summary>
    public static string? Delete(BobarPaths paths, RunPaths run)
    {
        if (!Directory.Exists(run.Dir)) return "the run is already gone";
        try
        {
            using (var lk = RunLock.TryAcquire(run))
                if (lk is null) return "a bobar-cli is working on it";
            Directory.CreateDirectory(paths.TrashDir);
            var dest = Path.Combine(paths.TrashDir, run.RunId);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.Move(run.Dir, dest);
            Directory.SetLastWriteTimeUtc(dest, DateTime.UtcNow); // the purge clock starts now
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Moves a trashed run back to the runs dir. Null on success, else why not.</summary>
    public static string? Restore(BobarPaths paths, string runId)
    {
        var src = Path.Combine(paths.TrashDir, runId);
        var run = paths.Run(runId);
        if (!Directory.Exists(src)) return "it is no longer in the trash";
        if (Directory.Exists(run.Dir)) return "a run with that id exists again";
        try
        {
            Directory.Move(src, run.Dir);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Permanently deletes runs trashed more than <see cref="KeepFor"/> ago. Best effort.</summary>
    public static void Purge(BobarPaths paths, DateTime nowUtc)
    {
        try
        {
            if (!Directory.Exists(paths.TrashDir)) return;
            foreach (var dir in Directory.GetDirectories(paths.TrashDir))
                if (nowUtc - Directory.GetLastWriteTimeUtc(dir) > KeepFor) RunFiles.TryDeleteDir(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
