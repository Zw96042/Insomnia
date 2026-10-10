using Insomnia.Core.Engine;
using Insomnia.Core.Logging;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;
using Insomnia.Core.Store;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Insomnia.Core.Tests;

public class RecoveryRunnerTests
{
    private static readonly DateTimeOffset T0 = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static (TempHome Home, FakeOs Os, RecoveryRunner Runner) Make()
    {
        var home = new TempHome();
        var os = new FakeOs();
        return (home, os, new RecoveryRunner(os.Services, home.Store, home.Lock, new FakeTimeProvider(T0)));
    }

    private static RuntimeState Dirty(FakeOs os)
    {
        var state = new RuntimeState();
        state.LidPolicy.Add(new LidPolicyEntry(FakeLidPolicy.Balanced, LidActions.Sleep, LidActions.Sleep));
        state.FrozenProcesses.Add(new FrozenProcess(10, 1010, "slack.exe", "slack.exe", Confirmed: true));
        state.SavedAudioOutputs.Add(new SavedAudioOutput("spk", "Speakers", 0.6f, false, Guid.NewGuid()));
        os.Lid.Schemes[FakeLidPolicy.Balanced] = (LidActions.DoNothing, LidActions.DoNothing);
        os.Freezer.Suspended.Add((10, 1010));
        os.Audio.Devices["spk"] = os.Audio.Devices["spk"] with { Muted = true };
        return state;
    }

    [Fact]
    public void ValidSessionIsHonored()
    {
        var (home, os, runner) = Make();
        using (home)
        {
            home.Store.WriteState(Dirty(os));
            home.Store.WriteSession(new Session(T0.AddHours(-1), T0.AddHours(1)));
            Assert.Equal(RecoveryExit.Ok, runner.Run());
            Assert.Equal((LidActions.DoNothing, LidActions.DoNothing), os.Lid.Schemes[FakeLidPolicy.Balanced]);
            Assert.Single(home.ReadState().FrozenProcesses);
        }
    }

    [Fact]
    public void ExpiredSessionIsRestoredAndRemoved()
    {
        var (home, os, runner) = Make();
        using (home)
        {
            home.Store.WriteState(Dirty(os));
            home.Store.WriteSession(new Session(T0.AddHours(-2), T0.AddMinutes(-1)));
            Assert.Equal(RecoveryExit.Ok, runner.Run());
            Assert.True(home.ReadState().IsEmpty);
            Assert.IsType<SessionRead.Missing>(home.Store.ReadSession());
            Assert.Equal((LidActions.Sleep, LidActions.Sleep), os.Lid.Schemes[FakeLidPolicy.Balanced]);
            Assert.Empty(os.Freezer.Suspended);
            Assert.False(os.Audio.Devices["spk"].Muted);
            Assert.Contains(os.Notifier.Posted, n => n.Title == "Sleep settings restored");
        }
    }

    [Fact]
    public void NothingToDoIsQuiet()
    {
        var (home, os, runner) = Make();
        using (home)
        {
            Assert.Equal(RecoveryExit.Ok, runner.Run());
            Assert.Empty(os.Notifier.Posted);
            Assert.Empty(os.Log.Calls);
        }
    }

    [Fact]
    public void BusyLockExitsWithoutChanges()
    {
        var (home, os, runner) = Make();
        using (home)
        {
            home.Store.WriteState(Dirty(os));
            using var held = home.Lock.TryAcquire(TimeSpan.Zero);
            Assert.Equal(RecoveryExit.Busy, runner.Run());
            Assert.Single(home.ReadState().LidPolicy);
        }
    }

    [Fact]
    public void UnreadableJournalChangesNothing()
    {
        var (home, os, runner) = Make();
        using (home)
        {
            File.WriteAllText(home.Paths.State, "nope");
            Assert.Equal(RecoveryExit.JournalUnreadable, runner.Run());
            Assert.Equal("nope", File.ReadAllText(home.Paths.State));
        }
    }

    [Fact]
    public void FailureStaysJournaledAndExitsIncomplete()
    {
        var (home, os, runner) = Make();
        using (home)
        {
            home.Store.WriteState(Dirty(os));
            os.Lid.RefuseWrites = true;
            Assert.Equal(RecoveryExit.Incomplete, runner.Run());
            Assert.Single(home.ReadState().LidPolicy);
            Assert.Empty(home.ReadState().FrozenProcesses);
            Assert.Contains(os.Notifier.Posted, n => n.Title == "Restore incomplete");
        }
    }

    [Fact]
    public void UnconfirmedEntriesResumeOnlyWhenFullySuspended()
    {
        var (home, os, runner) = Make();
        using (home)
        {
            var state = new RuntimeState();
            state.FrozenProcesses.Add(new FrozenProcess(10, 1010, "slack.exe", "slack.exe", Confirmed: false));
            state.FrozenProcesses.Add(new FrozenProcess(11, 1011, "slack.exe", "slack.exe", Confirmed: false));
            home.Store.WriteState(state);
            os.Freezer.Suspended.Add((10, 1010));
            Assert.Equal(RecoveryExit.Ok, runner.Run());
            Assert.Contains("resume:10", os.Log.Calls);
            Assert.DoesNotContain("resume:11", os.Log.Calls);
            Assert.True(home.ReadState().IsEmpty);
        }
    }

    [Fact]
    public void UnreadableSessionIsTreatedAsEnded()
    {
        var (home, os, runner) = Make();
        using (home)
        {
            home.Store.WriteState(Dirty(os));
            File.WriteAllText(home.Paths.Session, "{");
            Assert.Equal(RecoveryExit.Ok, runner.Run());
            Assert.True(home.ReadState().IsEmpty);
            Assert.Single(Directory.GetFiles(home.Root, "session.json.unreadable-*"));
        }
    }

    [Fact]
    public void AwayAudioAloneDoesNotWakeTheAgent()
    {
        var (home, os, runner) = Make();
        using (home)
        {
            var state = new RuntimeState();
            state.SavedAudioOutputs.Add(new SavedAudioOutput("spk", "Speakers", 0.6f, false, Guid.NewGuid()));
            home.Store.WriteState(state);
            os.Audio.Devices["spk"] = os.Audio.Devices["spk"] with { Muted = true, Status = AudioDeviceStatus.Away };
            Assert.Equal(RecoveryExit.Ok, runner.Run());
            Assert.Single(home.ReadState().SavedAudioOutputs);

            os.Audio.Devices["spk"] = os.Audio.Devices["spk"] with { Status = AudioDeviceStatus.Connected };
            Assert.Equal(RecoveryExit.Ok, runner.Run());
            Assert.Empty(home.ReadState().SavedAudioOutputs);
            Assert.False(os.Audio.Devices["spk"].Muted);
        }
    }
}

public class TmuxNudgeTests
{
    private static FakeCommands Tmux(Dictionary<string, string> panes, HashSet<string> marked) => new()
    {
        Handler = args =>
        {
            var a = args.Skip(3).ToList(); // wsl.exe -e tmux ...
            if (a[0] == "display-message")
            {
                return panes.TryGetValue(a[3], out var pane) ? new CommandResult(0, pane + "\n", "", false) : new CommandResult(1, "", "can't find", false);
            }
            if (a[0] == "show-options")
            {
                return new CommandResult(0, marked.Contains(a[3]) ? "on\n" : "", "", false);
            }
            return new CommandResult(0, "", "", false);
        },
    };

    [Fact]
    public async Task OnlyMarkedPanesAreNudged()
    {
        var runner = Tmux(new() { ["main:0.0"] = "%1", ["main:0.1"] = "%2" }, ["%2"]);
        var config = new Config() with { TmuxTargets = ["main:0.0", "main:0.1", "gone:0.0"] };
        Assert.Equal(1, await TmuxNudge.RunAsync(runner, config, CancellationToken.None));
        var sends = runner.Runs.Where(r => r.Contains("send-keys")).ToList();
        var send = Assert.Single(sends);
        Assert.Equal(["wsl.exe", "-e", "tmux", "send-keys", "-t", "%2", "-l", "continue"], send);
    }

    [Fact]
    public async Task EnterIsPressedOnlyWhenConfigured()
    {
        var runner = Tmux(new() { ["a:0.0"] = "%7" }, ["%7"]);
        var config = new Config() with { TmuxTargets = ["a:0.0"], TmuxNudgePressesEnter = true };
        Assert.Equal(1, await TmuxNudge.RunAsync(runner, config, CancellationToken.None));
        Assert.Contains(runner.Runs, r => r.SequenceEqual(["wsl.exe", "-e", "tmux", "send-keys", "-t", "%7", "Enter"]));
    }

    [Fact]
    public async Task OddPaneIdsAreRejected()
    {
        var runner = Tmux(new() { ["a:0.0"] = "%7; rm -rf /" }, ["%7; rm -rf /"]);
        Assert.Equal(0, await TmuxNudge.RunAsync(runner, new Config() with { TmuxTargets = ["a:0.0"] }, CancellationToken.None));
    }
}

public class FailoverTests
{
    private static (FakeOs Os, FakeTimeProvider Time, NetworkFailover Failover, List<FailoverEvent> Events, TempHome Home) Make(Config config)
    {
        var os = new FakeOs();
        var time = new FakeTimeProvider(new DateTimeOffset(2027, 1, 15, 8, 0, 0, TimeSpan.Zero));
        var home = new TempHome();
        var events = new List<FailoverEvent>();
        var failover = new NetworkFailover(os.Services, time, () => config, new RotatingLog(home.Paths.HandoffsLog), events.Add);
        return (os, time, failover, events, home);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task OutageJoinsHotspotAfterFiveSeconds()
    {
        var (os, time, failover, _, home) = Make(new Config() with { HotspotSsid = "Phone" });
        using (home)
        using (failover)
        {
            os.Credentials.Passwords["Phone"] = "secret";
            failover.Start();
            os.Wifi.Set(WifiConnectivity.Unsatisfied);
            time.Advance(TimeSpan.FromSeconds(4));
            Assert.Empty(os.Wifi.Joins);
            time.Advance(TimeSpan.FromSeconds(1));
            await Until(() => os.Wifi.Joins.Count == 1);
            Assert.Equal("Phone:secret", os.Wifi.Joins[0]);
        }
    }

    [Fact]
    public async Task FailedJoinsBackOff()
    {
        var (os, time, failover, _, home) = Make(new Config() with { HotspotSsid = "Phone" });
        using (home)
        using (failover)
        {
            os.Credentials.Passwords["Phone"] = "secret";
            os.Wifi.JoinResult = HotspotJoinResult.NotFound;
            failover.Start();
            os.Wifi.Set(WifiConnectivity.Unsatisfied);
            time.Advance(TimeSpan.FromSeconds(5));
            await Until(() => os.Wifi.Joins.Count == 1);
            time.Advance(TimeSpan.FromSeconds(9));
            await Task.Delay(30);
            Assert.Single(os.Wifi.Joins);
            time.Advance(TimeSpan.FromSeconds(1));
            await Until(() => os.Wifi.Joins.Count == 2);
        }
    }

    [Fact]
    public async Task MissingPasswordWarnsOncePerOutage()
    {
        var (os, time, failover, events, home) = Make(new Config() with { HotspotSsid = "Phone" });
        using (home)
        using (failover)
        {
            failover.Start();
            os.Wifi.Set(WifiConnectivity.Unsatisfied);
            time.Advance(TimeSpan.FromSeconds(5));
            await Until(() => events.Count == 1);
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(30);
            Assert.Single(events.OfType<FailoverEvent.PasswordMissing>());
            Assert.Single(os.Notifier.Posted);
            Assert.Empty(os.Wifi.Joins);
        }
    }

    [Fact]
    public async Task RecoveryLogsGapAndNudgesAboveThreshold()
    {
        var (os, time, failover, events, home) = Make(new Config() with { TmuxTargets = ["a:0.0"] });
        using (home)
        using (failover)
        {
            os.Commands.Handler = args => args.Contains("display-message") ? new CommandResult(0, "%1", "", false)
                : args.Contains("show-options") ? new CommandResult(0, "on", "", false)
                : new CommandResult(0, "", "", false);
            failover.Start();
            os.Wifi.Set(WifiConnectivity.Unsatisfied);
            time.Advance(TimeSpan.FromSeconds(130));
            os.Wifi.Set(WifiConnectivity.Satisfied);
            await Until(() => os.Notifier.Posted.Count == 1);
            Assert.Equal("Network back", os.Notifier.Posted[0].Title);
            Assert.Contains("2m 10s", os.Notifier.Posted[0].Body);
            Assert.Contains("1 tmux pane", os.Notifier.Posted[0].Body);
            Assert.StartsWith("2m 10s at", failover.LastGap);
            Assert.Contains("gap 2m 10s", File.ReadAllText(home.Paths.HandoffsLog));
            Assert.Single(events.OfType<FailoverEvent.Recovered>());
        }
    }

    [Fact]
    public async Task ShortGapDoesNotNudge()
    {
        var (os, time, failover, _, home) = Make(new Config() with { TmuxTargets = ["a:0.0"] });
        using (home)
        using (failover)
        {
            failover.Start();
            os.Wifi.Set(WifiConnectivity.Unsatisfied);
            time.Advance(TimeSpan.FromSeconds(30));
            os.Wifi.Set(WifiConnectivity.Satisfied);
            await Until(() => File.Exists(home.Paths.HandoffsLog));
            await Task.Delay(30);
            Assert.Empty(os.Commands.Runs);
            Assert.Empty(os.Notifier.Posted);
        }
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(130, "2m 10s")]
    [InlineData(120, "2m")]
    [InlineData(3780, "1h 3m")]
    public void GapFormat(double seconds, string expected) =>
        Assert.Equal(expected, NetworkFailover.FormatGap(TimeSpan.FromSeconds(seconds)));
}
