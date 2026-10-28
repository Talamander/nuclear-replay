using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NuclearReplay.Playback
{
    // Shot list side of the lens pack: lens fields of a shot (LensMath.cs reads them) and focal lengths in mm turned into
    // the field of view the cameras use.
    internal static class LensShots
    {
        // Aspect of the frame being rendered (the screen; a render job sets its size before the first shot runs).
        public static double Aspect => Screen.height > 0 ? Screen.width / (double)Screen.height : 16.0 / 9.0;

        public static LensSettings FromShot(JObject raw) => raw == null ? null : LensSettings.FromShot(Plain(raw));

        // Vertical field of view for a shot or key with "focal_mm" (null when it has none). The sensor comes from the
        // item itself, else from `sensor` (the shot's, for path keys).
        public static float? FocalFov(JToken item, string sensor = null)
        {
            if (!(item is JObject o) || o["focal_mm"] == null || (o["focal_mm"].Type != JTokenType.Float && o["focal_mm"].Type != JTokenType.Integer)) return null;
            double mm = (double)o["focal_mm"];
            if (!(mm > 0)) return null;
            string id = o["sensor"] != null && o["sensor"].Type == JTokenType.String ? (string)o["sensor"] : sensor;
            mm = LensMath.Clamp(mm, LensMath.MinFocal, LensMath.MaxFocal);
            return (float)LensMath.FovFromFocal(mm, LensMath.SensorOrDefault(id), Aspect);
        }

        // Key "focus": metres or "target" (-1); missing: -1.
        public static float KeyFocus(JToken key)
        {
            var f = key?["focus"];
            if (f == null) return -1;
            if (f.Type == JTokenType.Float || f.Type == JTokenType.Integer) return Mathf.Clamp((float)f, (float)LensMath.MinFocus, (float)LensMath.MaxFocus);
            return -1;
        }

        public static float KeyFStop(JToken key)
        {
            var f = key?["f_stop"];
            if (f == null || f.Type != JTokenType.Float && f.Type != JTokenType.Integer) return 0;
            return Mathf.Clamp((float)f, (float)LensMath.MinFStop, (float)LensMath.MaxFStop);
        }

        // JSON as plain values: objects as dictionaries, numbers as double.
        public static IDictionary<string, object> Plain(JObject o)
        {
            var d = new Dictionary<string, object>();
            foreach (var p in o.Properties()) d[p.Name] = Plain(p.Value);
            return d;
        }

        private static object Plain(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Object: return Plain((JObject)t);
                case JTokenType.Integer:
                case JTokenType.Float: return (double)t;
                case JTokenType.String: return (string)t;
                case JTokenType.Boolean: return (bool)t;
                default: return null;
            }
        }

        public static JObject ToJson(IDictionary<string, object> d)
        {
            var o = new JObject();
            foreach (var p in d) o[p.Key] = p.Value is IDictionary<string, object> inner ? ToJson(inner) : JToken.FromObject(p.Value);
            return o;
        }
    }

    // Sets a camera's field of view. NOVR patches Camera.fieldOfView's setter to do nothing, so when the setter does not
    // take, the lens is applied through the projection matrix of every camera that renders the view, and the LOD bias
    // is raised by the zoom factor so LOD selection (which still sees the camera's own field of view) matches what is on
    // screen. Reset undoes all of it. Used by the cine cameras and the free camera.
    internal sealed class LensFov
    {
        private readonly ManualLogSource log;
        private readonly string name;
        private bool logged, usingMatrix;
        private float savedLodBias = -1;
        private Camera matrixCam;

        public LensFov(ManualLogSource log, string name) { this.log = log; this.name = name; }

        public bool UsingMatrix => usingMatrix;
        public void LogNext() => logged = false;

        public void Apply(CameraStateManager cam, float v)
        {
            var c = cam.mainCamera;
            var r = CineCamera.RenderCamera(c);
            c.fieldOfView = v;
            if (r != c) r.fieldOfView = v;
            cam.desiredFOV = v;
            bool blocked = Mathf.Abs(r.fieldOfView - v) > 0.05f;
            if (blocked)
            {
                var m = Matrix4x4.Perspective(v, r.aspect, r.nearClipPlane, r.farClipPlane);
                r.projectionMatrix = m;
                if (r != c) c.projectionMatrix = Matrix4x4.Perspective(v, c.aspect, c.nearClipPlane, c.farClipPlane);
                if (matrixCam != null && matrixCam != r) matrixCam.ResetProjectionMatrix();
                matrixCam = r;
                if (savedLodBias < 0) savedLodBias = QualitySettings.lodBias;
                float zoom = Mathf.Tan(r.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(v * 0.5f * Mathf.Deg2Rad);
                QualitySettings.lodBias = savedLodBias * Mathf.Max(1f, zoom);
                usingMatrix = true;
            }
            else if (usingMatrix) Reset(c);
            if (!logged && log != null)
            {
                logged = true;
                log.LogInfo(string.Format(CultureInfo.InvariantCulture, "{0} lens: {1:F1} deg on {2}, its field of view reads {3:F1}{4}",
                    name, v, r.name, r.fieldOfView, blocked ? " (setter blocked by a mod; using the projection matrix, LOD bias compensated)" : ""));
            }
        }

        public void Reset(Camera main)
        {
            if (main != null) main.ResetProjectionMatrix();
            if (matrixCam != null) matrixCam.ResetProjectionMatrix();
            matrixCam = null;
            usingMatrix = false;
            if (savedLodBias > 0) { QualitySettings.lodBias = savedLodBias; savedLodBias = -1; }
        }
    }

    // The lens effects: one URP Volume owned by the replay, with depth of field (Bokeh), vignette, film grain, chromatic
    // aberration and camera motion blur.
    //
    // How it wins over the game's own Volumes without breaking them (checked headless, see docs/AGENTS.md "Lens"):
    //   - It is a global Volume with priority 1000. The game's Volumes (LevelInfo.PostProcessing with bloom, colour
    //     adjustments and tonemapping, and the camera's own Volume with GLOC's vignette) sit far below that.
    //   - URP blends per parameter: only the parameters this Volume marks as overridden replace the game's, so bloom,
    //     exposure and tonemapping stay the game's. An effect that is off is not overridden at all (the game's own
    //     vignette shows as before).
    //   - It sits on a layer of its own that only the camera rendering the view has in its volume mask, so no other
    //     camera (the target camera's screen, map cameras) gets the effects. The mask bit is added while the lens is
    //     on and taken away again when it goes off.
    //   - A camera whose volume stack updates only "via scripting" gets UpdateVolumeStack every frame while the lens
    //     is on, and once more when it goes off.
    // Off(): disables the Volume and restores the camera's mask, also when the replay ends.
    internal sealed class LensRig
    {
        public const float Priority = 1000f;

        private readonly ManualLogSource log;
        private GameObject go;
        private Volume volume;
        private VolumeProfile profile;
        private DepthOfField dof;
        private Vignette vignette;
        private FilmGrain grain;
        private ChromaticAberration chromatic;
        private MotionBlur blur;
        private int layer = -1;
        private Camera maskCamera;
        private int addedBit;
        private bool on, reported;

        public bool On => on;
        public string Report { get; private set; }   // what the first switch-on found (status file)

        public LensRig(ManualLogSource log) { this.log = log; }

        // `urpFocal` and `urpAperture` come from LensMath.UrpBokeh (the real lens mapped onto URP's Bokeh model).
        public void Apply(Camera cam, LensSettings lens, float focusDepth, float urpFocal, float urpAperture)
        {
            if (cam == null || lens == null || !lens.AnyEffect) { Off(); return; }
            if (go == null) Build();
            if (layer < 0) return;
            if (maskCamera != cam) { RestoreMask(); AddMask(cam); }
            if (!reported) { reported = true; Describe(cam); }

            dof.active = lens.Dof;
            if (lens.Dof)
            {
                Set(dof.mode, DepthOfFieldMode.Bokeh);
                Set(dof.focusDistance, Mathf.Max(0.1f, focusDepth));
                Set(dof.aperture, Mathf.Clamp(urpAperture, 1f, 32f));
                Set(dof.focalLength, Mathf.Clamp(urpFocal, 1f, 300f));
                Set(dof.bladeCount, 7);
                Set(dof.bladeCurvature, 1f);
            }
            vignette.active = lens.Vignette > 0;
            if (vignette.active)
            {
                Set(vignette.intensity, (float)lens.Vignette * 0.6f);
                Set(vignette.smoothness, 0.45f);
                Set(vignette.rounded, false);
                Set(vignette.color, Color.black);
                Set(vignette.center, new Vector2(0.5f, 0.5f));
            }
            grain.active = lens.Grain > 0;
            if (grain.active)
            {
                Set(grain.type, FilmGrainLookup.Medium2);
                Set(grain.intensity, (float)lens.Grain);
                Set(grain.response, 0.8f);
            }
            chromatic.active = lens.Chromatic > 0;
            if (chromatic.active) Set(chromatic.intensity, (float)lens.Chromatic);
            blur.active = lens.MotionBlur > 0;
            if (blur.active)
            {
                Set(blur.mode, MotionBlurMode.CameraOnly);
                Set(blur.quality, MotionBlurQuality.High);
                Set(blur.intensity, (float)lens.MotionBlur);
                Set(blur.clamp, 0.02f + 0.08f * (float)lens.MotionBlur);
            }
            if (!volume.enabled) volume.enabled = true;
            on = true;
            UpdateStack(cam);
        }

        public void Off()
        {
            if (!on) return;
            on = false;
            if (volume != null) volume.enabled = false;
            var cam = maskCamera;
            RestoreMask();
            UpdateStack(cam);
        }

        private static void Set<T>(VolumeParameter<T> p, T value)
        {
            p.overrideState = true;
            p.value = value;
        }

        private void Build()
        {
            layer = FreeLayer();
            if (layer < 0) { log.LogWarning("Lens: no free layer for the lens Volume; effects are off"); return; }
            go = new GameObject("NuclearReplay Lens Volume") { layer = layer };
            UnityEngine.Object.DontDestroyOnLoad(go);
            volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = Priority;
            volume.weight = 1f;
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "NuclearReplay Lens";
            dof = profile.Add<DepthOfField>(false);
            vignette = profile.Add<Vignette>(false);
            grain = profile.Add<FilmGrain>(false);
            chromatic = profile.Add<ChromaticAberration>(false);
            blur = profile.Add<MotionBlur>(false);
            volume.sharedProfile = profile;
            volume.enabled = false;
        }

        // The highest layer with no name that no camera in the scene renders or reads Volumes from.
        private static int FreeLayer()
        {
            var cams = Camera.allCameras;
            for (int i = 31; i >= 8; i--)
            {
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) continue;
                int bit = 1 << i;
                bool used = false;
                foreach (var c in cams)
                {
                    var data = c != null ? c.GetComponent<UniversalAdditionalCameraData>() : null;
                    if (data != null && data.volumeLayerMask.value != -1 && (data.volumeLayerMask.value & bit) != 0) used = true;
                }
                if (!used) return i;
            }
            return -1;
        }

        private void AddMask(Camera cam)
        {
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null) return;
            int bit = 1 << layer;
            maskCamera = cam;
            addedBit = (data.volumeLayerMask.value & bit) == 0 ? bit : 0;
            if (addedBit != 0) data.volumeLayerMask = data.volumeLayerMask.value | bit;
        }

        private void RestoreMask()
        {
            if (maskCamera != null && addedBit != 0)
            {
                var data = maskCamera.GetComponent<UniversalAdditionalCameraData>();
                if (data != null) data.volumeLayerMask = data.volumeLayerMask.value & ~addedBit;
            }
            maskCamera = null;
            addedBit = 0;
        }

        private static void UpdateStack(Camera cam)
        {
            if (cam == null) return;
            try
            {
                var data = cam.GetComponent<UniversalAdditionalCameraData>();
                if (data != null && !data.requiresVolumeFrameworkUpdate) cam.UpdateVolumeStack(data);
            }
            catch (Exception) { }
        }

        // Logs (once) what the lens Volume competes with: every Volume in the scene and every camera's post-processing
        // and volume mask. This is the evidence for the priority notes above.
        private void Describe(Camera cam)
        {
            try
            {
                var lines = new List<string>();
                var data = cam.GetComponent<UniversalAdditionalCameraData>();
                lines.Add(string.Format(CultureInfo.InvariantCulture, "render camera {0}: post-processing {1}, volume mask 0x{2:X8}, update {3}, AA {4}; lens layer {5}",
                    cam.name, data != null && data.renderPostProcessing, data != null ? data.volumeLayerMask.value : 0,
                    data != null ? (data.requiresVolumeFrameworkUpdate ? "every frame" : "via scripting") : "?", data != null ? data.antialiasing.ToString() : "?", layer));
                foreach (var v in UnityEngine.Object.FindObjectsOfType<Volume>(true))
                {
                    var comps = v.sharedProfile != null ? string.Join(",", v.sharedProfile.components.Where(c => c != null && c.active).Select(c => c.GetType().Name)) : "";
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "volume {0}: {1}, layer {2}, priority {3}, weight {4}, enabled {5}, [{6}]",
                        v.name, v.isGlobal ? "global" : "local", v.gameObject.layer, v.priority, v.weight, v.enabled && v.gameObject.activeInHierarchy, comps));
                }
                foreach (var c in Camera.allCameras)
                {
                    if (c == null || c == cam) continue;
                    var d = c.GetComponent<UniversalAdditionalCameraData>();
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "other camera {0}: post-processing {1}, volume mask 0x{2:X8}, type {3}",
                        c.name, d != null && d.renderPostProcessing, d != null ? d.volumeLayerMask.value : 0, d != null ? d.renderType.ToString() : "?"));
                }
                Report = string.Join("\n", lines);
                log.LogInfo("Lens Volume: " + string.Join("\n  ", lines));
            }
            catch (Exception e) { log.LogWarning("Lens: describe failed: " + e.Message); }
        }
    }
}
