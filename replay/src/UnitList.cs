#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace NuclearReplay.Playback
{
    // What the viewer's unit list and the camera target selectors know about a unit. Unity-free, so the tests run it;
    // Units.cs fills it in from the game's units.
    internal enum UnitKind { Aircraft, Rotorcraft, Ground, Sea, Missile, Building, Other }

    internal sealed class UnitEntry
    {
        public string Id;          // persistent id, as in unit:<id>
        public string Name;        // the unit's name (a player's callsign for a player aircraft)
        public string Player;      // player name, null for AI units
        public string Type;        // unit type ("FS-12 Revoker", "Shard Class Destroyer")
        public string Code;        // short type code
        public string Side;        // faction name, null when the unit has none
        public UnitKind Kind;
        public bool Alive = true;
        public float Distance;     // metres from the camera, for the list only
        public object Unit;        // the game's unit (opaque here)

        public string Label => !string.IsNullOrEmpty(Player) && (Name == null || Name.IndexOf(Player, StringComparison.OrdinalIgnoreCase) < 0)
            ? Player + " (" + Name + ")" : Name ?? Type ?? "unit " + Id;
    }

    // Unit kinds, the camera target filter, list order and search, and the type:<text> selector.
    //
    // Filters (the viewer's [ ] cycle and its unit list):
    //   aircraft  jets and helicopters (the default, as before)
    //   ground    ground vehicles
    //   sea       ships
    //   all       every unit; the [ ] cycle skips buildings and other static objects, the list shows them
    // List order: players first, then aircraft, helicopters, ships, ground vehicles, missiles, buildings, other;
    // then by type and name.
    internal static class UnitList
    {
        public static readonly string[] Filters = { "aircraft", "ground", "sea", "all" };
        public static readonly string[] FilterLabels = { "Aircraft", "Ground", "Sea", "All" };

        public static string FilterLabel(string filter)
        {
            int i = Array.IndexOf(Filters, filter);
            return i >= 0 ? FilterLabels[i] : filter;
        }

        public static string NextFilter(string filter, int step)
        {
            int i = Array.IndexOf(Filters, filter);
            if (i < 0) i = 0;
            return Filters[((i + step) % Filters.Length + Filters.Length) % Filters.Length];
        }

        // Whether a unit of this kind is in the filter. `cycle`: for the [ ] keys, which never step onto a building.
        public static bool Matches(UnitKind kind, string filter, bool cycle = false)
        {
            switch (filter)
            {
                case "ground": return kind == UnitKind.Ground;
                case "sea": return kind == UnitKind.Sea;
                case "all": return !cycle || (kind != UnitKind.Building && kind != UnitKind.Other);
                default: return kind == UnitKind.Aircraft || kind == UnitKind.Rotorcraft;
            }
        }

        // The filter a unit belongs to, so the [ ] keys keep stepping through units like the one just picked.
        public static string FilterOf(UnitKind kind) =>
            kind == UnitKind.Aircraft || kind == UnitKind.Rotorcraft ? "aircraft" : kind == UnitKind.Ground ? "ground" : kind == UnitKind.Sea ? "sea" : "all";

        public static string KindWord(UnitKind kind)
        {
            switch (kind)
            {
                case UnitKind.Aircraft: return "aircraft";
                case UnitKind.Rotorcraft: return "helicopter";
                case UnitKind.Ground: return "ground vehicle";
                case UnitKind.Sea: return "ship";
                case UnitKind.Missile: return "missile";
                case UnitKind.Building: return "building";
                default: return "other";
            }
        }

        // Extra words a search or a type:<text> selector may use for a kind.
        private static string[] KindWords(UnitKind kind)
        {
            switch (kind)
            {
                case UnitKind.Aircraft: return new[] { "aircraft", "air", "plane", "jet" };
                case UnitKind.Rotorcraft: return new[] { "aircraft", "air", "helicopter", "heli", "helo", "rotorcraft" };
                case UnitKind.Ground: return new[] { "ground", "vehicle" };
                case UnitKind.Sea: return new[] { "sea", "ship", "boat", "naval" };
                case UnitKind.Missile: return new[] { "missile", "weapon" };
                case UnitKind.Building: return new[] { "building", "structure" };
                default: return new[] { "other" };
            }
        }

        private static int KindOrder(UnitKind kind)
        {
            switch (kind)
            {
                case UnitKind.Aircraft: return 0;
                case UnitKind.Rotorcraft: return 1;
                case UnitKind.Sea: return 2;
                case UnitKind.Ground: return 3;
                case UnitKind.Missile: return 4;
                case UnitKind.Building: return 5;
                default: return 6;
            }
        }

        public static int Compare(UnitEntry a, UnitEntry b)
        {
            int c = string.IsNullOrEmpty(a.Player).CompareTo(string.IsNullOrEmpty(b.Player));
            if (c == 0) c = KindOrder(a.Kind).CompareTo(KindOrder(b.Kind));
            if (c == 0) c = string.Compare(a.Type ?? "", b.Type ?? "", StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = string.Compare(a.Label ?? "", b.Label ?? "", StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = string.CompareOrdinal(a.Id ?? "", b.Id ?? "");
            return c;
        }

        // Search: every word of the query must appear in the name, player, type, code, side or kind of the unit.
        public static bool Search(UnitEntry u, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            string text = string.Join(" ", new[] { u.Name, u.Player, u.Type, u.Code, u.Side }.Where(s => !string.IsNullOrEmpty(s)))
                + " " + string.Join(" ", KindWords(u.Kind));
            foreach (var word in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (text.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        // type:<text>: the text is part of the unit's type name or its code, or a kind word (ship, helicopter, ground ...).
        public static bool TypeMatches(UnitEntry u, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (KindWords(u.Kind).Any(w => string.Equals(w, text, StringComparison.OrdinalIgnoreCase))) return true;
            return (u.Type ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                || (!string.IsNullOrEmpty(u.Code) && string.Equals(u.Code, text, StringComparison.OrdinalIgnoreCase));
        }

        // The viewer's list: units in the filter that match the search, sorted; destroyed units only on request.
        public static List<UnitEntry> View(IEnumerable<UnitEntry> units, string filter, string query, bool showDestroyed)
        {
            var list = units.Where(u => u != null && (showDestroyed || u.Alive) && Matches(u.Kind, filter) && Search(u, query)).ToList();
            list.Sort(Compare);
            return list;
        }

        // type:<text> with more than one match: the one nearest the last view when there is one, else the first in
        // list order. Null when nothing alive matches. `distance` gives a unit's distance from the last view.
        public static UnitEntry PickType(IEnumerable<UnitEntry> units, string text, Func<UnitEntry, float> distance)
        {
            var matches = units.Where(u => u != null && u.Alive && TypeMatches(u, text)).ToList();
            if (matches.Count == 0) return null;
            if (distance != null) return matches.OrderBy(distance).ThenBy(u => u, Comparer<UnitEntry>.Create(Compare)).First();
            matches.Sort(Compare);
            return matches[0];
        }
    }
}
