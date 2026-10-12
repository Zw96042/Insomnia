using System.Text.Json;
using Insomnia.Core.Logging;
using Insomnia.Core.Model;
using Insomnia.Core.Store;
using Xunit;

namespace Insomnia.Core.Tests;

public class StoreTests
{
    private static readonly DateTimeOffset T0 = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SessionRoundTrips()
    {
        using var home = new TempHome();
        var session = new Session(T0, T0.AddHours(4), [600.0]);
        home.Store.WriteSession(session);
        Assert.Contains("\"endsAt\": \"2027-01-15T12:00:00Z\"", File.ReadAllText(home.Paths.Session));
        var read = Assert.IsType<SessionRead.Valid>(home.Store.ReadSession());
        Assert.Equal(session, read.Session);
    }

    [Fact]
    public void MissingSessionIsMissing()
    {
        using var home = new TempHome();
        Assert.IsType<SessionRead.Missing>(home.Store.ReadSession());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"startedAt\":\"2027-01-15T08:00:00Z\",\"endsAt\":\"2027-01-15T12:00:00Z\"}")]
    [InlineData("{\"startedAt\":\"2027-01-15T08:00:00Z\",\"endsAt\":\"2027-01-15T12:00:00.5Z\",\"extensions\":[]}")]
    [InlineData("{\"startedAt\":\"2027-01-15T08:00:00Z\",\"endsAt\":\"2027-02-30T12:00:00Z\",\"extensions\":[]}")]
    [InlineData("{\"startedAt\":\"1969-12-31T23:59:59Z\",\"endsAt\":\"2027-01-15T12:00:00Z\",\"extensions\":[]}")]
    [InlineData("{\"startedAt\":\"2027-01-15T08:00:00Z\",\"endsAt\":1800000000,\"extensions\":[]}")]
    [InlineData("{\"startedAt\":\"2027-01-15T08:00:00Z\",\"endsAt\":\"2027-01-15T12:00:00Z\",\"extensions\":[\"x\"]}")]
    public void MalformedSessionIsUnreadable(string text) =>
        Assert.IsType<SessionRead.Unreadable>(JournalStore.DecodeSession(text));

    [Fact]
    public void OffsetDatesAreAccepted()
    {
        var read = JournalStore.DecodeSession("{\"startedAt\":\"2027-01-15T10:00:00+02:00\",\"endsAt\":\"2027-01-15T12:00:00Z\",\"extensions\":[]}");
        Assert.Equal(T0, Assert.IsType<SessionRead.Valid>(read).Session.StartedAt);
    }

    [Fact]
    public void FolderAtSessionPathIsUnreadable()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(home.Paths.Session);
        Assert.IsType<SessionRead.Unreadable>(home.Store.ReadSession());
    }

    [Fact]
    public void SetAsideNeverOverwrites()
    {
        using var home = new TempHome();
        File.WriteAllText(home.Paths.Session, "garbage 1");
        var first = home.Store.SetAsideSession(T0);
        File.WriteAllText(home.Paths.Session, "garbage 2");
        var second = home.Store.SetAsideSession(T0);
        Assert.NotEqual(first, second);
        Assert.EndsWith("session.json.unreadable-20270115T080000Z", first);
        Assert.Equal("garbage 1", File.ReadAllText(first));
        Assert.Equal("garbage 2", File.ReadAllText(second));
        Assert.False(File.Exists(home.Paths.Session));
    }

    [Fact]
    public void StateRoundTripsAndEmptyJournalRemovesFile()
    {
        using var home = new TempHome();
        var state = new RuntimeState();
        state.LidPolicy.Add(new LidPolicyEntry(Guid.NewGuid(), 1, 1));
        state.FrozenProcesses.Add(new FrozenProcess(10, 133_000_000_000_000_000, "slack.exe", "slack.exe", true));
        state.SavedAudioOutputs.Add(new SavedAudioOutput("{0.0.0}.{abc}", "Speakers", 0.6f, false, Guid.NewGuid()));
        state.PowerMode.Add(new PowerModeEntry(PowerSourceKind.Dc, Guid.Empty, PowerOverlays.BestPowerEfficiency));
        state.PowerSavingSetByUs = true;
        home.Store.WriteState(state);
        var json = File.ReadAllText(home.Paths.State);
        Assert.Contains("\"source\": \"dc\"", json);
        Assert.DoesNotContain("isEmpty", json, StringComparison.OrdinalIgnoreCase);
        var read = home.ReadState();
        Assert.Equal(state.LidPolicy, read.LidPolicy);
        Assert.Equal(state.FrozenProcesses, read.FrozenProcesses);
        Assert.Equal(state.SavedAudioOutputs, read.SavedAudioOutputs);
        Assert.Equal(state.PowerMode, read.PowerMode);
        Assert.True(read.PowerSavingSetByUs);

        home.Store.WriteState(new RuntimeState());
        Assert.False(File.Exists(home.Paths.State));
        Assert.True(home.ReadState().IsEmpty);
    }

    [Fact]
    public void CorruptStateIsUnreadable()
    {
        using var home = new TempHome();
        File.WriteAllText(home.Paths.State, "{ \"lidPolicy\": 7 }");
        Assert.IsType<StateRead.Unreadable>(home.Store.ReadState());
    }

    [Fact]
    public void ConfigWithMissingKeysGetsDefaults()
    {
        using var home = new TempHome();
        File.WriteAllText(home.Paths.Config, "{ \"endFloor\": 20, \"freezeAllApps\": true }");
        var (config, problem) = home.Store.ReadConfig();
        Assert.Null(problem);
        Assert.Equal(20, config.EndFloor);
        Assert.True(config.FreezeAllApps);
        Assert.Equal(40, config.LowPowerFloor);
        Assert.True(config.MuteOnLidClose);
        Assert.Equal(Config.DefaultFreezeList, config.FreezeList);
    }

    [Fact]
    public void ConfigRoundTrips()
    {
        using var home = new TempHome();
        var config = new Config() with { HotspotSsid = "Phone", TmuxTargets = ["main:0.1"], DockerRule = true };
        home.Store.WriteConfig(config);
        Assert.DoesNotContain("floorsAreOrdered", File.ReadAllText(home.Paths.Config));
        Assert.Equal(config, home.Store.ReadConfig().Config);
    }

    [Fact]
    public void BrokenConfigReportsAProblem()
    {
        using var home = new TempHome();
        File.WriteAllText(home.Paths.Config, "{ nope");
        var (config, problem) = home.Store.ReadConfig();
        Assert.NotNull(problem);
        Assert.Equal(new Config(), config);
    }

    [Fact]
    public void AtomicWriteLeavesNoTemporaryFiles()
    {
        using var home = new TempHome();
        for (var i = 0; i < 5; i++)
        {
            JournalStore.AtomicWrite(home.Paths.Config, JsonSerializer.Serialize(new { i }));
        }
        Assert.Equal([home.Paths.Config], Directory.GetFiles(home.Root));
    }

    [Fact]
    public void RecoveryLockIsExclusive()
    {
        using var home = new TempHome();
        using var first = home.Lock.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(first);
        Assert.Null(home.Lock.TryAcquire(TimeSpan.FromMilliseconds(100)));
        first!.Dispose();
        using var again = home.Lock.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(again);
    }

    [Fact]
    public void LogRotatesAtCap()
    {
        using var home = new TempHome();
        var log = new RotatingLog(home.Paths.InsomniaLog);
        File.WriteAllText(home.Paths.InsomniaLog, new string('x', (int)RotatingLog.Cap));
        log.Write("after");
        Assert.True(File.Exists(home.Paths.InsomniaLog + ".1"));
        Assert.Equal("after" + Environment.NewLine, File.ReadAllText(home.Paths.InsomniaLog));
    }
}
