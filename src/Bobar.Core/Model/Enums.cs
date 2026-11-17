using System.Text.Json.Serialization;

namespace Bobar.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    [JsonStringEnumMemberName("bug")] Bug,
    [JsonStringEnumMemberName("risk")] Risk,
    [JsonStringEnumMemberName("design")] Design,
    [JsonStringEnumMemberName("nit")] Nit,
}

/// <summary>How a finding relates to earlier rounds (computed by the CLI, §5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FindingOrigin>))]
public enum FindingOrigin
{
    /// <summary>No previousId.</summary>
    [JsonStringEnumMemberName("new")] New,
    /// <summary>previousId names a finding the author rejected in the previous round.</summary>
    [JsonStringEnumMemberName("maintained")] Maintained,
    /// <summary>previousId names some other earlier finding (e.g. one the author said was fixed).</summary>
    [JsonStringEnumMemberName("repeated")] Repeated,
}

[JsonConverter(typeof(JsonStringEnumConverter<AuthorAction>))]
public enum AuthorAction
{
    [JsonStringEnumMemberName("fix")] Fix,
    [JsonStringEnumMemberName("reject")] Reject,
}

[JsonConverter(typeof(JsonStringEnumConverter<UserDecision>))]
public enum UserDecision
{
    [JsonStringEnumMemberName("fix")] Fix,
    [JsonStringEnumMemberName("dismiss")] Dismiss,
}

[JsonConverter(typeof(JsonStringEnumConverter<Outcome>))]
public enum Outcome
{
    [JsonStringEnumMemberName("clean")] Clean,
    [JsonStringEnumMemberName("cap")] Cap,
    [JsonStringEnumMemberName("decided")] Decided,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("error")] Error,
}

[JsonConverter(typeof(JsonStringEnumConverter<AwaitReason>))]
public enum AwaitReason
{
    [JsonStringEnumMemberName("deadlock")] Deadlock,
    [JsonStringEnumMemberName("cap")] Cap,
}

public static class EnumText
{
    public static string Name(this Severity s) => s switch
    {
        Severity.Bug => "bug",
        Severity.Risk => "risk",
        Severity.Design => "design",
        _ => "nit",
    };

    public static string Name(this FindingOrigin o) => o switch
    {
        FindingOrigin.Maintained => "maintained",
        FindingOrigin.Repeated => "repeated",
        _ => "new",
    };

    public static bool TryParseSeverity(string? s, out Severity sev)
    {
        switch (s)
        {
            case "bug": sev = Severity.Bug; return true;
            case "risk": sev = Severity.Risk; return true;
            case "design": sev = Severity.Design; return true;
            case "nit": sev = Severity.Nit; return true;
            default: sev = default; return false;
        }
    }
}
