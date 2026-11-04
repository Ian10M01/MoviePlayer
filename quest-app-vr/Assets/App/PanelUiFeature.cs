using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MoviePlayer.App
{
    /// <summary>
    /// Draws the panel UI render texture over the camera that presents to the
    /// Horizon window. uGUI overlay on this URP build does not land on that buffer.
    /// </summary>
    public class PanelUiFeature : ScriptableRendererFeature
    {
        public static RTHandle Source;

        class Pass : ScriptableRenderPass
        {
            class Data
            {
                public RTHandle rt;
                public Material material;
                public Mesh mesh;
                public TextureHandle color;
            }

            RTHandle _source;
            Material _material;
            Mesh _mesh;
            bool _logged;

            public void Setup(RTHandle source, Material material, Mesh mesh)
            {
                _source = source;
                _material = material;
                _mesh = mesh;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_source == null || _source.rt == null || _material == null || _mesh == null) return;
                var cameraData = frameData.Get<UniversalCameraData>();
                if (cameraData.camera.targetTexture != null || cameraData.xr.enabled) return;

                var resources = frameData.Get<UniversalResourceData>();
                _material.SetTexture("_MainTex", _source.rt);
                using var builder = renderGraph.AddUnsafePass<Data>("Panel UI", out var passData);
                passData.rt = _source;
                passData.material = _material;
                passData.mesh = _mesh;
                passData.color = resources.backBufferColor;
                builder.UseTexture(renderGraph.ImportTexture(_source), AccessFlags.Read);
                builder.UseTexture(resources.backBufferColor, AccessFlags.Write);
                builder.AllowPassCulling(false);
                if (!_logged)
                {
                    _logged = true;
                    Debug.Log("[App] UI blit pass");
                }
                builder.SetRenderFunc(static (Data data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.color);
                    context.cmd.DrawMesh(data.mesh, Matrix4x4.identity, data.material, 0, 0);
                });
            }
        }

        Pass _pass;
        Material _material;
        Mesh _mesh;

        public override void Create()
        {
            _pass = new Pass { renderPassEvent = RenderPassEvent.AfterRendering };
            var shader = Shader.Find("MoviePlayer/PresentUI");
            if (shader != null) _material = new Material(shader) { name = "PresentUI" };
            else Debug.LogError("[App] PresentUI shader missing");
            _mesh = new Mesh { name = "PresentUI" };
            _mesh.vertices = new[]
            {
                new Vector3(-1f, -1f, 0f),
                new Vector3(-1f, 1f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(1f, -1f, 0f)
            };
            _mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f)
            };
            _mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (Source == null || _pass == null || _material == null) return;
            _pass.Setup(Source, _material, _mesh);
            renderer.EnqueuePass(_pass);
        }
    }
}
