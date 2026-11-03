using UnityEditor;
using UnityEditor.Build.Reporting;
using System.IO;

public static class BuildScript
{
    public static void BuildAPK()
    {
        string outputPath = Path.Combine(System.Environment.CurrentDirectory, "..", "MoviePlayer.apk");

        BuildPlayerOptions opts = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/App/MainScene.unity" },
            locationPathName = outputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        };

        // Set Android settings
        PlayerSettings.applicationIdentifier = "com.movieplayer.vrcinema";
        PlayerSettings.productName = "MoviePlayer";
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);

        BuildReport report = BuildPipeline.BuildPlayer(opts);
        BuildSummary summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            UnityEngine.Debug.Log($"Build succeeded: {outputPath} ({summary.totalSize} bytes)");
        }
        else
        {
            UnityEngine.Debug.LogError($"Build FAILED: {summary.result}");
            EditorApplication.Exit(1);
        }
    }
}
