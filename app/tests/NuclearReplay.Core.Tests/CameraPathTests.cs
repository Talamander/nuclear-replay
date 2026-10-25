using System.Numerics;
using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

// replay/src/CameraPath.cs and CameraShake.cs: camera path and shake maths for the path cine camera.
public class CameraPathTests
{
    private static CameraPath.Key K(double t, float x, float y, float z, float yaw = 0, float pitch = 0, float fov = 50) =>
        new CameraPath.Key { T = t, Pos = new Vector3(x, y, z), Rot = CameraPath.Euler(pitch, yaw, 0), Fov = fov };

    private static Vector3 Pos(CameraPath p, double t) { p.Evaluate(t, out var pos, out _, out _); return pos; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HitsEveryKeyAtItsTimeWithKeyTiming(bool unevenSpacing)
    {
        var keys = new[]
        {
            K(0, 0, 10, 0, yaw: 0, fov: 60),
            K(2, 100, 20, 0, yaw: 90, fov: 40),
            K(unevenSpacing ? 3 : 4, 120, 20, 80, yaw: 180, fov: 30),
            K(7, 0, 50, 100, yaw: 270, fov: 70),
        };
        var path = new CameraPath(keys, keyTiming: true, ease: 0.5);
        foreach (var k in keys)
        {
            path.Evaluate(k.T, out var pos, out var rot, out var fov);
            Assert.True(Vector3.Distance(pos, k.Pos) < 0.05f, $"key at {k.T}: {pos} vs {k.Pos}");
            Assert.True(CameraPath.Angle(rot, k.Rot) < 0.01f, $"key at {k.T}: rotation off by {CameraPath.Angle(rot, k.Rot)} rad");
            Assert.InRange(fov, k.Fov - 0.05f, k.Fov + 0.05f);
        }
    }

    [Fact]
    public void EvenTimingWithoutEaseMovesAtConstantSpeed()
    {
        // A wiggly path with very uneven key spacing; same rotation and lens, so progress is distance.
        var keys = new[] { K(0, 0, 0, 0), K(1, 10, 0, 0), K(2, 200, 0, 50), K(3, 260, 30, 400), K(4, 300, 30, 420) };
        var path = new CameraPath(keys, keyTiming: false, ease: 0);
        double steps = 200, dt = (path.End - path.Start) / steps;
        double expected = path.Length / steps;
        for (int i = 0; i < steps; i++)
        {
            double t = path.Start + i * dt;
            float step = Vector3.Distance(Pos(path, t), Pos(path, t + dt));
            Assert.InRange(step, expected * 0.97, expected * 1.03);
        }
    }

    [Fact]
    public void EvenTimingStartsAndEndsOnTheEndKeys()
    {
        var keys = new[] { K(10, 0, 0, 0), K(11, 10, 0, 0), K(30, 50, 0, 50) };
        var path = new CameraPath(keys, keyTiming: false, ease: 0.5);
        Assert.Equal(10, path.Start);
        Assert.Equal(30, path.End);
        Assert.True(Vector3.Distance(Pos(path, 10), keys[0].Pos) < 1e-3f);
        Assert.True(Vector3.Distance(Pos(path, 30), keys[2].Pos) < 1e-3f);
        // Holds outside the keys.
        Assert.True(Vector3.Distance(Pos(path, 5), keys[0].Pos) < 1e-3f);
        Assert.True(Vector3.Distance(Pos(path, 99), keys[2].Pos) < 1e-3f);
    }

    [Fact]
    public void PanOnTheSpotStillTakesTime()
    {
        var keys = new[] { K(0, 5, 5, 5, yaw: 0), K(4, 5, 5, 5, yaw: 90) };
        var path = new CameraPath(keys, keyTiming: false, ease: 0);
        path.Evaluate(2, out var pos, out var rot, out _);
        Assert.True(Vector3.Distance(pos, keys[0].Pos) < 1e-4f);
        float half = CameraPath.Angle(rot, keys[0].Rot);
        Assert.InRange(half, 0.75f, 0.82f);   // about 45 degrees
    }

    [Fact]
    public void SameKeysEverywhereFallsBackToTime()
    {
        var keys = new[] { K(0, 1, 2, 3), K(2, 1, 2, 3) };
        var path = new CameraPath(keys, keyTiming: false, ease: 0.5);
        Assert.Equal(0, path.Length, 6);
        Assert.Equal(0.5, path.ParamAt(1), 6);
        Assert.True(Vector3.Distance(Pos(path, 1), keys[0].Pos) < 1e-4f);
    }

    [Fact]
    public void SharpCornerDoesNotOvershoot()
    {
        // Two keys close together between two far keys: the uniform spline loops here, the centripetal one does not.
        var keys = new[] { K(0, 0, 0, 0), K(1, 100, 0, 0), K(2, 101, 0, 1), K(3, 101, 0, 100) };
        var path = new CameraPath(keys, keyTiming: true, ease: 0);
        foreach (var p in path.Polyline)
        {
            Assert.InRange(p.X, -1f, 102.5f);
            Assert.InRange(p.Z, -1.5f, 101f);
        }
    }

    [Fact]
    public void ZoomIsMonotoneBetweenKeys()
    {
        var keys = new[] { K(0, 0, 0, 0, fov: 70), K(1, 50, 0, 0, fov: 60), K(2, 100, 0, 0, fov: 20), K(3, 150, 0, 0, fov: 19) };
        var path = new CameraPath(keys, keyTiming: true, ease: 0.5);
        float last = float.MaxValue;
        for (double t = 0; t <= 3; t += 0.01)
        {
            path.Evaluate(t, out _, out _, out var fov);
            Assert.True(fov <= last + 1e-3f, $"fov rose at {t}: {last} -> {fov}");
            Assert.InRange(fov, 18.99f, 70.01f);
            last = fov;
        }
    }

    [Fact]
    public void KeyTimingNeverGoesBackward()
    {
        var keys = new[] { K(0, 0, 0, 0), K(0.5, 300, 0, 0), K(6, 310, 0, 0), K(6.4, 900, 0, 0) };
        var path = new CameraPath(keys, keyTiming: true, ease: 0.5);
        double last = -1;
        for (double t = 0; t <= 6.4; t += 0.005)
        {
            double u = path.ParamAt(t);
            Assert.True(u >= last - 1e-9, $"went back at {t}");
            last = u;
        }
    }

    [Fact]
    public void OneKeyIsAStillCamera()
    {
        var key = K(3, 7, 8, 9, yaw: 30, fov: 25);
        var path = new CameraPath(new[] { key }, keyTiming: false, ease: 0.5);
        path.Evaluate(100, out var pos, out var rot, out var fov);
        Assert.Equal(key.Pos, pos);
        Assert.True(CameraPath.Angle(rot, key.Rot) < 1e-4f);
        Assert.InRange(fov, 24.99f, 25.01f);
        Assert.Equal(Vector3.Zero, path.Direction(3));
    }

    [Fact]
    public void KeysAreSortedByTime()
    {
        var path = new CameraPath(new[] { K(5, 50, 0, 0), K(0, 0, 0, 0) }, keyTiming: true, ease: 0);
        Assert.Equal(0, path.Start);
        Assert.True(Vector3.Distance(Pos(path, 0), Vector3.Zero) < 1e-3f);
        Assert.True(Vector3.Dot(path.Direction(2.5), Vector3.UnitX) > 0.99f);
    }

    [Fact]
    public void EulerMatchesUnityConvention()
    {
        // Unity: yaw 90 turns forward (+z) to +x; pitch 90 turns forward to down (-y).
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, CameraPath.Euler(0, 90, 0)), Vector3.UnitX) < 1e-5f);
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, CameraPath.Euler(90, 0, 0)), -Vector3.UnitY) < 1e-5f);
        // Roll first, then pitch, then yaw: the camera's right axis after yaw 90 and roll 90 points down.
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitX, CameraPath.Euler(0, 90, 90)), Vector3.UnitY) < 1e-5f);
    }

    [Fact]
    public void ShakeOffIsZero()
    {
        CameraShake.Angles(12.3, 7, 0, 0, 0, 1, out var p, out var y, out var r);
        Assert.Equal(0f, p); Assert.Equal(0f, y); Assert.Equal(0f, r);
    }

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(2.2f, 0.3f, 1f)]
    public void ShakeIsBoundedAndMoves(float handheld, float rumble, float buffet)
    {
        float bound = CameraShake.Bound(handheld, rumble, buffet) * 1.01f;
        float min = float.MaxValue, max = float.MinValue;
        for (double t = 0; t < 60; t += 0.013)
        {
            CameraShake.Angles(t, 42, handheld, rumble, buffet, 1, out var p, out var y, out var r);
            Assert.InRange(p, -bound, bound);
            Assert.InRange(y, -bound, bound);
            Assert.InRange(r, -bound, bound);
            min = Math.Min(min, y); max = Math.Max(max, y);
        }
        Assert.True(max - min > bound * 0.2f, $"shake barely moves: {min}..{max}");
    }

    [Fact]
    public void ShakeIsDeterministicAndSeeded()
    {
        CameraShake.Angles(3.21, 5, 1, 1, 1, 1, out var p1, out var y1, out var r1);
        CameraShake.Angles(3.21, 5, 1, 1, 1, 1, out var p2, out var y2, out var r2);
        Assert.Equal((p1, y1, r1), (p2, y2, r2));
        CameraShake.Angles(3.21, 6, 1, 1, 1, 1, out var p3, out var y3, out var r3);
        Assert.NotEqual((p1, y1, r1), (p3, y3, r3));
    }

    [Fact]
    public void ShakeIsSmoothFrameToFrame()
    {
        // Handheld at 60 fps never jumps more than a few hundredths of a degree per frame.
        float lastYaw = 0;
        for (int f = 0; f < 3600; f++)
        {
            CameraShake.Angles(f / 60.0, 9, 1, 0, 0, 1, out _, out var y, out _);
            if (f > 0) Assert.True(Math.Abs(y - lastYaw) < 0.05f, $"jump at frame {f}");
            lastYaw = y;
        }
    }
}
