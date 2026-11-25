using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    internal enum LightmapUvMode
    {
        /// <summary>Generate when the original mesh has lightmap UVs or the object contributes to GI.</summary>
        Auto = 0,
        Always = 1,
        Never = 2,
    }

    internal enum MeshStorage
    {
        /// <summary>Inside the scene file; for prefabs inside the prefab file. No extra files.</summary>
        Embedded = 0,
        /// <summary>One .asset file per cut mesh in the Generated Mesh Folder.</summary>
        AssetFolder = 1,
    }

    /// <summary>Project-wide settings, stored in ProjectSettings/BasicCutterSettings.asset.</summary>
    [FilePath("ProjectSettings/BasicCutterSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class BasicCutterSettings : ScriptableSingleton<BasicCutterSettings>
    {
        [Tooltip("Embedded: cut meshes are stored inside the scene (or prefab) that uses them. Asset Folder: as .asset files in the Generated Mesh Folder.")]
        public MeshStorage meshStorage = MeshStorage.Embedded;

        [Tooltip("Asset Folder storage: folder for the generated (cut) meshes. A sub-folder per scene / prefab is created.")]
        public string outputFolder = "Assets/BasicCutterGenerated";

        [Tooltip("Re-cut walls automatically while windows are moved or edited.")]
        public bool liveUpdate = true;

        [Tooltip("Minimum time between two live updates while dragging (seconds).")]
        public float updateInterval = 0.05f;

        [Tooltip("Live update cuts large walls on a background thread, so the editor never stalls while dragging.")]
        public bool backgroundCutting = true;

        [Tooltip("A cut wall's Box Colliders are replaced by boxes that leave its openings free (characters can walk through doors).")]
        public bool splitBoxColliders = true;

        [Tooltip("When to generate lightmap UVs (UV2) for cut meshes.")]
        public LightmapUvMode lightmapUvs = LightmapUvMode.Auto;

        [Tooltip("Draw the cutting outline of every cutter, not only the selected ones.")]
        public bool showAllGizmos;

        public Color gizmoColor = new Color(1f, 0.55f, 0.1f, 1f);

        public void SaveSettings() => Save(true);

        public string NormalizedOutputFolder
        {
            get
            {
                string f = string.IsNullOrWhiteSpace(outputFolder) ? "Assets/BasicCutterGenerated" : outputFolder.Replace('\\', '/').TrimEnd('/');
                if (f != "Assets" && !f.StartsWith("Assets/")) f = "Assets/" + f;
                return f;
            }
        }
    }

    internal static class BasicCutterSettingsProvider
    {
        public const string Path = "Project/Basic Cutter";

        [SettingsProvider]
        static SettingsProvider Create()
        {
            return new SettingsProvider(Path, SettingsScope.Project)
            {
                label = "Basic Cutter",
                keywords = new HashSet<string> { "cut", "wall", "window", "boolean", "csg", "hole" },
                guiHandler = _ =>
                {
                    var s = BasicCutterSettings.instance;
                    EditorGUI.BeginChangeCheck();
                    EditorGUIUtility.labelWidth = 220;
                    s.meshStorage = (MeshStorage)EditorGUILayout.EnumPopup(new GUIContent("Mesh Storage",
                        "Embedded: inside the scene / prefab file, no extra files. Asset Folder: one .asset file per cut mesh."), s.meshStorage);
                    using (new EditorGUI.DisabledScope(s.meshStorage != MeshStorage.AssetFolder))
                        s.outputFolder = EditorGUILayout.TextField(new GUIContent("Generated Mesh Folder", "Asset Folder storage: cut meshes are saved here as .asset files."), s.outputFolder);
                    s.liveUpdate = EditorGUILayout.Toggle(new GUIContent("Live Update", "Re-cut while windows are moved."), s.liveUpdate);
                    using (new EditorGUI.DisabledScope(!s.liveUpdate))
                    {
                        s.updateInterval = EditorGUILayout.Slider(new GUIContent("Live Update Interval (s)"), s.updateInterval, 0f, 0.5f);
                        s.backgroundCutting = EditorGUILayout.Toggle(new GUIContent("Background Cutting",
                            $"Large walls (from {CutterSystem.BackgroundThreshold} triangles) are cut on a worker thread while dragging."), s.backgroundCutting);
                    }
                    bool split = EditorGUILayout.Toggle(new GUIContent("Split Box Colliders",
                        "A cut wall's Box Colliders are replaced by boxes around its openings, so characters can walk through doors. Mesh Colliders always get the cut mesh."), s.splitBoxColliders);
                    if (split != s.splitBoxColliders) { s.splitBoxColliders = split; CutterSystem.RebuildAllLater(); }
                    s.lightmapUvs = (LightmapUvMode)EditorGUILayout.EnumPopup(new GUIContent("Lightmap UVs", "Auto: when the original mesh has UV2 or the object is static for GI."), s.lightmapUvs);
                    s.showAllGizmos = EditorGUILayout.Toggle(new GUIContent("Show All Cutter Gizmos"), s.showAllGizmos);
                    s.gizmoColor = EditorGUILayout.ColorField("Gizmo Color", s.gizmoColor);
                    if (EditorGUI.EndChangeCheck())
                    {
                        s.SaveSettings();
                        SceneView.RepaintAll();
                    }
                    EditorGUILayout.Space();
                    if (GUILayout.Button("Rebuild All Cuts", GUILayout.Width(220))) CutterSystem.RebuildAll();
                    if (GUILayout.Button("Clean Up Unused Generated Meshes...", GUILayout.Width(220))) BasicCutterMenu.CleanUp();
                },
            };
        }
    }
}
