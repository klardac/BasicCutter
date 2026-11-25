using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>
    /// Scene view tool: pick a window / door prefab (a prefab with a Mesh Cutter) and click on walls to place
    /// it. The opening is centred on the clicked point, faces the wall, sits in the middle of its thickness,
    /// and a door stands on the wall's bottom. Optional grid along the wall.
    /// </summary>
    [EditorTool("Place Openings")]
    internal sealed class PlaceOpeningsTool : EditorTool
    {
        const string PrefabPref = "BasicCutter.Place.Prefab";
        const string GridPref = "BasicCutter.Place.Grid";
        const string GridStepPref = "BasicCutter.Place.GridStep";
        const string ParentPref = "BasicCutter.Place.ParentToWall";
        const string FlipPref = "BasicCutter.Place.Flip";

        /// <summary>How the next opening is placed.</summary>
        internal struct Settings
        {
            public bool grid;
            public float gridStep;
            public bool flip;
        }

        static GUIContent icon;
        Rect panel;

        public override GUIContent toolbarIcon =>
            icon ??= new GUIContent(EditorGUIUtility.IconContent("d_Prefab Icon").image, "Place Openings (Basic Cutter)\nClick on a wall to place the chosen window or door prefab.");

        // ------------------------------------------------------------------ settings

        internal static GameObject Prefab
        {
            get
            {
                string guid = EditorPrefs.GetString(PrefabPref, "");
                return string.IsNullOrEmpty(guid) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            }
            set => EditorPrefs.SetString(PrefabPref, value != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value)) : "");
        }

        static Settings Current => new Settings
        {
            grid = EditorPrefs.GetBool(GridPref, false),
            gridStep = EditorPrefs.GetFloat(GridStepPref, 0.25f),
            flip = EditorPrefs.GetBool(FlipPref, false),
        };

        static bool ParentToWall
        {
            get => EditorPrefs.GetBool(ParentPref, false);
            set => EditorPrefs.SetBool(ParentPref, value);
        }

        internal static bool IsOpeningPrefab(Object o) =>
            o is GameObject go && EditorUtility.IsPersistent(go) && go.GetComponent<MeshCutter>() != null;

        [MenuItem("Tools/Basic Cutter/Place Openings", false, 2)]
        static void Activate()
        {
            if (IsOpeningPrefab(Selection.activeObject)) Prefab = (GameObject)Selection.activeObject;
            ToolManager.SetActiveTool<PlaceOpeningsTool>();
            SceneView.lastActiveSceneView?.Focus();
        }

        public override void OnActivated()
        {
            if (IsOpeningPrefab(Selection.activeObject)) Prefab = (GameObject)Selection.activeObject;
        }

        // ------------------------------------------------------------------ scene view

        // the wall under the mouse, found on mouse events and reused while repainting
        struct Hover
        {
            public bool valid, cuttable;
            public MeshFilter wall;
            public Vector3 point, normal;
        }

        Hover hover;

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView view)) return;
            var e = Event.current;
            DrawPanel(view);

            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id); // clicks place instead of selecting
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { ToolManager.RestorePreviousPersistentTool(); e.Use(); return; }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F)
            {
                EditorPrefs.SetBool(FlipPref, !EditorPrefs.GetBool(FlipPref, false));
                view.Repaint();
                e.Use();
                return;
            }
            if (e.type == EventType.MouseLeaveWindow) { hover.valid = false; view.Repaint(); return; }

            var prefab = Prefab;
            if (prefab == null) { hover.valid = false; return; }
            var cutter = prefab.GetComponent<MeshCutter>();

            // Picking renders the scene for selection: only on mouse events, never while the view is being
            // drawn (a pick inside Repaint starts a second rendering and Unity reports a broken GUI state
            // on every frame).
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown)
            {
                hover.valid = !e.alt && !panel.Contains(e.mousePosition) &&
                              PickWall(e.mousePosition, HandleUtility.GUIPointToWorldRay(e.mousePosition), cutter,
                                  out hover.wall, out hover.point, out hover.normal, out hover.cuttable);
                if (e.type != EventType.MouseDown) view.Repaint();
            }
            if (!hover.valid || hover.wall == null) return;
            bool ok = TryPose(prefab, hover.wall, hover.point, hover.normal, Current, out var position, out var rotation);

            if (e.type == EventType.Repaint && ok) DrawPreview(cutter, position, rotation, prefab.transform.localScale, hover.cuttable);
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt && ok)
            {
                if (hover.cuttable) Place(prefab, hover.wall, position, rotation, ParentToWall);
                else Debug.LogWarning($"[Basic Cutter] '{hover.wall.name}' is not a closed, readable mesh, so it cannot be cut.", hover.wall);
                GUIUtility.hotControl = 0;
                e.Use();
            }
        }

        void DrawPanel(SceneView view)
        {
            Handles.BeginGUI();
            panel = new Rect(10, view.position.height - 150, 300, 118);
            GUILayout.BeginArea(panel, EditorStyles.helpBox);
            GUILayout.Label("Place Openings", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var prefab = (GameObject)EditorGUILayout.ObjectField("Prefab", Prefab, typeof(GameObject), false);
            if (EditorGUI.EndChangeCheck())
            {
                if (prefab == null || IsOpeningPrefab(prefab)) Prefab = prefab;
                else Debug.LogWarning("[Basic Cutter] Pick a prefab that has a Mesh Cutter on its root.");
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                bool grid = EditorGUILayout.ToggleLeft("Grid", EditorPrefs.GetBool(GridPref, false), GUILayout.Width(50));
                EditorPrefs.SetBool(GridPref, grid);
                using (new EditorGUI.DisabledScope(!grid))
                    EditorPrefs.SetFloat(GridStepPref, Mathf.Max(0.01f, EditorGUILayout.FloatField(EditorPrefs.GetFloat(GridStepPref, 0.25f), GUILayout.Width(50))));
                GUILayout.Label("m", GUILayout.Width(16));
                ParentToWall = EditorGUILayout.ToggleLeft("Parent To Wall", ParentToWall);
            }
            GUILayout.Label(Prefab == null ? "Choose a window or door prefab." : "Click: place   F: flip facing   Esc: exit", EditorStyles.miniLabel);
            GUILayout.EndArea();
            Handles.EndGUI();
        }

        static void DrawPreview(MeshCutter cutter, Vector3 position, Quaternion rotation, Vector3 scale, bool cuttable)
        {
            var outline = CutterShapeBuilder.Get(cutter);
            if (!outline.IsValid || outline.IsCustom) return;
            var m = Matrix4x4.TRS(position, rotation, scale);
            using (new Handles.DrawingScope(cuttable ? new Color(0.3f, 1f, 0.45f, 1f) : new Color(1f, 0.35f, 0.3f, 1f), m))
            {
                foreach (var loop in outline.loops)
                    foreach (double depth in new[] { outline.windowDepthMin, outline.windowDepthMax })
                    {
                        var pts = new Vector3[loop.Count + 1];
                        for (int i = 0; i <= loop.Count; i++)
                        {
                            var p = loop[i % loop.Count];
                            pts[i] = outline.ToLocal(p.x, p.y, depth).ToVector3();
                        }
                        Handles.DrawAAPolyLine(3f, pts);
                    }
            }
        }

        // ------------------------------------------------------------------ placement

        /// <summary>The wall under the mouse (windows and their trims are looked through) and the hit on it.</summary>
        static bool PickWall(Vector2 mouse, Ray ray, MeshCutter cutter, out MeshFilter wall, out Vector3 point, out Vector3 normal, out bool cuttable)
        {
            wall = null; point = default; normal = default; cuttable = false;
            var ignore = new List<GameObject>();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var go = HandleUtility.PickGameObject(mouse, false, ignore.ToArray());
                if (go == null) return false;
                if (go.GetComponentInParent<MeshCutter>(true) != null || !go.TryGetComponent(out MeshFilter mf) ||
                    ((1 << go.layer) & cutter.targetLayers.value) == 0 || !Raycast(mf, ray, out point, out normal, out cuttable))
                {
                    ignore.Add(go);
                    continue;
                }
                wall = mf;
                return true;
            }
            return false;
        }

        /// <summary>First hit of the ray on the wall's original (uncut) mesh, with the face normal towards the ray.</summary>
        internal static bool Raycast(MeshFilter wall, Ray ray, out Vector3 point, out Vector3 normal, out bool cuttable)
        {
            point = default; normal = default; cuttable = false;
            var cm = wall.GetComponent<CuttableMesh>();
            var mesh = cm != null && cm.SourceMesh != null ? cm.SourceMesh : wall.sharedMesh;
            if (mesh == null) return false;
            SourceMesh sm;
            try { sm = SourceMesh.Get(mesh); } catch { return false; }
            if (sm.TriangleCount == 0) return false;
            cuttable = sm.Closed && (cm == null || cm.State != CutState.Excluded);

            var toLocal = new DMatrix(wall.transform.worldToLocalMatrix);
            D3 origin = toLocal.Point(new D3(ray.origin)), dir = toLocal.Vector(new D3(ray.direction));
            var hits = new List<SourceMesh.Hit>();
            sm.Raycast(origin, dir, hits);
            double best = double.MaxValue;
            int tri = -1;
            foreach (var h in hits)
                if (h.t > 0 && h.t < best) { best = h.t; tri = h.tri; }
            if (tri < 0) return false;
            var toWorld = new DMatrix(wall.transform.localToWorldMatrix);
            point = toWorld.Point(origin + dir * best).ToVector3();
            normal = toWorld.Normal(sm.TriNormal(tri)).ToVector3().normalized;
            if (Vector3.Dot(normal, ray.direction) > 0) normal = -normal; // the face the ray sees
            return true;
        }

        /// <summary>
        /// Where the prefab goes for a hit on a wall: the opening's centre on the hit point (on the grid along the
        /// wall), its cut axis along the wall normal, its up along the wall's up, and a door on the wall's bottom.
        /// The depth is centred in the wall afterwards (see <see cref="Place"/>).
        /// </summary>
        internal static bool TryPose(GameObject prefab, MeshFilter wall, Vector3 hit, Vector3 normal, Settings s, out Vector3 position, out Quaternion rotation)
        {
            position = hit; rotation = Quaternion.identity;
            var cutter = prefab.GetComponent<MeshCutter>();
            if (cutter == null) return false;
            var outline = CutterShapeBuilder.Get(cutter);
            if (!outline.IsValid || outline.IsCustom || outline.loops.Count == 0) return false;

            // grid in the wall's own space, on the two axes along its surface
            if (s.grid && s.gridStep > 0)
            {
                var t = wall.transform;
                Vector3 local = t.InverseTransformPoint(hit);
                Vector3 n = t.InverseTransformDirection(normal);
                int k = Mathf.Abs(n.x) > Mathf.Abs(n.y) ? (Mathf.Abs(n.x) > Mathf.Abs(n.z) ? 0 : 2) : (Mathf.Abs(n.y) > Mathf.Abs(n.z) ? 1 : 2);
                Vector3 scale = t.lossyScale;
                for (int a = 0; a < 3; a++)
                {
                    if (a == k) continue;
                    float step = s.gridStep / Mathf.Max(1e-6f, Mathf.Abs(scale[a]));
                    local[a] = Mathf.Round(local[a] / step) * step;
                }
                hit = t.TransformPoint(local);
            }

            // facing: cut axis along the normal, the opening's up along the wall's up
            Vector3 axisLocal = outline.AxisVector.ToVector3().normalized;
            Vector3 upLocal = outline.ToLocal(outline.up2.x, outline.up2.y, 0).ToVector3().normalized;
            Vector3 facing = s.flip ? normal : -normal; // by default the cut axis points into the wall
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, facing);
            if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(wall.transform.forward, facing); // a floor or roof
            rotation = Quaternion.LookRotation(facing, up.normalized) * Quaternion.Inverse(Quaternion.LookRotation(axisLocal, upLocal));

            // the opening's centre (middle of its outline) on the hit point
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue, bottom = double.MaxValue;
            foreach (var loop in outline.loops)
                foreach (var p in loop)
                {
                    minU = System.Math.Min(minU, p.x); maxU = System.Math.Max(maxU, p.x);
                    minV = System.Math.Min(minV, p.y); maxV = System.Math.Max(maxV, p.y);
                    bottom = System.Math.Min(bottom, p.x * outline.up2.x + p.y * outline.up2.y);
                }
            double mid = (outline.windowDepthMin + outline.windowDepthMax) * 0.5;
            Vector3 scaleP = prefab.transform.localScale;
            Vector3 centreLocal = Vector3.Scale(scaleP, outline.ToLocal((minU + maxU) * 0.5, (minV + maxV) * 0.5, mid).ToVector3());
            position = hit - rotation * centreLocal;

            // a door stands on the wall's bottom
            if (cutter.door && Vector3.Dot(up, Vector3.up) > 0.7f && wall.TryGetComponent(out Renderer r))
            {
                double centreUp = ((minU + maxU) * 0.5) * outline.up2.x + ((minV + maxV) * 0.5) * outline.up2.y;
                Vector3 centre2D = outline.ToLocal((minU + maxU) * 0.5, (minV + maxV) * 0.5, mid).ToVector3();
                Vector3 bottomLocal = centre2D - upLocal * (float)(centreUp - bottom);
                Vector3 bottomWorld = position + rotation * Vector3.Scale(scaleP, bottomLocal);
                position.y += WallBottomBelow(wall, hit, normal) - bottomWorld.y;
            }
            return true;
        }

        /// <summary>
        /// The wall's bottom below a point: straight down through the wall's original mesh from just inside its
        /// surface (<paramref name="normal"/> points out of the wall at the point).
        /// </summary>
        internal static float WallBottomBelow(MeshFilter wall, Vector3 hit, Vector3 normal)
        {
            var cm = wall.GetComponent<CuttableMesh>();
            var mesh = cm != null && cm.SourceMesh != null ? cm.SourceMesh : wall.sharedMesh;
            var sm = SourceMesh.Get(mesh);
            var toLocal = new DMatrix(wall.transform.worldToLocalMatrix);
            var toWorld = new DMatrix(wall.transform.localToWorldMatrix);
            D3 o = toLocal.Point(new D3(hit - normal * 0.01f)), d = toLocal.Vector(new D3(Vector3.down));
            var hits = new List<SourceMesh.Hit>();
            sm.Raycast(o, d, hits);
            float lowest = float.MaxValue;
            foreach (var h in hits)
                if (h.t > 0) lowest = Mathf.Min(lowest, (float)toWorld.Point(o + d * h.t).y);
            return lowest < float.MaxValue ? lowest : wall.GetComponent<Renderer>().bounds.min.y;
        }

        /// <summary>Instantiates the prefab at the pose, centres it in the wall's thickness and records the undo.</summary>
        internal static GameObject Place(GameObject prefab, MeshFilter wall, Vector3 position, Quaternion rotation, bool parentToWall)
        {
            var scene = wall.gameObject.scene;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            if (go == null) return null;
            Undo.RegisterCreatedObjectUndo(go, "Place Opening");
            if (parentToWall) Undo.SetTransformParent(go.transform, wall.transform, "Place Opening");
            go.transform.SetPositionAndRotation(position, rotation);
            WallSnapping.Snap(go.GetComponent<MeshCutter>(), false); // centre in the wall's thickness
            EditorSceneManager.MarkSceneDirty(scene);
            CutterSystem.MarkDirty();
            return go;
        }
    }
}
