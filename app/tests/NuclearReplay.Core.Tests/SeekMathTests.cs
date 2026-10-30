using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

// replay/src/SeekMath.cs: viewer timeline jumps (drawing only a few frames while fast-forwarding, the progress label).
public class SeekMathTests
{
    [Fact]
    public void FastForwardStepIsUnchangedFrom05()
    {
        // The replay clock during a jump advances as in 0.5: 8/60 s of replay per game frame (a 16/60 s step was
        // measured: no faster, since physics cost scales with replay time, and less accurate).
        Assert.Equal(8f / 60f, SeekMath.FastForwardSpeed / SeekMath.FastForwardStepFps, 6);
    }

    [Theory]
    [InlineData(1f / 60, 0.05f, 0.05f)]     // the game's 60 Hz physics: three times fewer steps while jumping
    [InlineData(0.02f, 0.05f, 0.05f)]
    [InlineData(0.08f, 0.05f, 0.08f)]       // never finer than the game's own step
    [InlineData(1f / 60, 0f, 1f / 60)]      // "seekfixed 0": the game's step
    [InlineData(1f / 60, float.NaN, 1f / 60)]
    public void JumpPhysicsStepIsNeverFinerThanTheGame(float game, float jump, float expected) =>
        Assert.Equal(expected, SeekMath.PhysicsStep(game, jump), 6);

    [Fact]
    public void JumpPhysicsStepStaysCoarseButBounded()
    {
        // Measured on 2026-10-30 (seek/RESULTS.md): 1/20 s keeps units, states, targets and the Target HUD equal
        // to a jump at the game's step. Larger steps are untested.
        Assert.InRange(SeekMath.JumpPhysicsStep, 1f / 60, 0.05f);
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(0, 1)]
    [InlineData(10, 1)]       // slow machine: draw every frame
    [InlineData(12, 1)]
    [InlineData(36, 3)]       // about 12 drawn frames a second
    [InlineData(60, 5)]
    [InlineData(240, 20)]
    [InlineData(600, 50)]
    [InlineData(5000, 60)]    // capped
    public void RenderIntervalDrawsAboutTwelveFramesASecond(double fps, int interval) =>
        Assert.Equal(interval, SeekMath.RenderInterval(fps));

    [Theory]
    [InlineData(60, 60, 600, 0)]
    [InlineData(60, 330, 600, 0.5)]
    [InlineData(60, 600, 600, 1)]
    [InlineData(60, 700, 600, 1)]     // overshoot by a step
    [InlineData(60, 30, 600, 0)]      // before the start (clock not moved yet)
    [InlineData(600, 600, 600, 1)]    // nothing to do
    [InlineData(0, 150, 300, 0.5)]    // backward jump: from the start after the reload
    public void FractionIsClamped(double from, double now, double target, double expected) =>
        Assert.Equal(expected, SeekMath.Fraction(from, now, target), 6);

    [Fact]
    public void LabelShowsReloadThenPercent()
    {
        Assert.Equal("Jumping to 25:00 (reloading the mission)...", SeekMath.Label("25:00", true, 0.4, false));
        Assert.Equal("Jumping to 25:00...  43%   Space stops here", SeekMath.Label("25:00", false, 0.437, true));
        Assert.Equal("Jumping to 25:00...  0%", SeekMath.Label("25:00", false, -1, false));
        Assert.Equal("Jumping to 25:00...  100%   Space stops here", SeekMath.Label("25:00", false, 1.5, true));
    }

    [Fact]
    public void RateIsReplaySecondsPerWallSecond()
    {
        Assert.Equal(30, SeekMath.Rate(540, 18), 6);
        Assert.Equal(0, SeekMath.Rate(540, 0));
    }
}
