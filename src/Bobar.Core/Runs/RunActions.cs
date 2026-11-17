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

    public sealed record CancelOutcome(bool Ok, string Message, bool KilledProcess);

    /// <summary>
    /// Kills the run's active process tree (pid from run.lock), then appends run_finished(cancelled).
    /// </summary>
    public static async Task<CancelOutcome> CancelAsync(RunPaths run, Redactor redactor, TimeProvider time, string message = "cancelled by the user")
    {
        var state = RunReader.LoadState(run);
        if (state.Finished is not null) return new(false, $"the run had already finished ({state.Finished.Outcome.ToString().ToLowerInvariant()})", false);

        var killed = false;
        var lk = RunLock.TryAcquire(run);
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
            if (lk is null) return new(false, "could not take run.lock (another process holds it and could not be stopped)", killed);
        }
        using (lk)
        {
            state = RunReader.LoadState(run);
            if (state.Finished is not null) return new(false, "the run finished meanwhile", killed);
            EventLog.Append(run, new RunFinished { T = time.GetLocalNow(), Outcome = Outcome.Cancelled, Message = message }, redactor);
        }
        return new(true, killed ? "stopped the running reviewer and cancelled the run" : "cancelled the run", killed);
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
