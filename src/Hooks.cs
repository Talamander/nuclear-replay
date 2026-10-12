using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay
{
    internal static class Hooks
    {
        [ThreadStatic] internal static string DamageProjectile;
        private static Plugin P => Plugin.Instance;
        // Telemetry observers run only while the bundle writer is on; demo event annotations do not depend on it.
        private static void Observe(string name, Action action) { var p = P; if (p != null && p.Telemetry) p.Safe(name, action); }

        // telemetry false installs only the hooks the demo event annotations share (launch, damage scopes, damage credit).
        internal static void Install(Harmony harmony, Dictionary<string, bool> status, bool telemetry = true)
        {
            void Patch(string label, Type type, string method, string prefix = null, string postfix = null, string finalizer = null, Type[] arguments = null)
            {
                try
                {
                    var original = AccessTools.Method(type, method, arguments);
                    if (original == null) { status[label] = false; P.HookFailures[label] = "Method not found: " + type.FullName + "." + method; return; }
                    HarmonyMethod Hook(string name) => name == null ? null : new HarmonyMethod(typeof(Hooks), name);
                    harmony.Patch(original, Hook(prefix), Hook(postfix), null, Hook(finalizer), null);
                    status[label] = true;
                }
                catch (Exception error) { status[label] = false; P.HookFailures[label] = error.ToString(); }
            }
            var suffix = new[] { typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Unit) };
            Patch("launch_definition", typeof(Spawner), "SpawnMissile", "SpawnPrefix", "SpawnPostfix", arguments: new[] { typeof(MissileDefinition) }.Concat(suffix).ToArray());
            Patch("launch_prefab", typeof(Spawner), "SpawnMissile", "SpawnPrefix", "SpawnPostfix", arguments: new[] { typeof(GameObject) }.Concat(suffix).ToArray());
            Patch("collision_damageable", typeof(Missile), "PenetrateObject", "ImpactPrefix", finalizer: "DamageScopeFinalizer");
            Patch("projectile_fragment_damage_scope", typeof(DamageEffects), "BlastFrag", "BlastPrefix", finalizer: "DamageScopeFinalizer");
            Patch("damage_credit", typeof(Unit), "RecordDamage", postfix: "CreditPostfix");
            if (!telemetry) return;
            Patch("selection", typeof(WeaponManager), "TargetListChanged", postfix: "SelectionPostfix");
            Patch("weapon_selection", typeof(WeaponManager), "SetActiveStation", postfix: "SelectionPostfix");
            Patch("missile_target_changes", typeof(Missile), "TargetIDChanged", postfix: "TargetPostfix");
            Patch("detonation", typeof(Missile), "UserCode_RpcDetonate_897349600", postfix: "DetonatePostfix");
            Patch("part_damage", typeof(Unit), "UserCode_RpcDamage_1709046923", "PartDamagePrefix", "PartDamagePostfix");
            Patch("destruction_report", typeof(Unit), "ReportKilled", prefix: "KilledPrefix");
            Patch("entity_removal", typeof(Unit), "OnDestroy", prefix: "RemovePrefix");
            Patch("unit_disabled", typeof(Unit), "UnitDisabled", postfix: "DisabledPostfix");
            Patch("mission_selection", typeof(MissionManager), "SetMission", postfix: "MissionPostfix");
            Patch("mission_start", typeof(MissionManager), "StartMission", prefix: "StartMissionPrefix");
            Patch("registry_registration", typeof(UnitRegistry), "RegisterUnit", postfix: "RegisteredPostfix", arguments: new[] { typeof(Unit), typeof(PersistentID) });
            Patch("missile_network_start", typeof(Missile), "OnStartNetwork", postfix: "MissileStartedPostfix", arguments: Type.EmptyTypes);
            Patch("client_launch_request", typeof(Aircraft), "CmdLaunchMissile", prefix: "RequestPrefix", arguments: new[] { typeof(byte), typeof(Unit), typeof(GlobalPosition) });
            Patch("replicated_launch_animation", typeof(Aircraft), "UserCode_RpcLaunchMissile_1465828762", prefix: "LaunchAnimationPrefix", arguments: new[] { typeof(byte), typeof(Unit), typeof(GlobalPosition) });
        }

        private static void SpawnPrefix(Unit owner, Unit target, Vector3 launchPosition, out JObject __state)
        {
            JObject state = null;
            Observe("launch_snapshot", () => state = P.SnapshotLaunch(owner, target, launchPosition));
            __state = state;
        }
        private static void SpawnPostfix(Missile __result, Unit owner, Unit target, JObject __state)
        {
            Demo.DemoRecorder.OnLaunch(__result, owner, target);
            Observe("launch", () => P.LaunchObserved(__result, __state));
        }
        private static void SelectionPostfix(WeaponManager __instance)
        {
            Observe("selection", () =>
            {
                if (GameManager.GetLocalAircraft(out var player) && player.weaponManager == __instance)
                    P.SelectionChanged("WeaponManager hook");
            });
        }
        private static void TargetPostfix(Missile __instance, PersistentID oldValue, PersistentID newValue) =>
            Observe("missile_target_changes", () => P.SeekerChanged(__instance, oldValue, newValue));
        private static void DetonatePostfix(Missile __instance, Unit relativeUnit, bool useUnit, Vector3 pos, bool armed, bool hitArmor, bool hitTerrain, Vector3 normal) =>
            Observe("detonation", () => P.Detonation(__instance, armed, hitArmor, hitTerrain, relativeUnit, useUnit, pos, normal));
        private static void RegisteredPostfix(Unit unit) => Observe("registry_registration", () => P.Registered(unit, "UnitRegistry.RegisterUnit"));
        private static void MissileStartedPostfix(Missile __instance) => Observe("missile_network_start", () => P.Registered(__instance, "Missile.OnStartNetwork"));
        private static void RequestPrefix(Aircraft __instance, byte stationIndex, Unit target, GlobalPosition aimpoint) =>
            Observe("client_launch_request", () => P.LaunchRequest(__instance, stationIndex, target, aimpoint, "Aircraft.CmdLaunchMissile"));
        private static void LaunchAnimationPrefix(Aircraft __instance, byte stationIndex, Unit target, GlobalPosition aimpoint) =>
            Observe("replicated_launch_animation", () => P.LaunchRequest(__instance, stationIndex, target, aimpoint, "Aircraft.UserCode_RpcLaunchMissile_1465828762"));

        private static void ImpactPrefix(Missile __instance, IDamageable damageable, Vector3 hitPoint, out (string, string) __state)
        {
            __state = (DamageProjectile, Demo.DemoRecorder.DamageWeapon);
            Demo.DemoRecorder.EnterWeaponHit();
            if (Demo.DemoRecorder.Recording) Demo.DemoRecorder.DamageWeapon = Demo.DemoRecorder.MissileWeapon(__instance);
            Observe("collision_damageable", () =>
            {
                string id = P.EntityId(__instance);
                DamageProjectile = P.Ledger.Observation(id) != null ? id : null;
                P.Impact(__instance, damageable, hitPoint);
            });
        }
        private static void BlastPrefix(PersistentID missileID, out (string, string) __state)
        {
            __state = (DamageProjectile, Demo.DemoRecorder.DamageWeapon);
            Demo.DemoRecorder.EnterWeaponHit();
            // A blast without a missile (explosive gun rounds) keeps the outer scope's weapon.
            var weapon = Demo.DemoRecorder.MissileWeapon(missileID);
            if (weapon != null) Demo.DemoRecorder.DamageWeapon = weapon;
            Observe("projectile_fragment_damage_scope", () => DamageProjectile = P.ProjectileId(missileID));
        }
        // A void finalizer restores observation context even when the game throws; it never suppresses its exception.
        private static void DamageScopeFinalizer((string, string) __state)
        {
            DamageProjectile = __state.Item1;
            Demo.DemoRecorder.DamageWeapon = __state.Item2;
            Demo.DemoRecorder.ExitWeaponHit();
        }
        private static void CreditPostfix(Unit __instance, PersistentID lastDamagedBy, float damageAmount)
        {
            Demo.DemoRecorder.OnDamageCredit(__instance, lastDamagedBy);
            Observe("damage_credit", () => P.DamageCredit(__instance, lastDamagedBy, damageAmount));
        }
        private static void PartDamagePrefix(Unit __instance, byte index, out JObject __state)
        {
            JObject snapshot = null;
            Observe("part_damage", () => snapshot = P.PartDamageBefore(__instance, index));
            __state = snapshot;
        }
        private static void PartDamagePostfix(Unit __instance, byte index, DamageInfo damageInfo, JObject __state) =>
            Observe("part_damage", () => P.PartDamageAfter(__instance, index, damageInfo, __state));
        private static void KilledPrefix(Unit __instance) => Observe("destruction_report", () => P.Destruction(__instance));
        private static void RemovePrefix(Unit __instance) => Observe("entity_removal", () => P.Removed(__instance));
        private static void DisabledPostfix(Unit __instance, bool nowDisabled)
        {
            Observe("unit_disabled", () =>
            {
                if (nowDisabled && P.IsWatched(__instance))
                    P.Emit("unit_disabled", new JObject { ["unit"] = P.Entity(__instance), ["kill_attribution"] = "unknown" });
            });
        }
        private static void MissionPostfix() => Observe("mission_selection", () => P.BeginMission("MissionManager.SetMission", false));
        private static void StartMissionPrefix() => Observe("mission_start", () => P.BeginMission("MissionManager.StartMission", true));
    }
}
