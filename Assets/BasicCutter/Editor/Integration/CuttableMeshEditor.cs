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
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("sourceMesh"), new GUIContent("Original Mesh"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("cutMesh"), new GUIContent("Cut Mesh"));
            }
            serializedObject.ApplyModifiedProperties();

            var walls = targets.Cast<CuttableMesh>().ToArray();
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild")) CutterSystem.Invalidate(walls);
                bool anyFrozen = walls.Any(w => w.state == CutState.Frozen);
                if (GUILayout.Button(anyFrozen ? "Unbake" : "Bake")) Set(walls, anyFrozen ? CutState.Live : CutState.Frozen);
                bool anyExcluded = walls.Any(w => w.state == CutState.Excluded);
                if (GUILayout.Button(anyExcluded ? "Include Again" : "Revert To Original")) Set(walls, anyExcluded ? CutState.Live : CutState.Excluded);
            }

            if (walls.Length != 1) return;
            var cm = walls[0];
            switch (cm.state)
            {
                case CutState.Frozen: EditorGUILayout.HelpBox("Baked: this mesh is not updated when windows move.", MessageType.Info); break;
                case CutState.Excluded: EditorGUILayout.HelpBox("Reverted: the original mesh is shown and cutters ignore this object.", MessageType.Info); break;
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
