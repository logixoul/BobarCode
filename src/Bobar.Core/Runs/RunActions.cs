using System.Diagnostics;
using Bobar.Core.IO;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Proc;
using Bobar.Core.State;

namespace Bobar.Core.Runs;

/// <summary>The few writes the GUI shares with the CLI: cancel and submitting decisions.</summary>
public static class RunActions
{
    public static readonly TimeSpan LockPatience = TimeSpan.FromSeconds(10);
    public const string CancelledByUser = "cancelled by the user";

    public sealed record CancelOutcome(bool Ok, string Message);

    /// <summary>How long a cancel waits for the CLI holding run.lock to end the run itself before killing it.</summary>
    public static readonly TimeSpan CooperativePatience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Ends the run as cancelled. If a CLI holds run.lock, asks it to stop (cancel.request), so it kills its
    /// reviewer and prints STATUS: cancelled to its caller; only if it doesn't within CooperativePatience is
    /// its process tree killed. Otherwise appends run_finished(cancelled) itself.
    /// </summary>
    /// <param name="cliHoldsLock">Is run.lock held by a live bobar-cli? Default: <see cref="RunLock.HeldByLiveCli"/>; tests stand in for it.</param>
    public static async Task<CancelOutcome> CancelAsync(RunPaths run, Redactor redactor, TimeProvider time, string message = CancelledByUser,
        Func<RunPaths, bool>? cliHoldsLock = null)
    {
        var state = RunReader.LoadState(run);
        if (state.Finished is not null) return new(false, $"the run had already finished ({state.Finished.Outcome.ToString().ToLowerInvariant()})");

        var killed = false;
        var lk = RunLock.TryAcquire(run);
        if (lk is null && (cliHoldsLock ?? RunLock.HeldByLiveCli)(run))
        {
            RunFiles.WriteTextAtomic(run.CancelRequest, message, redactor);
            var until = time.GetUtcNow() + CooperativePatience;
            while (lk is null && time.GetUtcNow() < until)
            {
                await Task.Delay(100);
                if (RunReader.LoadState(run).Finished is { } f)
                    return f.Outcome == Outcome.Cancelled
                        ? new(true, "stopped the running reviewer and cancelled the run")
                        : new(false, "the run finished meanwhile");
                lk = RunLock.TryAcquire(run); // the CLI let go without ending the run (e.g. it now awaits decisions)
            }
        }
        if (lk is null)
        {
            var pid = RunLock.ReadPid(run);
            if (pid is { } p && p != Environment.ProcessId)
            {
                try
                {
                    using var proc = Process.GetProcessById(p);
                    if (RunLock.IsCliProcessName(proc.ProcessName))
                    {
                        ProcessRunner.KillTree(proc);
                        proc.WaitForExit(5000);
                        killed = true;
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            lk = await RunLock.AcquireAsync(run, LockPatience);
            if (lk is null) return new(false, "could not take run.lock (another process holds it and could not be stopped)");
        }
        using (lk)
        {
            state = RunReader.LoadState(run);
            if (state.Finished is not null) return new(false, "the run finished meanwhile");
            EventLog.Append(run, new RunFinished { T = time.GetLocalNow(), Outcome = Outcome.Cancelled, Message = message }, redactor);
        }
        return new(true, killed ? "stopped the running reviewer and cancelled the run" : "cancelled the run");
    }

    /// <summary>
    /// GUI Submit: writes decisions.json atomically, then appends decisions_submitted.
    /// If the lock can't be had, decisions.json alone is enough: `wait` appends the event.
    /// </summary>
    public static async Task<string?> SubmitDecisionsAsync(RunPaths run, DecisionsFile decisions, Redactor redactor, TimeProvider time)
    {
        var state = RunReader.LoadState(run);
        if (state.Phase != RunPhase.AwaitingDecisions || state.Awaiting is null) return "the run is not awaiting decisions";
        var v = Validation.Decisions(decisions, state.Awaiting.Ids);
        if (!v.Ok) return v.ErrorText;
        RunFiles.WriteJson(run.Decisions, decisions, BobarJson.Indented.DecisionsFile, redactor);
        using var lk = await RunLock.AcquireAsync(run, LockPatience);
        if (lk is not null && RunReader.LoadState(run).Decisions is null)
            EventLog.Append(run, Submitted(decisions, time), redactor);
        return null;
    }

    public static DecisionsSubmitted Submitted(DecisionsFile d, TimeProvider time) => new()
    {
        T = time.GetLocalNow(),
        Fix = d.Decisions.Count(x => x.Decision == UserDecision.Fix),
        Dismiss = d.Decisions.Count(x => x.Decision == UserDecision.Dismiss),
    };
}
