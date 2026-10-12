using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace NuclearReplay.Playback
{
    // Engine matte: a second camera that renders only the target aircraft into an off-screen texture.
    // While that camera renders (and only then) the aircraft's mesh renderers move to a spare layer;
    // the camera culls everything else and clears to transparent black. Opaque URP shaders write
    // alpha 1, so the texture's alpha is an exact, anti-aliased silhouette of the aircraft.
    // Limits: the matte ignores occlusion (a jet behind a hill is still white), and particles
    // (exhaust, smoke) are not part of it.
    internal sealed class EngineMatte
    {
        private readonly ManualLogSource log;
        private GameObject host;
        private Camera cam;
        private RenderTexture texture;
        private int layer = -1;
        private Unit unit;
        private readonly List<KeyValuePair<GameObject, int>> swapped = new List<KeyValuePair<GameObject, int>>();
        public bool Want;              // set each frame by the plugin
        public int RenderedFrame = -1; // Time.frameCount of the last matte render
        public RenderTexture Texture => texture;

        public EngineMatte(ManualLogSource log)
        {
            this.log = log;
            RenderPipelineManager.beginCameraRendering += OnBegin;
            RenderPipelineManager.endCameraRendering += OnEnd;
            Application.onBeforeRender += BeforeRender;
        }

        public void SetTarget(Unit target) { unit = target; }

        private void Ensure(Camera main)
        {
            if (layer < 0)
            {
                for (int l = 31; l >= 19; l--)
                    if (string.IsNullOrEmpty(LayerMask.LayerToName(l))) { layer = l; break; }
                if (layer < 0) { log.LogError("Engine matte: no spare layer"); layer = 31; }
                log.LogInfo("Engine matte uses layer " + layer);
            }
            if (cam == null)
            {
                host = new GameObject("nc-matte-camera");
                UnityEngine.Object.DontDestroyOnLoad(host);
                cam = host.AddComponent<Camera>();
                cam.enabled = false;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.cullingMask = 1 << layer;
                cam.allowHDR = false;
                cam.allowMSAA = true;
                cam.depth = -100;
                cam.useOcclusionCulling = false;
            }
            int w = main.pixelWidth, h = main.pixelHeight;
            if (texture == null || texture.width != w || texture.height != h)
            {
                if (texture != null) texture.Release();
                texture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8, name = "nc-matte" };
                texture.Create();
                cam.targetTexture = texture;
            }
        }

        // Runs after every LateUpdate (the game's camera has its final pose) and before rendering.
        private void BeforeRender()
        {
            try
            {
                var main = CameraStateManager.i != null ? CineCamera.RenderCamera(CameraStateManager.i.mainCamera) : null;
                if (!Want || main == null || unit == null || unit.disabled)
                {
                    if (cam != null) cam.enabled = false;
                    return;
                }
                Ensure(main);
                cam.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
                cam.fieldOfView = main.fieldOfView;   // may be blocked by a mod; the matrix below is what counts
                cam.nearClipPlane = main.nearClipPlane;
                cam.farClipPlane = main.farClipPlane;
                cam.aspect = main.aspect;
                cam.projectionMatrix = main.projectionMatrix;
                cam.enabled = true;
            }
            catch (Exception e) { log.LogError("Engine matte setup failed: " + e.Message); Want = false; }
        }

        private void OnBegin(ScriptableRenderContext context, Camera camera)
        {
            if (camera != cam || unit == null) return;
            swapped.Clear();
            foreach (var r in unit.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                var go = r.gameObject;
                swapped.Add(new KeyValuePair<GameObject, int>(go, go.layer));
                go.layer = layer;
            }
        }

        private void OnEnd(ScriptableRenderContext context, Camera camera)
        {
            if (camera != cam) return;
            foreach (var pair in swapped) if (pair.Key != null) pair.Key.layer = pair.Value;
            swapped.Clear();
            RenderedFrame = Time.frameCount;
        }

        public void Dispose()
        {
            RenderPipelineManager.beginCameraRendering -= OnBegin;
            RenderPipelineManager.endCameraRendering -= OnEnd;
            Application.onBeforeRender -= BeforeRender;
            if (texture != null) texture.Release();
            if (host != null) UnityEngine.Object.Destroy(host);
        }
    }
}
