using System;
using System.Collections.Generic;

namespace BasicCutter.EditorTools
{
    /// <summary>2D polygon helpers: boundary tracing, simplification, containment and triangulation.</summary>
    internal static class Poly2D
    {
        public static double SignedArea(IReadOnlyList<D2> pts, IReadOnlyList<int> loop)
        {
            double a = 0;
            for (int i = 0, n = loop.Count; i < n; i++)
            {
                D2 p = pts[loop[i]], q = pts[loop[(i + 1) % n]];
                a += p.x * q.y - q.x * p.y;
            }
            return a * 0.5;
        }

        public static double SignedArea(IReadOnlyList<D2> loop)
        {
            double a = 0;
            for (int i = 0, n = loop.Count; i < n; i++)
            {
                D2 p = loop[i], q = loop[(i + 1) % n];
                a += p.x * q.y - q.x * p.y;
            }
            return a * 0.5;
        }

        /// <summary>Point in polygon (even-odd). Points on the boundary give an arbitrary answer.</summary>
        public static bool Contains(IReadOnlyList<D2> pts, IReadOnlyList<int> loop, D2 p)
        {
            bool inside = false;
            for (int i = 0, n = loop.Count, j = n - 1; i < n; j = i++)
            {
                D2 a = pts[loop[i]], b = pts[loop[j]];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        public static double DistanceToSegment(D2 p, D2 a, D2 b, out double t)
        {
            D2 ab = b - a;
            double len2 = ab.SqrMagnitude;
            t = len2 > 0 ? D2.Dot(p - a, ab) / len2 : 0;
            double tc = Math.Max(0, Math.Min(1, t));
            return (a + ab * tc - p).Magnitude;
        }

        /// <summary>
        /// Chains directed boundary edges (interior on the left) into closed loops. Where several
        /// edges leave the same vertex, the first one clockwise from the incoming edge is used,
        /// which separates loops that only touch at a vertex.
        /// </summary>
        public static List<List<int>> TraceLoops(IReadOnlyList<D2> pts, List<(int a, int b)> edges)
        {
            var outgoing = new Dictionary<int, List<int>>(); // vertex -> edge indices
            for (int i = 0; i < edges.Count; i++)
            {
                if (!outgoing.TryGetValue(edges[i].a, out var l)) outgoing[edges[i].a] = l = new List<int>(1);
                l.Add(i);
            }
            var used = new bool[edges.Count];
            var loops = new List<List<int>>();
            for (int s = 0; s < edges.Count; s++)
            {
                if (used[s]) continue;
                var loop = new List<int>();
                int e = s;
                int guard = edges.Count + 2;
                while (guard-- > 0)
                {
                    used[e] = true;
                    loop.Add(edges[e].a);
                    int v = edges[e].b;
                    if (v == edges[s].a) break;
                    if (!outgoing.TryGetValue(v, out var outs)) { loop = null; break; }
                    D2 back = pts[edges[e].a] - pts[v];
                    double backAngle = Math.Atan2(back.y, back.x);
                    int best = -1; double bestCw = double.MaxValue;
                    foreach (int cand in outs)
                    {
                        if (used[cand]) continue;
                        D2 d = pts[edges[cand].b] - pts[v];
                        double cw = backAngle - Math.Atan2(d.y, d.x);
                        while (cw <= 1e-12) cw += 2 * Math.PI;
                        while (cw > 2 * Math.PI + 1e-12) cw -= 2 * Math.PI;
                        if (cw < bestCw) { bestCw = cw; best = cand; }
                    }
                    if (best < 0) { loop = null; break; }
                    e = best;
                }
                if (loop != null && loop.Count >= 3) loops.Add(loop);
            }
            return loops;
        }

        /// <summary>Removes duplicate, collinear and spike vertices from a loop.</summary>
        public static List<int> Simplify(IReadOnlyList<D2> pts, List<int> loop, double eps)
        {
            var l = new List<int>(loop);
            bool changed = true;
            while (changed && l.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < l.Count && l.Count >= 3; i++)
                {
                    int n = l.Count;
                    D2 a = pts[l[(i + n - 1) % n]], b = pts[l[i]], c = pts[l[(i + 1) % n]];
                    if ((b - a).SqrMagnitude <= eps * eps || l[i] == l[(i + n - 1) % n])
                    {
                        l.RemoveAt(i); i--; changed = true; continue;
                    }
                    // distance of b from line a-c
                    D2 ac = c - a;
                    double len = ac.Magnitude;
                    double dist = len > 0 ? Math.Abs(D2.Cross(ac, b - a)) / len : (b - a).Magnitude;
                    if (dist <= eps)
                    {
                        l.RemoveAt(i); i--; changed = true;
                    }
                }
            }
            return l.Count >= 3 ? l : null;
        }

        // ------------------------------------------------------------------ triangulation

        /// <summary>
        /// Triangulates a counter-clockwise outer loop with clockwise holes (point indices into
        /// <paramref name="pts"/>). Returns index triples, counter-clockwise.
        /// </summary>
        public static List<int> Triangulate(IReadOnlyList<D2> pts, List<int> outer, List<List<int>> holes, double eps)
        {
            var poly = new List<int>(outer);
            if (holes != null && holes.Count > 0)
            {
                var hs = new List<List<int>>();
                foreach (var h in holes)
                {
                    var hl = new List<int>(h);
                    if (SignedArea(pts, hl) > 0) hl.Reverse();
                    hs.Add(hl);
                }
                hs.Sort((a, b) => MaxX(pts, b).CompareTo(MaxX(pts, a)));
                foreach (var h in hs) poly = Bridge(pts, poly, h, eps);
            }
            var tris = EarClip(pts, poly, eps);
            DelaunayFlip(pts, tris, eps);
            return tris;
        }

        /// <summary>
        /// Lawson edge flips: turns the ear-clipped fan into a constrained Delaunay triangulation
        /// (no long slivers where better triangles exist). Boundary edges are never flipped.
        /// </summary>
        internal static void DelaunayFlip(IReadOnlyList<D2> pts, List<int> tris, double eps)
        {
            int triCount = tris.Count / 3;
            if (triCount < 2) return;
            double areaEps = eps * eps;
            for (int pass = 0; pass < 4 * triCount + 8; pass++)
            {
                // undirected edge -> (triangle, corner index of the edge start)
                var edges = new Dictionary<(int, int), (int t, int k)>();
                bool flipped = false;
                for (int t = 0; t < triCount && !flipped; t++)
                    for (int k = 0; k < 3 && !flipped; k++)
                    {
                        int a = tris[t * 3 + k], b = tris[t * 3 + (k + 1) % 3];
                        var key = a < b ? (a, b) : (b, a);
                        if (!edges.TryGetValue(key, out var other)) { edges[key] = (t, k); continue; }
                        // the edge is shared by triangles t (a -> b) and other.t (b -> a)
                        int t2 = other.t, k2 = other.k;
                        if (t2 == t || tris[t2 * 3 + k2] != b || tris[t2 * 3 + (k2 + 1) % 3] != a) continue;
                        int c = tris[t * 3 + (k + 2) % 3], d = tris[t2 * 3 + (k2 + 2) % 3];
                        if (c == d) continue;
                        D2 pa = pts[a], pb = pts[b], pc = pts[c], pd = pts[d];
                        // the quad a, d, b, c must stay strictly convex after the flip
                        if (D2.Cross(pd - pa, pc - pa) <= areaEps || D2.Cross(pb - pd, pc - pd) <= areaEps) continue;
                        if (!InCircle(pa, pb, pc, pd)) continue;
                        tris[t * 3] = a; tris[t * 3 + 1] = d; tris[t * 3 + 2] = c;
                        tris[t2 * 3] = d; tris[t2 * 3 + 1] = b; tris[t2 * 3 + 2] = c;
                        flipped = true;
                    }
                if (!flipped) return;
            }
        }

        /// <summary>True when d lies clearly inside the circumcircle of the counter-clockwise triangle a, b, c.</summary>
        static bool InCircle(D2 a, D2 b, D2 c, D2 d)
        {
            double adx = a.x - d.x, ady = a.y - d.y, bdx = b.x - d.x, bdy = b.y - d.y, cdx = c.x - d.x, cdy = c.y - d.y;
            double ad = adx * adx + ady * ady, bd = bdx * bdx + bdy * bdy, cd = cdx * cdx + cdy * cdy;
            double det = adx * (bdy * cd - bd * cdy) - ady * (bdx * cd - bd * cdx) + ad * (bdx * cdy - bdy * cdx);
            double scale = Math.Max(ad, Math.Max(bd, cd));
            return det > 1e-9 * scale * scale;
        }

        static double MaxX(IReadOnlyList<D2> pts, List<int> loop)
        {
            double m = double.MinValue;
            foreach (int i in loop) m = Math.Max(m, pts[i].x);
            return m;
        }

        static List<int> Bridge(IReadOnlyList<D2> pts, List<int> poly, List<int> hole, double eps)
        {
            // rightmost hole vertex
            int mi = 0;
            for (int i = 1; i < hole.Count; i++)
            {
                D2 p = pts[hole[i]], q = pts[hole[mi]];
                if (p.x > q.x || (p.x == q.x && p.y < q.y)) mi = i;
            }
            D2 m = pts[hole[mi]];

            // closest edge hit by the ray m -> +x
            int bestIdx = -1; double bestX = double.MaxValue;
            for (int i = 0; i < poly.Count; i++)
            {
                D2 a = pts[poly[i]], b = pts[poly[(i + 1) % poly.Count]];
                if ((a.y > m.y) == (b.y > m.y) && a.y != m.y && b.y != m.y) continue;
                if (a.y == b.y)
                {
                    // horizontal edge on the ray line
                    if (a.y != m.y) continue;
                    double xx = Math.Min(a.x, b.x);
                    if (xx >= m.x && xx < bestX) { bestX = xx; bestIdx = a.x < b.x ? i : (i + 1) % poly.Count; }
                    continue;
                }
                // the ray leaves the polygon's interior through an edge that has m on its inner (left)
                // side; this also picks the right one of two coincident bridge edges
                if (D2.Cross(b - a, m - a) <= 0) continue;
                double x = a.x + (m.y - a.y) * (b.x - a.x) / (b.y - a.y);
                if (x < m.x - eps || x >= bestX) continue;
                bestX = x;
                bestIdx = a.x > b.x ? i : (i + 1) % poly.Count;
                if (Math.Abs(a.y - m.y) <= eps && a.x >= m.x) bestIdx = i;
                else if (Math.Abs(b.y - m.y) <= eps && b.x >= m.x) bestIdx = (i + 1) % poly.Count;
            }
            if (bestIdx < 0)
            {
                // fall back to the closest vertex
                double bd = double.MaxValue;
                for (int i = 0; i < poly.Count; i++)
                {
                    double d = (pts[poly[i]] - m).SqrMagnitude;
                    if (d < bd) { bd = d; bestIdx = i; }
                }
            }
            else
            {
                // a reflex vertex inside triangle (m, hit, p) may block the bridge
                D2 hit = new D2(bestX, m.y);
                D2 p = pts[poly[bestIdx]];
                double bestAngle = double.MaxValue;
                int candidate = bestIdx;
                for (int i = 0; i < poly.Count; i++)
                {
                    D2 v = pts[poly[i]];
                    if (i == bestIdx || v.x < m.x) continue;
                    D2 prev = pts[poly[(i + poly.Count - 1) % poly.Count]], next = pts[poly[(i + 1) % poly.Count]];
                    bool reflex = D2.Cross(v - prev, next - v) <= 0;
                    if (!reflex) continue;
                    if (!PointInTriangle(v, m, hit, p, -eps) && !PointInTriangle(v, m, p, hit, -eps)) continue;
                    D2 d = v - m;
                    double ang = Math.Abs(Math.Atan2(d.y, d.x));
                    if (ang < bestAngle - 1e-12 || (Math.Abs(ang - bestAngle) <= 1e-12 && d.SqrMagnitude < (pts[poly[candidate]] - m).SqrMagnitude))
                    {
                        bestAngle = ang; candidate = i;
                    }
                }
                bestIdx = candidate;
            }
            bestIdx = OccurrenceFacing(pts, poly, bestIdx, m);

            var r = new List<int>(poly.Count + hole.Count + 2);
            for (int i = 0; i <= bestIdx; i++) r.Add(poly[i]);
            for (int k = 0; k <= hole.Count; k++) r.Add(hole[(mi + k) % hole.Count]);
            r.Add(poly[bestIdx]);
            for (int i = bestIdx + 1; i < poly.Count; i++) r.Add(poly[i]);
            return r;
        }

        /// <summary>
        /// A vertex that earlier bridges made appear twice: returns the occurrence whose interior
        /// wedge contains <paramref name="m"/>, so the new bridge does not cross the old one.
        /// </summary>
        static int OccurrenceFacing(IReadOnlyList<D2> pts, List<int> poly, int idx, D2 m)
        {
            int n = poly.Count, first = -1;
            for (int i = 0; i < n; i++)
            {
                if (poly[i] != poly[idx]) continue;
                if (first < 0) first = i;
                D2 v = pts[poly[i]], prev = pts[poly[(i + n - 1) % n]], next = pts[poly[(i + 1) % n]];
                bool leftOfIn = D2.Cross(v - prev, m - prev) > 0, leftOfOut = D2.Cross(next - v, m - v) > 0;
                bool convex = D2.Cross(v - prev, next - v) > 0;
                if (convex ? leftOfIn && leftOfOut : leftOfIn || leftOfOut) return i;
            }
            return idx;
        }

        static bool PointInTriangle(D2 p, D2 a, D2 b, D2 c, double tol)
        {
            double d1 = D2.Cross(b - a, p - a), d2 = D2.Cross(c - b, p - b), d3 = D2.Cross(a - c, p - c);
            return d1 >= tol && d2 >= tol && d3 >= tol;
        }

        static List<int> EarClip(IReadOnlyList<D2> pts, List<int> poly, double eps)
        {
            var tris = new List<int>();
            var v = new List<int>(poly);
            double areaEps = eps * eps;
            int guard = v.Count * v.Count + 10;
            while (v.Count > 3 && guard-- > 0)
            {
                int n = v.Count;
                int ear = -1;
                double bestDegenerate = -1;
                int degenerate = -1;
                for (int i = 0; i < n; i++)
                {
                    int ia = v[(i + n - 1) % n], ib = v[i], ic = v[(i + 1) % n];
                    D2 a = pts[ia], b = pts[ib], c = pts[ic];
                    double cr = D2.Cross(b - a, c - b);
                    if (Math.Abs(cr) <= areaEps)
                    {
                        // collinear / spike: removable without a triangle
                        if (degenerate < 0) degenerate = i;
                        continue;
                    }
                    if (cr < 0) continue; // reflex
                    bool empty = true;
                    for (int k = 0; k < n; k++)
                    {
                        int ip = v[k];
                        if (ip == ia || ip == ib || ip == ic) continue;
                        D2 p = pts[ip];
                        if (Same(p, a, eps) || Same(p, b, eps) || Same(p, c, eps)) continue;
                        if (PointInTriangle(p, a, b, c, -areaEps)) { empty = false; break; }
                    }
                    if (empty) { ear = i; break; }
                    if (cr > bestDegenerate) { bestDegenerate = cr; }
                }
                if (ear < 0 && degenerate >= 0) { v.RemoveAt(degenerate); continue; }
                if (ear < 0)
                {
                    // numerical trouble: clip the most convex corner anyway
                    double best = double.MinValue;
                    for (int i = 0; i < n; i++)
                    {
                        D2 a = pts[v[(i + n - 1) % n]], b = pts[v[i]], c = pts[v[(i + 1) % n]];
                        double cr = D2.Cross(b - a, c - b);
                        if (cr > best) { best = cr; ear = i; }
                    }
                    if (best <= 0) break;
                }
                tris.Add(v[(ear + n - 1) % n]); tris.Add(v[ear]); tris.Add(v[(ear + 1) % n]);
                v.RemoveAt(ear);
            }
            if (v.Count == 3)
            {
                D2 a = pts[v[0]], b = pts[v[1]], c = pts[v[2]];
                if (D2.Cross(b - a, c - b) > areaEps) { tris.Add(v[0]); tris.Add(v[1]); tris.Add(v[2]); }
            }
            return tris;
        }

        static bool Same(D2 a, D2 b, double eps) => (a - b).SqrMagnitude <= eps * eps;

        /// <summary>Offsets a counter-clockwise loop outward by <paramref name="d"/> (negative shrinks).</summary>
        public static List<D2> Offset(List<D2> loop, double d)
        {
            if (Math.Abs(d) < 1e-12) return new List<D2>(loop);
            int n = loop.Count;
            var r = new List<D2>(n);
            for (int i = 0; i < n; i++)
            {
                D2 prev = loop[(i + n - 1) % n], cur = loop[i], next = loop[(i + 1) % n];
                D2 e1 = (cur - prev).Normalized, e2 = (next - cur).Normalized;
                D2 n1 = new D2(e1.y, -e1.x), n2 = new D2(e2.y, -e2.x); // outward for CCW
                D2 bis = (n1 + n2);
                double bl = bis.Magnitude;
                if (bl < 1e-9) { r.Add(cur + n1 * d); continue; }
                bis /= bl;
                double cos = D2.Dot(bis, n1);
                double len = d / Math.Max(cos, 0.25);
                r.Add(cur + bis * len);
            }
            return r;
        }
    }
}
