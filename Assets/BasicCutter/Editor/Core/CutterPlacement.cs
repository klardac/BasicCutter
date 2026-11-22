using System;
using System.Collections.Generic;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>
    /// Decides whether a cutter really goes through a mesh (a wall rather than e.g. the floor under a
    /// door) and how deep the cutting prism has to be, by casting rays along the cut axis.
    /// </summary>
    internal static class CutterPlacement
    {
        public struct Result
        {
            public bool hit;
            public double d0, d1;      // prism depth range in cutter local units
            public double wallNear, wallFar;
            public double bottomShift; // door mode: how far the bottom edge moves along up
            public bool niche;         // the prism stops inside the wall
            public bool nicheNear;     // niche: opens on the wall face at wallNear (else at wallFar)
            public bool nicheTooDeep;  // niche deeper than the wall: cut goes through
        }

        /// <summary>
        /// Niche: limits the prism to <paramref name="depth"/> from one wall face. The open face is the
        /// one nearest to the window model, or the other one with <paramref name="flip"/>.
        /// </summary>
        public static void ApplyNiche(ref Result r, CutterOutline o, double depth, bool flip)
        {
            if (!r.hit || o.IsCustom) return;
            double centre = (o.windowDepthMin + o.windowDepthMax) * 0.5;
            // a model in the middle of the wall (or no model): the negative axis side, also under float noise
            double dNear = Math.Abs(centre - r.wallNear), dFar = Math.Abs(centre - r.wallFar);
            bool nearSide = dNear <= dFar + 1e-4 * Math.Max(1.0, r.wallFar - r.wallNear);
            if (flip) nearSide = !nearSide;
            if (depth >= r.wallFar - r.wallNear) { r.nicheTooDeep = true; return; }
            r.niche = true;
            r.nicheNear = nearSide;
            if (nearSide) r.d1 = r.wallNear + depth;
            else r.d0 = r.wallFar - depth;
        }

        /// <param name="cutterToWall">Cutter local space to wall local space.</param>
        /// <param name="doorSnap">Door mode: largest allowed bottom edge movement.</param>
        public static Result Evaluate(CutterOutline o, Matrix4x4 cutterToWall, SourceMesh wall, double margin, double doorSnap = 0)
        {
            var res = new Result();
            if (!o.IsValid) return res;

            var toWall = new DMatrix(cutterToWall);
            var toCutter = new DMatrix(cutterToWall.inverse);

            if (o.IsCustom)
            {
                // custom volumes are used as they are; the cut itself reports whether it touched the wall
                res.hit = true;
                res.d0 = o.windowDepthMin; res.d1 = o.windowDepthMax;
                return res;
            }

            // wall depth range along the axis (cutter space) to start the rays outside the wall
            double wallMin = double.MaxValue, wallMax = double.MinValue;
            var b = wall.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = new D3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                double d = toCutter.Point(corner)[o.axis];
                wallMin = Math.Min(wallMin, d); wallMax = Math.Max(wallMax, d);
            }
            double start = Math.Min(wallMin, o.windowDepthMin) - 1.0;
            D3 dirW = toWall.Vector(o.AxisVector);
            double eps = CutEngine.Epsilon(wall);

            double wMin = o.windowDepthMin, wMax = o.windowDepthMax;
            double tol = Math.Max(0.02, 0.25 * (wMax - wMin));
            double centre = (wMin + wMax) * 0.5;

            var hits = new List<SourceMesh.Hit>();
            var intervals = new List<(double start, double end)>();

            bool Cast(D2 p, out List<(double s, double e)> found)
            {
                found = new List<(double s, double e)>();
                D3 originW = toWall.Point(o.ToLocal(p.x, p.y, start));
                wall.Raycast(originW, dirW, hits);
                SourceMesh.HitsToIntervals(hits, eps, intervals);
                foreach (var iv in intervals) found.Add((start + iv.start, start + iv.end));
                return found.Count > 0;
            }

            // 1. primary rays through the interior of each loop must cross the wall around the window
            double near = double.MaxValue, far = double.MinValue;
            (double s, double e) primary = default;
            bool any = false;
            foreach (var ip in o.interiorPoints)
            {
                if (!Cast(ip, out var found)) continue;
                double bestScore = double.MaxValue;
                foreach (var iv in found)
                {
                    if (iv.e < wMin - tol || iv.s > wMax + tol) continue;
                    double score = iv.s <= centre && iv.e >= centre ? 0 : Math.Min(Math.Abs(iv.s - centre), Math.Abs(iv.e - centre));
                    if (score < bestScore) { bestScore = score; primary = iv; }
                }
                if (bestScore == double.MaxValue) continue;
                any = true;
                near = Math.Min(near, primary.s); far = Math.Max(far, primary.e);
            }
            if (!any) return res;

            // 2. rays along the outline extend the range (curved or uneven walls). For doors the bottom
            //    edge is skipped: there the rays may run through a floor that belongs to the wall mesh.
            for (int li = 0; li < o.loops.Count; li++)
            {
                var loop = o.loops[li];
                int step = Math.Max(1, loop.Count / 48);
                for (int i = 0; i < loop.Count; i += step)
                {
                    if (o.HasBottom(li) && D2.Dot(loop[i], o.up2) < o.loopBottoms[li] + doorSnap + 1e-6) continue;
                    if (!Cast(loop[i], out var found)) continue;
                    double bestOverlap = 0; (double s, double e) best = default;
                    foreach (var iv in found)
                    {
                        double ov = Math.Min(iv.e, far + tol) - Math.Max(iv.s, near - tol);
                        if (ov > bestOverlap) { bestOverlap = ov; best = iv; }
                    }
                    if (bestOverlap <= 0) continue;
                    near = Math.Min(near, best.s); far = Math.Max(far, best.e);
                }
            }

            res.hit = true;
            res.wallNear = near; res.wallFar = far;
            res.d0 = near - margin;
            res.d1 = far + margin;
            if (o.door && doorSnap > 0)
                res.bottomShift = DoorBottomShift(o, toWall, wall, eps, near, far, margin, doorSnap);
            return res;
        }

        /// <summary>
        /// Door mode: moves the bottom edge onto the floor surface when wall and floor are one mesh
        /// (prevents a groove), otherwise down to the wall's bottom (prevents a threshold strip).
        /// Only moves within <paramref name="snap"/>; returns 0 when nothing applies.
        /// </summary>
        static double DoorBottomShift(CutterOutline o, DMatrix toWall, SourceMesh wall, double eps, double near, double far, double margin, double snap)
        {
            int li = -1;
            for (int i = 0; i < o.loops.Count; i++) if (o.HasBottom(i)) { li = i; break; }
            if (li < 0) return 0;

            var loop = o.loops[li];
            var flags = o.bottomFlags[li];
            D2 up = o.up2, right = new D2(up.y, -up.x);
            double b = o.loopBottoms[li], top = double.MinValue, rMin = double.MaxValue, rMax = double.MinValue;
            for (int i = 0; i < loop.Count; i++)
            {
                top = Math.Max(top, D2.Dot(loop[i], up));
                if (!flags[i]) continue;
                double r = D2.Dot(loop[i], right);
                rMin = Math.Min(rMin, r); rMax = Math.Max(rMax, r);
            }
            double height = top - b;
            if (height <= 0 || rMax <= rMin) return 0;

            D3 down = -o.ToLocal(up.x, up.y, 0);
            D3 dirW = toWall.Vector(down);
            var hits = new List<SourceMesh.Hit>();
            var intervals = new List<(double start, double end)>();
            D3 Point(double r, double u, double depth) { D2 p = right * r + up * u; return o.ToLocal(p.x, p.y, depth); }
            void CastDown(D3 originLocal)
            {
                wall.Raycast(toWall.Point(originLocal), dirW, hits);
                SourceMesh.HitsToIntervals(hits, eps, intervals);
            }
            double[] samples = { rMin + (rMax - rMin) * 0.15, (rMin + rMax) * 0.5, rMin + (rMax - rMin) * 0.85 };

            // 1. a floor that belongs to the wall mesh: rays just outside both wall faces
            double outside = Math.Max(0.02, margin * 2);
            double startUp = b + height * 0.5;
            double floor = double.MaxValue;
            bool floorFound = false;
            foreach (double side in new[] { near - outside, far + outside })
                foreach (double r in samples)
                {
                    CastDown(Point(r, startUp, side));
                    foreach (var iv in intervals)
                    {
                        if (iv.start <= 1e-9) continue;
                        floor = Math.Min(floor, startUp - iv.start);
                        floorFound = true;
                        break;
                    }
                }
            if (floorFound && Math.Abs(floor - b) <= snap) return floor - b;

            // 2. a separate wall: extend down to its bottom inside the wall thickness
            double mid = (near + far) * 0.5;
            double startTop = top + 1;
            double deepest = 0;
            bool inside = false;
            foreach (double r in samples)
            {
                CastDown(Point(r, startTop, mid));
                foreach (var iv in intervals)
                {
                    double high = startTop - iv.start, low = startTop - iv.end;
                    if (b < low - eps || b > high) continue;
                    double depthBelow = Math.Max(0, b - low);
                    if (depthBelow > snap) return 0; // wall continues far below (sunk into the floor): leave it
                    deepest = Math.Max(deepest, depthBelow);
                    inside = true;
                    break;
                }
            }
            return inside ? -(deepest + margin) : 0;
        }

        /// <summary>The cutting volume converted to wall local space (winding kept outward).</summary>
        public static List<VolumeTri> VolumeInWallSpace(CutterOutline o, Matrix4x4 cutterToWall, double d0, double d1, double bottomShift = 0)
        {
            var src = o.BuildPrism(d0, d1, bottomShift);
            var m = new DMatrix(cutterToWall);
            bool mirrored = m.Determinant < 0;
            var r = new List<VolumeTri>(src.Count);
            foreach (var t in src)
            {
                var w = new VolumeTri
                {
                    p0 = m.Point(t.p0), p1 = m.Point(t.p1), p2 = m.Point(t.p2),
                    n0 = m.Normal(t.n0), n1 = m.Normal(t.n1), n2 = m.Normal(t.n2),
                };
                if (mirrored) { (w.p1, w.p2) = (w.p2, w.p1); (w.n1, w.n2) = (w.n2, w.n1); }
                r.Add(w);
            }
            return r;
        }
    }
}
