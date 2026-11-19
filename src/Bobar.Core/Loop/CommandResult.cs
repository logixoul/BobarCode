namespace Bobar.Core.Loop;

/// <summary>
/// One CLI result (§8): Markdown for Claude to read, whose first line is
/// `STATUS: &lt;status&gt; run=&lt;runId&gt; [round=&lt;k&gt;]`.
/// </summary>
public sealed record CommandResult(string Status, string? RunId, int? Round, string Body, int ExitCode = 0)
{
    public static class Statuses
    {
        public const string Findings = "findings";
        public const string AwaitingDecisions = "awaiting_decisions";
        public const string Done = "done";
        public const string Decided = "decided";
        public const string Cancelled = "cancelled";
        public const string Error = "error";
        public const string InvalidResponse = "invalid_response";
        /// <summary>Not in the spec's table: another process holds run.lock.</summary>
        public const string Busy = "busy";
        /// <summary>Not in the spec's table: `show` on a run whose reviewer is still working.</summary>
        public const string Running = "running";
        /// <summary>`hindsight` stored the author's look back on a finished run.</summary>
        public const string HindsightSaved = "hindsight_saved";
        /// <summary>`hindsight` input failed validation (exit 2).</summary>
        public const string InvalidHindsight = "invalid_hindsight";
    }

    public string Header
    {
        get
        {
            var h = $"STATUS: {Status}";
            if (RunId is not null) h += $" run={RunId}";
            if (Round is not null) h += $" round={Round}";
            return h;
        }
    }

    public string Render() => Header + "\n\n" + Body.TrimEnd() + "\n";

    public static CommandResult Fail(string message, string? runId = null, int? round = null)
        => new(Statuses.Error, runId, round, message, 1);
}
