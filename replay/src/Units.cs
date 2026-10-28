using System;
using System.Collections.Generic;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // The game's units as UnitEntry rows (UnitList.cs): kind, type, side and player name.
    internal static class Units
    {
        // Helicopters are Aircraft with a rotor; checked once per unit.
        private static readonly Dictionary<Unit, bool> rotor = new Dictionary<Unit, bool>();

        public static UnitKind Kind(Unit unit)
        {
            if (unit is Aircraft)
            {
                if (!rotor.TryGetValue(unit, out bool r))
                {
                    try { r = unit.GetComponentInChildren<RotorShaft>(true) != null; } catch (Exception) { r = false; }
                    if (rotor.Count > 2000) rotor.Clear();
                    rotor[unit] = r;
                }
                return r ? UnitKind.Rotorcraft : UnitKind.Aircraft;
            }
            if (unit is Ship) return UnitKind.Sea;
            if (unit is GroundVehicle) return UnitKind.Ground;
            if (unit is Missile) return UnitKind.Missile;
            if (unit is Building) return UnitKind.Building;
            return UnitKind.Other;
        }

        public static string Player(Unit unit)
        {
            try { return unit is Aircraft aircraft && aircraft.Player != null ? aircraft.Player.GetPlayerName()?.SanitizedName : null; }
            catch (Exception) { return null; }
        }

        public static string Side(Unit unit)
        {
            try { return unit.NetworkHQ != null && unit.NetworkHQ.faction != null ? unit.NetworkHQ.faction.factionName : null; }
            catch (Exception) { return null; }
        }

        public static UnitEntry Entry(Unit unit, Vector3? from = null)
        {
            var e = new UnitEntry
            {
                Id = unit.persistentID.ToString(),
                Name = unit.unitName,
                Player = Player(unit),
                Type = unit.definition != null ? unit.definition.unitName : null,
                Code = unit.definition != null ? unit.definition.code : null,
                Side = Side(unit),
                Kind = Kind(unit),
                Alive = !unit.disabled,
                Unit = unit
            };
            if (from.HasValue && unit.transform != null) e.Distance = (unit.transform.position - from.Value).magnitude;
            return e;
        }

        // Longest side of the unit in metres (its definition, else the size of its bounding sphere).
        public static float Size(Unit unit)
        {
            if (unit == null) return 15f;
            if (unit.definition != null && Mathf.Max(unit.definition.length, unit.definition.width) > 0.5f)
                return Mathf.Max(unit.definition.length, unit.definition.width);
            return Mathf.Max(1f, unit.maxRadius * 2f);
        }

        // Same side? False when either has no side.
        public static bool Enemies(Unit a, Unit b)
        {
            try { return a.NetworkHQ != null && b.NetworkHQ != null && a.NetworkHQ != b.NetworkHQ; }
            catch (Exception) { return false; }
        }
    }
}
