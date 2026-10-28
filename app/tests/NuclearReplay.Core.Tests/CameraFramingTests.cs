using System.Numerics;
using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

// replay/src/CameraFraming.cs: framing maths of the ride and two_shot cine cameras.
public class CameraFramingTests
{
    private static Quaternion Yaw(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180f);

    private static bool Near(Vector3 a, Vector3 b, float tol = 1e-3f) => Vector3.Distance(a, b) < tol;

    [Fact]
    public void RideOffsetIsInTheUnitsFrameAndRoundTrips()
    {
        var unitPos = new Vector3(100, 500, -40);
        var unitRot = Quaternion.Normalize(Yaw(90) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f));
        var camPos = new Vector3(103, 506, -48);
        var camRot = Yaw(-30);
        CameraFraming.RideFromPose(camPos, camRot, unitPos, unitRot, out var offset, out var lookDir);
        Assert.True(Near(CameraFraming.RidePosition(unitPos, unitRot, offset), camPos));
        // The look direction turns with the unit back into the camera's world direction.
        Assert.True(Near(Vector3.Transform(lookDir, unitRot), Vector3.Transform(CameraFraming.Forward, camRot)));
        Assert.Equal(1f, lookDir.Length(), 3);

        // The same offset follows the unit when it turns: 6 m off the right wing stays off the right wing.
        var right = CameraFraming.RidePosition(Vector3.Zero, Yaw(90), new Vector3(6, 0, 0));
        Assert.True(Near(right, new Vector3(0, 0, -6)));
    }

    [Theory]
    [InlineData("unit", 1f)]
    [InlineData("level", 0f)]
    [InlineData(" Level ", 0f)]
    [InlineData("0.25", 0.25f)]
    [InlineData("3", 1f)]
    [InlineData("-1", 0f)]
    [InlineData(null, 1f)]
    [InlineData("bank", 1f)]
    public void RollAmountReadsUnitLevelOrANumber(string? roll, float expected) => Assert.Equal(expected, CameraFraming.RollAmount(roll!), 4);

    [Fact]
    public void TwoShotSitsBehindTheSubjectOnItsSideAndHoldsBoth()
    {
        var a = new Vector3(0, 1000, 0);
        var b = new Vector3(0, 1000, 400);      // straight ahead (z) of the subject
        var axis = Vector3.Normalize(b - a);
        var right = CameraFraming.TwoShot(a, b, axis, axis, side: 1, distance: 40, azimuthDeg: 35, elevation: 0.15f,
            radiusA: 8, radiusB: 8, aspect: 16f / 9f, fovMin: 20, fovMax: 65, distanceMax: 240, margin: 0.3f);
        Assert.True(right.Fits);
        Assert.True(right.Camera.Z < a.Z, "behind the subject");
        Assert.True(right.Camera.X > 0, "on the right");
        Assert.True(right.Camera.Y > a.Y, "a little above");
        Assert.InRange(right.Fov, 20f, 65f);
        // Both units really are inside that lens from that camera.
        float need = CameraFraming.NeededFov(right.Camera, a, 8, b, 8, 16f / 9f, 0.3f, out _);
        Assert.True(need <= right.Fov + 0.01f);

        var left = CameraFraming.TwoShot(a, b, axis, axis, side: -1, distance: 40, azimuthDeg: 35, elevation: 0.15f,
            radiusA: 8, radiusB: 8, aspect: 16f / 9f, fovMin: 20, fovMax: 65, distanceMax: 240, margin: 0.3f);
        Assert.True(left.Camera.X < 0, "on the left");
    }

    [Fact]
    public void TwoShotBacksOffWhenTheLensLimitIsNotEnough()
    {
        var a = new Vector3(0, 1000, 0);
        var b = new Vector3(600, 1000, 50);     // far off to the side: needs a very wide lens from 30 m
        var axis = Vector3.Normalize(b - a);
        Assert.False(CameraFraming.TwoShot(a, b, axis, axis, 1, 30, 35, 0.15f, 8, 8, 16f / 9f, 20, 30, 30, 0.3f).Fits);
        var frame = CameraFraming.TwoShot(a, b, axis, axis, 1, 30, 35, 0.15f, 8, 8, 16f / 9f, 20, 30, 2000, 0.3f);
        Assert.True(frame.Distance > 30);
        Assert.True(frame.Fits);
        Assert.True(frame.Fov <= 30);

        var capped = CameraFraming.TwoShot(a, b, axis, axis, 1, 30, 35, 0.15f, 8, 8, 16f / 9f, 20, 30, 40, 0.3f);
        Assert.Equal(40, capped.Distance, 3);
        Assert.False(capped.Fits);
        Assert.Equal(30, capped.Fov, 3);
    }

    [Fact]
    public void TwoShotWithoutASecondUnitFramesTheSubjectFromBehindItsHeading()
    {
        var a = new Vector3(10, 200, 10);
        var heading = new Vector3(1, 0, 0);
        var frame = CameraFraming.TwoShot(a, Vector3.Zero, Vector3.Zero, heading, 1, 40, 0, 0, 8, 8, 16f / 9f, 20, 65, 200, 0.3f);
        Assert.True(Near(frame.Camera, a - heading * 40));
        Assert.True(Near(frame.Look, a));
    }

    [Fact]
    public void TurnTowardIsSmoothAndCapped()
    {
        var from = new Vector3(0, 0, 1);
        var to = new Vector3(1, 0, 0);
        // 90 degrees with a 40 deg/s cap: at most 4 degrees in 0.1 s.
        var step = CameraFraming.TurnToward(from, to, 0.1f, 0.01f, 40);
        float turned = MathF.Acos(Vector3.Dot(step, from)) * 180f / MathF.PI;
        Assert.InRange(turned, 3.9f, 4.01f);
        // Over enough time it arrives, and never overshoots.
        var v = from;
        for (int i = 0; i < 300; i++) v = CameraFraming.TurnToward(v, to, 1 / 60f, 1f, 40);
        Assert.True(Vector3.Dot(v, to) > 0.999f);
        // Opposite directions still turn (no NaN).
        var flip = CameraFraming.TurnToward(from, -from, 0.1f, 0.5f, 90);
        Assert.False(float.IsNaN(flip.X));
        Assert.True(Vector3.Dot(flip, from) < 1f);
    }

    [Fact]
    public void AutoSidePutsTheCameraAwayFromTheSideways()
    {
        var axis = new Vector3(0, 0, 1);
        Assert.Equal(-1f, CameraFraming.AutoSide(axis, new Vector3(50, 0, 200)));   // drifting right: camera left
        Assert.Equal(1f, CameraFraming.AutoSide(axis, new Vector3(-50, 0, 200)));
    }
}
