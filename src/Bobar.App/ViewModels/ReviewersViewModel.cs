using System.Globalization;
using Bobar.Core;
using Bobar.Core.Runs;
using Bobar.Core.State;

namespace Bobar.App.ViewModels;

/// <summary>One hindsight in the Notes tab.</summary>
public sealed record HindsightNoteViewModel(string Heading, string Rating, string Anchor, string Regrets, string Notes);

/// <summary>
/// The Reviewers window: per reviewer, the author's average usefulness rating next to cost and time,
/// and every hindsight's notes, newest first. A snapshot taken when the window opens.
/// </summary>
public sealed class ReviewersViewModel
{
    public IReadOnlyList<ReviewerStatsRow> Rows { get; }
    public IReadOnlyList<HindsightNoteViewModel> Notes { get; }
    public bool HasRows => Rows.Count > 0;
    public bool HasNotes => Notes.Count > 0;

    public ReviewersViewModel(IReadOnlyList<(RunPaths Paths, RunState State)> runs)
    {
        Rows = ReviewerStats.Build(runs.Select(r => r.State));
        var notes = new List<HindsightNoteViewModel>();
        foreach (var (paths, s) in runs.Where(r => r.State.Hindsight is not null).OrderByDescending(r => r.State.Hindsight!.T))
        {
            if (RunReader.LoadHindsight(paths) is not { } h) continue;
            var when = s.StartedAt?.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture) ?? s.RunId;
            var reviewer = s.Started is { } st ? $"{st.Reviewer} ({st.ReviewerConfig.ModelShort})" : "?";
            var regrets = $"{h.AcceptedWrongly.Count} accepted wrongly · {h.RejectedWrongly.Count} rejected wrongly";
            notes.Add(new HindsightNoteViewModel($"{s.RepoName} · {when} · {reviewer}", Hindsight.Rating(h.Usefulness), Hindsight.Anchor(h.Usefulness), regrets, h.Notes));
        }
        Notes = notes;
    }
}
