using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Bobar.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bobar.App.ViewModels;

/// <summary>One earlier day in a card's history menu.</summary>
public sealed record OlderDay(string Group, string Label, IReadOnlyList<RunItemViewModel> Runs);

/// <summary>
/// One project's card in the sidebar: today's runs (plus any older run that still needs the user or is live), and
/// a history button on its header whose menu holds every earlier day. Expanded at launch; collapsing it isn't remembered.
/// </summary>
public sealed partial class ProjectCardViewModel(string name) : ObservableObject
{
    private DateOnly _today;
    private IBrush? _dot;

    public string Name { get; } = name;
    /// <summary>The runs listed in the card, newest first.</summary>
    public ObservableCollection<RunItemViewModel> Shown { get; } = [];
    /// <summary>The rest, newest first: only reachable through the history menu.</summary>
    public IReadOnlyList<RunItemViewModel> Older { get; private set; } = [];

    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
    [ObservableProperty] public partial string TodayText { get; set; } = "";
    [ObservableProperty] public partial bool HasNothingShown { get; set; }
    [ObservableProperty] public partial bool HasOlder { get; set; }
    [ObservableProperty] public partial string OlderTip { get; set; } = "";
    /// <summary>The header's dot while collapsed: orange if a listed run needs the user, blue if one is live.</summary>
    [ObservableProperty] public partial IBrush? CollapsedDot { get; set; }
    /// <summary>The selected run when it lives in the history menu: listed under the card's runs while it's selected.</summary>
    [ObservableProperty] public partial RunItemViewModel? PickedOlder { get; set; }
    [ObservableProperty] public partial string PickedOlderDay { get; set; } = "";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => CollapsedDot = value ? null : _dot;

    public void Update(IReadOnlyList<RunItemViewModel> newestFirst, DateOnly today, RunItemViewModel? selected)
    {
        _today = today;
        var shown = newestFirst.Where(r => r.Day is not { } d || d >= today || r.NeedsYou || r.IsLive).ToList();
        if (!shown.SequenceEqual(Shown))
        {
            Shown.Clear();
            foreach (var r in shown) Shown.Add(r);
        }
        Older = newestFirst.Where(r => !shown.Contains(r)).ToList();
        var todays = shown.Count(r => r.Day is not { } d || d >= today);
        TodayText = todays > 0 ? $"{todays} today" : "";
        HasNothingShown = shown.Count == 0;
        HasOlder = Older.Count > 0;
        OlderTip = $"{Older.Count} older run{(Older.Count == 1 ? "" : "s")}";
        _dot = shown.Any(r => r.NeedsYou) ? RunItemViewModel.Badge(RunStatusKind.AwaitingDecisions)
            : shown.Any(r => r.IsLive) ? RunItemViewModel.Badge(RunStatusKind.Running) : null;
        CollapsedDot = IsExpanded ? null : _dot;
        PickedOlder = selected is not null && Older.Contains(selected) ? selected : null;
        PickedOlderDay = PickedOlder?.Day is { } pd ? DayLabel(pd, today) : "";
    }

    /// <summary>The history menu's days, newest first, each under This week, Last week or Earlier (weeks start on Monday).</summary>
    public List<OlderDay> OlderByDay()
    {
        var weekStart = _today.AddDays(-(((int)_today.DayOfWeek + 6) % 7));
        return Older.GroupBy(r => r.Day ?? _today)
            .Select(g => new OlderDay(g.Key >= weekStart ? "This week" : g.Key >= weekStart.AddDays(-7) ? "Last week" : "Earlier",
                DayLabel(g.Key, _today), g.ToList()))
            .ToList();
    }

    private static string DayLabel(DateOnly d, DateOnly today) =>
        d == today.AddDays(-1) ? "Yesterday"
        : d.ToString(d.Year == today.Year ? "ddd d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
}
