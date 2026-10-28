using System;
using System.Globalization;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace NuclearReplay.Playback
{
    // Lens pack (docs/CINEMA-PLAN.md pillar 3): focal length in mm on a chosen sensor, depth of field with autofocus
    // on the target or a fixed (or keyed) focus distance, vignette, film grain, chromatic aberration and camera
    // motion blur. LensMath.cs holds the maths and the settings, Lens.cs the URP Volume and the field of view.
    //
    // The lens on screen each frame:
    //   free camera on: the Lens panel's (its focal length drives the free camera, so Camera Studio keys get it);
    //   otherwise the current shot's lens fields (focal_mm, sensor, dof, vignette, grain, chromatic, motion_blur);
    //   a shot without any of them shows the Lens panel's effects in the viewer and none in a render job.
    // Focus "on the target": the distance along the view to the shot's target (free camera: the Camera Studio
    // aircraft, else the unit nearest the middle of the screen, else whatever the middle of the screen hits).
    // A path shot whose keys carry focus or f_stop pulls focus from key to key (CinePath.PathLens).
    //
    // Viewer keys: F3 opens and closes the panel. While it is open: Tab / Shift+Tab pick a row, PageUp / PageDown
    // change it (Shift: bigger steps), Home resets it. The free camera adds Ctrl + wheel (focal length) and Alt +
    // wheel (focus distance). The panel's settings are kept in the BepInEx config ([Lens]).
    public sealed partial class ReplayPlugin
    {
        private readonly LensSettings lens = new LensSettings();
        private LensRig lensRig;
        private bool lensPanel;
        private LensSettings.Row lensRow;
        private ConfigEntry<float> lensFocalCfg, lensFStopCfg, lensVignetteCfg, lensGrainCfg, lensChromaticCfg, lensBlurCfg;
        private ConfigEntry<string> lensSensorCfg, lensFocusCfg;
        private ConfigEntry<bool> lensDofCfg;
        private JObject lensShotRaw;
        private LensSettings lensShot;
        private double lensFocalNow, lensFocusNow, lensFocusUsed, lensFStopNow;
        private string lensSource = "none";
        // Autofocus at render time: the unit focused on and the camera it is measured from (null when focus is fixed,
        // keyed or on a raycast). LensStep runs in LateUpdate, before the game's camera code may still move the camera
        // that frame; the focus distance is measured again as the render camera starts drawing (LensBeforeRender).
        private Unit lensFocusUnit;
        private Camera lensFocusCam;
        private bool lensRenderHook;
        private int lensFocusLogs, lensFocusFrame;

        // Called from ViewerAwake (every session; render jobs read only shot lenses).
        private void LensAwake()
        {
            lensFocalCfg = Config.Bind("Lens", "FocalMm", 0f, "Free camera focal length in mm (8 to 600). 0: the game's own field of view.");
            lensSensorCfg = Config.Bind("Lens", "Sensor", LensMath.DefaultSensor, "Sensor the focal length is measured on: super35 or full_frame.");
            lensDofCfg = Config.Bind("Lens", "DepthOfField", false, "Depth of field on (viewer).");
            lensFStopCfg = Config.Bind("Lens", "FStop", 2.8f, "Aperture (f-number) for depth of field.");
            lensFocusCfg = Config.Bind("Lens", "Focus", "target", "Focus: target (autofocus on the target) or a distance in metres.");
            lensVignetteCfg = Config.Bind("Lens", "Vignette", 0f, "Vignette amount, 0 (off) to 1.");
            lensGrainCfg = Config.Bind("Lens", "Grain", 0f, "Film grain amount, 0 (off) to 1.");
            lensChromaticCfg = Config.Bind("Lens", "ChromaticAberration", 0f, "Chromatic aberration amount, 0 (off) to 1.");
            lensBlurCfg = Config.Bind("Lens", "MotionBlur", 0f, "Camera motion blur amount, 0 (off) to 1.");
            lens.FocalMm = lensFocalCfg.Value > 0 ? LensMath.Clamp(lensFocalCfg.Value, LensMath.MinFocal, LensMath.MaxFocal) : 0;
            lens.Sensor = LensMath.SensorOrDefault(lensSensorCfg.Value).Id;
            lens.Dof = lensDofCfg.Value;
            lens.FStop = LensMath.Clamp(lensFStopCfg.Value, LensMath.MinFStop, LensMath.MaxFStop);
            lens.FocusTarget = !double.TryParse(lensFocusCfg.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var focus) || focus <= 0;
            if (!lens.FocusTarget) lens.FocusM = LensMath.Clamp(focus, LensMath.MinFocus, LensMath.MaxFocus);
            lens.Vignette = LensMath.Clamp(lensVignetteCfg.Value, 0, 1);
            lens.Grain = LensMath.Clamp(lensGrainCfg.Value, 0, 1);
            lens.Chromatic = LensMath.Clamp(lensChromaticCfg.Value, 0, 1);
            lens.MotionBlur = LensMath.Clamp(lensBlurCfg.Value, 0, 1);
        }

        private void LensSave()
        {
            if (lensFocalCfg == null) return;
            lensFocalCfg.Value = (float)Math.Round(lens.FocalMm, 1);
            lensSensorCfg.Value = lens.Sensor;
            lensDofCfg.Value = lens.Dof;
            lensFStopCfg.Value = (float)lens.FStop;
            lensFocusCfg.Value = lens.FocusTarget ? "target" : Math.Round(lens.FocusM, 2).ToString(CultureInfo.InvariantCulture);
            lensVignetteCfg.Value = (float)lens.Vignette;
            lensGrainCfg.Value = (float)lens.Grain;
            lensChromaticCfg.Value = (float)lens.Chromatic;
            lensBlurCfg.Value = (float)lens.MotionBlur;
        }

        // The free camera shares the panel's settings.
        private void LensAttach(FreeFlyCamera fly)
        {
            fly.Lens = lens;
            fly.LensChanged = LensSave;
        }

        // ---------------- keys and panel ----------------

        // ViewerKeys calls this every frame.
        private void LensKeys()
        {
            if (Input.GetKeyDown(KeyCode.F3)) ViewerToggleLens();
            if (!lensPanel) return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(KeyCode.Tab)) LensSelect(shift ? -1 : 1);
            if (Input.GetKeyDown(KeyCode.PageUp)) LensAdjust(1, shift);
            if (Input.GetKeyDown(KeyCode.PageDown)) LensAdjust(-1, shift);
            if (Input.GetKeyDown(KeyCode.Home)) LensReset();
        }

        internal void ViewerToggleLens()
        {
            if (!viewer) return;
            lensPanel = !lensPanel;
            if (lensPanel) lightingOpen = false;   // both panels use Page Up, Page Down and Home
        }

        internal void LensSelect(int step)
        {
            int n = LensSettings.RowCount;
            lensRow = (LensSettings.Row)((((int)lensRow + step) % n + n) % n);
        }

        internal void LensAdjust(int dir, bool big) => LensChanged(lens.Adjust(lensRow, dir, big, lensFocalNow, lensFocusNow));

        internal void LensReset() => LensChanged(lens.Reset(lensRow));

        internal void LensAllOff()
        {
            lens.Dof = false;
            lens.Vignette = lens.Grain = lens.Chromatic = lens.MotionBlur = 0;
            LensChanged("Lens effects off");
        }

        private void LensChanged(string message)
        {
            if (message != null) ui.Toast(message, 2f);
            LensSave();
            // Back to the game's own field of view when the focal length is reset.
            var cam = CameraStateManager.i;
            if (lens.FocalMm <= 0 && freeFly != null && freeCamOn && cam != null && freeFly.Fov > 0) freeFly.GameFov(cam);
        }

        // Panel text (null when closed).
        private string LensPanelText()
        {
            if (!lensPanel) return null;
            string text = lens.Describe(lensRow, lensFocalNow, lensFocusNow);
            string note = freeCamOn ? "Free camera: Ctrl + wheel focal length, Alt + wheel focus."
                : lensSource == "shot" ? "This shot sets its own lens; the panel's focal length is for the free camera."
                : "The focal length is for the free camera (F); the effects show on every camera.";
            return text + "\n<size=14>" + note + "</size>";
        }

        // ---------------- every frame ----------------

        // LateUpdate, after the director and the camera have moved.
        private void LensStep(bool live)
        {
            var cams = CameraStateManager.i;
            if (!live || cams == null || cams.mainCamera == null) { LensOff(); return; }
            var rc = CineCamera.RenderCamera(cams.mainCamera);
            LensSettings use = null;
            if (viewer && freeCamOn) { use = lens; lensSource = "panel"; }
            else
            {
                var raw = director?.CurrentRaw;
                if (!ReferenceEquals(raw, lensShotRaw)) { lensShotRaw = raw; lensShot = LensShots.FromShot(raw); }
                if (lensShot != null) { use = lensShot; lensSource = "shot"; }
                else if (viewer) { use = lens; lensSource = "panel"; }
                else lensSource = "none";
            }

            var sensor = (use ?? lens).SensorInfo;
            double aspect = rc.aspect > 0 ? rc.aspect : LensShots.Aspect;
            float fov = cams.desiredFOV > 0.5f ? cams.desiredFOV : rc.fieldOfView;
            lensFocalNow = LensMath.FocalFromFov(fov, sensor, aspect);
            double target = LensTargetDepth(rc);
            lensFocusNow = target;
            if (freeFly != null && target > 0) freeFly.LiveFocusM = target;
            if (use == null || !use.AnyEffect) { lensFStopNow = 0; LensOff(); return; }

            double focus = use.FocusTarget ? (target > 0 ? target : 1000) : use.FocusM;
            double fStop = use.FStop;
            var cine = director?.Cine;
            bool keyed = false;
            if (!freeCamOn && cine != null && cine.PathLens(focus, fStop, out var keyedFocus, out var keyedStop)) { focus = keyedFocus; fStop = keyedStop; keyed = true; }
            var focusUnit = use.Dof && use.FocusTarget && !keyed ? LensFocusUnit(rc) : null;
            if (!ReferenceEquals(focusUnit, lensFocusUnit)) lensFocusLogs = 0;
            lensFocusUnit = focusUnit;
            lensFocusCam = rc;
            if (!lensRenderHook) { RenderPipelineManager.beginCameraRendering += LensBeforeRender; lensRenderHook = true; }
            focus = LensMath.Clamp(focus, LensMath.MinFocus, LensMath.MaxFocus);
            lensFocusUsed = focus;
            lensFStopNow = fStop;
            LensMath.UrpBokeh(lensFocalNow, fStop, focus, LensMath.FrameHeight(sensor, aspect), rc.pixelHeight, out var urpFocal, out var urpAperture);
            if (lensRig == null) lensRig = new LensRig(Logger);
            lensRig.Apply(rc, use, (float)focus, (float)urpFocal, (float)urpAperture);
        }

        private void LensOff() { lensFocusUnit = null; lensRig?.Off(); }

        // The render camera is about to draw: autofocus is measured again from where the camera and the unit really
        // are this frame (no frame of lag on a fast mover), and logged for a few frames per target.
        private void LensBeforeRender(ScriptableRenderContext context, Camera cam)
        {
            try
            {
                if (cam == null || cam != lensFocusCam || lensFocusUnit == null || lensRig == null || !lensRig.On) return;
                if (lensFocusUnit.transform == null) return;
                Vector3 at = cam.transform.position, forward = cam.transform.forward;
                Vector3 pivot = lensFocusUnit.transform.position;
                double depth = Vector3.Dot(pivot - at, forward);
                if (!(depth > LensMath.MinFocus)) return;
                double before = lensFocusUsed;
                lensFocusUsed = LensMath.Clamp(depth, LensMath.MinFocus, LensMath.MaxFocus);
                lensRig.Refocus((float)lensFocusUsed);
                if (lensFocusLogs < 8 && Time.frameCount - lensFocusFrame >= 15)
                {
                    lensFocusLogs++;
                    lensFocusFrame = Time.frameCount;
                    var b = LensUnitBounds(lensFocusUnit);
                    double boundsDepth = b.HasValue ? Vector3.Dot(b.Value.center - at, forward) : double.NaN;
                    Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "Lens focus {0}: {1:0.0} m at render (LateUpdate had {2:0.0}), straight line {3:0.0} m, bounds centre along view {4:0.0} m, bounds size {5}, f/{6:0.0} {7:0} mm",
                        lensFocusUnit.unitName, lensFocusUsed, before, Vector3.Distance(pivot, at), boundsDepth,
                        b.HasValue ? b.Value.size.ToString("F1") : "?", lensFStopNow, lensFocalNow));
                }
            }
            catch (Exception) { }
        }

        private static Bounds? LensUnitBounds(Unit unit)
        {
            Bounds? all = null;
            foreach (var r in unit.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (all.HasValue) { var a = all.Value; a.Encapsulate(r.bounds); all = a; } else all = r.bounds;
            }
            return all;
        }

        // The replay is over (or restarting): the Volume goes off and the camera's volume mask is restored.
        private void LensTeardown()
        {
            LensOff();
            lensShotRaw = null; lensShot = null;
            lensSource = "none";
        }

        // Distance in metres along the view to what autofocus focuses on; -1 when there is nothing.
        private Unit LensFocusUnit(Camera rc)
        {
            var unit = freeCamOn ? (studio.Open ? StudioUnit() : null) ?? LensCentreUnit(rc) : director?.CurrentUnit;
            if (unit == null || unit.transform == null) return null;
            return Vector3.Dot(unit.transform.position - rc.transform.position, rc.transform.forward) > LensMath.MinFocus ? unit : null;
        }

        private double LensTargetDepth(Camera rc)
        {
            Vector3 at = rc.transform.position, forward = rc.transform.forward;
            var unit = LensFocusUnit(rc);
            if (unit != null)
            {
                double d = Vector3.Dot(unit.transform.position - at, forward);
                if (d > LensMath.MinFocus) return d;
            }
            if (Physics.Raycast(at, forward, out var hit, 20000f, ~0, QueryTriggerInteraction.Ignore)) return hit.distance;
            return -1;
        }

        // The live unit nearest the middle of the screen, within 12 degrees of it and 8 km.
        private static Unit LensCentreUnit(Camera rc)
        {
            Vector3 at = rc.transform.position, forward = rc.transform.forward;
            float bestCos = Mathf.Cos(12f * Mathf.Deg2Rad);
            Unit best = null;
            foreach (var u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled || u.transform == null) continue;
                Vector3 to = u.transform.position - at;
                float d = to.magnitude;
                if (d < 1f || d > 8000f) continue;
                float c = Vector3.Dot(to / d, forward);
                if (c > bestCos) { bestCos = c; best = u; }
            }
            return best;
        }

        // ---------------- test hooks and status ----------------

        // Viewer command "lens": "lens" (state), "lens panel on|off", "lens select <row>", "lens up|down [big]",
        // "lens reset", "lens off", "lens <field> <value>" with the shot-list field names (focal_mm, sensor, f_stop,
        // focus, vignette, grain, chromatic, motion_blur, dof on|off).
        private string LensCommand(string arg)
        {
            var parts = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string what = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
            string value = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            double num = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
            switch (what)
            {
                case "": break;
                case "panel": lensPanel = value != "off"; break;
                case "select":
                    if (Enum.TryParse<LensSettings.Row>(value, true, out var row)) lensRow = row;
                    else return "unknown row";
                    break;
                case "up": LensAdjust(1, parts.Length > 2); break;
                case "down": LensAdjust(-1, parts.Length > 2); break;
                case "reset": LensReset(); break;
                case "off": LensAllOff(); break;
                case "focal_mm": lens.FocalMm = double.IsNaN(num) || num <= 0 ? 0 : LensMath.Clamp(num, LensMath.MinFocal, LensMath.MaxFocal); LensChanged(null); break;
                case "sensor": if (LensMath.FindSensor(value) == null) return "unknown sensor"; lens.Sensor = value; LensChanged(null); break;
                case "dof": lens.Dof = value != "off"; LensChanged(null); break;
                case "f_stop": if (!double.IsNaN(num)) { lens.FStop = LensMath.Clamp(num, LensMath.MinFStop, LensMath.MaxFStop); lens.Dof = true; } LensChanged(null); break;
                case "focus":
                    if (value == "target") lens.FocusTarget = true;
                    else if (!double.IsNaN(num)) { lens.FocusTarget = false; lens.FocusM = LensMath.Clamp(num, LensMath.MinFocus, LensMath.MaxFocus); }
                    lens.Dof = true;
                    LensChanged(null);
                    break;
                case "vignette": lens.Vignette = LensMath.Clamp(double.IsNaN(num) ? 0 : num, 0, 1); LensChanged(null); break;
                case "grain": lens.Grain = LensMath.Clamp(double.IsNaN(num) ? 0 : num, 0, 1); LensChanged(null); break;
                case "chromatic": lens.Chromatic = LensMath.Clamp(double.IsNaN(num) ? 0 : num, 0, 1); LensChanged(null); break;
                case "motion_blur": lens.MotionBlur = LensMath.Clamp(double.IsNaN(num) ? 0 : num, 0, 1); LensChanged(null); break;
                default: return "unknown lens command";
            }
            return "lens " + lens.Summary() + (lensPanel ? " (panel open, row " + lensRow + ")" : "");
        }

        private JObject LensStatus() => new JObject
        {
            ["panel"] = lensPanel, ["row"] = lensRow.ToString(), ["source"] = lensSource, ["on"] = lensRig != null && lensRig.On,
            ["settings"] = LensShots.ToJson(lens.ToShot()), ["summary"] = lens.Summary(),
            ["focal_mm_now"] = Math.Round(lensFocalNow, 1), ["focus_target_m"] = Math.Round(lensFocusNow, 1),
            ["focus_used_m"] = Math.Round(lensFocusUsed, 1), ["f_stop_now"] = Math.Round(lensFStopNow, 2),
            ["volume"] = lensRig?.Report, ["text"] = LensPanelText()
        };
    }
}
