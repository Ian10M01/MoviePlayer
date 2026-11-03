using System.Collections.Generic;
using UnityEngine;

namespace MoviePlayer.Theater
{
    /// <summary>
    /// Loads the Blender-built, Cycles-baked cinema from Resources/TheaterModel
    /// (Theater.fbx + T_*.png + T_Lightmap.png + TheaterBake.json). Source scripts
    /// live in ~/MoviePlayerVR/BlenderSource. Renderer names are "Theater_{Material}_*";
    /// seat anchors are the "SeatAnchor_###" empties (000 = viewer at the origin).
    /// Returns false when anything is missing so the procedural theater is used instead.
    /// </summary>
    public partial class TheaterEnvironment
    {
        private const string ModelFolder = "TheaterModel/";

        [System.Serializable]
        private class BakeInfo
        {
            public float rangeHouse = 1f;
            public float rangeScreen = 1f;
        }

        // Texture tile size in metres, must match TILE in build_theater.py
        private static readonly Dictionary<string, float> TileSize = new Dictionary<string, float>
        {
            { "SeatFabric", 0.30f }, { "WallFabric", 1.0f }, { "Velvet", 1.0f }, { "Carpet", 2.0f },
            { "Wood", 1.0f }, { "Leather", 0.30f }, { "Metal", 0.5f }, { "Plastic", 0.5f }, { "Ceiling", 1.2f }
        };

        private bool TryBuildModelTheater()
        {
            var prefab   = Resources.Load<GameObject>(ModelFolder + "Theater");
            var baseMat  = Resources.Load<Material>(ModelFolder + "TheaterBaked");
            var emisMat  = Resources.Load<Material>(ModelFolder + "TheaterEmissive");
            var lightmap = Resources.Load<Texture2D>(ModelFolder + "T_Lightmap");
            if (prefab == null || baseMat == null || emisMat == null || lightmap == null)
            {
                Debug.LogWarning("[TheaterEnvironment] Baked theater model missing, using the procedural theater.");
                return false;
            }

            var info = new BakeInfo();
            var json = Resources.Load<TextAsset>(ModelFolder + "TheaterBake");
            if (json != null) JsonUtility.FromJsonOverwrite(json.text, info);

            var model = Instantiate(prefab, _theaterRoot.transform, false);
            model.name = "TheaterModel";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;
            AlignModel(model.transform);

            var mats = new Dictionary<string, Material>();
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                string[] parts = r.gameObject.name.Split('_');
                string key = parts.Length > 1 ? parts[1] : "";
                Material m;
                if (!mats.TryGetValue(key, out m))
                {
                    if (key == "Emissive")
                    {
                        m = emisMat;
                    }
                    else
                    {
                        m = new Material(baseMat) { name = "Theater_" + key };
                        var albedo = Resources.Load<Texture2D>(ModelFolder + "T_" + key);
                        if (albedo != null) m.SetTexture("_BaseMap", albedo);
                        float tile = TileSize.TryGetValue(key, out var t) ? t : 1f;
                        m.SetTextureScale("_BaseMap", Vector2.one / tile);
                        m.SetTexture("_LightMap", lightmap);
                        m.SetVector("_LightRange", new Vector4(info.rangeHouse, info.rangeScreen, 0f, 0f));
                    }
                    mats[key] = m;
                }
                r.sharedMaterial = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }

            // Seat anchors, sorted by name: 000 is the viewer, then nearest neighbours first
            var anchors = new List<Transform>();
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("SeatAnchor_")) anchors.Add(t);
            anchors.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            _seatAnchors.Clear();
            foreach (var a in anchors)
            {
                // face the screen centre (head yaw only)
                Vector3 local = transform.InverseTransformPoint(a.position);
                Vector3 dir = new Vector3(-local.x, 0f, ScreenPlaneZ - local.z);
                a.rotation = transform.rotation * Quaternion.LookRotation(dir, Vector3.up);
                _seatAnchors.Add(a);
            }
            if (_seatAnchors.Count == 0)
            {
                Debug.LogWarning("[TheaterEnvironment] Theater model has no SeatAnchor_ empties, using the procedural theater.");
                Destroy(model);
                return false;
            }

            Shader.SetGlobalFloat("_TheaterHouseLight", 1f);
            ApplyGlowGlobals();
            Debug.Log($"[TheaterEnvironment] Loaded baked theater: {_seatAnchors.Count} seats, " +
                      $"seat 0 at {transform.InverseTransformPoint(_seatAnchors[0].position)}.");
            return true;
        }

        /// <summary>
        /// Safety net for FBX axis conversion: rotates the model so its ScreenCenter
        /// marker lies on +Z (expected local (0, 3.9, 12)).
        /// </summary>
        private void AlignModel(Transform model)
        {
            Transform marker = null;
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                if (t.name == "ScreenCenter") { marker = t; break; }
            if (marker == null) return;

            Vector3 found = transform.InverseTransformPoint(marker.position);
            Vector3 expected = new Vector3(0f, 3.9f, ScreenPlaneZ);
            if (Vector3.Distance(found, expected) < 0.05f) return;

            // Only ever turn about the vertical axis: the room's up is right after import, and a
            // free FromToRotation would tip the whole cinema over. The Blender→FBX axis
            // conversion leaves the screen behind the viewer (z = -12), so a half-turn fixes it.
            Vector3 flatFound = new Vector3(found.x, 0f, found.z);
            Vector3 flatExpected = new Vector3(expected.x, 0f, expected.z);
            float yaw = Vector3.SignedAngle(flatFound, flatExpected, Vector3.up);
            if (Mathf.Abs(yaw) > 179f) yaw = 180f;
            Debug.LogWarning($"[TheaterEnvironment] Theater model screen marker at {found}, expected {expected}; turning {yaw:F0}° about Y.");
            model.localRotation = Quaternion.AngleAxis(yaw, Vector3.up) * model.localRotation;
        }

        private void ApplyGlowGlobals()
        {
            Shader.SetGlobalColor("_GlowColor", _glowColor);
            Shader.SetGlobalFloat("_GlowBrightness", _glowBrightness * ScreenGlowGain);
        }
    }
}
