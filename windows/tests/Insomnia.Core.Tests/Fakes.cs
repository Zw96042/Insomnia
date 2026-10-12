using Insomnia.Core.Logic;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;
using Insomnia.Core.Store;

namespace Insomnia.Core.Tests;

/// <summary>A temporary data folder, deleted on dispose.</summary>
public sealed class TempHome : IDisposable
{
    public TempHome()
    {
        Root = Path.Combine(Path.GetTempPath(), "insomnia-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new Paths(Root);
        Paths.EnsureFolders();
        Store = new JournalStore(Paths);
        Lock = new RecoveryLock(Paths.Lock);
    }

    public string Root { get; }

    public Paths Paths { get; }

    public JournalStore Store { get; }

    public RecoveryLock Lock { get; }

    public RuntimeState ReadState() => ((StateRead.Valid)Store.ReadState()).State;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Records the order of side effects across fakes.</summary>
public sealed class CallLog
{
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public void Add(string call)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }
    }

    public int IndexOf(string call) => Calls.ToList().IndexOf(call);
}

public sealed class FakePowerEvents : IPowerEvents
{
    public bool? LidClosed { get; set; } = false;

    public event EventHandler<bool>? LidChanged;

    public event EventHandler? PowerSourceChanged;

    public event EventHandler? ActiveSchemeChanged;

    public event EventHandler? Resumed;

    public void SetLid(bool closed)
    {
        LidClosed = closed;
        LidChanged?.Invoke(this, closed);
    }

    public void RaisePowerSource() => PowerSourceChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseScheme() => ActiveSchemeChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);
}

public sealed class FakeKeepAwake(CallLog log) : IKeepAwake
{
    public bool Refuse { get; set; }

    public bool IsHeld { get; private set; }

    public bool Acquire(string reason)
    {
        if (Refuse)
        {
            return false;
        }
        IsHeld = true;
        log.Add("keepawake:acquire");
        return true;
    }

    public void Release()
    {
        IsHeld = false;
        log.Add("keepawake:release");
    }
}

public sealed class FakeLidPolicy(CallLog log, Func<RuntimeState>? journal = null) : ILidPolicy
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public Dictionary<Guid, (uint Ac, uint Dc)> Schemes { get; } = new() { [Balanced] = (LidActions.Sleep, LidActions.Sleep) };

    public Guid Active { get; set; } = Balanced;

    public bool HasLid { get; set; } = true;

    public bool RefuseWrites { get; set; }

    /// <summary>Journal state as on disk at the moment of each write, to check journal-first ordering.</summary>
    public List<RuntimeState> JournalAtWrite { get; } = [];

    public Guid? ActiveScheme() => Active;

    /// <summary>Reads fail without saying the scheme is gone.</summary>
    public bool FailReads { get; set; }

    /// <summary>Writes store the values but fail to re-apply the scheme.</summary>
    public bool FailActivation { get; set; }

    /// <summary>The values in effect: what the last successful activation applied.</summary>
    public Dictionary<Guid, (uint Ac, uint Dc)> Effective { get; } = [];

    public LidRead Read(Guid scheme) =>
        FailReads ? LidRead.Failure
        : Schemes.TryGetValue(scheme, out var v) ? new LidRead(LidReadStatus.Ok, v.Ac, v.Dc)
        : LidRead.Missing;

    public bool Write(Guid scheme, uint ac, uint dc)
    {
        if (journal is not null)
        {
            JournalAtWrite.Add(journal());
        }
        if (RefuseWrites || !Schemes.ContainsKey(scheme))
        {
            return false;
        }
        Schemes[scheme] = (ac, dc);
        if (FailActivation)
        {
            return false;
        }
        Effective[scheme] = (ac, dc);
        log.Add($"lid:write:{ac}/{dc}");
        return true;
    }
}

public sealed class FakePowerMode(CallLog log) : IPowerMode
{
    public bool IsSupported { get; set; } = true;

    public PowerSourceKind? Source { get; set; } = PowerSourceKind.Dc;

    public Guid Ac { get; set; } = PowerOverlays.Balanced;

    public Guid Dc { get; set; } = PowerOverlays.Balanced;

    /// <summary>When true, setting one source sets both, as some Windows builds may.</summary>
    public bool SetsBoth { get; set; }

    public bool Refuse { get; set; }

    /// <summary>Reads fail (return null for both sources).</summary>
    public bool FailReads { get; set; }

    public PowerSourceKind? CurrentSource() => Source;

    public (Guid? Ac, Guid? Dc) Read() => FailReads ? (null, null) : (Ac, Dc);

    /// <summary>The power source flips just before the next write lands.</summary>
    public bool SwitchSourceOnNextSet { get; set; }

    /// <summary>The write lands, but reads fail from then on and the call reports failure (an unverifiable write).</summary>
    public bool FailReadsAfterNextSet { get; set; }

    public bool SetForCurrentSource(Guid overlay)
    {
        if (Refuse || Source is null)
        {
            return false;
        }
        if (SwitchSourceOnNextSet)
        {
            // The charger changes between the caller's source check and the write.
            SwitchSourceOnNextSet = false;
            Source = Source == PowerSourceKind.Ac ? PowerSourceKind.Dc : PowerSourceKind.Ac;
        }
        if (FailReadsAfterNextSet)
        {
            FailReadsAfterNextSet = false;
            FailReads = true;
            if (Source == PowerSourceKind.Ac || SetsBoth)
            {
                Ac = overlay;
            }
            if (Source == PowerSourceKind.Dc || SetsBoth)
            {
                Dc = overlay;
            }
            return false;
        }
        if (Source == PowerSourceKind.Ac || SetsBoth)
        {
            Ac = overlay;
        }
        if (Source == PowerSourceKind.Dc || SetsBoth)
        {
            Dc = overlay;
        }
        log.Add($"power:{Source}:{PowerOverlays.Describe(overlay)}");
        return true;
    }
}

public sealed class FakePowerSource : IPowerSource
{
    public BatteryReading Reading { get; set; } = new(true, false, 80, -5000);

    public BatteryReading Read() => Reading;
}

public sealed class FakeThermal : IThermalSensor
{
    public bool IsAvailable { get; set; } = true;

    public ThermalState State { get; set; } = ThermalState.Nominal;

    public ThermalState Read() => State;
}

public sealed class FakeProcesses : IProcessCatalog
{
    public List<ProcessInfo> Processes { get; } = [];

    public int SelfPid { get; set; } = 9999;

    public string WindowsDirectory { get; set; } = @"C:\Windows";

    public IReadOnlyList<ProcessInfo> Snapshot() => [.. Processes];

    public ProcessInfo Add(int pid, string image, int parent = 1, bool window = false, string? path = null, long? created = null, bool currentUser = true)
    {
        var info = new ProcessInfo(pid, parent, created ?? 1000 + pid, image, path ?? $@"C:\Program Files\{Path.GetFileNameWithoutExtension(image)}\{image}",
            Path.GetFileNameWithoutExtension(image), currentUser, window);
        Processes.Add(info);
        return info;
    }
}

public sealed class FakeFreezer(CallLog log) : IProcessFreezer
{
    public HashSet<(int Pid, long Created)> Suspended { get; } = [];

    public HashSet<int> Refuse { get; } = [];

    public HashSet<int> Gone { get; } = [];

    public SuspendOutcome Suspend(int pid, long creationTime)
    {
        if (Gone.Contains(pid))
        {
            return SuspendOutcome.Gone;
        }
        if (Refuse.Contains(pid))
        {
            return SuspendOutcome.AccessDenied;
        }
        Suspended.Add((pid, creationTime));
        log.Add($"suspend:{pid}");
        return SuspendOutcome.Suspended;
    }

    public ResumeOutcome Resume(int pid, long creationTime)
    {
        if (Gone.Contains(pid))
        {
            return ResumeOutcome.Gone;
        }
        if (Refuse.Contains(pid))
        {
            return ResumeOutcome.AccessDenied;
        }
        Suspended.Remove((pid, creationTime));
        log.Add($"resume:{pid}");
        return ResumeOutcome.Resumed;
    }

    /// <summary>Pids whose inspection fails.</summary>
    public HashSet<int> InspectFails { get; } = [];

    public ProcessState Inspect(int pid, long creationTime) =>
        InspectFails.Contains(pid) ? ProcessState.Unknown
        : Gone.Contains(pid) ? ProcessState.Gone
        : Suspended.Contains((pid, creationTime)) ? ProcessState.FullySuspended
        : ProcessState.Running;
}

public sealed class FakeThrottle : IThrottleControl
{
    public Dictionary<int, (uint Control, uint State)> Masks { get; } = [];

    public (uint ControlMask, uint StateMask)? Get(int pid, long creationTime) =>
        Masks.TryGetValue(pid, out var m) ? (m.Control, m.State) : null;

    public bool Set(int pid, long creationTime, uint controlMask, uint stateMask)
    {
        if (!Masks.ContainsKey(pid))
        {
            return false;
        }
        Masks[pid] = (controlMask, stateMask);
        return true;
    }
}

public sealed class FakeAudio(CallLog log) : IAudioControl
{
    public Dictionary<string, (string Name, float Volume, bool Muted, AudioDeviceStatus Status)> Devices { get; } = new()
    {
        ["spk"] = ("Speakers", 0.6f, false, AudioDeviceStatus.Connected),
    };

    public string? Default { get; set; } = "spk";

    public bool RefuseSets { get; set; }

    public event EventHandler? DevicesChanged;

    public AudioEndpoint? DefaultOutput() =>
        Default is { } id && Devices.TryGetValue(id, out var d) && d.Status == AudioDeviceStatus.Connected
            ? new AudioEndpoint(id, d.Name, d.Volume, d.Muted)
            : null;

    public AudioDeviceStatus Status(string deviceId) =>
        Devices.TryGetValue(deviceId, out var d) ? d.Status : AudioDeviceStatus.Unknown;

    public AudioEndpoint? Read(string deviceId) =>
        Devices.TryGetValue(deviceId, out var d) && d.Status == AudioDeviceStatus.Connected
            ? new AudioEndpoint(deviceId, d.Name, d.Volume, d.Muted)
            : null;

    public bool SetMute(string deviceId, bool muted)
    {
        if (RefuseSets || !Devices.TryGetValue(deviceId, out var d) || d.Status != AudioDeviceStatus.Connected)
        {
            return false;
        }
        Devices[deviceId] = d with { Muted = muted };
        log.Add($"mute:{deviceId}:{muted}");
        return true;
    }

    public bool SetVolume(string deviceId, float volume)
    {
        if (RefuseSets || !Devices.TryGetValue(deviceId, out var d) || d.Status != AudioDeviceStatus.Connected)
        {
            return false;
        }
        Devices[deviceId] = d with { Volume = volume };
        return true;
    }

    public void SetStatus(string deviceId, AudioDeviceStatus status)
    {
        Devices[deviceId] = Devices[deviceId] with { Status = status };
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class FakeDisplay(CallLog log) : IDisplayControl
{
    public void RequestDisplaysOff() => log.Add("display:off");

    public void WakeDisplays() => log.Add("display:wake");
}

public sealed class FakeDocker(FakeProcesses processes) : IDocker
{
    public Queue<DockerProbeResult> Results { get; } = new();

    /// <summary>When set, a probe waits for this before answering.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int Probes { get; private set; }

    public IReadOnlyList<ProcessInfo> DesktopProcesses() =>
        processes.Processes.Where(p => p.Image.Equals("Docker Desktop.exe", StringComparison.OrdinalIgnoreCase)
            || p.Image.StartsWith("com.docker.", StringComparison.OrdinalIgnoreCase)).ToList();

    public async Task<DockerProbeResult> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Probes++;
        if (Gate is { } gate)
        {
            try
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return DockerProbeResult.Failed;
            }
        }
        return Results.Count > 0 ? Results.Dequeue() : DockerProbeResult.NoContainers;
    }
}

public sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Body)> Posted { get; } = [];

    public void Notify(string title, string body)
    {
        lock (Posted)
        {
            Posted.Add((title, body));
        }
    }
}

public sealed class FakeRecoveryAgent : IRecoveryAgent
{
    public RecoveryAgentState State { get; set; } = RecoveryAgentState.Armed;

    public bool ArmFails { get; set; }

    public int Arms { get; private set; }

    public RecoveryAgentState Check() => State;

    public (RecoveryAgentState State, string? Error) Arm()
    {
        Arms++;
        if (ArmFails)
        {
            return (RecoveryAgentState.Missing, "schtasks failed");
        }
        State = RecoveryAgentState.Armed;
        return (State, null);
    }

    public bool Remove()
    {
        State = RecoveryAgentState.Missing;
        return true;
    }
}

public sealed class FakeStartup : IStartupRegistration
{
    public StartupState State { get; set; } = StartupState.NotRegistered;

    public StartupState Read() => State;

    public string? Set(bool enabled)
    {
        State = enabled ? StartupState.Enabled : StartupState.NotRegistered;
        return null;
    }
}

public sealed class FakeCredentials : ICredentialStore
{
    public Dictionary<string, string> Passwords { get; } = [];

    public string? ReadHotspotPassword(string ssid) => Passwords.TryGetValue(ssid, out var p) ? p : null;

    public string? WriteHotspotPassword(string ssid, string password)
    {
        Passwords[ssid] = password;
        return null;
    }

    public string? DeleteHotspotPassword(string ssid)
    {
        Passwords.Remove(ssid);
        return null;
    }
}

public sealed class FakeWifi : IWifi
{
    public WifiConnectivity State { get; set; } = WifiConnectivity.Satisfied;

    public List<string> Joins { get; } = [];

    public HotspotJoinResult JoinResult { get; set; } = HotspotJoinResult.Joined;

    public event EventHandler? ConnectivityChanged;

    public WifiConnectivity Connectivity() => State;

    public string? CurrentSsid() => State == WifiConnectivity.Satisfied ? "Home" : null;

    public Task<bool> RequestAccessAsync() => Task.FromResult(true);

    public Task<HotspotJoinResult> JoinAsync(string ssid, string password, CancellationToken cancellationToken)
    {
        lock (Joins)
        {
            Joins.Add($"{ssid}:{password}");
        }
        return Task.FromResult(JoinResult);
    }

    public void Set(WifiConnectivity state)
    {
        State = state;
        ConnectivityChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class FakeCommands : ICommandRunner
{
    public List<IReadOnlyList<string>> Runs { get; } = [];

    public Func<IReadOnlyList<string>, CommandResult> Handler { get; set; } = _ => new CommandResult(0, "", "", false);

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var all = new List<string> { fileName };
        all.AddRange(arguments);
        lock (Runs)
        {
            Runs.Add(all);
        }
        return Task.FromResult(Handler(all));
    }
}

public sealed class FakeBrowsers : IBrowserControl
{
    public List<BrowserInstance> Instances { get; } = [];

    public BrowserRelaunchResult Result { get; set; } = BrowserRelaunchResult.Relaunched;

    public IReadOnlyList<BrowserInstance> Scan() => [.. Instances];

    public Task<BrowserRelaunchResult> RelaunchAfterExitAsync(string image, TimeSpan wait, CancellationToken cancellationToken) => Task.FromResult(Result);
}

/// <summary>Every fake, wired into one <see cref="PlatformServices"/>.</summary>
public sealed class FakeOs
{
    public FakeOs(Func<RuntimeState>? journal = null)
    {
        Log = new CallLog();
        Events = new FakePowerEvents();
        KeepAwake = new FakeKeepAwake(Log);
        Lid = new FakeLidPolicy(Log, journal);
        Power = new FakePowerMode(Log);
        Battery = new FakePowerSource();
        Thermal = new FakeThermal();
        Processes = new FakeProcesses();
        Freezer = new FakeFreezer(Log);
        Throttle = new FakeThrottle();
        Audio = new FakeAudio(Log);
        Display = new FakeDisplay(Log);
        Docker = new FakeDocker(Processes);
        Notifier = new FakeNotifier();
        Agent = new FakeRecoveryAgent();
        Startup = new FakeStartup();
        Credentials = new FakeCredentials();
        Wifi = new FakeWifi();
        Commands = new FakeCommands();
        Browsers = new FakeBrowsers();
        Services = new PlatformServices(Events, KeepAwake, Lid, Power, Battery, Thermal, Processes, Freezer, Throttle, Audio, Display, Docker,
            Notifier, Agent, Startup, Credentials, Wifi, Commands, Browsers);
    }

    public CallLog Log { get; }

    public FakePowerEvents Events { get; }

    public FakeKeepAwake KeepAwake { get; }

    public FakeLidPolicy Lid { get; }

    public FakePowerMode Power { get; }

    public FakePowerSource Battery { get; }

    public FakeThermal Thermal { get; }

    public FakeProcesses Processes { get; }

    public FakeFreezer Freezer { get; }

    public FakeThrottle Throttle { get; }

    public FakeAudio Audio { get; }

    public FakeDisplay Display { get; }

    public FakeDocker Docker { get; }

    public FakeNotifier Notifier { get; }

    public FakeRecoveryAgent Agent { get; }

    public FakeStartup Startup { get; }

    public FakeCredentials Credentials { get; }

    public FakeWifi Wifi { get; }

    public FakeCommands Commands { get; }

    public FakeBrowsers Browsers { get; }

    public PlatformServices Services { get; }
}
