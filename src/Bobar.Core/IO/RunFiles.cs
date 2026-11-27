using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bobar.Core.Model;

namespace Bobar.Core.IO;

/// <summary>All writes into a run dir go through here, and through the redactor.</summary>
public static class RunFiles
{
    /// <summary>Best-effort delete: IO and access errors are ignored.</summary>
    public static void TryDeleteFile(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Best-effort recursive delete: IO and access errors are ignored.</summary>
    public static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteText(string path, string text, Redactor redactor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, redactor.Apply(text), Utf8);
    }

    /// <summary>Temp file + rename, so readers never see half a file.</summary>
    public static void WriteTextAtomic(string path, string text, Redactor redactor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, redactor.Apply(text), Utf8);
        File.Move(tmp, path, overwrite: true);
    }

    public static void AppendLine(string path, string line, Redactor redactor)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        // A writer killed mid-line (cancel kills the CLI's tree) leaves a torn line; start on a fresh one.
        var prefix = "";
        if (fs.Length > 0)
        {
            fs.Seek(-1, SeekOrigin.End);
            if (fs.ReadByte() != '\n') prefix = "\n";
        }
        fs.Seek(0, SeekOrigin.End);
        var bytes = Utf8.GetBytes(prefix + redactor.Apply(line) + "\n");
        fs.Write(bytes);
        fs.Flush(flushToDisk: false);
    }

    /// <summary>Reads a file another process may be appending to.</summary>
    public static string? ReadShared(string path)
    {
        // Missing files are the normal case (e.g. an unanswered round's response.json); checking first keeps
        // first-chance FileNotFoundExceptions out of the debugger console. The catches still cover races.
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, Utf8);
            return r.ReadToEnd();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static T? ReadJson<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        var text = ReadShared(path);
        if (text is null) return null;
        try { return JsonSerializer.Deserialize(text, info); }
        catch (JsonException) { return null; }
    }

    public static void WriteJson<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, Redactor redactor)
        => WriteTextAtomic(path, JsonSerializer.Serialize(value, info) + "\n", redactor);
}

/// <summary>events.jsonl: append-only, one JSON object per line.</summary>
public static class EventLog
{
    public static void Append(RunPaths run, RunEvent ev, Redactor redactor)
        => RunFiles.AppendLine(run.Events, JsonSerializer.Serialize(ev, BobarJson.Compact.RunEvent), redactor);

    /// <summary>Tolerant: skips blank, torn (last line mid-write) and unknown lines.</summary>
    public static List<RunEvent> Read(RunPaths run) => Parse(RunFiles.ReadShared(run.Events) ?? "");

    public static List<RunEvent> Parse(string text)
    {
        var list = new List<RunEvent>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            try
            {
                var ev = JsonSerializer.Deserialize(line, BobarJson.Compact.RunEvent);
                if (ev is not null) list.Add(ev);
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"skipping bad event line: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                Debug.WriteLine($"skipping unknown event type: {ex.Message}");
            }
        }
        return list;
    }
}
