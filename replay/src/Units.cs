using System;
using System.Collections.Generic;
using NuclearOption.Networking;
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

        // The player flying the unit. In a replay Aircraft.Player is often empty while the Player object's Aircraft is
        // set, so the Player objects are read too (once a frame).
        private static readonly Dictionary<Unit, string> playerNames = new Dictionary<Unit, string>();
        private static int playerFrame = -1;

        // Player names by unit id from the recording's event annotations ("player", "kill", "launch" ... carry
        // {id, player}). A replay often has no Player object for a unit, so this is the main source.
        private static readonly Dictionary<string, string> recordedPlayers = new Dictionary<string, string>();

        public static void ClearRecordedPlayers() { recordedPlayers.Clear(); RecordedHome = null; }

        // The recording player's side: the first "player" annotation that names a faction (null when none does).
        // The target HUD draws this side friendly for the whole replay.
        public static string RecordedHome { get; private set; }
        public static int RecordedPlayerCount => recordedPlayers.Count;

        // One annotation's JSON: every object in it with an id and a player name.
        public static void LearnPlayers(Newtonsoft.Json.Linq.JToken token)
        {
            if (token is Newtonsoft.Json.Linq.JObject o)
            {
                if (RecordedHome == null && o["event"]?.Type == Newtonsoft.Json.Linq.JTokenType.String && (string)o["event"] == "player"
                    && o["faction"]?.Type == Newtonsoft.Json.Linq.JTokenType.String && !string.IsNullOrEmpty((string)o["faction"]))
                    RecordedHome = (string)o["faction"];
                var id = o["id"];
                string player = o["player"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)o["player"] : null;
                if (id != null && (id.Type == Newtonsoft.Json.Linq.JTokenType.Integer || id.Type == Newtonsoft.Json.Linq.JTokenType.String) && !string.IsNullOrEmpty(player))
                    recordedPlayers[id.ToString()] = player;
                foreach (var p in o.Properties()) if (p.Value is Newtonsoft.Json.Linq.JContainer) LearnPlayers(p.Value);
            }
            else if (token is Newtonsoft.Json.Linq.JArray a)
                foreach (var item in a) LearnPlayers(item);
        }

        public static string Player(Unit unit)
        {
            if (!(unit is Aircraft aircraft)) return null;
            try
            {
                if (recordedPlayers.Count > 0 && recordedPlayers.TryGetValue(unit.persistentID.ToString(), out string recorded)) return recorded;
                if (aircraft.Player != null)
                {
                    string own = aircraft.Player.GetPlayerName()?.SanitizedName;
                    if (!string.IsNullOrEmpty(own)) return own;
                }
                if (playerFrame != Time.frameCount)
                {
                    playerFrame = Time.frameCount;
                    playerNames.Clear();
                    foreach (var player in UnityEngine.Object.FindObjectsOfType<Player>())
                    {
                        if (player == null || player.Aircraft == null) continue;
                        string name = player.GetPlayerName()?.SanitizedName;
                        if (!string.IsNullOrEmpty(name)) playerNames[player.Aircraft] = name;
                    }
                }
                return playerNames.TryGetValue(unit, out string found) ? found : null;
            }
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
