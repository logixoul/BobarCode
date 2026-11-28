using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using Bobar.Core;
using Bobar.Core.IO;
using Bobar.Core.Model;
using Bobar.Core.Runs;
using Bobar.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bobar.App.ViewModels;

/// <summary>
/// The window: the run list on the left and the selected run on the right. Live updates come from
/// a FileSystemWatcher on the runs dir plus a 1 s poll (watchers drop events). The GUI only reads,
/// except for decisions.json, cancel, deleting runs (to the trash, with Undo) and Settings (config.json, plus the theme in gui.json).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly BobarPaths _paths;
    private readonly Dictionary<string, RunItemViewModel> _byId = new();
    private readonly DispatcherTimer _timer;
    private FileSystemWatcher? _watcher;
    private bool _refreshQueued;
    private DateTime _keysStamp = DateTime.MinValue;
    private string? _pendingSelect;

    /// <summary>The sidebar: one card per project.</summary>
    public ObservableCollection<ProjectCardViewModel> Projects { get; } = [];
    private readonly Dictionary<string, ProjectCardViewModel> _cards = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<string> RangeNames { get; } = ["24 hours", "7 days", "30 days", "All time"];
    /// <summary>The cards' range, as the dropdown's index into <see cref="RangeNames"/>. 24 hours at every launch.</summary>
    [ObservableProperty] public partial int RangeIndex { get; set; }
    private RunRange Range => (RunRange)Math.Clamp(RangeIndex, 0, RangeNames.Count - 1);
    partial void OnRangeIndexChanged(int value) => Refresh();

    [ObservableProperty] public partial RunItemViewModel? SelectedRun { get; set; }
    [ObservableProperty] public partial RunDetailViewModel? Detail { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    /// <summary>The run the last delete moved to the trash, while Undo can still bring it back.</summary>
    [ObservableProperty] public partial string? DeletedRunId { get; set; }

    public Redactor Redactor { get; private set; } = Redactor.None;

    /// <summary>config.json's price table, for the live cost of a round in progress. The last good one if the file
    /// is broken; empty until one has been read. Replaced (never mutated) on reload, so the reference is its version.</summary>
    public IReadOnlyDictionary<string, Price> Prices { get; private set; } = new Dictionary<string, Price>();
    /// <summary>config.json's webSearch.pricePerCall, read with <see cref="Prices"/>.</summary>
    public double? PricePerSearch { get; private set; } = new AppConfig().WebSearch.PricePerCall;
    private DateTime _configStamp = DateTime.MinValue;

    /// <summary>Raised to open the Details modal for a run (the view owns windows).</summary>
    public event Action<RunPaths>? DetailsRequested;

    public const double MinZoom = 0.5, MaxZoom = 3.0, ZoomStep = 0.1;

    /// <summary>gui.json, as loaded at launch and changed since. A tiny file, so it's written on every change.</summary>
    private readonly GuiStateFile _gui;

    /// <summary>The run body's zoom: the one owner of the level. Clamped and rounded to a step (so 1.7 + 0.1
    /// stays 1.8), kept in gui.json across restarts.</summary>
    public double Zoom
    {
        get => _gui.Zoom;
        set
        {
            var z = ClampZoom(value);
            if (z == _gui.Zoom) return;
            _gui.Zoom = z;
            OnPropertyChanged();
            SaveGuiState();
        }
    }

    /// <summary>The app's colours (Settings), kept in gui.json and applied as soon as they change.</summary>
    public GuiTheme Theme
    {
        get => _gui.Theme;
        set
        {
            ApplyTheme(value);
            if (value == _gui.Theme) return;
            _gui.Theme = value;
            OnPropertyChanged();
            SaveGuiState();
        }
    }

    public static void ApplyTheme(GuiTheme theme)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme switch { GuiTheme.Light => ThemeVariant.Light, GuiTheme.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }

    /// <summary>The sidebar's backdrop on Windows (Settings), kept in gui.json.</summary>
    public GuiBackdrop Backdrop
    {
        get => _gui.Backdrop;
        set
        {
            ShownBackdrop = value;
            if (value == _gui.Backdrop) return;
            _gui.Backdrop = value;
            OnPropertyChanged();
            SaveGuiState();
        }
    }

    /// <summary>What the window shows: <see cref="Backdrop"/>, or the one Settings is previewing.</summary>
    [ObservableProperty] public partial GuiBackdrop ShownBackdrop { get; set; }

    private void SaveGuiState()
    {
        try { RunFiles.WriteJson(_paths.GuiStateFile, _gui, BobarJson.Indented.GuiStateFile, Redactor.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // not worth bothering the user
    }

    private static double ClampZoom(double z) => double.IsFinite(z) ? Math.Clamp(Math.Round(z, 1), MinZoom, MaxZoom) : 1.0;

    /// <summary>gui.json, or the defaults (100%, the system's theme) for what's missing, malformed or unreadable
    /// (a locked file must not stop the GUI starting).</summary>
    private static GuiStateFile LoadGuiState(BobarPaths paths)
    {
        GuiStateFile gui;
        try { gui = RunFiles.ReadJson(paths.GuiStateFile, BobarJson.Indented.GuiStateFile) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { gui = new(); }
        gui.Zoom = ClampZoom(gui.Zoom);
        if (!Enum.IsDefined(gui.Theme)) gui.Theme = GuiTheme.System;
        if (!Enum.IsDefined(gui.Backdrop)) gui.Backdrop = GuiBackdrop.Wallpaper;
        return gui;
    }

    public MainViewModel(BobarPaths paths)
    {
        _paths = paths;
        _gui = LoadGuiState(paths);
        ApplyTheme(_gui.Theme);
        ShownBackdrop = _gui.Backdrop;
        Directory.CreateDirectory(paths.RunsDir);
        RunTrash.Purge(paths, DateTime.UtcNow);
        StartWatcher();
        Refresh();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
    }

    partial void OnSelectedRunChanged(RunItemViewModel? oldValue, RunItemViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        // Lists (or unlists) a run picked from a card's history menu straight away, not on the next tick.
        Refresh();
    }

    [RelayCommand]
    private void OpenRun(RunItemViewModel run) => SelectedRun = run;

    partial void OnSelectedRunChanged(RunItemViewModel? value)
    {
        if (value is null)
        {
            Detail = null;
            return;
        }
        Detail = new RunDetailViewModel(this, _paths.Run(value.RunId));
        Detail.Refresh(value.State, value.Status);
    }

    /// <summary>Selects a run by id (from `--run`); remembered until it shows up if it isn't listed yet.</summary>
    public void SelectRun(string runId)
    {
        _pendingSelect = runId;
        Refresh();
    }

    /// <summary>Moves a run to the trash; the status bar then offers Undo.</summary>
    public void DeleteRun(RunItemViewModel item)
    {
        var label = $"{item.RepoName} · {item.Title}";
        if (RunTrash.Delete(_paths, item.Paths) is { } err)
        {
            StatusText = $"Could not delete the run {label}: {err}.";
            return;
        }
        if (SelectedRun == item) SelectedRun = null;
        Refresh();
        DeletedRunId = item.RunId;
        StatusText = $"Deleted the run {label}.";
    }

    [RelayCommand]
    private void UndoDelete()
    {
        if (DeletedRunId is not { } id) return;
        DeletedRunId = null;
        if (RunTrash.Restore(_paths, id) is { } err)
        {
            StatusText = $"Could not restore the run: {err}.";
            return;
        }
        StatusText = "";
        SelectRun(id);
    }

    public void RequestDetails(RunPaths run) => DetailsRequested?.Invoke(run);

    /// <summary>Raised to open the Settings modal (the view owns windows).</summary>
    public event Action<SettingsViewModel>? SettingsRequested;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(new SettingsViewModel(_paths, this));

    /// <summary>Raised to open the Reviewers modal (the view owns windows).</summary>
    public event Action<ReviewersViewModel>? ReviewersRequested;

    [RelayCommand]
    private void OpenReviewers()
    {
        Refresh();
        ReviewersRequested?.Invoke(new ReviewersViewModel(_byId.Values.Select(r => (r.Paths, r.State)).ToList()));
    }

    private void StartWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher(_paths.RunsDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += OnFsEvent;
            _watcher.Created += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            StatusText = $"File watching is off ({ex.Message}); polling every second.";
        }
    }

    private void OnFsEvent(object? sender, FileSystemEventArgs e)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    /// <summary>Re-reads changed runs, re-sorts the list, and updates the selected run.</summary>
    public void Refresh()
    {
        ReloadKeysIfChanged();
        ReloadPricesIfChanged();
        var now = DateTimeOffset.Now;
        var seen = new HashSet<string>();
        foreach (var run in RunReader.ListRuns(_paths))
        {
            seen.Add(run.RunId);
            if (!_byId.TryGetValue(run.RunId, out var item))
            {
                item = new RunItemViewModel(run);
                _byId[run.RunId] = item;
            }
            item.Refresh(now);
        }
        foreach (var gone in _byId.Keys.Where(k => !seen.Contains(k)).ToList()) _byId.Remove(gone);

        if (SelectedRun is { } cur && !_byId.ContainsKey(cur.RunId)) SelectedRun = null;
        if (_pendingSelect is not null)
        {
            var match = _byId.Values.FirstOrDefault(r => r.RunId == _pendingSelect)
                        ?? _byId.Values.FirstOrDefault(r => r.RunId.StartsWith(_pendingSelect, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                if (_cards.TryGetValue(match.RepoName, out var card)) card.IsExpanded = true;
                SelectedRun = match;
                _pendingSelect = null;
            }
        }

        // One card per project (cards ordered by their newest run), newest first within a card.
        // By the start time in the run id (both id formats), not the id's text.
        var groups = _byId.Values
            .GroupBy(r => r.RepoName, StringComparer.OrdinalIgnoreCase)
            .Select(g => RunIds.NewestFirst(g, r => r.RunId).ToList())
            .OrderByDescending(g => RunIds.StartTime(g[0].RunId) ?? DateTime.MinValue)
            .ToList();
        var cards = groups.Select(g =>
        {
            if (!_cards.TryGetValue(g[0].RepoName, out var card))
            {
                _cards[g[0].RepoName] = card = new ProjectCardViewModel(g[0].RepoName);
                // "Show N more" lists the rest now, not on the next tick.
                card.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProjectCardViewModel.ShowAll) && card.ShowAll) Refresh(); };
            }
            card.Update(g, now, Range, SelectedRun);
            return card;
        }).ToList();
        foreach (var gone in _cards.Keys.Where(n => !cards.Any(c => c.Name == n)).ToList()) _cards.Remove(gone);
        if (!cards.SequenceEqual(Projects))
        {
            Projects.Clear();
            foreach (var c in cards) Projects.Add(c);
        }

        if (SelectedRun is { } sel && Detail is not null) Detail.Refresh(sel.State, sel.Status);
        if (_byId.Count == 0) StatusText = $"No runs yet in {_paths.RunsDir}.";
        else if (StatusText.StartsWith("No runs yet", StringComparison.Ordinal)) StatusText = "";
    }

    private void ReloadKeysIfChanged()
    {
        try
        {
            var stamp = File.Exists(_paths.KeysFile) ? File.GetLastWriteTimeUtc(_paths.KeysFile) : DateTime.MinValue;
            if (stamp == _keysStamp) return;
            _keysStamp = stamp;
            Redactor = KeysEnv.Load(_paths.KeysFile).Redactor();
        }
        catch (IOException) { }
    }

    private void ReloadPricesIfChanged()
    {
        // Read-only: the GUI never creates config.json here. A read failure retries next tick; a broken file waits
        // for its next change. Either way the last good prices stay.
        try
        {
            var f = new FileInfo(_paths.ConfigFile);
            if (!f.Exists || f.LastWriteTimeUtc == _configStamp) return;
            var text = RunFiles.ReadShared(f.FullName);
            if (text is null) return;
            var stamp = f.LastWriteTimeUtc;
            try
            {
                var cfg = ConfigStore.Parse(text, f.FullName);
                PricePerSearch = cfg.WebSearch?.PricePerCall;
                Prices = cfg.Prices ?? new Dictionary<string, Price>();
            }
            finally { _configStamp = stamp; }
        }
        catch (Exception) { } // best-effort display: a hand-edited config can fail in many shapes; never crash the GUI over it
    }

    public void Dispose()
    {
        _timer.Stop();
        _watcher?.Dispose();
    }
}
