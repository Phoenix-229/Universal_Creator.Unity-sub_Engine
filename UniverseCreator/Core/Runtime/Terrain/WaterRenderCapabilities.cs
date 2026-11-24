// UC-T04-08: conservative per-camera URP texture availability; no renderer asset mutation or texture ownership.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UniverseCreator.TerrainSystem
{
    internal static class WaterRenderCapabilities
    {
        private static readonly int Depth = Shader.PropertyToID("_UCWaterDepthAvailable");
        private static readonly int Opaque = Shader.PropertyToID("_UCWaterOpaqueAvailable");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCamera;
            RenderPipelineManager.beginCameraRendering += BeginCamera;
            Shader.SetGlobalFloat(Depth, 0); Shader.SetGlobalFloat(Opaque, 0);
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void InitializeEditor() => Initialize();
#endif

        private static void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            bool depth = false, opaque = false;
            if (asset != null && camera != null)
            {
                if (camera.TryGetComponent<UniversalAdditionalCameraData>(out var data))
                { depth = data.requiresDepthTexture; opaque = data.requiresColorTexture; }
                else
                { depth = asset.supportsCameraDepthTexture; opaque = asset.supportsCameraOpaqueTexture; }
            }
            Shader.SetGlobalFloat(Depth, depth ? 1 : 0); Shader.SetGlobalFloat(Opaque, opaque ? 1 : 0);
        }
    }
}
