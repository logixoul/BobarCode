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
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string StatusLabel { get; set; } = "";
    [ObservableProperty] public partial IBrush BadgeBrush { get; set; } = Brushes.Gray;
    [ObservableProperty] public partial bool ShowRepoHeader { get; set; }
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
            UsefulnessText = State.Hindsight is { } h ? Hindsight.Rating(h.Usefulness) : "";
        }
        var live = State.Finished is null && RunLock.HeldByLiveCli(Paths);
        CanDelete = !live;
        Status = RunStatus.Of(State, live, now);
        StatusLabel = Status.Label();
        BadgeBrush = Badge(Status);
        var age = State.StartedAt is { } t ? Summary.Ago(now - t) + " ago" : "";
        var round = State.CurrentRound is { } r ? $"round {r.Number}" : "starting";
        Subtitle = State.IsDiscuss ? $"discuss · {string.Join(", ", State.Discussers)} · round {State.CurrentDiscussRound?.Number ?? 1} of {State.MaxRounds} · {age}"
            : State.IsExplore ? $"explore · {State.Started?.Reviewer} · {age}" : $"{State.Started?.Reviewer ?? "?"} · {round} · {age}";
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
