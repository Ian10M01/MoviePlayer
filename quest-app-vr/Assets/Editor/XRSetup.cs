// XRSetup.cs — Configures XR Management (OpenXR + Meta Quest) for Android build.
// Called by BuildScript.BuildAPK() before the actual build.

using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEngine.XR.Management;
using System.Linq;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using MoviePlayer.App;

public static class XRSetup
{
    public static void Configure()
    {
        Debug.Log("[XRSetup] ── Configuring XR ───────────────────────────────────");
        ConfigureXRLoader();
        EnableOpenXRFeatures();
        ConfigurePlayerSettings();
        WriteAndroidManifest();
        IncludeRuntimeShaders();
        EnsurePanelBlitFeature();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[XRSetup] Done.");
    }

    // ─── 0. Shaders created from code ────────────────────────────────────────
    // Runtime scripts build materials with Shader.Find. A shader no asset references is
    // stripped from the build, and Shader.Find then returns null (pink objects).

    static void EnsurePanelBlitFeature()
    {
        const string path = "Assets/Settings/Project Configuration/Android Preset.asset";
        var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
        if (data == null)
        {
            Debug.LogWarning("[XRSetup] Android renderer preset missing");
            return;
        }
        foreach (var existing in data.rendererFeatures)
        {
            if (existing is PanelUiFeature) return;
        }
        var feature = ScriptableObject.CreateInstance<PanelUiFeature>();
        feature.name = "PanelUiFeature";
        AssetDatabase.AddObjectToAsset(feature, data);
        data.rendererFeatures.Add(feature);
        data.SetDirty();
        EditorUtility.SetDirty(data);
        Debug.Log("[XRSetup] Panel UI blit feature added");
    }

    static readonly string[] RuntimeShaders =
    {
        "Universal Render Pipeline/Unlit",
        "Universal Render Pipeline/Lit",
        "UI/Default",
        "Sprites/Default",
        "MoviePlayer/FlatUI",
        "MoviePlayer/PresentUI",
        "Hidden/MoviePlayer/VideoEnhance",
        "MoviePlayer/PanelStereo",
    };

    static void IncludeRuntimeShaders()
    {
        var settings = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("ProjectSettings/GraphicsSettings.asset");
        var so = new SerializedObject(settings);
        var list = so.FindProperty("m_AlwaysIncludedShaders");

        foreach (var name in RuntimeShaders)
        {
            var shader = Shader.Find(name);
            if (shader == null) { Debug.LogWarning($"[XRSetup] Shader not found: {name}"); continue; }

            bool present = false;
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) present = true;
            if (present) continue;

            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            Debug.Log($"[XRSetup] Always-included shader added: {name}");
        }
        so.ApplyModifiedProperties();
    }

    // ─── 1. XR loader assignment ─────────────────────────────────────────────

    static void ConfigureXRLoader()
    {
        const string containerPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";

        // Load or create the per-build-target container
        var container =
            AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(containerPath);

        if (container == null)
        {
            container = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            Directory.CreateDirectory(Path.GetDirectoryName(
                Path.Combine(Application.dataPath, "..", containerPath)));
            AssetDatabase.CreateAsset(container, containerPath);
        }

        // Android general settings
        var androidSettings = container.SettingsForBuildTarget(BuildTargetGroup.Android);
        if (androidSettings == null)
        {
            androidSettings = ScriptableObject.CreateInstance<XRGeneralSettings>();
            AssetDatabase.AddObjectToAsset(androidSettings, container);
            AssetDatabase.SaveAssets();
            container.SetSettingsForBuildTarget(BuildTargetGroup.Android, androidSettings);
            EditorUtility.SetDirty(container);
        }

        // XR Manager
        if (androidSettings.Manager == null)
        {
            var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
            AssetDatabase.AddObjectToAsset(manager, container);
            AssetDatabase.SaveAssets();

            // Assign manager via the known internal property / serialized field
            var managerProp = typeof(XRGeneralSettings)
                .GetProperty("Manager", BindingFlags.Public | BindingFlags.Instance);
            if (managerProp != null && managerProp.CanWrite)
                managerProp.SetValue(androidSettings, manager);
            else
            {
                var so = new SerializedObject(androidSettings);
                var p  = so.FindProperty("m_Manager");
                if (p != null) { p.objectReferenceValue = manager; so.ApplyModifiedPropertiesWithoutUndo(); }
            }
        }

        androidSettings.InitManagerOnStart = false;

        // Assign the OpenXR loader
        bool assigned = XRPackageMetadataStore.AssignLoader(
            androidSettings.Manager,
            "UnityEngine.XR.OpenXR.OpenXRLoader",
            BuildTargetGroup.Android);

        Debug.Log($"[XRSetup] OpenXR loader assigned: {assigned}");

        // Register with EditorBuildSettings so the runtime finds it
        // k_SettingsKey is deprecated in 4.7+ → use settingsKey if available
        string settingsKey;
        try   { settingsKey = XRGeneralSettings.k_SettingsKey; }
        catch { settingsKey = "com.unity.xr.management.loader"; }
        EditorBuildSettings.AddConfigObject(settingsKey, container, true);

        EditorUtility.SetDirty(container);
        EditorUtility.SetDirty(androidSettings);
        if (androidSettings.Manager != null)
            EditorUtility.SetDirty(androidSettings.Manager);

        // Quest only starts OpenXR when these settings are in the player preload list.
        EnsurePreloadedAssets(androidSettings);
    }

    static void EnsurePreloadedAssets(XRGeneralSettings androidSettings)
    {
        var list = new System.Collections.Generic.List<UnityEngine.Object>();
        if (androidSettings != null)
            list.Add(androidSettings);

        string settingsAssetPath = OpenXrSettingsPath();
        var androidOpenXr = AssetDatabase.LoadAllAssetsAtPath(settingsAssetPath)
            .FirstOrDefault(s => s != null && s.name == "Android");
        if (androidOpenXr != null)
            list.Add(androidOpenXr);

        PlayerSettings.SetPreloadedAssets(list.ToArray());
        Debug.Log($"[XRSetup] Preloaded XR assets: {list.Count}");
    }

    // ─── 2. Enable OpenXR features ───────────────────────────────────────────

    static void EnableOpenXRFeatures()
    {
        // Load OpenXR settings directly from the asset (GetSettingsFor may not exist in all versions)
        string settingsAssetPath = OpenXrSettingsPath();
        var settings = AssetDatabase.LoadAllAssetsAtPath(settingsAssetPath)
            .OfType<OpenXRSettings>()
            .FirstOrDefault(s => s != null && s.name == "Android");

        if (settings == null)
        {
            Debug.LogWarning("[XRSetup] OpenXR Android settings not found; features not enabled.");
            return;
        }

        // Get features via reflection (avoids hard-coding assembly-qualified names)
        OpenXRFeature[] features = GetAllFeatures(settings);
        if (features == null || features.Length == 0)
        {
            Debug.LogWarning("[XRSetup] No OpenXR features found.");
            return;
        }

        foreach (var f in features)
        {
            if (f == null) continue;
            string n = f.GetType().Name;

            // OculusQuestFeature is deprecated in favour of MetaQuestFeature.
            // Enabling it causes a build error; disable it explicitly.
            if (n == "OculusQuestFeature")
            {
                if (f.enabled) { f.enabled = false; EditorUtility.SetDirty(f); }
                Debug.Log($"[XRSetup] Disabled deprecated: {n}");
                continue;
            }

            bool enable =
                n == "MetaQuestFeature"                     ||
                n == "OculusTouchControllerProfile"         ||
                n == "MetaQuestTouchPlusControllerProfile"  ||
                n == "MetaQuestTouchProControllerProfile";

            if (enable)
            {
                f.enabled = true;
                EditorUtility.SetDirty(f);
                Debug.Log($"[XRSetup] Enabled: {n}");
            }
        }

        // Render mode: Single Pass Instanced (= 1)
        settings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;

        // Depth: 16-bit (= 1)
        try { settings.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.Depth16Bit; }
        catch { /* enum may not exist in older version */ }

        EditorUtility.SetDirty(settings);
        Debug.Log("[XRSetup] OpenXR settings configured (SPIn, 16-bit depth).");
    }

    static OpenXRFeature[] GetAllFeatures(OpenXRSettings settings)
    {
        // Try the public generic GetFeatures<T> via reflection
        var method = settings.GetType()
            .GetMethod("GetFeatures", new[] { typeof(Type) });

        if (method != null)
        {
            try { return method.Invoke(settings, new object[] { typeof(OpenXRFeature) })
                               as OpenXRFeature[]; }
            catch { }
        }

        // Fallback: non-public "features" field
        var field = settings.GetType()
            .GetField("features", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (field != null)
        {
            var val = field.GetValue(settings);
            if (val is OpenXRFeature[] arr) return arr;
            // Might be a List
            if (val is System.Collections.IList list)
            {
                var result = new OpenXRFeature[list.Count];
                for (int i = 0; i < list.Count; i++)
                    result[i] = list[i] as OpenXRFeature;
                return result;
            }
        }

        return null;
    }

    // quest-app-vr stores this at the unspaced path. The spaced name belongs to quest-app.
    static string OpenXrSettingsPath()
    {
        const string real = "Assets/XR/Settings/OpenXRPackageSettings.asset";
        const string spaced = "Assets/XR/Settings/OpenXR Package Settings.asset";
        if (AssetDatabase.LoadMainAssetAtPath(real) != null)
        {
            Debug.Log("[XRSetup] OpenXR settings: " + real);
            return real;
        }
        if (AssetDatabase.LoadMainAssetAtPath(spaced) != null)
        {
            Debug.LogWarning("[XRSetup] Falling back to spaced OpenXR settings path.");
            return spaced;
        }
        Debug.LogError("[XRSetup] OpenXR settings asset missing at " + real);
        return real;
    }

    // ─── 3. Player settings ──────────────────────────────────────────────────

    static void ConfigurePlayerSettings()
    {
        PlayerSettings.applicationIdentifier = "com.movieplayer.vrcinema";
        PlayerSettings.productName           = "MoviePlayer";
        PlayerSettings.companyName           = "MoviePlayer";

        // Android SDK levels
        PlayerSettings.Android.minSdkVersion    = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;

        // ARM64 only (Quest requirement)
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

        // Use classic UnityPlayerActivity — Meta recommended for Quest, no AppCompat theme needed.
        // GameActivity requires a Theme.AppCompat theme which our minimal manifest doesn't supply.
        try { PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity; }
        catch { /* older SDK fallback */ }

        // Permissions
        PlayerSettings.Android.forceInternetPermission = true;

        // IL2CPP
        PlayerSettings.SetScriptingBackend(
            UnityEditor.Build.NamedBuildTarget.Android,
            ScriptingImplementation.IL2CPP);

        // Linear colour space (required for correct HDR video rendering)
        PlayerSettings.colorSpace = ColorSpace.Linear;

        // Graphics APIs: Vulkan first, then OpenGLES3
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[]
        {
            GraphicsDeviceType.Vulkan,
            GraphicsDeviceType.OpenGLES3
        });

        // Orientation
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;

        // Fullscreen would fight Horizon's resizable 2D panel. The theater activity is immersive on its own.
        // Never draws past the SurfaceView Horizon composites, so the 2D window stays black.
        try { PlayerSettings.Android.startInFullscreen = false; } catch { }
        try { PlayerSettings.Android.blitType = AndroidBlitType.Always; } catch { }
        try { PlayerSettings.SplashScreen.show = false;         } catch { }

        Debug.Log("[XRSetup] Player settings applied.");
    }

    // ─── 4. AndroidManifest.xml ──────────────────────────────────────────────

    static void WriteAndroidManifest()
    {
        const string dir  = "Assets/Plugins/Android";
        const string path = dir + "/AndroidManifest.xml";
        Directory.CreateDirectory(dir);

        // Classic UnityPlayerActivity — no AppCompat theme required (Meta recommended for Quest).
        string activityClass = "com.unity3d.player.UnityPlayerActivity";

        // Panel activity is the launcher (no VR category, resizable Horizon window).
        // TheaterActivity is a second Unity player process and is the only VR activity.
        string xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<manifest xmlns:android=""http://schemas.android.com/apk/res/android""
    xmlns:tools=""http://schemas.android.com/tools""
    package=""com.movieplayer.vrcinema"">

    <uses-permission android:name=""android.permission.INTERNET"" />
    <uses-permission android:name=""android.permission.RECORD_AUDIO"" />
    <uses-permission android:name=""android.permission.MODIFY_AUDIO_SETTINGS"" />
    <!-- Local movie library: READ_MEDIA_VIDEO (Android 13+), READ_EXTERNAL_STORAGE (older) -->
    <uses-permission android:name=""android.permission.READ_MEDIA_VIDEO"" />
    <uses-permission android:name=""android.permission.READ_EXTERNAL_STORAGE"" android:maxSdkVersion=""32"" />

    <!-- Optional so the library launches the 2D panel. The theater activity still enters VR. -->
    <uses-feature android:name=""android.hardware.vr.headtracking""
                  android:required=""false""
                  android:version=""1""
                  tools:replace=""android:required"" />

    <application
        android:allowBackup=""false""
        android:resizeableActivity=""true""
        android:usesCleartextTraffic=""true"">

        <activity
            android:name=""{activityClass}""
            android:theme=""@style/UnityThemeSelector""
            android:launchMode=""singleTask""
            android:resizeableActivity=""true""
            android:screenOrientation=""userLandscape""
            android:configChanges=""density|fontScale|keyboard|keyboardHidden|layoutDirection|locale|mcc|mnc|navigation|orientation|screenLayout|screenSize|smallestScreenSize|touchscreen|uiMode""
            android:hardwareAccelerated=""false""
            tools:replace=""android:hardwareAccelerated""
            android:exported=""true"">

            <intent-filter>
                <action  android:name=""android.intent.action.MAIN"" />
                <category android:name=""android.intent.category.LAUNCHER"" />
                <category android:name=""com.oculus.intent.category.VR"" tools:node=""remove"" />
            </intent-filter>

            <meta-data android:name=""unityplayer.UnityActivity"" android:value=""true"" />
            <meta-data android:name=""com.oculus.vr.focusaware"" tools:node=""remove"" />
            <property android:name=""android.window.PROPERTY_XR_ACTIVITY_START_MODE"" tools:node=""remove"" />
        </activity>

        <activity
            android:name=""com.movieplayer.vrcinema.TheaterActivity""
            android:theme=""@style/UnityThemeSelector""
            android:process="":theater""
            android:taskAffinity=""com.movieplayer.vrcinema.theater""
            android:launchMode=""singleTask""
            android:resizeableActivity=""false""
            android:screenOrientation=""landscape""
            android:configChanges=""density|keyboard|keyboardHidden|navigation|orientation|screenLayout|screenSize|uiMode""
            android:hardwareAccelerated=""true""
            android:exported=""true"">

            <intent-filter>
                <action android:name=""android.intent.action.MAIN"" />
                <category android:name=""com.oculus.intent.category.VR"" />
            </intent-filter>

            <meta-data android:name=""unityplayer.UnityActivity"" android:value=""true"" />
            <meta-data android:name=""com.oculus.vr.focusaware"" android:value=""true"" />
            <property android:name=""android.window.PROPERTY_XR_ACTIVITY_START_MODE""
                      android:value=""XR_ACTIVITY_START_MODE_FULL_SPACE_UNMANAGED"" />
        </activity>

        <!-- Supported Quest devices (tools:replace overrides the xrmanifest.androidlib default) -->
        <meta-data android:name=""com.oculus.supportedDevices""
                   android:value=""quest2|questpro|quest3|quest3s""
                   tools:replace=""android:value"" />
        <meta-data android:name=""com.oculus.handtracking.version""
                   android:value=""V2.0"" />
    </application>

</manifest>";

        File.WriteAllText(path, xml);
        Debug.Log($"[XRSetup] AndroidManifest.xml → {path}");
    }
}
