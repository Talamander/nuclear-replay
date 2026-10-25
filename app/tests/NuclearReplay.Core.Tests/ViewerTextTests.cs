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

    [Theory]
    [InlineData(500, "1 KB")]
    [InlineData(840_000, "821 KB")]
    [InlineData(327_155_712, "312 MB")]
    [InlineData(1_503_238_553, "1.4 GB")]
    [InlineData(32_212_254_720, "30 GB")]
    public void SizesAreShort(long bytes, string text) => Assert.Equal(text, ViewerText.Size(bytes));

    [Fact]
    public void DeleteTextsAndFiles()
    {
        Assert.Equal("No recordings", ViewerText.LibrarySummary(0, 0));
        Assert.Equal("1 recording, 312 MB on disk", ViewerText.LibrarySummary(1, 327_155_712));
        Assert.Equal("Delete \"05. Furball\" (312 MB) for good? Clips made from it stay.", ViewerText.DeleteQuestion("05. Furball", 327_155_712));
        var files = ViewerText.RecordingFiles(Path.Combine("demos", "20261005T210314Z-05. Furball.ncdemo"));
        Assert.Equal(Path.Combine("demos", "20261005T210314Z-05. Furball.ncdemo"), files[0]);
        Assert.Equal(Path.Combine("demos", "20261005T210314Z-05. Furball.shots.json"), files[1]);
        Assert.Equal(Path.Combine("demos", "20261005T210314Z-05. Furball.path.json"), files[2]);
        Assert.Equal(3, files.Length);
    }
}
