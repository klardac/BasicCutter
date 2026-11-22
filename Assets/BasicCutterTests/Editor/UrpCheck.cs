using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BasicCutter.Tests
{
    /// <summary>
    /// Switches this test project to URP (if the package is installed) and renders the demo scene.
    /// Uses reflection so the test assembly does not depend on URP.
    /// </summary>
    public static class UrpCheck
    {
        public static void EnableUrpAndRender()
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unity.RenderPipelines.Universal.Runtime");
                if (asm == null) { Debug.LogError("[UrpCheck] URP is not installed."); EditorApplication.Exit(2); return; }
                var assetType = asm.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset");
                var rendererEnum = asm.GetType("UnityEngine.Rendering.Universal.RendererType");
                const string folder = "Assets/UrpCheck";
                if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "UrpCheck");

                var createRenderer = assetType.GetMethod("CreateRendererAsset", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                var rendererData = createRenderer.Invoke(null, new object[] { folder + "/UrpCheck_Renderer.asset", Enum.Parse(rendererEnum, "UniversalRenderer"), false, "Renderer" });
                var create = assetType.GetMethods(BindingFlags.Public | BindingFlags.Static).First(m => m.Name == "Create" && m.GetParameters().Length == 1);
                var pipeline = (RenderPipelineAsset)create.Invoke(null, new[] { rendererData });
                AssetDatabase.CreateAsset(pipeline, folder + "/UrpCheck.asset");
                GraphicsSettings.defaultRenderPipeline = pipeline;
                for (int i = 0; i < QualitySettings.names.Length; i++)
                {
                    QualitySettings.SetQualityLevel(i, false);
                    QualitySettings.renderPipeline = pipeline;
                }
                AssetDatabase.SaveAssets();
                Debug.Log("[UrpCheck] Active pipeline: " + GraphicsSettings.currentRenderPipeline);
            }
            catch (Exception e)
            {
                Debug.LogError("[UrpCheck] " + e);
                EditorApplication.Exit(3);
                return;
            }
            DemoSnapshot.Render(); // exits
        }
    }
}
