namespace Bobar.Core.IO;

/// <summary>
/// For the CLI holding run.lock: a token that trips when a cancel (GUI or `review cancel`) writes
/// cancel.request, so the CLI stops its reviewer and reports STATUS: cancelled instead of being killed.
/// </summary>
public sealed class CancelWatch : IDisposable
{
    // Not disposed: Timer.Dispose doesn't wait for a running callback, and disposing a CTS while it is being
    // cancelled is unsafe. It is per command, and the process exits right after.
    private readonly CancellationTokenSource _cts;
    private readonly Timer _timer;
    private volatile bool _stopped;

    public CancelWatch(RunPaths run, CancellationToken outer, TimeSpan poll)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _timer = new Timer(_ =>
        {
            if (_stopped || !File.Exists(run.CancelRequest)) return;
            try { _cts.Cancel(); }
            catch (AggregateException) { } // a registered callback failed; the token is cancelled anyway
        }, null, TimeSpan.Zero, poll);
    }

    public CancellationToken Token => _cts.Token;

    public void Dispose()
    {
        _stopped = true;
        _timer.Dispose();
    }
}
