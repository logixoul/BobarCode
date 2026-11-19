using System.Collections.ObjectModel;
using System.Text;
using Bobar.Core;
using Bobar.Core.IO;
using Bobar.Core.Reviewers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bobar.App.ViewModels;

/// <summary>
/// The Details modal: reviewer activity per round, from reviewer.jsonl, tailing live. Entries are
/// diffed by value on each refresh (the logs are append-only), so unchanged ones keep their view
/// model and with it their expanded state.
/// </summary>
public sealed partial class DetailsViewModel(RunPaths run) : ObservableObject
{
    private string? _stamp;
    private List<object> _keys = [];

    public string Title => $"Reviewer activity · {run.RunId}";

    /// <summary><see cref="RoundHeader"/>s and <see cref="ActivityEntryViewModel"/>s, in order.</summary>
    public ObservableCollection<object> Entries { get; } = [];

    /// <summary>True when the entries changed.</summary>
    public bool Refresh()
    {
        var rounds = Rounds();
        var stamp = string.Join("|", rounds.Select(r =>
        {
            var f = new FileInfo(r.ReviewerLog);
            return f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "-";
        }));
        if (stamp == _stamp) return false;
        _stamp = stamp;

        var keys = new List<object>();
        foreach (var r in rounds)
        {
            keys.Add(new RoundHeader($"Round {r.Number}"));
            var text = RunFiles.ReadShared(r.ReviewerLog);
            if (string.IsNullOrEmpty(text)) keys.Add(new ReviewerActivity(ActivityKind.Info, "(no reviewer activity yet)"));
            else keys.AddRange(ReviewerLogFormatter.Parse(text));
        }
        if (rounds.Count == 0) keys.Add(new ReviewerActivity(ActivityKind.Info, "(no rounds yet)"));

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

    /// <summary>Everything, as plain text with details expanded, for the clipboard.</summary>
    public string PlainText()
    {
        var sb = new StringBuilder();
        foreach (var r in Rounds())
        {
            sb.Append($"═══════════ Round {r.Number} ═══════════\n");
            sb.Append(ReviewerLogFormatter.Render(RunFiles.ReadShared(r.ReviewerLog) ?? "")).Append('\n');
        }
        return sb.ToString();
    }

    [RelayCommand] private void ExpandAll() => SetExpanded(true);
    [RelayCommand] private void CollapseAll() => SetExpanded(false);

    private void SetExpanded(bool value)
    {
        foreach (var e in Entries.OfType<ActivityEntryViewModel>().Where(e => e.CanExpand)) e.IsExpanded = value;
    }

    private List<RoundPaths> Rounds() => Enumerable.Range(1, 1000)
        .Select(run.Round)
        .TakeWhile(r => Directory.Exists(r.Dir))
        .ToList();
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
