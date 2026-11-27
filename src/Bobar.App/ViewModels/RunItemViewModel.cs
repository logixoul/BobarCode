using System.Globalization;
using Avalonia.Media;
using Bobar.Core;
using Bobar.Core.IO;
using Bobar.Core.Runs;
using Bobar.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bobar.App.ViewModels;

/// <summary>One run in the left pane. Re-folds its events only when events.jsonl changed.</summary>
public sealed partial class RunItemViewModel(RunPaths run) : ObservableObject
{
    private long _len = -1;
    private DateTime _mtime;

    public RunPaths Paths { get; } = run;
    public string RunId => Paths.RunId;
    public RunState State { get; private set; } = RunState.Fold(run.RunId, []);
    public RunStatusKind Status { get; private set; }

    [ObservableProperty] public partial string RepoName { get; set; } = "";
    [ObservableProperty] public partial string Title { get; set; } = "";
    /// <summary>The run's name (its checkout's folder), without the start time <see cref="Title"/> leads with.</summary>
    [ObservableProperty] public partial string Name { get; set; } = "";
    /// <summary>"14:57 · name": the sidebar row, where the card already says which project and day.</summary>
    [ObservableProperty] public partial string RowTitle { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string StatusLabel { get; set; } = "";
    [ObservableProperty] public partial IBrush BadgeBrush { get; set; } = Brushes.Gray;
    /// <summary>How long ago it started ("4m"), for its one-line row.</summary>
    [ObservableProperty] public partial string Age { get; set; } = "";
    /// <summary>The status and the subtitle, for the row's tooltip.</summary>
    [ObservableProperty] public partial string Tip { get; set; } = "";
    [ObservableProperty] public partial bool IsSelected { get; set; }
    /// <summary>When it started, and the local day it started on; null before its first event (it's starting now).</summary>
    public DateTimeOffset? StartedAt { get; private set; }
    public DateOnly? Day { get; private set; }
    /// <summary>The local time it started ("14:32"), for the Older menu.</summary>
    public string StartTime { get; private set; } = "";
    /// <summary>A run that needs the user (decisions or answers) or is still being worked on: kept in its card whatever its day.</summary>
    public bool NeedsYou => Status is RunStatusKind.AwaitingDecisions or RunStatusKind.AwaitingAnswers;
    /// <summary>Rounds finished and the most it may run, for its card's progress ring. 0 max for an exploration
    /// (one round, nothing to show progress through).</summary>
    public int RoundsDone { get; private set; }
    public int RoundsMax { get; private set; }
    public bool IsLive => Status is RunStatusKind.Running or RunStatusKind.Discussing or RunStatusKind.WaitingForAuthor or RunStatusKind.AwaitingReport;
    /// <summary>The author's usefulness rating ("7/10"), once its hindsight is in.</summary>
    [ObservableProperty] public partial string UsefulnessText { get; set; } = "";
    /// <summary>False while a bobar-cli is working on the run.</summary>
    [ObservableProperty] public partial bool CanDelete { get; set; }

    public void Refresh(DateTimeOffset now)
    {
        var fi = new FileInfo(Paths.Events);
        if (fi.Exists && (fi.Length != _len || fi.LastWriteTimeUtc != _mtime))
        {
            _len = fi.Length;
            _mtime = fi.LastWriteTimeUtc;
            State = RunReader.LoadState(Paths);
            RepoName = State.RepoName;
            Title = State.Label;
            Name = State.Name;
            UsefulnessText = State.Hindsight is { } h ? Hindsight.Rating(h.Usefulness) : "";
        }
        var live = State.Finished is null && RunLock.HeldByLiveCli(Paths);
        CanDelete = !live;
        Status = RunStatus.Of(State, live, now);
        StatusLabel = Status.Label();
        BadgeBrush = Badge(Status);
        StartedAt = State.StartedAt;
        Day = State.StartedAt is { } st ? DateOnly.FromDateTime(st.LocalDateTime) : null;
        StartTime = State.StartedAt?.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        // A discussion that ended (early or not) runs no more rounds: its last one is its maximum, all done.
        RoundsMax = State.IsExplore ? 0 : State.DiscussionEnded is { } ended ? Math.Min(ended.Round, State.MaxRounds) : State.MaxRounds;
        RoundsDone = Math.Min(RoundsMax, State.IsDiscuss
            ? State.DiscussionEnded is not null ? RoundsMax : Math.Max(0, State.DiscussRounds.Count - 1)
            : State.Rounds.Count(r => r.Response is not null));
        RowTitle = StartTime.Length > 0 ? $"{StartTime} · {Name}" : Title;
        Age = State.StartedAt is { } t ? Summary.Ago(now - t) : "";
        var age = Age.Length > 0 ? Age + " ago" : "";
        var round = State.CurrentRound is { } r ? $"round {r.Number}" : "starting";
        Subtitle = State.IsDiscuss ? $"discuss · {string.Join(", ", State.Discussers)} · round {State.CurrentDiscussRound?.Number ?? 1} of {State.MaxRounds} · {age}"
            : State.IsExplore ? $"explore · {State.Started?.Reviewer} · {age}" : $"{State.Started?.Reviewer ?? "?"} · {round} · {age}";
        Tip = $"{StatusLabel} · {Subtitle}";
    }

    private static readonly Dictionary<RunStatusKind, IBrush> Badges = new()
    {
        [RunStatusKind.Running] = new SolidColorBrush(Color.Parse("#2D7FF9")),
        [RunStatusKind.WaitingForAuthor] = new SolidColorBrush(Color.Parse("#8E6CEF")),
        [RunStatusKind.AwaitingDecisions] = new SolidColorBrush(Color.Parse("#E8590C")),
        [RunStatusKind.Done] = new SolidColorBrush(Color.Parse("#2F9E44")),
        [RunStatusKind.Error] = new SolidColorBrush(Color.Parse("#C92A2A")),
        [RunStatusKind.Cancelled] = new SolidColorBrush(Color.Parse("#868E96")),
        [RunStatusKind.Idle] = new SolidColorBrush(Color.Parse("#ADB5BD")),
        [RunStatusKind.Discussing] = new SolidColorBrush(Color.Parse("#2D7FF9")),
        [RunStatusKind.AwaitingAnswers] = new SolidColorBrush(Color.Parse("#E8590C")),
        [RunStatusKind.AwaitingReport] = new SolidColorBrush(Color.Parse("#8E6CEF")),
        [RunStatusKind.Reported] = new SolidColorBrush(Color.Parse("#2F9E44")),
    };

    public static IBrush Badge(RunStatusKind k) => Badges[k];
}
