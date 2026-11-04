using UnityEngine;
using UnityEngine.UI;

namespace MoviePlayer.App
{
    /// <summary>
    /// Writes each vertex's screen position into uv1 as NDC. The overlay UI
    /// shader reads that instead of TransformObjectToHClip, which on this
    /// player stretches every graphic to the full window.
    /// </summary>
    public class FlatUiMesh : BaseMeshEffect
    {
        static readonly Vector3[] Corners = new Vector3[4];
        static bool _logged;

        public override void ModifyMesh(VertexHelper vh)
        {
            if (vh.currentVertCount < 1) return;
            var rt = (RectTransform)transform;
            var rect = rt.rect;
            if (rect.width < 1f || rect.height < 1f || Screen.width < 2 || Screen.height < 2)
                return;

            rt.GetWorldCorners(Corners);
            var vert = new UIVertex();
            for (int i = 0; i < vh.currentVertCount; i++)
            {
                vh.PopulateUIVertex(ref vert, i);
                float u = Mathf.InverseLerp(rect.xMin, rect.xMax, vert.position.x);
                float v = Mathf.InverseLerp(rect.yMin, rect.yMax, vert.position.y);
                float x = Mathf.Lerp(Corners[0].x, Corners[3].x, u);
                float y = Mathf.Lerp(Corners[0].y, Corners[1].y, v);
                vert.uv1 = new Vector4(
                    x / Screen.width * 2f - 1f,
                    y / Screen.height * 2f - 1f,
                    0f, 0f);
                vh.SetUIVertex(vert, i);
            }
            if (!_logged && name == "Background")
            {
                _logged = true;
                vh.PopulateUIVertex(ref vert, 0);
                Debug.Log("[App] UI ndc " + name + " " + vert.uv1 + " screen " + Screen.width + "x" + Screen.height);
            }
        }
    }
}
