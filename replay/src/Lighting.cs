using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Replay-time lighting: time of day, sun direction, haze and weather (cosmetic only, client side). docs/LIGHTING.md
    // has the research behind it.
    //
    // The game lights the scene from LevelInfo's SyncVars timeOfDay, conditions (weather) and cloudHeight, which the
    // recorded server stream keeps writing. While an override is active:
    //   - the three fields hold the override (or the recorded value for a key left "as recorded"). A prefix on
    //     LevelInfo.DeserializeSyncVars puts the recorded values back before the stream's update is read, and a
    //     postfix keeps what the stream wrote as the new recorded values and writes the override again. So
    //     everything that reads the fields follows the override (sun, sky, clouds, building lights), and
    //     "as recorded" writes back exactly what the recording says now;
    //   - a prefix on LevelInfo.UpdateTimeOfDayLighting forces the update every frame (the game runs it once a
    //     second) and turns worldAxis about the world up axis to put the sun at `sun_azimuth`. The game keeps the y
    //     angle, so the turn persists until it is restored;
    //   - a postfix scales RenderSettings.fogDensity by `fog` (only when the game's update really ran);
    //   - a weather change rebuilds the cloud layer's weather set at once (it would otherwise wait up to a second).
    //
    // Two layers: the shot list's (the director sets it per shot; render jobs only have this one) and the viewer's
    // Lighting panel (F2). They merge key by key, the shot list winning. With neither carrying a key, nothing is
    // patched and no lighting code runs, so old shot lists render exactly as before. Nothing here touches the
    // recorder or the recorded stream: the recording still says the same thing.
    internal static class LightingOverride
    {
        private static ManualLogSource log;
        private static Harmony harmony;
        private static MethodInfo updateLighting, updateWeather;
        private static FieldInfo cloudLayerField;
        private static AccessTools.FieldRef<LevelInfo, float> lightingLastUpdated;
        private static ShotLighting shotLayer, viewerLayer, want;
        private static LevelInfo level;                      // the LevelInfo whose recorded values are known
        private static float recTime, recWeather, recCloud;
        private static bool haveRecorded, wroteFields;
        private static float savedYaw = float.NaN;
        private static LevelInfo yawLevel;
        private static float? cameraAz;                      // locked azimuth for sun_azimuth "camera"
        private static int setFrame;
        private static float appliedWeather = float.NaN;

        internal static bool Active => want != null;
        internal static ShotLighting Effective => want;
        internal static ShotLighting Viewer => viewerLayer;
        internal static bool ShotListLighting => shotLayer != null;

        internal static void Init(ManualLogSource logger) => log = logger;

        // ---------------- JSON ----------------

        internal static bool Mentions(JObject o) => o != null && ShotLighting.Keys.Any(k => o[k] != null && o[k].Type != JTokenType.Null);

        internal static ShotLighting FromJson(JObject o)
        {
            if (!Mentions(o)) return null;
            var s = new ShotLighting();
            foreach (var k in ShotLighting.Keys)
            {
                var v = o[k];
                if (v == null || v.Type == JTokenType.Null) continue;
                double? number = v.Type == JTokenType.Integer || v.Type == JTokenType.Float ? (double)v : (double?)null;
                string text = v.Type == JTokenType.String ? (string)v : null;
                if (!s.Set(k, number, text)) log?.LogWarning("Lighting: ignored " + k + " = " + v.ToString(Newtonsoft.Json.Formatting.None));
            }
            return s.Any ? s : null;
        }

        // A shot's lighting merged with the shot list's root "lighting" default (the shot wins key by key).
        internal static ShotLighting Parse(JObject shot, JObject defaults) => ShotLighting.Merge(FromJson(shot), FromJson(defaults));

        internal static void Write(ShotLighting s, JObject into)
        {
            if (s == null || into == null) return;
            foreach (var p in s.Pairs()) into[p.Key] = JToken.FromObject(p.Value);
        }

        internal static JObject ToJson(ShotLighting s)
        {
            if (s == null || !s.Any) return null;
            var o = new JObject();
            Write(s, o);
            return o;
        }

        // ---------------- layers ----------------

        // The director, when a shot starts (null: the shot list has no lighting for it). Applies at once, so the sun
        // is already where it will render when the cine camera sets up this same frame (backlight placement reads it).
        internal static void SetShot(ShotLighting s, string label)
        {
            if (s != null && !s.Any) s = null;
            if (s == null && shotLayer == null) return;
            shotLayer = s;
            cameraAz = null;
            Apply(label, newShot: true);
        }

        // The viewer's Lighting panel (null or empty: everything as recorded).
        internal static void SetViewer(ShotLighting s, string label)
        {
            if (s != null && !s.Any) s = null;
            if (s == null && viewerLayer == null) return;
            var before = viewerLayer;
            viewerLayer = s?.Clone();
            if (before == null || s == null || before.SunCamera != s.SunCamera || before.SunCameraOff != s.SunCameraOff) cameraAz = null;
            Apply(label, newShot: false);
        }

        // A new replay or the viewer closing: everything back to the recording.
        internal static void Reset(string label)
        {
            if (shotLayer == null && viewerLayer == null) return;
            shotLayer = null; viewerLayer = null;
            Apply(label, newShot: false);
        }

        private static void Apply(string label, bool newShot)
        {
            var w = ShotLighting.Merge(shotLayer, viewerLayer);
            if (w == null && want == null) return;
            if (w != null && w.Same(want) && !newShot) return;
            try
            {
                if (w != null) EnsurePatched();
                bool sunWas = want != null && (want.SunAzimuth.HasValue || want.SunCamera);
                want = w;
                setFrame = Time.frameCount;
                var lv = NetworkSceneSingleton<LevelInfo>.i;
                if (lv == null) return;
                Bind(lv);
                if (sunWas && (w == null || (!w.SunAzimuth.HasValue && !w.SunCamera))) RestoreYaw(lv);
                WriteFields(lv);
                Force(lv);
                ApplyWeather(lv);
                if (w == null) wroteFields = false;
                log?.LogInfo("Lighting " + (w == null ? "restored to the recording" : "override: " + w) + " (" + label + "); " + Describe(lv));
            }
            catch (Exception e) { log?.LogWarning("Lighting override failed: " + e.Message); want = null; }
        }

        // Every frame, after the director: binds a LevelInfo that a scene reload made and keeps the override on it.
        internal static void Tick(bool live)
        {
            if (want == null || !live) return;
            var lv = NetworkSceneSingleton<LevelInfo>.i;
            if (lv == null) return;
            if (lv != level)
            {
                Bind(lv);
                WriteFields(lv);
                Force(lv);
                ApplyWeather(lv);
                log?.LogInfo("Lighting: new level, override " + want + "; " + Describe(lv));
            }
        }

        // ---------------- the recorded values ----------------

        private static void Bind(LevelInfo lv)
        {
            if (lv == level) return;
            level = lv;
            recTime = lv.timeOfDay; recWeather = lv.conditions; recCloud = lv.cloudHeight;
            haveRecorded = true;
            wroteFields = false;
            appliedWeather = float.NaN;
            savedYaw = float.NaN; yawLevel = null;
            try
            {
                cloudLayerField = cloudLayerField ?? AccessTools.Field(typeof(LevelInfo), "cloudLayer");
                var cl = cloudLayerField?.GetValue(lv) as CloudLayer;
                var sets = cl != null ? AccessTools.Field(typeof(CloudLayer), "weatherSets")?.GetValue(cl) as WeatherSet[] : null;
                if (sets != null)
                    log?.LogInfo("Lighting: weather sets " + string.Join(", ", sets.Select((ws, i) => string.Format(CultureInfo.InvariantCulture,
                        "{0:0.##}-{1:0.##} {2} (coverage {3:0.##})", (float)i / sets.Length, (float)(i + 1) / sets.Length, ws != null ? ws.displayName : "none", ws != null ? ws.coverage : 0f))));
            }
            catch (Exception e) { log?.LogInfo("Lighting: could not list weather sets: " + e.Message); }
        }

        // The recording's own values right now (NaN with no level).
        internal static float RecordedTime()
        {
            var lv = NetworkSceneSingleton<LevelInfo>.i;
            if (lv == null) return float.NaN;
            return lv == level && haveRecorded ? recTime : lv.timeOfDay;
        }

        private static void WriteFields(LevelInfo lv)
        {
            if (lv != level || !haveRecorded) return;
            var w = want;
            if (w == null)
            {
                if (!wroteFields) return;
                lv.timeOfDay = recTime; lv.conditions = recWeather; lv.cloudHeight = recCloud;
                return;
            }
            lv.timeOfDay = w.TimeOfDay ?? recTime;
            lv.conditions = w.Weather ?? recWeather;
            lv.cloudHeight = w.CloudHeight ?? recCloud;
            wroteFields = true;
        }

        private static void ApplyWeather(LevelInfo lv)
        {
            float now = lv.conditions;
            if (now == appliedWeather) return;
            appliedWeather = now;
            try
            {
                cloudLayerField = cloudLayerField ?? AccessTools.Field(typeof(LevelInfo), "cloudLayer");
                var cl = cloudLayerField?.GetValue(lv) as CloudLayer;
                if (cl == null) return;
                updateWeather = updateWeather ?? AccessTools.Method(typeof(CloudLayer), "UpdateWeatherSets");
                updateWeather?.Invoke(cl, null);
            }
            catch (Exception e) { log?.LogInfo("Lighting: weather update failed: " + e.Message); }
        }

        // ---------------- camera hooks ----------------

        // Cine cameras report their pose once per frame; "camera" sun azimuth locks to the first posed frame of a shot.
        internal static void CameraPosed(Transform cam)
        {
            if (want == null || !want.SunCamera || cameraAz.HasValue || cam == null) return;
            Vector3 f = cam.forward; f.y = 0;
            if (f.sqrMagnitude < 1e-6f) return;
            cameraAz = Azimuth(f) + want.SunCameraOff;
            var lv = NetworkSceneSingleton<LevelInfo>.i;
            if (lv != null) Force(lv);
            log?.LogInfo(string.Format(CultureInfo.InvariantCulture, "Lighting: sun locked {0:F1} deg right of the camera view; {1}", want.SunCameraOff, Describe(lv)));
        }

        internal static Vector3 TowardSun()
        {
            var lv = NetworkSceneSingleton<LevelInfo>.i;
            return lv != null && lv.sun != null ? -lv.sun.transform.forward : Vector3.up;
        }

        internal static string Describe(LevelInfo lv)
        {
            if (lv == null || lv.sun == null) return "no level";
            Vector3 s = -lv.sun.transform.forward;
            return string.Format(CultureInfo.InvariantCulture,
                "sun azimuth {0:F1} elevation {1:F1}, time {2:F2} h (recorded {3:F2}), weather {4:F2}, cloud height {5:F0} m, fog density {6:E2}, sun light {7}",
                Azimuth(s), Elevation(s), lv.timeOfDay, lv == level && haveRecorded ? recTime : lv.timeOfDay, lv.conditions, lv.cloudHeight,
                RenderSettings.fogDensity, lv.sun.gameObject.activeSelf ? "on" : "off");
        }

        internal static string Status()
        {
            var lv = NetworkSceneSingleton<LevelInfo>.i;
            return (want == null ? "as recorded" : want.ToString()) + "; " + Describe(lv);
        }

        internal static System.Numerics.Vector3 N(Vector3 v) => new System.Numerics.Vector3(v.x, v.y, v.z);
        internal static Vector3 U(System.Numerics.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
        internal static float Azimuth(Vector3 v) => BacklightMath.Azimuth(N(v));
        internal static float Elevation(Vector3 v) => BacklightMath.Elevation(N(v));

        // ---------------- patches ----------------

        private static void Force(LevelInfo lv)
        {
            if (updateLighting == null) updateLighting = AccessTools.Method(typeof(LevelInfo), "UpdateTimeOfDayLighting");
            updateLighting?.Invoke(lv, new object[] { true });
        }

        private static void RestoreYaw(LevelInfo lv)
        {
            if (float.IsNaN(savedYaw) || lv.worldAxis == null || yawLevel != lv) { savedYaw = float.NaN; return; }
            var axis = lv.worldAxis.transform;
            var e = axis.eulerAngles;
            axis.eulerAngles = new Vector3(e.x, savedYaw, e.z);
            savedYaw = float.NaN;
        }

        private static void EnsurePatched()
        {
            if (harmony != null) return;
            harmony = new Harmony(ReplayPlugin.Id + ".lighting");
            lightingLastUpdated = AccessTools.FieldRefAccess<LevelInfo, float>("lightingLastUpdated");
            harmony.Patch(AccessTools.Method(typeof(LevelInfo), "UpdateTimeOfDayLighting"),
                prefix: new HarmonyMethod(typeof(LightingOverride), nameof(LightingPrefix)),
                postfix: new HarmonyMethod(typeof(LightingOverride), nameof(LightingPostfix)));
            harmony.Patch(AccessTools.Method(typeof(LevelInfo), nameof(LevelInfo.DeserializeSyncVars)),
                prefix: new HarmonyMethod(typeof(LightingOverride), nameof(SyncPrefix)),
                postfix: new HarmonyMethod(typeof(LightingOverride), nameof(SyncPostfix)));
            log?.LogInfo("Lighting: patched LevelInfo.UpdateTimeOfDayLighting and LevelInfo.DeserializeSyncVars");
        }

        // The stream's update lands on the recorded values, never on the override.
        private static void SyncPrefix(LevelInfo __instance)
        {
            if (__instance == null || __instance != level || !haveRecorded || !wroteFields) return;
            __instance.timeOfDay = recTime; __instance.conditions = recWeather; __instance.cloudHeight = recCloud;
        }

        private static void SyncPostfix(LevelInfo __instance)
        {
            if (__instance == null) return;
            if (__instance != level)
            {
                if (want == null) return;
                Bind(__instance);
            }
            recTime = __instance.timeOfDay; recWeather = __instance.conditions; recCloud = __instance.cloudHeight;
            haveRecorded = true;
            if (want != null) WriteFields(__instance);
            else wroteFields = false;
        }

        private static void LightingPrefix(LevelInfo __instance, ref bool forceImmediate, out float __state)
        {
            __state = float.NaN;
            var w = want;
            if (w == null || __instance == null) return;
            forceImmediate = true;
            // Marks the call: the game writes the time back only when it really updates (it skips with the camera
            // under sea level), and the postfix then knows whether the fog density is fresh.
            if (lightingLastUpdated != null) { __state = lightingLastUpdated(__instance); lightingLastUpdated(__instance) = float.MinValue; }
            if (yawLevel != __instance) { yawLevel = __instance; savedYaw = float.NaN; }
            // Game camera states do not report their pose; lock "camera" to the main camera a frame after the cut.
            if (w.SunCamera && !cameraAz.HasValue && Time.frameCount > setFrame + 1 && CameraStateManager.i != null)
            {
                Vector3 f = CameraStateManager.i.transform.forward; f.y = 0;
                if (f.sqrMagnitude > 1e-6f) cameraAz = Azimuth(f) + w.SunCameraOff;
            }
            float? az = w.SunCamera ? cameraAz : w.SunAzimuth;
            if (az.HasValue && __instance.worldAxis != null && __instance.sun != null)
            {
                var axis = __instance.worldAxis.transform;
                var e = axis.eulerAngles;
                if (float.IsNaN(savedYaw)) savedYaw = e.y;
                float z = __instance.timeOfDay * 15f + 180f;   // what the game is about to set
                axis.eulerAngles = new Vector3(e.x, savedYaw, z);
                float az0 = Azimuth(-__instance.sun.transform.forward);
                axis.eulerAngles = new Vector3(e.x, savedYaw + Mathf.DeltaAngle(az0, az.Value), z);
            }
        }

        private static void LightingPostfix(LevelInfo __instance, float __state)
        {
            if (__instance == null || float.IsNaN(__state)) return;
            if (lightingLastUpdated(__instance) == float.MinValue)
            {
                // Skipped: put the time back and leave the fog alone, so the multiplier never compounds.
                lightingLastUpdated(__instance) = __state;
                return;
            }
            var w = want;
            if (w != null && w.Fog.HasValue) RenderSettings.fogDensity *= w.Fog.Value;
        }
    }
}
