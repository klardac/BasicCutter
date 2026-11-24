using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace BasicCutter.EditorTools
{
    /// <summary>
    /// Apply Cuts makes a wall's cut its new base mesh. With Embedded storage that mesh lives in the scene,
    /// and a prefab made from the wall (or Apply All from a prefab instance) cannot store a reference to
    /// it: the prefab only gets the mesh's ID. This finds applied meshes by ID and stores them inside the
    /// prefab file. A prefab whose mesh cannot be found yet is marked pending and repaired as soon as a
    /// scene that holds the mesh is opened.
    /// </summary>
    [InitializeOnLoad]
    internal static class AppliedMeshes
    {
        const string Marker = "_Applied_";
        const string PendingFile = "Library/BasicCutter/PendingAppliedPrefabs.txt";

        // copies kept for the editor session: a scene can be closed before a prefab stores its mesh
        static readonly Dictionary<string, Mesh> cache = new Dictionary<string, Mesh>();
        static readonly HashSet<string> warned = new HashSet<string>();
        static bool repairing;

        static AppliedMeshes()
        {
            EditorSceneManager.sceneOpened += (scene, mode) =>
            {
                RememberScene(scene);
                EditorApplication.delayCall += RepairPending;
            };
            EditorApplication.delayCall += () =>
            {
                for (int i = 0; i < SceneManager.sceneCount; i++) RememberScene(SceneManager.GetSceneAt(i));
                RepairPending();
            };
        }

        public static string NewId() => Guid.NewGuid().ToString("N");

        public static string MeshName(string wallName, string id) => wallName + Marker + id;

        static bool IsAppliedMesh(Mesh m) => m != null && m.name.Contains(Marker);

        /// <summary>Keeps a copy of an applied mesh for the rest of the session.</summary>
        public static void Remember(string id, Mesh mesh)
        {
            if (string.IsNullOrEmpty(id) || mesh == null) return;
            if (cache.TryGetValue(id, out var have) && have != null) return;
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            copy.hideFlags = HideFlags.HideAndDontSave;
            cache[id] = copy;
        }

        static void RememberScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var cm in root.GetComponentsInChildren<CuttableMesh>(true))
                {
                    if (!cm.HasAppliedCuts || cm.appliedMissing || cm.sourceMesh == null) continue;
                    if (string.IsNullOrEmpty(cm.appliedId))
                    {
                        // applied by 1.1.0: give it an ID now (saved with the scene's next save)
                        cm.appliedId = NewId();
                        if (!EditorUtility.IsPersistent(cm.sourceMesh)) cm.sourceMesh.name = MeshName(cm.name, cm.appliedId);
                        EditorUtility.SetDirty(cm);
                    }
                    Remember(cm.appliedId, cm.sourceMesh);
                }
        }

        /// <summary>The applied mesh with this ID, from this session's copies or any loaded scene or asset.</summary>
        public static Mesh Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (cache.TryGetValue(id, out var m) && m != null) return m;
            foreach (var cm in Resources.FindObjectsOfTypeAll<CuttableMesh>())
                if (cm != null && cm.appliedId == id && !cm.appliedMissing && cm.sourceMesh != null) return cm.sourceMesh;
            // copies from before a script reload are still loaded, only the dictionary is gone
            string suffix = Marker + id;
            foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
                if (mesh != null && mesh.name.EndsWith(suffix, StringComparison.Ordinal)) return mesh;
            return null;
        }

        /// <summary>
        /// True when a prefab's wall does not hold its applied mesh: the reference was lost (a scene mesh),
        /// Apply All kept an older applied mesh, or it is pending. With <paramref name="requireFound"/> a
        /// pending wall only counts when its mesh can be found now (so a pending prefab is not fixed in a loop).
        /// </summary>
        public static bool NeedsRecovery(CuttableMesh cm, bool requireFound)
        {
            if (cm == null || cm.originalMesh == null) return false;
            if (cm.appliedMissing) return !requireFound || Find(cm.appliedId) != null;
            if (cm.sourceMesh == null) return true;
            return !string.IsNullOrEmpty(cm.appliedId) && IsAppliedMesh(cm.sourceMesh) &&
                   !cm.sourceMesh.name.EndsWith(Marker + cm.appliedId, StringComparison.Ordinal);
        }

        /// <summary>The mesh as a sub-asset of the file: an existing one with the same name, or a new copy.</summary>
        public static Mesh StoreIn(Mesh mesh, string assetPath)
        {
            if (AssetDatabase.GetAssetPath(mesh) == assetPath) return mesh;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                if (o is Mesh existing && existing.name == mesh.name && IsAppliedMesh(existing)) return existing;
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            copy.hideFlags = HideFlags.None;
            AssetDatabase.AddObjectToAsset(copy, assetPath);
            return copy;
        }

        /// <summary>Removes applied meshes from the prefab file that nothing in it uses any more (an older Apply Cuts).</summary>
        public static void RemoveUnused(string prefabPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (root == null) return;
            var used = new HashSet<Mesh>();
            foreach (var cm in root.GetComponentsInChildren<CuttableMesh>(true)) { used.Add(cm.sourceMesh); used.Add(cm.originalMesh); used.Add(cm.cutMesh); }
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true)) used.Add(mf.sharedMesh);
            foreach (var mc in root.GetComponentsInChildren<MeshCollider>(true)) used.Add(mc.sharedMesh);
            bool removed = false;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(prefabPath))
                if (o is Mesh m && IsAppliedMesh(m) && !AssetDatabase.IsMainAsset(m) && !used.Contains(m))
                {
                    AssetDatabase.RemoveObjectFromAsset(m);
                    Object.DestroyImmediate(m, true);
                    removed = true;
                }
            if (removed) AssetDatabase.SaveAssets();
        }

        /// <summary>Logs once per session that a prefab waits for the scene with its applied mesh.</summary>
        public static void WarnMissing(string prefabPath, string wallName)
        {
            if (!warned.Add(prefabPath + "|" + wallName)) return;
            Debug.LogWarning($"[Basic Cutter] '{prefabPath}': the applied cuts of '{wallName}' were made in a scene and are not loaded now, " +
                             "so it shows a temporary cut of its original mesh. Open the scene where Apply Cuts was used on it and the prefab is repaired.",
                AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
        }

        // ------------------------------------------------------------------ pending prefabs

        static List<string> LoadPending()
        {
            try { return File.Exists(PendingFile) ? File.ReadAllLines(PendingFile).Where(l => l.Length > 0).Distinct().ToList() : new List<string>(); }
            catch (IOException) { return new List<string>(); }
        }

        static void SavePending(List<string> paths)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PendingFile));
                File.WriteAllLines(PendingFile, paths);
            }
            catch (IOException e) { Debug.LogWarning("[Basic Cutter] Could not save the list of pending prefabs: " + e.Message); }
        }

        /// <summary>Records whether the prefab waits for an applied mesh.</summary>
        public static void SetPending(string prefabPath, bool pending)
        {
            var paths = LoadPending();
            if (paths.Contains(prefabPath) == pending) return;
            if (pending) paths.Add(prefabPath); else paths.Remove(prefabPath);
            SavePending(paths);
        }

        internal static bool HasPending(GameObject prefabRoot) =>
            prefabRoot != null && prefabRoot.GetComponentsInChildren<CuttableMesh>(true).Any(c => c.appliedMissing);

        internal static IReadOnlyList<string> PendingPrefabs => LoadPending();

        /// <summary>Repairs the pending prefabs whose applied mesh can be found now.</summary>
        public static void RepairPending()
        {
            if (repairing) return;
            var paths = LoadPending();
            if (paths.Count == 0) return;
            repairing = true;
            try
            {
                foreach (var path in paths)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (!HasPending(asset)) { SetPending(path, false); continue; }
                    if (asset.GetComponentsInChildren<CuttableMesh>(true).Any(c => c.appliedMissing && Find(c.appliedId) != null))
                        PrefabMeshFixer.Fix(path);
                }
            }
            finally { repairing = false; }
        }

        /// <summary>Tests: forget this session's copies, as after a restart.</summary>
        internal static void ClearForTests()
        {
            foreach (var m in cache.Values) if (m != null) Object.DestroyImmediate(m);
            cache.Clear();
            foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>())
                if (m != null && m.hideFlags == HideFlags.HideAndDontSave && IsAppliedMesh(m)) Object.DestroyImmediate(m);
            warned.Clear();
        }
    }
}
