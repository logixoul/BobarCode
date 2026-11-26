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

/// <summary>One participant in one round; click it to open its reply or its activity below the table. Updated in place
/// on each tick, so the timer doesn't replace the button you focused or selected.</summary>
public sealed partial class DiscussCellViewModel(DiscussCell cell, Action<DiscussCellViewModel> open) : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Text), nameof(Tip), nameof(CanOpen), nameof(IsWorking), nameof(IsDropped))]
    public partial DiscussCell Cell { get; set; } = cell;

    public string Text => Cell.Text;
    public string Tip => Cell.Detail.Length > 0 ? Cell.Detail
        : Cell.State switch
        {
            DiscussCellState.Replied => $"Read {Cell.Discusser}'s reply",
            DiscussCellState.Absent => "",
            _ => $"Watch {Cell.Discusser}'s activity",
        };
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

/// <summary>Where the activity view should scroll after its entries change.</summary>
public enum ActivityScroll { Follow, Top, End }

/// <summary>The right pane's body for a discussion: the question, the rounds table, the selected turn (its reply or
/// its activity), questions, report.</summary>
public sealed partial class DiscussViewModel(RunPaths run, Func<Redactor> redactor, Action afterSubmit) : ObservableObject
{
    private (string Discusser, int Round)? _selected;
    private int _eventCount = -1;

    /// <summary>The selected turn's activity log, tailed while it is shown.</summary>
    public ActivityFeed Activity { get; } = new();

    /// <summary>Raised after the activity entries changed; the view scrolls as asked (Follow: only if it was at the bottom).</summary>
    public event Action<ActivityScroll>? ActivityChanged;

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

    [ObservableProperty] public partial string Caption { get; set; } = "";
    /// <summary>The table's minimum width, so many participants scroll the table sideways instead of squeezing it.</summary>
    [ObservableProperty] public partial double TableMinWidth { get; set; }

    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial string SelectedTitle { get; set; } = "";
    /// <summary>Why the selected turn dropped out or stopped, shown above its activity.</summary>
    [ObservableProperty] public partial string SelectedNote { get; set; } = "";
    /// <summary>The selected turn has a reply to read (it may arrive while you watch its activity).</summary>
    [ObservableProperty] public partial bool HasReply { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowReply))] public partial bool ShowActivity { get; set; }
    public bool ShowReply => !ShowActivity;
    /// <summary>The selected turn is still running.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActivityLabel))] public partial bool IsLive { get; set; }
    public string ActivityLabel => IsLive ? "Activity · live" : "Activity";
    [ObservableProperty] public partial string SelectedSolution { get; set; } = "";
    /// <summary>The messages the selected reply sent, each rendered on its own.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedMessages))]
    public partial IReadOnlyList<MessageItem> SelectedMessages { get; set; } = [];
    public bool HasSelectedMessages => SelectedMessages.Count > 0;

    [ObservableProperty] public partial QuestionsViewModel? Questions { get; set; }
    [ObservableProperty] public partial bool ShowQuestions { get; set; }
    [ObservableProperty] public partial string AnswersText { get; set; } = "";
    [ObservableProperty] public partial string Report { get; set; } = "";

    public void Update(RunState s)
    {
        var m = DiscussTableModel.Build(s, DateTimeOffset.Now);
        if (!m.Discussers.SequenceEqual(Headers)) Headers = m.Discussers.ToList();
        Columns = Math.Max(1, m.Discussers.Count);
        TableMinWidth = 80 + Columns * 150;
        for (var i = 0; i < m.Rows.Count; i++)
        {
            if (i >= Rows.Count) Rows.Add(new DiscussRowViewModel(m.Rows[i].Round));
            var cells = m.Rows[i].Cells;
            var row = Rows[i];
            if (row.Cells.Count == cells.Count && row.Cells.Zip(cells).All(p => p.First.Cell.Discusser == p.Second.Discusser))
                for (var j = 0; j < cells.Count; j++) row.Cells[j].Cell = cells[j];
            else
                row.Cells = cells.Select(c => new DiscussCellViewModel(c, Open) { IsSelected = _selected == (c.Discusser, c.Round) }).ToList();
        }
        while (Rows.Count > m.Rows.Count) Rows.RemoveAt(Rows.Count - 1);
        HasRows = Rows.Count > 0;
        if (!m.Totals.SequenceEqual(Totals)) Totals = m.Totals.ToList();
        TotalTime = m.TotalTime;
        Caption = "Click a participant's cell to read its reply or watch its activity."
            + (TotalTime.Length > 0 && s.DiscussionEnded is not null ? $" The discussion took {TotalTime} (rounds run in parallel)." : "");
        UpdateSelection();
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

    private DiscussCellViewModel? SelectedCell =>
        _selected is { } sel ? Rows.SelectMany(r => r.Cells).FirstOrDefault(c => (c.Cell.Discusser, c.Cell.Round) == sel) : null;

    private string SelectedLog => run.Round(_selected!.Value.Round).Discusser(_selected.Value.Discusser).ReviewerLog;

    /// <summary>A replied turn opens on its reply; any other on its activity (live while it works).</summary>
    public void Open(DiscussCellViewModel cell)
    {
        if (!cell.CanOpen || (HasSelection && _selected == (cell.Cell.Discusser, cell.Cell.Round))) return;
        _selected = (cell.Cell.Discusser, cell.Cell.Round);
        foreach (var c in Rows.SelectMany(r => r.Cells)) c.IsSelected = c == cell;
        SelectedTitle = $"Round {cell.Cell.Round} · {cell.Cell.Discusser}";
        HasReply = false;
        Activity.Clear();
        HasSelection = true;
        ShowActivity = cell.Cell.State != DiscussCellState.Replied;
        UpdateSelection(ShowActivity ? (cell.Cell.State == DiscussCellState.Working ? ActivityScroll.End : ActivityScroll.Top) : ActivityScroll.Follow);
    }

    /// <summary>Keeps the selected turn current on each tick: its reply once it arrives (without switching away from
    /// its activity), its state, and its activity while shown.</summary>
    private void UpdateSelection(ActivityScroll scroll = ActivityScroll.Follow)
    {
        if (!HasSelection || SelectedCell is not { } cell) return;
        var state = cell.Cell.State;
        IsLive = state == DiscussCellState.Working;
        SelectedNote = state switch
        {
            DiscussCellState.Dropped => $"Dropped out: {cell.Cell.Detail}",
            DiscussCellState.Stopped => "Stopped before it replied.",
            _ => "",
        };
        if (state == DiscussCellState.Replied && !HasReply)
        {
            var turn = RunFiles.ReadJson(run.Round(cell.Cell.Round).Discusser(cell.Cell.Discusser).Output, BobarJson.Compact.DiscussTurnFile);
            SelectedSolution = turn?.Solution ?? "(its output.json is missing or unreadable)";
            SelectedMessages = turn is null ? [] : turn.Messages.Select(m => new MessageItem(m.Key, m.Value.Trim())).ToList();
            HasReply = true;
        }
        if (ShowActivity) RefreshActivity(scroll);
    }

    private void RefreshActivity(ActivityScroll scroll)
    {
        if (_selected is null) return;
        if (Activity.Refresh([(null, SelectedLog)], IsLive ? "Waiting for activity…" : "(no activity was logged)", ""))
            ActivityChanged?.Invoke(scroll);
    }

    [RelayCommand]
    private void ShowReplyView()
    {
        if (HasReply) ShowActivity = false;
    }

    [RelayCommand]
    private void ShowActivityView()
    {
        if (ShowActivity) return;
        ShowActivity = true;
        RefreshActivity(IsLive ? ActivityScroll.End : ActivityScroll.Top);
    }

    [RelayCommand] private void ExpandAll() => Activity.SetExpanded(true);
    [RelayCommand] private void CollapseAll() => Activity.SetExpanded(false);

    /// <summary>The selected turn's activity as plain text (Copy log).</summary>
    public string ActivityText() => _selected is null ? "" : ActivityFeed.PlainText([(null, SelectedLog)]);

    [RelayCommand]
    private void CloseSelection()
    {
        _selected = null;
        HasSelection = false;
        HasReply = false;
        Activity.Clear();
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

/// <summary>A message a discusser sent: the recipient (plain) and the Markdown text.</summary>
public sealed record MessageItem(string Recipient, string Text);
