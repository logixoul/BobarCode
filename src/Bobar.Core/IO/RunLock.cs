using System.Diagnostics;
using System.Text;

namespace Bobar.Core.IO;

/// <summary>
/// run.lock: an OS file lock held by the process working on the run; the file contains its pid.
/// The holder opens it for writing and shares only reads, so a second writer fails at once while
/// readers (cancel, GUI) can still see the pid.
/// On macOS/Linux .NET maps FileShare to flock, where any sharing means a shared lock that a second
/// writer also gets; so there the holder takes it exclusively and writes its pid to run.pid instead.
/// </summary>
public sealed class RunLock : IDisposable
{
    private readonly FileStream _fs;

    private RunLock(FileStream fs) => _fs = fs;

    /// <summary>Null when another process (or another handle in this one) holds it.</summary>
    public static RunLock? TryAcquire(RunPaths run)
    {
        Directory.CreateDirectory(run.Dir);
        try
        {
            var windows = OperatingSystem.IsWindows();
            var fs = new FileStream(run.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, windows ? FileShare.Read : FileShare.None);
            var bytes = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString());
            if (windows)
            {
                fs.SetLength(0);
                fs.Write(bytes);
                fs.Flush(flushToDisk: false);
            }
            else
            {
                try { File.WriteAllBytes(run.Pid, bytes); }
                catch { fs.Dispose(); throw; }
            }
            return new RunLock(fs);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Retries for a short while; for brief writers (GUI decisions, wait's final event, cancel).</summary>
    public static async Task<RunLock?> AcquireAsync(RunPaths run, TimeSpan patience, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + patience;
        while (true)
        {
            var l = TryAcquire(run);
            if (l is not null || DateTime.UtcNow >= until) return l;
            await Task.Delay(100, ct);
        }
    }

    /// <summary>The pid written by the last holder (it may have exited since).</summary>
    public static int? ReadPid(RunPaths run)
    {
        var text = RunFiles.ReadShared(OperatingSystem.IsWindows() ? run.Lock : run.Pid);
        return int.TryParse(text?.Trim(), out var pid) ? pid : null;
    }

    /// <summary>
    /// Non-intrusive check for the GUI: is the pid in run.lock a live bobar-cli process?
    /// (Trying to take the lock would race with a CLI that is about to take it.)
    /// </summary>
    public static bool HeldByLiveCli(RunPaths run)
    {
        var pid = ReadPid(run);
        if (pid is null) return false;
        try
        {
            using var p = Process.GetProcessById(pid.Value);
            return !p.HasExited && IsCliProcessName(p.ProcessName);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static bool IsCliProcessName(string name) => name.Equals("bobar-cli", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _fs.Dispose();
}
