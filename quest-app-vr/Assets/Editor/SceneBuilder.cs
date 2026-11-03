// SceneBuilder.cs — Programmatically constructs MainScene so the batch build
// never depends on a hand-edited YAML scene file.
//
// Called by BuildScript.BuildAPK() before the actual build.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.Video;
using UnityEditor;
using UnityEditor.SceneManagement;
using TMPro;
using MoviePlayer.App;
using MoviePlayer.Player;
using MoviePlayer.Room;
using MoviePlayer.Theater;

// Input System types – present since the project has com.unity.inputsystem
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.InputSystem.UI;

public static class SceneBuilder
{
    const string SCENE_PATH = "Assets/App/MainScene.unity";

    // ─── UI palette (dark glass + single soft-blue accent) ───────────────────

    static readonly Color Glass         = new Color(0.063f, 0.075f, 0.094f, 0.82f); // #101318 @ 82%
    static readonly Color FieldFill     = new Color(0.110f, 0.125f, 0.149f, 0.90f); // slightly lifted
    static readonly Color GlassEdge     = new Color(1f, 1f, 1f, 0.08f);             // 1px hairline
    static readonly Color FieldEdge     = new Color(1f, 1f, 1f, 0.14f);
    static readonly Color Accent        = new Color(0.298f, 0.553f, 1.000f, 1f);    // #4C8DFF
    static readonly Color AccentHover   = new Color(0.420f, 0.650f, 1.000f, 1f);
    static readonly Color AccentPressed = new Color(0.220f, 0.440f, 0.850f, 1f);
    static readonly Color TextPrimary   = new Color(1f, 1f, 1f, 1f);
    static readonly Color TextMuted     = new Color(0.600f, 0.640f, 0.710f, 1f);
    static readonly Color ButtonDisabled = new Color(0.200f, 0.220f, 0.260f, 1f);

    // Card size for every panel; centred on the canvas so child anchors are unchanged
    static readonly Vector2 CardSize = new Vector2(1440f, 1040f);

    // Built-in rounded sprite, loaded once per editor session
    static Sprite _roundedSprite;
    static bool   _roundedLoaded;

    // ─── Public entry point ──────────────────────────────────────────────────

    public static void BuildMainScene()
    {
        Debug.Log("[SceneBuilder] ── Building MainScene ──────────────────────────");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // 1 ── Ambient lighting
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.06f, 0.05f, 0.05f);

        // 2 ── Directional light (dim; cinema is dark)
        var lightGO = new GameObject("Directional Light");
        var light = lightGO.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 0.4f;
        light.color = new Color(1f, 0.95f, 0.9f);
        lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        // 3 ── XR Rig  (XR Origin → Camera Offset → Main Camera)
        var (xrOrigin, cameraOffsetGO, mainCamGO) = CreateXRRig();
        _ = xrOrigin; // suppress warning

        // 4 ── App game object (single GO holds all MonoBehaviour components)
        var app = CreateAppGameObject();

        // 5 ── ClarityShader material → wire to screen renderer + clarity stack
        var clarityMat = FindOrCreateClarityMaterial();
        SetSerializedField(app.GetComponent<StereoScreenRenderer>(), "ScreenMaterial",   clarityMat);
        SetSerializedField(app.GetComponent<ClarityStack>(),         "ClarityMaterial",  clarityMat);

        // 6 ── World-space Canvas  (2 m in front of camera)
        var canvasRoot = CreateWorldCanvas(mainCamGO.GetComponent<Camera>());
        canvasRoot.transform.position = new Vector3(0, 1.5f, 2f);

        // 7 ── UI panels + widgets, returns a bag of named GOs/components
        var ui = BuildUI(canvasRoot);

        // 8 ── Debug overlay (always visible, below UI)
        CreateDebugOverlay(canvasRoot);

        // 9 ── EventSystem with Input System UI
        CreateEventSystem();

        // 10 ── GazeClick on Main Camera
        mainCamGO.AddComponent<GazeClick>();

        // 11 ── Wire all component cross-references
        WireAppReferences(app, ui, canvasRoot, clarityMat);

        // 12 ── Save
        Directory.CreateDirectory(Path.GetDirectoryName(
            Path.Combine(Application.dataPath, "..", SCENE_PATH))
            .Replace('/', Path.DirectorySeparatorChar));

        EditorSceneManager.SaveScene(scene, SCENE_PATH);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(SCENE_PATH, true) };
        AssetDatabase.SaveAssets();
        Debug.Log("[SceneBuilder] MainScene saved → " + SCENE_PATH);
    }

    // ─── XR Rig ──────────────────────────────────────────────────────────────

    static (GameObject origin, GameObject offset, GameObject cam) CreateXRRig()
    {
        // XR Origin
        var origin = new GameObject("XR Origin");

        // Camera Offset (y=1.6 for standing height)
        var offset = new GameObject("Camera Offset");
        offset.transform.SetParent(origin.transform, false);
        offset.transform.localPosition = new Vector3(0f, 1.6f, 0f);

        // Main Camera
        var camGO = new GameObject("Main Camera");
        camGO.tag = "MainCamera";
        camGO.transform.SetParent(offset.transform, false);

        var cam = camGO.AddComponent<Camera>();
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane  = 500f;
        cam.clearFlags    = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.02f, 0.02f, 0.02f, 1f);
        cam.stereoTargetEye = StereoTargetEyeMask.Both;

        // TrackedPoseDriver (Input System) – drives HMD tracking
        AddTrackedPoseDriver(camGO);

        // AudioListener
        camGO.AddComponent<AudioListener>();

        return (origin, offset, camGO);
    }

    static void AddTrackedPoseDriver(GameObject camGO)
    {
        try
        {
            var tpd = camGO.AddComponent<TrackedPoseDriver>();
            tpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            tpd.updateType   = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;

            // Inline Input Actions bound to OpenXR HMD pose
            var posAction = new InputAction("Position",
                InputActionType.PassThrough, "<XRHMD>/centerEyePosition");
            var rotAction = new InputAction("Rotation",
                InputActionType.PassThrough, "<XRHMD>/centerEyeRotation");

            tpd.positionInput = new InputActionProperty(posAction);
            tpd.rotationInput = new InputActionProperty(rotAction);

            Debug.Log("[SceneBuilder] TrackedPoseDriver added to Main Camera.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SceneBuilder] TrackedPoseDriver setup failed: {e.Message}  (non-fatal)");
        }
    }

    // ─── App game object ─────────────────────────────────────────────────────

    static GameObject CreateAppGameObject()
    {
        var app = new GameObject("App");

        // Add all MonoBehaviour components – order matters for RequireComponent
        SafeAdd<AppController>(app);
        SafeAdd<StereoScreenRenderer>(app);
        SafeAdd<ClarityStack>(app);
        SafeAdd<HlsPlayerController>(app);
        SafeAdd<TheaterEnvironment>(app);
        SafeAdd<RoomClient>(app);
        SafeAdd<SyncEngine>(app);
        SafeAdd<PreCacheLobby>(app);
        SafeAdd<VoiceChat>(app);
        SafeAdd<LobbyUI>(app);

        return app;
    }

    // ─── Clarity material ────────────────────────────────────────────────────

    static Material FindOrCreateClarityMaterial()
    {
        // Look for an existing material that uses ClarityShader
        var guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets/Player" });
        foreach (var guid in guids)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (mat != null && mat.shader != null &&
                mat.shader.name.Contains("ClarityShader"))
                return mat;
        }

        // Create one
        var shader = Shader.Find("MoviePlayer/ClarityShader");
        if (shader == null) shader = Shader.Find("Unlit/Texture");

        var newMat = new Material(shader) { name = "ClarityScreen" };
        newMat.color = Color.white;
        AssetDatabase.CreateAsset(newMat, "Assets/Player/ClarityScreen.mat");
        AssetDatabase.SaveAssets();
        return newMat;
    }

    // ─── World-space Canvas ──────────────────────────────────────────────────

    static GameObject CreateWorldCanvas(Camera eventCamera)
    {
        var canvasGO = new GameObject("UI");

        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.WorldSpace;
        canvas.worldCamera  = eventCamera;
        canvas.sortingOrder = 0;

        // 1600×1200 px at 0.001 scale = 1.6 m × 1.2 m in front of the user.
        // Widget positions are in this pixel space (hundreds of px), so the
        // rect must be pixel-sized or the controls land hundreds of metres away.
        var rt = canvasGO.GetComponent<RectTransform>();
        rt.sizeDelta  = new Vector2(1600f, 1200f);
        rt.localScale = new Vector3(0.001f, 0.001f, 0.001f);

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor         = 1f;
        scaler.dynamicPixelsPerUnit = 10f;

        canvasGO.AddComponent<GraphicRaycaster>();
        canvasGO.AddComponent<UiFontBootstrap>();

        return canvasGO;
    }

    // ─── UI panels ───────────────────────────────────────────────────────────

    struct UIBag
    {
        public GameObject SetupPanel, LibraryPanel, MovieDetailPanel, WatchingPanel;
        public TMP_InputField MediaUrlInput, ApiUrlInput;
        public Button SaveSetupButton;
        public Transform LibraryScrollContent;
        public TMP_Text DetailTitle, DetailMeta;
        public Button WatchButton, EnhanceButton, JoinButton;
        public TMP_InputField InviteCodeInput;
        // LobbyUI sub-elements inside WatchingPanel
        public TMP_Text InviteCodeLabel, StatusLabel, GuestStatusLabel;
        public Transform SeatRowContainer;
        public Button PlayWhenReadyButton, PlayNowButton;
        public GameObject HostControlsPanel, GuestStatusPanel;
    }

    static UIBag BuildUI(GameObject canvasGO)
    {
        var bag = new UIBag();

        // ── Setup Panel
        bag.SetupPanel = CreatePanel(canvasGO, "SetupPanel", Glass);
        AddLabel(bag.SetupPanel,   "TitleLabel",    "VR Cinema", 36, new Vector2(0f, 200f));
        AddLabel(bag.SetupPanel,   "InstructLabel", "Enter your Home-Node URLs to continue.",
                 18, new Vector2(0f, 140f));

        AddLabel(bag.SetupPanel,   "MediaUrlLabel", "Packager URL (https://…/)", 14, new Vector2(-320f, 70f));
        bag.MediaUrlInput  = AddInputField(bag.SetupPanel, "MediaUrlInput",
                                           "https://xxxx.trycloudflare.com", new Vector2(0f, 30f));

        AddLabel(bag.SetupPanel,   "ApiUrlLabel",   "Sync-Server URL (https://…/)", 14, new Vector2(-320f, -40f));
        bag.ApiUrlInput    = AddInputField(bag.SetupPanel, "ApiUrlInput",
                                           "https://yyyy.trycloudflare.com", new Vector2(0f, -80f));

        bag.SaveSetupButton = AddButton(bag.SetupPanel, "SaveSetupButton", "Save & Continue",
                                        new Vector2(0f, -180f));

        // ── Library Panel
        bag.LibraryPanel = CreatePanel(canvasGO, "LibraryPanel", Glass);
        AddLabel(bag.LibraryPanel, "LibTitle", "Movie Library", 28, new Vector2(0f, 220f));

        var scroll = AddScrollView(bag.LibraryPanel, "LibraryScroll", new Vector2(0f, -20f));
        bag.LibraryScrollContent = scroll.content;

        // ── Movie Detail Panel
        bag.MovieDetailPanel = CreatePanel(canvasGO, "MovieDetailPanel", Glass);
        bag.DetailTitle   = AddLabel(bag.MovieDetailPanel, "DetailTitle", "Movie Title", 28, new Vector2(0f, 230f));
        bag.DetailMeta    = AddLabel(bag.MovieDetailPanel, "DetailMeta", "", 14, new Vector2(0f, 170f));
        bag.WatchButton   = AddButton(bag.MovieDetailPanel, "WatchButton",   "▶ Watch",   new Vector2(-150f, -200f));
        bag.EnhanceButton = AddButton(bag.MovieDetailPanel, "EnhanceButton", "✦ Enhance", new Vector2( 150f, -200f));

        var inviteRow = new GameObject("InviteRow", typeof(RectTransform));
        inviteRow.transform.SetParent(bag.MovieDetailPanel.transform, false);
        ((RectTransform)inviteRow.transform).anchoredPosition = new Vector2(0f, -120f);
        AddLabel(inviteRow, "JoinLabel", "Join code:", 14, new Vector2(-620f, 0f));
        bag.InviteCodeInput = AddInputField(inviteRow, "InviteCodeInput", "XXXXXX", new Vector2(-100f, 0f));
        bag.JoinButton      = AddButton(inviteRow, "JoinButton", "Join", new Vector2(400f, 0f));

        // ── Watching Panel (lobby)
        bag.WatchingPanel = CreatePanel(canvasGO, "WatchingPanel", Glass);
        bag.InviteCodeLabel    = AddLabel(bag.WatchingPanel, "InviteCodeLabel", "Room: ——", 24, new Vector2(0f, 230f));
        bag.InviteCodeLabel.color = Accent;
        bag.StatusLabel        = AddLabel(bag.WatchingPanel, "StatusLabel", "Waiting…", 16, new Vector2(0f, 180f));

        bag.HostControlsPanel  = new GameObject("HostControlsPanel", typeof(RectTransform));
        bag.HostControlsPanel.transform.SetParent(bag.WatchingPanel.transform, false);
        ((RectTransform)bag.HostControlsPanel.transform).anchoredPosition = new Vector2(0f, -100f);
        bag.PlayWhenReadyButton = AddButton(bag.HostControlsPanel, "PlayWhenReadyButton",
                                            "Play (when ready)", new Vector2(-150f, 0f));
        bag.PlayNowButton       = AddButton(bag.HostControlsPanel, "PlayNowButton",
                                            "Play Now",          new Vector2( 150f, 0f));

        bag.GuestStatusPanel = new GameObject("GuestStatusPanel", typeof(RectTransform));
        bag.GuestStatusPanel.transform.SetParent(bag.WatchingPanel.transform, false);
        ((RectTransform)bag.GuestStatusPanel.transform).anchoredPosition = new Vector2(0f, -100f);
        bag.GuestStatusLabel = AddLabel(bag.GuestStatusPanel, "GuestStatusLabel",
                                        "Buffering…", 16, Vector2.zero);

        var seatContainer = new GameObject("SeatRowContainer", typeof(RectTransform));
        seatContainer.transform.SetParent(bag.WatchingPanel.transform, false);
        var seatRT = (RectTransform)seatContainer.transform;
        seatRT.anchoredPosition = new Vector2(0f, 0f);
        seatRT.sizeDelta        = new Vector2(800f, 400f);
        seatContainer.AddComponent<VerticalLayoutGroup>();
        bag.SeatRowContainer = seatContainer.transform;

        // Initially only SetupPanel is active; others hidden
        bag.SetupPanel.SetActive(true);
        bag.LibraryPanel.SetActive(false);
        bag.MovieDetailPanel.SetActive(false);
        bag.WatchingPanel.SetActive(false);

        return bag;
    }

    static void CreateDebugOverlay(GameObject canvasGO)
    {
        var go = new GameObject("DebugOverlay");
        go.transform.SetParent(canvasGO.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin        = new Vector2(0f, 0f);
        rt.anchorMax        = new Vector2(1f, 0f);
        rt.offsetMin        = new Vector2(20f,  10f);
        rt.offsetMax        = new Vector2(-20f, 200f);
        rt.localScale       = Vector3.one;

        var bg = go.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.7f);

        var textGO = new GameObject("DebugText");
        textGO.transform.SetParent(go.transform, false);
        var textRT = textGO.AddComponent<RectTransform>();
        textRT.anchorMin  = Vector2.zero;
        textRT.anchorMax  = Vector2.one;
        textRT.offsetMin  = new Vector2(8f, 4f);
        textRT.offsetMax  = new Vector2(-8f, -4f);

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.fontSize      = 10f;
        tmp.color         = new Color(0.8f, 1f, 0.8f, 1f);
        tmp.alignment     = TextAlignmentOptions.BottomLeft;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode  = TextOverflowModes.Truncate;

        go.AddComponent<DebugOverlay>();
    }

    // ─── EventSystem ─────────────────────────────────────────────────────────

    static void CreateEventSystem()
    {
        var esGO = new GameObject("EventSystem");
        esGO.AddComponent<EventSystem>();
        try
        {
            esGO.AddComponent<InputSystemUIInputModule>();
            Debug.Log("[SceneBuilder] InputSystemUIInputModule added.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SceneBuilder] InputSystemUIInputModule failed: {e.Message}  — falling back to StandaloneInputModule.");
            esGO.AddComponent<StandaloneInputModule>();
        }
    }

    // ─── Wire references ─────────────────────────────────────────────────────

    static void WireAppReferences(GameObject app, UIBag ui, GameObject canvasRoot,
                                  Material clarityMat)
    {
        var ac  = app.GetComponent<AppController>();
        var sc  = app.GetComponent<StereoScreenRenderer>();
        var cs  = app.GetComponent<ClarityStack>();
        var hls = app.GetComponent<HlsPlayerController>();
        var se  = app.GetComponent<SyncEngine>();
        var te  = app.GetComponent<TheaterEnvironment>();
        var rc  = app.GetComponent<RoomClient>();
        var pcl = app.GetComponent<PreCacheLobby>();
        var vc  = app.GetComponent<VoiceChat>();
        var lu  = app.GetComponent<LobbyUI>();

        // AppController ← component refs
        SetSerializedField(ac, "PlayerController", hls);
        SetSerializedField(ac, "ScreenRenderer",   sc);
        SetSerializedField(ac, "ClarityStack",     cs);
        SetSerializedField(ac, "Theater",          te);
        SetSerializedField(ac, "RoomClient",       rc);
        SetSerializedField(ac, "PreCacheLobby",    pcl);
        SetSerializedField(ac, "LobbyUI",          lu);
        SetSerializedField(ac, "VoiceChat",        vc);

        // AppController ← UI panels
        SetSerializedField(ac, "SetupPanel",        ui.SetupPanel);
        SetSerializedField(ac, "LibraryPanel",      ui.LibraryPanel);
        SetSerializedField(ac, "MovieDetailPanel",  ui.MovieDetailPanel);
        SetSerializedField(ac, "WatchingPanel",     ui.WatchingPanel);

        // AppController ← UI widgets
        SetSerializedField(ac, "HomeNodeUrlInput",  ui.MediaUrlInput);
        SetSerializedField(ac, "ApiUrlInput",       ui.ApiUrlInput);
        SetSerializedField(ac, "SaveSetupButton",   ui.SaveSetupButton);
        SetSerializedField(ac, "LibraryGrid",       ui.LibraryScrollContent);
        SetSerializedField(ac, "DetailTitle",       ui.DetailTitle);
        SetSerializedField(ac, "DetailMeta",        ui.DetailMeta);
        SetSerializedField(ac, "WatchButton",       ui.WatchButton);
        SetSerializedField(ac, "EnhanceButton",     ui.EnhanceButton);
        SetSerializedField(ac, "JoinButton",        ui.JoinButton);
        SetSerializedField(ac, "InviteCodeInput",   ui.InviteCodeInput);

        // LobbyUI ← component + UI refs
        SetSerializedField(lu, "LobbyLogic",          pcl);
        SetSerializedField(lu, "InviteCodeLabel",     ui.InviteCodeLabel);
        SetSerializedField(lu, "StatusLabel",         ui.StatusLabel);
        SetSerializedField(lu, "SeatRowContainer",    ui.SeatRowContainer);
        SetSerializedField(lu, "PlayWhenReadyButton", ui.PlayWhenReadyButton);
        SetSerializedField(lu, "PlayNowButton",       ui.PlayNowButton);
        SetSerializedField(lu, "HostControlsPanel",   ui.HostControlsPanel);
        SetSerializedField(lu, "GuestStatusLabel",    ui.GuestStatusLabel);
        SetSerializedField(lu, "GuestStatusPanel",    ui.GuestStatusPanel);

        // VoiceChat ← movie player
        SetSerializedField(vc, "MoviePlayer", hls);

        // HlsPlayerController ← screen renderer (for RenderTexture assignment)
        SetSerializedField(hls, "ScreenRenderer", sc);

        // SyncEngine ← player
        SetSerializedField(se, "Player", hls);

        Debug.Log("[SceneBuilder] All component references wired.");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    static T SafeAdd<T>(GameObject go) where T : Component
    {
        var existing = go.GetComponent<T>();
        if (existing != null) return existing;
        return go.AddComponent<T>();
    }

    static void SetSerializedField(UnityEngine.Object target, string fieldName,
                                   UnityEngine.Object value)
    {
        if (target == null || value == null) return;
        var so = new SerializedObject(target);
        var prop = so.FindProperty(fieldName);
        if (prop == null) return;
        prop.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // Built-in rounded-rectangle sprite (sliced). Returns null if unavailable,
    // in which case widgets fall back to plain rectangles.
    static Sprite RoundedSprite()
    {
        if (!_roundedLoaded)
        {
            _roundedSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            _roundedLoaded = true;
        }
        return _roundedSprite;
    }

    static void StyleRounded(Image img, Color color)
    {
        img.color = color;
        var sprite = RoundedSprite();
        if (sprite != null)
        {
            img.sprite = sprite;
            img.type   = Image.Type.Sliced;
        }
    }

    // Centred rounded "glass" card with a 1px hairline edge. Children keep
    // their centre-anchored positions because the card is centred on the canvas.
    static GameObject CreatePanel(GameObject parent, string name, Color bgColor)
    {
        var panel = new GameObject(name);
        panel.transform.SetParent(parent.transform, false);
        var rt = panel.AddComponent<RectTransform>();
        rt.anchorMin        = new Vector2(0.5f, 0.5f);
        rt.anchorMax        = new Vector2(0.5f, 0.5f);
        rt.pivot            = new Vector2(0.5f, 0.5f);
        rt.sizeDelta        = CardSize;
        rt.anchoredPosition = Vector2.zero;

        var img = panel.AddComponent<Image>();
        StyleRounded(img, bgColor);

        var edge = panel.AddComponent<UnityEngine.UI.Outline>();
        edge.effectColor    = GlassEdge;
        edge.effectDistance = new Vector2(1f, -1f);

        return panel;
    }

    // Creates a TMP_Text label and returns the component.
    // Titles (fontSize >= 24) are bold white; everything else is muted grey.
    static TMP_Text AddLabel(GameObject parent, string name, string text,
                             float fontSize, Vector2 anchoredPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta        = new Vector2(800f, 60f);
        rt.anchoredPosition = anchoredPos;

        bool isTitle = fontSize >= 24f;

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.fontSize  = fontSize;
        tmp.fontStyle = isTitle ? FontStyles.Bold : FontStyles.Normal;
        tmp.color     = isTitle ? TextPrimary : TextMuted;
        tmp.alignment = TextAlignmentOptions.Center;
        return tmp;
    }

    static TMP_InputField AddInputField(GameObject parent, string name,
                                        string placeholder, Vector2 anchoredPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta        = new Vector2(700f, 56f);
        rt.anchoredPosition = anchoredPos;

        var img = go.AddComponent<Image>();
        StyleRounded(img, FieldFill);

        // Lighter hairline so the field reads as an input on the glass card
        var outline = go.AddComponent<UnityEngine.UI.Outline>();
        outline.effectColor    = FieldEdge;
        outline.effectDistance = new Vector2(1f, -1f);

        var textArea = new GameObject("Text Area");
        textArea.transform.SetParent(go.transform, false);
        var taRT = textArea.AddComponent<RectTransform>();
        taRT.anchorMin = Vector2.zero;
        taRT.anchorMax = Vector2.one;
        taRT.offsetMin = new Vector2(20f, 6f);
        taRT.offsetMax = new Vector2(-20f, -6f);

        var inputText = new GameObject("InputText");
        inputText.transform.SetParent(textArea.transform, false);
        var itRT = inputText.AddComponent<RectTransform>();
        itRT.anchorMin = Vector2.zero;
        itRT.anchorMax = Vector2.one;
        itRT.offsetMin = Vector2.zero;
        itRT.offsetMax = Vector2.zero;
        var inputTMP = inputText.AddComponent<TextMeshProUGUI>();
        inputTMP.fontSize = 20f;
        inputTMP.color    = TextPrimary;

        var phGO = new GameObject("Placeholder");
        phGO.transform.SetParent(textArea.transform, false);
        var phRT = phGO.AddComponent<RectTransform>();
        phRT.anchorMin = Vector2.zero;
        phRT.anchorMax = Vector2.one;
        phRT.offsetMin = Vector2.zero;
        phRT.offsetMax = Vector2.zero;
        var phTMP = phGO.AddComponent<TextMeshProUGUI>();
        phTMP.text     = placeholder;
        phTMP.fontSize = 20f;
        phTMP.color    = TextMuted;

        var field = go.AddComponent<TMP_InputField>();
        field.textViewport  = taRT;
        field.textComponent = inputTMP;
        field.placeholder   = phTMP;
        field.customCaretColor = true;
        field.caretColor       = TextPrimary;
        field.selectionColor   = WithAlpha(Accent, 0.35f);

        return field;
    }

    static Color WithAlpha(Color c, float alpha)
    {
        return new Color(c.r, c.g, c.b, alpha);
    }

    // Pill-style primary button. The built-in UISprite has a fixed corner
    // radius, so the shape is a rounded rectangle at 64 px height; a true
    // full pill would need a generated sprite asset.
    static Button AddButton(GameObject parent, string name, string label,
                            Vector2 anchoredPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta        = new Vector2(260f, 64f);
        rt.anchoredPosition = anchoredPos;

        // White base so the ColorBlock below supplies the real button colour
        var img = go.AddComponent<Image>();
        StyleRounded(img, Color.white);

        var textGO = new GameObject("Label");
        textGO.transform.SetParent(go.transform, false);
        var tRT = textGO.AddComponent<RectTransform>();
        tRT.anchorMin = Vector2.zero;
        tRT.anchorMax = Vector2.one;
        tRT.offsetMin = Vector2.zero;
        tRT.offsetMax = Vector2.zero;
        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text      = label;
        tmp.fontSize  = 20f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color     = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;

        var cb = ColorBlock.defaultColorBlock;
        cb.normalColor      = Accent;
        cb.highlightedColor = AccentHover;
        cb.pressedColor     = AccentPressed;
        cb.selectedColor    = Accent;
        cb.disabledColor    = ButtonDisabled;
        cb.colorMultiplier  = 1f;
        cb.fadeDuration     = 0.08f;
        btn.colors = cb;

        return btn;
    }

    struct ScrollViewResult { public Transform content; }

    static ScrollViewResult AddScrollView(GameObject parent, string name, Vector2 anchoredPos)
    {
        var sv = new GameObject(name);
        sv.transform.SetParent(parent.transform, false);
        var svRT = sv.AddComponent<RectTransform>();
        svRT.sizeDelta        = new Vector2(800f, 400f);
        svRT.anchoredPosition = anchoredPos;

        // Transparent: keeps the raycast target for drag-scrolling without a visible box
        var bg = sv.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0f);

        var scrollRect = sv.AddComponent<ScrollRect>();
        scrollRect.horizontal = false;

        var viewport = new GameObject("Viewport");
        viewport.transform.SetParent(sv.transform, false);
        var vpRT = viewport.AddComponent<RectTransform>();
        vpRT.anchorMin = Vector2.zero;
        vpRT.anchorMax = Vector2.one;
        vpRT.offsetMin = Vector2.zero;
        vpRT.offsetMax = Vector2.zero;
        viewport.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        viewport.AddComponent<Mask>().showMaskGraphic = false;

        var content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        var cRT = content.AddComponent<RectTransform>();
        cRT.anchorMin = new Vector2(0f, 1f);
        cRT.anchorMax = new Vector2(1f, 1f);
        cRT.pivot     = new Vector2(0.5f, 1f);
        cRT.offsetMin = Vector2.zero;
        cRT.offsetMax = Vector2.zero;
        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.childControlWidth  = true;
        vlg.childControlHeight = false;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.padding = new RectOffset(12, 12, 12, 12);
        vlg.spacing = 8f;
        content.AddComponent<ContentSizeFitter>().verticalFit =
            ContentSizeFitter.FitMode.PreferredSize;

        scrollRect.viewport = vpRT;
        scrollRect.content  = cRT;

        return new ScrollViewResult { content = content.transform };
    }
}

// Extension to avoid null check chains
static class GoExt
{
    public static T GetOrAddComponent<T>(this GameObject go) where T : Component
    {
        return go.GetComponent<T>() ?? go.AddComponent<T>();
    }
}
