using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace NuclearReplay.Playback
{
    // Unit icons for the target HUD (HudOverlay.cs) without their black backgrounds. The game's own icon and pip
    // sprites are a light symbol on opaque black; the game draws them through CombatHUD's private unitMarker
    // prefab. Two ways to lose the black:
    //   1. the prefab Image's material, when it is an additive shader (black adds nothing);
    //   2. otherwise a keyed copy of each sprite: alpha = max(r, g, b) x alpha, colour white, so the Image's
    //      colour still tints it. The copy goes through a RenderTexture, so the game's textures need not be
    //      readable. Copies are cached per source sprite and dropped on a scene change.
    internal static class HudIcons
    {
        private static readonly Dictionary<Sprite, Sprite> keyed = new Dictionary<Sprite, Sprite>();
        private static readonly List<UnityEngine.Object> made = new List<UnityEngine.Object>();
        private static bool probed, useGame, hooked;
        private static Material gameMaterial;
        private static ManualLogSource log;

        public static string Approach { get; private set; } = "not probed";

        public static void Init(ManualLogSource logger)
        {
            log = logger;
            if (hooked) return;
            SceneManager.activeSceneChanged += (a, b) => Clear();
            hooked = true;
        }

        public static void Clear()
        {
            foreach (var o in made) if (o != null) UnityEngine.Object.Destroy(o);
            made.Clear();
            keyed.Clear();
            probed = false; useGame = false; gameMaterial = null;
        }

        // The material to draw unit icons and pips with, or null to use keyed sprites with the default UI material.
        public static Material GameMaterial()
        {
            if (probed) return useGame ? gameMaterial : null;
            CombatHUD hud = null;
            try { hud = SceneSingleton<CombatHUD>.i; } catch (Exception) { }
            if (hud == null) return null;   // no HUD yet: keyed sprites this frame, probe again later
            probed = true;
            try
            {
                var prefab = AccessTools.Field(typeof(CombatHUD), "unitMarker")?.GetValue(hud) as GameObject;
                var image = prefab != null ? prefab.GetComponent<Image>() : null;
                var mat = image != null ? image.material : null;
                string shader = mat != null && mat.shader != null ? mat.shader.name : "none";
                bool isDefault = mat == null || mat == Graphic.defaultGraphicMaterial || shader == "UI/Default";
                bool additive = !isDefault && (shader.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0
                    || (mat.HasProperty("_DstBlend") && Mathf.RoundToInt(mat.GetFloat("_DstBlend")) == (int)BlendMode.One));
                useGame = additive;
                gameMaterial = additive ? mat : null;
                Approach = additive ? "game material " + shader : "keyed sprites (game material " + shader + " is not additive)";
                log?.LogInfo($"Target HUD icons: unitMarker Image material {(mat != null ? mat.name : "none")}, shader {shader}, "
                    + $"default {isDefault}, additive {additive}; icons drawn with {Approach}");
            }
            catch (Exception e)
            {
                useGame = false; gameMaterial = null;
                Approach = "keyed sprites (prefab unreadable)";
                log?.LogWarning("Target HUD icons: could not read CombatHUD.unitMarker (" + e.Message + "); using keyed sprites");
            }
            return useGame ? gameMaterial : null;
        }

        // A copy of the sprite with its black made transparent; the sprite itself when the copy fails.
        public static Sprite Keyed(Sprite s)
        {
            if (s == null) return null;
            if (keyed.TryGetValue(s, out var k)) return k != null ? k : s;
            k = null;
            try
            {
                var tex = s.texture;
                var r = s.textureRect;
                int w = Mathf.Max(1, Mathf.RoundToInt(r.width)), h = Mathf.Max(1, Mathf.RoundToInt(r.height));
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                var previous = RenderTexture.active;
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = s.name + " keyed", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                copy.ReadPixels(new Rect(Mathf.Round(r.x), Mathf.Round(r.y), w, h), 0, 0, false);
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                var px = copy.GetPixels32();
                int opaque = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i];
                    int m = Math.Max(c.r, Math.Max(c.g, c.b));
                    byte a = (byte)(m * c.a / 255);
                    if (a > 128) opaque++;
                    px[i] = new Color32(255, 255, 255, a);
                }
                copy.SetPixels32(px);
                copy.Apply(false, false);
                var pivot = new Vector2(s.pivot.x / s.rect.width, s.pivot.y / s.rect.height);
                k = Sprite.Create(copy, new Rect(0, 0, w, h), pivot, s.pixelsPerUnit, 0, SpriteMeshType.FullRect);
                k.name = copy.name;
                made.Add(copy); made.Add(k);
                log?.LogInfo($"Target HUD icons: keyed {s.name} ({w}x{h} from {tex.name} {tex.width}x{tex.height}), {opaque * 100 / px.Length}% opaque after keying");
            }
            catch (Exception e)
            {
                log?.LogWarning($"Target HUD icons: could not key {s.name} ({e.Message}); drawing it as is");
            }
            keyed[s] = k;
            return k != null ? k : s;
        }
    }
}
