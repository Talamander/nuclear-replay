using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

// replay/src/ViewerText.cs: in-game viewer text that needs no Unity.
public class ViewerTextTests
{
    [Fact]
    public void ClipToastIsPlain()
    {
        Assert.Equal("Clip saved: 05. Furball 0-14 to 0-22.mp4", ViewerText.ClipToast(true, "05. Furball 0-14 to 0-22.mp4", true, null));
        Assert.Equal("Clip saved without sound: 05. Furball 0-14 to 0-22.mp4", ViewerText.ClipToast(true, "05. Furball 0-14 to 0-22.mp4", false, null));
        Assert.Equal("Clip failed: ffmpeg exited with code 1", ViewerText.ClipToast(false, "x.mp4", true, "ffmpeg exited with code 1"));
        Assert.Equal("Clip failed: unknown error", ViewerText.ClipToast(false, "x.mp4", true, ""));
        Assert.DoesNotContain("fps", ViewerText.ClipToast(true, "a.mp4", false, null));
    }

    [Theory]
    [InlineData(28, true)]    // jumping to a moment starts 5 s before it
    [InlineData(33.6, true)]
    [InlineData(39.6, true)]  // the moment's clip ends 6 s after
    [InlineData(42.6, true)]
    [InlineData(43, false)]
    [InlineData(80, false)]
    [InlineData(25, false)]
    public void MomentShowsOnlyNearItsTime(double now, bool shown) => Assert.Equal(shown, ViewerText.MomentInView(now, 33.6));
}
