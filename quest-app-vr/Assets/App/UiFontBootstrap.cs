// UiFontBootstrap.cs — Gives every TMP label a font when the project has
// no imported TextMesh Pro essential resources. Uses Unity's built-in
// LegacyRuntime.ttf so no extra font file is shipped in the repo.

using TMPro;
using UnityEngine;

namespace MoviePlayer.App
{
    public class UiFontBootstrap : MonoBehaviour
    {
        private void Awake()
        {
            var source = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (source == null) return;

            var font = TMP_FontAsset.CreateFontAsset(source);
            if (font == null) return;

            foreach (var label in GetComponentsInChildren<TMP_Text>(true))
            {
                if (label.font != null) continue;
                label.font = font;
            }
        }
    }
}
