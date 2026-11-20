using System.Collections.ObjectModel;
using Bobar.Core;
using Bobar.Core.IO;
using Bobar.Core.Loop;
using Bobar.Core.Model;
using Bobar.Core.Reviewers;
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
    public SummaryTableViewModel Table { get; } = new();
    public HindsightViewModel Hindsight { get; } = new();
    private List<RoundData> _rounds = [];

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string ExchangeText { get; set; } = "";
    [ObservableProperty] public partial bool CanCancel { get; set; }
    [ObservableProperty] public partial string CancelLabel { get; set; } = "Cancel run";
    [ObservableProperty] public partial string Notice { get; set; } = "";
    [ObservableProperty] public partial string StatusLabel { get; set; } = "";
    [ObservableProperty] public partial Avalonia.Media.IBrush BadgeBrush { get; set; } = Avalonia.Media.Brushes.Gray;
    [ObservableProperty] public partial bool ShowDecisions { get; set; }
    [ObservableProperty] public partial DecisionsViewModel? Decisions { get; set; }
    [ObservableProperty] public partial string Banner { get; set; } = "";

    private static string CommitsLabel(RunStarted st) => st.Commits.Count switch
    {
        0 => "",
        1 => $" · commit {st.Commits[0].Sha[..7]}",
        var n => $" · {n} commits {st.Commits[0].Sha[..7]}..{st.Commits[^1].Sha[..7]}",
    };

    public void Refresh(RunState s, RunStatusKind status)
    {
        Title = s.RunId;
        var started = s.Started;
        Subtitle = started is null
            ? ""
            : $"{s.RepoName}{CommitsLabel(started)} · {started.Reviewer} ({started.ReviewerConfig.ModelShort}) · max {s.MaxRounds} rounds";
        StatusLabel = status.Label();
        BadgeBrush = RunItemViewModel.Badge(status);
        CanCancel = s.Finished is null;
        if (DateTime.UtcNow > _cancelArmedUntil) CancelLabel = "Cancel run";

        // The decisions panel: kept while the same decision is pending, so typed comments survive refreshes.
        var decisionsWritten = File.Exists(Paths.Decisions);
        if (s.Phase == RunPhase.AwaitingDecisions && s.Awaiting is { } aw && !decisionsWritten)
        {
            if (Decisions?.Key != DecisionsViewModel.KeyOf(Paths, aw))
                Decisions = new DecisionsViewModel(Paths, s, () => main.Redactor, main.Refresh);
        }
        else Decisions = null;
        ShowDecisions = Decisions is not null;
        Banner = s.Phase == RunPhase.AwaitingDecisions && decisionsWritten
            ? "Your decisions are submitted. The author's session picks them up with `bobar-cli review wait`; if it's gone, run that yourself or tell Claude."
            : "";

        if (s.Events.Count != _eventCount)
        {
            _eventCount = s.Events.Count;
            _rounds = RunReader.LoadRounds(Paths, s);
            var decisions = RunReader.LoadDecisions(Paths);
            ExchangeText = Rendering.Exchange(_rounds, decisions);
            var h = s.Hindsight is null ? null : RunReader.LoadHindsight(Paths);
            Hindsight.Update(s.WantsHindsight, h is null ? null : HindsightView.Build(h, _rounds, decisions));
        }
        Table.Update(SummaryTable.Build(s, _rounds, DateTimeOffset.Now, LastActivity(s), LiveCost(s)));
    }

    private (string Path, long Length, IReadOnlyDictionary<string, Price>? Prices, double? Cost) _live;

    /// <summary>The round in progress's cost so far, recomputed only when its reviewer.jsonl grew or the prices were reloaded.</summary>
    private double? LiveCost(RunState s)
    {
        if (s.Phase != RunPhase.Reviewing || s.CurrentRound is not { } r || s.Started?.ReviewerConfig is not { Kind: ReviewerKinds.Pi } cfg) return null;
        var f = new FileInfo(Paths.Round(r.Number).ReviewerLog);
        if (!f.Exists) return null;
        var prices = main.Prices;
        if (_live.Path != f.FullName || _live.Length != f.Length || !ReferenceEquals(_live.Prices, prices))
            _live = (f.FullName, f.Length, prices, Pricing.LiveCost(cfg, RunFiles.ReadShared(f.FullName) ?? "", prices));
        return _live.Cost;
    }

    /// <summary>reviewer.jsonl's mtime for the round in progress: how a stuck model shows up.</summary>
    private DateTimeOffset? LastActivity(RunState s)
    {
        if (s.Phase != RunPhase.Reviewing || s.CurrentRound is not { } r) return null;
        var f = new FileInfo(Paths.Round(r.Number).ReviewerLog);
        return f.Exists ? new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero) : null;
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
