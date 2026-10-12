using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Insomnia.App.Services;
using Insomnia.Core.Engine;
using Insomnia.Core.Logging;
using Insomnia.Core.Logic;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;

namespace Insomnia.App.Settings;

/// <summary>A preset chip in Settings.</summary>
public sealed record PresetChip(double Seconds, string Label, bool IsDefault);

/// <summary>Where the last save stands, for the line under the page title.</summary>
public enum SaveState
{
    Idle,
    Pending,
    Saving,
    Saved,
    Failed,
}

/// <summary>
/// Settings as an editable copy of <see cref="ISessionController.Config"/>.
/// Live save: every change is applied to the draft with a <c>with</c>
/// expression (or the coupled floor helpers) and saved 400 ms after the last
/// change through <see cref="ISessionController.SaveConfigAsync"/>. Text
/// fields commit when they lose focus or on Enter, so normalisation never
/// rewrites text under the caret. A refused save keeps the draft and shows
/// the controller's message until a save succeeds.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly ISessionController _controller;
    private readonly ISettingsServices _services;
    private readonly DispatcherTimer _saveTimer;
    private Config _draft;
    private int _generation;
    private bool _saving;
    private bool _saveAgain;
    private SaveState _saveState;
    private string? _saveError;
    private StartupState _startup;
    private string? _startupError;
    private bool _hasHotspotPassword;
    private string? _hotspotMessage;
    private bool _hotspotMessageIsError;
    private string _wouldFreeze = "";
    private int _previewGeneration;

    public SettingsViewModel(ISessionController controller, ISettingsServices services)
    {
        _controller = controller;
        _services = services;
        _draft = controller.Config;
        _saveTimer = new DispatcherTimer(SaveDelay, DispatcherPriority.Normal, OnSaveTimer, Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
        SyncCollections();
        ReadStartup();
        ReadHotspotPassword();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Config Draft => _draft;

    public ObservableCollection<PresetChip> Presets { get; } = [];

    public ObservableCollection<string> FreezeList { get; } = [];

    public ObservableCollection<string> AgentList { get; } = [];

    public ObservableCollection<string> TmuxTargets { get; } = [];

    // Session

    public string DefaultPresetLabel => SessionMath.FormatPreset(_draft.DefaultPreset);

    public string MaxDurationLabel => SessionMath.FormatPreset(_draft.MaxDuration);

    // Lid-close actions

    public bool FreezeAllApps
    {
        get => _draft.FreezeAllApps;
        set => Update(_draft with { FreezeAllApps = value });
    }

    public bool DockerRule
    {
        get => _draft.DockerRule;
        set => Update(_draft with { DockerRule = value });
    }

    public bool MuteOnLidClose
    {
        get => _draft.MuteOnLidClose;
        set => Update(_draft with { MuteOnLidClose = value });
    }

    public bool DisplaysOffOnLidClose
    {
        get => _draft.DisplaysOffOnLidClose;
        set => Update(_draft with { DisplaysOffOnLidClose = value });
    }

    public bool PowerSavingOnLidClose
    {
        get => _draft.PowerSavingOnLidClose;
        set => Update(_draft with { PowerSavingOnLidClose = value });
    }

    /// <summary>"Would freeze now: Slack, Discord", from the controller's preview of the draft.</summary>
    public string WouldFreeze
    {
        get => _wouldFreeze;
        private set => Set(ref _wouldFreeze, value);
    }

    // Agent apps

    public bool DisableThrottlingForAgents
    {
        get => _draft.DisableThrottlingForAgents;
        set => Update(_draft with { DisableThrottlingForAgents = value });
    }

    // Battery and thermal

    public int LowPowerFloor
    {
        get => _draft.LowPowerFloor;
        set => Update(_draft.WithLowPowerFloor(value));
    }

    public int EndFloor
    {
        get => _draft.EndFloor;
        set => Update(_draft.WithEndFloor(value));
    }

    public int MaxEndFloor => Config.MaxEndFloor;

    public int FloorStep => Config.FloorStep;

    public bool ThermalRules
    {
        get => _draft.ThermalRules;
        set => Update(_draft with { ThermalRules = value });
    }

    // Network

    public string HotspotSsid
    {
        get => _draft.HotspotSsid;
        set
        {
            var trimmed = (value ?? "").Trim();
            if (trimmed == _draft.HotspotSsid)
            {
                return;
            }
            Update(_draft with { HotspotSsid = trimmed });
            ReadHotspotPassword();
        }
    }

    public bool HasHotspot => _draft.HotspotSsid.Length > 0;

    public bool HasHotspotPassword
    {
        get => _hasHotspotPassword;
        private set => Set(ref _hasHotspotPassword, value);
    }

    public string HotspotPasswordStatus => !HasHotspot
        ? "Enter the hotspot name first."
        : HasHotspotPassword ? $"A password is saved for {_draft.HotspotSsid}." : "No password saved.";

    public string? HotspotMessage
    {
        get => _hotspotMessage;
        private set => Set(ref _hotspotMessage, value);
    }

    public bool HotspotMessageIsError
    {
        get => _hotspotMessageIsError;
        private set => Set(ref _hotspotMessageIsError, value);
    }

    public int NudgeThreshold
    {
        get => (int)Math.Round(_draft.NudgeThreshold);
        set => Update(_draft with { NudgeThreshold = Math.Max(0, value) });
    }

    public bool TmuxNudgePressesEnter
    {
        get => _draft.TmuxNudgePressesEnter;
        set => Update(_draft with { TmuxNudgePressesEnter = value });
    }

    public string TmuxCommand
    {
        get => _draft.TmuxCommand;
        set
        {
            var trimmed = (value ?? "").Trim();
            Update(_draft with { TmuxCommand = trimmed.Length == 0 ? "wsl.exe -e tmux" : trimmed });
        }
    }

    // General

    public bool LaunchAtLogin
    {
        get => _startup == StartupState.Enabled;
        set => SetLaunchAtLogin(value);
    }

    public bool StartupDisabledByUser => _startup == StartupState.DisabledByUser;

    /// <summary>The note under the switch has something to say.</summary>
    public bool ShowStartupNote => StartupDisabledByUser || StartupError is not null;

    public string? StartupError
    {
        get => _startupError;
        private set => Set(ref _startupError, value);
    }

    public string LogsFolder => _services.LogsFolder;

    // Save status

    public SaveState SaveState
    {
        get => _saveState;
        private set
        {
            if (Set(ref _saveState, value))
            {
                OnPropertyChanged(nameof(SaveStatusText));
            }
        }
    }

    public string SaveStatusText => _saveState switch
    {
        SaveState.Pending or SaveState.Saving => "Saving…",
        SaveState.Saved => "Changes saved",
        _ => "",
    };

    public string? SaveError
    {
        get => _saveError;
        private set => Set(ref _saveError, value);
    }

    #region Edits

    /// <summary>Add a preset typed as "30m", "2h", "1h30m" or "3d". Returns an error message, or null.</summary>
    public string? AddPreset(string text)
    {
        if (DurationParser.Seconds(text) is not { } seconds)
        {
            return "Use a duration like 30m, 2h, 1h30m or 3d.";
        }
        if (seconds < SessionMath.MinimumDuration)
        {
            return "Presets are at least 1 minute.";
        }
        if (seconds > _draft.MaxDuration)
        {
            return $"Presets can't be longer than {SessionMath.FormatPreset(_draft.MaxDuration)}.";
        }
        if (_draft.Presets.Contains(seconds))
        {
            return $"{SessionMath.FormatPreset(seconds)} is already a preset.";
        }
        Update(_draft with { Presets = [.. _draft.Presets.Append(seconds).Order()] });
        return null;
    }

    public void RemovePreset(double seconds)
    {
        if (_draft.Presets.Count <= 1)
        {
            return;
        }
        var presets = _draft.Presets.Where(p => !p.Equals(seconds)).ToList();
        var next = _draft with { Presets = presets };
        if (_draft.DefaultPreset.Equals(seconds))
        {
            // The default goes with its chip: the nearest remaining preset takes over.
            next = next with { DefaultPreset = presets.MinBy(p => Math.Abs(p - seconds)) };
        }
        Update(next);
    }

    public void MakeDefault(double seconds) => Update(_draft with { DefaultPreset = seconds });

    /// <summary>Add an executable to the freeze list. Returns an error message, or null.</summary>
    public string? AddFreeze(string name) => AddName(name, _draft.FreezeList, list => _draft with { FreezeList = list });

    public void RemoveFreeze(string name) => Update(_draft with { FreezeList = _draft.FreezeList.Where(n => n != name).ToList() });

    public string? AddAgent(string name) => AddName(name, _draft.AgentList, list => _draft with { AgentList = list });

    public void RemoveAgent(string name) => Update(_draft with { AgentList = _draft.AgentList.Where(n => n != name).ToList() });

    public string? AddTmuxTarget(string text)
    {
        var target = (text ?? "").Trim();
        if (target.Length == 0 || target.Any(char.IsWhiteSpace) || !target.Contains(':', StringComparison.Ordinal))
        {
            return "Use session:window.pane, for example work:1.0.";
        }
        if (_draft.TmuxTargets.Contains(target, StringComparer.Ordinal))
        {
            return $"{target} is already listed.";
        }
        Update(_draft with { TmuxTargets = [.. _draft.TmuxTargets, target] });
        return null;
    }

    public void RemoveTmuxTarget(string target) => Update(_draft with { TmuxTargets = _draft.TmuxTargets.Where(t => t != target).ToList() });

    /// <summary>Running apps that could join the freeze list: not listed yet, and not agent apps (which are never frozen).</summary>
    public Task<IReadOnlyList<RunningApp>> FreezeCandidatesAsync() =>
        CandidatesAsync([.. _draft.FreezeList, .. _draft.AgentList]);

    /// <summary>Running apps that could join the agent list.</summary>
    public Task<IReadOnlyList<RunningApp>> AgentCandidatesAsync() => CandidatesAsync(_draft.AgentList);

    private async Task<IReadOnlyList<RunningApp>> CandidatesAsync(IEnumerable<string> exclude)
    {
        var excluded = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        var running = await Task.Run(_services.RunningApps);
        return running
            .Where(a => !excluded.Contains(a.Image))
            .DistinctBy(a => a.Image, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Re-read "Would freeze now" for the current draft (off the UI thread: it snapshots processes).</summary>
    public async Task RefreshWouldFreezeAsync()
    {
        var generation = ++_previewGeneration;
        var draft = _draft;
        try
        {
            var preview = await Task.Run(() => _controller.PreviewFreeze(draft));
            if (generation != _previewGeneration)
            {
                return;
            }
            var names = preview.Explicit.Concat(preview.Automatic).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            WouldFreeze = names.Count == 0
                ? (draft.FreezeAllApps ? "Would freeze now: nothing that is running" : "Would freeze now: nothing on the list is running")
                : "Would freeze now: " + string.Join(", ", names.Select(Friendly));
        }
        catch (Exception ex)
        {
            Log.Error("settings: freeze preview failed", ex);
            WouldFreeze = "Would freeze now: couldn't read the running apps";
        }
    }

    public string? SaveHotspotPassword(string password)
    {
        if (!HasHotspot)
        {
            return Report("Enter the hotspot name first.", error: true);
        }
        if (password.Length == 0)
        {
            return Report("Type the password first.", error: true);
        }
        string? error;
        try
        {
            error = _services.SaveHotspotPassword(_draft.HotspotSsid, password);
        }
        catch (Exception ex)
        {
            Log.Error("settings: saving the hotspot password failed", ex);
            error = ex.Message;
        }
        ReadHotspotPassword();
        return error is null
            ? Report("Password saved in Credential Manager.", error: false)
            : Report("Couldn't save the password: " + error, error: true);
    }

    public string? ClearHotspotPassword()
    {
        if (!HasHotspot)
        {
            return null;
        }
        string? error;
        try
        {
            error = _services.ClearHotspotPassword(_draft.HotspotSsid);
        }
        catch (Exception ex)
        {
            Log.Error("settings: clearing the hotspot password failed", ex);
            error = ex.Message;
        }
        ReadHotspotPassword();
        return error is null
            ? Report("Password removed.", error: false)
            : Report("Couldn't remove the password: " + error, error: true);
    }

    /// <summary>Re-read state that lives outside config.json (Task Manager can change startup at any time).</summary>
    public void RefreshExternal()
    {
        ReadStartup();
        ReadHotspotPassword();
    }

    /// <summary>
    /// Save any pending or previously refused change now (the window is
    /// closing, or the app is quitting) and wait for it.
    /// </summary>
    public async Task FlushAsync()
    {
        if (_saveTimer.IsEnabled || _saveState == SaveState.Failed)
        {
            _saveTimer.Stop();
            await SaveAsync();
        }
        while (_saving)
        {
            await Task.Delay(25);
        }
    }

    #endregion

    private string? AddName(string text, List<string> current, Func<List<string>, Config> apply)
    {
        var name = NormalizeExe(text, out var error);
        if (name is null)
        {
            return error;
        }
        if (current.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return $"{name} is already listed.";
        }
        Update(apply([.. current, name]));
        return null;
    }

    /// <summary>"Slack" becomes "Slack.exe"; a full path becomes its file name.</summary>
    private static string? NormalizeExe(string text, out string? error)
    {
        error = null;
        var name = (text ?? "").Trim().Trim('"');
        if (name.Length == 0)
        {
            error = "Enter an app's executable name, for example slack.exe.";
            return null;
        }
        if (name.Contains('\\', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal))
        {
            name = Path.GetFileName(name);
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Length == 0)
        {
            error = "That isn't a valid file name. Use the executable name, for example slack.exe.";
            return null;
        }
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".exe";
        }
        return name;
    }

    private static string Friendly(string image) => image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;

    private string? Report(string message, bool error)
    {
        HotspotMessageIsError = error;
        HotspotMessage = message;
        return error ? message : null;
    }

    private void SetLaunchAtLogin(bool enabled)
    {
        string? error;
        try
        {
            error = _services.SetStartup(enabled);
        }
        catch (Exception ex)
        {
            Log.Error("settings: changing launch at login failed", ex);
            error = ex.Message;
        }
        StartupError = error is null ? null : "Couldn't change launch at login: " + error;
        OnPropertyChanged(nameof(ShowStartupNote));
        ReadStartup();
        if (error is null)
        {
            Update(_draft with { LaunchAtLogin = enabled });
        }
    }

    private void ReadStartup()
    {
        try
        {
            _startup = _services.ReadStartup();
        }
        catch (Exception ex)
        {
            Log.Error("settings: reading launch at login failed", ex);
            _startup = StartupState.NotRegistered;
        }
        OnPropertyChanged(nameof(LaunchAtLogin));
        OnPropertyChanged(nameof(StartupDisabledByUser));
        OnPropertyChanged(nameof(ShowStartupNote));
    }

    private void ReadHotspotPassword()
    {
        try
        {
            HasHotspotPassword = HasHotspot && _services.HasHotspotPassword(_draft.HotspotSsid);
        }
        catch (Exception ex)
        {
            Log.Error("settings: reading the hotspot password state failed", ex);
            HasHotspotPassword = false;
        }
        OnPropertyChanged(nameof(HasHotspot));
        OnPropertyChanged(nameof(HotspotPasswordStatus));
    }

    private void Update(Config next)
    {
        if (next.Equals(_draft))
        {
            // A stepper may need to snap back to the value the draft kept.
            OnPropertyChanged(nameof(LowPowerFloor));
            OnPropertyChanged(nameof(EndFloor));
            OnPropertyChanged(nameof(NudgeThreshold));
            return;
        }
        var freezeChanged = next.FreezeAllApps != _draft.FreezeAllApps || !next.FreezeList.SequenceEqual(_draft.FreezeList) || !next.AgentList.SequenceEqual(_draft.AgentList);
        Adopt(next);
        SaveState = SaveState.Pending;
        _saveTimer.Stop();
        _saveTimer.Start();
        if (freezeChanged)
        {
            _ = RefreshWouldFreezeAsync();
        }
    }

    private void OnSaveTimer(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (_saving)
        {
            _saveAgain = true;
            return;
        }
        _saving = true;
        SaveState = SaveState.Saving;
        var generation = _generation;
        var draft = _draft;
        try
        {
            var error = await _controller.SaveConfigAsync(draft);
            if (error is not null)
            {
                Log.Warn("settings: save refused: " + error);
                SaveError = error;
                SaveState = SaveState.Failed;
            }
            else
            {
                SaveError = null;
                SaveState = generation == _generation ? SaveState.Saved : SaveState.Pending;
                if (generation == _generation)
                {
                    // Adopt the normalised config (sorted presets, trimmed names),
                    // notifying only what normalising changed.
                    Adopt(_controller.Config, countAsEdit: false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("settings: save failed", ex);
            SaveError = ex.Message;
            SaveState = SaveState.Failed;
        }
        finally
        {
            _saving = false;
            if (_saveAgain)
            {
                _saveAgain = false;
                _ = SaveAsync();
            }
        }
    }

    /// <summary>
    /// Replace the draft and notify only the properties whose values changed,
    /// so a text box the user is still typing in is never refreshed by an
    /// unrelated change.
    /// </summary>
    private void Adopt(Config next, bool countAsEdit = true)
    {
        var previous = _draft;
        _draft = next;
        if (countAsEdit)
        {
            _generation++;
        }
        SyncCollections();
        foreach (var (name, read) in Projections)
        {
            if (!Equals(read(previous), read(next)))
            {
                OnPropertyChanged(name);
            }
        }
    }

    /// <summary>Each bindable property and the config value it shows.</summary>
    private static readonly (string Name, Func<Config, object> Read)[] Projections =
    [
        (nameof(DefaultPresetLabel), c => c.DefaultPreset),
        (nameof(MaxDurationLabel), c => c.MaxDuration),
        (nameof(FreezeAllApps), c => c.FreezeAllApps),
        (nameof(DockerRule), c => c.DockerRule),
        (nameof(MuteOnLidClose), c => c.MuteOnLidClose),
        (nameof(DisplaysOffOnLidClose), c => c.DisplaysOffOnLidClose),
        (nameof(PowerSavingOnLidClose), c => c.PowerSavingOnLidClose),
        (nameof(DisableThrottlingForAgents), c => c.DisableThrottlingForAgents),
        (nameof(LowPowerFloor), c => c.LowPowerFloor),
        (nameof(EndFloor), c => c.EndFloor),
        (nameof(ThermalRules), c => c.ThermalRules),
        (nameof(HotspotSsid), c => c.HotspotSsid),
        (nameof(HasHotspot), c => c.HotspotSsid.Length > 0),
        (nameof(HotspotPasswordStatus), c => c.HotspotSsid),
        (nameof(NudgeThreshold), c => c.NudgeThreshold),
        (nameof(TmuxNudgePressesEnter), c => c.TmuxNudgePressesEnter),
        (nameof(TmuxCommand), c => c.TmuxCommand),
    ];

    private void SyncCollections()
    {
        var chips = _draft.Presets.Select(p => new PresetChip(p, SessionMath.FormatPreset(p), p.Equals(_draft.DefaultPreset))).ToList();
        Sync(Presets, chips);
        Sync(FreezeList, _draft.FreezeList);
        Sync(AgentList, _draft.AgentList);
        Sync(TmuxTargets, _draft.TmuxTargets);
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source))
        {
            return;
        }
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
