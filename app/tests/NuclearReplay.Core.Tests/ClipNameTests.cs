using NuclearReplay.Shared;

namespace NuclearReplay.Core.Tests;

// shared/Clips/ClipNames.cs names clips in the game viewer and nr, so one test covers both.
public class ClipNameTests
{
    [Theory]
    [InlineData(0, "0-00")]
    [InlineData(14.3, "0-14")]
    [InlineData(22.5, "0-23")]
    [InlineData(12.306, "0-12")]
    [InlineData(59.6, "1-00")]
    [InlineData(1929, "32-09")]
    [InlineData(3725, "1-02-05")]
    [InlineData(-3, "0-00")]
    public void ClockUsesWholeSeconds(double seconds, string expected) => Assert.Equal(expected, ClipNames.Clock(seconds));

    [Fact]
    public void StemIsTitleAndRange()
    {
        Assert.Equal("05. Furball 0-14 to 0-22", ClipNames.Stem("05. Furball", 14.3, 22.4));
        Assert.Equal("Escalation_ Co-op 0-30 to 0-41", ClipNames.Stem("Escalation: Co-op", 29.96, 40.96));
    }

    [Theory]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("  name. ", "name")]
    [InlineData("...", "clip")]
    [InlineData(null, "clip")]
    public void SanitizeMakesAWindowsFileName(string? name, string expected) => Assert.Equal(expected, ClipNames.Sanitize(name));

    [Fact]
    public void UniquePathNeverReusesAName()
    {
        using var dir = new TempDir();
        string first = ClipNames.UniquePath(dir.Path, "m 0-14 to 0-22");
        Assert.Equal(Path.Combine(dir.Path, "m 0-14 to 0-22.mp4"), first);

        File.WriteAllText(first, "existing");
        string second = ClipNames.UniquePath(dir.Path, "m 0-14 to 0-22");
        Assert.Equal(Path.Combine(dir.Path, "m 0-14 to 0-22 (2).mp4"), second);

        var planned = new HashSet<string> { second };
        Assert.Equal(Path.Combine(dir.Path, "m 0-14 to 0-22 (3).mp4"), ClipNames.UniquePath(dir.Path, "m 0-14 to 0-22", planned.Contains));
        Assert.Equal("existing", File.ReadAllText(first));
    }
}
