using System.Collections.ObjectModel;
using System.Text;
using Bobar.Core;
using Bobar.Core.IO;
using Bobar.Core.Model;
using Bobar.Core.Runs;
using Bobar.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bobar.App.ViewModels;

/// <summary>One participant in one round; click it to read its solution and messages below the table.</summary>
public sealed partial class DiscussCellViewModel(DiscussCell cell, Action<DiscussCellViewModel> open) : ObservableObject
{
    public DiscussCell Cell { get; } = cell;
    public string Text => Cell.Text;
    public string Tip => Cell.Detail.Length > 0 ? Cell.Detail : Cell.CanOpen ? "Show this solution" : "";
    public bool CanOpen => Cell.CanOpen;
    public bool IsWorking => Cell.State == DiscussCellState.Working;
    public bool IsDropped => Cell.State == DiscussCellState.Dropped;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Open() => open(this);
}

public sealed partial class DiscussRowViewModel(int round) : ObservableObject
{
    public int Round { get; } = round;
    public string RoundText { get; } = round.ToString(System.Globalization.CultureInfo.InvariantCulture);
    [ObservableProperty] public partial IReadOnlyList<DiscussCellViewModel> Cells { get; set; } = [];
}

/// <summary>The right pane's body for a discussion: the question, the rounds table, the selected solution, questions, report.</summary>
public sealed partial class DiscussViewModel(RunPaths run, Func<Redactor> redactor, Action afterSubmit) : ObservableObject
{
    private (string Discusser, int Round)? _selected;
    private int _eventCount = -1;

    public ObservableCollection<DiscussRowViewModel> Rows { get; } = [];

    [ObservableProperty] public partial IReadOnlyList<string> Headers { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<string> Totals { get; set; } = [];
    [ObservableProperty] public partial int Columns { get; set; } = 1;
    [ObservableProperty] public partial string TotalTime { get; set; } = "";
    [ObservableProperty] public partial string TotalCost { get; set; } = "";
    [ObservableProperty] public partial bool HasRows { get; set; }
    [ObservableProperty] public partial string Outcome { get; set; } = "";
    [ObservableProperty] public partial bool HasOutcome { get; set; }
    [ObservableProperty] public partial bool OutcomeIsGood { get; set; }
    [ObservableProperty] public partial bool OutcomeIsError { get; set; }
    [ObservableProperty] public partial string Question { get; set; } = "";

    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial string SelectedTitle { get; set; } = "";
    [ObservableProperty] public partial string SelectedSolution { get; set; } = "";
    [ObservableProperty] public partial string SelectedMessages { get; set; } = "";

    [ObservableProperty] public partial QuestionsViewModel? Questions { get; set; }
    [ObservableProperty] public partial bool ShowQuestions { get; set; }
    [ObservableProperty] public partial string AnswersText { get; set; } = "";
    [ObservableProperty] public partial string Report { get; set; } = "";

    public void Update(RunState s)
    {
        var m = DiscussTableModel.Build(s, DateTimeOffset.Now);
        if (!m.Discussers.SequenceEqual(Headers)) Headers = m.Discussers.ToList();
        Columns = Math.Max(1, m.Discussers.Count);
        for (var i = 0; i < m.Rows.Count; i++)
        {
            if (i >= Rows.Count) Rows.Add(new DiscussRowViewModel(m.Rows[i].Round));
            if (!m.Rows[i].Cells.SequenceEqual(Rows[i].Cells.Select(c => c.Cell)))
                Rows[i].Cells = m.Rows[i].Cells.Select(c => new DiscussCellViewModel(c, Open) { IsSelected = _selected == (c.Discusser, c.Round) }).ToList();
        }
        while (Rows.Count > m.Rows.Count) Rows.RemoveAt(Rows.Count - 1);
        HasRows = Rows.Count > 0;
        if (!m.Totals.SequenceEqual(Totals)) Totals = m.Totals.ToList();
        TotalTime = m.TotalTime;
        TotalCost = m.TotalCost;
        Outcome = m.Outcome;
        HasOutcome = Outcome.Length > 0;
        OutcomeIsGood = m.OutcomeKind == Core.Model.Outcome.Reported;
        OutcomeIsError = m.OutcomeKind == Core.Model.Outcome.Error;

        if (s.Events.Count == _eventCount && (Questions is not null || s.Phase != RunPhase.AwaitingAnswers)) return;
        _eventCount = s.Events.Count;
        if (Question.Length == 0) Question = RunFiles.ReadShared(run.Prompt)?.Trim() ?? "";
        var questions = s.Questions is null ? null : RunFiles.ReadJson(run.Questions, BobarJson.Compact.QuestionsFile);
        var answersWritten = File.Exists(run.Answers);
        if (s.Phase == RunPhase.AwaitingAnswers && questions is not null && !answersWritten) Questions ??= new QuestionsViewModel(run, questions, redactor, afterSubmit);
        else Questions = null;
        ShowQuestions = Questions is not null;
        AnswersText = questions is not null && answersWritten && RunFiles.ReadJson(run.Answers, BobarJson.Compact.AnswersFile) is { } a ? AnswersSummary(questions, a) : "";
        Report = s.Finished?.Outcome == Core.Model.Outcome.Reported ? RunFiles.ReadJson(run.Report, BobarJson.Compact.ReportFile)?.Markdown ?? "" : "";
    }

    private void Open(DiscussCellViewModel cell)
    {
        if (!cell.CanOpen) return;
        _selected = (cell.Cell.Discusser, cell.Cell.Round);
        foreach (var c in Rows.SelectMany(r => r.Cells)) c.IsSelected = c == cell;
        var turn = RunFiles.ReadJson(run.Round(cell.Cell.Round).Discusser(cell.Cell.Discusser).Output, BobarJson.Compact.DiscussTurnFile);
        SelectedTitle = $"{cell.Cell.Discusser} · round {cell.Cell.Round}";
        SelectedSolution = turn?.Solution ?? "(its output.json is missing or unreadable)";
        SelectedMessages = turn is null || turn.Messages.Count == 0 ? ""
            : string.Join("\n\n", turn.Messages.Select(m => $"→ {m.Key}:\n{m.Value.Trim()}"));
        HasSelection = true;
    }

    [RelayCommand]
    private void CloseSelection()
    {
        _selected = null;
        HasSelection = false;
        foreach (var c in Rows.SelectMany(r => r.Cells)) c.IsSelected = false;
    }

    private static string AnswersSummary(QuestionsFile q, AnswersFile a)
    {
        var sb = new StringBuilder();
        foreach (var item in q.Questions)
        {
            var ans = a.Answers.FirstOrDefault(x => x.Id == item.Id);
            sb.Append($"{item.Id}. {item.Title}\n");
            if (ans?.Option is { } o && o >= 0 && o < item.Options.Count) sb.Append($"   → {item.Options[o].Label}\n");
            if (!string.IsNullOrWhiteSpace(ans?.Comment)) sb.Append($"   → {ans!.Comment!.Trim()}\n");
        }
        if (!string.IsNullOrWhiteSpace(a.Comment)) sb.Append($"\n{a.Comment.Trim()}\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The whole discussion as plain text (Copy exchange).</summary>
    public string ExchangeText(RunState s)
    {
        var turns = new List<(int, string, DiscussTurnFile?, string?)>();
        foreach (var r in s.DiscussRounds)
            foreach (var sh in r.Live)
            {
                var drop = r.Dropped.FirstOrDefault(d => d.Discusser == sh);
                turns.Add((r.Number, sh, RunFiles.ReadJson(run.Round(r.Number).Discusser(sh).Output, BobarJson.Compact.DiscussTurnFile),
                    drop is null ? null : $"dropped out: {drop.Reason}"));
            }
        return Core.Loop.Rendering.DiscussExchange(RunFiles.ReadShared(run.Prompt) ?? "", turns,
            RunFiles.ReadJson(run.Questions, BobarJson.Compact.QuestionsFile), RunFiles.ReadJson(run.Answers, BobarJson.Compact.AnswersFile),
            RunFiles.ReadJson(run.Report, BobarJson.Compact.ReportFile));
    }
}

/// <summary>The author's questions: one card each, with its options and an optional comment. Submit writes answers.json.</summary>
public sealed partial class QuestionsViewModel : ObservableObject
{
    private readonly RunPaths _run;
    private readonly Func<Redactor> _redactor;
    private readonly Action _afterSubmit;

    public IReadOnlyList<QuestionCardViewModel> Cards { get; }
    public string Decided { get; }
    public bool HasDecided => Decided.Length > 0;

    [ObservableProperty] public partial string OverallComment { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SubmitCommand))] public partial bool AllAnswered { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SubmitCommand))] public partial bool Busy { get; set; }

    public QuestionsViewModel(RunPaths run, QuestionsFile questions, Func<Redactor> redactor, Action afterSubmit)
    {
        _run = run;
        _redactor = redactor;
        _afterSubmit = afterSubmit;
        Decided = questions.Decided ?? "";
        Cards = questions.Questions.Select(q => new QuestionCardViewModel(q, UpdateAllAnswered)).ToList();
        UpdateAllAnswered();
    }

    private void UpdateAllAnswered() => AllAnswered = Cards.Count > 0 && Cards.All(c => c.IsAnswered);

    private bool CanSubmit() => AllAnswered && !Busy;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        Busy = true;
        Error = "";
        try
        {
            var file = new AnswersFile
            {
                Answers = Cards.Select(c => new AnswerItem { Id = c.Id, Option = c.ChosenIndex, Comment = string.IsNullOrWhiteSpace(c.Comment) ? null : c.Comment.Trim() }).ToList(),
                Comment = string.IsNullOrWhiteSpace(OverallComment) ? null : OverallComment.Trim(),
            };
            var err = await RunActions.SubmitAnswersAsync(_run, file, _redactor(), TimeProvider.System);
            if (err is not null) Error = $"Could not submit: {err}";
            else _afterSubmit();
        }
        catch (IOException ex)
        {
            Error = $"Could not write answers.json: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }
}

public sealed partial class QuestionCardViewModel : ObservableObject
{
    private readonly Action _changed;

    public string Id { get; }
    public string Title { get; }
    public string Context { get; }
    public bool HasContext => Context.Length > 0;
    public IReadOnlyList<QuestionOptionViewModel> Options { get; }

    [ObservableProperty] public partial bool ShowComment { get; set; }
    [ObservableProperty] public partial string Comment { get; set; } = "";

    public QuestionCardViewModel(QuestionItem q, Action changed)
    {
        _changed = changed;
        Id = q.Id;
        Title = $"{q.Id}. {q.Title}";
        Context = q.Context;
        Options = q.Options.Select(o => new QuestionOptionViewModel(q.Id, o, OnOptionChanged)).ToList();
    }

    public int? ChosenIndex
    {
        get
        {
            for (var i = 0; i < Options.Count; i++)
                if (Options[i].IsChecked) return i;
            return null;
        }
    }
    public bool IsAnswered => ChosenIndex is not null || !string.IsNullOrWhiteSpace(Comment);

    private void OnOptionChanged(QuestionOptionViewModel o)
    {
        if (o.IsChecked)
            foreach (var other in Options.Where(x => x != o)) other.IsChecked = false;
        _changed();
    }

    partial void OnCommentChanged(string value) => _changed();

    [RelayCommand]
    private void AddComment() => ShowComment = true;
}

public sealed partial class QuestionOptionViewModel(string group, QuestionOption o, Action<QuestionOptionViewModel> changed) : ObservableObject
{
    public string Group { get; } = group;
    public string Label { get; } = o.Label;
    public string Description { get; } = o.Description ?? "";
    public bool HasDescription => Description.Length > 0;

    [ObservableProperty] public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed(this);
}
