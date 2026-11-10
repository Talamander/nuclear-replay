using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using Mirage.RemoteCalls;
using Mirage.Serialization;
using NuclearOption.Networking;
using NuclearOption.NetworkTransforms;
using NuclearOption.SceneLoading;
using NuclearReplay.Demo;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // One jump: the prelude, then the burst that rebuilds the world at the cut, and where normal playback goes on.
    //
    // The client must have its scene loaded before the burst can be made, because scene objects and prefabs name the
    // RPCs. So the prelude goes first; BeginBurst names the RPCs on the main thread and builds the burst off it.
    internal sealed class JumpPlan
    {
        // True while the client handles a burst. Do not retain radar references across bursts or an aborted replay.
        private static bool burstActive;
        public static bool BurstActive
        {
            get => burstActive;
            set { burstActive = value; JumpRadarPatches.Clear(); }
        }

        private readonly ManualLogSource log;
        private readonly JumpIndex index;
        private readonly List<DemoRecord> records;
        private readonly JumpCut cut;
        private volatile bool buildDone;

        public List<DemoRecord> Prelude { get; }
        public int ResumeIndex => cut.ResumeIndex;
        public double CutTime => cut.Time;
        public double CutNetTime => cut.NetTime;
        public JumpBurst Burst { get; private set; }
        public string Failure { get; private set; }
        public bool BuildDone => buildDone;
        // Objects the client can spawn: the unit count check after the burst.
        public int ExpectedObjects { get; private set; }
        public bool FailBuild, FailSend, StallSend, CorruptBurst;

        private JumpPlan(ManualLogSource log, JumpIndex index, List<DemoRecord> records, JumpCut cut)
        {
            this.log = log;
            this.index = index;
            this.records = records;
            this.cut = cut;
            Prelude = index.Prelude(cut);
        }

        // Null when the recording has no map load before the cut.
        public static JumpPlan Create(ManualLogSource log, JumpIndex index, List<DemoRecord> records, double cutTime)
        {
            var cut = index.Cut(cutTime);
            return cut == null ? null : new JumpPlan(log, index, records, cut);
        }

        // True when the game's message ids still match the ones JumpIndex reads.
        public static string WireMismatch()
        {
            var transform = AccessTools.Inner(typeof(SendTransformBatcher), "TransformMessage");
            if (transform == null || transform.FullName != JumpWire.TransformType) return "transform message type";
            if (MessagePacker.GetId<LoadMapMessage>() != JumpWire.LoadMapId) return nameof(LoadMapMessage);
            if (MessagePacker.GetId<SpawnMessage>() != JumpWire.SpawnId) return nameof(SpawnMessage);
            if (MessagePacker.GetId<ObjectDestroyMessage>() != JumpWire.DestroyId) return nameof(ObjectDestroyMessage);
            if (MessagePacker.GetId<ObjectHideMessage>() != JumpWire.HideId) return nameof(ObjectHideMessage);
            if (MessagePacker.GetId<UpdateVarsMessage>() != JumpWire.VarsId) return nameof(UpdateVarsMessage);
            if (MessagePacker.GetId<RpcMessage>() != JumpWire.RpcId) return nameof(RpcMessage);
            return null;
        }

        public void BeginBurst()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var names = new RpcNames(log);
            var tables = new Dictionary<long, JumpRpcRule[]>();
            foreach (var o in index.LiveObjects(cut))
            {
                if (!tables.TryGetValue(o.TableKey, out var table)) tables[o.TableKey] = table = names.Rules(o.SceneId, o.PrefabHash);
                if (table != null) ExpectedObjects++;
            }
            foreach (var key in tables.Where(t => t.Value == null).Select(t => t.Key).ToList()) tables.Remove(key);
            log.LogInfo($"Jump plan: {tables.Count} RPC tables named on the main thread in {watch.ElapsedMilliseconds} ms");
            var thread = new Thread(() =>
            {
                try
                {
                    if (FailBuild) throw new InvalidOperationException("test failure");
                    Burst = index.BuildBurst(cut, tables);
                }
                catch (Exception e) { Failure = e.ToString(); }
                buildDone = true;
            }) { IsBackground = true, Name = "NuclearReplay jump burst" };
            thread.Start();
        }

        // Called on the main thread once BuildDone is true.
        public void LogBurst()
        {
            log.LogInfo("Jump plan: " + Burst.Summary);
            if (Burst.Unnamed.Count == 0) return;
            log.LogWarning($"Jump plan: {Burst.Unnamed.Values.Sum()} RPCs on {Burst.Unnamed.Count} unknown prefabs left out: "
                + string.Join(", ", Burst.Unnamed.OrderByDescending(u => u.Value).Take(6).Select(u => $"{u.Key} ({u.Value})")));
        }

        // A stopped vehicle is kinematic on the client and ignores transform snapshots (GroundVehicleNetworkTransform),
        // so one that drove and parked would stay at its spawn. Places those directly from their last recorded transform.
        public int PlaceStoppedVehicles(NetworkWorld world)
        {
            int placed = 0;
            foreach (var pair in Burst.LastTransforms)
            {
                if (!world.TryGetIdentity(pair.Key, out var identity) || identity == null) continue;
                var vehicle = identity.GetComponent<GroundVehicle>();
                if (vehicle == null || vehicle.rb == null || !vehicle.rb.isKinematic) continue;
                using var reader = NetworkReaderPool.GetReader(records[pair.Value.Record].Payload, null);
                reader.MoveBitPosition(pair.Value.BitPosition);
                reader.ReadPackedUInt32();
                var position = reader.ReadGlobalPosition().ToLocalPosition();
                var rotation = reader.Read<Quaternion>();
                vehicle.transform.SetPositionAndRotation(position, rotation);
                vehicle.rb.position = position;
                vehicle.rb.rotation = rotation;
                placed++;
            }
            return placed;
        }

        // Names RPCs from the spawn that made each object: the scene object or prefab holds the same RPC table.
        private sealed class RpcNames
        {
            private readonly ManualLogSource log;
            private readonly NetworkMap[] maps;
            private readonly Dictionary<ulong, NetworkIdentity> scene = new Dictionary<ulong, NetworkIdentity>();

            public RpcNames(ManualLogSource log)
            {
                this.log = log;
                maps = UnityEngine.Object.FindObjectsOfType<NetworkMap>();
                foreach (var identity in Resources.FindObjectsOfTypeAll<NetworkIdentity>())
                    if (identity.gameObject.scene.IsValid() && identity.SceneId != 0) scene[identity.SceneId] = identity;
            }

            // Objects built into the map spawn by a hash of the map prefix (top byte) and their NetworkObjects index.
            private NetworkIdentity MapObject(int hash)
            {
                foreach (var map in maps)
                {
                    int index = hash & 0xFFFFFF;
                    if ((hash >> 24 & 0xFF) == map.MapPrefix && index < map.NetworkObjects.Length) return map.NetworkObjects[index];
                }
                return null;
            }

            // Null for an object this game cannot spawn: a recording from another game build or mod set can hold
            // prefabs that this copy does not have. The client skips those spawns too.
            public JumpRpcRule[] Rules(ulong? sceneId, int? prefabHash)
            {
                NetworkIdentity identity = null;
                if (sceneId.HasValue && sceneId.Value != 0) scene.TryGetValue(sceneId.Value, out identity);
                else if (prefabHash.HasValue)
                {
                    identity = MapObject(prefabHash.Value);
                    if (identity == null)
                    {
                        try { identity = NetworkManagerNuclearOption.i.ClientObjectManager.GetSpawnHandler(prefabHash.Value).Prefab; }
                        catch (SpawnObjectException) { return null; }
                    }
                }
                if (identity == null) return null;
                try { return identity.RemoteCallCollection.RemoteCalls.Select(c => JumpRpcRules.Rule(c?.Name)).ToArray(); }
                catch (Exception e) { log.LogWarning($"Jump plan: rpc table of {identity.name}: {e.Message}"); return null; }
            }
        }
    }

    // Rearming replaces a radar immediately, but the old Radar.OnDestroy clears it at the end of the frame.
    // In a burst that cleanup can overwrite a later recorded toggle. Let the game's cleanup run, then restore
    // only toggles recorded after the last replacement; a radar with no later toggle keeps the game's state.
    [HarmonyPatch(typeof(Radar), nameof(Radar.AttachToUnit))]
    internal static class JumpRadarPatches
    {
        private static readonly JumpRadarState<Aircraft> Recorded = new JumpRadarState<Aircraft>();

        internal static void Clear() => Recorded.Clear();

        [HarmonyPostfix]
        private static void Replaced(Unit unit)
        {
            if (JumpPlan.BurstActive && unit is Aircraft aircraft) Recorded.Replaced(aircraft);
        }

        internal static void RestoreRecordedState()
        {
            foreach (var aircraft in UnitRegistry.allAircraft)
                if (aircraft != null && aircraft.radar != null)
                    aircraft.radar.activated = Recorded.Restore(aircraft, aircraft.radar.activated);
        }

        [HarmonyPatch]
        internal static class Toggles
        {
            private static IEnumerable<MethodBase> TargetMethods()
                => AccessTools.GetDeclaredMethods(typeof(Aircraft)).Where(m => m.Name.StartsWith("UserCode_RpcToggleRadar"));

            private static void Postfix(Aircraft __instance, bool activated)
            {
                if (JumpPlan.BurstActive) Recorded.RecordToggle(__instance, activated);
            }
        }
    }

    // Landing gear is a SyncVar (Aircraft.gearDeployed), and the game ignores a change while the gear is still moving
    // (GearStateChanged returns early on Extending or Retracting). Normal playback spaces the changes seconds apart, but a
    // burst delivers every change since the spawn in one frame, so only the first took effect: an aircraft that spawned
    // on the runway and took off could fly on with its gear down. While a burst runs the gear goes straight to the
    // recorded value, locked, without the fold animation (or its sound).
    [HarmonyPatch(typeof(Aircraft), "GearStateChanged")]
    internal static class JumpGearPatches
    {
        [HarmonyPrefix]
        private static bool SnapGear(Aircraft __instance, bool gearDeployed)
        {
            if (!JumpPlan.BurstActive) return true;
            __instance.SetGear(gearDeployed ? LandingGear.GearState.LockedExtended : LandingGear.GearState.LockedRetracted);
            return false;
        }
    }

    // A burst replays launches and rearms in one frame, so a launcher would skip those inside its fire interval, and a
    // rearm that waits for its reload time would land long after the shots it came before. While a burst runs, the
    // recorded server counts decide instead: launchers do not count their own launches, each count goes straight to
    // the weapons, and a rearm after the last count adds at once.
    [HarmonyPatch]
    internal static class JumpAmmoPatches
    {
        private static readonly AccessTools.FieldRef<MissileLauncher, int> ReloadingAmmo = AccessTools.FieldRefAccess<MissileLauncher, int>("reloadingAmmo");

        [HarmonyPatch(typeof(MissileLauncher), nameof(MissileLauncher.Fire))]
        [HarmonyPrefix]
        private static bool SkipLaunch() => !JumpPlan.BurstActive;

        [HarmonyPatch(typeof(MissileLauncher), nameof(MissileLauncher.Rearm))]
        [HarmonyPrefix]
        private static bool RearmAtOnce(MissileLauncher __instance, int ammoToRearm, WeaponStation weaponStation)
        {
            if (!JumpPlan.BurstActive) return true;
            // Do not start Reload's coroutine, even for a zero-round initialization. Its delayed AccountAmmo would
            // overwrite a newer recorded count during the run-in after the pending ammo was already applied here.
            __instance.SetWeaponStation(weaponStation);
            __instance.ammo += ReloadingAmmo(__instance) + ammoToRearm;
            ReloadingAmmo(__instance) = 0;
            return false;
        }

        [HarmonyPatch]
        internal static class Counts
        {
            private static IEnumerable<MethodBase> TargetMethods()
                => AccessTools.GetDeclaredMethods(typeof(Unit)).Where(m => m.Name.StartsWith("UserCode_RpcSyncAmmoCount") || m.Name.StartsWith("UserCode_RpcSingleRemoteFire"));

            private static void Postfix(Unit __instance, byte stationIndex, int ammo)
            {
                if (!JumpPlan.BurstActive) return;
                int extra = 0;
                var weapons = __instance.weaponStations[stationIndex].Weapons;
                foreach (var weapon in weapons) if (weapon is MissileLauncher || weapon is Gun) extra += weapon.ammo;
                extra -= ammo;
                foreach (var weapon in weapons)
                {
                    if (extra == 0 || !(weapon is MissileLauncher || weapon is Gun)) continue;
                    int take = extra > 0 ? Math.Min(extra, weapon.ammo) : extra;
                    weapon.ammo -= take;
                    extra -= take;
                }
            }
        }
    }
}
