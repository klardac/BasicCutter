using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>
    /// Owns the generated meshes (cut walls, trims). Depending on <see cref="BasicCutterSettings.meshStorage"/>:
    /// Embedded keeps them inside the scene file (scene objects) or inside the prefab file (prefab mode,
    /// prefab contents); Asset Folder writes one .asset file per mesh.
    /// </summary>
    internal static class GeneratedMeshStore
    {
        /// <summary>Set while the contents of a prefab file are cut outside prefab mode.</summary>
        internal static string PrefabPathOverride;

        static MeshStorage Storage => BasicCutterSettings.instance.meshStorage;

        /// <summary>The prefab file an object is being edited in, or null for scene objects.</summary>
        static string PrefabPath(GameObject go)
        {
            if (!string.IsNullOrEmpty(PrefabPathOverride)) return PrefabPathOverride;
            var stage = PrefabStageUtility.GetPrefabStage(go);
            return stage != null ? stage.assetPath : null;
        }

        /// <summary>True for a mesh asset file in the Generated Mesh Folder.</summary>
        public static bool IsGenerated(Mesh mesh)
        {
            if (mesh == null || !AssetDatabase.Contains(mesh)) return false;
            string path = AssetDatabase.GetAssetPath(mesh).Replace('\\', '/');
            return path.StartsWith(BasicCutterSettings.instance.NormalizedOutputFolder + "/") && AssetDatabase.IsMainAsset(mesh);
        }

        /// <summary>True when the mesh is stored the way the current setting wants for this object, so it may be overwritten.</summary>
        static bool IsOwned(Mesh m, GameObject go)
        {
            if (m == null) return false;
            if (Storage == MeshStorage.AssetFolder) return IsGenerated(m);
            string prefab = PrefabPath(go);
            if (prefab != null)
                return EditorUtility.IsPersistent(m) && AssetDatabase.GetAssetPath(m) == prefab && !AssetDatabase.IsMainAsset(m);
            return !EditorUtility.IsPersistent(m);
        }

        /// <summary>True when the component's mesh reference comes unchanged from a prefab asset.</summary>
        public static bool IsInheritedFromPrefab(Component c, string propertyPath)
        {
            if (c == null || !PrefabUtility.IsPartOfPrefabInstance(c)) return false;
            if (PrefabUtility.IsAddedComponentOverride(c)) return false;
            var so = new SerializedObject(c);
            var p = so.FindProperty(propertyPath);
            return p != null && !p.prefabOverride;
        }

        /// <summary>
        /// Returns a mesh the owner may overwrite: its current one if it is stored correctly and nobody
        /// else uses it, otherwise a new one (duplicated objects, prefab instances, changed setting).
        /// </summary>
        public static Mesh Acquire(Component owner, Mesh current, string suffix, bool inheritedFromPrefab, Dictionary<Mesh, int> referenceCount)
        {
            if (current != null && IsOwned(current, owner.gameObject) && !inheritedFromPrefab &&
                (!referenceCount.TryGetValue(current, out int refs) || refs <= 1))
                return current;

            if (current != null && referenceCount.TryGetValue(current, out int c)) referenceCount[current] = c - 1;

            string baseName = Sanitize(owner.gameObject.name) + suffix;
            Mesh mesh;
            if (Storage == MeshStorage.AssetFolder)
            {
                string folder = EnsureFolder(BasicCutterSettings.instance.NormalizedOutputFolder + "/" + ContextName(owner.gameObject));
                string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{baseName}.asset");
                mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(mesh, path);
            }
            else
            {
                mesh = new Mesh { name = baseName };
                string prefab = PrefabPath(owner.gameObject);
                // prefab files cannot reference scene data: store the mesh inside the prefab file
                if (prefab != null) AssetDatabase.AddObjectToAsset(mesh, prefab);
            }
            referenceCount[mesh] = 1;
            return mesh;
        }

        /// <summary>Writes a changed mesh to disk when it lives in an asset file (scene meshes are saved with the scene).</summary>
        public static void Save(Mesh mesh)
        {
            if (mesh == null) return;
            EditorUtility.SetDirty(mesh);
            if (EditorUtility.IsPersistent(mesh) && AssetDatabase.IsMainAsset(mesh)) AssetDatabase.SaveAssetIfDirty(mesh);
        }

        static string ContextName(GameObject go)
        {
            string prefab = PrefabPath(go);
            if (prefab != null) return Sanitize(Path.GetFileNameWithoutExtension(prefab));
            string scene = go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.name) ? go.scene.name : "Untitled";
            return Sanitize(scene);
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Mesh";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        public static string EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return folder;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            return folder;
        }
    }

    /// <summary>
    /// Embedded storage: when scene objects are saved as a new prefab, their scene-stored meshes cannot
    /// go with them. This re-cuts such prefabs right after import and stores the meshes inside the file.
    /// </summary>
    internal sealed class PrefabMeshFixer : AssetPostprocessor
    {
        static readonly HashSet<string> queue = new HashSet<string>();

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (BasicCutterSettings.instance.meshStorage != MeshStorage.Embedded) return;
            bool any = false;
            foreach (var p in imported)
                if (p.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase) && queue.Add(p)) any = true;
            if (any) EditorApplication.delayCall += ProcessQueue;
        }

        static void ProcessQueue()
        {
            var list = new List<string>(queue);
            queue.Clear();
            foreach (var p in list) Fix(p);
        }

        /// <summary>True when cut walls or trims in the prefab lost their mesh.</summary>
        internal static bool NeedsFix(GameObject root)
        {
            foreach (var cm in root.GetComponentsInChildren<CuttableMesh>(true))
            {
                if (cm.state == CutState.Excluded || string.IsNullOrEmpty(cm.signature)) continue;
                var mf = cm.GetComponent<MeshFilter>();
                if (cm.cutMesh == null || mf == null || mf.sharedMesh == null) return true;
            }
            foreach (var t in root.GetComponentsInChildren<CutterTrim>(true))
            {
                var mf = t.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh == null) return true;
            }
            return false;
        }

        internal static void Fix(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null || !NeedsFix(asset)) return;
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                CutterSystem.CutPrefabContents(root, path);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Basic Cutter] Could not store the cut meshes inside '{path}': {e.Message}");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
