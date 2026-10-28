using NuclearReplay.Playback;

namespace NuclearReplay.Core.Tests;

// replay/src/UnitList.cs: the viewer's unit list, the [ ] filter and the type:<text> selector.
public class UnitListTests
{
    private static UnitEntry U(string id, string name, UnitKind kind, string type, string? player = null, string side = "Boscali", bool alive = true, string? code = null) =>
        new() { Id = id, Name = name, Kind = kind, Type = type, Player = player, Side = side, Alive = alive, Code = code };

    private static readonly UnitEntry[] Units =
    {
        U("1", "Shard Class Destroyer 1", UnitKind.Sea, "Shard Class Destroyer", side: "Primeva", code: "DD"),
        U("2", "FS-12 Revoker", UnitKind.Aircraft, "FS-12 Revoker", player: "Stormtruper22"),
        U("3", "AFV-6 Bulwark", UnitKind.Ground, "AFV-6 Bulwark"),
        U("4", "UH-80 Ibis", UnitKind.Rotorcraft, "UH-80 Ibis"),
        U("5", "KR-67 Ifrit", UnitKind.Aircraft, "KR-67 Ifrit", side: "Primeva"),
        U("6", "Talamander", UnitKind.Aircraft, "SAH-46 Chicane", player: "Talamander"),
        U("7", "AFV-6 Bulwark", UnitKind.Ground, "AFV-6 Bulwark", alive: false),
        U("8", "Hangar", UnitKind.Building, "Hangar"),
        U("9", "Annihilator Frigate", UnitKind.Sea, "Annihilator Frigate", alive: false),
    };

    [Fact]
    public void ListsPlayersFirstThenByKindAndType()
    {
        var all = UnitList.View(Units, "all", "", showDestroyed: false).Select(u => u.Id).ToArray();
        // Players (by type), then aircraft, helicopters, ships, ground vehicles, buildings; destroyed hidden.
        Assert.Equal(new[] { "2", "6", "5", "4", "1", "3", "8" }, all);
        Assert.Contains("7", UnitList.View(Units, "all", "", showDestroyed: true).Select(u => u.Id));
    }

    [Fact]
    public void FiltersMatchTheirKinds()
    {
        Assert.Equal(new[] { "2", "6", "5", "4" }, UnitList.View(Units, "aircraft", null!, false).Select(u => u.Id));
        Assert.Equal(new[] { "3" }, UnitList.View(Units, "ground", "", false).Select(u => u.Id));
        Assert.Equal(new[] { "9", "1" }, UnitList.View(Units, "sea", "", true).Select(u => u.Id));
        // The [ ] cycle never steps onto buildings, even with "all".
        Assert.False(UnitList.Matches(UnitKind.Building, "all", cycle: true));
        Assert.True(UnitList.Matches(UnitKind.Building, "all"));
        Assert.Equal("ground", UnitList.NextFilter("aircraft", 1));
        Assert.Equal("all", UnitList.NextFilter("aircraft", -1));
        Assert.Equal("sea", UnitList.FilterOf(UnitKind.Sea));
        Assert.Equal("aircraft", UnitList.FilterOf(UnitKind.Rotorcraft));
    }

    [Theory]
    [InlineData("storm", "2")]
    [InlineData("revoker", "2")]
    [InlineData("primeva ship", "1")]
    [InlineData("helicopter", "4")]
    [InlineData("bulwark", "3")]
    [InlineData("chicane tal", "6")]
    public void SearchesNamePlayerTypeSideAndKind(string query, string id) =>
        Assert.Equal(new[] { id }, UnitList.View(Units, "all", query, false).Select(u => u.Id));

    [Fact]
    public void LabelShowsThePlayerOnlyWhenTheNameDoesNotAlready()
    {
        Assert.Equal("Stormtruper22 (FS-12 Revoker)", Units[1].Label);
        Assert.Equal("Talamander", Units[5].Label);
    }

    [Fact]
    public void TypeSelectorPicksTheNearestLiveMatch()
    {
        Assert.Equal("1", UnitList.PickType(Units, "destroyer", null)!.Id);
        Assert.Equal("1", UnitList.PickType(Units, "DD", null)!.Id);
        Assert.Equal("1", UnitList.PickType(Units, "ship", null)!.Id);             // the frigate is destroyed
        Assert.Equal("4", UnitList.PickType(Units, "helicopter", null)!.Id);
        Assert.Null(UnitList.PickType(Units, "frigate", null));
        // Nearest wins when there is a last view.
        var dist = new Dictionary<string, float> { ["2"] = 900, ["5"] = 200, ["6"] = 500, ["4"] = 50 };
        Assert.Equal("5", UnitList.PickType(Units, "jet", u => dist.TryGetValue(u.Id, out var d) ? d : 1e9f)!.Id);
        Assert.Equal("4", UnitList.PickType(Units, "aircraft", u => dist.TryGetValue(u.Id, out var d) ? d : 1e9f)!.Id);
    }
}
