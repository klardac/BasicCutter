using System.IO;
using System.Linq;
using BasicCutter.EditorTools;
using UnityEditor;
using UnityEngine;

namespace BasicCutter.Tests
{
    /// <summary>Renders the demo scene to PNG files (run with -executeMethod for a visual check).</summary>
    public static class DemoSnapshot
    {
        public static void Render()
        {
            var env = DemoScene.Build(DemoScene.DefaultFolder);
            Directory.CreateDirectory("Logs");
            if (System.Environment.GetCommandLineArgs().Contains("-twoSidedWalls"))
                foreach (var cm in env.GetComponentsInChildren<CuttableMesh>())
                    cm.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
            Shot(new Vector3(-6.5f, 5.5f, 9f), new Vector3(0, 1f, -3f), "Logs/warmup.png"); // first SRP frame can be off
            Shot(new Vector3(-6.5f, 5.5f, 9f), new Vector3(0, 1f, -3f), "Logs/demo_overview.png");
            Shot(new Vector3(1.6f, 2.6f, 3.2f), new Vector3(0, 1.6f, 0), "Logs/demo_arch_closeup.png");
            Shot(new Vector3(-1f, 2.2f, -2.5f), new Vector3(-1f, 1.6f, -7f), "Logs/demo_curved_from_inside.png");
            Shot(new Vector3(1.5f, 2.0f, -4.5f), new Vector3(4.15f, 1.0f, -2.5f), "Logs/demo_door.png");
            Shot(new Vector3(0.4f, 2.0f, -3.2f), new Vector3(4.15f, 1.2f, -1.3f), "Logs/demo_wallB.png");
            Shot(new Vector3(-1.2f, 2.1f, 2.6f), new Vector3(-2.6f, 1.3f, 0), "Logs/demo_trim_sill.png");
            Shot(new Vector3(0.2f, 1.8f, -4.2f), new Vector3(0.6f, 1.5f, -7.3f), "Logs/demo_curved_trim.png");
            AddWireframes(env);
            Shot(new Vector3(2.3f, 1.4f, -0.6f), new Vector3(4.15f, 1.0f, -1.925f), "Logs/demo_door_wire.png");
            Shot(new Vector3(1.6f, 2.6f, 3.2f), new Vector3(0, 1.6f, 0), "Logs/demo_arch_wire.png");
            EditorApplication.Exit(0);
        }

        /// <summary>Overlays the triangle edges of every cut wall (to check the topology).</summary>
        static void AddWireframes(GameObject env)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.black);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.black);
            // hide windows and doors so the holes' inner faces are visible
            foreach (var cutter in Object.FindObjectsByType<MeshCutter>(FindObjectsSortMode.None))
                foreach (var r in cutter.GetComponentsInChildren<MeshRenderer>()) r.enabled = false;
            foreach (var cm in env.GetComponentsInChildren<CuttableMesh>())
            {
                var src = cm.GetComponent<MeshFilter>().sharedMesh;
                var v = src.vertices; var n = src.normals; var t = src.triangles;
                var lv = new System.Collections.Generic.List<Vector3>();
                var li = new System.Collections.Generic.List<int>();
                for (int i = 0; i < t.Length; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = t[i + k], b = t[i + (k + 1) % 3];
                        li.Add(lv.Count); lv.Add(v[a] + n[a] * 0.002f);
                        li.Add(lv.Count); lv.Add(v[b] + n[b] * 0.002f);
                    }
                var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                m.SetVertices(lv);
                m.SetIndices(li, MeshTopology.Lines, 0);
                var go = new GameObject(cm.name + " Wire");
                go.transform.SetParent(cm.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = m;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            }
        }

        static void Shot(Vector3 pos, Vector3 target, string path)
        {
            var go = new GameObject("Snapshot Camera");
            var cam = go.AddComponent<Camera>();
            cam.transform.position = pos;
            cam.transform.LookAt(target);
            cam.fieldOfView = 55;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.72f, 0.82f);
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(go);
        }
    }
}
