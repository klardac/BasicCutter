using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>Builds a small scene with straight and curved walls, window / door prefabs and a floor.</summary>
    internal static class DemoScene
    {
        public const string DefaultFolder = "Assets/BasicCutterDemo";

        public static void Create()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var root = Build(DefaultFolder);
            EditorSceneManager.SaveScene(root.scene, DefaultFolder + "/BasicCutterDemo.unity");
            Selection.activeGameObject = GameObject.Find("Arch Window");
            Debug.Log("[Basic Cutter] Demo scene created in " + DefaultFolder + ". Move the windows around to see the walls update.");
        }

        sealed class Kit
        {
            public string folder;
            public Material wall, frame, glass, floor, door, trim;
        }

        /// <summary>Creates the demo content in a new scene and cuts it. Returns the scene's environment root.</summary>
        internal static GameObject Build(string folder)
        {
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            GeneratedMeshStore.EnsureFolder(folder + "/Meshes");
            GeneratedMeshStore.EnsureFolder(folder + "/Materials");
            GeneratedMeshStore.EnsureFolder(folder + "/Prefabs");
            var kit = new Kit
            {
                folder = folder,
                wall = Mat(folder, "Wall", new Color(0.86f, 0.84f, 0.8f)),
                frame = Mat(folder, "Frame", new Color(0.28f, 0.2f, 0.14f)),
                glass = Glass(Mat(folder, "Glass", new Color(0.2f, 0.45f, 0.85f, 0.45f))),
                floor = Mat(folder, "Floor", new Color(0.45f, 0.47f, 0.45f)),
                door = Mat(folder, "Door", new Color(0.5f, 0.32f, 0.18f)),
                trim = Mat(folder, "Trim", new Color(0.97f, 0.96f, 0.93f)),
            };

            var env = new GameObject("Environment");
            Obj("Floor", MeshAsset(kit, ProceduralMeshes.Box(new Vector3(24, 0.2f, 24), new Vector3(0, -0.1f, 0), "Floor")), kit.floor, env.transform, new Vector3(0, 0, -3), Quaternion.identity);

            // straight wall with three windows
            // the walls overlap at the corner (no touching faces, which would leak light in shadows)
            var wallA = Obj("Wall A", MeshAsset(kit, ProceduralMeshes.Box(new Vector3(8.3f, 3, 0.3f), Vector3.zero, "Wall_8x3")), kit.wall, env.transform, new Vector3(0, 1.5f, 0), Quaternion.identity);
            // the rectangular window gets a trim with a sill (it follows the curved wall too)
            var rectPrefab = WindowPrefab(kit, "Rect Window", ProceduralMeshes.Rect(1.2f, 1.4f), ProceduralMeshes.Rect(1.04f, 1.24f), c =>
            {
                c.trim = true;
                c.trimSides = TrimSides.Both;
                c.trimMaterial = kit.trim;
                c.sill = true;
            });
            var archPrefab = WindowPrefab(kit, "Arch Window",
                ProceduralMeshes.ToVector2(CutterShapeBuilder.ArchOutline(1.2, 1.9, 0.6, 24)),
                ProceduralMeshes.ToVector2(CutterShapeBuilder.ArchOutline(1.04, 1.74, 0.52, 24)));
            var roundPrefab = WindowPrefab(kit, "Round Window", ProceduralMeshes.Circle(0.5f, 32), ProceduralMeshes.Circle(0.42f, 32));
            var doorPrefab = DoorPrefab(kit);

            Place(rectPrefab, "Rect Window", new Vector3(-2.6f, 1.6f, 0), Quaternion.identity);
            Place(archPrefab, "Arch Window", new Vector3(0, 1.6f, 0), Quaternion.identity);
            Place(roundPrefab, "Round Window", new Vector3(2.6f, 1.8f, 0), Quaternion.identity);

            // second wall, rotated, with a door touching the floor
            var rotB = Quaternion.Euler(0, 90, 0);
            var wallB = Obj("Wall B", MeshAsset(kit, ProceduralMeshes.Box(new Vector3(6.15f, 3, 0.3f), Vector3.zero, "Wall_6x3")), kit.wall, env.transform, new Vector3(4.15f, 1.5f, -2.925f), rotB);
            Place(doorPrefab, "Door", wallB.transform.TransformPoint(new Vector3(-1f, -1.5f, 0)), rotB);
            Place(rectPrefab, "Rect Window (Wall B)", wallB.transform.TransformPoint(new Vector3(1.4f, 0.1f, 0)), rotB);
            // a plain box cutter that overlaps the door frame: both merge into one opening
            ShapeCutter("Side Light (merges with Door)", new Vector2(0.5f, 1.6f), wallB.transform.TransformPoint(new Vector3(-0.3f, -0.1f, 0)), rotB, null);
            // a niche with a trim: cut only 15 cm into the wall
            ShapeCutter("Niche", new Vector2(0.7f, 1.0f), wallB.transform.TransformPoint(new Vector3(-2.2f, -0.1f, 0)), rotB, c =>
            {
                c.depth = CutDepth.Niche;
                c.nicheDepth = 0.15f;
                c.trim = true;
                c.trimWidth = 0.06f;
                c.trimMaterial = kit.trim;
            });

            // curved wall: one window placed exactly, one snapped from a rough position
            var curved = Obj("Curved Wall", MeshAsset(kit, ProceduralMeshes.CurvedWall(5f, 0.3f, 3f, 80f, 48, "CurvedWall_R5")), kit.wall, env.transform, new Vector3(-1f, 0, -12f), Quaternion.identity);
            float a = -14f * Mathf.Deg2Rad;
            Place(archPrefab, "Arch Window (Curved)", curved.transform.TransformPoint(new Vector3(Mathf.Sin(a) * 5f, 1.6f, Mathf.Cos(a) * 5f)), Quaternion.Euler(0, -14f, 0));
            a = 14f * Mathf.Deg2Rad;
            var rough = Place(rectPrefab, "Rect Window (Curved, snapped)", curved.transform.TransformPoint(new Vector3(Mathf.Sin(a) * 5.25f, 1.6f, Mathf.Cos(a) * 5.25f)), Quaternion.Euler(0, 6f, 0));

            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(-6.5f, 5.5f, 9f);
                cam.transform.LookAt(new Vector3(0, 1f, -3f));
            }

            CutterSystem.SyncNow();
            WallSnapping.Snap(rough.GetComponent<MeshCutter>(), true);
            CutterSystem.RebuildAll();
            return env;
        }

        static GameObject Place(GameObject prefab, string name, Vector3 pos, Quaternion rot)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = name;
            go.transform.SetPositionAndRotation(pos, rot);
            return go;
        }

        static GameObject ShapeCutter(string name, Vector2 size, Vector3 pos, Quaternion rot, System.Action<MeshCutter> setup)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(pos, rot);
            var c = go.AddComponent<MeshCutter>();
            c.shape = CutterShape.Box;
            c.axis = CutterAxis.Z;
            c.shapeSize = size;
            setup?.Invoke(c);
            return go;
        }

        static GameObject WindowPrefab(Kit kit, string name, List<Vector2> outer, List<Vector2> inner, System.Action<MeshCutter> setup = null)
        {
            var root = new GameObject(name);
            // the hole is 1 cm smaller than the frame, so the frame overlaps the wall and no light leaks through the seam
            var cutter = root.AddComponent<MeshCutter>();
            cutter.padding = -0.01f;
            setup?.Invoke(cutter);
            Obj("Frame", MeshAsset(kit, ProceduralMeshes.Frame(outer, inner, 0.12f, name + " Frame")), kit.frame, root.transform, Vector3.zero, Quaternion.identity);
            // the glass reaches into the frame so there is no seam between them
            var innerD = new List<D2>();
            foreach (var p in inner) innerD.Add(new D2(p.x, p.y));
            var glass = ProceduralMeshes.ToVector2(Poly2D.Offset(innerD, 0.03));
            // double sided: the glass is seen from inside the room too; see-through glass casts no shadow
            var pane = Obj("Glass", MeshAsset(kit, ProceduralMeshes.Panel(glass, 0f, name + " Glass", doubleSided: true)), kit.glass, root.transform, Vector3.zero, Quaternion.identity);
            pane.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return SavePrefab(kit, root);
        }

        static GameObject DoorPrefab(Kit kit)
        {
            var root = new GameObject("Door");
            var cutter = root.AddComponent<MeshCutter>();
            cutter.door = true;
            cutter.jambMaterial = kit.frame; // wooden lining inside the opening
            var bar = MeshAsset(kit, ProceduralMeshes.Box(new Vector3(0.08f, 2.2f, 0.14f), Vector3.zero, "Door Post"));
            var top = MeshAsset(kit, ProceduralMeshes.Box(new Vector3(1.0f, 0.08f, 0.14f), Vector3.zero, "Door Lintel"));
            var leaf = MeshAsset(kit, ProceduralMeshes.Box(new Vector3(0.84f, 2.12f, 0.04f), Vector3.zero, "Door Leaf"));
            Obj("Post L", bar, kit.frame, root.transform, new Vector3(-0.46f, 1.1f, 0), Quaternion.identity);
            Obj("Post R", bar, kit.frame, root.transform, new Vector3(0.46f, 1.1f, 0), Quaternion.identity);
            Obj("Lintel", top, kit.frame, root.transform, new Vector3(0, 2.16f, 0), Quaternion.identity);
            Obj("Leaf", leaf, kit.door, root.transform, new Vector3(0, 1.06f, 0), Quaternion.identity);
            return SavePrefab(kit, root);
        }

        static GameObject SavePrefab(Kit kit, GameObject root)
        {
            string path = $"{kit.folder}/Prefabs/{root.name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        static GameObject Obj(string name, Mesh mesh, Material mat, Transform parent, Vector3 pos, Quaternion rot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static Mesh MeshAsset(Kit kit, Mesh mesh)
        {
            string path = $"{kit.folder}/Meshes/{mesh.name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mesh, existing);
                Object.DestroyImmediate(mesh);
                return existing;
            }
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        static Material Mat(string folder, string name, Color color)
        {
            string path = $"{folder}/Materials/{name}.mat";
            // the default material of the active render pipeline (Built-in, URP or HDRP)
            var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var baseMat = probe.GetComponent<MeshRenderer>().sharedMaterial;
            Object.DestroyImmediate(probe);
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            Material m;
            if (existing != null)
            {
                // the project may have switched pipelines since the demo was created
                if (baseMat != null && existing.shader != baseMat.shader) existing.shader = baseMat.shader;
                m = existing;
            }
            else
            {
                m = baseMat != null ? new Material(baseMat) : new Material(Shader.Find("Standard"));
                m.name = name;
                AssetDatabase.CreateAsset(m, path);
            }
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>
        /// Makes the default material of the active pipeline alpha blended (its colour's alpha is the opacity)
        /// and glossy: URP Lit, HDRP Lit or the Built-in Standard shader.
        /// </summary>
        static Material Glass(Material m)
        {
            const int Transparent = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            void Set(string p, float v) { if (m.HasProperty(p)) m.SetFloat(p, v); }
            if (m.HasProperty("_Surface"))
            {
                // URP Lit
                Set("_Surface", 1);                    // transparent
                Set("_Blend", 0);                      // alpha
                Set("_BlendModePreserveSpecular", 0);
                Set("_AlphaClip", 0);
                Set("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                Set("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                Set("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                Set("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                Set("_ZWrite", 0);
                Set("_Smoothness", 0.9f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.DisableKeyword("_ALPHATEST_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.SetShaderPassEnabled("DepthOnly", false);
                m.SetShaderPassEnabled("ShadowCaster", false);
            }
            else if (m.HasProperty("_SurfaceType"))
            {
                // HDRP Lit: set the inputs, then let HDRP derive keywords and passes from them
                Set("_SurfaceType", 1);
                Set("_BlendMode", 0);
                Set("_TransparentZWrite", 0);
                Set("_Smoothness", 0.9f);
                var hd = System.Type.GetType("UnityEngine.Rendering.HighDefinition.HDMaterial, Unity.RenderPipelines.HighDefinition.Runtime");
                hd?.GetMethod("ValidateMaterial", new[] { typeof(Material) })?.Invoke(null, new object[] { m });
            }
            else if (m.HasProperty("_Mode"))
            {
                // Built-in Standard, "Transparent" mode
                Set("_Mode", 3);
                Set("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                Set("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                Set("_ZWrite", 0);
                Set("_Glossiness", 0.9f);
                m.DisableKeyword("_ALPHATEST_ON");
                m.DisableKeyword("_ALPHABLEND_ON");
                m.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            }
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = Transparent;
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
