using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor.Android;
using UnityEngine;

// After OpenXR/Meta inject their manifests, keep the launcher activity a
// resizable 2D panel and leave the VR category only on TheaterActivity.
public class AndroidManifestPatch : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 10000;

    static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";
    const string Panel = "com.unity3d.player.UnityPlayerActivity";
    const string Theater = "com.movieplayer.vrcinema.TheaterActivity";
    const string VrCategory = "com.oculus.intent.category.VR";

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string root = Path.GetFullPath(Path.Combine(path, ".."));
        foreach (var manifest in Directory.GetFiles(root, "AndroidManifest.xml", SearchOption.AllDirectories))
        {
            if (manifest.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}"))
                continue;
            try { Patch(manifest); }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Manifest] Could not patch {manifest}: {e.Message}");
            }
        }
    }

    static void Patch(string path)
    {
        var doc = XDocument.Load(path);
        var app = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "application");
        if (app == null) return;

        bool changed = false;
        XElement panel = null;
        XElement theater = null;
        foreach (var activity in app.Elements().Where(e => e.Name.LocalName == "activity").ToList())
        {
            string name = (string)activity.Attribute(Android + "name");
            if (name == Panel) panel = activity;
            else if (name == Theater) theater = activity;
        }

        if (panel != null)
        {
            Set(panel, "resizeableActivity", "true");
            Set(panel, "theme", "@style/UnityThemeSelector");
            Set(panel, "screenOrientation", "userLandscape");
            foreach (var category in panel.Descendants().Where(e =>
                         e.Name.LocalName == "category" &&
                         (string)e.Attribute(Android + "name") == VrCategory).ToList())
            {
                category.Remove();
                changed = true;
            }
            changed = true;
        }

        if (theater == null && panel != null && IsPrimaryManifest(path))
        {
            theater = new XElement("activity");
            theater.SetAttributeValue(Android + "name", Theater);
            theater.SetAttributeValue(Android + "theme", "@style/UnityThemeSelector");
            theater.SetAttributeValue(Android + "process", ":theater");
            theater.SetAttributeValue(Android + "taskAffinity", "com.movieplayer.vrcinema.theater");
            theater.SetAttributeValue(Android + "launchMode", "singleTask");
            theater.SetAttributeValue(Android + "resizeableActivity", "false");
            theater.SetAttributeValue(Android + "screenOrientation", "landscape");
            theater.SetAttributeValue(Android + "hardwareAccelerated", "true");
            theater.SetAttributeValue(Android + "exported", "true");
            theater.SetAttributeValue(Android + "configChanges",
                "density|keyboard|keyboardHidden|navigation|orientation|screenLayout|screenSize|uiMode");
            var filter = new XElement("intent-filter");
            filter.Add(new XElement("action", new XAttribute(Android + "name", "android.intent.action.MAIN")));
            filter.Add(new XElement("category", new XAttribute(Android + "name", VrCategory)));
            theater.Add(filter);
            theater.Add(new XElement("meta-data",
                new XAttribute(Android + "name", "unityplayer.UnityActivity"),
                new XAttribute(Android + "value", "true")));
            panel.AddAfterSelf(theater);
            changed = true;
        }

        if (!changed) return;
        doc.Save(path);
        Debug.Log("[Manifest] Patched " + path);
    }

    static bool IsPrimaryManifest(string path)
    {
        string normalized = path.Replace('/', '\\');
        return normalized.EndsWith(@"unityLibrary\src\main\AndroidManifest.xml");
    }

    static void Set(XElement element, string localName, string value)
    {
        element.SetAttributeValue(Android + localName, value);
    }
}
