using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>
    /// Aligns and spaces windows by the edges of their openings (not their pivots), so different models line
    /// up: bottoms / middles / tops at the same height, a given height above the wall's bottom, even gaps
    /// along the wall, or a fixed gap. Openings are moved along the wall and seated on it again, which keeps
    /// them on curved walls too. One undo step per action.
    /// </summary>
    internal static class OpeningAlignment
    {
        internal enum Edge { Bottom, Middle, Top }

        /// <summary>An opening's extent: height range and range along a horizontal direction.</summary>
        internal struct Extent
        {
            public float minY, maxY, minS, maxS;
            public float MidY => (minY + maxY) * 0.5f;
            public float MidS => (minS + maxS) * 0.5f;
        }

        /// <summary>World points of the opening's outline (its middle depth).</summary>
        internal static List<Vector3> OpeningPoints(MeshCutter c)
        {
            var pts = new List<Vector3>();
            var o = CutterShapeBuilder.Get(c);
            if (!o.IsValid) return pts;
            var t = c.transform;
            if (o.IsCustom)
            {
                foreach (var v in o.customVolume) { pts.Add(t.TransformPoint(v.p0.ToVector3())); pts.Add(t.TransformPoint(v.p1.ToVector3())); pts.Add(t.TransformPoint(v.p2.ToVector3())); }
                return pts;
            }
            double mid = (o.windowDepthMin + o.windowDepthMax) * 0.5;
            foreach (var loop in o.loops)
                foreach (var p in loop) pts.Add(t.TransformPoint(o.ToLocal(p.x, p.y, mid).ToVector3()));
            return pts;
        }

        internal static Extent Measure(MeshCutter c, Vector3 along)
        {
            var e = new Extent { minY = float.MaxValue, maxY = float.MinValue, minS = float.MaxValue, maxS = float.MinValue };
            foreach (var p in OpeningPoints(c))
            {
                e.minY = Mathf.Min(e.minY, p.y); e.maxY = Mathf.Max(e.maxY, p.y);
                float s = Vector3.Dot(p, along);
                e.minS = Mathf.Min(e.minS, s); e.maxS = Mathf.Max(e.maxS, s);
            }
            return e;
        }

        static List<MeshCutter> Valid(IEnumerable<MeshCutter> cutters) =>
            cutters.Where(c => c != null && OpeningPoints(c).Count > 0).Distinct().ToList();

        // ------------------------------------------------------------------ height

        /// <summary>Moves every opening up or down so its edge is at the reference's height.</summary>
        public static void AlignHeights(IEnumerable<MeshCutter> cutters, MeshCutter reference, Edge edge)
        {
            var list = Valid(cutters);
            if (reference == null || !list.Contains(reference)) reference = list.FirstOrDefault();
            if (reference == null) return;
            float target = Pick(Measure(reference, Vector3.right), edge);
            Move(list, "Align Openings", c => Vector3.up * (target - Pick(Measure(c, Vector3.right), edge)), reseat: false);
        }

        /// <summary>Puts the bottom of every opening this far above the bottom of its wall (a sill height).</summary>
        public static void SetBottomHeight(IEnumerable<MeshCutter> cutters, float height)
        {
            Move(Valid(cutters), "Set Opening Height", c =>
            {
                float? bottom = WallBottom(c);
                if (bottom == null) return Vector3.zero;
                return Vector3.up * (bottom.Value + height - Measure(c, Vector3.right).minY);
            }, reseat: false);
        }

        static float Pick(Extent e, Edge edge) => edge == Edge.Bottom ? e.minY : edge == Edge.Top ? e.maxY : e.MidY;

        // ------------------------------------------------------------------ along the wall

        /// <summary>The direction along the wall: from one end of the row to the other, level.</summary>
        internal static Vector3 RowDirection(List<MeshCutter> list)
        {
            var centres = list.Select(c => Centre(c)).ToList();
            float best = -1; Vector3 dir = Vector3.right;
            for (int i = 0; i < centres.Count; i++)
                for (int j = i + 1; j < centres.Count; j++)
                {
                    var d = centres[j] - centres[i];
                    d.y = 0;
                    if (d.sqrMagnitude > best) { best = d.sqrMagnitude; dir = d; }
                }
            if (best < 1e-8f)
            {
                // all at one spot: along the first wall's surface
                var axis = list[0].transform.TransformDirection(CutterShapeBuilder.Get(list[0]).AxisVector.ToVector3());
                dir = Vector3.Cross(Vector3.up, axis);
            }
            dir.y = 0;
            return dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.right;
        }

        static Vector3 Centre(MeshCutter c)
        {
            var pts = OpeningPoints(c);
            var sum = Vector3.zero;
            foreach (var p in pts) sum += p;
            return pts.Count > 0 ? sum / pts.Count : c.transform.position;
        }

        /// <summary>Keeps the two outer openings and spaces the others so all gaps between openings are equal.</summary>
        public static void DistributeEvenly(IEnumerable<MeshCutter> cutters)
        {
            var list = Valid(cutters);
            if (list.Count < 3) return;
            var along = RowDirection(list);
            var row = list.Select(c => (c, e: Measure(c, along))).OrderBy(x => x.e.MidS).ToList();
            float span = row[row.Count - 1].e.maxS - row[0].e.minS;
            float widths = row.Sum(x => x.e.maxS - x.e.minS);
            float gap = (span - widths) / (row.Count - 1);
            Place(row, along, gap, "Distribute Openings", keepLast: true);
        }

        /// <summary>Keeps the first opening of the row and puts the others this far apart (edge to edge).</summary>
        public static void SetSpacing(IEnumerable<MeshCutter> cutters, float gap)
        {
            var list = Valid(cutters);
            if (list.Count < 2) return;
            var along = RowDirection(list);
            var row = list.Select(c => (c, e: Measure(c, along))).OrderBy(x => x.e.MidS).ToList();
            Place(row, along, gap, "Space Openings", keepLast: false);
        }

        static void Place(List<(MeshCutter c, Extent e)> row, Vector3 along, float gap, string undo, bool keepLast)
        {
            var moves = new Dictionary<MeshCutter, Vector3>();
            float cursor = row[0].e.maxS + gap;
            for (int i = 1; i < row.Count; i++)
            {
                if (keepLast && i == row.Count - 1) break;
                var (c, e) = row[i];
                moves[c] = along * (cursor - e.minS);
                cursor += (e.maxS - e.minS) + gap;
            }
            Move(moves.Keys.ToList(), undo, c => moves[c], reseat: true);
        }

        // ------------------------------------------------------------------ helpers

        static void Move(List<MeshCutter> list, string undo, System.Func<MeshCutter, Vector3> delta, bool reseat)
        {
            if (list.Count == 0) return;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undo);
            // measure everything first: moving one opening must not change another's target
            var deltas = list.ToDictionary(c => c, delta);
            foreach (var c in list)
            {
                var d = deltas[c];
                if (d.sqrMagnitude < 1e-12f) continue;
                Undo.RecordObject(c.transform, undo);
                c.transform.position += d;
                // back onto the wall surface (curved walls), centred in its thickness
                if (reseat) WallSnapping.Snap(c, WallSnapping.AlignRotation);
            }
            Undo.CollapseUndoOperations(group);
            CutterSystem.MarkDirty();
        }

        /// <summary>The bottom of the wall the opening cuts (or is baked / applied into), below the opening.</summary>
        static float? WallBottom(MeshCutter c)
        {
            var st = CutterSystem.GetStatus(c);
            var wall = st?.targets.Concat(st.baked).Concat(st.applied).FirstOrDefault(r => r != null);
            if (wall == null) return null;
            var mf = wall.GetComponent<MeshFilter>();
            var centre = Centre(c);
            var normal = c.transform.TransformDirection(CutterShapeBuilder.Get(c).AxisVector.ToVector3()).normalized;
            return mf != null ? PlaceOpeningsTool.WallBottomBelow(mf, centre, normal) : wall.bounds.min.y;
        }

        // ------------------------------------------------------------------ menu

        static IEnumerable<MeshCutter> SelectedCutters() =>
            Selection.gameObjects.Select(g => g.GetComponentInParent<MeshCutter>()).Where(c => c != null).Distinct();

        static MeshCutter ActiveCutter() => Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<MeshCutter>() : null;

        const string Menu = "Tools/Basic Cutter/Align Openings/";

        [MenuItem(Menu + "Bottoms", false, 3)] static void MenuBottoms() => AlignHeights(SelectedCutters(), ActiveCutter(), Edge.Bottom);
        [MenuItem(Menu + "Middles", false, 4)] static void MenuMiddles() => AlignHeights(SelectedCutters(), ActiveCutter(), Edge.Middle);
        [MenuItem(Menu + "Tops", false, 5)] static void MenuTops() => AlignHeights(SelectedCutters(), ActiveCutter(), Edge.Top);
        [MenuItem(Menu + "Distribute Evenly", false, 20)] static void MenuDistribute() => DistributeEvenly(SelectedCutters());

        [MenuItem(Menu + "Bottoms", true)] [MenuItem(Menu + "Middles", true)] [MenuItem(Menu + "Tops", true)]
        static bool CanAlign() => SelectedCutters().Count() >= 2;

        [MenuItem(Menu + "Distribute Evenly", true)]
        static bool CanDistribute() => SelectedCutters().Count() >= 3;
    }
}
