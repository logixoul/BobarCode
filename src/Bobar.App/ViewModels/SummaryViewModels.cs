using System.Collections.ObjectModel;
using Bobar.Core.Model;
using Bobar.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bobar.App.ViewModels;

/// <summary>A severity chip; the bools pick its colour class.</summary>
public sealed record ChipViewModel(string Text, bool IsBug, bool IsRisk, bool IsDesign, bool IsNit)
{
    public static ChipViewModel From(SeverityChip c) => new(c.Text, c.Severity == Severity.Bug, c.Severity == Severity.Risk, c.Severity == Severity.Design, c.Severity == Severity.Nit);
}

/// <summary>One finding in an expanded round.</summary>
public sealed record FindingViewModel(string Id, string Severity, string Title, string Action, bool IsFixed, bool IsRejected, bool IsBug, bool IsRisk, bool IsDesign, bool IsNit)
{
    public static FindingViewModel From(FindingRow f) => new(f.Id, f.Severity.Name(), f.Title, f.Action ?? "",
        f.Action == "fixed", f.Action == "rejected",
        f.Severity == Core.Model.Severity.Bug, f.Severity == Core.Model.Severity.Risk, f.Severity == Core.Model.Severity.Design, f.Severity == Core.Model.Severity.Nit);
}

/// <summary>A finding the author's hindsight regrets its call on; click to expand the finding itself.</summary>
public sealed partial class HindsightRowViewModel(HindsightRow r) : ObservableObject
{
    public string Id { get; } = r.Id;
    public string Severity { get; } = r.Severity?.Name() ?? "";
    public string Title { get; } = r.Title.Length > 0 ? r.Title : "(finding not found)";
    public string Why { get; } = r.Why;
    public string Explanation { get; } = r.Explanation;
    public string Refs { get; } = r.Refs;
    public string AuthorReasoning { get; } = r.AuthorReasoning;
    public string UserDecision { get; } = r.UserDecision;
    public bool HasRefs => Refs.Length > 0;
    public bool HasAuthorReasoning => AuthorReasoning.Length > 0;
    public bool HasUserDecision => UserDecision.Length > 0;
    public bool CanExpand => Explanation.Length > 0 || HasAuthorReasoning || HasUserDecision;
    public bool IsBug { get; } = r.Severity == Core.Model.Severity.Bug;
    public bool IsRisk { get; } = r.Severity == Core.Model.Severity.Risk;
    public bool IsDesign { get; } = r.Severity == Core.Model.Severity.Design;
    public bool IsNit { get; } = r.Severity == Core.Model.Severity.Nit;

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public static HindsightRowViewModel From(HindsightRow r) => new(r);
}

/// <summary>The Summary tab's Hindsight section.</summary>
public sealed partial class HindsightViewModel : ObservableObject
{
    [ObservableProperty] public partial bool IsVisible { get; set; }
    [ObservableProperty] public partial bool HasHindsight { get; set; }
    [ObservableProperty] public partial string Rating { get; set; } = "";
    [ObservableProperty] public partial string Anchor { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<HindsightRowViewModel> AcceptedWrongly { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<HindsightRowViewModel> RejectedWrongly { get; set; } = [];
    [ObservableProperty] public partial bool HasAcceptedWrongly { get; set; }
    [ObservableProperty] public partial bool HasRejectedWrongly { get; set; }
    [ObservableProperty] public partial string Notes { get; set; } = "";

    /// <param name="wanted">The run finished in a way that calls for a hindsight (not cancelled).</param>
    public void Update(bool wanted, HindsightView? v)
    {
        IsVisible = wanted || v is not null;
        HasHindsight = v is not null;
        Rating = v is null ? "" : Hindsight.Rating(v.Usefulness);
        Anchor = v?.Anchor ?? "";
        AcceptedWrongly = v?.AcceptedWrongly.Select(HindsightRowViewModel.From).ToList() ?? [];
        RejectedWrongly = v?.RejectedWrongly.Select(HindsightRowViewModel.From).ToList() ?? [];
        HasAcceptedWrongly = AcceptedWrongly.Count > 0;
        HasRejectedWrongly = RejectedWrongly.Count > 0;
        Notes = v?.Notes ?? "";
    }
}

/// <summary>One round of the Summary table. Updated in place each refresh, so expansion and focus survive.</summary>
public sealed partial class RoundRowViewModel(int number) : ObservableObject
{
    public int Number { get; } = number;
    public string NumberText { get; } = number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty] public partial IReadOnlyList<ChipViewModel> Chips { get; set; } = [];
    [ObservableProperty] public partial string ReviewerNote { get; set; } = "";
    [ObservableProperty] public partial string FixText { get; set; } = "";
    [ObservableProperty] public partial string RejectText { get; set; } = "";
    [ObservableProperty] public partial string AuthorNote { get; set; } = "";
    [ObservableProperty] public partial string Time { get; set; } = "";
    [ObservableProperty] public partial string Cost { get; set; } = "";
    [ObservableProperty] public partial bool IsLive { get; set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanExpand))] public partial IReadOnlyList<FindingViewModel> Findings { get; set; } = [];

    public bool CanExpand => Findings.Count > 0;

    public void Update(RoundRow r)
    {
        var chips = r.Chips.Select(ChipViewModel.From).ToList();
        if (!chips.SequenceEqual(Chips)) Chips = chips;
        var findings = r.Findings.Select(FindingViewModel.From).ToList();
        if (!findings.SequenceEqual(Findings)) Findings = findings;
        ReviewerNote = r.ReviewerNote;
        FixText = r.Fix > 0 ? $"fix {r.Fix}" : "";
        RejectText = r.Reject > 0 ? $"reject {r.Reject}" : "";
        AuthorNote = r.AuthorNote;
        Time = r.Time;
        Cost = r.Cost;
        IsLive = r.State == RowState.Reviewing;
        if (!CanExpand) IsExpanded = false;
    }
}

/// <summary>The Summary tab's table: rounds, the decisions row, totals and the outcome.</summary>
public sealed partial class SummaryTableViewModel : ObservableObject
{
    public ObservableCollection<RoundRowViewModel> Rounds { get; } = [];

    [ObservableProperty] public partial bool HasRounds { get; set; }
    [ObservableProperty] public partial bool HasDecision { get; set; }
    [ObservableProperty] public partial string DecisionText { get; set; } = "";
    [ObservableProperty] public partial string DecisionNote { get; set; } = "";
    [ObservableProperty] public partial string DecisionTime { get; set; } = "";
    [ObservableProperty] public partial string TotalFindings { get; set; } = "";
    [ObservableProperty] public partial string TotalAuthor { get; set; } = "";
    [ObservableProperty] public partial string TotalTime { get; set; } = "";
    [ObservableProperty] public partial string TotalCost { get; set; } = "";
    [ObservableProperty] public partial string Outcome { get; set; } = "";
    [ObservableProperty] public partial bool HasOutcome { get; set; }
    [ObservableProperty] public partial bool OutcomeIsGood { get; set; }
    [ObservableProperty] public partial bool OutcomeIsError { get; set; }

    public void Update(SummaryTableModel m)
    {
        for (var i = 0; i < m.Rounds.Count; i++)
        {
            if (i >= Rounds.Count || Rounds[i].Number != m.Rounds[i].Number)
            {
                while (Rounds.Count > i) Rounds.RemoveAt(Rounds.Count - 1);
                Rounds.Add(new RoundRowViewModel(m.Rounds[i].Number));
            }
            Rounds[i].Update(m.Rounds[i]);
        }
        while (Rounds.Count > m.Rounds.Count) Rounds.RemoveAt(Rounds.Count - 1);
        HasRounds = Rounds.Count > 0;

        HasDecision = m.Decision is not null;
        DecisionText = m.Decision?.Text ?? "";
        DecisionNote = m.Decision?.Note ?? "";
        DecisionTime = m.Decision?.Time ?? "";

        TotalFindings = m.Total.Findings;
        TotalAuthor = m.Total.Author;
        TotalTime = m.Total.Time;
        TotalCost = m.Total.Cost;

        Outcome = m.Outcome;
        HasOutcome = m.Outcome.Length > 0;
        OutcomeIsGood = m.OutcomeKind is Core.Model.Outcome.Clean or Core.Model.Outcome.Cap or Core.Model.Outcome.Decided;
        OutcomeIsError = m.OutcomeKind is Core.Model.Outcome.Error;
    }
}
