using System.Text;
using Bobar.Core;
using Bobar.Core.IO;
using Bobar.Core.Reviewers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bobar.App.ViewModels;

/// <summary>The Details modal: raw reviewer activity per round, from reviewer.jsonl, tailing live.</summary>
public sealed partial class DetailsViewModel(RunPaths run) : ObservableObject
{
    private string _stamp = "";

    public string Title => $"Reviewer activity · {run.RunId}";

    [ObservableProperty] public partial string Text { get; set; } = "";

    /// <summary>True when the text changed.</summary>
    public bool Refresh()
    {
        var rounds = Enumerable.Range(1, 1000)
            .Select(run.Round)
            .TakeWhile(r => Directory.Exists(r.Dir))
            .ToList();
        var stamp = string.Join("|", rounds.Select(r =>
        {
            var f = new FileInfo(r.ReviewerLog);
            return f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "-";
        }));
        if (stamp == _stamp) return false;
        _stamp = stamp;

        var sb = new StringBuilder();
        foreach (var r in rounds)
        {
            sb.Append($"═══════════ Round {r.Number} ═══════════\n");
            var text = RunFiles.ReadShared(r.ReviewerLog);
            sb.Append(string.IsNullOrEmpty(text) ? "(no reviewer activity yet)\n" : ReviewerLogFormatter.Render(text));
            sb.Append('\n');
        }
        Text = rounds.Count == 0 ? "(no rounds yet)" : sb.ToString();
        return true;
    }
}
