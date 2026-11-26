using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bobar.Core.Model;

namespace Bobar.Core.Reviewers;

/// <summary>One scripted reviewer call: output text, a failure, or a hang (until cancelled).</summary>
public sealed record FakeStep
{
    public string? Output { get; init; }
    public string? Fail { get; init; }
    public bool Transient { get; init; }
    public double? RetryAfterSeconds { get; init; }
    public bool Hang { get; init; }
    /// <summary>Streaming: the final text after reporting the output's findings (default "DONE").</summary>
    public string Final { get; init; } = "DONE";
    public TokenUsage Usage { get; init; } = new();
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(1);

    public static FakeStep Json(string json) => new() { Output = json };
    public static FakeStep Findings(params object[] findings) => new() { Output = JsonSerializer.Serialize(new { findings }) };
    public static FakeStep Error(string msg, bool transient = false, double? retryAfter = null) => new() { Fail = msg, Transient = transient, RetryAfterSeconds = retryAfter };
}

/// <summary>
/// Scripted reviewer for tests and for trying the skill end to end (`--reviewer fake`).
/// Steps are keyed by round; each call (run, repair) in that round consumes the next step.
/// A round without steps uses the default behaviour: round 1 raises two sample findings, later
/// rounds maintain every finding the author rejected in the previous round and raise nothing new.
/// </summary>
public sealed class FakeReviewer : IReviewer
{
    public const string ScriptEnv = "BOBAR_FAKE_SCRIPT";
    /// <summary>"1": the fake reviewer streams its findings (report_finding) like a Pi reviewer.</summary>
    public const string StreamEnv = "BOBAR_FAKE_STREAM";

    private readonly Dictionary<int, Queue<FakeStep>> _script;
    public List<(int Round, string Prompt)> Calls { get; } = [];
    /// <summary>Per call, the session it was asked to resume (null: fresh). A repair resumes the call's own session.</summary>
    public List<string?> ResumeIds { get; } = [];
    /// <summary>Runs at the start of each call (tests look at the call's directory while it "reviews").</summary>
    public Action<ReviewerCall>? OnCall { get; set; }

    private readonly bool _streams;

    /// <param name="streams">Report each finding of a step's output through <see cref="ReviewerCall.OnFinding"/>
    /// and return "DONE", as a streaming reviewer does.</param>
    public FakeReviewer(IDictionary<int, IEnumerable<FakeStep>>? script = null, bool streams = false)
    {
        _streams = streams;
        _script = (script ?? new Dictionary<int, IEnumerable<FakeStep>>()).ToDictionary(kv => kv.Key, kv => new Queue<FakeStep>(kv.Value));
    }

    /// <summary>
    /// BOBAR_FAKE_SCRIPT = path to {"1": [{"output": {...}} | {"output": "raw text"} | {"fail": "...", "transient": true} | {"hang": true}], "2": [...]}.
    /// </summary>
    public static FakeReviewer FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(ScriptEnv);
        var streams = Environment.GetEnvironmentVariable(StreamEnv) == "1";
        if (string.IsNullOrWhiteSpace(path)) return new FakeReviewer(streams: streams);
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new ConfigException($"{ScriptEnv}: {path} must hold a JSON object keyed by round");
        var script = new Dictionary<int, IEnumerable<FakeStep>>();
        foreach (var (key, val) in root)
        {
            if (!int.TryParse(key, out var round) || val is not JsonArray steps) continue;
            script[round] = steps.OfType<JsonObject>().Select(s => new FakeStep
            {
                Output = s["output"] switch
                {
                    null => null,
                    JsonValue v when v.TryGetValue<string>(out var str) => str,
                    var n => n.ToJsonString(),
                },
                Fail = s["fail"]?.GetValue<string>(),
                Transient = s["transient"]?.GetValue<bool>() ?? false,
                RetryAfterSeconds = s["retryAfterSeconds"]?.GetValue<double>(),
                Hang = s["hang"]?.GetValue<bool>() ?? false,
            }).ToList();
        }
        return new FakeReviewer(script, streams);
    }

    public bool StreamsFindings => _streams;

    public Task<ReviewerResult> RunAsync(ReviewerCall call, CancellationToken ct) => Step(call, call.ResumeSessionId, ct);

    public Task<ReviewerResult> RepairAsync(ReviewerResult previous, string error, CancellationToken ct)
        => Step(previous.Call with { Prompt = error }, previous.SessionId, ct);

    private async Task<ReviewerResult> Step(ReviewerCall call, string? resumeId, CancellationToken ct)
    {
        Calls.Add((call.Round, call.Prompt));
        ResumeIds.Add(resumeId);
        OnCall?.Invoke(call);
        var step = _script.TryGetValue(call.Round, out var q) && q.Count > 0 ? q.Dequeue() : null;
        step ??= new FakeStep { Output = DefaultOutput(call) };
        call.RawLog(JsonSerializer.Serialize(new { type = "fake_step", round = call.Round, output = step.Output, fail = step.Fail, hang = step.Hang }));

        if (step.Hang) await Task.Delay(Timeout.Infinite, ct);
        ct.ThrowIfCancellationRequested();
        // Streaming: the step's findings are reported first, so a step with both output and a failure
        // is a reviewer that reported some findings and then failed.
        var output = step.Output ?? "";
        if (_streams && call.OnFinding is not null && Loop.Validation.ParseModelJson(output) is JsonObject { } parsed && parsed["findings"] is JsonArray found)
        {
            var n = call.FirstFindingNumber - 1;
            foreach (var f in found.OfType<JsonObject>()) call.OnFinding(new StreamedFinding(++n, (JsonObject)f.DeepClone()));
            output = step.Final;
        }
        if (step.Fail is not null)
            throw new ReviewerException(step.Fail, step.Transient, step.RetryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null, usage: step.Usage);

        return new ReviewerResult
        {
            Call = call,
            FinalText = output,
            Parsed = Loop.Validation.ParseModelJson(output),
            Usage = step.Usage,
            RequestedModel = "fake",
            ResolvedModels = ["fake"],
            SessionId = resumeId ?? $"fake-session-{call.Round}",
            Duration = step.Duration,
        };
    }

    private static string DefaultOutput(ReviewerCall call)
    {
        // A discussion turn (its schema asks for a solution): a placeholder solution, no messages.
        if (call.JsonSchema.Contains("\"solution\"", StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { solution = $"The fake participant's solution in round {call.Round}." });
        if (call.Round == 1)
        {
            var file = FirstChangedFile(call.DiffText) ?? "README.md";
            return JsonSerializer.Serialize(new
            {
                findings = new object[]
                {
                    new { severity = "bug", title = "Sample bug from the fake reviewer", explanation = "The fake reviewer always raises this in round 1. Fix it (or reject it) to see the loop move on.", refs = new[] { file + ":1" } },
                    new { severity = "nit", title = "Sample nit from the fake reviewer", explanation = "A second sample finding, so you can fix one and reject the other.", refs = Array.Empty<string>() },
                },
            });
        }
        var last = call.PriorRounds.LastOrDefault();
        var maintained = last?.Review is null || last.Response is null
            ? []
            : last.Response.Responses.Where(r => r.Action == AuthorAction.Reject)
                .Select(r => last.Review.Findings.First(f => f.Id == r.Id))
                .Select(f => (object)new { severity = f.Severity.Name(), title = f.Title, explanation = "Still a problem (the fake reviewer maintains every rejected finding).", refs = f.Refs, previousId = f.Id })
                .ToArray();
        return JsonSerializer.Serialize(new { findings = maintained });
    }

    private static string? FirstChangedFile(string? diff)
    {
        if (diff is null) return null;
        var m = Regex.Match(diff, @"^\+\+\+ b/(.+)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
