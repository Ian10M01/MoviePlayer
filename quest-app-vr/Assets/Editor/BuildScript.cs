using System;
using UnityEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;
using System.IO;

public static class BuildScript
{
    public static void BuildAPK()
    {
        Debug.Log("=== BuildScript.BuildAPK START ===");

        // Step 1 — Build the scene programmatically (replaces the hand-written YAML)
        try
        {
            SceneBuilder.BuildPanelScene();
            SceneBuilder.BuildMainScene();
        }
        catch (Exception e)
        {
            Debug.LogError($"[BuildScript] SceneBuilder failed: {e}");
            EditorApplication.Exit(1);
            return;
        }

        // Step 2 — Configure XR (OpenXR + Meta Quest, PlayerSettings, AndroidManifest)
        try
        {
            XRSetup.Configure();
        }
        catch (Exception e)
        {
            Debug.LogError($"[BuildScript] XRSetup failed: {e}");
            EditorApplication.Exit(1);
            return;
        }

        // Step 3 — Resolve the APK output path
        string out2 = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "..", "MoviePlayer.apk"));

        var opts = new BuildPlayerOptions
        {
            scenes           = new[]
            {
                "Assets/App/PanelScene.unity",
                "Assets/App/MainScene.unity",
            },
            locationPathName = out2,
            target           = BuildTarget.Android,
            options          = BuildOptions.None
        };

        Debug.Log($"=== Building APK → {out2} ===");
        var rep = BuildPipeline.BuildPlayer(opts);
        if (rep.summary.result == BuildResult.Succeeded)
            Debug.Log("APK built successfully: " + out2);
        else
        {
            Debug.LogError("BUILD FAILED: " + rep.summary.result);
            EditorApplication.Exit(1);
        }
    }
}
