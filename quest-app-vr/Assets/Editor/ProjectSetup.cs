using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// One-time setup after porting MoviePlayer into the VR template:
// removes the template's SampleScene, regenerates MainScene and makes it the only build scene.
// Runs automatically while SampleScene still exists; also available from the MoviePlayer menu.
public static class ProjectSetup
{
    const string SampleScene = "Assets/Scenes/SampleScene.unity";
    const string SampleDir   = "Assets/Scenes/SampleScene";
    const string MainScene   = "Assets/App/MainScene.unity";

    [InitializeOnLoadMethod]
    static void AutoRun()
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SampleScene) == null) return;
        EditorApplication.delayCall += Run;
    }

    [MenuItem("MoviePlayer/Rebuild MainScene")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        // Switch away from SampleScene before deleting it
        if (EditorSceneManager.GetActiveScene().path == SampleScene)
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SampleScene) != null)
            AssetDatabase.DeleteAsset(SampleScene);
        if (AssetDatabase.IsValidFolder(SampleDir))
            AssetDatabase.DeleteAsset(SampleDir);

        SceneBuilder.BuildMainScene();

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(MainScene, true) };
        EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
        AssetDatabase.SaveAssets();

        Debug.Log("[ProjectSetup] SampleScene removed; MainScene rebuilt and set as the build scene.");
    }
}
