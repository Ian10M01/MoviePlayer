using System.Collections.Generic;
using UnityEngine;

namespace MoviePlayer.Theater
{
    /// <summary>
    /// Procedural construction of the cinema: room shell, acoustic-panel walls,
    /// black screen frame, stadium risers, seats and dim warm lights.
    /// Everything is built from primitives with colliders removed. The total
    /// primitive count stays well below 600 for Quest 2 performance.
    /// </summary>
    public partial class TheaterEnvironment
    {
        // ── Room layout (metres, in this transform's space) ───────────────────
        private const float FloorY       = -1.2f;  // carpet of the front row
        private const float CeilingY     = 10.0f;  // above the tallest screen (y 8.4)
        private const float RoomMinZ     = -6f;    // back wall inner face
        private const float RoomMaxZ     = 12.4f;  // front wall inner face (behind the screen plane z = 12)
        private const float RoomHalfW    = 10.5f;  // side wall inner faces at x = +-10.5
        private const float WallThickness = 0.2f;

        // ── Seating layout ────────────────────────────────────────────────────
        private const float EyeHeight    = 1.2f;   // seated head height above seat floor
        private const int   Rows         = 5;
        private const int   SeatsPerRow  = 9;
        private const float SeatPitch    = 0.75f;  // metres between seat centres
        private const float RowPitch     = 1.2f;   // metres between row centres (z)
        private const float RiserStep    = 0.45f;  // height gained per row (stadium)
        private const float ArcRadius    = 20f;    // gentle arc: row ends sit further back
        private const float AisleLightX  = 4.6f;   // step lights sit near the aisle edges

        // Column order for each row, nearest to centre first, so seat 0 is the
        // viewer and the following indices are the closest neighbours.
        private static readonly int[] ColumnOrder = { 0, -1, 1, -2, 2, -3, 3, -4, 4 };

        // ── Generated objects ─────────────────────────────────────────────────
        private GameObject _theaterRoot;   // room, screen frame, risers, seats, lights
        private GameObject _avatarRoot;    // friend avatars (filled in Avatars partial)
        private readonly List<Transform> _seatAnchors = new List<Transform>();

        // ─────────────────────────────────────────────────────────────────────
        #region Build

        private void BuildTheater()
        {
            _theaterRoot = new GameObject("Theater");
            _theaterRoot.transform.SetParent(transform, false);

            _avatarRoot = new GameObject("FriendAvatars");
            _avatarRoot.transform.SetParent(transform, false);

            // Preferred: the Blender-built, Cycles-baked cinema. Procedural primitives are the fallback.
            if (TryBuildModelTheater()) return;

            // Materials. Only a handful are created and shared by all primitives.
            Material wallA   = WallMaterial   ?? CreateLitMaterial(new Color(0.09f, 0.085f, 0.09f), 0.15f);
            Material wallB   = WallMaterial   ?? CreateLitMaterial(new Color(0.13f, 0.075f, 0.075f), 0.15f);
            Material carpet  = FloorMaterial  ?? CreateLitMaterial(new Color(0.20f, 0.07f, 0.08f), 0.10f);
            Material ceiling = CeilingMaterial ?? CreateLitMaterial(new Color(0.05f, 0.05f, 0.055f), 0.10f);
            Material black   = CreateLitMaterial(new Color(0.01f, 0.01f, 0.01f), 0.05f);
            Material fabric  = CreateLitMaterial(new Color(0.42f, 0.03f, 0.05f), 0.20f);
            Material metal   = CreateLitMaterial(new Color(0.12f, 0.12f, 0.13f), 0.20f);
            Material stepLit = CreateUnlitMaterial(new Color(1.0f, 0.62f, 0.28f));

            BuildRoomShell(wallA, wallB, carpet, ceiling);
            BuildScreenFrame(black);
            BuildRisers(carpet);
            BuildSeats(fabric, metal);
            BuildStepLights(stepLit);
            BuildWarmLights();
            BuildScreenGlowLight();
        }

        /// <summary>Unshadowed light at the screen, tinted every frame by SetScreenGlow.</summary>
        private void BuildScreenGlowLight()
        {
            var go = new GameObject("ScreenGlowLight");
            go.transform.SetParent(_theaterRoot.transform, false);
            go.transform.localPosition = new Vector3(0f, 3.9f, ScreenPlaneZ - 0.5f);
            go.transform.localRotation = Quaternion.LookRotation(new Vector3(0f, -0.3f, -1f));
            _glowLight = go.AddComponent<Light>();
            _glowLight.type = LightType.Spot;
            _glowLight.spotAngle = 120f;
            _glowLight.range = 30f;
            _glowLight.shadows = LightShadows.None;
            _glowLight.intensity = 0f;
        }

        /// <summary>Floor, ceiling, and dark acoustic-panel walls in alternating vertical strips.</summary>
        private void BuildRoomShell(Material wallA, Material wallB, Material carpet, Material ceiling)
        {
            float wallHeight = CeilingY - FloorY;
            float wallCentreY = (CeilingY + FloorY) * 0.5f;
            float roomDepth = RoomMaxZ - RoomMinZ;
            float roomCentreZ = (RoomMaxZ + RoomMinZ) * 0.5f;

            // Carpet, slightly lighter than the walls
            SpawnPrimitive("Carpet", PrimitiveType.Cube, _theaterRoot.transform,
                new Vector3(0f, FloorY - 0.05f, roomCentreZ),
                new Vector3(RoomHalfW * 2f, 0.1f, roomDepth), carpet);

            // Ceiling
            SpawnPrimitive("Ceiling", PrimitiveType.Cube, _theaterRoot.transform,
                new Vector3(0f, CeilingY + 0.05f, roomCentreZ),
                new Vector3(RoomHalfW * 2f, 0.1f, roomDepth), ceiling);

            // Back wall as 1 m acoustic-panel strips, alternating tones
            for (int i = 0; i < (int)(RoomHalfW * 2f); i++)
            {
                float x = -RoomHalfW + 0.5f + i;
                SpawnPrimitive($"BackPanel_{i}", PrimitiveType.Cube, _theaterRoot.transform,
                    new Vector3(x, wallCentreY, RoomMinZ - WallThickness * 0.5f),
                    new Vector3(1f, wallHeight, WallThickness),
                    (i % 2 == 0) ? wallA : wallB);
            }

            // Side walls as 1 m strips along z, alternating tones
            int stripCount = (int)roomDepth;
            for (int side = -1; side <= 1; side += 2)
            {
                string sideName = side < 0 ? "L" : "R";
                for (int i = 0; i < stripCount; i++)
                {
                    float z = RoomMinZ + 0.5f + i;
                    SpawnPrimitive($"SidePanel_{sideName}_{i}", PrimitiveType.Cube,
                        _theaterRoot.transform,
                        new Vector3(side * (RoomHalfW + WallThickness * 0.5f), wallCentreY, z),
                        new Vector3(WallThickness, wallHeight, 1f),
                        (i % 2 == 0) ? wallA : wallB);
                }
            }

            // Front wall behind the screen. Plain dark surface, the screen covers most of it.
            SpawnPrimitive("FrontWall", PrimitiveType.Cube, _theaterRoot.transform,
                new Vector3(0f, wallCentreY, RoomMaxZ + WallThickness * 0.5f),
                new Vector3(RoomHalfW * 2f, wallHeight, WallThickness), wallA);
        }

        /// <summary>Black border around the screen. Bars sit outside the screen area and never overlap it.</summary>
        private void BuildScreenFrame(Material black)
        {
            // Black masking wall just behind the screen plane frames any screen size.
            float z = ScreenPlaneZ + 0.05f;
            float wallHeight = CeilingY - FloorY;
            SpawnPrimitive("ScreenMasking", PrimitiveType.Cube, _theaterRoot.transform,
                new Vector3(0f, (CeilingY + FloorY) * 0.5f, z + 0.025f),
                new Vector3(RoomHalfW * 2f, wallHeight, 0.05f), black);
            // Low stage in front of the screen, top below StageTopY
            float stageTop = StageTopY - 0.2f;
            SpawnPrimitive("Stage", PrimitiveType.Cube, _theaterRoot.transform,
                new Vector3(0f, (stageTop + FloorY) * 0.5f, z - 0.8f),
                new Vector3(RoomHalfW * 2f, stageTop - FloorY, 1.6f), black);
        }

        /// <summary>
        /// Stadium risers. Each row above the front one gets a tier whose top is
        /// RiserStep higher than the row in front. Tiers overlap, and the taller
        /// rear tier hides the lower one's back half.
        /// </summary>
        private void BuildRisers(Material carpet)
        {
            for (int row = 1; row < Rows; row++)
            {
                float topY = FloorY + row * RiserStep;
                float height = topY - FloorY;
                float rowZ = RowZ(row);
                float front = rowZ + 0.6f;
                float back  = rowZ - 1.2f;

                SpawnPrimitive($"Riser_{row}", PrimitiveType.Cube, _theaterRoot.transform,
                    new Vector3(0f, FloorY + height * 0.5f, (front + back) * 0.5f),
                    new Vector3(10f, height, front - back), carpet);
            }
        }

        /// <summary>Seat rows. Each seat is a parent anchor at seated head height plus cushion, backrest, armrests and stand.</summary>
        private void BuildSeats(Material fabric, Material metal)
        {
            _seatAnchors.Clear();

            for (int row = 0; row < Rows; row++)
            {
                float floorY = FloorY + row * RiserStep;

                foreach (int offset in ColumnOrder)
                {
                    if (Mathf.Abs(offset) > SeatsPerRow / 2) continue;

                    float x = offset * SeatPitch;
                    float z = RowZ(row) - (x * x) / (2f * ArcRadius);

                    // Seat anchor at seated head height. Friends' avatars and the viewer's
                    // head both use this point.
                    int seatIndex = _seatAnchors.Count;
                    var anchor = new GameObject($"Seat_{seatIndex}").transform;
                    anchor.SetParent(_theaterRoot.transform, false);
                    anchor.localPosition = new Vector3(x, floorY + EyeHeight, z);

                    // Face the screen centre
                    float yaw = Mathf.Atan2(-x, ScreenPlaneZ - z) * Mathf.Rad2Deg;
                    anchor.localRotation = Quaternion.Euler(0f, yaw, 0f);

                    // Child parts. Positions are given relative to the floor and then
                    // converted to the anchor's local space.
                    SpawnSeatPart(anchor, "Stand",    0f,    0.19f, 0f,     0.50f, 0.38f, 0.46f, metal);
                    SpawnSeatPart(anchor, "Cushion",  0f,    0.44f, 0f,     0.54f, 0.10f, 0.50f, fabric);
                    SpawnSeatPart(anchor, "Backrest", 0f,    0.80f, -0.22f, 0.54f, 0.62f, 0.10f, fabric);
                    SpawnSeatPart(anchor, "ArmL",    -0.31f, 0.64f, -0.02f, 0.07f, 0.08f, 0.44f, metal);
                    SpawnSeatPart(anchor, "ArmR",     0.31f, 0.64f, -0.02f, 0.07f, 0.08f, 0.44f, metal);

                    _seatAnchors.Add(anchor);
                }
            }
        }

        /// <summary>Small unlit strips at the front edge of each tier, along both aisles.</summary>
        private void BuildStepLights(Material stepLit)
        {
            for (int row = 0; row < Rows; row++)
            {
                float y = FloorY + row * RiserStep + 0.02f;
                float z = RowZ(row) + 0.1f;

                foreach (int side in new[] { -1, 1 })
                {
                    string sideName = side < 0 ? "L" : "R";
                    SpawnPrimitive($"StepLight_{row}_{sideName}", PrimitiveType.Cube,
                        _theaterRoot.transform,
                        new Vector3(side * AisleLightX, y, z),
                        new Vector3(0.08f, 0.02f, 1f), stepLit);
                }
            }
        }

        /// <summary>Four very dim warm point lights on the side walls. Shadows are off for performance.</summary>
        private void BuildWarmLights()
        {
            Vector3[] positions =
            {
                new Vector3(-7.2f, 2.2f, -2.5f),
                new Vector3( 7.2f, 2.2f, -2.5f),
                new Vector3(-7.2f, 2.2f,  2.5f),
                new Vector3( 7.2f, 2.2f,  2.5f)
            };

            for (int i = 0; i < positions.Length; i++)
            {
                var go = new GameObject($"WarmLight_{i}");
                go.transform.SetParent(_theaterRoot.transform, false);
                go.transform.localPosition = positions[i];

                var light = go.AddComponent<Light>();
                light.type      = LightType.Point;
                light.color     = new Color(1f, 0.72f, 0.45f);
                light.intensity = 0.6f;
                light.range     = 6f;
                light.shadows   = LightShadows.None;
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Helpers

        /// <summary>Z of the centre of row r (row 0 is the viewer's row, rows rise toward the back).</summary>
        private static float RowZ(int row) => -row * RowPitch;

        /// <summary>Spawns a seat part at floor-relative height, expressed in the seat anchor's local space.</summary>
        private void SpawnSeatPart(Transform anchor, string name,
                                   float x, float yAboveFloor, float z,
                                   float sx, float sy, float sz, Material mat)
        {
            SpawnPrimitive(name, PrimitiveType.Cube, anchor,
                new Vector3(x, yAboveFloor - EyeHeight, z),
                new Vector3(sx, sy, sz), mat);
        }

        /// <summary>
        /// Creates a primitive, removes its collider, assigns the material and
        /// disables shadow work. Returns the new GameObject.
        /// </summary>
        private static GameObject SpawnPrimitive(string name, PrimitiveType type, Transform parent,
                                                 Vector3 localPos, Vector3 localScale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale    = localScale;

            // Visual geometry only
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                rend.sharedMaterial = mat;
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                rend.receiveShadows = false;
            }
            return go;
        }

        #endregion
    }
}
