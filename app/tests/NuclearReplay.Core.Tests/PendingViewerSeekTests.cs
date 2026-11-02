using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

public class PendingViewerSeekTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void RetargetWaitsUntilTheOldWorldIsComplete(bool sceneReady, bool sending, bool settling)
    {
        var pending = new PendingViewerSeek();
        pending.Request(610, false, 12);
        Assert.False(pending.TryTake(sceneReady, sending, settling, out _, out _, out _));
        Assert.True(pending.TryTake(true, false, false, out double target, out _, out double requested));
        Assert.Equal(610, target);
        Assert.Equal(12, requested);
        Assert.False(pending.TryTake(true, false, false, out _, out _, out _));
    }

    [Fact]
    public void OnlyTheLatestRetargetRunsAndKeepsItsRequestTime()
    {
        var pending = new PendingViewerSeek();
        pending.Request(610, false, 12);
        pending.Request(300, true, 13);
        Assert.True(pending.TryTake(true, false, false, out double target, out bool keepClip, out double requested));
        Assert.Equal(300, target);
        Assert.True(keepClip);
        Assert.Equal(13, requested);
    }

    [Fact]
    public void StopOrExitCancelsThePendingRetarget()
    {
        var pending = new PendingViewerSeek();
        pending.Request(610, false, 12);
        pending.Clear();
        Assert.False(pending.TryTake(true, false, false, out _, out _, out _));
    }
}
