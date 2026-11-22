using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BasicCutter.EditorTools
{
    [CustomEditor(typeof(MeshCutter)), CanEditMultipleObjects]
    internal sealed class MeshCutterEditor : Editor
    {
        /// <summary>
        /// What the opening is. Not stored: it is read from (and written to) Door, Cut and Axis, so
        /// existing cutters keep working unchanged.
        /// </summary>
        enum OpeningType { Window, Door, Niche, Skylight }

        static readonly GUIContent[] TypeLabels =
        {
            new GUIContent("Window", "A hole through the wall."),
            new GUIContent("Door", "A hole through the wall whose bottom edge reaches the floor (Door mode)."),
            new GUIContent("Niche", "A recess in one face of the wall."),
            new GUIContent("Skylight", "A hole through a roof or floor: cuts along the local Y axis."),
        };

        SerializedProperty shape, axis, padding, depthMargin, targetLayers, shapeSize, shapeCenter, archRise, segments, customMesh, door, doorBottomSnap;
        SerializedProperty depth, nicheDepth, nicheFlip, jambMaterial;
        SerializedProperty trim, trimSides, trimWidth, trimThickness, trimMaterial, sill, sillDepth, sillOverhang;

        static GUIStyle richLabel, richMini;

        void OnEnable()
        {
            shape = Find(nameof(MeshCutter.shape));
            axis = Find(nameof(MeshCutter.axis));
            padding = Find(nameof(MeshCutter.padding));
            depthMargin = Find(nameof(MeshCutter.depthMargin));
            targetLayers = Find(nameof(MeshCutter.targetLayers));
            shapeSize = Find(nameof(MeshCutter.shapeSize));
            shapeCenter = Find(nameof(MeshCutter.shapeCenter));
            archRise = Find(nameof(MeshCutter.archRise));
            segments = Find(nameof(MeshCutter.segments));
            customMesh = Find(nameof(MeshCutter.customMesh));
            door = Find(nameof(MeshCutter.door));
            doorBottomSnap = Find(nameof(MeshCutter.doorBottomSnap));
            depth = Find(nameof(MeshCutter.depth));
            nicheDepth = Find(nameof(MeshCutter.nicheDepth));
            nicheFlip = Find(nameof(MeshCutter.nicheFlip));
            jambMaterial = Find(nameof(MeshCutter.jambMaterial));
            trim = Find(nameof(MeshCutter.trim));
            trimSides = Find(nameof(MeshCutter.trimSides));
            trimWidth = Find(nameof(MeshCutter.trimWidth));
            trimThickness = Find(nameof(MeshCutter.trimThickness));
            trimMaterial = Find(nameof(MeshCutter.trimMaterial));
            sill = Find(nameof(MeshCutter.sill));
            sillDepth = Find(nameof(MeshCutter.sillDepth));
            sillOverhang = Find(nameof(MeshCutter.sillOverhang));
        }

        SerializedProperty Find(string name) => serializedObject.FindProperty(name);

        public override void OnInspectorGUI()
        {
            richLabel ??= new GUIStyle(EditorStyles.label) { richText = true };
            richMini ??= new GUIStyle(EditorStyles.miniLabel) { richText = true, wordWrap = true };

            serializedObject.Update();

            if (targets.Length == 1) DrawStatusCard((MeshCutter)target);
            else EditorGUILayout.HelpBox($"{targets.Length} cutters selected. Select one to see what it cuts.", MessageType.None);

            var type = DrawTypeBar();
            var s = (CutterShape)shape.enumValueIndex;
            bool mixedShape = shape.hasMultipleDifferentValues;
            bool parametric = !mixedShape && (s == CutterShape.Box || s == CutterShape.Arch || s == CutterShape.Circle);
            bool custom = !mixedShape && s == CutterShape.CustomMesh;

            // ---- shape
            if (Section("Shape", "Cut Details", true))
            {
                EditorGUILayout.PropertyField(shape);
                if (parametric)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUILayout.PropertyField(shapeSize, new GUIContent("Size", "Width and height of the shape (local units, usually metres)."));
                        EditorGUILayout.PropertyField(shapeCenter, new GUIContent("Center", "Offset of the shape from the object's pivot."));
                        if (s == CutterShape.Arch) EditorGUILayout.PropertyField(archRise);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(EditorGUIUtility.labelWidth);
                        if (GUILayout.Button(new GUIContent("Fit To Window Meshes", "Sets Size and Center from the outline of the meshes under this object."), EditorStyles.miniButton))
                            FitToMeshes();
                    }
                }
                if (custom)
                    using (new EditorGUI.IndentLevelScope())
                        EditorGUILayout.PropertyField(customMesh);
                if (!custom)
                    Cm(padding, "Padding", -20, 20, "Grows (+) or shrinks (-) the opening. -1 cm hides the hole edge behind the frame, so no light leaks through the seam.");
            }
            EndSection();

            // ---- type specific
            if (type == OpeningType.Door && !custom)
            {
                if (Section("Door", "Door", true))
                    Cm(doorBottomSnap, "Bottom Snap", 0, 30, "How far the bottom edge may move to reach the floor surface or the wall's bottom. Further than this, the door is left where it is (e.g. a raised threshold).");
                EndSection();
            }
            else if (type == OpeningType.Niche)
            {
                if (Section("Niche", "Niche", true))
                {
                    Cm(nicheDepth, "Depth", 1, 100, "How deep the recess goes into the wall, from the wall face.");
                    EditorGUILayout.PropertyField(nicheFlip, new GUIContent("Open Other Face", nicheFlip.tooltip));
                }
                EndSection();
            }

            // ---- surfaces
            if (Section("Surfaces", "Surfaces", true))
            {
                MaterialField(jambMaterial, "Jamb Material", "Material of the opening's inner faces.");
                if (!custom)
                {
                    EditorGUILayout.PropertyField(trim, new GUIContent("Trim", "A frame around the opening on the wall surface. It follows curved walls."));
                    if (trim.boolValue || trim.hasMultipleDifferentValues)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            if (type != OpeningType.Niche)
                                EditorGUILayout.PropertyField(trimSides, new GUIContent("Sides", trimSides.tooltip));
                            Cm(trimWidth, "Width", 1, 40, "Width of the trim around the opening.");
                            Cm(trimThickness, "Thickness", 0.5f, 20, "How far the trim stands out of the wall.");
                            MaterialField(trimMaterial, "Material", "Trim material.");
                            if (type == OpeningType.Window || type == OpeningType.Niche)
                            {
                                EditorGUILayout.PropertyField(sill, new GUIContent("Sill", "A sill under the opening."));
                                if (sill.boolValue || sill.hasMultipleDifferentValues)
                                    using (new EditorGUI.IndentLevelScope())
                                    {
                                        Cm(sillDepth, "Depth", 0, 30, "How much further than the trim the sill stands out.");
                                        Cm(sillOverhang, "Overhang", 0, 30, "How much wider than the trim the sill is on each side.");
                                    }
                            }
                        }
                }
            }
            EndSection();

            // ---- advanced
            if (Section("Advanced", "Advanced", false))
            {
                EditorGUILayout.PropertyField(axis, new GUIContent("Axis", "Local axis that goes through the wall. Auto picks the thinnest axis of the window meshes. Skylight uses Y."));
                if (parametric && s != CutterShape.Box) EditorGUILayout.PropertyField(segments);
                Cm(depthMargin, "Depth Margin", 0, 10, "Extra depth on both sides of the wall so the cut always goes fully through.");
                EditorGUILayout.PropertyField(targetLayers, new GUIContent("Target Layers", "Only objects on these layers can be cut."));
            }
            EndSection();

            serializedObject.ApplyModifiedProperties();

            // ---- actions
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Snap To Wall", "Move onto the nearest wall along the cut axis and centre in its thickness (Alt+Shift+W).")))
                    foreach (MeshCutter c in targets) WallSnapping.Snap(c, WallSnapping.AlignRotation);
                WallSnapping.AlignRotation = GUILayout.Toggle(WallSnapping.AlignRotation, new GUIContent("Align Rotation", "Snap To Wall also rotates the window to face the wall (useful on curved walls)."), "Button", GUILayout.Width(110));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Rebuild", "Cut the walls of this window again."))) CutterSystem.Invalidate(AffectedWalls());
                if (GUILayout.Button(new GUIContent("Bake Walls", "Freeze the walls this window cuts: they keep their current mesh.")))
                    SetState(CutState.Frozen, "Bake Walls");
                if (GUILayout.Button(new GUIContent("Unbake Walls", "Let the walls this window cuts update live again.")))
                    SetState(CutState.Live, "Unbake Walls");
            }
        }

        // ------------------------------------------------------------------ type

        static OpeningType TypeOf(MeshCutter c) =>
            c.depth == CutDepth.Niche ? OpeningType.Niche :
            c.door ? OpeningType.Door :
            c.axis == CutterAxis.Y ? OpeningType.Skylight : OpeningType.Window;

        OpeningType DrawTypeBar()
        {
            var types = targets.Cast<MeshCutter>().Select(TypeOf).Distinct().ToList();
            int current = types.Count == 1 ? (int)types[0] : -1;
            EditorGUILayout.Space(2);
            EditorGUI.BeginChangeCheck();
            int picked = GUILayout.Toolbar(current, TypeLabels, GUILayout.Height(24));
            if (EditorGUI.EndChangeCheck() && picked >= 0)
            {
                ApplyType((OpeningType)picked);
                current = picked;
            }
            EditorGUILayout.Space(2);
            return current >= 0 ? (OpeningType)current : OpeningType.Window;
        }

        void ApplyType(OpeningType t)
        {
            door.boolValue = t == OpeningType.Door;
            depth.enumValueIndex = (int)(t == OpeningType.Niche ? CutDepth.Niche : CutDepth.Through);
            if (t == OpeningType.Skylight) axis.enumValueIndex = (int)CutterAxis.Y;
            else if (axis.enumValueIndex == (int)CutterAxis.Y && !axis.hasMultipleDifferentValues) axis.enumValueIndex = (int)CutterAxis.Auto;
        }

        // ------------------------------------------------------------------ status card

        void DrawStatusCard(MeshCutter cutter)
        {
            var status = CutterSystem.GetStatus(cutter);
            var settings = BasicCutterSettings.instance;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string dot, title;
                if (!cutter.isActiveAndEnabled) { dot = Gray; title = "Disabled: walls show their original mesh"; }
                else if (status == null) { dot = Gray; title = "Waiting for the next update"; }
                else if (status.outline == null || !status.outline.IsValid) { dot = Red; title = "Cannot build the opening"; }
                else if (status.pending) { dot = Blue; title = "Cutting in the background..."; }
                else if (status.targets.Count == 0) { dot = Gray; title = "Not cutting anything"; }
                else if (status.issues.Count > 0) { dot = Amber; title = "Cutting " + WallNames(status) + ", with problems"; }
                else { dot = Green; title = "Cutting " + WallNames(status); }
                EditorGUILayout.LabelField($"<color={dot}>●</color>  {title}", richLabel);

                if (status != null && status.outline != null && status.outline.IsValid)
                {
                    var details = new List<string>();
                    string axisName = "XYZ"[status.outline.axis].ToString();
                    details.Add($"Local {axisName} axis" + (cutter.axis == CutterAxis.Auto ? " (auto)" : ""));
                    if (status.outline.IsCustom) details.Add($"{status.outline.customVolume.Count} volume triangles");
                    else details.Add($"{status.outline.loops.Sum(l => l.Count)} outline points");
                    if (cutter.depth == CutDepth.Niche && status.niche)
                        details.Add($"niche opens on the {(status.nicheNear ? "-" : "+")}{axisName} side");
                    if (cutter.door && status.outline.door && status.targets.Count > 0)
                    {
                        double sh = status.bottomShift * 100;
                        details.Add(Math.Abs(sh) < 1e-4 ? "bottom edge unchanged" : sh > 0 ? $"bottom raised {sh:0.#} cm to the floor" : $"bottom lowered {-sh:0.#} cm to the wall bottom");
                    }
                    EditorGUILayout.LabelField(string.Join("  ·  ", details), richMini);
                }

                // walls: click to select
                if (status != null && status.targets.Count > 0)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        foreach (var r in status.targets.Distinct())
                            if (r != null && GUILayout.Button(new GUIContent(r.name, "Select this wall"), EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                                Select(r.gameObject);
                        GUILayout.FlexibleSpace();
                    }

                // problems with one-click fixes
                if (status != null)
                {
                    foreach (var issue in status.issues) DrawIssue(issue);
                    if (cutter.door && status.outline != null && status.outline.IsValid && !status.outline.door)
                        EditorGUILayout.HelpBox("Door needs a flat bottom edge on the outline. Check that the door's local up axis points up.", MessageType.Warning);
                    if (status.outline != null && !status.outline.IsValid) DrawUnreadableWindowMeshes(cutter);
                    foreach (var info in status.infos) EditorGUILayout.LabelField("<color=" + Blue + ">ⓘ</color>  " + info, richMini);
                    if (cutter.isActiveAndEnabled && status.outline != null && status.outline.IsValid && status.targets.Count == 0 && !status.pending)
                        Hint("Place the window inside a closed wall mesh.", "Snap To Wall", () => WallSnapping.Snap(cutter, WallSnapping.AlignRotation));
                }
                if (!settings.liveUpdate)
                    Hint("Live Update is off: use Rebuild to apply changes.", "Settings", () => SettingsService.OpenProjectSettings(BasicCutterSettingsProvider.Path));
            }
        }

        const string Green = "#4CAF50", Amber = "#E0A030", Red = "#E05050", Gray = "#909090", Blue = "#4A90D9";

        static string WallNames(CutterSystem.CutterStatus st)
        {
            var names = st.targets.Where(r => r != null).Select(r => r.name).Distinct().ToList();
            return names.Count <= 2 ? string.Join(", ", names) : $"{names.Count} walls";
        }

        static void DrawIssue(CutterSystem.Issue issue)
        {
            switch (issue.fix)
            {
                case CutterSystem.IssueFix.EnableReadWrite when issue.target is Mesh mesh && CanEnableReadWrite(mesh):
                    Hint(issue.text, "Enable Read/Write", () => EnableReadWrite(new[] { mesh }), MessageType.Warning);
                    break;
                case CutterSystem.IssueFix.SelectObject when issue.target != null:
                    Hint(issue.text, "Select", () => Select(issue.target), MessageType.Warning);
                    break;
                default:
                    EditorGUILayout.HelpBox(issue.text, MessageType.Warning);
                    break;
            }
        }

        /// <summary>The window's own meshes cannot be read (outline error): offer to fix their import settings.</summary>
        static void DrawUnreadableWindowMeshes(MeshCutter cutter)
        {
            var meshes = CutterShapeBuilder.WindowMeshes(cutter).Select(mf => mf.sharedMesh)
                .Where(m => m != null && !m.isReadable && CanEnableReadWrite(m)).Distinct().ToArray();
            if (meshes.Length == 0) return;
            Hint($"{meshes.Length} window mesh(es) have Read/Write turned off.", "Enable Read/Write", () => EnableReadWrite(meshes), MessageType.Warning);
        }

        static void Hint(string text, string button, Action action, MessageType type = MessageType.Info)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox(text, type);
                if (GUILayout.Button(button, GUILayout.Width(Mathf.Max(70, button.Length * 7 + 16)), GUILayout.Height(38))) action();
            }
        }

        static void Select(Object o)
        {
            Selection.activeObject = o;
            EditorGUIUtility.PingObject(o);
        }

        static bool CanEnableReadWrite(Mesh m) => AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(m)) is ModelImporter;

        static void EnableReadWrite(IEnumerable<Mesh> meshes)
        {
            foreach (var path in meshes.Select(AssetDatabase.GetAssetPath).Distinct())
                if (AssetImporter.GetAtPath(path) is ModelImporter mi && !mi.isReadable)
                {
                    mi.isReadable = true;
                    mi.SaveAndReimport();
                }
            SourceMesh.ClearCache();
            CutterShapeBuilder.ClearCache();
            CutterSystem.MarkDirty();
            GUIUtility.ExitGUI(); // the reimport invalidated this inspector frame
        }

        // ------------------------------------------------------------------ fields

        /// <summary>Foldout section; its open state is remembered per user.</summary>
        static bool Section(string key, string label, bool openByDefault)
        {
            string pref = "BasicCutter.Inspector." + key;
            bool open = EditorPrefs.GetBool(pref, openByDefault);
            EditorGUILayout.Space(2);
            bool now = EditorGUILayout.BeginFoldoutHeaderGroup(open, label);
            if (now != open) EditorPrefs.SetBool(pref, now);
            return now;
        }

        static void EndSection() => EditorGUILayout.EndFoldoutHeaderGroup();

        /// <summary>A length shown and edited in centimetres (stored in local units, 1 unit = 1 m).</summary>
        static void Cm(SerializedProperty p, string label, float minCm, float maxCm, string tooltip)
        {
            var rect = EditorGUILayout.GetControlRect();
            var content = EditorGUI.BeginProperty(rect, new GUIContent(label, tooltip + " (centimetres, assuming 1 unit = 1 m)"), p);
            var field = new Rect(rect.x, rect.y, rect.width - 24, rect.height);
            EditorGUI.showMixedValue = p.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            float cm = EditorGUI.Slider(field, content, p.floatValue * 100f, minCm, maxCm);
            if (EditorGUI.EndChangeCheck()) p.floatValue = Mathf.Round(cm * 100f) / 10000f; // 0.01 cm steps
            EditorGUI.showMixedValue = false;
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0; // the unit label must not be indented (it would be clipped)
            EditorGUI.LabelField(new Rect(rect.xMax - 22, rect.y, 22, rect.height), "cm", EditorStyles.miniLabel);
            EditorGUI.indentLevel = indent;
            EditorGUI.EndProperty();
        }

        /// <summary>Material field that says what an empty value means.</summary>
        static void MaterialField(SerializedProperty p, string label, string tooltip)
        {
            EditorGUILayout.PropertyField(p, new GUIContent(label, tooltip + " Empty: the wall's own material."));
            if (p.hasMultipleDifferentValues || p.objectReferenceValue != null) return;
            var r = EditorGUILayout.GetControlRect(false, 12);
            r.xMin += EditorGUIUtility.labelWidth + 2;
            r.y -= 2;
            EditorGUI.LabelField(r, "Empty: uses the wall's material", MiniGrey);
        }

        static GUIStyle miniGrey;
        static GUIStyle MiniGrey => miniGrey ??= new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.UpperLeft };

        // ------------------------------------------------------------------ actions

        IEnumerable<CuttableMesh> AffectedWalls()
        {
            foreach (MeshCutter c in targets)
            {
                var st = CutterSystem.GetStatus(c);
                if (st == null) continue;
                foreach (var r in st.targets)
                    if (r != null && r.TryGetComponent(out CuttableMesh cm)) yield return cm;
            }
        }

        void SetState(CutState state, string undoName)
        {
            var walls = AffectedWalls().Distinct().ToArray();
            if (walls.Length == 0) return;
            Undo.RecordObjects(walls, undoName);
            foreach (var w in walls) { w.state = state; EditorUtility.SetDirty(w); PrefabUtility.RecordPrefabInstancePropertyModifications(w); }
            CutterSystem.MarkDirty();
        }

        void FitToMeshes()
        {
            serializedObject.ApplyModifiedProperties();
            foreach (MeshCutter c in targets)
            {
                var old = c.shape;
                Undo.RecordObject(c, "Fit Cutter Size");
                c.shape = CutterShape.Exact;
                var o = CutterShapeBuilder.Get(c);
                c.shape = old;
                if (!o.IsValid) { Debug.LogWarning("[Basic Cutter] No window meshes to fit to.", c); continue; }
                // bounds in the right/up frame used by parametric shapes
                D3 right, up;
                switch (o.axis)
                {
                    case 0: right = new D3(0, 0, 1); up = new D3(0, 1, 0); break;
                    case 1: right = new D3(1, 0, 0); up = new D3(0, 0, 1); break;
                    default: right = new D3(1, 0, 0); up = new D3(0, 1, 0); break;
                }
                double minR = double.MaxValue, maxR = double.MinValue, minU = double.MaxValue, maxU = double.MinValue;
                foreach (var loop in o.loops)
                    foreach (var p in loop)
                    {
                        var l = o.ToLocal(p.x, p.y, 0);
                        double rr = D3.Dot(l, right), uu = D3.Dot(l, up);
                        minR = Math.Min(minR, rr); maxR = Math.Max(maxR, rr);
                        minU = Math.Min(minU, uu); maxU = Math.Max(maxU, uu);
                    }
                c.shapeSize = new Vector2((float)(maxR - minR), (float)(maxU - minU));
                c.shapeCenter = new Vector2((float)(minR + maxR) * 0.5f, (float)(minU + maxU) * 0.5f);
                if (c.shape == CutterShape.Arch) c.archRise = Mathf.Min(c.archRise, c.shapeSize.x * 0.5f);
                EditorUtility.SetDirty(c);
                PrefabUtility.RecordPrefabInstancePropertyModifications(c);
            }
            serializedObject.Update();
            CutterSystem.MarkDirty();
        }
    }
}
