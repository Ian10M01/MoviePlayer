using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor.Android;
using UnityEngine;

// After OpenXR/Meta inject their manifests, keep the launcher activity a
// resizable 2D panel and leave immersive XR only on TheaterActivity.
public class AndroidManifestPatch : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 100000;

    static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";
    static readonly XNamespace Tools = "http://schemas.android.com/tools";
    const string Panel = "com.unity3d.player.UnityPlayerActivity";
    const string Theater = "com.movieplayer.vrcinema.TheaterActivity";
    const string VrCategory = "com.oculus.intent.category.VR";
    const string XrStartMode = "android.window.PROPERTY_XR_ACTIVITY_START_MODE";
    const string FullSpace = "XR_ACTIVITY_START_MODE_FULL_SPACE_UNMANAGED";
    const string FocusAware = "com.oculus.vr.focusaware";

    static readonly string[] OptionalXrFeatures =
    {
        "android.hardware.vr.headtracking",
        "android.software.xr.api.openxr",
        "android.software.xr.api.spatial",
        "android.hardware.xr.input.hand_tracking",
    };

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
        bool changed = RelaxXrRequirements(doc);

        if (app == null)
        {
            if (changed) Save(doc, path);
            return;
        }

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
            // Blit Type Always only reaches Horizon's panel when the activity is not a hardware layer.
            Set(panel, "hardwareAccelerated", "false");
            Set(panel, "theme", "@style/UnityThemeSelector");
            Set(panel, "screenOrientation", "userLandscape");
            RemoveNamed(panel, "category", VrCategory);
            RemoveNamed(panel, "meta-data", FocusAware);
            RemoveNamed(panel, "property", XrStartMode);
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

        if (theater != null && IsPrimaryManifest(path))
        {
            Set(theater, "resizeableActivity", "false");
            Set(theater, "hardwareAccelerated", "true");
            EnsureChild(theater, "meta-data", FocusAware, "true");
            EnsureChild(theater, "property", XrStartMode, FullSpace);
            changed = true;
        }

        if (!changed) return;
        Save(doc, path);
    }

    static bool RelaxXrRequirements(XDocument doc)
    {
        bool changed = false;
        foreach (var feature in doc.Descendants().Where(e => e.Name.LocalName == "uses-feature"))
        {
            string name = (string)feature.Attribute(Android + "name");
            if (name == null || System.Array.IndexOf(OptionalXrFeatures, name) < 0) continue;
            if ((string)feature.Attribute(Android + "required") == "false") continue;
            feature.SetAttributeValue(Android + "required", "false");
            changed = true;
        }
        return changed;
    }

    static void RemoveNamed(XElement activity, string element, string androidName)
    {
        foreach (var node in activity.Descendants().Where(e =>
                     e.Name.LocalName == element &&
                     (string)e.Attribute(Android + "name") == androidName).ToList())
        {
            if ((string)node.Attribute(Tools + "node") == "remove") continue;
            node.Remove();
        }
    }

    static void EnsureChild(XElement activity, string element, string androidName, string androidValue)
    {
        var existing = activity.Elements().FirstOrDefault(e =>
            e.Name.LocalName == element &&
            (string)e.Attribute(Android + "name") == androidName &&
            (string)e.Attribute(Tools + "node") != "remove");
        if (existing == null)
        {
            activity.Add(new XElement(element,
                new XAttribute(Android + "name", androidName),
                new XAttribute(Android + "value", androidValue)));
            return;
        }
        existing.SetAttributeValue(Android + "value", androidValue);
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

    static void Save(XDocument doc, string path)
    {
        doc.Save(path);
        Debug.Log("[Manifest] Patched " + path);
    }
}
