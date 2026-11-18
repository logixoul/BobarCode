using System.Collections.ObjectModel;
using System.Text;
using Bobar.Core;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;
using Bobar.Core.Runs;
using Bobar.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bobar.App.ViewModels;

/// <summary>
/// The decisions panel: one card per finding the author rejected in the last round. Submit is
/// enabled once every card has a choice; it writes decisions.json and appends decisions_submitted.
/// </summary>
public sealed partial class DecisionsViewModel : ObservableObject
{
    private readonly RunPaths _run;
    private readonly Func<Redactor> _redactor;
    private readonly Action _afterSubmit;

    public string Key { get; }
    public ObservableCollection<DecisionCardViewModel> Cards { get; } = [];
    public string Heading { get; }

    [ObservableProperty] public partial string OverallComment { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SubmitCommand))] public partial bool AllChosen { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SubmitCommand))] public partial bool Busy { get; set; }

    public DecisionsViewModel(RunPaths run, RunState s, Func<Redactor> redactor, Action afterSubmit)
    {
        _run = run;
        _redactor = redactor;
        _afterSubmit = afterSubmit;
        var aw = s.Awaiting!;
        Key = KeyOf(run, aw);
        Heading = aw.Reason == AwaitReason.Deadlock
            ? $"The reviewer and the author disagree on {Summary.Plural(aw.Ids.Count, "finding")}. Decide each one:"
            : $"The round cap ({s.MaxRounds}) was reached with {Summary.Plural(aw.Ids.Count, "rejected finding")}. Decide each one:";
        var rounds = RunReader.LoadRounds(run, s);
        foreach (var id in aw.Ids)
        {
            var thread = RunReader.Thread(rounds, id);
            if (thread.Count == 0) continue;
            var card = new DecisionCardViewModel(thread);
            card.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(DecisionCardViewModel.IsFix) or nameof(DecisionCardViewModel.IsDismiss)) UpdateAllChosen();
            };
            Cards.Add(card);
        }
        UpdateAllChosen();
    }

    public static string KeyOf(RunPaths run, AwaitingDecisions aw) => $"{run.RunId}|{aw.Round}|{string.Join(",", aw.Ids)}";

    private void UpdateAllChosen() => AllChosen = Cards.Count > 0 && Cards.All(c => c.IsFix || c.IsDismiss);

    private bool CanSubmit() => AllChosen && !Busy;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        Busy = true;
        Error = "";
        try
        {
            var file = new DecisionsFile
            {
                Decisions = Cards.Select(c => new DecisionItem
                {
                    Id = c.Id,
                    Decision = c.IsFix ? UserDecision.Fix : UserDecision.Dismiss,
                    Comment = string.IsNullOrWhiteSpace(c.Comment) ? null : c.Comment.Trim(),
                }).ToList(),
                Comment = string.IsNullOrWhiteSpace(OverallComment) ? null : OverallComment.Trim(),
            };
            var err = await RunActions.SubmitDecisionsAsync(_run, file, _redactor(), TimeProvider.System);
            if (err is not null) Error = $"Could not submit: {err}";
            else _afterSubmit();
        }
        catch (IOException ex)
        {
            Error = $"Could not write decisions.json: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }
}

/// <summary>One rejected finding, with its thread across rounds and a fix/dismiss choice.</summary>
public sealed partial class DecisionCardViewModel : ObservableObject
{
    public string Id { get; }
    public string Header { get; }
    public string Title { get; }
    public bool IsBug { get; }
    public bool IsRisk { get; }
    public bool IsDesign { get; }
    public bool IsNit { get; }
    public string Severity { get; }
    public string Explanation { get; }
    public string Refs { get; }
    public bool HasRefs => Refs.Length > 0;
    public string AuthorReasoning { get; }
    public string Thread { get; }
    public bool HasThread => Thread.Length > 0;

    [ObservableProperty] public partial bool IsFix { get; set; }
    [ObservableProperty] public partial bool IsDismiss { get; set; }
    [ObservableProperty] public partial bool ShowComment { get; set; }
    [ObservableProperty] public partial string Comment { get; set; } = "";

    public DecisionCardViewModel(List<(StoredFinding Finding, AuthorResponseItem? Response)> thread)
    {
        var (f, resp) = thread[^1];
        Id = f.Id;
        Severity = f.Severity.Name();
        Header = $"{f.Id}  {f.Title}";
        Title = $"{f.Id} · {f.Title}";
        IsBug = f.Severity == Core.Model.Severity.Bug;
        IsRisk = f.Severity == Core.Model.Severity.Risk;
        IsDesign = f.Severity == Core.Model.Severity.Design;
        IsNit = f.Severity == Core.Model.Severity.Nit;
        Explanation = f.Explanation;
        Refs = string.Join(", ", f.Refs) + string.Concat(f.Warnings.Select(w => $"   ({w})"));
        AuthorReasoning = resp?.Reasoning ?? "(no reasoning recorded)";
        var sb = new StringBuilder();
        foreach (var (pf, pr) in thread.Take(thread.Count - 1))
        {
            sb.Append($"{pf.Id} [{pf.Severity.Name()}] {pf.Title}\n{pf.Explanation.Trim()}\n");
            if (pr is not null) sb.Append($"→ Author: {(pr.Action == AuthorAction.Fix ? "fix" : "reject")}. {pr.Reasoning}\n");
            sb.Append('\n');
        }
        Thread = sb.ToString().TrimEnd();
    }

    partial void OnIsFixChanged(bool value)
    {
        if (value) IsDismiss = false;
    }

    partial void OnIsDismissChanged(bool value)
    {
        if (value) IsFix = false;
    }

    [RelayCommand]
    private void AddComment() => ShowComment = true;
}
