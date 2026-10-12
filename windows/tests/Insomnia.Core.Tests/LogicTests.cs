using Insomnia.Core.Logic;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;
using Xunit;

namespace Insomnia.Core.Tests;

public class SessionMathTests
{
    private static readonly DateTimeOffset T0 = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private const double Max = 30 * 24 * 3600;

    [Fact]
    public void NewSessionClampsToMinimumAndMaximum()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), SessionMath.NewSession(T0, TimeSpan.FromSeconds(5), Max).EndsAt - T0);
        Assert.Equal(TimeSpan.FromDays(30), SessionMath.NewSession(T0, TimeSpan.FromDays(45), Max).EndsAt - T0);
    }

    [Fact]
    public void NewSessionUsesWholeSeconds()
    {
        var s = SessionMath.NewSession(T0.AddMilliseconds(700), TimeSpan.FromMinutes(30), Max);
        Assert.Equal(T0, s.StartedAt);
        Assert.Equal(T0.AddMinutes(30), s.EndsAt);
    }

    [Fact]
    public void ExtendAddsToDeadlineAndRecordsExtension()
    {
        var s = SessionMath.NewSession(T0, TimeSpan.FromHours(1), Max);
        var e = SessionMath.Extended(s, TimeSpan.FromMinutes(30), T0.AddMinutes(10), Max);
        Assert.Equal(T0.AddMinutes(90), e.EndsAt);
        Assert.Equal([1800.0], e.Extensions);
    }

    [Fact]
    public void ExtendExpiredSessionRestartsFromNow()
    {
        var s = SessionMath.NewSession(T0, TimeSpan.FromHours(1), Max);
        var now = T0.AddHours(2);
        Assert.Equal(now.AddMinutes(15), SessionMath.Extended(s, TimeSpan.FromMinutes(15), now, Max).EndsAt);
    }

    [Fact]
    public void ExtendNeverPassesCeiling()
    {
        var s = SessionMath.NewSession(T0, TimeSpan.FromDays(29), Max);
        Assert.Equal(T0.AddDays(30), SessionMath.Extended(s, TimeSpan.FromDays(5), T0, Max).EndsAt);
    }

    [Theory]
    [InlineData(0, "<1m")]
    [InlineData(59, "<1m")]
    [InlineData(60, "1m")]
    [InlineData(3600, "1h")]
    [InlineData(3600 + 14 * 60 + 59, "1h 14m")]
    [InlineData(3 * 86400 + 2 * 3600, "3d 2h")]
    [InlineData(2 * 86400 + 30 * 60, "2d")]
    public void FormatRemaining(double seconds, string expected) =>
        Assert.Equal(expected, SessionMath.FormatRemaining(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(1800, CountdownShape.Minutes, "30:00")]
    [InlineData(1799.2, CountdownShape.Minutes, "30:00")]
    [InlineData(9, CountdownShape.Minutes, "00:09")]
    [InlineData(247, CountdownShape.Hours, "0:04:07")]
    [InlineData(86400 + 3661, CountdownShape.Days, "1d 1:01:01")]
    [InlineData(3661, CountdownShape.Days, "1:01:01")]
    [InlineData(-5, CountdownShape.Minutes, "00:00")]
    public void FormatCountdown(double seconds, CountdownShape shape, string expected) =>
        Assert.Equal(expected, SessionMath.FormatCountdown(TimeSpan.FromSeconds(seconds), shape));

    [Fact]
    public void ShapeFollowsInitialSpan()
    {
        Assert.Equal(CountdownShape.Minutes, CountdownShapes.ForInitialDuration(TimeSpan.FromMinutes(59)));
        Assert.Equal(CountdownShape.Hours, CountdownShapes.ForInitialDuration(TimeSpan.FromHours(1)));
        Assert.Equal(CountdownShape.Days, CountdownShapes.ForInitialDuration(TimeSpan.FromDays(1)));
    }
}

public class DurationInputTests
{
    [Fact]
    public void DigitsAppendWithinCeilings()
    {
        var input = new DurationInput();
        input = input.Append(2, DurationField.Hours)!.Value;
        input = input.Append(3, DurationField.Hours)!.Value;
        Assert.Equal(23, input.Hours);
        Assert.Null(input.Append(1, DurationField.Hours));
        Assert.Null(new DurationInput(Hours: 3).Append(0, DurationField.Hours));
        Assert.Null(new DurationInput(Days: 3).Append(1, DurationField.Days));
        Assert.Equal(30, new DurationInput(Days: 3).Append(0, DurationField.Days)!.Value.Days);
    }

    [Fact]
    public void LeadingZeroIsNotRepeated()
    {
        var zero = new DurationInput().Append(0, DurationField.Minutes)!.Value;
        Assert.Equal(0, zero.Minutes);
        Assert.Null(zero.Append(0, DurationField.Minutes));
        Assert.Equal(5, zero.Append(5, DurationField.Minutes)!.Value.Minutes);
    }

    [Fact]
    public void BackspaceEmptiesThenReportsEmpty()
    {
        var input = new DurationInput(Minutes: 45);
        input = input.Backspace(DurationField.Minutes)!.Value;
        Assert.Equal(4, input.Minutes);
        input = input.Backspace(DurationField.Minutes)!.Value;
        Assert.Null(input.Minutes);
        Assert.Null(input.Backspace(DurationField.Minutes));
    }

    [Fact]
    public void TotalNeedsSomethingNonZero()
    {
        Assert.Null(new DurationInput(Minutes: 0).Total);
        Assert.Equal(TimeSpan.FromSeconds(86400 + 7200 + 300), new DurationInput(1, 2, 5).Total);
        Assert.True(new DurationInput().IsEmpty);
    }

    [Fact]
    public void FromSecondsFillsOnlyNonZeroFields()
    {
        var input = DurationInput.FromSeconds(2 * 3600);
        Assert.Null(input.Days);
        Assert.Equal(2, input.Hours);
        Assert.Null(input.Minutes);
        Assert.Equal(30, DurationInput.FromSeconds(90 * 86400).Days);
    }

    [Fact]
    public void FieldNavigationWraps()
    {
        Assert.Equal(DurationField.Days, DurationField.Minutes.Next());
        Assert.Equal(DurationField.Minutes, DurationField.Days.Previous());
    }

    [Theory]
    [InlineData("30", 1800.0)]
    [InlineData("30m", 1800.0)]
    [InlineData("2h", 7200.0)]
    [InlineData("1h30m", 5400.0)]
    [InlineData("1h 30m", 5400.0)]
    [InlineData("3d", 259200.0)]
    [InlineData(" 2H ", 7200.0)]
    public void ParserAccepts(string text, double seconds) => Assert.Equal(seconds, DurationParser.Seconds(text));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("h")]
    [InlineData("2x")]
    [InlineData("2h5")]
    [InlineData("-5")]
    [InlineData("99999999999999999999d")]
    public void ParserRejects(string text) => Assert.Null(DurationParser.Seconds(text));
}

public class FloorRulesTests
{
    private static readonly Config Defaults = new();

    private static IReadOnlyList<FloorAction> Eval(BatteryStatus battery, bool onAc = false, ThermalState thermal = ThermalState.Nominal, bool lid = false, bool ours = false, Config? config = null) =>
        FloorRules.Evaluate(battery, onAc, thermal, lid, ours, config ?? Defaults);

    [Fact]
    public void BelowEndFloorOnBatteryEnds() =>
        Assert.Equal([new FloorAction.EndSession(EndReason.BatteryFloor)], Eval(new BatteryStatus.Level(9)));

    [Fact]
    public void BelowEndFloorOnChargerDoesNotEnd() =>
        Assert.Empty(Eval(new BatteryStatus.Level(9), onAc: true));

    [Fact]
    public void EndFloorZeroTurnsTheEndOff()
    {
        var config = Defaults.WithEndFloor(0);
        Assert.Equal([new FloorAction.EnablePowerSaving(PowerSavingCause.Battery)], Eval(new BatteryStatus.Level(1), config: config));
    }

    [Fact]
    public void BelowPowerSavingFloorEnables() =>
        Assert.Equal([new FloorAction.EnablePowerSaving(PowerSavingCause.Battery)], Eval(new BatteryStatus.Level(39)));

    [Fact]
    public void CriticalThermalEnds() =>
        Assert.Equal([new FloorAction.EndSession(EndReason.ThermalCritical)], Eval(new BatteryStatus.Level(90), thermal: ThermalState.Critical));

    [Fact]
    public void ThermalRulesOffIgnoresThermal() =>
        Assert.Empty(Eval(new BatteryStatus.Level(90), thermal: ThermalState.Critical, config: Defaults with { ThermalRules = false }));

    [Fact]
    public void SeriousThermalEnablesWithThermalCause() =>
        Assert.Equal([new FloorAction.EnablePowerSaving(PowerSavingCause.Thermal)], Eval(new BatteryStatus.Level(30), thermal: ThermalState.Serious));

    [Fact]
    public void UnreadableTwiceOnBatteryEnds()
    {
        Assert.Empty(Eval(new BatteryStatus.Unreadable(1)));
        Assert.Equal([new FloorAction.EndSession(EndReason.BatteryUnreadable)], Eval(new BatteryStatus.Unreadable(2)));
        Assert.Empty(Eval(new BatteryStatus.Unreadable(2), onAc: true));
        Assert.Empty(Eval(new BatteryStatus.Unreadable(5), config: Defaults.WithEndFloor(0)));
    }

    [Fact]
    public void DesktopHasNoFloor() => Assert.Empty(Eval(new BatteryStatus.NoBattery()));

    [Fact]
    public void LidClosedEnablesUnlessTurnedOff()
    {
        Assert.Equal([new FloorAction.EnablePowerSaving(PowerSavingCause.Lid)], Eval(new BatteryStatus.Level(90), onAc: true, lid: true));
        Assert.Empty(Eval(new BatteryStatus.Level(90), lid: true, config: Defaults with { PowerSavingOnLidClose = false }));
    }

    [Fact]
    public void NoCauseWhileOursDisables() =>
        Assert.Equal([new FloorAction.DisablePowerSaving()], Eval(new BatteryStatus.Level(90), ours: true));

    [Fact]
    public void CauseWhileOursDoesNothing() =>
        Assert.Empty(Eval(new BatteryStatus.Level(30), ours: true));
}

public class ConfigTests
{
    [Fact]
    public void FloorSettersKeepOrder()
    {
        var c = new Config().WithEndFloor(40);
        Assert.Equal(40, c.EndFloor);
        Assert.Equal(45, c.LowPowerFloor);
        c = c.WithLowPowerFloor(20);
        Assert.Equal(15, c.EndFloor);
        c = new Config().WithLowPowerFloor(5).WithLowPowerFloor(0);
        Assert.Equal(0, c.EndFloor);
        Assert.Equal(Config.MaxEndFloor, new Config().WithEndFloor(100).EndFloor);
    }

    [Fact]
    public void NormalizeRaisesLowPowerFloor()
    {
        var (c, changes) = (new Config() with { EndFloor = 50, LowPowerFloor = 30 }).Normalized();
        Assert.Equal(50, c.EndFloor);
        Assert.Equal(55, c.LowPowerFloor);
        Assert.Single(changes);
    }

    [Fact]
    public void NormalizeLeavesDefaultsAlone() => Assert.Empty(new Config().Normalized().Changes);

    [Fact]
    public void NormalizeCleansListsAndRanges()
    {
        var (c, changes) = (new Config() with
        {
            FreezeList = ["slack.exe", " Slack.exe ", ""],
            Presets = [10, 1800, 1800, 99 * 86400],
            DefaultPreset = -1,
            NudgeThreshold = double.NaN,
        }).Normalized();
        Assert.Equal(["slack.exe"], c.FreezeList);
        Assert.Equal([1800.0], c.Presets);
        Assert.Equal(4 * 3600, c.DefaultPreset);
        Assert.Equal(90, c.NudgeThreshold);
        Assert.NotEmpty(changes);
    }
}

public class FreezePlannerTests
{
    private const string Win = @"C:\Windows";

    private static ProcessInfo P(int pid, string image, int parent = 1, bool window = false, string? path = null, bool user = true, long? created = null) =>
        new(pid, parent, created ?? 1000 + pid, image, path ?? $@"C:\Apps\{image}", Path.GetFileNameWithoutExtension(image), user, window);

    [Fact]
    public void ExplicitListFreezesTheWholeTree()
    {
        var snapshot = new[]
        {
            P(10, "slack.exe", window: true),
            P(11, "slack.exe", parent: 10),
            P(12, "crashpad.exe", parent: 11),
            P(20, "notepad.exe", window: true),
        };
        var plan = FreezePlanner.Plan(snapshot, new Config(), 1, Win);
        var group = Assert.Single(plan);
        Assert.Equal(FreezeOrigin.Explicit, group.Origin);
        Assert.Equal([10, 11, 12], group.Processes.Select(p => p.Pid).Order());
    }

    [Fact]
    public void ReusedParentPidIsNotFollowed()
    {
        // pid 30 was created before its "parent" 10, so 10 is not its parent.
        var snapshot = new[] { P(10, "slack.exe", window: true, created: 5000), P(30, "unrelated.exe", parent: 10, created: 100) };
        var group = Assert.Single(FreezePlanner.Plan(snapshot, new Config(), 1, Win));
        Assert.Equal([10], group.Processes.Select(p => p.Pid));
    }

    [Fact]
    public void AutomaticScopeSkipsProtectedAgentsSystemAndOtherUsers()
    {
        var snapshot = new[]
        {
            P(10, "spotify.exe", window: true),
            P(11, "chrome.exe", window: true),
            P(12, "Code.exe", window: true),
            P(13, "idea64.exe", window: true, path: @"C:\Program Files\JetBrains\IntelliJ\bin\idea64.exe"),
            P(14, "Notepad.exe", window: true, path: @"C:\Windows\System32\Notepad.exe"),
            P(15, "explorer.exe", window: true),
            P(16, "games.exe", window: true, user: false),
            P(17, "Zoom.exe", window: true),
            P(18, "myagent.exe", window: true),
            P(19, "background.exe"),
            P(42, "Insomnia.exe", window: true),
        };
        var config = new Config() with { FreezeList = [], FreezeAllApps = true, AgentList = ["myagent.exe"] };
        var plan = FreezePlanner.Plan(snapshot, config, 42, Win);
        Assert.Equal(["spotify.exe"], plan.Select(g => g.Image));
        Assert.Equal(FreezeOrigin.Automatic, plan[0].Origin);
    }

    [Fact]
    public void ExplicitEntryOverridesProtectionButNotDenylist()
    {
        var snapshot = new[] { P(10, "Zoom.exe", window: true), P(11, "explorer.exe", window: true) };
        var config = new Config() with { FreezeList = ["Zoom.exe", "explorer.exe"] };
        Assert.Equal(["Zoom.exe"], FreezePlanner.Plan(snapshot, config, 1, Win).Select(g => g.Image));
    }

    [Fact]
    public void AgentListBeatsExplicitList()
    {
        var snapshot = new[] { P(10, "slack.exe", window: true) };
        var config = new Config() with { AgentList = ["slack.exe"] };
        Assert.Empty(FreezePlanner.Plan(snapshot, config, 1, Win));
    }

    [Fact]
    public void ProtectedChildOfFrozenAppIsLeftRunning()
    {
        var snapshot = new[] { P(10, "launcher.exe", window: true), P(11, "chrome.exe", parent: 10) };
        var config = new Config() with { FreezeList = ["launcher.exe"] };
        Assert.Equal([10], Assert.Single(FreezePlanner.Plan(snapshot, config, 1, Win)).Processes.Select(p => p.Pid));
    }

    [Fact]
    public void ExplicitFirstThenAutomaticByName()
    {
        var snapshot = new[]
        {
            P(10, "zeta.exe", window: true),
            P(11, "alpha.exe", window: true),
            P(12, "Discord.exe", window: true),
            P(13, "slack.exe", window: true),
        };
        var config = new Config() with { FreezeAllApps = true };
        var plan = FreezePlanner.Plan(snapshot, config, 1, Win);
        Assert.Equal(["slack.exe", "Discord.exe", "alpha.exe", "zeta.exe"], plan.Select(g => g.Image));
    }

    [Fact]
    public void UnreadableProcessesAreSkipped()
    {
        var snapshot = new[] { P(10, "slack.exe", window: true, created: 0) };
        Assert.Empty(FreezePlanner.Plan(snapshot, new Config(), 1, Win));
    }
}
