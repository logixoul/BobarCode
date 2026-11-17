using System.Collections.ObjectModel;
using Bobar.Core;
using Bobar.Core.Loop;
using Bobar.Core.Runs;
using Bobar.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bobar.App.ViewModels;

/// <summary>The right pane for the selected run: Summary and Exchange tabs, Details, Cancel.</summary>
public sealed partial class RunDetailViewModel(MainViewModel main, RunPaths run) : ObservableObject
{
    private int _eventCount = -1;
    private DateTime _cancelArmedUntil;

    public RunPaths Paths { get; } = run;
    public ObservableCollection<SummaryLine> Lines { get; } = [];

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string ExchangeText { get; set; } = "";
    [ObservableProperty] public partial bool CanCancel { get; set; }
    [ObservableProperty] public partial string CancelLabel { get; set; } = "Cancel run";
    [ObservableProperty] public partial string Notice { get; set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FeedRowSpan))] public partial bool ShowDecisions { get; set; }
    [ObservableProperty] public partial object? Decisions { get; set; }

    /// <summary>The decisions panel replaces the lower half of the Summary tab.</summary>
    public int FeedRowSpan => ShowDecisions ? 1 : 2;

    public void Refresh(RunState s, RunStatusKind status)
    {
        Title = s.RunId;
        var started = s.Started;
        Subtitle = started is null
            ? status.Label()
            : $"{s.RepoName} · {started.Reviewer} ({started.ReviewerConfig.ModelShort}) · {status.Label()} · max {s.MaxRounds} rounds";
        CanCancel = s.Finished is null;
        if (DateTime.UtcNow > _cancelArmedUntil) CancelLabel = "Cancel run";

        var lines = Summary.Lines(s, DateTimeOffset.Now, LastActivity(s));
        SyncLines(lines);

        if (s.Events.Count != _eventCount)
        {
            _eventCount = s.Events.Count;
            ExchangeText = Rendering.Exchange(RunReader.LoadRounds(Paths, s), RunReader.LoadDecisions(Paths));
        }
    }

    /// <summary>reviewer.jsonl's mtime for the round in progress: how a stuck model shows up.</summary>
    private DateTimeOffset? LastActivity(RunState s)
    {
        if (s.Phase != RunPhase.Reviewing || s.CurrentRound is not { } r) return null;
        var f = new FileInfo(Paths.Round(r.Number).ReviewerLog);
        return f.Exists ? new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero) : null;
    }

    private void SyncLines(List<SummaryLine> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (i < Lines.Count)
            {
                if (Lines[i] != lines[i]) Lines[i] = lines[i];
            }
            else Lines.Add(lines[i]);
        }
        while (Lines.Count > lines.Count) Lines.RemoveAt(Lines.Count - 1);
    }

    [RelayCommand]
    private void OpenDetails() => main.RequestDetails(Paths);

    /// <summary>Two clicks within 4 s, so a stray click doesn't kill a review.</summary>
    [RelayCommand]
    private async Task Cancel()
    {
        if (DateTime.UtcNow > _cancelArmedUntil)
        {
            _cancelArmedUntil = DateTime.UtcNow.AddSeconds(4);
            CancelLabel = "Click again to cancel";
            return;
        }
        _cancelArmedUntil = DateTime.MinValue;
        CancelLabel = "Cancel run";
        var r = await RunActions.CancelAsync(Paths, main.Redactor, TimeProvider.System);
        Notice = r.Ok ? $"BobarCode {r.Message}." : $"Could not cancel: {r.Message}.";
        main.Refresh();
    }
}
