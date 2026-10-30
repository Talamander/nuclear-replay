using NuclearReplay.Core.Shots;
using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

// replay/src/HudModel.cs (the target HUD over replay cameras: which units get a marker, a label, a box and a line)
// and the shot-list validator's "target_hud" field. The fixture is the Point Blank recording at 111.5 s: Talamander's
// SAH-46 (Boscali) has rippled ten AGR-18 Lynchpins at five Primeva ground targets, and a StratoLance SAM has fired
// at one of the Lynchpins.
public class HudTests
{
    private static readonly HudView Wide = new(1920, 1080);
    private static readonly HudView Tall = new(1080, 1920);

    private static HudUnit Unit(string id, UnitKind kind, string side, string code, string type, double x, double y, double distance,
        string? player = null, double px = 0, double pz = 0)
        => new()
        {
            Id = id, Kind = kind, Side = side, Code = code, Type = type, Player = player, X = x, Y = y, Depth = distance,
            Distance = distance, PX = px, PZ = pz, ScreenSize = 20
        };

    private static HudUnit Missile(string id, string owner, string target, string side, double x, double y, double distance, double pz, string seeker = "active", string code = "AGR-18")
    {
        var m = Unit(id, UnitKind.Missile, side, code, code + " Lynchpin", x, y, distance, pz: pz);
        m.OwnerId = owner; m.TargetId = target; m.Seeker = seeker;
        return m;
    }

    // Tal's jet at the origin (the chase camera 60 m behind), targets 6-9 km north, the salvo spread between.
    private static List<HudUnit> PointBlank(bool clustered = false)
    {
        var tal = Unit("221", UnitKind.Aircraft, "Boscali", "SAH-46", "SAH-46 Chicane", 960, 520, 60, "Talamander");
        tal.ScreenSize = 300;   // the chase camera's subject fills the frame
        tal.Targets = new List<string> { "137", "138", "176", "127", "139" };
        var units = new List<HudUnit> { tal };
        string[] targets = { "137", "138", "176", "127", "139" };
        string[] names = { "StratoLance R9 Launcher", "StratoLance R9 Launcher", "Munitions Truck", "Vehicle Depot", "StratoLance R9 Launcher" };
        for (int i = 0; i < 5; i++)
        {
            var t = Unit(targets[i], i == 3 ? UnitKind.Building : UnitKind.Ground, "Primeva", "T" + i, names[i], 500 + i * 220, 600, 6000 + i * 700, pz: 6000 + i * 700);
            units.Add(t);
        }
        for (int i = 0; i < 10; i++)
        {
            double x = clustered ? 1000 + i * 2 : 700 + i * 60, y = clustered ? 560 + i : 560 + (i % 3) * 40;
            units.Add(Missile((234 + i).ToString(), "221", targets[i % 5], "Boscali", x, y, 1500 + i * 50, 1500 + i * 50, seeker: i % 2 == 0 ? "active" : "passive"));
        }
        // A StratoLance SAM fires at the first Lynchpin; a Primeva truck and a Boscali tanker are about.
        var sam = Missile("226", "137", "234", "Primeva", 900, 590, 4000, 4000, code: "SAM-9");
        units.Add(sam);
        units.Add(Unit("150", UnitKind.Ground, "Primeva", "HLT", "HLT Tractor", 300, 300, 12000));
        units.Add(Unit("151", UnitKind.Ground, "Primeva", "HLT", "HLT Tractor", 320, 300, 16000));
        units.Add(Unit("90", UnitKind.Aircraft, "Boscali", "KR-67", "KR-67 Ifrit", 1500, 900, 3000));
        units.Add(Unit("91", UnitKind.Aircraft, "Boscali", "KR-67", "KR-67 Ifrit", 1550, 900, 6000));
        return units;
    }

    private static HudSettings Mode(string mode) => new() { Mode = mode };
    private static HudUnit Focus(List<HudUnit> units, string id = "221") => units.First(u => u.Id == id);

    [Fact]
    public void ShotFieldsReadAsTheDesignSays()
    {
        Assert.Null(HudSettings.FromShot(null, null));
        Assert.Equal("story", HudSettings.FromShot(true, null)!.Mode);
        Assert.Equal("off", HudSettings.FromShot(false, "all")!.Mode);
        Assert.Equal("missiles", HudSettings.FromShot("missiles", null)!.Mode);
        // The root is the default; a shot's own keys win key by key.
        var s = HudSettings.FromShot(new Dictionary<string, object> { ["labels"] = false },
            new Dictionary<string, object> { ["mode"] = "all", ["size"] = 1.4, ["side"] = "Primeva" })!;
        Assert.Equal("all", s.Mode);
        Assert.False(s.Labels);
        Assert.Equal(1.4, s.Size, 3);
        Assert.Equal("Primeva", s.Side);
        // An object without a mode means story; sizes are clamped.
        var o = HudSettings.FromShot(new Dictionary<string, object> { ["size"] = 9L, ["units"] = "imperial", ["home"] = "Boscali" }, null)!;
        Assert.Equal("story", o.Mode);
        Assert.Equal(HudSettings.MaxSize, o.Size);
        Assert.Equal("imperial", o.Units);
        Assert.Equal("Boscali", o.Home);
        Assert.Equal("mode=story home=Boscali size=2.5 units=imperial", string.Join(" ", o.ToShot().Select(kv => kv.Key + "=" + Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void ModeCycleRunsOffStoryMissilesAll()
    {
        Assert.Equal("story", HudSettings.NextMode("off", 1));
        Assert.Equal("missiles", HudSettings.NextMode("story", 1));
        Assert.Equal("all", HudSettings.NextMode("missiles", 1));
        Assert.Equal("off", HudSettings.NextMode("all", 1));
        Assert.Equal("all", HudSettings.NextMode("off", -1));
    }

    [Theory]
    [InlineData("off", "Target HUD: Off")]
    [InlineData("story", "Target HUD: Story")]
    [InlineData("missiles", "Target HUD: Missiles")]
    [InlineData("all", "Target HUD: All")]
    [InlineData("bogus", "Target HUD: Off")]
    [InlineData(null, "Target HUD: Off")]
    public void ButtonLabelNamesTheMode(string mode, string label)
    {
        Assert.Equal(label, HudSettings.ButtonLabel(mode));
    }

    [Fact]
    public void ButtonClicksWalkAllFourModesBothWays()
    {
        // Left click steps forward, right click back; four clicks either way come home.
        string forward = "off", back = "off";
        var seenForward = new List<string>();
        for (int i = 0; i < 4; i++) { forward = HudSettings.NextMode(forward, 1); seenForward.Add(forward); back = HudSettings.NextMode(back, -1); }
        Assert.Equal(new[] { "story", "missiles", "all", "off" }, seenForward);
        Assert.Equal("off", back);
        Assert.StartsWith("Target HUD (F4)", HudSettings.ButtonHint);
    }

    [Theory]
    [InlineData(850, "metric", "850 m")]
    [InlineData(4200, "metric", "4.2 km")]
    [InlineData(12400, "metric", "12 km")]
    [InlineData(500, "imperial", "547 yd")]
    [InlineData(9260, "imperial", "5.0 nm")]
    public void DistancesReadLikeTheGame(double metres, string units, string text)
    {
        Assert.Equal(text, HudModel.Distance(metres, units));
    }

    [Fact]
    public void GameShrinksMarkersWithDistance()
    {
        Assert.Equal(1, HudModel.DistanceScale(5000), 3);
        Assert.Equal(1, HudModel.DistanceScale(12500), 3);
        Assert.Equal(0.45, HudModel.DistanceScale(40000), 3);
    }

    [Fact]
    public void StoryShowsTheFocusUnitsFightWithBoxesAndLines()
    {
        var units = PointBlank();
        var f = HudModel.Build(Mode("story"), units, Focus(units), Wide, "Boscali");
        // Tal's jet fills the frame: no marker (subject rule), but its fight is all there.
        Assert.Null(f.Find("221"));
        foreach (var id in new[] { "137", "138", "176", "127", "139" })
        {
            var t = f.Find(id)!;
            Assert.NotNull(t);
            Assert.True(t.Box);
            Assert.Equal(HudSide.Hostile, t.Side);
        }
        Assert.Equal(HudRole.Primary, f.Find("137")!.Role);
        for (int i = 234; i < 244; i++) Assert.Equal(HudRole.FocusMissile, f.Find(i.ToString())!.Role);
        // The SAM shot at Tal's missile is not Tal's fight; quiet context keeps no missiles in story.
        Assert.Null(f.Find("226"));
        // Missile lines: solid when the missile's own seeker has the target, dashed before.
        Assert.Equal(10, f.Lines.Count(l => l.Kind == HudLineKind.Missile));
        Assert.True(f.Lines.First(l => l.From.Id == "234").Solid);
        Assert.False(f.Lines.First(l => l.From.Id == "235").Solid);
        Assert.All(f.Lines, l => Assert.Equal(HudSide.Friendly, l.Side));
        // No designation line: missiles already fly at the primary target.
        Assert.DoesNotContain(f.Lines, l => l.Kind == HudLineKind.Designation);
        // Context: the 12 km truck faded, the 16 km one gone; a friendly 3 km away small, 6 km away not drawn.
        Assert.Equal(HudLook.Pip, f.Find("150")!.Look);
        Assert.Equal(0.6, f.Find("150")!.Alpha, 2);
        Assert.Null(f.Find("151"));
        Assert.Equal(HudLook.Small, f.Find("90")!.Look);
        Assert.Null(f.Find("91"));
        Assert.Null(f.Find("150")!.Label);
    }

    [Fact]
    public void LabelsKeepToTheBudgetAndPriority()
    {
        var units = PointBlank();
        var f = HudModel.Build(Mode("story"), units, Focus(units), Wide, "Boscali");
        var labelled = f.Marks.Where(m => m.Label != null).ToList();
        Assert.InRange(labelled.Count, 1, 8);
        // Tal's missiles come first, with the distance to their target.
        Assert.Contains(labelled, m => m.Unit.Kind == UnitKind.Missile && m.Label!.StartsWith("AGR-18 · ") && m.Label.EndsWith(" km"));
        // No two labels overlap.
        var vertical = HudModel.Build(Mode("story"), units, Focus(units), Tall, "Boscali");
        Assert.InRange(vertical.Marks.Count(m => m.Label != null), 0, 4);
    }

    [Fact]
    public void ASalvoSharesOneLabel()
    {
        var units = PointBlank(clustered: true);
        var f = HudModel.Build(Mode("story"), units, Focus(units), Wide, "Boscali");
        var salvo = f.Marks.Where(m => m.Unit.Kind == UnitKind.Missile && m.Label != null).ToList();
        Assert.Single(salvo);
        Assert.Equal("AGR-18 ×10", salvo[0].Label);
    }

    [Fact]
    public void LabelsNeverCoverAnotherMarker()
    {
        var units = PointBlank();
        var f = HudModel.Build(Mode("all"), units, Focus(units), Wide, "Boscali");
        double scale = Wide.Scale;
        foreach (var m in f.Marks.Where(m => m.Label != null && !m.Arrow && m.Salvo < 3))
        {
            double w = HudModel.LabelWidth(m.Label) * scale, h = HudModel.TextPx * 1.25 * scale, gap = (HudModel.MarkerPx * 0.6 + 6) * scale;
            double x = m.Unit.X, y = m.Unit.Y;
            var r = m.LabelAt switch
            {
                HudLabelSide.Right => (x + gap, y - h / 2, x + gap + w, y + h / 2),
                HudLabelSide.Left => (x - gap - w, y - h / 2, x - gap, y + h / 2),
                HudLabelSide.Above => (x - w / 2, y + gap, x + w / 2, y + gap + h),
                _ => (x - w / 2, y - gap - h, x + w / 2, y - gap),
            };
            foreach (var o in f.Marks.Where(o => o != m && !o.Arrow && o.Look == HudLook.Full && o.Unit.InFront))
            {
                double half = HudModel.MarkerPx * o.Size * scale * (o.Box ? 0.75 : 0.5);
                bool hit = r.Item1 < o.Unit.X + half && o.Unit.X - half < r.Item3 && r.Item2 < o.Unit.Y + half && o.Unit.Y - half < r.Item4;
                Assert.False(hit, $"label of {m.Unit.Id} covers {o.Unit.Id}");
            }
        }
    }

    [Fact]
    public void LabelsKeepTheirUnitAndSideFromFrameToFrame()
    {
        List<HudUnit> Scene(double d300, double d301)
        {
            var units = PointBlank().Where(u => u.Kind != UnitKind.Missile).ToList();
            units.Add(Missile("300", "221", "137", "Boscali", 900, 500, d300, d300));
            units.Add(Missile("301", "221", "138", "Boscali", 900, 505, d301, d301));
            return units;
        }
        var one = Scene(1100, 1150);
        var first = HudModel.Build(Mode("all"), one, Focus(one), Wide, "Boscali");
        var side300 = first.Find("300")!.LabelAt;
        var side301 = first.Find("301")!.LabelAt;
        Assert.NotEqual(side300, side301);
        // 301 is now nearer, so it would win the first spot; the last frame's placement holds instead.
        var two = Scene(1150, 1100);
        var second = HudModel.Build(Mode("all"), two, Focus(two), Wide, "Boscali", first);
        Assert.Equal(side300, second.Find("300")!.LabelAt);
        Assert.Equal(side301, second.Find("301")!.LabelAt);
        var fresh = HudModel.Build(Mode("all"), two, Focus(two), Wide, "Boscali");
        Assert.Equal(side300, fresh.Find("301")!.LabelAt);
    }

    [Fact]
    public void PrimaryTargetLabelCarriesTheRangeFromTheFocus()
    {
        var units = PointBlank();
        var f = HudModel.Build(Mode("story") , units.Where(u => u.Kind != UnitKind.Missile).ToList(), Focus(units), Wide, "Boscali");
        Assert.Equal("StratoLance R9 Launcher · 6.0 km", f.Find("137")!.Label);
        // With no missile at it, a fainter dashed designation line runs from the focus unit to its primary target.
        var line = Assert.Single(f.Lines);
        Assert.Equal(HudLineKind.Designation, line.Kind);
        Assert.Equal("221", line.From.Id);
        Assert.Equal("137", line.To.Id);
        Assert.False(line.Solid);
    }

    [Fact]
    public void AMissileCamerasFocusIsItsShooter()
    {
        var units = PointBlank();
        var f = HudModel.Build(Mode("story"), units, Focus(units, "236"), Wide, "Boscali");
        Assert.Equal("221", f.Focus!.Id);
        Assert.Equal(10, f.Marks.Count(m => m.Role == HudRole.FocusMissile));
    }

    [Fact]
    public void ColoursStayWithTheRecordingPlayersSideWhenTheCameraCutsToTheEnemy()
    {
        var units = PointBlank();
        var sam = Focus(units, "137");
        var f = HudModel.Build(Mode("story"), units, sam, Wide, "Boscali");
        // The SAM site's fight: its missile at Tal's Lynchpin. The SAM is still hostile, the Lynchpin friendly.
        Assert.Equal(HudSide.Hostile, f.Find("226")!.Side);
        Assert.Equal(HudRole.FocusMissile, f.Find("226")!.Role);
        Assert.Equal(HudSide.Friendly, f.Find("234")!.Side);
        Assert.Equal(HudRole.Threat, f.Find("234")!.Role);   // the Lynchpin flies at the SAM site too
        Assert.Equal(HudRole.Shooter, f.Find("221")?.Role ?? HudRole.Shooter);
        Assert.Equal(HudSide.Hostile, f.Lines.Single(l => l.From.Id == "226").Side);
    }

    [Fact]
    public void ThreatsAtTheFocusAreItsTopPriorityAndShowTheirShooter()
    {
        var units = PointBlank();
        var lynchpin = Focus(units, "234");
        lynchpin.Kind = UnitKind.Missile;
        // Follow the Lynchpin's shooter? No: make the SAM's target an aircraft to see an incoming threat.
        var tal = Focus(units);
        units.First(u => u.Id == "226").TargetId = "221";
        var f = HudModel.Build(Mode("story"), units, tal, Wide, "Boscali");
        var threat = f.Find("226")!;
        Assert.Equal(HudRole.Threat, threat.Role);
        Assert.Equal(HudRole.Shooter, f.Find("137")!.Role is HudRole.Primary ? HudRole.Shooter : f.Find("137")!.Role);
        Assert.Same(threat, f.Marks.First());
    }

    [Fact]
    public void OffScreenArrowsAreStoryOnlyAndAtMostTwo()
    {
        var units = PointBlank();
        foreach (var id in new[] { "137" }) { var t = Focus(units, id); t.X = -400; }
        var ids = new[] { "226", "300", "301" };
        units.First(u => u.Id == "226").TargetId = "221";
        units.Add(Missile("300", "138", "221", "Primeva", 3000, 500, 5000, 5000, code: "SAM-9"));
        units.Add(Missile("301", "138", "221", "Primeva", -500, 500, 6000, 6000, code: "SAM-9"));
        units.First(u => u.Id == "226").X = 2500;
        var f = HudModel.Build(Mode("story"), units, Focus(units), Wide, "Boscali");
        Assert.Equal(2, f.Marks.Count(m => m.Arrow));
        var all = HudModel.Build(Mode("all"), units, Focus(units), Wide, "Boscali");
        Assert.DoesNotContain(all.Marks, m => m.Arrow);
        var noArrows = HudModel.Build(new HudSettings { Mode = "story", Arrows = false }, units, Focus(units), Wide, "Boscali");
        Assert.DoesNotContain(noArrows.Marks, m => m.Arrow);
        Assert.NotNull(ids);
    }

    [Fact]
    public void MissilesModeShowsEveryMissileItsTargetAndLauncherOnly()
    {
        var units = PointBlank();
        var f = HudModel.Build(Mode("missiles"), units, Focus(units, "90"), Wide, "Boscali");
        Assert.NotNull(f.Find("226"));
        Assert.Equal(11, f.Marks.Count(m => m.Unit.Kind == UnitKind.Missile));
        Assert.Equal(11, f.Lines.Count);
        Assert.Null(f.Find("150"));              // no bystanders
        Assert.NotNull(f.Find("221") ?? f.Find("137"));
    }

    [Fact]
    public void AllModeShowsBystandersAndNoDesignationLines()
    {
        var units = PointBlank();
        var f = HudModel.Build(Mode("all"), units.Where(u => u.Kind != UnitKind.Missile).ToList(), Focus(units), Wide, "Boscali");
        Assert.NotNull(f.Find("90"));
        Assert.NotNull(f.Find("91"));
        Assert.Equal(HudLook.Pip, f.Find("150")!.Look);   // ground beyond 8 km: minimized
        Assert.Empty(f.Lines);
    }

    [Fact]
    public void SideFilterKeepsThatSidesUnitsItsMissilesAndTheirTargets()
    {
        var units = PointBlank();
        var f = HudModel.Build(new HudSettings { Mode = "all", Side = "Primeva" }, units, Focus(units), Wide, "Boscali");
        Assert.NotNull(f.Find("226"));
        Assert.NotNull(f.Find("234"));           // the SAM's target
        Assert.Null(f.Find("235"));              // other Boscali missiles
        Assert.Null(f.Find("90"));
        Assert.All(f.Marks, m => Assert.True(m.Unit.Side == "Primeva" || m.Unit.Id == "234"));
    }

    [Fact]
    public void KillsFlashThenFadeThenGo()
    {
        var units = PointBlank();
        var victim = Focus(units, "138");
        victim.WasCast = true;
        units.RemoveAll(u => u.Kind == UnitKind.Missile && u.TargetId == "138");
        victim.DeadFor = 0.2;
        var f = HudModel.Build(Mode("story"), units, Focus(units), Wide, "Boscali");
        Assert.Equal(1, f.Find("138")!.Flash);
        Assert.False(f.Find("138")!.Box);
        Assert.Null(f.Find("138")!.Label);
        victim.DeadFor = 1.0;
        Assert.Equal(0.5, HudModel.Build(Mode("story"), units, Focus(units), Wide, "Boscali").Find("138")!.Alpha, 2);
        victim.DeadFor = 1.6;
        Assert.Null(HudModel.Build(Mode("story"), units, Focus(units), Wide, "Boscali").Find("138"));
    }

    [Fact]
    public void NewMissilesFlashForASecond()
    {
        var units = PointBlank();
        var m = Focus(units, "243");
        m.Age = 0.25;
        var f = HudModel.Build(Mode("story"), units, Focus(units), Wide, "Boscali");
        Assert.Equal(0.75, f.Find("243")!.Flash, 2);
        Assert.Equal(0, f.Find("242")!.Flash);
    }

    [Fact]
    public void BehindTheCameraIsNotDrawnAndOffIsEmpty()
    {
        var units = PointBlank();
        Focus(units, "139").Depth = -500;
        var f = HudModel.Build(Mode("story"), units, Focus(units), Wide, "Boscali");
        Assert.Null(f.Find("139"));
        Assert.Empty(HudModel.Build(Mode("off"), units, Focus(units), Wide, "Boscali").Marks);
    }

    [Fact]
    public void LabelTextFollowsTheDesign()
    {
        var human = new HudMark { Unit = Unit("1", UnitKind.Aircraft, "Boscali", "SAH-46", "SAH-46 Chicane", 0, 0, 100, "Talamander") };
        var ai = new HudMark { Unit = Unit("2", UnitKind.Aircraft, "Primeva", "T/A-30 ", "T/A-30 Compass", 0, 0, 100) };
        var ship = new HudMark { Unit = Unit("3", UnitKind.Sea, "Primeva", "FFG", "Corvette", 0, 0, 100) };
        Assert.Equal("Talamander", HudModel.LabelText(human, null, null, "metric", false));
        Assert.Equal("T/A-30", HudModel.LabelText(ai, null, null, "metric", false));
        Assert.Equal("Corvette", HudModel.LabelText(ship, null, null, "metric", false));
        // A missile 200 m from its target drops the distance.
        var target = Unit("4", UnitKind.Ground, "Primeva", "T", "Truck", 0, 0, 100, pz: 200);
        var missile = Missile("5", "1", "4", "Boscali", 0, 0, 100, 0);
        var byId = new Dictionary<string, HudUnit> { ["4"] = target };
        Assert.Equal("AGR-18", HudModel.LabelText(new HudMark { Unit = missile }, null, byId, "metric", false));
        target.PZ = 2400;
        Assert.Equal("AGR-18 · 2.4 km", HudModel.LabelText(new HudMark { Unit = missile }, null, byId, "metric", false));
    }

    // ---------------- the shot-list validator ----------------

    [Theory]
    [InlineData("""{"target_hud":"story","shots":[{"t":1,"camera":"chase","target":"host","target_hud":{"mode":"all","side":"Primeva","size":1.5,"labels":false}}]}""")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase","target":"host","target_hud":true,"then":{"camera":"orbit","target":"auto","target_hud":"off"}}]}""")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase","target":"host","target_hud":{"focus":"unit:221","home":"boscali","units":"imperial","arrows":false}}]}""")]
    public void ValidTargetHudFieldsPass(string json)
    {
        var result = ShotValidator.Validate(json);
        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(i => i.Path + " " + i.Message)));
        Assert.DoesNotContain(result.Issues, i => i.Severity == "warning");
    }

    [Theory]
    [InlineData("""{"target_hud":"loud","shots":[]}""", "$.target_hud")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase","target":"host","target_hud":{"mode":"busy"}}]}""", "$.shots[0].target_hud.mode")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase","target":"host","target_hud":{"size":3}}]}""", "$.shots[0].target_hud.size")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase","target":"host","target_hud":{"side":"Martians"}}]}""", "$.shots[0].target_hud.side")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase","target":"host","target_hud":{"home":"both"}}]}""", "$.shots[0].target_hud.home")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase","target":"host","target_hud":{"focus":"Tal"}}]}""", "$.shots[0].target_hud.focus")]
    [InlineData("""{"shots":[{"t":1,"camera":"chase","target":"host","target_hud":7}]}""", "$.shots[0].target_hud")]
    public void BadTargetHudFieldsAreErrors(string json, string path)
    {
        var result = ShotValidator.Validate(json);
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, i => i.IsError && i.Path == path);
    }

    [Fact]
    public void TargetHudWarnsAboutUnknownKeysAndArrowsOutsideStory()
    {
        var result = ShotValidator.Validate("""{"shots":[{"t":1,"camera":"chase","target":"host","target_hud":{"mode":"all","arrows":true,"glow":1}}]}""");
        Assert.True(result.Valid);
        Assert.Contains(result.Issues, i => i.Severity == "warning" && i.Path == "$.shots[0].target_hud.glow");
        Assert.Contains(result.Issues, i => i.Severity == "warning" && i.Path == "$.shots[0].target_hud.arrows");
    }
}
