using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

public class JumpRadarStateTests
{
    [Fact]
    public void ToggleAfterReplacementSurvivesDeferredCleanup()
    {
        var state = new JumpRadarState<string>();
        state.Replaced("aircraft");
        state.Replaced("aircraft");
        state.RecordToggle("aircraft", true);

        Assert.True(state.Restore("aircraft", current: false));
    }

    [Fact]
    public void ReplacementWithoutToggleKeepsTheGamesResult()
    {
        var state = new JumpRadarState<string>();
        state.Replaced("aircraft");
        state.Replaced("aircraft");

        Assert.False(state.Restore("aircraft", current: false));
        Assert.True(state.Restore("aircraft", current: true));
    }

    [Fact]
    public void ReplacementInvalidatesAnEarlierToggle()
    {
        var state = new JumpRadarState<string>();
        state.RecordToggle("aircraft", true);
        state.Replaced("aircraft");

        Assert.False(state.Restore("aircraft", current: false));
    }

    [Fact]
    public void LastToggleWinsWithoutChangingOtherAircraft()
    {
        var state = new JumpRadarState<string>();
        state.RecordToggle("first", true);
        state.RecordToggle("second", true);
        state.RecordToggle("first", false);

        Assert.False(state.Restore("first", current: true));
        Assert.True(state.Restore("second", current: false));
    }

    [Fact]
    public void EndingOrAbortingBurstForgetsRecordedToggles()
    {
        var state = new JumpRadarState<string>();
        state.RecordToggle("aircraft", true);
        state.Clear();

        Assert.False(state.Restore("aircraft", current: false));
    }
}
