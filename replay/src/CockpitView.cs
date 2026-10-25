using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // The game's cockpit camera on a replayed aircraft. The game builds the cockpit's tactical display (the
    // TacScreen on the panel) only for the local player's aircraft, and its cockpit camera hides every crew
    // member, so a replayed cockpit had a blank screen and an empty back seat. While the cockpit camera is on an
    // aircraft this builds that display, shows every crew member except the one in the camera's seat and keeps
    // the aircraft's own canvases on when the replay hides the game's UI. Leaving the cockpit undoes it.
    internal sealed class CockpitView
    {
        private static readonly FieldInfo TacScreenField = typeof(Cockpit).GetField("tacScreen", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo TacScreenPrefabField = typeof(Cockpit).GetField("tacScreenUIPrefab", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly ManualLogSource log;
        private readonly List<Canvas> canvases = new List<Canvas>();
        private Aircraft aircraft;
        private Cockpit cockpit;
        private TacScreen screen;
        private bool cockpitWasEnabled;
        private double nextScan;
        private int loggedSeat = -1;

        public CockpitView(ManualLogSource log) { this.log = log; }

        public Aircraft Aircraft => aircraft;

        // True for a canvas that belongs to the aircraft in the cockpit camera; the replay's HUD hiding skips it.
        public bool Keeps(Canvas canvas) => aircraft != null && canvas != null && canvas.transform.IsChildOf(aircraft.transform);

        // Once per frame, after the director has set the camera.
        public void Step(bool allowed)
        {
            var cameras = CameraStateManager.i;
            Aircraft want = null;
            if (allowed && cameras != null && cameras.currentState == cameras.cockpitState && cameras.followingUnit is Aircraft a && a != null && !a.disabled)
                want = a;
            if (want != aircraft)
            {
                Leave();
                if (want != null) Enter(want);
            }
            if (aircraft == null) return;
            double now = Time.unscaledTimeAsDouble;
            if (now >= nextScan)
            {
                nextScan = now + 0.5;
                ShowCrew(cameras.mainCamera != null ? cameras.mainCamera.transform.position : aircraft.transform.position);
                canvases.Clear();
                foreach (var c in aircraft.GetComponentsInChildren<Canvas>(true)) if (c != null && c.isRootCanvas) canvases.Add(c);
            }
            foreach (var c in canvases) if (c != null && !c.enabled) c.enabled = true;
        }

        public void Leave()
        {
            if (aircraft == null) return;
            try
            {
                if (screen != null) UnityEngine.Object.Destroy(screen.gameObject);
                if (cockpit != null) cockpit.enabled = cockpitWasEnabled;
                if (!aircraft.disabled && aircraft.pilots != null)
                    foreach (var p in aircraft.pilots) if (p != null && !p.ejected && !p.dead) p.TogglePilotVisibility(true);
            }
            catch (Exception e) { log.LogWarning("Cockpit view: leave: " + e.Message); }
            screen = null; cockpit = null; aircraft = null;
            canvases.Clear();
        }

        private void Enter(Aircraft a)
        {
            aircraft = a;
            nextScan = 0;
            loggedSeat = -1;
            cockpit = a.GetComponentInChildren<Cockpit>(true);
            if (cockpit == null) { log.LogInfo($"Cockpit view: {a.unitName} has no cockpit displays"); return; }
            cockpitWasEnabled = cockpit.enabled;
            // The game made the display only if this is the local player's aircraft.
            if (TacScreenField?.GetValue(cockpit) == null && TacScreenPrefabField?.GetValue(cockpit) is GameObject prefab && prefab != null)
            {
                try
                {
                    screen = UnityEngine.Object.Instantiate(prefab, cockpit.transform).GetComponent<TacScreen>();
                    screen.Initialize(a, cockpit);
                }
                catch (Exception e)
                {
                    log.LogWarning($"Cockpit view: tactical display for {a.unitName}: {e.Message}");
                    if (screen != null) UnityEngine.Object.Destroy(screen.gameObject);
                    screen = null;
                }
            }
            // Animates the stick and throttle from the aircraft's inputs.
            cockpit.enabled = true;
            log.LogInfo($"Cockpit view: {a.unitName}, tactical display {(screen != null ? "built" : "not built")}, crew {a.pilots?.Length ?? 0}");
        }

        // The cockpit camera hides the whole crew; show everyone but the one whose seat the camera is in.
        private void ShowCrew(Vector3 eye)
        {
            var pilots = aircraft.pilots;
            if (pilots == null || pilots.Length < 2) return;
            int seat = -1;
            float best = float.MaxValue;
            for (int i = 0; i < pilots.Length; i++)
            {
                if (pilots[i] == null) continue;
                float d = (pilots[i].transform.position - eye).sqrMagnitude;
                if (d < best) { best = d; seat = i; }
            }
            for (int i = 0; i < pilots.Length; i++)
                if (pilots[i] != null && !pilots[i].ejected && !pilots[i].dead) pilots[i].TogglePilotVisibility(i != seat);
            if (seat != loggedSeat) { loggedSeat = seat; log.LogInfo($"Cockpit view: camera in seat {seat} of {pilots.Length}, the rest of the crew shown"); }
        }
    }
}
