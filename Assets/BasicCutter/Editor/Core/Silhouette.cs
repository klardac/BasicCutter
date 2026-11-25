using System;
using System.Collections.Generic;

namespace BasicCutter.EditorTools
{
    /// <summary>
    /// Exact outer outline of a set of (possibly overlapping, non-closed) 2D triangles: the boundary
    /// of their union, with interior holes filled. Used to turn a window model into a cutting outline.
    /// </summary>
    internal static class Silhouette
    {
        sealed class Grid
        {
            readonly double minX, minY, cell;
            readonly int nx, ny;
            readonly List<int>[] cells;

            public Grid(double minX, double minY, double maxX, double maxY, int itemCount)
            {
                double w = Math.Max(maxX - minX, 1e-9), h = Math.Max(maxY - minY, 1e-9);
                double target = Math.Max(8, Math.Sqrt(itemCount) * 1.5);
                cell = Math.Max(w, h) / target;
                this.minX = minX; this.minY = minY;
                nx = Math.Max(1, (int)Math.Ceiling(w / cell) + 1);
                ny = Math.Max(1, (int)Math.Ceiling(h / cell) + 1);
                cells = new List<int>[nx * ny];
            }

            int Cx(double x) => Math.Max(0, Math.Min(nx - 1, (int)Math.Floor((x - minX) / cell)));
            int Cy(double y) => Math.Max(0, Math.Min(ny - 1, (int)Math.Floor((y - minY) / cell)));

            public void Insert(int item, double x0, double y0, double x1, double y1)
            {
                for (int x = Cx(x0); x <= Cx(x1); x++)
                for (int y = Cy(y0); y <= Cy(y1); y++)
                {
                    ref var l = ref cells[y * nx + x];
                    if (l == null) l = new List<int>(4);
                    l.Add(item);
                }
            }

            public List<int> At(double x, double y) => cells[Cy(y) * nx + Cx(x)];

            public void Query(double x0, double y0, double x1, double y1, List<int> result, int[] stamp, int mark)
            {
                result.Clear();
                for (int x = Cx(x0); x <= Cx(x1); x++)
                for (int y = Cy(y0); y <= Cy(y1); y++)
                {
                    var l = cells[y * nx + x];
                    if (l == null) continue;
                    foreach (int i in l)
                    {
                        if (stamp[i] == mark) continue;
                        stamp[i] = mark;
                        result.Add(i);
                    }
                }
            }
        }

        /// <summary>
        /// Returns counter-clockwise outer loops of the union of the triangles.
        /// </summary>
        /// <param name="points">2D vertex positions.</param>
        /// <param name="triangles">Index triples into <paramref name="points"/> (any winding).</param>
        public static List<List<D2>> Build(IReadOnlyList<D2> points, IReadOnlyList<int> triangles)
        {
            var result = new List<List<D2>>();
            if (points.Count == 0 || triangles.Count < 3) return result;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in points)
            {
                minX = Math.Min(minX, p.x); minY = Math.Min(minY, p.y);
                maxX = Math.Max(maxX, p.x); maxY = Math.Max(maxY, p.y);
            }
            double extent = Math.Max(maxX - minX, maxY - minY);
            if (extent <= 0) return result;
            // above float rounding of the input (about 1.2e-7 relative): parts that meet are one point
            double weldEps = extent * 1e-6;
            double sideDelta = extent * 1e-5;
            double simplifyEps = extent * 2e-6;
            double collinearTol = weldEps * 4;
            double areaEps = extent * extent * 1e-12;

            // 1. weld and orient triangles
            var welder = new PointWelder2(weldEps);
            var ids = new int[points.Count];
            for (int i = 0; i < points.Count; i++) ids[i] = welder.Add(points[i]);
            var P = welder.Points;
            var tris = new List<(int a, int b, int c)>();
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                int a = ids[triangles[t]], b = ids[triangles[t + 1]], c = ids[triangles[t + 2]];
                if (a == b || b == c || a == c) continue;
                double ar = D2.Cross(P[b] - P[a], P[c] - P[a]);
                if (Math.Abs(ar) <= areaEps) continue;
                tris.Add(ar > 0 ? (a, b, c) : (a, c, b));
            }
            if (tris.Count == 0) return result;

            // 2. drop edges that have covered area on both sides (interior edges)
            var sides = new Dictionary<(int, int), int>(); // bit 1: left covered, bit 2: right covered
            void AddSide(int p, int q, int r)
            {
                var key = p < q ? (p, q) : (q, p);
                double s = D2.Cross(P[key.Item2] - P[key.Item1], P[r] - P[key.Item1]);
                sides.TryGetValue(key, out int m);
                sides[key] = m | (s > 0 ? 1 : 2);
            }
            foreach (var (a, b, c) in tris) { AddSide(a, b, c); AddSide(b, c, a); AddSide(c, a, b); }
            var edges = new List<(int a, int b)>();
            foreach (var kv in sides)
                if (kv.Value != 3) edges.Add(kv.Key);

            // 3. split candidate edges at all mutual intersections
            var triGrid = new Grid(minX, minY, maxX, maxY, tris.Count);
            for (int i = 0; i < tris.Count; i++)
            {
                var (a, b, c) = tris[i];
                triGrid.Insert(i, Math.Min(P[a].x, Math.Min(P[b].x, P[c].x)), Math.Min(P[a].y, Math.Min(P[b].y, P[c].y)),
                    Math.Max(P[a].x, Math.Max(P[b].x, P[c].x)), Math.Max(P[a].y, Math.Max(P[b].y, P[c].y)));
            }
            var edgeGrid = new Grid(minX, minY, maxX, maxY, edges.Count);
            for (int i = 0; i < edges.Count; i++)
            {
                D2 a = P[edges[i].a], b = P[edges[i].b];
                edgeGrid.Insert(i, Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Max(a.x, b.x), Math.Max(a.y, b.y));
            }
            var splits = new List<int>[edges.Count];
            var stamp = new int[edges.Count];
            var cand = new List<int>();
            double tEps = 1e-9;
            for (int i = 0; i < edges.Count; i++)
            {
                D2 p1 = P[edges[i].a], p2 = P[edges[i].b];
                edgeGrid.Query(Math.Min(p1.x, p2.x), Math.Min(p1.y, p2.y), Math.Max(p1.x, p2.x), Math.Max(p1.y, p2.y), cand, stamp, i + 1);
                D2 r = p2 - p1;
                double rl2 = r.SqrMagnitude;
                foreach (int j in cand)
                {
                    if (j <= i) continue;
                    int qa = edges[j].a, qb = edges[j].b;
                    D2 q1 = P[qa], q2 = P[qb];
                    D2 s = q2 - q1;
                    double d = D2.Cross(r, s);
                    double sl2 = s.SqrMagnitude;
                    // on one line (within rounding): decided by distance, not by angle, so two edges that are
                    // a hair apart or a hair tilted still share their pieces. The overlap's endpoints split both.
                    double rl = Math.Sqrt(rl2);
                    if (Math.Abs(D2.Cross(r, q1 - p1)) / rl <= collinearTol && Math.Abs(D2.Cross(r, q2 - p1)) / rl <= collinearTol)
                    {
                        AddCollinear(i, edges[i].a, edges[i].b, qa, P, splits, tEps);
                        AddCollinear(i, edges[i].a, edges[i].b, qb, P, splits, tEps);
                        AddCollinear(j, qa, qb, edges[i].a, P, splits, tEps);
                        AddCollinear(j, qa, qb, edges[i].b, P, splits, tEps);
                        continue;
                    }
                    if (Math.Abs(d) <= 1e-12 * Math.Sqrt(rl2 * sl2)) continue; // parallel, apart
                    D2 qp = q1 - p1;
                    double t = D2.Cross(qp, s) / d;
                    double u = D2.Cross(qp, r) / d;
                    if (t < -tEps || t > 1 + tEps || u < -tEps || u > 1 + tEps) continue;
                    bool ti = t > tEps && t < 1 - tEps;
                    bool uj = u > tEps && u < 1 - tEps;
                    if (!ti && !uj) continue;
                    int x = welder.Add(p1 + r * t);
                    if (ti && x != edges[i].a && x != edges[i].b) (splits[i] ??= new List<int>()).Add(x);
                    if (uj && x != qa && x != qb) (splits[j] ??= new List<int>()).Add(x);
                }
            }

            // 4. keep sub-segments with covered area on exactly one side
            bool Covered(D2 p)
            {
                var l = triGrid.At(p.x, p.y);
                if (l == null) return false;
                foreach (int ti in l)
                {
                    var (a, b, c) = tris[ti];
                    D2 A = P[a], B = P[b], C = P[c];
                    if (D2.Cross(B - A, p - A) >= 0 && D2.Cross(C - B, p - B) >= 0 && D2.Cross(A - C, p - C) >= 0) return true;
                }
                return false;
            }

            var boundary = new HashSet<(int, int)>();
            var chain = new List<(double t, int id)>();
            for (int i = 0; i < edges.Count; i++)
            {
                int a = edges[i].a, b = edges[i].b;
                D2 pa = P[a], pb = P[b];
                D2 dir = pb - pa;
                double l2 = dir.SqrMagnitude;
                chain.Clear();
                chain.Add((0, a));
                if (splits[i] != null)
                    foreach (int s in splits[i]) chain.Add((D2.Dot(P[s] - pa, dir) / l2, s));
                chain.Add((1, b));
                chain.Sort((x, y) => x.t.CompareTo(y.t));
                for (int k = 0; k + 1 < chain.Count; k++)
                {
                    int u = chain[k].id, v = chain[k + 1].id;
                    if (u == v) continue;
                    D2 pu = P[u], pv = P[v];
                    D2 m = (pu + pv) * 0.5;
                    D2 nrm = (pv - pu).Normalized.PerpLeft * sideDelta;
                    bool left = Covered(m + nrm), right = Covered(m - nrm);
                    if (left == right) continue;
                    boundary.Add(left ? (u, v) : (v, u));
                }
            }

            // 5. chain into loops and keep the outermost counter-clockwise ones
            var loops = Poly2D.TraceLoops(P, new List<(int a, int b)>(boundary));
            var outers = new List<List<int>>();
            foreach (var loop in loops)
            {
                var s = Poly2D.Simplify(P, loop, simplifyEps);
                if (s == null) continue;
                double area = Poly2D.SignedArea(P, s);
                if (area > extent * extent * 1e-8) outers.Add(s);
            }
            outers.Sort((x, y) => Poly2D.SignedArea(P, y).CompareTo(Poly2D.SignedArea(P, x)));
            var kept = new List<List<int>>();
            foreach (var o in outers)
            {
                D2 a = P[o[0]], b = P[o[1]];
                D2 probe = (a + b) * 0.5 + (b - a).Normalized.PerpLeft * sideDelta;
                bool inside = false;
                foreach (var k in kept)
                    if (Poly2D.Contains(P, k, probe)) { inside = true; break; }
                if (!inside) kept.Add(o);
            }
            foreach (var k in kept)
            {
                var l = new List<D2>(k.Count);
                foreach (int i in k) l.Add(P[i]);
                result.Add(l);
            }
            return result;
        }

        static void AddCollinear(int edge, int a, int b, int p, List<D2> P, List<int>[] splits, double tEps)
        {
            if (p == a || p == b) return;
            D2 pa = P[a], d = P[b] - pa;
            double t = D2.Dot(P[p] - pa, d) / d.SqrMagnitude;
            if (t > tEps && t < 1 - tEps) (splits[edge] ??= new List<int>()).Add(p);
        }
    }
}
