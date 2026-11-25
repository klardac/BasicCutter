using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    [CustomEditor(typeof(CuttableMesh)), CanEditMultipleObjects]
    internal sealed class CuttableMeshEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var stateProp = serializedObject.FindProperty("state");
            EditorGUILayout.PropertyField(stateProp, new GUIContent("Mode", "Live: re-cut automatically. Frozen: keep the current cut (baked). Excluded: original mesh, never cut."));
            var walls = targets.Cast<CuttableMesh>().ToArray();
            bool anyApplied = walls.Any(w => w.HasAppliedCuts);
            using (new EditorGUI.DisabledScope(true))
            {
                if (anyApplied)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("originalMesh"), new GUIContent("Original Mesh", "The mesh from before Apply Cuts."));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("sourceMesh"), new GUIContent("Applied Mesh", "The original mesh with the applied openings; live cuts start from it."));
                }
                else EditorGUILayout.PropertyField(serializedObject.FindProperty("sourceMesh"), new GUIContent("Original Mesh"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("cutMesh"), new GUIContent("Cut Mesh"));
            }
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild")) CutterSystem.Invalidate(walls);
                bool anyFrozen = walls.Any(w => w.state == CutState.Frozen);
                if (GUILayout.Button(anyFrozen ? "Unbake" : "Bake")) Set(walls, anyFrozen ? CutState.Live : CutState.Frozen);
                bool anyExcluded = walls.Any(w => w.state == CutState.Excluded);
                if (GUILayout.Button(anyExcluded ? "Include Again" : "Revert To Original")) Set(walls, anyExcluded ? CutState.Live : CutState.Excluded);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                bool canApply = walls.Any(w => w.state != CutState.Excluded && !w.appliedMissing && w.cutMesh != null && w.TryGetComponent(out MeshFilter f) && f.sharedMesh == w.cutMesh);
                using (new EditorGUI.DisabledScope(!canApply))
                    if (GUILayout.Button(new GUIContent("Apply Cuts", "Make the current holes part of this wall's mesh. They stay when their windows move or are deleted, and new windows cut on top of them. The wall becomes live.")))
                    {
                        CutterSystem.ApplyCuts(walls);
                        GUIUtility.ExitGUI();
                    }
                using (new EditorGUI.DisabledScope(!anyApplied))
                    if (GUILayout.Button(new GUIContent("Discard Applied Cuts", "Go back to the mesh from before Apply Cuts. The wall is cut live by its current windows again.")))
                    {
                        CutterSystem.DiscardAppliedCuts(walls);
                        GUIUtility.ExitGUI();
                    }
            }

            if (walls.Length != 1) return;
            var cm = walls[0];
            switch (cm.state)
            {
                case CutState.Frozen: EditorGUILayout.HelpBox("Baked: this mesh is not updated when windows move. Use Apply Cuts to keep these holes and cut new windows into it.", MessageType.Info); break;
                case CutState.Excluded:
                    EditorGUILayout.HelpBox(cm.HasAppliedCuts
                        ? "Excluded: the applied mesh is shown and cutters ignore this object."
                        : "Reverted: the original mesh is shown and cutters ignore this object.", MessageType.Info);
                    break;
            }
            if (cm.AppliedMeshMissing)
                EditorGUILayout.HelpBox("The applied mesh of this prefab was made in a scene that is not open, so a temporary cut of the original mesh is shown. " +
                                        "Open the scene where Apply Cuts was used on it and the prefab is repaired.", MessageType.Warning);
            else if (cm.HasAppliedCuts)
            {
                int n = cm.appliedCutters.Count(c => c != null);
                EditorGUILayout.HelpBox($"Applied cuts: the holes of earlier windows are part of this wall's mesh{(n > 0 ? $" ({n} window(s) applied, they no longer cut it)" : "")}. Discard Applied Cuts goes back to the original mesh.", MessageType.None);
            }
            if (cm.sourceMesh != null)
            {
                try
                {
                    var sm = SourceMesh.Get(cm.sourceMesh);
                    if (!sm.Closed) EditorGUILayout.HelpBox($"The original mesh is not closed ({sm.OpenEdges} open edges). Walls must be watertight to be cut.", MessageType.Warning);
                    int after = cm.cutMesh != null && cm.GetComponent<MeshFilter>().sharedMesh == cm.cutMesh ? TriCount(cm.cutMesh) : sm.TriangleCount;
                    EditorGUILayout.LabelField("Triangles", $"{sm.TriangleCount} -> {after}");
                }
                catch (System.Exception e) { EditorGUILayout.HelpBox(e.Message, MessageType.Error); }
            }
            var split = ColliderSplitter.Find(cm);
            if (split != null)
            {
                int boxes = split.GetComponents<BoxCollider>().Count(b => b.enabled);
                int own = split.replaced.Count(b => b != null);
                EditorGUILayout.LabelField("Colliders", own > 0
                    ? $"{boxes} boxes around the openings (replace {own} Box Collider{(own == 1 ? "" : "s")})"
                    : $"{boxes} boxes around the openings");
            }
            var cutters = CutterSystem.GetCutters(cm);
            if (cutters.Count > 0)
            {
                EditorGUILayout.LabelField("Cut By", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(true))
                    foreach (var c in cutters) if (c != null) EditorGUILayout.ObjectField(c, typeof(MeshCutter), true);
            }
        }

        static int TriCount(Mesh m)
        {
            long c = 0;
            for (int i = 0; i < m.subMeshCount; i++) c += m.GetIndexCount(i);
            return (int)(c / 3);
        }

        static void Set(CuttableMesh[] walls, CutState state)
        {
            Undo.RecordObjects(walls, "Change Cut Mode");
            foreach (var w in walls)
            {
                w.state = state;
                EditorUtility.SetDirty(w);
                PrefabUtility.RecordPrefabInstancePropertyModifications(w);
            }
            CutterSystem.MarkDirty();
        }
    }
}
