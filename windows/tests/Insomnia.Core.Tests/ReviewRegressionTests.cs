using Insomnia.Core.Engine;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;
using Insomnia.Core.Store;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Insomnia.Core.Tests;

/// <summary>Regressions for the independent review's findings.</summary>
public class ReviewRegressionTests
{
    private static readonly DateTimeOffset T0 = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static (TempHome Home, FakeOs Os, RecoveryRunner Runner) Recovery()
    {
        var home = new TempHome();
        var os = new FakeOs();
        return (home, os, new RecoveryRunner(os.Services, home.Store, home.Lock, new FakeTimeProvider(T0)));
    }

    // 1. A failed read is not absence.

    [Fact]
    public void FailedLidReadKeepsTheEntry()
    {
        var (home, os, runner) = Recovery();
        using (home)
        {
            var state = new RuntimeState();
            state.LidPolicy.Add(new LidPolicyEntry(FakeLidPolicy.Balanced, LidActions.Sleep, LidActions.Sleep));
            home.Store.WriteState(state);
            os.Lid.Schemes[FakeLidPolicy.Balanced] = (LidActions.DoNothing, LidActions.DoNothing);
            os.Lid.FailReads = true;
            Assert.Equal(RecoveryExit.Incomplete, runner.Run());
            Assert.Single(home.ReadState().LidPolicy);

            os.Lid.FailReads = false;
            Assert.Equal(RecoveryExit.Ok, runner.Run());
            Assert.Empty(home.ReadState().LidPolicy);
        }
    }

    [Fact]
    public void DeletedSchemeDropsTheEntry()
    {
        var (home, os, runner) = Recovery();
        using (home)
        {
            var state = new RuntimeState();
            state.LidPolicy.Add(new LidPolicyEntry(Guid.NewGuid(), LidActions.Sleep, LidActions.Sleep));
            home.Store.WriteState(state);
            Assert.Equal(RecoveryExit.Ok, runner.Run());
            Assert.True(home.ReadState().IsEmpty);
        }
    }

    [Fact]
    public void FailedInspectionKeepsUnconfirmedEntry()
    {
        var (home, os, runner) = Recovery();
        using (home)
        {
            var state = new RuntimeState();
            state.FrozenProcesses.Add(new FrozenProcess(10, 1010, "slack.exe", "slack.exe", Confirmed: false));
            home.Store.WriteState(state);
            os.Freezer.InspectFails.Add(10);
            Assert.Equal(RecoveryExit.Incomplete, runner.Run());
            Assert.Single(home.ReadState().FrozenProcesses);
        }
    }

    [Fact]
    public void UnreadableThrottlingOfALiveProcessKeepsTheEntry()
    {
        var (home, os, runner) = Recovery();
        using (home)
        {
            var state = new RuntimeState();
            state.ThrottleOverrides.Add(new ThrottleOverride(10, 1010, "Code.exe", 0, 0));
            state.ThrottleOverrides.Add(new ThrottleOverride(11, 1011, "Code.exe", 0, 0));
            home.Store.WriteState(state);
            os.Freezer.Gone.Add(11);
            Assert.Equal(RecoveryExit.Incomplete, runner.Run());
            var left = Assert.Single(home.ReadState().ThrottleOverrides);
            Assert.Equal(10, left.Pid);
        }
    }

    // 2. A write that stored the values but failed to activate them is retried.

    [Fact]
    public async Task FailedActivationIsRetriedNotForgotten()
    {
        using var h = new EngineHarness();
        await h.Start();
        h.Os.Lid.FailActivation = true;
        Assert.Equal(EndOutcome.Incomplete, await h.Engine.EndAsync(EndReason.User));
        Assert.Single(h.State.LidPolicy);
        Assert.Equal((LidActions.Sleep, LidActions.Sleep), h.Os.Lid.Schemes[FakeLidPolicy.Balanced]);
        Assert.Equal((LidActions.DoNothing, LidActions.DoNothing), h.Os.Lid.Effective[FakeLidPolicy.Balanced]);

        h.Os.Lid.FailActivation = false;
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Settle(() => h.State.LidPolicy.Count == 0);
        Assert.Equal((LidActions.Sleep, LidActions.Sleep), h.Os.Lid.Effective[FakeLidPolicy.Balanced]);
    }

    // 3. An owed write is not replayed over a journal another process changed.

    [Fact]
    public async Task OwedWriteDoesNotResurrectWhatRecoveryRestored()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Needs Unix permissions to make journal writes fail; the logic is platform-neutral.
        }
        using var h = new EngineHarness();
        await h.Start();
        var mode = File.GetUnixFileMode(h.Home.Root);
        File.SetUnixFileMode(h.Home.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await h.Lid(closed: true); // the mute entry's journal write fails: owed
            Assert.Contains(h.Engine.Status.Warnings, w => w.Id == "journal-write");
        }
        finally
        {
            File.SetUnixFileMode(h.Home.Root, mode);
        }
        // The recovery agent restores and clears the journal meanwhile.
        h.Home.Store.WriteState(new RuntimeState());

        await h.Lid(closed: false);
        Assert.True(h.State.IsEmpty);
        Assert.DoesNotContain(h.Engine.Status.Warnings, w => w.Id == "journal-write");
    }

    // 4. An end that cannot remove session.json is not reported as done.

    [Fact]
    public async Task UndeletableSessionMakesTheEndIncompleteAndIsRetried()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Needs Unix permissions to make the delete fail.
        }
        using var h = new EngineHarness();
        await h.Start();
        var mode = File.GetUnixFileMode(h.Home.Root);
        File.SetUnixFileMode(h.Home.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        EndOutcome outcome;
        try
        {
            outcome = await h.Engine.EndAsync(EndReason.User);
        }
        finally
        {
            File.SetUnixFileMode(h.Home.Root, mode);
        }
        Assert.Equal(EndOutcome.Incomplete, outcome);
        Assert.Contains(h.Engine.Status.Warnings, w => w.Id == "session-delete");
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Settle(() => h.Home.Store.ReadSession() is SessionRead.Missing);
    }

    // 5. Waiting power mode blocks uninstall.

    [Fact]
    public void WaitingPowerModeIsReported()
    {
        var (home, os, runner) = Recovery();
        using (home)
        {
            var state = new RuntimeState();
            state.PowerMode.Add(new PowerModeEntry(PowerSourceKind.Ac, PowerOverlays.Balanced, PowerOverlays.BestPowerEfficiency));
            home.Store.WriteState(state);
            os.Power.Ac = PowerOverlays.BestPowerEfficiency;
            os.Power.Source = PowerSourceKind.Dc;
            Assert.Equal(RecoveryExit.WaitingForPowerSource, runner.Run());
            Assert.Single(home.ReadState().PowerMode);
        }
    }

    // 9. Unknown audio status is a failure, not a wait.

    [Fact]
    public void UnknownAudioStatusIsRetried()
    {
        var (home, os, runner) = Recovery();
        using (home)
        {
            var state = new RuntimeState();
            state.SavedAudioOutputs.Add(new SavedAudioOutput("spk", "Speakers", 0.6f, false, Guid.NewGuid()));
            home.Store.WriteState(state);
            os.Audio.Devices["spk"] = os.Audio.Devices["spk"] with { Muted = true, Status = AudioDeviceStatus.Unknown };
            Assert.Equal(RecoveryExit.Incomplete, runner.Run());
            Assert.Single(home.ReadState().SavedAudioOutputs);
        }
    }

    // 10. Extending a session the agent already ended does not recreate it.

    [Fact]
    public async Task ExtendAfterRecoveryEndedTheSessionIsRefused()
    {
        using var h = new EngineHarness();
        await h.Start();
        h.Home.Store.DeleteSession();
        Assert.False(await h.Engine.ExtendAsync(TimeSpan.FromHours(1)));
        Assert.IsType<SessionRead.Missing>(h.Home.Store.ReadSession());
        await h.Settle(() => h.Engine.Status.Session is null);
    }

    // 11. A timer end queued behind an extension is ignored.

    [Fact]
    public async Task StaleTimerEndDoesNotEndAnExtendedSession()
    {
        using var h = new EngineHarness(x =>
        {
            x.Os.Processes.Add(30, "Docker Desktop.exe", window: true);
            x.Home.Store.WriteConfig(new Config() with { DockerRule = true });
        });
        await h.Start(TimeSpan.FromMinutes(30));
        h.Os.Docker.Gate = new TaskCompletionSource();
        h.Os.Events.SetLid(true);
        h.Time.Advance(TimeSpan.FromSeconds(2)); // lid close occupies the queue on the probe
        await Task.Delay(50);
        var extend = h.Engine.ExtendAsync(TimeSpan.FromMinutes(30));
        await Task.Delay(50);
        h.Time.Advance(TimeSpan.FromMinutes(30)); // the old deadline passes: a timer end queues
        await Task.Delay(50);
        h.Os.Docker.Gate.SetResult();
        Assert.True(await extend);
        await h.Settle();
        Assert.NotNull(h.Engine.Status.Session);
        Assert.True(h.Os.KeepAwake.IsHeld);
    }

    // 12. A power mode the user picked is not taken back by the next poll.

    [Fact]
    public async Task UserPowerModeChoiceSurvivesPolls()
    {
        using var h = new EngineHarness();
        await h.Start();
        h.Os.Battery.Reading = new BatteryReading(true, false, 35, -8000);
        h.Os.Events.RaisePowerSource();
        await h.Settle(() => h.Os.Power.Dc == PowerOverlays.BestPowerEfficiency);

        h.Os.Power.Dc = PowerOverlays.BestPerformance;
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Settle();
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Settle();
        Assert.Equal(PowerOverlays.BestPerformance, h.Os.Power.Dc);
    }
}

/// <summary>Regressions for the second review round.</summary>
public class ReviewRound2Tests
{
    private static readonly DateTimeOffset T0 = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static (TempHome Home, FakeOs Os, RecoveryRunner Runner) Recovery()
    {
        var home = new TempHome();
        var os = new FakeOs();
        return (home, os, new RecoveryRunner(os.Services, home.Store, home.Lock, new FakeTimeProvider(T0)));
    }

    [Fact]
    public void InterruptedResumeIsNeverReplayed()
    {
        var (home, os, runner) = Recovery();
        using (home)
        {
            var state = new RuntimeState();
            state.FrozenProcesses.Add(new FrozenProcess(10, 1010, "slack.exe", "slack.exe", Confirmed: true, Resuming: true));
            home.Store.WriteState(state);
            os.Freezer.Suspended.Add((10, 1010)); // still suspended, perhaps by another program too
            Assert.Equal(RecoveryExit.Incomplete, runner.Run());
            Assert.DoesNotContain("resume:10", os.Log.Calls);
            // Kept for the user, announced once, and dropped once it reads running.
            Assert.True(Assert.Single(home.ReadState().FrozenProcesses).Resuming);
            Assert.Single(os.Notifier.Posted, n => n.Body.Contains("may still be paused", StringComparison.Ordinal));
            Assert.Equal(RecoveryExit.Incomplete, runner.Run());
            Assert.Single(os.Notifier.Posted);
            os.Freezer.Suspended.Clear();
            Assert.Equal(RecoveryExit.Ok, runner.Run());
            Assert.True(home.ReadState().IsEmpty);
            Assert.DoesNotContain("resume:10", os.Log.Calls);
        }
    }

    [Fact]
    public async Task UserCanResumeAnInterruptedEntryFromTheMenu()
    {
        using var h = new EngineHarness(x =>
        {
            var state = new RuntimeState();
            state.FrozenProcesses.Add(new FrozenProcess(10, 1010, "slack.exe", "slack.exe", Confirmed: true, Resuming: true));
            x.Home.Store.WriteState(state);
            x.Os.Freezer.Suspended.Add((10, 1010));
        });
        await h.Init();
        var warning = Assert.Single(h.Engine.Status.Warnings, w => w.Id.StartsWith("interrupted:", StringComparison.Ordinal));
        await h.Engine.PerformAsync(warning.Action!);
        Assert.Contains("resume:10", h.Os.Log.Calls);
        Assert.True(h.State.IsEmpty);
        Assert.DoesNotContain(h.Engine.Status.Warnings, w => w.Id.StartsWith("interrupted:", StringComparison.Ordinal));
    }

    [Fact]
    public void NoResumeWithoutRecordingItFirst()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Needs Unix permissions to make journal writes fail.
        }
        var (home, os, runner) = Recovery();
        using (home)
        {
            var state = new RuntimeState();
            state.FrozenProcesses.Add(new FrozenProcess(10, 1010, "slack.exe", "slack.exe", Confirmed: true));
            home.Store.WriteState(state);
            os.Freezer.Suspended.Add((10, 1010));
            home.Lock.TryAcquire(TimeSpan.Zero)!.Dispose(); // the lock file must exist before the folder goes read-only
            var mode = File.GetUnixFileMode(home.Root);
            File.SetUnixFileMode(home.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Assert.Equal(RecoveryExit.Incomplete, runner.Run());
            }
            finally
            {
                File.SetUnixFileMode(home.Root, mode);
            }
            Assert.DoesNotContain("resume:10", os.Log.Calls);
            Assert.False(home.ReadState().FrozenProcesses.Single().Resuming);
        }
    }

    [Fact]
    public async Task UnreadablePowerModeBaselineChangesNothing()
    {
        using var h = new EngineHarness(x => x.Os.Power.FailReads = true);
        await h.Start();
        await h.Lid(closed: true);
        Assert.Empty(h.State.PowerMode);
        Assert.DoesNotContain(h.Os.Log.Calls, c => c.StartsWith("power:", StringComparison.Ordinal));
    }

    [Fact]
    public void UnreadablePowerModeAtRestoreKeepsTheEntry()
    {
        var (home, os, runner) = Recovery();
        using (home)
        {
            var state = new RuntimeState();
            state.PowerMode.Add(new PowerModeEntry(PowerSourceKind.Dc, PowerOverlays.Balanced, PowerOverlays.BestPowerEfficiency));
            home.Store.WriteState(state);
            os.Power.FailReads = true;
            Assert.Equal(RecoveryExit.Incomplete, runner.Run());
            Assert.Single(home.ReadState().PowerMode);
        }
    }

    [Fact]
    public async Task BothSourcesAreJournaledBeforeTheWrite()
    {
        using var h = new EngineHarness();
        h.Os.Power.Ac = PowerOverlays.BestPerformance;
        await h.Start();
        await h.Lid(closed: true);
        var entries = h.State.PowerMode.OrderBy(e => e.Source).ToList();
        // DC was written and confirmed; AC was journaled first, then dropped as untouched.
        var dc = Assert.Single(entries);
        Assert.Equal(PowerSourceKind.Dc, dc.Source);
        Assert.True(dc.Confirmed);
        await h.Lid(closed: false);
        Assert.Equal(PowerOverlays.BestPerformance, h.Os.Power.Ac);
        Assert.Equal(PowerOverlays.Balanced, h.Os.Power.Dc);
    }

    [Fact]
    public async Task RelinquishedSourceSurvivesARelaunch()
    {
        var home = new TempHome();
        var os = new FakeOs(() => ((StateRead.Valid)home.Store.ReadState()).State);
        var time = new FakeTimeProvider(T0);
        try
        {
            using (var first = new SessionEngine(os.Services, home.Store, home.Lock, new EngineOptions(), time))
            {
                await first.InitializeAsync();
                await first.StartAsync(TimeSpan.FromHours(4));
                os.Battery.Reading = new BatteryReading(true, false, 35, -8000);
                os.Events.RaisePowerSource();
                for (var i = 0; i < 100 && os.Power.Dc != PowerOverlays.BestPowerEfficiency; i++)
                {
                    await first.IdleAsync();
                    await Task.Delay(5);
                }
                os.Power.Dc = PowerOverlays.BestPerformance;
                os.Events.RaisePowerSource();
                for (var i = 0; i < 100 && home.ReadState().PowerModeRelinquished.Count == 0; i++)
                {
                    await first.IdleAsync();
                    await Task.Delay(5);
                }
            }
            Assert.Contains(PowerSourceKind.Dc, home.ReadState().PowerModeRelinquished);

            using var second = new SessionEngine(os.Services, home.Store, home.Lock, new EngineOptions(), time);
            await second.InitializeAsync();
            time.Advance(TimeSpan.FromSeconds(30));
            await second.IdleAsync();
            await Task.Delay(20);
            await second.IdleAsync();
            Assert.Equal(PowerOverlays.BestPerformance, os.Power.Dc);
        }
        finally
        {
            home.Dispose();
        }
    }

    [Fact]
    public async Task QuitWaitsWhileTheEndedSessionIsStillOnDisk()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Needs Unix permissions to make the delete fail.
        }
        using var h = new EngineHarness();
        await h.Start();
        var mode = File.GetUnixFileMode(h.Home.Root);
        File.SetUnixFileMode(h.Home.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await h.Engine.EndAsync(EndReason.User);
            Assert.False(await h.Engine.PrepareToQuitAsync());
        }
        finally
        {
            File.SetUnixFileMode(h.Home.Root, mode);
        }
        Assert.True(await h.Engine.PrepareToQuitAsync());
        Assert.IsType<SessionRead.Missing>(h.Home.Store.ReadSession());
    }

    [Fact]
    public void UnreadableJournalFileIsNotAnEmptyJournal()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Needs Unix permissions to make the read fail.
        }
        using var home = new TempHome();
        var state = new RuntimeState();
        state.LidPolicy.Add(new LidPolicyEntry(Guid.NewGuid(), 1, 1));
        home.Store.WriteState(state);
        File.SetUnixFileMode(home.Paths.State, UnixFileMode.None);
        try
        {
            Assert.IsType<StateRead.Unreadable>(home.Store.ReadState());
            Assert.IsType<SessionRead.Missing>(home.Store.ReadSession());
        }
        finally
        {
            File.SetUnixFileMode(home.Paths.State, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void UnreadableConfigIsNotReplacedByDefaults()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var home = new TempHome();
        home.Store.WriteConfig(new Config() with { EndFloor = 25 });
        File.SetUnixFileMode(home.Paths.Config, UnixFileMode.None);
        try
        {
            var (_, problem) = home.Store.ReadConfig(out var missing);
            Assert.NotNull(problem);
            Assert.False(missing);
        }
        finally
        {
            File.SetUnixFileMode(home.Paths.Config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Assert.Equal(25, home.Store.ReadConfig().Config.EndFloor);
    }
}

/// <summary>Regressions for the third review round.</summary>
public class ReviewRound3Tests
{
    private static readonly DateTimeOffset T0 = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UnverifiedPowerModeWriteKeepsItsBaselineThroughARetry()
    {
        using var h = new EngineHarness();
        await h.Start();
        h.Os.Power.FailReadsAfterNextSet = true;
        await h.Lid(closed: true);
        Assert.Equal(PowerOverlays.BestPowerEfficiency, h.Os.Power.Dc);
        var pending = Assert.Single(h.State.PowerMode, e => e.Source == PowerSourceKind.Dc);
        Assert.False(pending.Confirmed);

        h.Os.Power.FailReads = false;
        h.Os.Events.RaisePowerSource();
        await h.Settle(() => h.State.PowerMode.Any(e => e.Source == PowerSourceKind.Dc && e.Confirmed));
        Assert.Equal(PowerOverlays.Balanced, h.State.PowerMode.Single(e => e.Source == PowerSourceKind.Dc).Previous);

        await h.Engine.EndAsync(EndReason.User);
        Assert.Equal(PowerOverlays.Balanced, h.Os.Power.Dc);
    }

    [Fact]
    public async Task RolledBackResumeMarkIsNotReplayedFromTheOwedWrite()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Needs Unix permissions to make journal writes fail.
        }
        using var h = new EngineHarness(x => x.Home.Store.WriteConfig(new Config() with { MuteOnLidClose = false, PowerSavingOnLidClose = false, DisplaysOffOnLidClose = false }));
        await h.Start();
        await h.Lid(closed: true);
        Assert.NotEmpty(h.State.FrozenProcesses);

        var mode = File.GetUnixFileMode(h.Home.Root);
        File.SetUnixFileMode(h.Home.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await h.Lid(closed: false); // the pre-resume mark cannot be written: nothing is resumed
        }
        finally
        {
            File.SetUnixFileMode(h.Home.Root, mode);
        }
        Assert.DoesNotContain("resume:10", h.Os.Log.Calls);

        // The next transaction writes what it owes: entries as they were, not marked resuming.
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Settle(() => h.State.FrozenProcesses.Count == 0);
        Assert.Contains("resume:10", h.Os.Log.Calls);
    }

    [Fact]
    public void InaccessibleSessionIsNeitherResumedNorRestored()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Needs Unix permissions to make the read fail.
        }
        using var home = new TempHome();
        var os = new FakeOs();
        var state = new RuntimeState();
        state.LidPolicy.Add(new LidPolicyEntry(FakeLidPolicy.Balanced, LidActions.Sleep, LidActions.Sleep));
        home.Store.WriteState(state);
        os.Lid.Schemes[FakeLidPolicy.Balanced] = (LidActions.DoNothing, LidActions.DoNothing);
        home.Store.WriteSession(new Session(T0, T0.AddHours(4)));
        File.SetUnixFileMode(home.Paths.Session, UnixFileMode.None);
        try
        {
            Assert.IsType<SessionRead.Inaccessible>(home.Store.ReadSession());
            var runner = new RecoveryRunner(os.Services, home.Store, home.Lock, new FakeTimeProvider(T0));
            Assert.Equal(RecoveryExit.Busy, runner.Run());
            Assert.Single(home.ReadState().LidPolicy);
            Assert.Equal((LidActions.DoNothing, LidActions.DoNothing), os.Lid.Schemes[FakeLidPolicy.Balanced]);
        }
        finally
        {
            File.SetUnixFileMode(home.Paths.Session, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

/// <summary>Regressions for the fourth review round.</summary>
public class ReviewRound4Tests
{
    private static readonly DateTimeOffset T0 = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RedirectedRestoreKeepsTheOtherSourcesBaseline()
    {
        using var home = new TempHome();
        var os = new FakeOs();
        var state = new RuntimeState();
        state.PowerMode.Add(new PowerModeEntry(PowerSourceKind.Ac, PowerOverlays.BestPerformance, PowerOverlays.BestPowerEfficiency));
        state.PowerMode.Add(new PowerModeEntry(PowerSourceKind.Dc, PowerOverlays.Balanced, PowerOverlays.BestPowerEfficiency));
        home.Store.WriteState(state);
        os.Power.Ac = PowerOverlays.BestPowerEfficiency;
        os.Power.Dc = PowerOverlays.BestPowerEfficiency;
        os.Power.Source = PowerSourceKind.Ac;
        os.Power.SwitchSourceOnNextSet = true; // unplugged mid-restore: AC's value lands on DC
        var runner = new RecoveryRunner(os.Services, home.Store, home.Lock, new FakeTimeProvider(T0));

        Assert.Equal(RecoveryExit.Incomplete, runner.Run());
        Assert.Equal(PowerOverlays.BestPerformance, os.Power.Dc);
        Assert.Equal(2, home.ReadState().PowerMode.Count);

        // On battery now: DC is recognized as Insomnia's and gets its own baseline back.
        Assert.Equal(RecoveryExit.WaitingForPowerSource, runner.Run());
        Assert.Equal(PowerOverlays.Balanced, os.Power.Dc);

        os.Power.Source = PowerSourceKind.Ac;
        Assert.Equal(RecoveryExit.Ok, runner.Run());
        Assert.Equal(PowerOverlays.BestPerformance, os.Power.Ac);
        Assert.True(home.ReadState().IsEmpty);
    }

    [Fact]
    public async Task MisplacedRestoreValueDoesNotBlockPowerSaving()
    {
        using var h = new EngineHarness();
        await h.Start();
        // A restore's write landed on DC; DC's entry knows it as Insomnia's.
        var state = h.State;
        state.PowerSavingSetByUs = true;
        state.PowerMode.Add(new PowerModeEntry(PowerSourceKind.Dc, PowerOverlays.BestPerformance, PowerOverlays.BestPowerEfficiency, AlsoApplied: PowerOverlays.Balanced));
        h.Home.Store.WriteState(state);
        h.Os.Power.Dc = PowerOverlays.Balanced;
        h.Os.Battery.Reading = new BatteryReading(true, false, 35, -8000);
        h.Os.Events.RaisePowerSource();
        await h.Settle(() => h.Os.Power.Dc == PowerOverlays.BestPowerEfficiency);
        var entry = Assert.Single(h.State.PowerMode, e => e.Source == PowerSourceKind.Dc);
        Assert.Equal(PowerOverlays.BestPerformance, entry.Previous);
        await h.Engine.EndAsync(EndReason.User);
        Assert.Equal(PowerOverlays.BestPerformance, h.Os.Power.Dc);
    }

    [Fact]
    public async Task PowerEventsDoNotRestoreBeforeReconcile()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Needs Unix permissions to make session.json unreadable.
        }
        using var h = new EngineHarness(x =>
        {
            var state = new RuntimeState();
            state.LidPolicy.Add(new LidPolicyEntry(FakeLidPolicy.Balanced, LidActions.Sleep, LidActions.Sleep));
            x.Home.Store.WriteState(state);
            x.Os.Lid.Schemes[FakeLidPolicy.Balanced] = (LidActions.DoNothing, LidActions.DoNothing);
            x.Home.Store.WriteSession(new Session(T0, T0.AddHours(4)));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(x.Home.Paths.Session, UnixFileMode.None);
            }
        });
        try
        {
            await h.Init(); // postponed: the session may be live
            h.Os.Events.RaisePowerSource();
            h.Os.Audio.SetStatus("spk", AudioDeviceStatus.Connected);
            await h.Settle();
            Assert.Equal((LidActions.DoNothing, LidActions.DoNothing), h.Os.Lid.Schemes[FakeLidPolicy.Balanced]);
            Assert.Single(h.State.LidPolicy);
        }
        finally
        {
            File.SetUnixFileMode(h.Home.Paths.Session, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        // Readable again: reconcile resumes the live session rather than undoing it.
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Settle(() => h.Engine.Status.Session is not null);
        Assert.Equal((LidActions.DoNothing, LidActions.DoNothing), h.Os.Lid.Schemes[FakeLidPolicy.Balanced]);
    }
}
