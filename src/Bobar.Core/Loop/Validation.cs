using System.Text.Json;
using System.Text.Json.Nodes;
using Bobar.Core.Model;

namespace Bobar.Core.Loop;

/// <summary>A reviewer finding as validated, before the CLI assigns its id.</summary>
public sealed record RawFinding(Severity Severity, string Title, string Explanation, List<string> Refs, string? PreviousId);

public sealed record Validated<T>(T? Value, List<string> Errors)
{
    public bool Ok => Errors.Count == 0 && Value is not null;
    public string ErrorText => string.Join("\n", Errors.Select(e => "- " + e));
}

public static class Validation
{
    /// <summary>The reviewer JSON schema (also passed to Claude's --json-schema).</summary>
    public const string ReviewerSchema = """
        {"type":"object","properties":{"findings":{"type":"array","items":{"type":"object","properties":{"severity":{"type":"string","enum":["bug","risk","design","nit"]},"title":{"type":"string"},"explanation":{"type":"string"},"refs":{"type":"array","items":{"type":"string"}},"previousId":{"type":"string"}},"required":["severity","title","explanation","refs"],"additionalProperties":false}}},"required":["findings"],"additionalProperties":false}
        """;

    /// <summary>
    /// Validates reviewer output (§7). <paramref name="earlierIds"/> are all finding ids of rounds &lt; k.
    /// `refs` may be missing (treated as empty); a `previousId` must name an earlier finding.
    /// </summary>
    public static Validated<List<RawFinding>> Review(JsonNode? node, IReadOnlySet<string> earlierIds)
    {
        var errors = new List<string>();
        if (node is not JsonObject root)
            return new(null, ["the output must be a JSON object of the form {\"findings\": [...]}"]);
        if (root["findings"] is not JsonArray arr)
            return new(null, ["missing \"findings\" array"]);

        var list = new List<RawFinding>();
        for (var i = 0; i < arr.Count; i++)
        {
            var at = $"findings[{i}]";
            if (arr[i] is not JsonObject f)
            {
                errors.Add($"{at} is not an object");
                continue;
            }
            var sevText = Str(f, "severity");
            if (!EnumText.TryParseSeverity(sevText, out var sev))
                errors.Add($"{at}.severity must be one of bug, risk, design, nit (got {Show(f["severity"])})");
            var title = Str(f, "title");
            if (string.IsNullOrWhiteSpace(title)) errors.Add($"{at}.title must be a non-empty string");
            var expl = Str(f, "explanation");
            if (string.IsNullOrWhiteSpace(expl)) errors.Add($"{at}.explanation must be a non-empty string");

            var refs = new List<string>();
            switch (f["refs"])
            {
                case null: break;
                case JsonArray ra:
                    foreach (var r in ra)
                    {
                        if (r is JsonValue v && v.TryGetValue<string>(out var s)) { if (!string.IsNullOrWhiteSpace(s)) refs.Add(s.Trim()); }
                        else errors.Add($"{at}.refs must contain only strings");
                    }
                    break;
                default: errors.Add($"{at}.refs must be an array of strings"); break;
            }

            string? prev = null;
            if (f["previousId"] is { } pn)
            {
                if (pn is JsonValue pv && pv.TryGetValue<string>(out var ps))
                {
                    ps = ps.Trim();
                    if (ps.Length > 0)
                    {
                        if (earlierIds.Contains(ps)) prev = ps;
                        else errors.Add(earlierIds.Count == 0
                            ? $"{at}.previousId \"{ps}\": there are no earlier findings in round 1; omit previousId"
                            : $"{at}.previousId \"{ps}\" does not name an earlier finding (valid: {string.Join(", ", earlierIds.Order(IdComparer.Instance))})");
                    }
                }
                else errors.Add($"{at}.previousId must be a string");
            }
            list.Add(new RawFinding(sev, title?.Trim() ?? "", expl?.Trim() ?? "", refs, prev));
        }
        return new(errors.Count == 0 ? list : null, errors);
    }

    /// <summary>The author's response must answer exactly the round's ids, each once (§7).</summary>
    public static Validated<ResponseFile> Response(string text, IReadOnlyList<string> roundIds)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, AllowDuplicateProperties = false }); }
        catch (JsonException ex) { return new(null, [$"not valid JSON: {ex.Message}"]); }
        if (node is not JsonObject root || root["responses"] is not JsonArray arr)
            return new(null, ["expected {\"responses\": [{\"id\", \"action\", \"reasoning\"}, ...]}"]);

        var errors = new List<string>();
        var items = new List<AuthorResponseItem>();
        var seen = new HashSet<string>();
        var wanted = roundIds.ToHashSet();
        for (var i = 0; i < arr.Count; i++)
        {
            var at = $"responses[{i}]";
            if (arr[i] is not JsonObject o) { errors.Add($"{at} is not an object"); continue; }
            var id = Str(o, "id")?.Trim();
            if (string.IsNullOrEmpty(id)) { errors.Add($"{at}.id is missing"); continue; }
            if (!wanted.Contains(id)) errors.Add($"{at}.id \"{id}\" is not a finding of this round");
            else if (!seen.Add(id)) errors.Add($"\"{id}\" is answered more than once");
            var actionText = Str(o, "action");
            AuthorAction action = default;
            if (actionText == "fix") action = AuthorAction.Fix;
            else if (actionText == "reject") action = AuthorAction.Reject;
            else errors.Add($"{at}.action must be \"fix\" or \"reject\" (got {Show(o["action"])})");
            var reasoning = Str(o, "reasoning")?.Trim() ?? "";
            if (action == AuthorAction.Reject && reasoning.Length == 0) errors.Add($"{at} (\"{id}\"): a reject needs a reasoning");
            items.Add(new AuthorResponseItem { Id = id, Action = action, Reasoning = reasoning });
        }
        var missing = roundIds.Where(id => !seen.Contains(id)).ToList();
        if (missing.Count > 0) errors.Add($"missing responses for: {string.Join(", ", missing)}");
        if (errors.Count > 0) return new(null, errors);
        // Keep the round's order.
        items = items.OrderBy(x => roundIds.ToList().IndexOf(x.Id)).ToList();
        return new(new ResponseFile { Responses = items }, errors);
    }

    /// <summary>
    /// An exploration's answer (`explore finish`): the review response format (`fix` = accepted) plus `chosen`,
    /// the accepted findings the author will implement (may be empty).
    /// </summary>
    public static Validated<ResponseFile> ExploreResponse(string text, IReadOnlyList<string> ids)
    {
        var v = Response(text, ids);
        var errors = new List<string>(v.Errors);
        JsonObject? root = null;
        try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject; }
        catch (JsonException) { } // Response already reported it
        var chosen = new List<string>();
        switch (root?["chosen"])
        {
            case null:
                if (root is not null) errors.Add("missing \"chosen\": the accepted ids you will implement, or [] for none");
                break;
            case JsonArray arr:
                var accepted = v.Value?.Responses.Where(x => x.Action == AuthorAction.Fix).Select(x => x.Id).ToHashSet();
                foreach (var n in arr)
                {
                    if (n is not JsonValue cv || !cv.TryGetValue<string>(out var id)) { errors.Add("chosen must contain only id strings"); continue; }
                    id = id.Trim();
                    if (chosen.Contains(id)) errors.Add($"\"{id}\" is chosen more than once");
                    else if (!ids.Contains(id)) errors.Add($"chosen: \"{id}\" is not a finding of this exploration");
                    else if (accepted is not null && !accepted.Contains(id)) errors.Add($"chosen: \"{id}\" is not one you accepted (\"fix\")");
                    chosen.Add(id);
                }
                break;
            default: errors.Add("chosen must be an array of finding ids"); break;
        }
        if (errors.Count > 0) return new(null, errors);
        v.Value!.Chosen = chosen;
        return new(v.Value, []);
    }

    /// <summary>decisions.json must decide exactly the awaited ids.</summary>
    public static Validated<DecisionsFile> Decisions(DecisionsFile? file, IReadOnlyList<string> awaitedIds)
    {
        if (file is null) return new(null, ["decisions.json is missing or not valid JSON"]);
        var errors = new List<string>();
        var got = file.Decisions.Select(d => d.Id).ToList();
        foreach (var id in awaitedIds.Where(id => !got.Contains(id))) errors.Add($"no decision for {id}");
        foreach (var id in got.Where(id => !awaitedIds.Contains(id))) errors.Add($"{id} was not awaiting a decision");
        if (got.Count != got.Distinct().Count()) errors.Add("an id is decided more than once");
        return new(errors.Count == 0 ? file : null, errors);
    }

    /// <summary>
    /// The author's hindsight: usefulness 1..10; acceptedWrongly ids are ones the author answered "fix",
    /// rejectedWrongly ids ones it answered "reject" (any round); each with a why; notes non-empty.
    /// </summary>
    public static Validated<HindsightFile> Hindsight(string text, IReadOnlySet<string> fixedIds, IReadOnlySet<string> rejectedIds)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, AllowDuplicateProperties = false }); }
        catch (JsonException ex) { return new(null, [$"not valid JSON: {ex.Message}"]); }
        if (node is not JsonObject root)
            return new(null, ["expected {\"usefulness\", \"acceptedWrongly\", \"rejectedWrongly\", \"notes\"}"]);

        var errors = new List<string>();
        var usefulness = 0;
        if (root["usefulness"] is JsonValue uv && uv.TryGetValue<int>(out var u) && u is >= State.Hindsight.Min and <= State.Hindsight.Max) usefulness = u;
        else errors.Add($"usefulness must be a whole number from {State.Hindsight.Min} to {State.Hindsight.Max} (got {Show(root["usefulness"])})");

        var seen = new HashSet<string>();
        List<HindsightItem> Items(string name, IReadOnlySet<string> allowed, string action)
        {
            var list = new List<HindsightItem>();
            switch (root[name])
            {
                case null: break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        var at = $"{name}[{i}]";
                        if (arr[i] is not JsonObject o) { errors.Add($"{at} is not an object"); continue; }
                        var id = Str(o, "id")?.Trim();
                        if (string.IsNullOrEmpty(id)) { errors.Add($"{at}.id is missing"); continue; }
                        if (!allowed.Contains(id))
                            errors.Add(allowed.Count == 0
                                ? $"{at}.id \"{id}\": you answered no finding \"{action}\" in this run, so {name} must be empty"
                                : $"{at}.id \"{id}\" is not a finding you answered \"{action}\" (valid: {string.Join(", ", allowed.Order(IdComparer.Instance))})");
                        else if (!seen.Add(id)) errors.Add($"\"{id}\" is listed more than once");
                        var why = Str(o, "why")?.Trim() ?? "";
                        if (why.Length == 0) errors.Add($"{at} (\"{id}\"): why is missing");
                        list.Add(new HindsightItem { Id = id, Why = why });
                    }
                    break;
                default: errors.Add($"{name} must be an array of {{\"id\", \"why\"}}"); break;
            }
            return list;
        }
        var accepted = Items("acceptedWrongly", fixedIds, "fix");
        var rejected = Items("rejectedWrongly", rejectedIds, "reject");
        var notes = Str(root, "notes")?.Trim() ?? "";
        if (notes.Length == 0) errors.Add("notes must be a non-empty string (Markdown)");
        if (errors.Count > 0) return new(null, errors);
        return new(new HindsightFile { Usefulness = usefulness, AcceptedWrongly = accepted, RejectedWrongly = rejected, Notes = notes }, errors);
    }

    // ───────────── Discussions (§8b) ─────────────

    /// <summary>A discussion turn's schema (Claude's --json-schema), with or without messages.</summary>
    public static string DiscussSchema(bool messages) => messages
        ? """{"type":"object","properties":{"solution":{"type":"string"},"messages":{"type":"object","additionalProperties":{"type":"string"}}},"required":["solution"],"additionalProperties":false}"""
        : """{"type":"object","properties":{"solution":{"type":"string"}},"required":["solution"],"additionalProperties":false}""";

    /// <summary>
    /// One discusser's turn: a non-empty `solution`, and in rounds that have them, optional `messages` (recipient →
    /// text; empty ones dropped). Messages in a round without them are ignored. Recipients aren't checked here:
    /// an unknown one is kept and shown as undelivered.
    /// </summary>
    public static Validated<DiscussTurnFile> DiscussTurn(JsonNode? node, bool messagesAllowed)
    {
        var shape = messagesAllowed ? "{\"solution\": \"...\", \"messages\": {\"<participant>\": \"...\"}}" : "{\"solution\": \"...\"}";
        if (node is not JsonObject root) return new(null, [$"the output must be a JSON object of the form {shape}"]);
        var errors = new List<string>();
        var solution = Str(root, "solution")?.Trim() ?? "";
        if (solution.Length == 0) errors.Add("\"solution\" must be a non-empty string (your solution, in Markdown)");
        var messages = new Dictionary<string, string>();
        if (messagesAllowed)
        {
            switch (root["messages"])
            {
                case null: break;
                case JsonObject m:
                    foreach (var (to, v) in m)
                    {
                        if (v is JsonValue mv && mv.TryGetValue<string>(out var text)) { if (!string.IsNullOrWhiteSpace(text)) messages[to.Trim()] = text.Trim(); }
                        else errors.Add($"messages.{to} must be a string");
                    }
                    break;
                default: errors.Add("\"messages\" must be an object mapping a participant's name to your message"); break;
            }
        }
        return errors.Count > 0 ? new(null, errors) : new(new DiscussTurnFile { Solution = solution, Messages = messages }, []);
    }

    /// <summary>`discuss ask`: 1+ questions, unique ids, a title, and 2–4 options with labels.</summary>
    public static Validated<QuestionsFile> Questions(string text)
    {
        if (Parse(text, out var err) is not JsonObject root || root["questions"] is not JsonArray arr)
            return new(null, [err ?? "expected {\"questions\": [{\"id\", \"title\", \"context\", \"options\": [{\"label\", \"description\"}]}], \"decided\": \"...\"}"]);
        var errors = new List<string>();
        var file = new QuestionsFile { Decided = Str(root, "decided")?.Trim() is { Length: > 0 } d ? d : null };
        var seen = new HashSet<string>();
        for (var i = 0; i < arr.Count; i++)
        {
            var at = $"questions[{i}]";
            if (arr[i] is not JsonObject q) { errors.Add($"{at} is not an object"); continue; }
            var id = Str(q, "id")?.Trim() ?? "";
            if (id.Length == 0) errors.Add($"{at}.id is missing");
            else if (!seen.Add(id)) errors.Add($"id \"{id}\" is used more than once");
            var title = Str(q, "title")?.Trim() ?? "";
            if (title.Length == 0) errors.Add($"{at}.title must be a non-empty string");
            var options = new List<QuestionOption>();
            if (q["options"] is JsonArray opts)
            {
                for (var j = 0; j < opts.Count; j++)
                {
                    var label = opts[j] is JsonObject o ? Str(o, "label")?.Trim() ?? "" : "";
                    if (label.Length == 0) errors.Add($"{at}.options[{j}].label must be a non-empty string");
                    options.Add(new QuestionOption { Label = label, Description = opts[j] is JsonObject od && Str(od, "description")?.Trim() is { Length: > 0 } desc ? desc : null });
                }
                if (opts.Count is < 2 or > 4) errors.Add($"{at} needs 2 to 4 options (has {opts.Count})");
            }
            else errors.Add($"{at}.options must be an array of {{\"label\", \"description\"}}");
            file.Questions.Add(new QuestionItem { Id = id, Title = title, Context = Str(q, "context")?.Trim() ?? "", Options = options });
        }
        if (arr.Count == 0) errors.Add("there are no questions: if you have none, skip `discuss ask` and send the report");
        return errors.Count > 0 ? new(null, errors) : new(file, []);
    }

    /// <summary>answers.json: every question answered once, with an option in range and/or a non-empty comment.</summary>
    public static Validated<AnswersFile> Answers(AnswersFile? file, QuestionsFile questions)
    {
        if (file is null) return new(null, ["answers.json is missing or not valid JSON"]);
        var errors = new List<string>();
        var byId = questions.Questions.ToDictionary(q => q.Id);
        foreach (var q in questions.Questions.Where(q => file.Answers.All(a => a.Id != q.Id))) errors.Add($"no answer for {q.Id}");
        foreach (var a in file.Answers)
        {
            if (!byId.TryGetValue(a.Id, out var q)) { errors.Add($"{a.Id} is not a question"); continue; }
            if (a.Option is { } o && (o < 0 || o >= q.Options.Count)) errors.Add($"{a.Id}: option {o} is out of range");
            if (a.Option is null && string.IsNullOrWhiteSpace(a.Comment)) errors.Add($"{a.Id}: pick an option or write a comment");
        }
        if (file.Answers.Select(a => a.Id).Distinct().Count() != file.Answers.Count) errors.Add("a question is answered more than once");
        return errors.Count > 0 ? new(null, errors) : new(file, []);
    }

    /// <summary>A discussion's hindsight: score 1..10, a non-empty reason and notes.</summary>
    public static Validated<DiscussHindsightFile> DiscussHindsight(string text)
    {
        if (Parse(text, out var err) is not JsonObject root) return new(null, [err ?? "expected {\"score\", \"reason\", \"notes\"}"]);
        var errors = new List<string>();
        var score = 0;
        if (root["score"] is JsonValue sv && sv.TryGetValue<int>(out var s) && s is >= State.Hindsight.Min and <= State.Hindsight.Max) score = s;
        else errors.Add($"score must be a whole number from {State.Hindsight.Min} to {State.Hindsight.Max} (got {Show(root["score"])})");
        var reason = Str(root, "reason")?.Trim() ?? "";
        if (reason.Length == 0) errors.Add("reason must be a non-empty string (Markdown): why this score");
        var notes = Str(root, "notes")?.Trim() ?? "";
        if (notes.Length == 0) errors.Add("notes must be a non-empty string (Markdown): what BobarCode should change, or \"Nothing to change.\"");
        return errors.Count > 0 ? new(null, errors) : new(new DiscussHindsightFile { Score = score, Reason = reason, Notes = notes }, []);
    }

    private static JsonNode? Parse(string text, out string? error)
    {
        error = null;
        try { return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, AllowDuplicateProperties = false }); }
        catch (JsonException ex) { error = $"not valid JSON: {ex.Message}"; return null; }
    }

    /// <summary>Lenient JSON extraction from a model's final text: strips a code fence or surrounding prose.</summary>
    public static JsonNode? ParseModelJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var whole = text.Trim();
        // The text as is first: a JSON string value may itself contain a code fence (a discussion's Markdown solution).
        var t = whole;
        var fence = t.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var nl = t.IndexOf('\n', fence);
            var end = nl < 0 ? -1 : t.IndexOf("```", nl, StringComparison.Ordinal);
            if (nl >= 0 && end > nl) t = t[(nl + 1)..end].Trim();
        }
        foreach (var candidate in new[] { whole, t, Braces(t), Braces(whole) })
        {
            if (candidate is null) continue;
            try { return JsonNode.Parse(candidate); }
            catch (JsonException) { }
        }
        return null;

        static string? Braces(string s)
        {
            var a = s.IndexOf('{');
            var b = s.LastIndexOf('}');
            return a >= 0 && b > a ? s[a..(b + 1)] : null;
        }
    }

    private static string? Str(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Show(JsonNode? n) => n is null ? "nothing" : n.ToJsonString();
}

/// <summary>Orders "R2.10" after "R2.9" (and "E.10" after "E.9", "P10" after "P9").</summary>
public sealed class IdComparer : IComparer<string>
{
    public static readonly IdComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var (xr, xn) = Parse(x);
        var (yr, yn) = Parse(y);
        var c = xr.CompareTo(yr);
        if (c != 0) return c;
        c = xn.CompareTo(yn);
        return c != 0 ? c : string.CompareOrdinal(x, y);
    }

    private static (int, int) Parse(string? id)
    {
        if (id is null || id.Length < 2 || !char.IsAsciiLetterUpper(id[0])) return (int.MaxValue, int.MaxValue);
        var dot = id.IndexOf('.');
        // "P3": a number with no round; "E.3": an exploration's, no round either.
        if (dot < 0) return int.TryParse(id[1..], out var p) ? (0, p) : (int.MaxValue, int.MaxValue);
        if (dot == 1) return int.TryParse(id[2..], out var e) ? (0, e) : (int.MaxValue, int.MaxValue);
        return (int.TryParse(id[1..dot], out var r) ? r : int.MaxValue, int.TryParse(id[(dot + 1)..], out var n) ? n : int.MaxValue);
    }
}
