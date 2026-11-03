using UnityEditor;
using UnityEngine;

namespace MoviePlayer.Theater.EditorTools
{
    /// <summary>
    /// Import settings for the Blender-baked cinema in Assets/Theater/Resources/TheaterModel/.
    /// The FBX keeps Blender's UV2 (lightmap atlas); materials are assigned at runtime.
    /// </summary>
    public class TheaterModelPostprocessor : AssetPostprocessor
    {
        private const string Folder = "Assets/Theater/Resources/TheaterModel/";

        public override uint GetVersion() => 2;

        private void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Folder)) return;
            var mi = (ModelImporter)assetImporter;
            mi.materialImportMode    = ModelImporterMaterialImportMode.None;
            mi.generateSecondaryUV   = false;   // keep the baked UV2 from Blender
            mi.isReadable            = false;
            mi.meshCompression       = ModelImporterMeshCompression.Off;   // lightmap UVs need full precision
            mi.importNormals         = ModelImporterNormals.Import;
            mi.importTangents        = ModelImporterTangents.None;
            mi.importBlendShapes     = false;
            mi.importCameras         = false;
            mi.importLights          = false;
            mi.importVisibility      = false;
            mi.importAnimation       = false;
            mi.animationType         = ModelImporterAnimationType.None;
            mi.addCollider           = false;
            mi.useFileScale          = true;
            mi.globalScale           = 1f;
            mi.bakeAxisConversion    = true;
            mi.preserveHierarchy     = true;
            mi.indexFormat           = ModelImporterIndexFormat.Auto;
            mi.optimizeMeshPolygons  = true;
            mi.optimizeMeshVertices  = true;
        }

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder)) return;
            var ti = (TextureImporter)assetImporter;
            bool lightmap = assetPath.Contains("Lightmap");
            ti.textureType        = TextureImporterType.Default;
            ti.sRGBTexture        = !lightmap;
            ti.alphaSource        = lightmap ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            ti.alphaIsTransparency = false;
            ti.mipmapEnabled      = true;
            ti.wrapMode           = lightmap ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            ti.filterMode         = FilterMode.Trilinear;
            ti.anisoLevel         = lightmap ? 1 : 4;
            ti.isReadable         = false;
            ti.maxTextureSize     = 2048;
            ti.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "Android",
                overridden = true,
                maxTextureSize = 2048,
                format = lightmap ? TextureImporterFormat.ASTC_4x4 : TextureImporterFormat.ASTC_6x6,
                compressionQuality = 50
            });
        }
    }
}
