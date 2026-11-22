using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>Moves a cutter onto the nearest wall along its cut axis and centres it in the wall thickness.</summary>
    internal static class WallSnapping
    {
        const string AlignPref = "BasicCutter.SnapAlignRotation";

        public static bool AlignRotation
        {
            get => EditorPrefs.GetBool(AlignPref, true);
            set => EditorPrefs.SetBool(AlignPref, value);
        }

        [Shortcut("Basic Cutter/Snap Selected Cutters To Wall", KeyCode.W, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        static void SnapSelectionShortcut()
        {
            foreach (var go in Selection.gameObjects)
            {
                var c = go.GetComponentInParent<MeshCutter>();
                if (c != null) Snap(c, AlignRotation);
            }
        }

        struct WallHit
        {
            public double start, end;     // depth along the cutter axis
            public Vector3 normalWorld;   // wall normal where the ray enters
            public double distance;
        }

        public static bool Snap(MeshCutter cutter, bool alignRotation)
        {
            var outline = CutterShapeBuilder.Get(cutter);
            if (!outline.IsValid || outline.IsCustom || outline.interiorPoints.Count == 0)
            {
                Debug.LogWarning("[Basic Cutter] Snap needs a valid Exact / Box / Arch / Circle shape.", cutter);
                return false;
            }
            if (!FindWall(cutter, outline, out var hit))
            {
                Debug.LogWarning($"[Basic Cutter] No closed wall found along the cut axis of '{cutter.name}'.", cutter);
                return false;
            }

            Undo.RecordObject(cutter.transform, "Snap To Wall");
            var t = cutter.transform;
            if (alignRotation)
            {
                D3 axis = outline.AxisVector;
                Vector3 axisWorld = t.TransformDirection(axis.ToVector3()).normalized;
                Vector3 n = hit.normalWorld.normalized;
                if (Vector3.Dot(axisWorld, n) < 0) n = -n;
                var delta = Quaternion.FromToRotation(axisWorld, n);
                var ip = outline.interiorPoints[0];
                Vector3 pivot = t.TransformPoint(outline.ToLocal(ip.x, ip.y, (outline.windowDepthMin + outline.windowDepthMax) * 0.5).ToVector3());
                t.rotation = delta * t.rotation;
                t.position = pivot + delta * (t.position - pivot);
                if (!FindWall(cutter, outline, out hit)) return true;
            }
            double centre = (outline.windowDepthMin + outline.windowDepthMax) * 0.5;
            double target = (hit.start + hit.end) * 0.5;
            Vector3 move = t.TransformVector((outline.AxisVector * (target - centre)).ToVector3());
            t.position += move;
            CutterSystem.MarkDirty();
            return true;
        }

        static bool FindWall(MeshCutter cutter, CutterOutline o, out WallHit best)
        {
            best = default;
            best.distance = double.MaxValue;
            var stage = UnityEditor.SceneManagement.StageUtility.GetCurrentStageHandle();
            var ip = o.interiorPoints[0];
            double centre = (o.windowDepthMin + o.windowDepthMax) * 0.5;
            var hits = new List<SourceMesh.Hit>();
            var intervals = new List<(double, double)>();
            bool found = false;
            foreach (var r in stage.FindComponentsOfType<MeshRenderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (((1 << r.gameObject.layer) & cutter.targetLayers.value) == 0) continue;
                if (r.GetComponentInParent<MeshCutter>(true) != null) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null) continue;
                var cm = r.GetComponent<CuttableMesh>();
                if (cm != null && cm.state == CutState.Excluded) continue;
                var mesh = cm != null && cm.sourceMesh != null ? cm.sourceMesh : mf.sharedMesh;
                if (mesh == null) continue;
                SourceMesh sm;
                try { sm = SourceMesh.Get(mesh); } catch { continue; }
                if (sm.TriangleCount == 0 || !sm.Closed) continue;

                var toWall = new DMatrix(r.transform.worldToLocalMatrix * cutter.transform.localToWorldMatrix);
                double start = -1e4;
                D3 origin = toWall.Point(o.ToLocal(ip.x, ip.y, centre + start));
                D3 dir = toWall.Vector(o.AxisVector);
                sm.Raycast(origin, dir, hits);
                SourceMesh.HitsToIntervals(hits, CutEngine.Epsilon(sm), intervals);
                foreach (var (s, e) in intervals)
                {
                    double ds = centre + start + s, de = centre + start + e;
                    double dist = centre >= ds && centre <= de ? 0 : Math.Min(Math.Abs(ds - centre), Math.Abs(de - centre));
                    if (dist >= best.distance) continue;
                    // normal of the entry triangle
                    int tri = -1;
                    foreach (var h in hits) if (h.entering && Math.Abs(h.t - s) < 1e-6) { tri = h.tri; break; }
                    Vector3 nWorld = r.transform.TransformDirection(o.AxisVector.ToVector3());
                    if (tri >= 0)
                    {
                        var nLocal = sm.TriNormal(tri);
                        var inv = new DMatrix(r.transform.localToWorldMatrix);
                        nWorld = -inv.Normal(nLocal).ToVector3();
                    }
                    best = new WallHit { start = ds, end = de, normalWorld = nWorld, distance = dist };
                    found = true;
                }
            }
            return found;
        }
    }
}
