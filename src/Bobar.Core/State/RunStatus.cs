using Bobar.Core.Model;

namespace Bobar.Core.State;

/// <summary>The badge a run gets in the GUI's list (and `runs`).</summary>
public enum RunStatusKind { Running, WaitingForAuthor, AwaitingDecisions, Done, Error, Cancelled, Idle, Discussing, AwaitingAnswers, AwaitingReport, Reported }

public static class RunStatus
{
    public static readonly TimeSpan IdleAfter = TimeSpan.FromHours(1);

    /// <param name="liveHolder">A live bobar-cli process holds run.lock.</param>
    public static RunStatusKind Of(RunState s, bool liveHolder, DateTimeOffset now)
    {
        if (s.Finished is { } f)
        {
            return f.Outcome switch
            {
                Outcome.Error => RunStatusKind.Error,
                Outcome.Cancelled => RunStatusKind.Cancelled,
                Outcome.Reported => RunStatusKind.Reported,
                _ => RunStatusKind.Done,
            };
        }
        // Awaiting decisions (or answers) is never shown as idle: the user is the one who's late.
        if (s.Phase == RunPhase.AwaitingDecisions) return RunStatusKind.AwaitingDecisions;
        if (s.Phase == RunPhase.AwaitingAnswers) return RunStatusKind.AwaitingAnswers;
        if (!liveHolder && s.LastEventAt is { } last && now - last > IdleAfter) return RunStatusKind.Idle;
        if (s.IsDiscuss) return s.Phase == RunPhase.AwaitingReport ? RunStatusKind.AwaitingReport : RunStatusKind.Discussing;
        return s.Phase == RunPhase.WaitingForAuthor ? RunStatusKind.WaitingForAuthor : RunStatusKind.Running;
    }

    public static string Label(this RunStatusKind k) => k switch
    {
        RunStatusKind.Running => "running",
        RunStatusKind.WaitingForAuthor => "waiting for author",
        RunStatusKind.AwaitingDecisions => "awaiting decisions",
        RunStatusKind.Done => "done",
        RunStatusKind.Error => "error",
        RunStatusKind.Cancelled => "cancelled",
        RunStatusKind.Discussing => "discussing",
        RunStatusKind.AwaitingAnswers => "awaiting answers",
        RunStatusKind.AwaitingReport => "awaiting report",
        RunStatusKind.Reported => "reported",
        _ => "idle",
    };
}
