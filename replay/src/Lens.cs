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
        private Camera maskCamera;   // the render camera the masks below were set up for
        // Cameras whose volume mask got the lens layer, with the bit actually added (0 when it was already there).
        private readonly List<KeyValuePair<Camera, int>> masked = new List<KeyValuePair<Camera, int>>();
        // Post-processing cameras whose clip planes and field of view were matched to the render camera, with the
        // values to put back.
        private readonly List<KeyValuePair<Camera, Vector3>> synced = new List<KeyValuePair<Camera, Vector3>>();
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
                Set(blur.clamp, 0.01f + 0.04f * (float)lens.MotionBlur);
            }
            if (!volume.enabled) volume.enabled = true;
            on = true;
            SyncPostCameras(cam, lens.Dof || lens.MotionBlur > 0);
            if (!reported) { reported = true; Describe(cam); }
            foreach (var m in masked) UpdateStack(m.Key);
        }

        public void Off()
        {
            if (!on) return;
            on = false;
            if (volume != null) volume.enabled = false;
            var cams = masked.Select(m => m.Key).ToList();
            RestoreMask();
            foreach (var c in cams) UpdateStack(c);
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

        // Nuclear Option renders the world with "Main Camera", which has post-processing off. Its post-processing
        // (bloom, tonemapping, vignette, color adjustments from the "PostProcessing" Volume on layer 7) runs on an
        // Overlay camera "postProcessingRenderer" stacked on it, which reads Volumes from layer 7 only. So the lens
        // layer goes into the mask of every camera that post-processes this frame: the render camera when it does,
        // and each post-processing Overlay camera in its stack (any active one when the stack has none).
        private void AddMask(Camera cam)
        {
            maskCamera = cam;
            foreach (var c in PostCameras(cam))
            {
                var data = c.GetUniversalAdditionalCameraData();
                if (data == null) continue;
                int bit = 1 << layer;
                int added = (data.volumeLayerMask.value & bit) == 0 ? bit : 0;
                if (added != 0) data.volumeLayerMask = data.volumeLayerMask.value | bit;
                masked.Add(new KeyValuePair<Camera, int>(c, added));
            }
        }

        private static List<Camera> PostCameras(Camera cam)
        {
            var list = new List<Camera>();
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null) return list;
            if (data.renderPostProcessing) list.Add(cam);
            var overlays = new List<Camera>();
            try { if (data.cameraStack != null) overlays.AddRange(data.cameraStack.Where(c => c != null && c.isActiveAndEnabled)); }
            catch (Exception) { }
            if (!overlays.Any(IsPost))
                overlays = Camera.allCameras.Where(c => c != null && c != cam && IsPost(c)
                    && c.GetComponent<UniversalAdditionalCameraData>().renderType == CameraRenderType.Overlay).ToList();
            list.AddRange(overlays.Where(IsPost));
            if (list.Count == 0) list.Add(cam);   // nothing post-processes: keep the old behavior (the effects stay off)
            return list;
        }

        private static bool IsPost(Camera c)
        {
            var d = c != null ? c.GetComponent<UniversalAdditionalCameraData>() : null;
            return d != null && d.renderPostProcessing;
        }

        // Depth of field and motion blur read the world's depth buffer through the post-processing camera's own clip
        // planes (and motion blur through its projection). The game's post-processing Overlay camera has no renderers
        // on the layers it draws, so while either effect is on its near/far planes and field of view follow the render
        // camera; they are put back when the effect goes off. Without this its planes (0.1 to 5 m) turn every depth
        // into a few metres and the whole frame blurs.
        private void SyncPostCameras(Camera cam, bool need)
        {
            if (!need) { Unsync(); return; }
            foreach (var m in masked)
            {
                var c = m.Key;
                if (c == null || c == cam || !DrawsNothing(c)) continue;
                int i = synced.FindIndex(x => x.Key == c);
                if (i < 0) synced.Add(new KeyValuePair<Camera, Vector3>(c, new Vector3(c.nearClipPlane, c.farClipPlane, c.fieldOfView)));
                if (c.nearClipPlane != cam.nearClipPlane) c.nearClipPlane = cam.nearClipPlane;
                if (c.farClipPlane != cam.farClipPlane) c.farClipPlane = cam.farClipPlane;
                if (c.fieldOfView != cam.fieldOfView) c.fieldOfView = cam.fieldOfView;
            }
        }

        // True when no active renderer sits on a layer the camera draws (the game's post-processing camera draws layer
        // 7, which holds only its Volumes). Checked once per camera.
        private readonly Dictionary<Camera, bool> empty = new Dictionary<Camera, bool>();
        private bool DrawsNothing(Camera c)
        {
            if (empty.TryGetValue(c, out bool known)) return known;
            bool none = c.cullingMask == 0;
            if (!none)
            {
                try
                {
                    int n = UnityEngine.Object.FindObjectsOfType<Renderer>().Count(r => r.enabled && (c.cullingMask & (1 << r.gameObject.layer)) != 0);
                    none = n == 0;
                    log.LogInfo(string.Format(CultureInfo.InvariantCulture, "Lens: {0} draws {1} renderers; {2}", c.name, n,
                        none ? "its clip planes and field of view follow the render camera while depth of field or motion blur is on"
                             : "left alone, so depth of field and motion blur may judge distances wrongly"));
                }
                catch (Exception) { none = false; }
            }
            empty[c] = none;
            return none;
        }

        private void Unsync()
        {
            foreach (var s in synced)
            {
                if (s.Key == null) continue;
                s.Key.nearClipPlane = s.Value.x;
                s.Key.farClipPlane = s.Value.y;
                s.Key.fieldOfView = s.Value.z;
            }
            synced.Clear();
        }

        private void RestoreMask()
        {
            Unsync();
            foreach (var m in masked)
            {
                if (m.Key == null || m.Value == 0) continue;
                var data = m.Key.GetComponent<UniversalAdditionalCameraData>();
                if (data != null) data.volumeLayerMask = data.volumeLayerMask.value & ~m.Value;
            }
            masked.Clear();
            maskCamera = null;
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
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "other camera {0}: post-processing {1}, volume mask 0x{2:X8}, type {3}, culling 0x{4:X8}, near {5:0.###} far {6:0.#} fov {7:0.#}, parent {8}, update {9}",
                        c.name, d != null && d.renderPostProcessing, d != null ? d.volumeLayerMask.value : 0, d != null ? d.renderType.ToString() : "?",
                        c.cullingMask, c.nearClipPlane, c.farClipPlane, c.fieldOfView, c.transform.parent != null ? c.transform.parent.name : "none",
                        d != null ? (d.requiresVolumeFrameworkUpdate ? "every frame" : "via scripting") : "?"));
                }
                lines.Add(string.Format(CultureInfo.InvariantCulture, "render camera near {0:0.###} far {1:0.#} fov {2:0.#}; lens layer added to [{3}]; clip planes matched on [{4}]",
                    cam.nearClipPlane, cam.farClipPlane, cam.fieldOfView, string.Join(", ", masked.Select(m => m.Key != null ? m.Key.name : "?")),
                    string.Join(", ", synced.Select(m => m.Key != null ? m.Key.name : "?"))));
                Report = string.Join("\n", lines);
                log.LogInfo("Lens Volume: " + string.Join("\n  ", lines));
            }
            catch (Exception e) { log.LogWarning("Lens: describe failed: " + e.Message); }
        }
    }
}
