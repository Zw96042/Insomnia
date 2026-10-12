using Insomnia.Core.Engine;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;
using Insomnia.Core.Store;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Insomnia.Core.Tests;

public sealed class EngineHarness : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);

    public EngineHarness(Action<EngineHarness>? arrange = null)
    {
        Home = new TempHome();
        Os = new FakeOs(() => Home.ReadState());
        Time = new FakeTimeProvider(T0);
        Os.Processes.Add(10, "slack.exe", window: true);
        Os.Processes.Add(11, "slack.exe", parent: 10);
        Os.Processes.Add(20, "Code.exe", window: true);
        arrange?.Invoke(this);
        Engine = new SessionEngine(Os.Services, Home.Store, Home.Lock, new EngineOptions(), Time);
    }

    public TempHome Home { get; }

    public FakeOs Os { get; }

    public FakeTimeProvider Time { get; }

    public SessionEngine Engine { get; }

    public RuntimeState State => Home.ReadState();

    public async Task Init() => await Engine.InitializeAsync();

    public async Task Settle(Func<bool>? until = null)
    {
        for (var i = 0; i < 200; i++)
        {
            await Engine.IdleAsync();
            if (until is null ? i >= 2 : until())
            {
                return;
            }
            await Task.Delay(5);
        }
        Assert.Fail("condition not reached");
    }

    public async Task Lid(bool closed)
    {
        Os.Events.SetLid(closed);
        Time.Advance(TimeSpan.FromSeconds(2));
        await Settle();
    }

    public async Task Start(TimeSpan? duration = null)
    {
        await Init();
        var result = await Engine.StartAsync(duration ?? TimeSpan.FromHours(4));
        Assert.Equal(StartOutcome.Started, result.Outcome);
    }

    public void Dispose()
    {
        Engine.Dispose();
        Home.Dispose();
    }
}

public class EngineStartTests
{
    [Fact]
    public async Task StartJournalsLidActionBeforeChangingIt()
    {
        using var h = new EngineHarness();
        await h.Start();

        var atWrite = Assert.Single(h.Os.Lid.JournalAtWrite);
        var entry = Assert.Single(atWrite.LidPolicy);
        Assert.Equal(LidActions.Sleep, entry.PreviousAc);
        Assert.Equal((LidActions.DoNothing, LidActions.DoNothing), h.Os.Lid.Schemes[FakeLidPolicy.Balanced]);
        Assert.True(h.Os.KeepAwake.IsHeld);
        Assert.IsType<SessionRead.Valid>(h.Home.Store.ReadSession());
        Assert.Equal(SessionPhase.Active, h.Engine.Status.Phase);
        Assert.Equal(1, h.Os.Agent.Arms);
    }

    [Fact]
    public async Task StartIsRefusedWithoutRecoveryAgent()
    {
        using var h = new EngineHarness(x => x.Os.Agent.ArmFails = true);
        await h.Init();
        var result = await h.Engine.StartAsync(TimeSpan.FromHours(1));
        Assert.Equal(StartOutcome.Refused, result.Outcome);
        Assert.IsType<SessionRead.Missing>(h.Home.Store.ReadSession());
        Assert.Equal((LidActions.Sleep, LidActions.Sleep), h.Os.Lid.Schemes[FakeLidPolicy.Balanced]);
        Assert.False(h.Os.KeepAwake.IsHeld);
    }

    [Fact]
    public async Task FailedLidWriteUnwindsTheStart()
    {
        using var h = new EngineHarness(x => x.Os.Lid.RefuseWrites = true);
        await h.Init();
        var result = await h.Engine.StartAsync(TimeSpan.FromHours(1));
        Assert.Equal(StartOutcome.Failed, result.Outcome);
        Assert.IsType<SessionRead.Missing>(h.Home.Store.ReadSession());
        Assert.False(h.Os.KeepAwake.IsHeld);
        Assert.Null(h.Engine.Status.Session);
    }

    [Fact]
    public async Task UnreadableJournalRefusesStart()
    {
        using var h = new EngineHarness();
        await h.Init();
        File.WriteAllText(h.Home.Paths.State, "{ broken");
        var result = await h.Engine.StartAsync(TimeSpan.FromHours(1));
        Assert.Equal(StartOutcome.Refused, result.Outcome);
        Assert.Contains(h.Engine.Status.Warnings, w => w.Id == "journal");
    }

    [Fact]
    public async Task NoLidMeansNoLidPolicy()
    {
        using var h = new EngineHarness(x => x.Os.Lid.HasLid = false);
        await h.Start();
        Assert.Empty(h.Os.Lid.JournalAtWrite);
        Assert.True(h.Os.KeepAwake.IsHeld);
    }

    [Fact]
    public async Task ExtendMovesDeadline()
    {
        using var h = new EngineHarness();
        await h.Start(TimeSpan.FromHours(1));
        Assert.True(await h.Engine.ExtendAsync(TimeSpan.FromMinutes(30)));
        var session = Assert.IsType<SessionRead.Valid>(h.Home.Store.ReadSession()).Session;
        Assert.Equal(EngineHarness.T0.AddMinutes(90), session.EndsAt);
        h.Time.Advance(TimeSpan.FromMinutes(61));
        await h.Settle();
        Assert.NotNull(h.Engine.Status.Session);
    }
}

public class EngineLidTests
{
    [Fact]
    public async Task LidCloseAppliesActionsAndOpenUndoesThem()
    {
        using var h = new EngineHarness();
        await h.Start();
        await h.Lid(closed: true);

        var state = h.State;
        Assert.Equal([10, 11], state.FrozenProcesses.Select(p => p.Pid).Order());
        Assert.All(state.FrozenProcesses, p => Assert.True(p.Confirmed));
        Assert.True(h.Os.Audio.Devices["spk"].Muted);
        Assert.Single(state.SavedAudioOutputs);
        Assert.Equal(PowerOverlays.BestPowerEfficiency, h.Os.Power.Dc);
        Assert.True(state.PowerSavingSetByUs);
        Assert.Contains("display:off", h.Os.Log.Calls);
        Assert.True(h.Engine.Status.LidClosed);
        Assert.Contains(10, h.Os.Freezer.Suspended.Select(s => s.Pid));

        await h.Lid(closed: false);
        state = h.State;
        Assert.Empty(state.FrozenProcesses);
        Assert.Empty(state.SavedAudioOutputs);
        Assert.False(state.PowerSavingSetByUs);
        Assert.Empty(h.Os.Freezer.Suspended);
        Assert.False(h.Os.Audio.Devices["spk"].Muted);
        Assert.Equal(0.6f, h.Os.Audio.Devices["spk"].Volume);
        Assert.Equal(PowerOverlays.Balanced, h.Os.Power.Dc);
        Assert.Contains("display:wake", h.Os.Log.Calls);
        Assert.Single(state.LidPolicy);
    }

    [Fact]
    public async Task LidEventsDoNothingWithoutASession()
    {
        using var h = new EngineHarness();
        await h.Init();
        await h.Lid(closed: true);
        Assert.Empty(h.Os.Freezer.Suspended);
        Assert.False(h.Os.Audio.Devices["spk"].Muted);
    }

    [Fact]
    public async Task FlappingWithinDebounceDoesNothing()
    {
        using var h = new EngineHarness();
        await h.Start();
        h.Os.Events.SetLid(true);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Os.Events.SetLid(false);
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await h.Settle();
        Assert.Empty(h.Os.Freezer.Suspended);
    }

    [Fact]
    public async Task UnmutedMeanwhileIsLeftAlone()
    {
        using var h = new EngineHarness();
        await h.Start();
        await h.Lid(closed: true);
        h.Os.Audio.Devices["spk"] = h.Os.Audio.Devices["spk"] with { Muted = false, Volume = 0.2f };
        await h.Lid(closed: false);
        Assert.Equal(0.2f, h.Os.Audio.Devices["spk"].Volume);
        Assert.Empty(h.State.SavedAudioOutputs);
    }

    [Fact]
    public async Task AlreadyMutedDeviceIsNotSaved()
    {
        using var h = new EngineHarness();
        h.Os.Audio.Devices["spk"] = h.Os.Audio.Devices["spk"] with { Muted = true };
        await h.Start();
        await h.Lid(closed: true);
        Assert.Empty(h.State.SavedAudioOutputs);
    }

    [Fact]
    public async Task DockerIsPausedOnlyAfterTwoCleanChecks()
    {
        using var h = new EngineHarness(x =>
        {
            x.Os.Processes.Add(30, "Docker Desktop.exe", window: true);
            x.Os.Processes.Add(31, "com.docker.backend.exe", parent: 30);
            x.Home.Store.WriteConfig(new Config() with { DockerRule = true });
        });
        await h.Start();
        h.Os.Docker.Results.Enqueue(DockerProbeResult.NoContainers);
        h.Os.Docker.Results.Enqueue(DockerProbeResult.NoContainers);
        await h.Lid(closed: true);
        Assert.True(h.State.DockerFrozen);
        Assert.Equal(2, h.Os.Docker.Probes);
        Assert.Contains(31, h.Os.Freezer.Suspended.Select(s => s.Pid));

        await h.Lid(closed: false);
        Assert.False(h.State.DockerFrozen);
        Assert.Empty(h.Os.Freezer.Suspended);
    }

    [Fact]
    public async Task DockerBusyOnSecondCheckIsLeftRunning()
    {
        using var h = new EngineHarness(x =>
        {
            x.Os.Processes.Add(30, "Docker Desktop.exe", window: true);
            x.Home.Store.WriteConfig(new Config() with { DockerRule = true });
        });
        await h.Start();
        h.Os.Docker.Results.Enqueue(DockerProbeResult.NoContainers);
        h.Os.Docker.Results.Enqueue(DockerProbeResult.Busy);
        await h.Lid(closed: true);
        Assert.False(h.State.DockerFrozen);
        Assert.DoesNotContain(h.State.FrozenProcesses, p => p.Group == Restorer.DockerGroup);
        Assert.DoesNotContain(30, h.Os.Freezer.Suspended.Select(s => s.Pid));
    }

    [Fact]
    public async Task LidOpenCancelsDockerProbe()
    {
        using var h = new EngineHarness(x =>
        {
            x.Os.Processes.Add(30, "Docker Desktop.exe", window: true);
            x.Home.Store.WriteConfig(new Config() with { DockerRule = true });
        });
        await h.Start();
        h.Os.Docker.Gate = new TaskCompletionSource();
        h.Os.Events.SetLid(true);
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        h.Os.Events.SetLid(false);
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await h.Settle();
        Assert.False(h.State.DockerFrozen);
        Assert.DoesNotContain(30, h.Os.Freezer.Suspended.Select(s => s.Pid));
        Assert.Empty(h.Os.Freezer.Suspended);
        Assert.False(h.Engine.Status.LidClosed);
    }
}

public class EngineEndTests
{
    [Fact]
    public async Task EndRestoresEverythingAndReleasesKeepAwakeLast()
    {
        using var h = new EngineHarness();
        await h.Start();
        await h.Lid(closed: true);
        var outcome = await h.Engine.EndAsync(EndReason.User);

        Assert.Equal(EndOutcome.Restored, outcome);
        Assert.True(h.State.IsEmpty);
        Assert.Equal((LidActions.Sleep, LidActions.Sleep), h.Os.Lid.Schemes[FakeLidPolicy.Balanced]);
        Assert.False(h.Os.KeepAwake.IsHeld);
        Assert.IsType<SessionRead.Missing>(h.Home.Store.ReadSession());
        Assert.Empty(h.Os.Freezer.Suspended);
        var calls = h.Os.Log.Calls.ToList();
        Assert.True(calls.LastIndexOf("lid:write:1/1") < calls.LastIndexOf("keepawake:release"));
        Assert.True(calls.LastIndexOf("resume:10") < calls.LastIndexOf("lid:write:1/1"));
        Assert.Contains(h.Os.Notifier.Posted, n => n.Title == "Session ended");
        Assert.Equal(SessionPhase.Idle, h.Engine.Status.Phase);
    }

    [Fact]
    public async Task LidActionTheUserChangedIsKept()
    {
        using var h = new EngineHarness();
        await h.Start();
        h.Os.Lid.Schemes[FakeLidPolicy.Balanced] = (LidActions.Hibernate, LidActions.DoNothing);
        await h.Engine.EndAsync(EndReason.User);
        Assert.Equal((LidActions.Hibernate, LidActions.Sleep), h.Os.Lid.Schemes[FakeLidPolicy.Balanced]);
        Assert.Empty(h.State.LidPolicy);
    }

    [Fact]
    public async Task NewSchemeDuringSessionIsJournaledAndRestored()
    {
        using var h = new EngineHarness();
        var other = Guid.NewGuid();
        h.Os.Lid.Schemes[other] = (LidActions.Hibernate, LidActions.ShutDown);
        await h.Start();
        h.Os.Lid.Active = other;
        h.Os.Events.RaiseScheme();
        await h.Settle(() => h.Os.Lid.Schemes[other] == (LidActions.DoNothing, LidActions.DoNothing));
        Assert.Equal(2, h.State.LidPolicy.Count);
        await h.Engine.EndAsync(EndReason.User);
        Assert.Equal((LidActions.Hibernate, LidActions.ShutDown), h.Os.Lid.Schemes[other]);
        Assert.Equal((LidActions.Sleep, LidActions.Sleep), h.Os.Lid.Schemes[FakeLidPolicy.Balanced]);
    }

    [Fact]
    public async Task DeadlineEndsTheSession()
    {
        using var h = new EngineHarness();
        await h.Start(TimeSpan.FromMinutes(30));
        h.Time.Advance(TimeSpan.FromMinutes(25));
        await h.Settle();
        Assert.Contains(h.Os.Notifier.Posted, n => n.Title == "5 minutes left");
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Settle(() => h.Engine.Status.Session is null);
        Assert.False(h.Os.KeepAwake.IsHeld);
        Assert.Equal((LidActions.Sleep, LidActions.Sleep), h.Os.Lid.Schemes[FakeLidPolicy.Balanced]);
    }

    [Fact]
    public async Task FailedRestoreStaysJournaledAndRetries()
    {
        using var h = new EngineHarness();
        await h.Start();
        await h.Lid(closed: true);
        h.Os.Freezer.Refuse.Add(10);
        var outcome = await h.Engine.EndAsync(EndReason.User);
        Assert.Equal(EndOutcome.Incomplete, outcome);
        Assert.Contains(h.State.FrozenProcesses, p => p.Pid == 10);
        Assert.Contains(h.Engine.Status.Warnings, w => w.Id == "restore");
        Assert.Contains(h.Os.Notifier.Posted, n => n.Title == "Restore incomplete");

        h.Os.Freezer.Refuse.Clear();
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Settle(() => h.State.FrozenProcesses.Count == 0);
        Assert.DoesNotContain(h.Engine.Status.Warnings, w => w.Id == "restore");
    }

    [Fact]
    public async Task AwayAudioDeviceWaitsAndIsRestoredOnReconnect()
    {
        using var h = new EngineHarness();
        await h.Start();
        await h.Lid(closed: true);
        h.Os.Audio.Devices["spk"] = h.Os.Audio.Devices["spk"] with { Status = AudioDeviceStatus.Away };
        var outcome = await h.Engine.EndAsync(EndReason.User);
        Assert.Equal(EndOutcome.Restored, outcome);
        var warning = Assert.Single(h.Engine.Status.Warnings, w => w.Id == "audio:spk");
        Assert.IsType<MenuAction.StopWaitingForAudio>(warning.Action);
        Assert.Single(h.State.SavedAudioOutputs);

        h.Os.Audio.SetStatus("spk", AudioDeviceStatus.Connected);
        await h.Settle(() => h.State.SavedAudioOutputs.Count == 0);
        Assert.False(h.Os.Audio.Devices["spk"].Muted);
        Assert.DoesNotContain(h.Engine.Status.Warnings, w => w.Id == "audio:spk");
    }

    [Fact]
    public async Task StopWaitingDropsTheSave()
    {
        using var h = new EngineHarness();
        await h.Start();
        await h.Lid(closed: true);
        h.Os.Audio.Devices["spk"] = h.Os.Audio.Devices["spk"] with { Status = AudioDeviceStatus.Away };
        await h.Engine.EndAsync(EndReason.User);
        var action = (MenuAction.StopWaitingForAudio)h.Engine.Status.Warnings.Single(w => w.Id == "audio:spk").Action!;
        await h.Engine.PerformAsync(action);
        Assert.Empty(h.State.SavedAudioOutputs);
        Assert.True(h.Os.Audio.Devices["spk"].Muted);
    }

    [Fact]
    public async Task QuitWithDirtyJournalNeedsTheAgent()
    {
        using var h = new EngineHarness();
        await h.Start();
        await h.Lid(closed: true);
        h.Os.Freezer.Refuse.Add(10);
        h.Os.Agent.State = RecoveryAgentState.Missing;
        Assert.False(await h.Engine.PrepareToQuitAsync());
        h.Os.Agent.State = RecoveryAgentState.Armed;
        Assert.True(await h.Engine.PrepareToQuitAsync());
    }

    [Fact]
    public async Task PowerModeTheUserChangedIsKept()
    {
        using var h = new EngineHarness();
        await h.Start();
        await h.Lid(closed: true);
        h.Os.Power.Dc = PowerOverlays.BestPerformance;
        await h.Engine.EndAsync(EndReason.User);
        Assert.Equal(PowerOverlays.BestPerformance, h.Os.Power.Dc);
        Assert.Empty(h.State.PowerMode);
    }

    [Fact]
    public async Task PowerModeForTheOtherSourceWaits()
    {
        using var h = new EngineHarness(x => x.Os.Power.SetsBoth = true);
        await h.Start();
        await h.Lid(closed: true);
        Assert.Equal(2, h.State.PowerMode.Count);
        await h.Engine.EndAsync(EndReason.User);
        Assert.Equal(PowerOverlays.Balanced, h.Os.Power.Dc);
        var waiting = Assert.Single(h.State.PowerMode);
        Assert.Equal(PowerSourceKind.Ac, waiting.Source);

        h.Os.Power.Source = PowerSourceKind.Ac;
        h.Os.Events.RaisePowerSource();
        await h.Settle(() => h.State.PowerMode.Count == 0);
        Assert.Equal(PowerOverlays.Balanced, h.Os.Power.Ac);
    }
}

public class EngineFloorTests
{
    [Fact]
    public async Task BatteryBelowEndFloorEndsTheSession()
    {
        using var h = new EngineHarness();
        await h.Start();
        h.Os.Battery.Reading = new BatteryReading(true, false, 9, -8000);
        h.Os.Events.RaisePowerSource();
        await h.Settle(() => h.Engine.Status.Session is null);
        Assert.Contains(h.Os.Notifier.Posted, n => n.Title == "Battery floor reached");
        Assert.False(h.Os.KeepAwake.IsHeld);
    }

    [Fact]
    public async Task UnreadableBatteryTwiceEndsTheSession()
    {
        using var h = new EngineHarness();
        await h.Start();
        h.Os.Battery.Reading = new BatteryReading(true, false, null, null);
        h.Os.Events.RaisePowerSource();
        await h.Settle();
        Assert.NotNull(h.Engine.Status.Session);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Settle(() => h.Engine.Status.Session is null);
        Assert.Contains(h.Os.Notifier.Posted, n => n.Title == "Battery unreadable");
    }

    [Fact]
    public async Task LowBatteryEnablesPowerSavingAndChargerDisablesIt()
    {
        using var h = new EngineHarness();
        await h.Start();
        h.Os.Battery.Reading = new BatteryReading(true, false, 35, -8000);
        h.Os.Events.RaisePowerSource();
        await h.Settle(() => h.State.PowerSavingSetByUs);
        Assert.Equal(PowerOverlays.BestPowerEfficiency, h.Os.Power.Dc);
        Assert.Contains(h.Os.Notifier.Posted, n => n.Title == "Power saving on");

        h.Os.Battery.Reading = new BatteryReading(true, true, 36, 20000);
        h.Os.Power.Source = PowerSourceKind.Ac;
        h.Os.Events.RaisePowerSource();
        await h.Settle(() => !h.State.PowerSavingSetByUs);
        Assert.Contains(h.Os.Notifier.Posted, n => n.Title == "Power saving off");
    }

    [Fact]
    public async Task CriticalThermalEndsTheSession()
    {
        using var h = new EngineHarness();
        await h.Start();
        h.Os.Thermal.State = Logic.ThermalState.Critical;
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Settle(() => h.Engine.Status.Session is null);
        Assert.Contains(h.Os.Notifier.Posted, n => n.Title == "Too hot");
    }

    [Fact]
    public async Task SessionFileRemovedExternallyEndsTheSession()
    {
        using var h = new EngineHarness();
        await h.Start();
        h.Home.Store.DeleteSession();
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Settle(() => h.Engine.Status.Session is null);
        Assert.False(h.Os.KeepAwake.IsHeld);
    }
}

public class EngineReconcileTests
{
    private static void SeedJournal(EngineHarness h)
    {
        var state = new RuntimeState();
        state.LidPolicy.Add(new LidPolicyEntry(FakeLidPolicy.Balanced, LidActions.Sleep, LidActions.Hibernate));
        state.FrozenProcesses.Add(new FrozenProcess(10, 1010, "slack.exe", "slack.exe", Confirmed: true));
        h.Home.Store.WriteState(state);
        h.Os.Lid.Schemes[FakeLidPolicy.Balanced] = (LidActions.DoNothing, LidActions.DoNothing);
        h.Os.Freezer.Suspended.Add((10, 1010));
    }

    [Fact]
    public async Task ExpiredSessionIsRestoredAtLaunch()
    {
        using var h = new EngineHarness(x =>
        {
            SeedJournal(x);
            x.Home.Store.WriteSession(new Session(EngineHarness.T0.AddHours(-5), EngineHarness.T0.AddHours(-1)));
        });
        await h.Init();
        Assert.True(h.State.IsEmpty);
        Assert.Equal((LidActions.Sleep, LidActions.Hibernate), h.Os.Lid.Schemes[FakeLidPolicy.Balanced]);
        Assert.Empty(h.Os.Freezer.Suspended);
        Assert.IsType<SessionRead.Missing>(h.Home.Store.ReadSession());
        Assert.Null(h.Engine.Status.Session);
    }

    [Fact]
    public async Task ValidSessionResumesAndUndoesLidCloseWithLidOpen()
    {
        using var h = new EngineHarness(x =>
        {
            SeedJournal(x);
            x.Home.Store.WriteSession(new Session(EngineHarness.T0.AddHours(-1), EngineHarness.T0.AddHours(3)));
        });
        await h.Init();
        Assert.NotNull(h.Engine.Status.Session);
        Assert.True(h.Os.KeepAwake.IsHeld);
        Assert.Empty(h.Os.Freezer.Suspended);
        Assert.Single(h.State.LidPolicy);
        Assert.False(h.Engine.Status.LidClosed);
    }

    [Fact]
    public async Task ValidSessionWithLidClosedLeavesCloseActionsInPlace()
    {
        using var h = new EngineHarness(x =>
        {
            SeedJournal(x);
            x.Home.Store.WriteSession(new Session(EngineHarness.T0.AddHours(-1), EngineHarness.T0.AddHours(3)));
            x.Os.Events.LidClosed = true;
        });
        await h.Init();
        Assert.True(h.Engine.Status.LidClosed);
        Assert.Contains((10, 1010L), h.Os.Freezer.Suspended);
    }

    [Fact]
    public async Task UnreadableSessionIsSetAsideAndJournalRestored()
    {
        using var h = new EngineHarness(x =>
        {
            SeedJournal(x);
            File.WriteAllText(x.Home.Paths.Session, "{ not a session");
        });
        await h.Init();
        Assert.True(h.State.IsEmpty);
        Assert.False(File.Exists(h.Home.Paths.Session));
        Assert.Single(Directory.GetFiles(h.Home.Root, "session.json.unreadable-*"));
        Assert.Contains(h.Os.Notifier.Posted, n => n.Title == "Session file could not be read");
    }

    [Fact]
    public async Task LaunchAtLoginIsHealedButTaskManagerChoiceKept()
    {
        using var h = new EngineHarness(x => x.Home.Store.WriteConfig(new Config() with { LaunchAtLogin = true }));
        await h.Init();
        Assert.Equal(StartupState.Enabled, h.Os.Startup.State);

        using var h2 = new EngineHarness(x =>
        {
            x.Home.Store.WriteConfig(new Config() with { LaunchAtLogin = true });
            x.Os.Startup.State = StartupState.DisabledByUser;
        });
        await h2.Init();
        Assert.Equal(StartupState.DisabledByUser, h2.Os.Startup.State);
    }

    [Fact]
    public async Task BusyLockRetriesReconcile()
    {
        using var h = new EngineHarness(x => x.Home.Store.WriteSession(new Session(EngineHarness.T0.AddHours(-5), EngineHarness.T0.AddHours(-1))));
        var held = h.Home.Lock.TryAcquire(TimeSpan.Zero)!;
        var init = h.Engine.InitializeAsync();
        await init;
        Assert.IsType<SessionRead.Valid>(h.Home.Store.ReadSession());
        held.Dispose();
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Settle(() => h.Home.Store.ReadSession() is SessionRead.Missing);
    }
}
