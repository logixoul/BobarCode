using System.Collections.ObjectModel;
using System.Text;
using Bobar.Core;
using Bobar.Core.IO;
using Bobar.Core.Reviewers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bobar.App.ViewModels;

/// <summary>
/// Tails reviewer.jsonl files into entries. They are diffed by value on each refresh (the logs are append-only),
/// so unchanged ones keep their view model and with it their expanded state.
/// </summary>
public sealed class ActivityFeed
{
    private string? _stamp;
    private List<object> _keys = [];

    /// <summary><see cref="RoundHeader"/>s and <see cref="ActivityEntryViewModel"/>s, in order.</summary>
    public ObservableCollection<object> Entries { get; } = [];

    /// <summary>Re-reads the logs if any changed; a log with a title gets a header. True when the entries changed.</summary>
    public bool Refresh(IReadOnlyList<(string? Title, string Log)> logs, string noActivity, string noLogs)
    {
        var stamp = string.Join("|", logs.Select(r =>
        {
            var f = new FileInfo(r.Log);
            return f.Exists ? $"{r.Log}:{f.Length}:{f.LastWriteTimeUtc.Ticks}" : $"{r.Log}:-";
        }));
        if (stamp == _stamp) return false;
        _stamp = stamp;

        var keys = new List<object>();
        foreach (var r in logs)
        {
            if (r.Title is not null) keys.Add(new RoundHeader(r.Title));
            var text = RunFiles.ReadShared(r.Log);
            if (string.IsNullOrEmpty(text)) keys.Add(new ReviewerActivity(ActivityKind.Info, noActivity));
            else keys.AddRange(ReviewerLogFormatter.Parse(text));
        }
        if (logs.Count == 0) keys.Add(new ReviewerActivity(ActivityKind.Info, noLogs));

        for (var i = 0; i < keys.Count; i++)
        {
            if (i < _keys.Count && Equals(_keys[i], keys[i])) continue;
            var item = keys[i] is ReviewerActivity a ? new ActivityEntryViewModel(a) : keys[i];
            if (i < Entries.Count) Entries[i] = item;
            else Entries.Add(item);
        }
        while (Entries.Count > keys.Count) Entries.RemoveAt(Entries.Count - 1);
        _keys = keys;
        return true;
    }

    /// <summary>Forgets everything, e.g. before showing another log.</summary>
    public void Clear()
    {
        _stamp = null;
        _keys = [];
        Entries.Clear();
    }

    public void SetExpanded(bool value)
    {
        foreach (var e in Entries.OfType<ActivityEntryViewModel>().Where(e => e.CanExpand)) e.IsExpanded = value;
    }

    /// <summary>The logs as plain text with details expanded, for the clipboard.</summary>
    public static string PlainText(IEnumerable<(string? Title, string Log)> logs)
    {
        var sb = new StringBuilder();
        foreach (var r in logs)
        {
            if (r.Title is not null) sb.Append($"═══════════ {r.Title} ═══════════\n");
            sb.Append(ReviewerLogFormatter.Render(RunFiles.ReadShared(r.Log) ?? "")).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>The Details modal: reviewer activity per round, from reviewer.jsonl, tailing live.</summary>
public sealed partial class DetailsViewModel(RunPaths run) : ObservableObject
{
    private readonly ActivityFeed _feed = new();

    public string Title => $"Reviewer activity · {run.RunId}";

    public ObservableCollection<object> Entries => _feed.Entries;

    /// <summary>True when the entries changed.</summary>
    public bool Refresh() => _feed.Refresh(Logs(), "(no reviewer activity yet)", "(no rounds yet)");

    /// <summary>Everything, as plain text with details expanded, for the clipboard.</summary>
    public string PlainText() => ActivityFeed.PlainText(Logs());

    [RelayCommand] private void ExpandAll() => _feed.SetExpanded(true);
    [RelayCommand] private void CollapseAll() => _feed.SetExpanded(false);

    /// <summary>Each round's reviewer.jsonl; in a discussion, one per participant (round-k/&lt;shorthand&gt;/reviewer.jsonl).</summary>
    private List<(string? Title, string Log)> Logs()
    {
        var logs = new List<(string?, string)>();
        foreach (var r in Enumerable.Range(1, 1000).Select(run.Round).TakeWhile(r => Directory.Exists(r.Dir)))
        {
            var discussers = Directory.EnumerateDirectories(r.Dir).Select(Path.GetFileName).OfType<string>()
                .Where(sh => File.Exists(r.Discusser(sh).ReviewerLog)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (discussers.Count == 0) logs.Add(($"Round {r.Number}", r.ReviewerLog));
            else logs.AddRange(discussers.Select(sh => ((string?)$"Round {r.Number} · {sh}", r.Discusser(sh).ReviewerLog)));
        }
        return logs;
    }
}

public sealed record RoundHeader(string Title);

/// <summary>One activity line; when it has a detail, a click on it shows or hides that detail.</summary>
public sealed partial class ActivityEntryViewModel(ReviewerActivity a) : ObservableObject
{
    public string Text => a.Text;
    public string? Detail => a.Detail;
    public bool CanExpand => a.Detail is not null;

    public bool IsText => a.Kind == ActivityKind.Text;
    public bool IsThinking => a.Kind == ActivityKind.Thinking;
    public bool IsToolCall => a.Kind == ActivityKind.ToolCall;
    public bool IsToolResult => a.Kind == ActivityKind.ToolResult;
    public bool IsError => a.Kind == ActivityKind.Error;
    public bool IsRetry => a.Kind == ActivityKind.Retry;
    public bool IsInfo => a.Kind == ActivityKind.Info;

    [ObservableProperty] public partial bool IsExpanded { get; set; }
}
