using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BasicCutter.EditorTools
{
    internal static class BasicCutterMenu
    {
        const string Root = "Tools/Basic Cutter/";

        [MenuItem(Root + "Add Cutter To Selection", false, 0)]
        static void AddCutter()
        {
            foreach (var go in Selection.gameObjects)
                if (go.GetComponent<MeshCutter>() == null) Undo.AddComponent<MeshCutter>(go);
        }

        [MenuItem(Root + "Add Cutter To Selection", true)]
        static bool AddCutterValidate() => Selection.gameObjects.Length > 0;

        [MenuItem(Root + "Snap Selected Cutters To Wall", false, 1)]
        static void SnapSelected()
        {
            foreach (var go in Selection.gameObjects)
            {
                var c = go.GetComponentInParent<MeshCutter>();
                if (c != null) WallSnapping.Snap(c, WallSnapping.AlignRotation);
            }
        }

        [MenuItem(Root + "Snap Selected Cutters To Wall", true)]
        static bool SnapSelectedValidate() => Selection.gameObjects.Any(g => g.GetComponentInParent<MeshCutter>() != null);

        [MenuItem(Root + "Rebuild All", false, 20)]
        static void RebuildAll() => CutterSystem.RebuildAll();

        [MenuItem(Root + "Bake All In Scene", false, 21)]
        static void BakeAll() => SetAll(CutState.Live, CutState.Frozen, "Bake All Cuts");

        [MenuItem(Root + "Unbake All In Scene", false, 22)]
        static void UnbakeAll() => SetAll(CutState.Frozen, CutState.Live, "Unbake All Cuts");

        static CuttableMesh[] StageWalls() =>
            StageUtility.GetCurrentStageHandle().FindComponentsOfType<CuttableMesh>().Where(c => c != null).ToArray();

        static void SetAll(CutState from, CutState to, string undo)
        {
            var walls = StageWalls().Where(w => w.state == from).ToArray();
            if (walls.Length == 0) return;
            Undo.RecordObjects(walls, undo);
            foreach (var w in walls)
            {
                w.state = to;
                EditorUtility.SetDirty(w);
                PrefabUtility.RecordPrefabInstancePropertyModifications(w);
            }
            CutterSystem.MarkDirty();
        }

        [MenuItem(Root + "Strip Components (Final)...", false, 40)]
        static void Strip()
        {
            var walls = StageWalls();
            var cutters = StageUtility.GetCurrentStageHandle().FindComponentsOfType<MeshCutter>().Where(c => c != null).ToArray();
            var trims = StageUtility.GetCurrentStageHandle().FindComponentsOfType<CutterTrim>().Where(c => c != null).ToArray();
            var colliders = StageUtility.GetCurrentStageHandle().FindComponentsOfType<CutterColliders>().Where(c => c != null).ToArray();
            if (walls.Length == 0 && cutters.Length == 0) return;
            if (!EditorUtility.DisplayDialog("Strip Basic Cutter Components",
                    $"Removes {walls.Length} Cuttable Mesh and {cutters.Length} Mesh Cutter components from the open scene(s). " +
                    "Walls keep their cut meshes, trims stay as plain meshes and split colliders as plain Box Colliders, but windows will no longer update them. This can be undone with Ctrl+Z.",
                    "Strip", "Cancel")) return;
            CutterSystem.FinalizeAll();
            Undo.SetCurrentGroupName("Strip Basic Cutter Components");
            int group = Undo.GetCurrentGroup();
            foreach (var w in walls) Undo.DestroyObjectImmediate(w);
            foreach (var c in cutters) Undo.DestroyObjectImmediate(c);
            foreach (var t in trims) Undo.DestroyObjectImmediate(t);
            foreach (var c in colliders) Undo.DestroyObjectImmediate(c); // the boxes stay, only the marker goes
            Undo.CollapseUndoOperations(group);
        }

        [MenuItem(Root + "Clean Up Unused Generated Meshes...", false, 41)]
        public static void CleanUp()
        {
            CutterSystem.FinalizeAll();
            string folder = BasicCutterSettings.instance.NormalizedOutputFolder;
            if (!AssetDatabase.IsValidFolder(folder))
            {
                EditorUtility.DisplayDialog("Basic Cutter", $"There is no {folder} folder yet.", "OK");
                return;
            }
            var generated = AssetDatabase.FindAssets("t:Mesh", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToList();

            var used = new HashSet<string>();
            void Use(Object o) { if (o != null && AssetDatabase.Contains(o)) used.Add(AssetDatabase.GetAssetPath(o)); }
            void UseScene(Scene s)
            {
                if (!s.IsValid() || !s.isLoaded) return;
                foreach (var root in s.GetRootGameObjects())
                {
                    foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true)) Use(mf.sharedMesh);
                    foreach (var mc in root.GetComponentsInChildren<MeshCollider>(true)) Use(mc.sharedMesh);
                    foreach (var cm in root.GetComponentsInChildren<CuttableMesh>(true)) { Use(cm.cutMesh); Use(cm.sourceMesh); Use(cm.originalMesh); }
                }
            }
            for (int i = 0; i < SceneManager.sceneCount; i++) UseScene(SceneManager.GetSceneAt(i));
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) UseScene(stage.scene);

            var roots = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
                .Concat(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
                .Select(AssetDatabase.GUIDToAssetPath).Distinct().ToArray();
            foreach (var d in AssetDatabase.GetDependencies(roots, true)) used.Add(d);

            var unused = generated.Where(p => !used.Contains(p)).ToList();
            if (unused.Count == 0)
            {
                EditorUtility.DisplayDialog("Basic Cutter", "All generated meshes are in use.", "OK");
                return;
            }
            string list = string.Join("\n", unused.Take(15)) + (unused.Count > 15 ? $"\n... and {unused.Count - 15} more" : "");
            if (!EditorUtility.DisplayDialog("Clean Up Unused Generated Meshes",
                    $"{unused.Count} generated mesh(es) are not used by any scene or prefab and will be deleted:\n\n{list}\n\nSave your scenes first: references that exist only in unsaved scenes are kept.",
                    "Delete", "Cancel")) return;
            var failed = new List<string>();
            AssetDatabase.DeleteAssets(unused.ToArray(), failed);
            if (failed.Count > 0) Debug.LogWarning("[Basic Cutter] Could not delete: " + string.Join(", ", failed));
        }

        [MenuItem(Root + "Create Demo Scene", false, 60)]
        static void CreateDemo() => DemoScene.Create();

        [MenuItem(Root + "Settings...", false, 80)]
        static void OpenSettings() => SettingsService.OpenProjectSettings(BasicCutterSettingsProvider.Path);
    }
}
