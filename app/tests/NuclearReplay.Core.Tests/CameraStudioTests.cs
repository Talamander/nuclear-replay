using System.Numerics;
using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

// replay/src/CameraStudio.cs: the viewer's Camera Studio model (keys, timing rules, path shot settings).
public class CameraStudioTests
{
    private static CameraStudio.Key K(float x = 0, float yaw = 0, bool rel = false) =>
        new CameraStudio.Key { World = new Vector3(x, 100, 0), Yaw = yaw, Pitch = 5, Fov = 50, HasRel = rel, Rel = new Vector3(0, 5, -30), RelYaw = 10 };

    private static double[] Times(CameraStudio s) => s.Keys.ConvertAll(k => k.Rt).ToArray();

    [Fact]
    public void FirstKeyTakesTheReplayTime()
    {
        var s = new CameraStudio();
        Assert.Equal(0, s.Add(K(), 12.5));
        Assert.Equal(12.5, s.Keys[0].Rt);
        Assert.Equal(0, s.Selected);
        Assert.Equal(12.5, s.Start);
        Assert.Equal(11, s.ShotTime);
    }

    [Fact]
    public void FirstKeyNeverBeforeTheStart()
    {
        var s = new CameraStudio();
        s.Add(K(), -2);
        Assert.Equal(0, s.Keys[0].Rt);
    }

    [Fact]
    public void AppendUsesNowWhenTheReplayMovedOn()
    {
        var s = new CameraStudio();
        s.Add(K(), 10);
        s.Add(K(), 14);
        Assert.Equal(new[] { 10.0, 14.0 }, Times(s));
        Assert.Equal(1, s.Selected);
    }

    [Fact]
    public void AppendWhileStandingStillSpacesKeysByGap()
    {
        var s = new CameraStudio();
        s.Add(K(), 10);
        s.Add(K(), 10.1);
        s.Add(K(), 10.1);
        Assert.Equal(new[] { 10.0, 10 + CameraStudio.Gap, 10 + 2 * CameraStudio.Gap }, Times(s));
    }

    [Fact]
    public void InsertAfterTheSelectedKey()
    {
        var s = new CameraStudio();
        s.Add(K(), 10);
        s.Add(K(), 20);
        s.Select(-1);                       // back from the second key to the first
        Assert.Equal(0, s.Selected);
        Assert.Equal(1, s.Add(K(1), 15));   // now falls between: used as is
        Assert.Equal(new[] { 10.0, 15.0, 20.0 }, Times(s));
        Assert.Equal(1, s.Selected);

        s.Selected = 0;
        s.Add(K(2), 40);                    // now outside 10..15: halfway
        Assert.Equal(new[] { 10.0, 12.5, 15.0, 20.0 }, Times(s));
        Assert.Equal(2f, s.Keys[1].World.X);
    }

    [Fact]
    public void UpdateKeepsTheTimeAndDropsTargetSpaceWhenAKeyIsNotRelative()
    {
        var s = new CameraStudio { UnitId = "77" };
        s.Add(K(rel: true), 5);
        s.Add(K(rel: true), 9);
        Assert.True(s.ToggleSpace());
        Assert.True(s.TargetSpace);
        Assert.True(s.Update(K(x: 3, rel: false)));
        Assert.Equal(9, s.Keys[1].Rt);
        Assert.Equal(3f, s.Keys[1].World.X);
        Assert.False(s.TargetSpace);
    }

    [Fact]
    public void UpdateAndDeleteNeedASelection()
    {
        var s = new CameraStudio();
        Assert.False(s.Update(K()));
        Assert.False(s.Delete());
        Assert.False(s.Retime(1));
    }

    [Fact]
    public void DeleteKeepsANearbySelection()
    {
        var s = new CameraStudio();
        s.Add(K(0), 1); s.Add(K(1), 2); s.Add(K(2), 3);
        s.Selected = 2;
        Assert.True(s.Delete());
        Assert.Equal(1, s.Selected);
        s.Selected = 0;
        Assert.True(s.Delete());
        Assert.Equal(0, s.Selected);
        Assert.Equal(1f, s.Keys[0].World.X);
        Assert.True(s.Delete());
        Assert.Equal(-1, s.Selected);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void SelectWrapsBothWays()
    {
        var s = new CameraStudio();
        Assert.Null(s.Select(1));
        s.Add(K(0), 1); s.Add(K(1), 2); s.Add(K(2), 3);
        s.Selected = -1;
        Assert.Equal(0f, s.Select(1).World.X);
        s.Selected = -1;
        Assert.Equal(2f, s.Select(-1).World.X);
        Assert.Equal(0f, s.Select(1).World.X);
        Assert.Equal(2f, s.Select(-1).World.X);
    }

    [Fact]
    public void RetimeMovesLaterKeysAndClamps()
    {
        var s = new CameraStudio();
        s.Add(K(), 10); s.Add(K(), 14); s.Add(K(), 18);
        s.Selected = 1;
        Assert.True(s.Retime(0.5));
        Assert.Equal(new[] { 10.0, 14.5, 18.5 }, Times(s));
        Assert.True(s.Retime(-30));         // stops just after the key before it
        Assert.Equal(10 + CameraStudio.MinStep, s.Keys[1].Rt, 9);
        Assert.Equal(14 + CameraStudio.MinStep, s.Keys[2].Rt, 9);
        Assert.False(s.Retime(-1));         // already against it

        s.Selected = 0;
        Assert.True(s.Retime(-30));         // never before the replay's start
        Assert.Equal(0, s.Keys[0].Rt, 9);
    }

    [Fact]
    public void TargetSpaceNeedsEveryKeyRelativeAndAnAircraft()
    {
        var s = new CameraStudio();
        s.Add(K(rel: true), 1);
        Assert.False(s.ToggleSpace());      // no aircraft
        s.UnitId = "5";
        s.Add(K(rel: false), 2);
        Assert.False(s.ToggleSpace());      // one key is world only
        s.Delete();
        Assert.True(s.ToggleSpace());
        Assert.True(s.TargetSpace);
        Assert.True(s.ToggleSpace());       // back to world always works
        Assert.False(s.TargetSpace);
    }

    [Theory]
    [InlineData(0, 0f, 0f, 0f)]
    [InlineData(1, 1f, 0f, 0f)]
    [InlineData(2, 2.2f, 0f, 0f)]
    [InlineData(3, 0.3f, 1f, 0f)]
    [InlineData(4, 0f, 0f, 1f)]
    public void ShakeAmounts(int index, float handheld, float rumble, float buffet)
    {
        var s = new CameraStudio();
        for (int i = 0; i < index; i++) s.NextShake();
        s.ShakeAmounts(out var h, out var r, out var b);
        Assert.Equal((handheld, rumble, buffet), (h, r, b));
        Assert.Equal(CameraStudio.ShakeNames[index], s.ShakeName);
    }

    [Fact]
    public void ShakeAndAimCycle()
    {
        var s = new CameraStudio();
        for (int i = 0; i < CameraStudio.ShakeNames.Length; i++) s.NextShake();
        Assert.Equal(0, s.ShakeIndex);
        s.NextAim(); Assert.Equal("target", s.Aim);
        s.NextAim(); Assert.Equal("path", s.Aim);
        s.NextAim(); Assert.Equal("keys", s.Aim);
    }

    [Fact]
    public void ShotTargetIsTheAircraftOnlyWhenTheShotNeedsIt()
    {
        var s = new CameraStudio();
        Assert.Equal("none", s.ShotTarget);
        s.Aim = "target";
        Assert.Equal("none", s.ShotTarget);  // no aircraft followed
        s.UnitId = "42";
        Assert.Equal("unit:42", s.ShotTarget);
        s.Aim = "keys";
        Assert.Equal("none", s.ShotTarget);
        s.TargetSpace = true;
        Assert.Equal("unit:42", s.ShotTarget);
    }

    [Fact]
    public void ShotKeysStartLeadAfterTheShotInEitherSpace()
    {
        var s = new CameraStudio { UnitId = "1" };
        s.Add(K(x: 7, yaw: 90, rel: true), 20);
        s.Add(K(x: 9, yaw: 95, rel: true), 23.4567);
        var world = s.ShotKeys();
        Assert.Equal(18.5, s.ShotTime);
        Assert.Equal(CameraStudio.Lead, world[0].T);
        Assert.Equal(4.957, world[1].T);
        Assert.Equal(7f, world[0].Pos.X);
        Assert.Equal(90f, world[0].Yaw);

        s.ToggleSpace();
        var rel = s.ShotKeys();
        Assert.Equal(new Vector3(0, 5, -30), rel[0].Pos);
        Assert.Equal(10f, rel[0].Yaw);
        Assert.Equal(5f, rel[1].Pitch);
        Assert.Equal(50f, rel[1].Fov);
    }

    [Fact]
    public void ToRelativeUsesTheLevelHeadingFrame()
    {
        var unit = new Vector3(1000, 500, -200);
        // Heading 90 (flying along +x): a point 30 m along +x is 30 m ahead, one along -z is 10 m to the left.
        Assert.True(Vector3.Distance(CameraStudio.ToRelative(unit + new Vector3(30, 4, 0), unit, 90), new Vector3(0, 4, 30)) < 1e-3f);
        Assert.True(Vector3.Distance(CameraStudio.ToRelative(unit + new Vector3(0, 0, 10), unit, 90), new Vector3(-10, 0, 0)) < 1e-3f);
        // Heading 0: world and relative axes agree.
        Assert.True(Vector3.Distance(CameraStudio.ToRelative(unit + new Vector3(3, 2, 1), unit, 0), new Vector3(3, 2, 1)) < 1e-4f);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(190f, -170f)]
    [InlineData(-190f, 170f)]
    [InlineData(540f, 180f)]
    [InlineData(-180f, 180f)]
    public void WrapYaw(float yaw, float wrapped) => Assert.Equal(wrapped, CameraStudio.WrapYaw(yaw), 3);

    [Theory]
    [InlineData(0, "0:00.0")]
    [InlineData(-3, "0:00.0")]
    [InlineData(65.25, "1:05.3")]
    [InlineData(600, "10:00.0")]
    public void Clock(double seconds, string text) => Assert.Equal(text, CameraStudio.Clock(seconds));

    [Fact]
    public void DescribeNamesTheKeysAndSettings()
    {
        var s = new CameraStudio();
        Assert.Contains("no keys yet", s.Describe());
        s.Add(new CameraStudio.Key { World = new Vector3(0, 100, 0), Fov = 50 }, 10);
        s.Add(new CameraStudio.Key { World = new Vector3(0, 100, 120), Fov = 50 }, 14);
        string text = s.Describe();
        Assert.Contains("2 keys, 0:10.0 to 0:14.0 (4.0 s, 120 m)", text);
        Assert.Contains("Key 2 at 0:14.0", text);
        Assert.Contains("Timing even (T)", text);
        Assert.Contains("Space world (no aircraft followed)", text);
        s.UnitId = "9"; s.UnitName = "F/A-26 Wyvern";
        Assert.Contains("Aircraft F/A-26 Wyvern", s.Describe());
    }

    [Fact]
    public void EveryChangeBumpsTheVersion()
    {
        var s = new CameraStudio();
        int v = s.Version;
        s.Add(K(), 1); Assert.True(s.Version > v); v = s.Version;
        s.Retime(1); Assert.True(s.Version > v); v = s.Version;
        s.ToggleTiming(); Assert.True(s.Version > v); v = s.Version;
        s.Clear(); Assert.True(s.Version > v);
        Assert.Equal(-1, s.Selected);
    }
}
