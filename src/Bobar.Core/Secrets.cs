namespace Bobar.Core;

/// <summary>keys.env: NAME=value lines. Values are never printed or logged.</summary>
public sealed class KeysEnv
{
    public IReadOnlyDictionary<string, string> Values { get; }

    public KeysEnv(IReadOnlyDictionary<string, string> values) => Values = values;

    public static KeysEnv Empty { get; } = new(new Dictionary<string, string>());

    public static KeysEnv Load(string file)
    {
        if (!File.Exists(file)) return Empty;
        return Parse(File.ReadAllText(file));
    }

    public static KeysEnv Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var name = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            d[name] = value;
        }
        return new KeysEnv(d);
    }

    public Redactor Redactor() => new(Values.Values);
}

/// <summary>Masks every keys.env value. Everything written to a run dir goes through one of these.</summary>
public sealed class Redactor
{
    public const string Mask = "***";
    /// <summary>Shorter values would mask ordinary text; real keys are much longer.</summary>
    public const int MinSecretLength = 6;

    private readonly string[] _secrets;

    public Redactor(IEnumerable<string> secrets)
    {
        // Longest first, so a secret that contains another is masked whole.
        _secrets = secrets.Where(s => s.Length >= MinSecretLength).Distinct().OrderByDescending(s => s.Length).ToArray();
    }

    public static Redactor None { get; } = new([]);

    public string Apply(string text)
    {
        if (_secrets.Length == 0 || string.IsNullOrEmpty(text)) return text;
        foreach (var s in _secrets)
            if (text.Contains(s, StringComparison.Ordinal)) text = text.Replace(s, Mask, StringComparison.Ordinal);
        return text;
    }
}
