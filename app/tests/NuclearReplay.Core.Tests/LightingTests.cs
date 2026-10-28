#nullable disable
using System.Numerics;
using NuclearReplay.Core.Shots;
using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

// replay/src/ShotLighting.cs (lighting settings and the viewer's Lighting panel), replay/src/BacklightMath.cs (sun-line
// geometry) and the shot list checks for both (app/src/NuclearReplay.Core/Shots/ShotLightingFields.cs).
public class LightingTests
{
    private static bool Near(float a, float b, float tol = 0.05f) => MathF.Abs(BacklightMath.DeltaAngle(a, b)) <= tol;

    private static float Angle(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * 180f / MathF.PI;

    private static ShotLighting S(params (string key, object value)[] pairs)
    {
        var s = new ShotLighting();
        foreach (var (k, v) in pairs) s.Set(k, v is string ? null : Convert.ToDouble(v), v as string);
        return s;
    }

    // ---------------- settings ----------------

    [Fact]
    public void NoKeyMeansNothingToApply()
    {
        Assert.False(new ShotLighting().Any);
        Assert.Null(ShotLighting.Merge(null, null));
        Assert.Null(ShotLighting.Merge(new ShotLighting(), new ShotLighting()));
        Assert.DoesNotContain("backlight", ShotLighting.Keys);
    }

    [Fact]
    public void ValuesAreNormalized()
    {
        Assert.Equal(1f, S(("time_of_day", 25)).TimeOfDay);
        Assert.Equal(270f, S(("sun_azimuth", -90)).SunAzimuth);
        Assert.Equal(0f, S(("fog", -1)).Fog);
        Assert.Equal(1f, S(("weather", 3)).Weather);
        Assert.Equal(2500f, S(("cloud_height", 2500)).CloudHeight);
        var s = S(("time_of_day", 18.25), ("fog", 0.5));
        Assert.True(s.TimeOfDay == 18.25f && s.Fog == 0.5f && !s.SunAzimuth.HasValue && !s.SunCamera);
    }

    [Fact]
    public void WrongKindsAndUnknownKeysAreRefused()
    {
        var s = new ShotLighting();
        Assert.False(s.Set("time_of_day", null, "dusk"));
        Assert.False(s.Set("sun_azimuth", null, "north"));
        Assert.False(s.Set("backlight", 1, null));
        Assert.False(s.Any);
    }

    [Fact]
    public void CameraLockedSun()
    {
        var cam = S(("sun_azimuth", "Camera"), ("sun_camera_off", -20));
        Assert.True(cam.SunCamera && !cam.SunAzimuth.HasValue && cam.SunCameraOff == -20f);
        Assert.True(cam.Same(S(("sun_azimuth", "camera"), ("sun_camera_off", -20))));
        Assert.False(cam.Same(S(("sun_azimuth", "camera"))));
        Assert.False(cam.Same(null));
        // A number after "camera" turns the lock off.
        cam.Set("sun_azimuth", 100, null);
        Assert.True(!cam.SunCamera && cam.SunAzimuth == 100f);
    }

    [Fact]
    public void ShotWinsKeyByKeyOverTheDefaults()
    {
        var root = S(("time_of_day", 6.5), ("fog", 0.7), ("sun_azimuth", "camera"));
        var merged = ShotLighting.Merge(S(("fog", 1.2), ("sun_azimuth", 40)), root)!;
        Assert.Equal(6.5f, merged.TimeOfDay);
        Assert.Equal(1.2f, merged.Fog);
        Assert.True(!merged.SunCamera && merged.SunAzimuth == 40f);
        var plain = ShotLighting.Merge(null, root)!;
        Assert.True(plain.TimeOfDay == 6.5f && plain.Fog == 0.7f && plain.SunCamera);
        // Merge never changes its inputs.
        Assert.True(root.SunCamera && !root.SunAzimuth.HasValue);
    }

    [Fact]
    public void PairsRoundTrip()
    {
        var s = S(("time_of_day", 17.3), ("sun_azimuth", "camera"), ("sun_camera_off", -10), ("fog", 2), ("weather", 0.7), ("cloud_height", 1500));
        var back = new ShotLighting();
        foreach (var p in s.Pairs()) Assert.True(back.Set(p.Key, p.Value is string ? null : Convert.ToDouble(p.Value), p.Value as string));
        Assert.True(back.Same(s));
        Assert.Equal(new[] { "time_of_day", "sun_azimuth", "sun_camera_off", "fog", "weather", "cloud_height" }, s.Pairs().Select(p => p.Key));
        // The default camera offset is left out.
        Assert.DoesNotContain(S(("sun_azimuth", "camera")).Pairs(), p => p.Key == "sun_camera_off");
        Assert.Empty(new ShotLighting().Pairs());
    }

    [Fact]
    public void ValidatorKnowsTheSameKeysAsThePlayer() => Assert.Equal(ShotLighting.Keys, ShotLightingFields.Lighting);

    // ---------------- the Lighting panel ----------------

    [Fact]
    public void TimePresetsStepAndWrap()
    {
        Assert.Equal(new[] { "As recorded", "Dawn", "Golden hour", "Dusk", "Night" }, LightingPanel.TimeNames);
        float? t = null;
        var seen = new List<float?>();
        for (int i = 0; i < LightingPanel.TimeHours.Length; i++) { t = LightingPanel.Next(LightingPanel.TimeHours, t); seen.Add(t); }
        Assert.Equal(new float?[] { 6.5f, 17.3f, 17.8f, 22f, null }, seen);
        // A hand-set time steps to the first preset.
        Assert.Equal(6.5f, LightingPanel.Next(LightingPanel.TimeHours, 12.1f));
        Assert.Equal(new float?[] { null, 0f, 0.35f, 0.7f, 0.95f }, LightingPanel.WeatherValues);
        Assert.Equal(new float?[] { null, 0.25f, 0.5f, 2f, 4f }, LightingPanel.HazeValues);
    }

    [Fact]
    public void TimeStepsFromTheRecordedTime()
    {
        Assert.Equal(10.25f, LightingPanel.StepTime(null, 10f, 1));
        Assert.Equal(23.75f, LightingPanel.StepTime(0f, 10f, -1));
        Assert.Equal("17:18", LightingPanel.Clock(17.3f));
        Assert.Equal("06:30", LightingPanel.Clock(6.5f));
        Assert.Equal("00:00", LightingPanel.Clock(23.9999f));
    }

    [Fact]
    public void PanelTextNamesEverySetting()
    {
        string asRecorded = LightingPanel.Describe(null, 10.5f, false);
        Assert.Contains("Time  <b>As recorded  (recorded 10:30)</b>", asRecorded);
        Assert.Contains("Haze  <b>As recorded</b>", asRecorded);
        Assert.Contains("Weather  <b>As recorded</b>", asRecorded);
        Assert.Contains("Sun  the map's own path", asRecorded);
        Assert.DoesNotContain("shot list", asRecorded);

        string set = LightingPanel.Describe(S(("time_of_day", 17.3), ("fog", 2), ("weather", 0.7), ("sun_azimuth", 200)), float.NaN, true);
        Assert.Contains("Golden hour 17:18", set);
        Assert.Contains("Hazy (x2)", set);
        Assert.Contains("Overcast (0.7)", set);
        Assert.Contains("turned to 200 deg", set);
        Assert.Contains("The shot list sets lighting too", set);
        Assert.Contains("Set by hand 12:06", LightingPanel.Describe(S(("time_of_day", 12.1)), float.NaN, false));

        Assert.Equal("Lighting: dusk (17:48)", LightingPanel.TimeToast(17.8f));
        Assert.Equal("Lighting: time as recorded", LightingPanel.TimeToast(null));
        Assert.Equal("Lighting: thick haze (x4)", LightingPanel.HazeToast(4f));
        Assert.Equal("Lighting: weather storm", LightingPanel.WeatherToast(0.95f));
    }

    // ---------------- geometry ----------------

    [Fact]
    public void AzimuthConventionAndRoundTrip()
    {
        Assert.True(Near(BacklightMath.Azimuth(new Vector3(0, 0, 1)), 0f));
        Assert.True(Near(BacklightMath.Azimuth(new Vector3(1, 0, 0)), 90f));
        Assert.True(Near(BacklightMath.Azimuth(new Vector3(-1, 0, 0)), 270f));
        for (float az = 0; az < 360; az += 37)
            for (float el = -10; el <= 80; el += 15)
            {
                var v = BacklightMath.FromAngles(az, el);
                Assert.True(MathF.Abs(v.Length() - 1f) < 1e-4f && Near(BacklightMath.Azimuth(v), az) && MathF.Abs(BacklightMath.Elevation(v) - el) < 0.05f, $"round trip {az} {el}");
            }
        Assert.Equal(-20f, BacklightMath.DeltaAngle(10f, 350f), 3);
        Assert.Equal(20f, BacklightMath.DeltaAngle(350f, 10f), 3);
    }

    [Fact]
    public void ViewDirectionPutsTheSunWhereAsked()
    {
        var sun = BacklightMath.FromAngles(120f, 20f);
        Assert.True(Angle(BacklightMath.ViewDir(sun, 0f, false, 0f), sun) < 0.05f, "zero offset looks at the sun");
        foreach (float el in new[] { 5f, 20f, 45f })
        {
            sun = BacklightMath.FromAngles(200f, el);
            var view = BacklightMath.ViewDir(sun, 15f, false, 0f);
            float sep = Angle(view, sun);
            Assert.True(MathF.Abs(sep - 15f) < 2.5f, $"separation {sep:F2} at elevation {el}");
            // Screen right of a camera looking along `view` (Unity: left-handed, y up; the cross product is the same formula).
            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, view));
            Assert.True(Vector3.Dot(sun, right) > 0f, $"sun on the right at elevation {el}");
            var leftView = BacklightMath.ViewDir(sun, 15f, true, 0f);
            Assert.True(Vector3.Dot(sun, Vector3.Normalize(Vector3.Cross(Vector3.UnitY, leftView))) < 0f, $"sun on the left at elevation {el}");
        }
        sun = BacklightMath.FromAngles(90f, 10f);
        Assert.True(MathF.Abs(BacklightMath.Elevation(BacklightMath.ViewDir(sun, 0f, false, 6f)) - 4f) < 0.05f, "pitch lowers the view");
    }

    [Fact]
    public void SunBehindSubjectPose()
    {
        var subject = new Vector3(1000, 500, -200);
        var sun = BacklightMath.FromAngles(250f, 8f);
        var p = BacklightMath.BehindSubject(subject, sun, 15f, false, 120f, 0f, 5f, out float yaw, out float pitch);
        Assert.Equal(120f, Vector3.Distance(p, subject), 1);
        // The camera looks at the subject, toward the sun, with the sun about 15 degrees to the right.
        var look = subject - p;
        Assert.True(Near(BacklightMath.Azimuth(look), yaw) && MathF.Abs(BacklightMath.Elevation(look) - pitch) < 0.05f);
        Assert.True(MathF.Abs(Angle(look, sun) - 15f) < 2.5f);
        Assert.True(Vector3.Dot(sun, Vector3.Cross(Vector3.UnitY, look)) > 0f);
        // Never under the ground: a subject near the ground with a high sun.
        var low = BacklightMath.BehindSubject(new Vector3(0, 20, 0), BacklightMath.FromAngles(0f, 60f), 15f, false, 200f, 10f, 5f, out _, out float up);
        Assert.Equal(15f, low.Y, 3);
        Assert.True(up > 0f, "looks up at the subject");
    }

    [Fact]
    public void GroundSpotOnTheSunLine()
    {
        float t;
        Assert.True(BacklightMath.GroundOnLine(d => 0f, 300f, 10f, 0f, 60f, 8000f, out t) && MathF.Abs(t - 1701.4f) < 1f, "flat ground");
        Assert.True(BacklightMath.GroundOnLine(d => 0f, 300f, 10f, 2f, 60f, 8000f, out t) && MathF.Abs(t - 1690.1f) < 1f, "camera height shortens it");
        Assert.True(!BacklightMath.GroundOnLine(d => 0f, 300f, 0.3f, 2f, 60f, 8000f, out t) && t == 8000f, "sun on the horizon");
        Assert.False(BacklightMath.GroundOnLine(d => 0f, 3000f, 5f, 2f, 60f, 8000f, out _), "subject too high for the maximum");
        Assert.True(BacklightMath.GroundOnLine(d => 0f, 5f, 60f, 0f, 60f, 8000f, out t) && t == 60f, "subject low: minimum distance");
        // A 200 m ridge 1000-1200 m out: the line (124 m up at 1000 m) meets the ridge face first.
        Assert.True(BacklightMath.GroundOnLine(d => d >= 1000f && d <= 1200f ? 200f : 0f, 300f, 10f, 0f, 60f, 8000f, out t) && t >= 1000f && t <= 1001f, "ridge face");
        float expected = 300f / (MathF.Tan(10f * MathF.PI / 180f) - 0.05f);
        Assert.True(BacklightMath.GroundOnLine(d => -0.05f * d, 300f, 10f, 0f, 60f, 8000f, out t) && MathF.Abs(t - expected) < 2f, "falling ground");
    }

    // ---------------- shot list checks ----------------

    [Fact]
    public void LightingShotListIsValidWithoutWarnings()
    {
        var result = ShotValidator.Validate("""
            {"lighting": {"time_of_day": 17.3, "fog": 0.7},
             "shots":[
              {"t":0, "camera":"chase_cine", "target":"host", "backlight":true, "backlight_off":12, "backlight_side":"left", "backlight_pitch":3},
              {"t":4, "camera":"orbit", "target":"host", "time_of_day":17.8, "weather":0.7, "cloud_height":1500, "sun_azimuth":"camera", "sun_camera_off":-15},
              {"t":8, "camera":"chase", "target":"host", "sun_azimuth":250}
             ]}
            """);
        Assert.True(result.Valid);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","time_of_day":25}]}""", "$.shots[0].time_of_day")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","time_of_day":"dusk"}]}""", "$.shots[0].time_of_day")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","sun_azimuth":"north"}]}""", "$.shots[0].sun_azimuth")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","sun_azimuth":400}]}""", "$.shots[0].sun_azimuth")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","fog":-1}]}""", "$.shots[0].fog")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","weather":1.5}]}""", "$.shots[0].weather")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","cloud_height":-5}]}""", "$.shots[0].cloud_height")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase_cine","target":"host","backlight":"yes"}]}""", "$.shots[0].backlight")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase_cine","target":"host","backlight":true,"backlight_side":"up"}]}""", "$.shots[0].backlight_side")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase_cine","target":"host","backlight":true,"backlight_off":120}]}""", "$.shots[0].backlight_off")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase_cine","target":"host","backlight":true,"backlight_min":500,"backlight_max":100}]}""", "$.shots[0].backlight_max")]
    [InlineData("""{"lighting":5,"shots":[]}""", "$.lighting")]
    [InlineData("""{"lighting":{"time_of_day":-1},"shots":[]}""", "$.lighting.time_of_day")]
    public void ReportsLightingErrors(string json, string path)
    {
        var result = ShotValidator.Validate(json);
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, i => i.IsError && i.Path == path);
    }

    [Theory]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","backlight":true}]}""", "$.shots[0].backlight")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase_cine","target":"host","backlight_off":10}]}""", "$.shots[0].backlight")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","sun_azimuth":90,"sun_camera_off":10}]}""", "$.shots[0].sun_camera_off")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","fog":40}]}""", "$.shots[0].fog")]
    [InlineData("""{"lighting":{"time_of_day":6.5,"backlight":true},"shots":[]}""", "$.lighting.backlight")]
    public void WarnsAboutLightingThatDoesNothing(string json, string path)
    {
        var result = ShotValidator.Validate(json);
        Assert.True(result.Valid);
        Assert.Contains(result.Issues, i => i.Severity == "warning" && i.Path == path);
    }

    [Fact]
    public void SchemaDescribesEveryLightingField()
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse(NcEnvironment.ShotListSchema)!;
        var shot = schema["$defs"]!["shot"]!["properties"]!.AsObject();
        var lighting = schema["$defs"]!["lighting"]!["properties"]!.AsObject();
        foreach (var k in ShotLightingFields.Lighting) { Assert.True(shot.ContainsKey(k), k); Assert.True(lighting.ContainsKey(k), k); }
        foreach (var k in ShotLightingFields.Backlight) Assert.True(shot.ContainsKey(k), k);
        Assert.NotNull(schema["properties"]!["lighting"]);
    }

    [Fact]
    public void PreviewCarriesTheRootLighting()
    {
        var msg = Preview.PreviewCommands.Shots("""{"lighting":{"time_of_day":6.5},"shots":[{"t":0,"camera":"orbit","target":"host"}]}""");
        Assert.Equal(6.5, (double)msg["lighting"]!["time_of_day"]!);
        Assert.False(Preview.PreviewCommands.Shots("""{"shots":[]}""").ContainsKey("lighting"));
    }
}
