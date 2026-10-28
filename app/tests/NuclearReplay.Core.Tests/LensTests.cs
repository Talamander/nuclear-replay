using System.Numerics;
using NuclearReplay.Core.Shots;
using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

// replay/src/LensMath.cs (focal length, depth of field maths, the Lens panel's settings and text), the keyed focus of
// CameraPath, Camera Studio's lens text, and the shot-list validator's lens fields.
public class LensTests
{
    private const double Wide = 16.0 / 9.0;
    private static LensMath.Sensor S35 => LensMath.SensorOrDefault("super35");
    private static LensMath.Sensor FF => LensMath.SensorOrDefault("full_frame");

    [Theory]
    [InlineData("super35", 35, 22.6)]
    [InlineData("super35", 100, 8.0)]
    [InlineData("super35", 200, 4.0)]
    [InlineData("full_frame", 50, 22.9)]
    public void FocalLengthToVerticalFieldOfView(string sensor, double mm, double deg)
    {
        Assert.Equal(deg, LensMath.FovFromFocal(mm, LensMath.SensorOrDefault(sensor), Wide), 1);
    }

    [Fact]
    public void FrameIsTheWidestCropOfTheSensorAtTheScreenAspect()
    {
        Assert.Equal(14.0, LensMath.FrameHeight(S35, Wide), 1);          // width-limited at 16:9
        Assert.Equal(18.66, LensMath.FrameHeight(S35, 1.0), 2);          // height-limited when square
        Assert.Equal(20.25, LensMath.FrameHeight(FF, Wide), 2);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(35)]
    [InlineData(135)]
    [InlineData(400)]
    public void FocalAndFovRoundTrip(double mm)
    {
        double fov = LensMath.FovFromFocal(mm, S35, Wide);
        Assert.Equal(mm, LensMath.FocalFromFov(fov, S35, Wide), 3);
    }

    [Fact]
    public void FovIsClampedToWhatTheCamerasAccept()
    {
        Assert.Equal(LensMath.MinFov, LensMath.FovFromFocal(5000, S35, Wide), 6);
        Assert.True(LensMath.FovFromFocal(1, S35, Wide) <= LensMath.MaxFov);
    }

    [Fact]
    public void SharpRangeShrinksWithALongerLensAndWiderAperture()
    {
        LensMath.SharpRange(35, 8, 50, 0.025, out double n1, out double f1);
        LensMath.SharpRange(200, 2, 50, 0.025, out double n2, out double f2);
        Assert.True(n1 < 50 && f1 > 50);
        Assert.True(n2 < 50 && f2 > 50);
        Assert.True(f2 - n2 < (f1 - n1) / 10, $"{n2}-{f2} vs {n1}-{f1}");
    }

    [Fact]
    public void SharpRangeIsInfiniteBeyondTheHyperfocalDistance()
    {
        LensMath.SharpRange(24, 11, 1000, 0.025, out _, out double far);
        Assert.True(double.IsPositiveInfinity(far) || far > 1e6);
    }

    [Fact]
    public void UrpBokehMatchesTheRealBlurShareOfTheFrame()
    {
        // 100 mm f/2.8 focused at 60 m on Super 35, 720 lines: URP's blur of a point at infinity, as a share of the
        // frame height, equals the real lens's.
        double focal = 100, n = 2.8, focus = 60, height = 720, frame = LensMath.FrameHeight(S35, Wide);
        LensMath.UrpBokeh(focal, n, focus, frame, height, out double uf, out double ua);
        double realShare = focal / n * (focal / 1000) / (focus - focal / 1000) / 2 / frame;
        double urpShare = uf / ua * (uf / 1000) / (focus - uf / 1000) * (14 / height);
        Assert.Equal(realShare, urpShare, 6);
        Assert.InRange(ua, 1, 32);
        Assert.InRange(uf, 1, 300);
    }

    [Fact]
    public void UrpBokehUsesALongerUrpLensPastFOne()
    {
        // 200 mm at f/1.4 at 1080 lines wants an aperture under f/1: URP's focal length grows instead.
        LensMath.UrpBokeh(200, 1.4, 120, LensMath.FrameHeight(S35, Wide), 1080, out double uf, out double ua);
        Assert.Equal(1, ua, 6);
        Assert.True(uf > 200 && uf <= 300, uf.ToString());
    }

    [Fact]
    public void StepsWalkTheListsAndClampAtTheEnds()
    {
        Assert.Equal(50, LensMath.Step(LensMath.FocalSteps, 40, 1));
        Assert.Equal(40, LensMath.Step(LensMath.FocalSteps, 45, -1));
        Assert.Equal(600, LensMath.Step(LensMath.FocalSteps, 600, 1));
        Assert.Equal(4, LensMath.Step(LensMath.FStops, 2.8, 1));
    }

    [Fact]
    public void FocusBlendsInDioptres()
    {
        // Half way from 10 m to infinity-ish (10 km) in dioptres is about 20 m, not 5 km.
        Assert.Equal(20, LensMath.FocusBlend(10, 10000, 0.5), 0);
        Assert.Equal(10, LensMath.FocusBlend(10, 50, 0), 6);
        Assert.Equal(50, LensMath.FocusBlend(10, 50, 1), 6);
    }

    [Fact]
    public void ShotWithoutLensFieldsHasNoLens()
    {
        Assert.Null(LensSettings.FromShot(new Dictionary<string, object> { ["t"] = 1.0, ["camera"] = "chase_cine", ["fov"] = 30.0 }));
    }

    [Fact]
    public void ShotLensFieldsParse()
    {
        var shot = new Dictionary<string, object>
        {
            ["focal_mm"] = 135.0, ["sensor"] = "full_frame",
            ["dof"] = new Dictionary<string, object> { ["f_stop"] = 2.0, ["focus"] = 80.0 },
            ["vignette"] = 0.3, ["grain"] = true, ["chromatic"] = 2.0, ["motion_blur"] = 0.0,
        };
        var lens = LensSettings.FromShot(shot)!;
        Assert.Equal(135, lens.FocalMm);
        Assert.Equal("full_frame", lens.Sensor);
        Assert.True(lens.Dof);
        Assert.Equal(2, lens.FStop);
        Assert.False(lens.FocusTarget);
        Assert.Equal(80, lens.FocusM);
        Assert.Equal(0.3, lens.Vignette);
        Assert.Equal(0.5, lens.Grain);
        Assert.Equal(1, lens.Chromatic);
        Assert.Equal(0, lens.MotionBlur);
    }

    [Fact]
    public void DofTrueFocusesOnTheTarget()
    {
        var lens = LensSettings.FromShot(new Dictionary<string, object> { ["dof"] = true })!;
        Assert.True(lens.Dof);
        Assert.True(lens.FocusTarget);
        Assert.Equal(2.8, lens.FStop);
        var off = LensSettings.FromShot(new Dictionary<string, object> { ["dof"] = new Dictionary<string, object> { ["enabled"] = false } })!;
        Assert.False(off.Dof);
    }

    [Fact]
    public void ToShotRoundTrips()
    {
        var a = new LensSettings { FocalMm = 85, Sensor = "full_frame", Dof = true, FStop = 4, FocusTarget = false, FocusM = 30, Grain = 0.2, MotionBlur = 0.4 };
        var shot = a.ToShot();
        Assert.Equal(new[] { "focal_mm", "sensor", "dof", "grain", "motion_blur" }, shot.Keys.ToArray());
        Assert.True(a.SameAs(LensSettings.FromShot(shot)));
        Assert.Empty(new LensSettings().ToShot());
    }

    [Fact]
    public void PanelAdjustsAndResetsRows()
    {
        var lens = new LensSettings();
        Assert.Equal("Focal length 50 mm", lens.Adjust(LensSettings.Row.Focal, 1, false, 42, 0));
        Assert.Equal("Focal length 40 mm", lens.Adjust(LensSettings.Row.Focal, -1, false, 0, 0));
        Assert.Equal("Sensor Full frame", lens.Adjust(LensSettings.Row.Sensor, 1, false, 0, 0));
        // Leaving autofocus starts from the live distance and turns depth of field on.
        Assert.Equal("Focus 150 m", lens.Adjust(LensSettings.Row.Focus, 1, false, 0, 120));
        Assert.True(lens.Dof);
        Assert.False(lens.FocusTarget);
        Assert.Equal("Aperture f/2", lens.Adjust(LensSettings.Row.FStop, -1, false, 0, 0));
        Assert.Equal("Film grain 25%", lens.Adjust(LensSettings.Row.Grain, 1, true, 0, 0));
        Assert.Equal("Film grain off", lens.Reset(LensSettings.Row.Grain));
        Assert.Equal("Focus on the target", lens.Reset(LensSettings.Row.Focus));
        Assert.True(lens.FocusTarget);
        Assert.Equal("Focal length: the camera's own", lens.Reset(LensSettings.Row.Focal));
        Assert.Equal(0, lens.FocalMm);
    }

    [Fact]
    public void PanelTextMarksTheSelectedRowAndSaysWhatIsSharp()
    {
        var lens = new LensSettings { FocalMm = 200, Dof = true, FStop = 2.8, Vignette = 0.3 };
        string text = lens.Describe(LensSettings.Row.FStop, 0, 150);
        Assert.Contains("> <b>Aperture: f/2.8</b>", text);
        Assert.Contains("   Focal length: 200 mm", text);
        Assert.Contains("Focus: on the target (150 m)", text);
        Assert.Contains("Vignette: 30%", text);
        Assert.Contains("Sharp from", text);
        Assert.Equal("200 mm, Super 35, depth of field f/2.8 on the target, vignette 30%", lens.Summary());
        Assert.Equal("Super 35, no effects", new LensSettings().Summary());
        Assert.Contains("Focal length: camera's own (35 mm)", new LensSettings().Describe(LensSettings.Row.Focal, 35, 0));
    }

    [Fact]
    public void PathKeysPullFocus()
    {
        var keys = new[]
        {
            new CameraPath.Key { T = 0, Pos = new Vector3(0, 0, 0), Focus = 10, FStop = 2 },
            new CameraPath.Key { T = 4, Pos = new Vector3(100, 0, 0), Focus = -1, FStop = 0 },
        };
        var path = new CameraPath(keys, keyTiming: true, ease: 0.5);
        Assert.True(path.HasLensKeys);
        path.Lens(0, 200, 5.6, out double f0, out double n0);
        Assert.Equal(10, f0, 3);
        Assert.Equal(2, n0, 3);
        path.Lens(4, 200, 5.6, out double f1, out double n1);
        Assert.Equal(200, f1, 3);       // the second key focuses on the target
        Assert.Equal(5.6, n1, 3);       // and has the shot's aperture
        path.Lens(2, 200, 5.6, out double fm, out _);
        Assert.InRange(fm, 10, 200);
        Assert.False(new CameraPath(new[] { new CameraPath.Key { T = 0 }, new CameraPath.Key { T = 1, Pos = new Vector3(5, 0, 0) } }, true, 0.5).HasLensKeys);
    }

    [Fact]
    public void StudioShowsKeyLensInMillimetres()
    {
        var s = new CameraStudio();
        float fov = (float)LensMath.FovFromFocal(85, S35, Wide);
        s.Add(new CameraStudio.Key { World = new Vector3(0, 100, 0), Fov = fov, Focus = 40, FStop = 2.8f }, 10);
        string text = s.Describe();
        Assert.Contains("lens 85 mm, focus 40 m at f/2.8", text);
        Assert.Contains("Lens Super 35, no effects (F3 sets it; K or U keeps it)", text);
        var keys = s.ShotKeys();
        Assert.Equal(40, keys[0].Focus);
        Assert.Equal(2.8f, keys[0].FStop);
    }

    [Fact]
    public void ValidatorAcceptsLensFields()
    {
        var result = ShotValidator.Validate("""
        {"shots":[{"t":1,"camera":"chase_cine","target":"host","focal_mm":135,"sensor":"full_frame",
                   "dof":{"f_stop":2.8,"focus":"target"},"vignette":0.3,"grain":0.2,"chromatic":0.1,"motion_blur":true}]}
        """);
        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(i => i.Path + " " + i.Message)));
        Assert.DoesNotContain(result.Issues, i => i.Path.Contains("focal_mm") || i.Path.Contains("dof") || i.Path.Contains("grain"));
    }

    [Fact]
    public void ValidatorRejectsBadLensFields()
    {
        var result = ShotValidator.Validate("""
        {"shots":[{"t":1,"camera":"chase_cine","target":"host","fov":30,"focal_mm":-5,"sensor":"imax",
                   "dof":{"f_stop":"wide","focus":"near","bokeh":1},"vignette":2,"grain":"lots"}]}
        """);
        Assert.False(result.Valid);
        string[] errors = result.Issues.Where(i => i.Severity == "error").Select(i => i.Path).ToArray();
        Assert.Contains("$.shots[0].focal_mm", errors);
        Assert.Contains("$.shots[0].sensor", errors);
        Assert.Contains("$.shots[0].dof.f_stop", errors);
        Assert.Contains("$.shots[0].dof.focus", errors);
        Assert.Contains("$.shots[0].vignette", errors);
        Assert.Contains("$.shots[0].grain", errors);
        string[] warnings = result.Issues.Where(i => i.Severity == "warning").Select(i => i.Path).ToArray();
        Assert.Contains("$.shots[0].fov", warnings);
        Assert.Contains("$.shots[0].dof.bokeh", warnings);
    }

    [Fact]
    public void ValidatorChecksKeyedFocus()
    {
        var result = ShotValidator.Validate("""
        {"shots":[{"t":1,"camera":"chase_cine","target":"host",
                   "keys":[{"t":0,"pos":[0,0,0],"focal_mm":50,"focus":20,"f_stop":2},{"t":2,"pos":[1,0,0],"focus":"far"}]}]}
        """);
        Assert.Contains(result.Issues, i => i.Path == "$.shots[0].keys[1].focus" && i.Severity == "error");
        Assert.Contains(result.Issues, i => i.Path == "$.shots[0].keys[0].focus" && i.Severity == "warning");   // no dof on the shot
    }

    [Fact]
    public void ValidatorLeavesOldShotListsAlone()
    {
        var result = ShotValidator.Validate("""{"shots":[{"t":1,"camera":"chase","target":"host","fov":40}]}""");
        Assert.Empty(result.Issues);
    }
}
