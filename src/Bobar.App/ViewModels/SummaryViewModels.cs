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
