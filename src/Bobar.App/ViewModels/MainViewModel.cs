using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using Bobar.Core;
using Bobar.Core.IO;
using Bobar.Core.Runs;
using Bobar.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bobar.App.ViewModels;

/// <summary>
/// The window: the run list on the left and the selected run on the right. Live updates come from
/// a FileSystemWatcher on the runs dir plus a 1 s poll (watchers drop events). The GUI only reads,
/// except for decisions.json and cancel.
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

    public ObservableCollection<RunItemViewModel> Runs { get; } = [];

    [ObservableProperty] public partial RunItemViewModel? SelectedRun { get; set; }
    [ObservableProperty] public partial RunDetailViewModel? Detail { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";

    public Redactor Redactor { get; private set; } = Redactor.None;

    /// <summary>Raised to open the Details modal for a run (the view owns windows).</summary>
    public event Action<RunPaths>? DetailsRequested;

    public MainViewModel(BobarPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(paths.RunsDir);
        StartWatcher();
        Refresh();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
    }

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

    public void RequestDetails(RunPaths run) => DetailsRequested?.Invoke(run);

    [RelayCommand]
    private void OpenConfig()
    {
        try
        {
            ConfigStore.EnsureExists(_paths);
            Process.Start(new ProcessStartInfo(_paths.ConfigFile) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText = $"Could not open {_paths.ConfigFile}: {ex.Message}";
        }
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

        // Grouped by repo (groups ordered by their newest run), newest first within a group.
        var ordered = _byId.Values
            .GroupBy(r => r.RepoName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Select(r => r.RunId).Max(StringComparer.Ordinal), StringComparer.Ordinal)
            .SelectMany(g => g.OrderByDescending(r => r.RunId, StringComparer.Ordinal).Select((r, i) => (r, first: i == 0)))
            .ToList();
        foreach (var (r, first) in ordered) r.ShowRepoHeader = first;
        if (!ordered.Select(x => x.r).SequenceEqual(Runs))
        {
            var selected = SelectedRun;
            Runs.Clear();
            foreach (var (r, _) in ordered) Runs.Add(r);
            if (selected is not null && Runs.Contains(selected)) SelectedRun = selected;
        }

        if (_pendingSelect is not null)
        {
            var match = Runs.FirstOrDefault(r => r.RunId == _pendingSelect)
                        ?? Runs.FirstOrDefault(r => r.RunId.StartsWith(_pendingSelect, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                SelectedRun = match;
                _pendingSelect = null;
            }
        }
        if (SelectedRun is { } sel && Detail is not null) Detail.Refresh(sel.State, sel.Status);
        if (Runs.Count == 0) StatusText = $"No runs yet in {_paths.RunsDir}.";
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

    public void Dispose()
    {
        _timer.Stop();
        _watcher?.Dispose();
    }
}
