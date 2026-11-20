using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bobar.Core.Model;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    AllowOutOfOrderMetadataProperties = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(RunEvent))]
[JsonSerializable(typeof(ReviewFile))]
[JsonSerializable(typeof(ResponseFile))]
[JsonSerializable(typeof(DecisionsFile))]
[JsonSerializable(typeof(HindsightFile))]
[JsonSerializable(typeof(LedgerFile))]
[JsonSerializable(typeof(List<string>))]
public partial class BobarJsonContext : JsonSerializerContext;

public static class BobarJson
{
    /// <summary>Single-line JSON (events.jsonl).</summary>
    public static readonly BobarJsonContext Compact = new(new JsonSerializerOptions(BobarJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Human-readable JSON files.</summary>
    public static readonly BobarJsonContext Indented = new(new JsonSerializerOptions(BobarJsonContext.Default.Options)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
