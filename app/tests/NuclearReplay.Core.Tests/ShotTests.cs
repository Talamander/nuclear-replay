using System.Text.Json.Nodes;
using NuclearReplay.Core.Demos;
using NuclearReplay.Core.Shots;

namespace NuclearReplay.Core.Tests;

public class ShotTests
{
    [Fact]
    public void DemoMdExampleIsValid()
    {
        var result = ShotValidator.Validate("""
            {"shots":[
             {"t":0,  "camera":"orbit",   "target":"host", "fov":55},
             {"t":12, "camera":"cockpit", "target":"host"},
             {"t":20, "camera":"chase",   "target":"auto", "hold":3}
            ]}
            """);
        Assert.True(result.Valid);
        Assert.Empty(result.Issues);
        Assert.Equal(3, result.ShotList!.Shots.Count);
        Assert.Equal(55, result.ShotList.Shots[0].Fov);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("auto")]
    [InlineData("latest_missile")]
    [InlineData("latest_host_missile")]
    [InlineData("unit:169")]
    [InlineData("name:Revoker")]
    [InlineData("type:destroyer")]
    [InlineData("type:FS-12")]
    public void AcceptsAllDirectorTargets(string target) => Assert.True(ShotValidator.IsValidTarget(target));

    [Theory]
    [InlineData("unit:")]
    [InlineData("unit:abc")]
    [InlineData("name:")]
    [InlineData("Host")]
    [InlineData("missile")]
    [InlineData("type:")]
    [InlineData("type: ")]
    public void RejectsUnknownTargets(string target) => Assert.False(ShotValidator.IsValidTarget(target));

    [Theory]
    [InlineData("""{"shots":[{"t":-1,"camera":"orbit","target":"host"}]}""", "$.shots[0].t")]
    [InlineData("""{"shots":[{"t":"5","camera":"orbit","target":"host"}]}""", "$.shots[0].t")]
    [InlineData("""{"shots":[{"camera":"orbit","target":"host"}]}""", "$.shots[0].t")]
    [InlineData("""{"shots":[{"t":1,"camera":"drone","target":"host"}]}""", "$.shots[0].camera")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"me"}]}""", "$.shots[0].target")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","fov":0}]}""", "$.shots[0].fov")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","fov":200}]}""", "$.shots[0].fov")]
    [InlineData("""{"shots":[{"t":1,"camera":"orbit","target":"host","hold":-2}]}""", "$.shots[0].hold")]
    [InlineData("""{"shots":[5]}""", "$.shots[0]")]
    [InlineData("""{"shot":[]}""", "$.shots")]
    [InlineData("""[1,2]""", "$")]
    [InlineData("""{"shots":[""", "$")]
    public void ReportsErrorsWithJsonPath(string json, string path)
    {
        var result = ShotValidator.Validate(json);
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, i => i.IsError && i.Path == path);
    }

    [Fact]
    public void SchemaIdsPointAtTheirFilesInTheGitHubRepository()
    {
        foreach (var (file, text) in new[] { ("shotlist.schema.json", NcEnvironment.ShotListSchema), ("render-job.schema.json", NcEnvironment.RenderJobSchema) })
            Assert.Equal("https://raw.githubusercontent.com/Talamander/nuclear-replay/main/app/schemas/" + file,
                (string?)JsonNode.Parse(text)!["$id"]);
    }

    [Fact]
    public void WarnsAboutUnknownFieldsOrderAndLength()
    {
        var result = ShotValidator.Validate("""{"shots":[{"t":30,"camera":"orbit","target":"host","zoom":2},{"t":10,"camera":"tv","target":"auto"}],"note":"x"}""", demoDuration: 20);
        Assert.True(result.Valid);
        Assert.Contains(result.Issues, i => i.Severity == "warning" && i.Path == "$.shots[0].zoom");
        Assert.Contains(result.Issues, i => i.Severity == "warning" && i.Path == "$.note");
        Assert.Contains(result.Issues, i => i.Severity == "warning" && i.Path == "$.shots[0].t"); // after the end
        Assert.Contains(result.Issues, i => i.Severity == "warning" && i.Path == "$.shots[1].t"); // out of order
    }

    [Fact]
    public void SchemaMatchesValidator()
    {
        var schema = JsonNode.Parse(NcEnvironment.ShotListSchema)!;
        var shot = schema["$defs"]!["shot"]!;
        var cameras = shot["properties"]!["camera"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(ShotValidator.Cameras, cameras);
        var pattern = new System.Text.RegularExpressions.Regex(shot["properties"]!["target"]!["pattern"]!.GetValue<string>());
        foreach (var t in new[] { "host", "auto", "latest_missile", "latest_host_missile", "unit:1", "name:x", "type:ship" }) Assert.Matches(pattern, t);
        foreach (var t in new[] { "unit:x", "name:", "type:", "Host" }) Assert.DoesNotMatch(pattern, t);
        // The schema's own examples validate without warnings.
        foreach (var node in schema["examples"]!.AsArray())
        {
            var result = ShotValidator.Validate(node!.ToJsonString());
            Assert.True(result.Valid, string.Join("; ", result.Issues));
            Assert.Empty(result.Issues);
        }
        // Every camera option the validator knows is a shot property and a "then" property of the schema.
        var properties = shot["properties"]!.AsObject().Select(p => p.Key).ToHashSet();
        var thenProperties = schema["$defs"]!["then"]!["properties"]!.AsObject().Select(p => p.Key).ToHashSet();
        foreach (var camera in ShotValidator.Cameras)
        foreach (var field in ShotValidator.CameraFields(camera))
        {
            Assert.Contains(field, properties);
            Assert.Contains(field, thenProperties);
        }
    }

    [Fact]
    public void RideAndTwoShotOptionsValidate()
    {
        var result = ShotValidator.Validate("""
            {"shots":[
             {"t":0,  "camera":"ride", "target":"name:Revoker", "offset":[6,1.5,-2], "look_dir":[-0.8,-0.1,0.6], "roll":"level", "lag":0.2, "fov":50, "handheld":0.5},
             {"t":5,  "camera":"ride", "target":"host", "look_at":"type:destroyer", "roll":0.5},
             {"t":10, "camera":"two_shot", "target":"unit:169", "second":"unit:201", "side":"left", "distance":40, "distance_max":300,
                      "fov_min":25, "fov_max":60, "margin":0.2, "azimuth":-30, "elevation":-0.1,
                      "then":{"camera":"two_shot","target":"unit:201","side":"auto"}},
             {"t":20, "camera":"orbit", "target":"type:ship"}
            ]}
            """);
        Assert.True(result.Valid, string.Join("; ", result.Issues));
        Assert.Empty(result.Issues);
        Assert.Equal("unit:201", result.ShotList!.Shots[2].Second);
    }

    [Theory]
    [InlineData("""{"t":1,"camera":"ride","target":"host","offset":[1,2]}""", "$.shots[0].offset")]
    [InlineData("""{"t":1,"camera":"ride","target":"host","offset":"near"}""", "$.shots[0].offset")]
    [InlineData("""{"t":1,"camera":"ride","target":"host","look_dir":[0,0,0]}""", "$.shots[0].look_dir")]
    [InlineData("""{"t":1,"camera":"ride","target":"host","look_at":"me"}""", "$.shots[0].look_at")]
    [InlineData("""{"t":1,"camera":"ride","target":"host","roll":"bank"}""", "$.shots[0].roll")]
    [InlineData("""{"t":1,"camera":"ride","target":"host","roll":2}""", "$.shots[0].roll")]
    [InlineData("""{"t":1,"camera":"ride","target":"host","lag":-1}""", "$.shots[0].lag")]
    [InlineData("""{"t":1,"camera":"two_shot","target":"host","second":"unit:x"}""", "$.shots[0].second")]
    [InlineData("""{"t":1,"camera":"two_shot","target":"host","side":"above"}""", "$.shots[0].side")]
    [InlineData("""{"t":1,"camera":"two_shot","target":"host","fov_max":200}""", "$.shots[0].fov_max")]
    [InlineData("""{"t":1,"camera":"two_shot","target":"host","fov_min":70,"fov_max":40}""", "$.shots[0].fov_min")]
    [InlineData("""{"t":1,"camera":"two_shot","target":"host","distance":"far"}""", "$.shots[0].distance")]
    [InlineData("""{"t":1,"camera":"orbit","target":"host","then":{"camera":"two_shot","target":"host","side":"up"}}""", "$.shots[0].then.side")]
    public void RejectsBadRideAndTwoShotOptions(string shot, string path)
    {
        var result = ShotValidator.Validate("{\"shots\":[" + shot + "]}");
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, i => i.IsError && i.Path == path);
    }

    [Fact]
    public void WarnsAboutCameraOptionsOnTheWrongCamera()
    {
        var result = ShotValidator.Validate("""{"shots":[{"t":1,"camera":"orbit","target":"host","second":"unit:3"},{"t":2,"camera":"ride","target":"host","side":"left"}]}""");
        Assert.True(result.Valid);
        Assert.Contains(result.Issues, i => i.Severity == "warning" && i.Path == "$.shots[0].second" && i.Message.Contains("two_shot"));
        Assert.Contains(result.Issues, i => i.Severity == "warning" && i.Path == "$.shots[1].side" && i.Message.Contains("two_shot"));
    }

    [Fact]
    public void TwoShotPresetFramesShooterAndTarget()
    {
        var launch = Event("""{"event":"launch","shooter":{"id":169,"name":"Revoker"},"target":{"id":201},"weapon":"AAM-29","missile":4021}""", 100);
        var kill = Event("""{"event":"kill","victim":{"id":201,"name":"Ifrit"},"killer":{"id":169}}""", 120);
        var lonely = Event("""{"event":"kill","victim":{"id":201,"name":"Ifrit"}}""", 120);
        var a = ShotPresets.Build("two_shot", launch, 95, 106).Shots.Single();
        Assert.Equal(("two_shot", "unit:169", "unit:201"), (a.Camera, a.Target, a.Second));
        var b = ShotPresets.Build("two_shot", kill, 115, 126).Shots.Single();
        Assert.Equal(("unit:169", "unit:201"), (b.Target, b.Second));
        var c = ShotPresets.Build("two_shot", lonely, 115, 126).Shots.Single();
        Assert.Equal("unit:201", c.Target);
        Assert.Null(c.Second);
        Assert.Contains("\"second\": \"unit:201\"", ShotPresets.Build("two_shot", kill, 115, 126).ToJson());
    }

    private static DemoEvent Event(string json, double t) => DemoEvent.Parse(t, json)!;

    [Fact]
    public void PresetsProduceValidShotListsForEveryStyle()
    {
        var launch = Event("""{"event":"launch","shooter":{"id":169,"name":"Revoker"},"target":{"id":201},"weapon":"AAM-29","missile":4021}""", 100);
        var kill = Event("""{"event":"kill","victim":{"id":201,"name":"Ifrit"},"killer":{"id":169}}""", 120);
        foreach (var style in ShotPresets.CameraStyles)
        foreach (var moment in new DemoEvent?[] { launch, kill, null })
        {
            var shots = ShotPresets.Build(style.Id, moment, 95, 106);
            var result = ShotValidator.Validate(shots.ToJson());
            Assert.True(result.Valid, $"{style.Id}: {string.Join("; ", result.Issues)}");
            Assert.Equal(95, shots.Shots[0].T);
        }
        var cinematic = ShotPresets.Build("cinematic_chase", launch, 95, 106);
        Assert.Equal("unit:169", cinematic.Shots[0].Target);
        Assert.Equal("unit:4021", cinematic.Shots[1].Target);
        Assert.Equal(100.4, cinematic.Shots[1].T, 6);
        Assert.Equal("cockpit", ShotPresets.Build("cockpit", kill, 115, 126).Shots[0].Camera);
        Assert.Equal("unit:201", ShotPresets.Build("flyby", kill, 115, 126).Shots[0].Target);
    }

    [Fact]
    public void VictimCutCutsWhenTheMissileIsGoneAndMissileFollowHolds()
    {
        var launch = Event("""{"event":"launch","shooter":{"id":169,"name":"Revoker"},"target":{"id":201},"weapon":"AAM-29","missile":4021}""", 100);
        var kill = Event("""{"event":"kill","victim":{"id":201,"name":"Ifrit"},"killer":{"id":169}}""", 104);
        var events = new[] { launch, kill };

        var victim = ShotPresets.Build("victim_cut", kill, 99, 110, events).Shots;
        var ride = victim.Single(s => s.Target == "unit:4021");
        Assert.Equal(0, ride.Hold);
        Assert.Equal("orbit", ride.Then!.Camera);
        Assert.Equal("unit:201", ride.Then.Target);
        // The fallback at the logged kill repeats the same camera and target.
        Assert.Contains(victim, s => s.Camera == "orbit" && s.Target == "unit:201" && s.T > 103);

        var follow = ShotPresets.Build("missile_follow", kill, 99, 110, events).Shots;
        var missile = follow.Single(s => s.Target == "unit:4021");
        Assert.Null(missile.Then);
        Assert.True(missile.Hold >= 11 - 0.001);
        Assert.DoesNotContain(follow, s => s.Target == "unit:201");

        var json = ShotPresets.Build("victim_cut", kill, 99, 110, events).ToJson();
        Assert.Contains("\"then\"", json);
        var parsed = ShotValidator.Validate(json);
        Assert.True(parsed.Valid, string.Join("; ", parsed.Issues));
        Assert.Equal("unit:201", parsed.ShotList!.Shots.Single(s => s.Target == "unit:4021").Then!.Target);
    }

    [Fact]
    public void KillsWithAHitTimeCutToTheVictimAtTheImpact()
    {
        var launch = Event("""{"event":"launch","shooter":{"id":169,"name":"Revoker"},"target":{"id":201},"weapon":"AAM-29","missile":4021}""", 100);
        // Kill feed at 106.5, hit 2.5 s earlier.
        var kill = Event("""{"event":"kill","victim":{"id":201,"name":"Ifrit"},"killer":{"id":169},"hit_ago":2.5}""", 106.5);
        var events = new[] { launch, kill };
        Assert.Equal(104, kill.T, 6);
        Assert.Same(launch, ShotPresets.Pair(kill, events).Launch);
        Assert.Same(kill, ShotPresets.Pair(launch, events).Kill);

        var victim = ShotPresets.Build("victim_cut", kill, 99, 110, events).Shots;
        Assert.Equal(103.9, victim.Last(s => s.Camera == "orbit" && s.Target == "unit:201").T, 6);

        // Cinematic chase: just before the impact with a hit time, 2.5 s before the kill feed without one.
        var cinematic = ShotPresets.Build("cinematic_chase", kill, 99, 110, events).Shots;
        Assert.Equal(103.9, cinematic.Single(s => s.Target == "unit:201").T, 6);
        var old = Event("""{"event":"kill","victim":{"id":201,"name":"Ifrit"},"killer":{"id":169}}""", 106.5);
        Assert.Equal(104, ShotPresets.Build("cinematic_chase", old, 99, 110).Shots.Single(s => s.Target == "unit:201").T, 6);
    }

    [Theory]
    [InlineData("""{"shots":[{"t":1,"camera":"chase","target":"host","then":5}]}""", "$.shots[0].then")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase","target":"host","then":{"camera":"spin","target":"host"}}]}""", "$.shots[0].then.camera")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase","target":"host","then":{"camera":"orbit","target":"nobody"}}]}""", "$.shots[0].then.target")]
    public void InvalidThenIsReported(string json, string path)
    {
        var result = ShotValidator.Validate(json);
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, i => i.IsError && i.Path == path);
    }

    [Fact]
    public void RangeAroundClampsToRecording()
    {
        Assert.Equal((0.0, 8.0), ShotPresets.RangeAround(2, 5, 6, 100));
        Assert.Equal((95.0, 100.0), ShotPresets.RangeAround(100, 5, 6, 100));
        Assert.Equal((45.0, 56.0), ShotPresets.RangeAround(50, 5, 6, 0)); // unknown length
    }
}
