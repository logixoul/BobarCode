using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Bobar.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bobar.App.ViewModels;

/// <summary>How far back the sidebar's cards list runs, counted back from now (not from midnight); picked by the
/// one dropdown above them.</summary>
public enum RunRange { Day, Week, Month, AllTime }

/// <summary>A day's subheading inside a card (every range but the past 24 hours).</summary>
public sealed record DayHeader(string Label);

/// <summary>
/// One project's card in the sidebar: its runs in the chosen range (newest first, under day subheadings past 24 hours),
/// plus, in the 24-hour range, any older run that still needs the user or is live. Expanded at launch; collapsing isn't remembered.
/// The header says "Working…" with a ring while runs are live: their rounds done over the most they may run.
/// </summary>
public sealed partial class ProjectCardViewModel(string name) : ObservableObject
{
    /// <summary>Past the 24-hour range, a card lists this many runs before "Show N more".</summary>
    public const int Cap = 8;

    private IBrush? _dot;
    private RunRange _range;

    public string Name { get; } = name;
    /// <summary>Day headers and runs, in display order.</summary>
    public ObservableCollection<object> Items { get; } = [];

    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
    [ObservableProperty] public partial string CountText { get; set; } = "";
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    /// <summary>"Show 12 more", or empty when everything in range is listed.</summary>
    [ObservableProperty] public partial string MoreText { get; set; } = "";
    [ObservableProperty] public partial bool ShowAll { get; set; }
    /// <summary>The header's dot while collapsed: a listed run needs the user.</summary>
    [ObservableProperty] public partial IBrush? CollapsedDot { get; set; }
    /// <summary>The selected run when the range leaves it out (opened by id): listed under the card's runs while selected.</summary>
    [ObservableProperty] public partial RunItemViewModel? PickedOlder { get; set; }
    [ObservableProperty] public partial bool IsWorking { get; set; }
    /// <summary>The ring's arc in degrees; 0 hides it (only explorations live: nothing to measure).</summary>
    [ObservableProperty] public partial double ProgressSweep { get; set; }
    [ObservableProperty] public partial bool HasProgress { get; set; }
    [ObservableProperty] public partial string ProgressTip { get; set; } = "";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ShowMore() => ShowAll = true;

    partial void OnIsExpandedChanged(bool value) => CollapsedDot = value ? null : _dot;

    public void Update(IReadOnlyList<RunItemViewModel> newestFirst, DateTimeOffset now, RunRange range, RunItemViewModel? selected)
    {
        if (range != _range) ShowAll = false;
        _range = range;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var from = range switch
        {
            RunRange.Day => now.AddHours(-24),
            RunRange.Week => now.AddDays(-7),
            RunRange.Month => now.AddDays(-30),
            _ => DateTimeOffset.MinValue,
        };
        var inRange = newestFirst.Where(r => r.StartedAt is not { } t || t >= from || (range == RunRange.Day && (r.NeedsYou || r.IsLive))).ToList();
        // Past the cap, a selected run still gets listed (with the runs above it), under its own day.
        var listed = range == RunRange.Day || ShowAll ? inRange
            : inRange.Take(Math.Max(Cap, selected is null ? 0 : inRange.IndexOf(selected) + 1)).ToList();

        var items = new List<object>();
        string? day = null;
        foreach (var r in listed)
        {
            if (range != RunRange.Day && DayLabel(r.Day ?? today, today) is var label && label != day) items.Add(new DayHeader(day = label));
            items.Add(r);
        }
        if (!items.SequenceEqual(Items))
        {
            Items.Clear();
            foreach (var i in items) Items.Add(i);
        }

        CountText = inRange.Count > 0 ? inRange.Count.ToString(CultureInfo.InvariantCulture) : "";
        EmptyText = inRange.Count > 0 ? "" : range switch
        {
            RunRange.Day => "No runs in the past 24 hours",
            RunRange.Week => "No runs in the past 7 days",
            RunRange.Month => "No runs in the past 30 days",
            _ => "No runs",
        };
        MoreText = listed.Count < inRange.Count ? $"Show {inRange.Count - listed.Count} more" : "";
        _dot = inRange.Any(r => r.NeedsYou) ? RunItemViewModel.Badge(RunStatusKind.AwaitingDecisions) : null;
        CollapsedDot = IsExpanded ? null : _dot;
        PickedOlder = selected is not null && newestFirst.Contains(selected) && !listed.Contains(selected) ? selected : null;

        var live = newestFirst.Where(r => r.IsLive).ToList();
        IsWorking = live.Count > 0;
        var measured = live.Where(r => r.RoundsMax > 0).ToList();
        int done = measured.Sum(r => r.RoundsDone), max = measured.Sum(r => r.RoundsMax);
        HasProgress = max > 0;
        // A sliver even at 0%, so the ring reads as a progress ring from the start.
        ProgressSweep = max > 0 ? Math.Max(8, 360.0 * done / max) : 0;
        ProgressTip = !IsWorking ? ""
            : string.Join("\n", live.Select(r => r.RoundsMax > 0 ? $"{r.Name}: {r.RoundsDone} of up to {r.RoundsMax} rounds done" : $"{r.Name}: exploring"))
              + (max > 0 ? $"\n{done} of up to {max} rounds done ({100 * done / max}%)" : "");
    }

    private static string DayLabel(DateOnly d, DateOnly today) =>
        d == today ? "Today"
        : d == today.AddDays(-1) ? "Yesterday"
        : d.ToString(d.Year == today.Year ? "ddd d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
}
