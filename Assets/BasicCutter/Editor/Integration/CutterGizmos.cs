using UnityEditor;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>Draws the cutting outline. Uses the Gizmos API, so it works in Built-in, URP and HDRP.</summary>
    internal static class CutterGizmos
    {
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable)]
        static void Draw(MeshCutter cutter, GizmoType type)
        {
            var settings = BasicCutterSettings.instance;
            bool selected = (type & (GizmoType.Selected | GizmoType.InSelectionHierarchy)) != 0;
            if (!selected && !settings.showAllGizmos) return;

            var outline = CutterShapeBuilder.Get(cutter);
            if (!outline.IsValid) return;

            var status = CutterSystem.GetStatus(cutter);
            double d0, d1;
            if (status != null && !double.IsNaN(status.d0)) { d0 = status.d0; d1 = status.d1; }
            else { d0 = outline.windowDepthMin; d1 = outline.windowDepthMax; }

            var oldMatrix = Gizmos.matrix;
            var oldColor = Gizmos.color;
            Gizmos.matrix = cutter.transform.localToWorldMatrix;
            var c = settings.gizmoColor;
            if (!selected) c.a *= 0.4f;
            bool hasTargets = status != null && status.targets.Count > 0;
            if (!hasTargets) c = Color.Lerp(c, Color.gray, 0.5f);
            Gizmos.color = c;

            if (outline.IsCustom)
            {
                foreach (var t in outline.customVolume)
                {
                    Gizmos.DrawLine(t.p0.ToVector3(), t.p1.ToVector3());
                    Gizmos.DrawLine(t.p1.ToVector3(), t.p2.ToVector3());
                    Gizmos.DrawLine(t.p2.ToVector3(), t.p0.ToVector3());
                }
            }
            else
            {
                double shift = status != null ? status.bottomShift : 0;
                for (int li = 0; li < outline.loops.Count; li++)
                {
                    var loop = outline.ShiftedLoop(li, shift);
                    int n = loop.Count;
                    int step = Mathf.Max(1, n / 16);
                    for (int i = 0; i < n; i++)
                    {
                        var p = loop[i]; var q = loop[(i + 1) % n];
                        Vector3 a0 = outline.ToLocal(p.x, p.y, d0).ToVector3(), b0 = outline.ToLocal(q.x, q.y, d0).ToVector3();
                        Vector3 a1 = outline.ToLocal(p.x, p.y, d1).ToVector3(), b1 = outline.ToLocal(q.x, q.y, d1).ToVector3();
                        Gizmos.DrawLine(a0, b0);
                        Gizmos.DrawLine(a1, b1);
                        if (i % step == 0) Gizmos.DrawLine(a0, a1);
                    }
                }
            }
            Gizmos.matrix = oldMatrix;
            Gizmos.color = oldColor;
        }
    }
}
