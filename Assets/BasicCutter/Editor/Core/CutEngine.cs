using System;
using System.Collections.Generic;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>Result of a cut: plain vertex streams ready for a <see cref="Mesh"/>.</summary>
    internal sealed class CutResult
    {
        public List<Vector3> positions = new List<Vector3>();
        public List<Vector3> normals = new List<Vector3>();
        public List<Vector2>[] uvs = new List<Vector2>[SourceMesh.UvChannels];
        public List<Color> colors;
        public List<int>[] submeshes;
        public bool[] cutterAffected;
        /// <summary>Diagnostics: the boolean had to run on the whole wall (local cut not possible or rejected).</summary>
        public bool wholeWall;
        public int AffectedCount { get { int c = 0; foreach (bool b in cutterAffected) if (b) c++; return c; } }

        public void WriteTo(Mesh mesh, SourceMesh source)
        {
            mesh.Clear();
            mesh.indexFormat = positions.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            for (int c = 0; c < uvs.Length; c++)
                if (uvs[c] != null) mesh.SetUVs(c, uvs[c]);
            if (colors != null) mesh.SetColors(colors);
            mesh.subMeshCount = submeshes.Length;
            for (int s = 0; s < submeshes.Length; s++) mesh.SetTriangles(submeshes[s], s, false);
            mesh.RecalculateBounds();
            if (source.hasTangents && uvs[0] != null) mesh.RecalculateTangents();
        }

        public int TriangleCount { get { int c = 0; foreach (var s in submeshes) c += s.Count / 3; return c; } }
    }

    /// <summary>
    /// Subtracts cutting volumes from a closed mesh. Works in the wall's local space. Wall faces keep
    /// their exact attributes (barycentric interpolation of the original triangle); the new inner
    /// faces take their normals from the cutter and material/UV mapping from the wall.
    /// </summary>
    internal static class CutEngine
    {
        struct Vtx
        {
            public D3 p, n;
            public Vector2 uv0, uv1, uv2, uv3;
            public Color color;

            public Vector2 Uv(int c) => c == 0 ? uv0 : c == 1 ? uv1 : c == 2 ? uv2 : uv3;
            public void SetUv(int c, Vector2 v) { if (c == 0) uv0 = v; else if (c == 1) uv1 = v; else if (c == 2) uv2 = v; else uv3 = v; }

            public static Vtx Lerp(in Vtx a, in Vtx b, double t)
            {
                float ft = (float)t;
                return new Vtx
                {
                    p = D3.Lerp(a.p, b.p, t),
                    n = D3.Lerp(a.n, b.n, t).Normalized,
                    uv0 = Vector2.LerpUnclamped(a.uv0, b.uv0, ft),
                    uv1 = Vector2.LerpUnclamped(a.uv1, b.uv1, ft),
                    uv2 = Vector2.LerpUnclamped(a.uv2, b.uv2, ft),
                    uv3 = Vector2.LerpUnclamped(a.uv3, b.uv3, ft),
                    color = Color.LerpUnclamped(a.color, b.color, ft),
                };
            }
        }

        public static double Epsilon(SourceMesh wall) => Math.Min(1e-3, Math.Max(1e-7, wall.Size * 2e-6));

        /// <summary>Set by tests: always run the boolean on the whole wall.</summary>
        internal static bool DisableLocalCut;

        /// <param name="wall">Closed mesh to cut (local space).</param>
        /// <param name="volumes">Closed cutting volumes, already in the wall's local space.</param>
        /// <param name="jambSubmesh">Per volume: submesh for the hole's inner faces, or -1 to use the wall's.</param>
        /// <remarks>Pure computation on <see cref="SourceMesh"/> data: safe to run on a worker thread.</remarks>
        public static CutResult Cut(SourceMesh wall, List<List<VolumeTri>> volumes, int[] jambSubmesh = null)
        {
            if (!DisableLocalCut)
            {
                var local = CutCore(wall, volumes, jambSubmesh, true);
                if (local != null) return local;
            }
            var full = CutCore(wall, volumes, jambSubmesh, false);
            full.wholeWall = true;
            return full;
        }

        /// <summary>
        /// The boolean itself. With <paramref name="localOnly"/>, only wall triangles near the volumes go
        /// through the BSP and the rest is copied as is (much faster on dense walls); returns null when
        /// the result fails a volume sanity check, so the caller can retry on the whole wall.
        /// </summary>
        static CutResult CutCore(SourceMesh wall, List<List<VolumeTri>> volumes, int[] jambSubmesh, bool localOnly)
        {
            double eps = Epsilon(wall);
            int wallTris = wall.TriangleCount;

            // ---- region of interest: generously expanded bounds of every volume
            var regions = new List<(D3 min, D3 max)>();
            foreach (var vol in volumes)
            {
                if (vol.Count == 0) continue;
                D3 mn = vol[0].p0, mx = vol[0].p0;
                foreach (var t in vol)
                {
                    mn = D3.Min(mn, D3.Min(t.p0, D3.Min(t.p1, t.p2)));
                    mx = D3.Max(mx, D3.Max(t.p0, D3.Max(t.p1, t.p2)));
                }
                D3 pad = (mx - mn) * 0.5 + new D3(eps * 16, eps * 16, eps * 16);
                regions.Add((mn - pad, mx + pad));
            }

            // ---- wall polygons (outside the region they skip the boolean)
            var polys = new List<CsgPolygon>(wallTris);
            var untouched = new List<CsgPolygon>();
            var inRegion = new bool[wallTris];
            for (int t = 0; t < wallTris; t++)
            {
                D3 a = wall.positions[wall.triangles[t * 3]], b = wall.positions[wall.triangles[t * 3 + 1]], c = wall.positions[wall.triangles[t * 3 + 2]];
                if (!CsgPlane.TryFromPoints(a, b, c, out var plane)) continue;
                var poly = new CsgPolygon(new List<D3> { a, b, c }, plane, t);
                bool near = !localOnly;
                if (!near)
                {
                    D3 mn = D3.Min(a, D3.Min(b, c)), mx = D3.Max(a, D3.Max(b, c));
                    foreach (var (rmin, rmax) in regions)
                        if (mn.x <= rmax.x && mx.x >= rmin.x && mn.y <= rmax.y && mx.y >= rmin.y && mn.z <= rmax.z && mx.z >= rmin.z) { near = true; break; }
                }
                inRegion[t] = near;
                (near ? polys : untouched).Add(poly);
            }
            if (localOnly && untouched.Count == 0) localOnly = false; // nothing to gain, and no check needed

            // ---- subtract every volume (overlapping volumes simply merge into one opening)
            var volumeTris = new List<VolumeTri>();
            var volumeStart = new int[volumes.Count + 1];
            var affected = new bool[volumes.Count];
            double removableVolume = 0;
            for (int v = 0; v < volumes.Count; v++)
            {
                volumeStart[v] = wallTris + volumeTris.Count;
                var cutPolys = new List<CsgPolygon>(volumes[v].Count);
                double vv = 0;
                foreach (var tri in volumes[v])
                {
                    int tag = wallTris + volumeTris.Count;
                    volumeTris.Add(tri);
                    vv += D3.Dot(tri.p0, D3.Cross(tri.p1, tri.p2));
                    if (!CsgPlane.TryFromPoints(tri.p0, tri.p1, tri.p2, out var plane)) continue;
                    cutPolys.Add(new CsgPolygon(new List<D3> { tri.p0, tri.p1, tri.p2 }, plane, tag));
                }
                removableVolume += Math.Abs(vv / 6.0);
                volumeStart[v + 1] = wallTris + volumeTris.Count;
                if (cutPolys.Count < 4 || polys.Count == 0) continue;
                var next = Csg.Subtract(polys, cutPolys, eps);
                bool hit = false;
                foreach (var p in next)
                    if (p.tag >= volumeStart[v] && p.tag < wallTris + volumeTris.Count) { hit = true; break; }
                affected[v] = hit;
                if (hit) polys = next; // untouched walls keep their original topology
            }

            // ---- wall triangles the cut really removed area from: only those are re-shaped, so
            // the rest of a dense wall keeps its exact topology
            var triArea = new double[wallTris];
            var fragArea = new double[wallTris];
            for (int t = 0; t < wallTris; t++)
            {
                D3 a = wall.positions[wall.triangles[t * 3]], b = wall.positions[wall.triangles[t * 3 + 1]], c = wall.positions[wall.triangles[t * 3 + 2]];
                triArea[t] = D3.Cross(b - a, c - a).Magnitude * 0.5;
            }
            foreach (var p in polys)
                if (p.tag < wallTris)
                    for (int i = 1; i + 1 < p.verts.Count; i++)
                        fragArea[p.tag] += D3.Cross(p.verts[i] - p.verts[0], p.verts[i + 1] - p.verts[0]).Magnitude * 0.5;
            var reshaped = new bool[wallTris];
            for (int t = 0; t < wallTris; t++) reshaped[t] = inRegion[t] && fragArea[t] < triArea[t] * (1 - 1e-7) - 1e-15;
            polys.AddRange(untouched);

            // ---- group fragments by flat face (coplanar neighbouring source triangles with one
            // attribute mapping) and rebuild clean triangles: a rectangular jamb becomes one quad
            var faceOf = BuildFaces(wall, reshaped, volumeTris, volumeStart, eps);
            var volumeOf = new int[volumeTris.Count];
            for (int v = 0; v < volumes.Count; v++)
                for (int t = volumeStart[v]; t < volumeStart[v + 1]; t++) volumeOf[t - wallTris] = v;
            var groups = new Dictionary<int, List<CsgPolygon>>();
            foreach (var p in polys)
            {
                int face = faceOf[p.tag];
                if (!groups.TryGetValue(face, out var l)) groups[face] = l = new List<CsgPolygon>();
                l.Add(p);
            }

            var verts = new List<Vtx>();
            var tris = new List<(int a, int b, int c, int sub)>();
            var vertexWelder = new Dictionary<VertexKey, int>();
            var uvMapper = new JambMapper(wall, eps);
            var triPts = new List<D3>();

            foreach (var kv in groups)
            {
                int tag = kv.Key;
                triPts.Clear();
                if (tag < wallTris && kv.Value.Count == 1 && kv.Value[0].verts.Count == 3 && !reshaped[tag])
                    triPts.AddRange(kv.Value[0].verts); // untouched triangle: copy it as is
                else
                    TriangulateGroup(kv.Value, eps, triPts);
                if (triPts.Count == 0) continue;

                if (tag < wallTris)
                {
                    int sub = wall.triSubmesh[tag];
                    for (int i = 0; i < triPts.Count; i += 3)
                    {
                        int a = AddVertex(WallVertex(wall, tag, triPts[i]), verts, vertexWelder);
                        int b = AddVertex(WallVertex(wall, tag, triPts[i + 1]), verts, vertexWelder);
                        int c = AddVertex(WallVertex(wall, tag, triPts[i + 2]), verts, vertexWelder);
                        if (a != b && b != c && a != c) tris.Add((a, b, c, sub));
                    }
                }
                else
                {
                    var vt = volumeTris[tag - wallTris];
                    D3 polyNormal = kv.Value[0].plane.n;
                    D3 srcNormal = D3.Cross(vt.p1 - vt.p0, vt.p2 - vt.p0).Normalized;
                    double sign = D3.Dot(polyNormal, srcNormal) < 0 ? -1 : 1;
                    D3 centroid = D3.Zero;
                    foreach (var p in triPts) centroid += p;
                    centroid /= triPts.Count;
                    var map = uvMapper.Get(tag, polyNormal, centroid);
                    int sub = jambSubmesh != null && jambSubmesh[volumeOf[tag - wallTris]] >= 0 ? jambSubmesh[volumeOf[tag - wallTris]] : map.submesh;
                    for (int i = 0; i < triPts.Count; i += 3)
                    {
                        int a = AddVertex(JambVertex(vt, sign, map, triPts[i]), verts, vertexWelder);
                        int b = AddVertex(JambVertex(vt, sign, map, triPts[i + 1]), verts, vertexWelder);
                        int c = AddVertex(JambVertex(vt, sign, map, triPts[i + 2]), verts, vertexWelder);
                        if (a != b && b != c && a != c) tris.Add((a, b, c, sub));
                    }
                }
            }

            FixTJunctions(verts, tris, eps);

            // ---- sanity check of a local cut: the volume can only shrink, and by at most the volumes
            if (localOnly)
            {
                double before = wall.Volume(), after = 0;
                foreach (var t in tris) after += D3.Dot(verts[t.a].p, D3.Cross(verts[t.b].p, verts[t.c].p));
                after /= 6.0;
                double tol = 1e-6 * Math.Abs(before) + eps * wall.Size * wall.Size;
                if (after > before + tol || after < before - removableVolume - tol) return null;
            }

            // ---- output
            var r = new CutResult { cutterAffected = affected };
            for (int c = 0; c < SourceMesh.UvChannels; c++)
                if (wall.uvs[c] != null) r.uvs[c] = new List<Vector2>(verts.Count);
            if (wall.colors != null) r.colors = new List<Color>(verts.Count);
            foreach (var v in verts)
            {
                r.positions.Add(v.p.ToVector3());
                r.normals.Add(v.n.ToVector3());
                for (int c = 0; c < SourceMesh.UvChannels; c++) r.uvs[c]?.Add(v.Uv(c));
                r.colors?.Add(v.color);
            }
            int subCount = Math.Max(1, wall.subMeshCount);
            if (jambSubmesh != null) foreach (int js in jambSubmesh) subCount = Math.Max(subCount, js + 1);
            r.submeshes = new List<int>[subCount];
            for (int s = 0; s < r.submeshes.Length; s++) r.submeshes[s] = new List<int>();
            foreach (var t in tris)
            {
                var l = r.submeshes[Mathf.Clamp(t.sub, 0, r.submeshes.Length - 1)];
                l.Add(t.a); l.Add(t.b); l.Add(t.c);
            }
            return r;
        }

        // ------------------------------------------------------------------ attributes

        static void Barycentric(D3 p, D3 a, D3 b, D3 c, out double u, out double v, out double w)
        {
            D3 v0 = b - a, v1 = c - a, v2 = p - a;
            double d00 = D3.Dot(v0, v0), d01 = D3.Dot(v0, v1), d11 = D3.Dot(v1, v1);
            double d20 = D3.Dot(v2, v0), d21 = D3.Dot(v2, v1);
            double den = d00 * d11 - d01 * d01;
            if (Math.Abs(den) < 1e-30) { u = 1; v = 0; w = 0; return; }
            v = (d11 * d20 - d01 * d21) / den;
            w = (d00 * d21 - d01 * d20) / den;
            u = 1 - v - w;
        }

        static Vtx WallVertex(SourceMesh m, int tri, D3 p)
        {
            int ia = m.triangles[tri * 3], ib = m.triangles[tri * 3 + 1], ic = m.triangles[tri * 3 + 2];
            Barycentric(p, m.positions[ia], m.positions[ib], m.positions[ic], out double u, out double v, out double w);
            var r = new Vtx { p = p };
            r.n = m.normals != null ? (m.normals[ia] * u + m.normals[ib] * v + m.normals[ic] * w).Normalized : m.TriNormal(tri);
            float fu = (float)u, fv = (float)v, fw = (float)w;
            for (int c = 0; c < SourceMesh.UvChannels; c++)
            {
                var uv = m.uvs[c];
                if (uv == null) continue;
                r.SetUv(c, uv[ia] * fu + uv[ib] * fv + uv[ic] * fw);
            }
            r.color = m.colors != null ? m.colors[ia] * fu + m.colors[ib] * fv + m.colors[ic] * fw : Color.white;
            return r;
        }

        static Vtx JambVertex(in VolumeTri t, double sign, JambMapper.Map map, D3 p)
        {
            Barycentric(p, t.p0, t.p1, t.p2, out double u, out double v, out double w);
            var r = new Vtx { p = p, n = ((t.n0 * u + t.n1 * v + t.n2 * w) * sign).Normalized, color = map.color };
            for (int c = 0; c < SourceMesh.UvChannels; c++) r.SetUv(c, map.Uv(c, p));
            return r;
        }

        /// <summary>Chooses how the wall's UV mapping continues onto the hole's inner faces.</summary>
        sealed class JambMapper
        {
            public struct Map
            {
                public int submesh;
                public Color color;
                public bool planar;
                public D3 a, b, c;              // reference wall triangle (affine extrapolation)
                public Vector2[] ua, ub, uc;    // its uvs per channel
                public D3 t1, t2; public double density;

                public Vector2 Uv(int channel, D3 p)
                {
                    if (planar)
                        return channel == 0 ? new Vector2((float)(D3.Dot(p, t1) * density), (float)(D3.Dot(p, t2) * density)) : Vector2.zero;
                    if (ua == null) return Vector2.zero;
                    Barycentric(p, a, b, c, out double u, out double v, out double w);
                    return ua[channel] * (float)u + ub[channel] * (float)v + uc[channel] * (float)w;
                }
            }

            readonly SourceMesh wall;
            readonly Dictionary<int, Map> cache = new Dictionary<int, Map>();
            readonly D3[] triNormals;
            double density = -1;

            public JambMapper(SourceMesh wall, double eps)
            {
                this.wall = wall;
                triNormals = new D3[wall.TriangleCount];
                for (int t = 0; t < triNormals.Length; t++) triNormals[t] = wall.TriNormal(t);
            }

            double Density()
            {
                if (density >= 0) return density;
                var ratios = new List<double>();
                var uv = wall.uvs[0];
                if (uv != null)
                    for (int t = 0; t < triNormals.Length; t++)
                    {
                        int ia = wall.triangles[t * 3], ib = wall.triangles[t * 3 + 1], ic = wall.triangles[t * 3 + 2];
                        double area = D3.Cross(wall.positions[ib] - wall.positions[ia], wall.positions[ic] - wall.positions[ia]).Magnitude;
                        Vector2 e1 = uv[ib] - uv[ia], e2 = uv[ic] - uv[ia];
                        double uvArea = Math.Abs(e1.x * e2.y - e1.y * e2.x);
                        if (area > 1e-12 && uvArea > 1e-12) ratios.Add(Math.Sqrt(uvArea / area));
                    }
                ratios.Sort();
                density = ratios.Count > 0 ? ratios[ratios.Count / 2] : 1.0;
                return density;
            }

            public Map Get(int tag, D3 outwardNormal, D3 centroid)
            {
                if (cache.TryGetValue(tag, out var m)) return m;
                int best = -1, nearest = -1;
                double bestScore = double.MaxValue, nearestDist = double.MaxValue;
                for (int t = 0; t < triNormals.Length; t++)
                {
                    D3 a = wall.positions[wall.triangles[t * 3]], b = wall.positions[wall.triangles[t * 3 + 1]], c = wall.positions[wall.triangles[t * 3 + 2]];
                    double dist = DistanceToTriangle(centroid, a, b, c);
                    if (dist < nearestDist) { nearestDist = dist; nearest = t; }
                    double align = D3.Dot(triNormals[t], outwardNormal);
                    if (align < 0.7) continue;
                    double score = dist * (2 - align);
                    if (score < bestScore) { bestScore = score; best = t; }
                }
                m = new Map();
                int refTri = best >= 0 ? best : nearest;
                m.submesh = refTri >= 0 ? wall.triSubmesh[refTri] : 0;
                if (refTri >= 0)
                {
                    int ia = wall.triangles[refTri * 3], ib = wall.triangles[refTri * 3 + 1], ic = wall.triangles[refTri * 3 + 2];
                    m.color = wall.colors != null ? (wall.colors[ia] + wall.colors[ib] + wall.colors[ic]) / 3f : Color.white;
                }
                else m.color = Color.white;

                if (best >= 0)
                {
                    int ia = wall.triangles[best * 3], ib = wall.triangles[best * 3 + 1], ic = wall.triangles[best * 3 + 2];
                    m.a = wall.positions[ia]; m.b = wall.positions[ib]; m.c = wall.positions[ic];
                    m.ua = new Vector2[SourceMesh.UvChannels]; m.ub = new Vector2[SourceMesh.UvChannels]; m.uc = new Vector2[SourceMesh.UvChannels];
                    for (int ch = 0; ch < SourceMesh.UvChannels; ch++)
                    {
                        var uv = wall.uvs[ch];
                        if (uv == null) continue;
                        m.ua[ch] = uv[ia]; m.ub[ch] = uv[ib]; m.uc[ch] = uv[ic];
                    }
                }
                else
                {
                    // planar projection with the wall's texel density, "up" kept vertical where possible
                    m.planar = true;
                    D3 n = outwardNormal;
                    D3 up = new D3(0, 1, 0);
                    D3 t1 = Math.Abs(D3.Dot(n, up)) < 0.9 ? D3.Cross(up, n).Normalized : D3.Cross(new D3(1, 0, 0), n).Normalized;
                    m.t1 = t1; m.t2 = D3.Cross(n, t1).Normalized;
                    m.density = Density();
                }
                cache[tag] = m;
                return m;
            }

            static double DistanceToTriangle(D3 p, D3 a, D3 b, D3 c)
            {
                // Ericson, Real-Time Collision Detection, closest point on triangle
                D3 ab = b - a, ac = c - a, ap = p - a;
                double d1 = D3.Dot(ab, ap), d2 = D3.Dot(ac, ap);
                if (d1 <= 0 && d2 <= 0) return (p - a).Magnitude;
                D3 bp = p - b;
                double d3 = D3.Dot(ab, bp), d4 = D3.Dot(ac, bp);
                if (d3 >= 0 && d4 <= d3) return (p - b).Magnitude;
                double vc = d1 * d4 - d3 * d2;
                if (vc <= 0 && d1 >= 0 && d3 <= 0) return (p - (a + ab * (d1 / (d1 - d3)))).Magnitude;
                D3 cp = p - c;
                double d5 = D3.Dot(ab, cp), d6 = D3.Dot(ac, cp);
                if (d6 >= 0 && d5 <= d6) return (p - c).Magnitude;
                double vb = d5 * d2 - d1 * d6;
                if (vb <= 0 && d2 >= 0 && d6 <= 0) return (p - (a + ac * (d2 / (d2 - d6)))).Magnitude;
                double va = d3 * d6 - d5 * d4;
                if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
                    return (p - (b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).Magnitude;
                double denom = 1.0 / (va + vb + vc);
                return (p - (a + ab * (vb * denom) + ac * (vc * denom))).Magnitude;
            }
        }

        // ------------------------------------------------------------------ faces

        /// <summary>
        /// Groups source triangles into flat faces: edge-connected, coplanar, same submesh and one
        /// affine attribute mapping (so any triangle of the face reproduces the attributes of the
        /// others exactly). Returns the representative triangle (tag) of each tag's face.
        /// </summary>
        static int[] BuildFaces(SourceMesh wall, bool[] reshaped, List<VolumeTri> volumeTris, int[] volumeStart, double eps)
        {
            int wallTris = wall.TriangleCount;
            var parent = new int[wallTris + volumeTris.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }

            void Link(int count, Func<int, int, D3> corner, Func<int, int, bool> canMerge, int offset)
            {
                var welder = new PointWelder3(eps);
                var ids = new int[count * 3];
                var edgeTri = new Dictionary<(int, int), int>();
                for (int t = 0; t < count; t++)
                {
                    for (int k = 0; k < 3; k++) ids[t * 3 + k] = welder.Add(corner(t, k));
                    for (int k = 0; k < 3; k++)
                    {
                        var e = (ids[t * 3 + k], ids[t * 3 + (k + 1) % 3]);
                        if (e.Item1 != e.Item2 && !edgeTri.ContainsKey(e)) edgeTri[e] = t;
                    }
                }
                for (int t = 0; t < count; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        if (!edgeTri.TryGetValue((ids[t * 3 + (k + 1) % 3], ids[t * 3 + k]), out int s) || s <= t) continue;
                        int rt = Find(t + offset), rs = Find(s + offset);
                        if (rt != rs && canMerge(t, s)) parent[rs] = rt;
                    }
            }

            // wall triangles
            var wallN = new D3[wallTris];
            for (int t = 0; t < wallTris; t++) wallN[t] = wall.TriNormal(t);
            Link(wallTris, (t, k) => wall.positions[wall.triangles[t * 3 + k]], (t, s) =>
            {
                if (!reshaped[t] || !reshaped[s]) return false;
                if (wall.triSubmesh[t] != wall.triSubmesh[s]) return false;
                D3 a = wall.positions[wall.triangles[t * 3]];
                if (!Coplanar(wallN[t], a, wallN[s], k => wall.positions[wall.triangles[s * 3 + k]], eps)) return false;
                for (int k = 0; k < 3; k++)
                {
                    int vi = wall.triangles[s * 3 + k];
                    if (!SameAttributes(WallVertex(wall, t, wall.positions[vi]), wall, vi)) return false;
                }
                return true;
            }, 0);

            // cutter triangles, one volume at a time
            for (int v = 0; v + 1 < volumeStart.Length; v++)
            {
                int start = volumeStart[v] - wallTris, count = volumeStart[v + 1] - volumeStart[v];
                if (count <= 0) continue;
                Link(count, (t, k) => Corner(volumeTris[start + t], k), (t, s) =>
                {
                    var vt = volumeTris[start + t];
                    var vs = volumeTris[start + s];
                    D3 nt = D3.Cross(vt.p1 - vt.p0, vt.p2 - vt.p0), ns = D3.Cross(vs.p1 - vs.p0, vs.p2 - vs.p0);
                    if (nt.Magnitude < 1e-20 || ns.Magnitude < 1e-20) return false;
                    if (!Coplanar(nt.Normalized, vt.p0, ns.Normalized, k => Corner(vs, k), eps)) return false;
                    for (int k = 0; k < 3; k++)
                    {
                        Barycentric(Corner(vs, k), vt.p0, vt.p1, vt.p2, out double a, out double b, out double c);
                        D3 n = (vt.n0 * a + vt.n1 * b + vt.n2 * c).Normalized;
                        if (D3.Dot(n, CornerNormal(vs, k).Normalized) < 1 - 1e-6) return false;
                    }
                    return true;
                }, volumeStart[v]);
            }

            var face = new int[parent.Length];
            for (int i = 0; i < parent.Length; i++) face[i] = Find(i);
            return face;
        }

        static D3 Corner(in VolumeTri t, int k) => k == 0 ? t.p0 : k == 1 ? t.p1 : t.p2;
        static D3 CornerNormal(in VolumeTri t, int k) => k == 0 ? t.n0 : k == 1 ? t.n1 : t.n2;

        static bool Coplanar(D3 nt, D3 pointOnT, D3 ns, Func<int, D3> cornerOfS, double eps)
        {
            if (D3.Dot(nt, ns) < 1 - 1e-9) return false;
            for (int k = 0; k < 3; k++)
                if (Math.Abs(D3.Dot(cornerOfS(k) - pointOnT, nt)) > eps) return false;
            return true;
        }

        static bool SameAttributes(in Vtx predicted, SourceMesh m, int vi)
        {
            if (m.normals != null && D3.Dot(predicted.n, m.normals[vi].Normalized) < 1 - 1e-6) return false;
            for (int c = 0; c < SourceMesh.UvChannels; c++)
            {
                var uv = m.uvs[c];
                if (uv == null) continue;
                Vector2 d = predicted.Uv(c) - uv[vi];
                float tol = 1e-5f * Mathf.Max(1f, uv[vi].magnitude);
                if (Mathf.Abs(d.x) > tol || Mathf.Abs(d.y) > tol) return false;
            }
            if (m.colors != null)
            {
                Color d = predicted.color - m.colors[vi];
                if (Mathf.Abs(d.r) > 1e-3f || Mathf.Abs(d.g) > 1e-3f || Mathf.Abs(d.b) > 1e-3f || Mathf.Abs(d.a) > 1e-3f) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ welding

        readonly struct VertexKey : IEquatable<VertexKey>
        {
            readonly long px, py, pz, nx, ny, nz, u, v;
            public VertexKey(in Vtx x)
            {
                const double pq = 1e6, nq = 1e4, uq = 1e5;
                px = (long)Math.Round(x.p.x * pq); py = (long)Math.Round(x.p.y * pq); pz = (long)Math.Round(x.p.z * pq);
                nx = (long)Math.Round(x.n.x * nq); ny = (long)Math.Round(x.n.y * nq); nz = (long)Math.Round(x.n.z * nq);
                u = (long)Math.Round(x.uv0.x * uq); v = (long)Math.Round(x.uv0.y * uq);
            }
            public bool Equals(VertexKey o) => px == o.px && py == o.py && pz == o.pz && nx == o.nx && ny == o.ny && nz == o.nz && u == o.u && v == o.v;
            public override bool Equals(object obj) => obj is VertexKey k && Equals(k);
            public override int GetHashCode()
            {
                unchecked
                {
                    long h = px * 73856093 ^ py * 19349663 ^ pz * 83492791 ^ nx * 2654435761 ^ ny * 40503 ^ nz * 9737333 ^ u * 1572869 ^ v * 3145739;
                    return (int)(h ^ (h >> 32));
                }
            }
        }

        static int AddVertex(Vtx v, List<Vtx> verts, Dictionary<VertexKey, int> welder)
        {
            var k = new VertexKey(v);
            if (welder.TryGetValue(k, out int i)) return i;
            i = verts.Count;
            verts.Add(v);
            welder[k] = i;
            return i;
        }

        // ------------------------------------------------------------------ fragment merge

        /// <summary>
        /// Merges the coplanar convex fragments of one source triangle into its outline (with holes)
        /// and triangulates that cleanly. Removes the slivers BSP splitting creates.
        /// </summary>
        internal static void TriangulateGroup(List<CsgPolygon> polys, double eps, List<D3> output)
        {
            D3 n = polys[0].plane.n;
            D3 u = D3.AnyPerpendicular(n);
            D3 v = D3.Cross(n, u);
            var welder = new PointWelder3(eps);
            var pts2 = new List<D2>();
            var edges = new List<(int a, int b)>();
            var ids = new List<int>();
            foreach (var poly in polys)
            {
                ids.Clear();
                foreach (var p in poly.verts)
                {
                    int id = welder.Add(p);
                    if (id == pts2.Count) pts2.Add(new D2(D3.Dot(p, u), D3.Dot(p, v)));
                    if (ids.Count == 0 || ids[ids.Count - 1] != id) ids.Add(id);
                }
                while (ids.Count > 1 && ids[0] == ids[ids.Count - 1]) ids.RemoveAt(ids.Count - 1);
                if (ids.Count < 3) continue;
                for (int i = 0; i < ids.Count; i++) edges.Add((ids[i], ids[(i + 1) % ids.Count]));
            }
            if (edges.Count < 3) return;

            // single convex polygon: fan directly (most common case for untouched faces)
            if (polys.Count == 1 && edges.Count == polys[0].verts.Count)
            {
                var only = new List<int>();
                foreach (var e in edges) only.Add(e.a);
                var simple = Poly2D.Simplify(pts2, only, eps);
                if (simple == null) return;
                for (int i = 1; i + 1 < simple.Count; i++)
                {
                    output.Add(welder.Points[simple[0]]);
                    output.Add(welder.Points[simple[i]]);
                    output.Add(welder.Points[simple[i + 1]]);
                }
                return;
            }

            // split edges at vertices lying on them (T-junctions inside the group)
            var split = new List<(int a, int b)>(edges.Count);
            var onEdge = new List<(double t, int id)>();
            for (int e = 0; e < edges.Count; e++)
            {
                var (a, b) = edges[e];
                D2 pa = pts2[a], pb = pts2[b];
                double len = (pb - pa).Magnitude;
                if (len <= eps) continue;
                onEdge.Clear();
                for (int w = 0; w < pts2.Count; w++)
                {
                    if (w == a || w == b) continue;
                    double d = Poly2D.DistanceToSegment(pts2[w], pa, pb, out double t);
                    if (d <= eps && t * len > eps && (1 - t) * len > eps) onEdge.Add((t, w));
                }
                if (onEdge.Count == 0) { split.Add((a, b)); continue; }
                onEdge.Sort((x, y) => x.t.CompareTo(y.t));
                int prev = a;
                foreach (var (_, w) in onEdge) { if (w != prev) split.Add((prev, w)); prev = w; }
                if (prev != b) split.Add((prev, b));
            }

            // cancel interior edges (present in both directions)
            var count = new Dictionary<(int, int), int>();
            foreach (var e in split)
            {
                if (count.TryGetValue((e.b, e.a), out int back) && back > 0) { count[(e.b, e.a)] = back - 1; continue; }
                count.TryGetValue(e, out int c);
                count[e] = c + 1;
            }
            var boundary = new List<(int a, int b)>();
            foreach (var kv in count)
                for (int i = 0; i < kv.Value; i++) boundary.Add(kv.Key);
            if (boundary.Count < 3) return;

            var loops = Poly2D.TraceLoops(pts2, boundary);
            var outers = new List<List<int>>();
            var holes = new List<List<int>>();
            double minArea = eps * eps;
            foreach (var l in loops)
            {
                var s = Poly2D.Simplify(pts2, l, eps);
                if (s == null) continue;
                double area = Poly2D.SignedArea(pts2, s);
                if (area > minArea) outers.Add(s);
                else if (area < -minArea) holes.Add(s);
            }
            if (outers.Count == 0) return;

            var holesOf = new List<List<int>>[outers.Count];
            foreach (var h in holes)
            {
                // a probe point just inside the hole (to the left of a reversed edge == right of the edge)
                D2 a = pts2[h[0]], b = pts2[h[1]];
                D2 probe = (a + b) * 0.5 - (b - a).Normalized.PerpLeft * (eps * 4);
                int owner = -1; double ownerArea = double.MaxValue;
                for (int o = 0; o < outers.Count; o++)
                {
                    if (!Poly2D.Contains(pts2, outers[o], probe)) continue;
                    double ar = Poly2D.SignedArea(pts2, outers[o]);
                    if (ar < ownerArea) { ownerArea = ar; owner = o; }
                }
                if (owner < 0) continue;
                (holesOf[owner] ??= new List<List<int>>()).Add(h);
            }

            for (int o = 0; o < outers.Count; o++)
            {
                var tris = Poly2D.Triangulate(pts2, outers[o], holesOf[o], eps);
                for (int i = 0; i + 2 < tris.Count; i += 3)
                {
                    output.Add(welder.Points[tris[i]]);
                    output.Add(welder.Points[tris[i + 1]]);
                    output.Add(welder.Points[tris[i + 2]]);
                }
            }
        }

        // ------------------------------------------------------------------ T-junctions

        /// <summary>Splits triangle edges that have another vertex lying on them so the mesh is watertight.</summary>
        static void FixTJunctions(List<Vtx> verts, List<(int a, int b, int c, int sub)> tris, double eps)
        {
            var welder = new PointWelder3(eps);
            var posId = new List<int>(verts.Count);
            foreach (var v in verts) posId.Add(welder.Add(v.p));
            var P = welder.Points;
            // snap every vertex onto its welded position so neighbouring faces match bit-for-bit
            for (int i = 0; i < verts.Count; i++)
            {
                var v = verts[i];
                v.p = P[posId[i]];
                verts[i] = v;
            }

            // spatial grid for the edge queries: cells about one average edge long (the welder's
            // cells are tiny and would make long edges visit thousands of empty cells)
            double edgeSum = 0;
            foreach (var t0 in tris)
                edgeSum += (P[posId[t0.a]] - P[posId[t0.b]]).Magnitude + (P[posId[t0.b]] - P[posId[t0.c]]).Magnitude + (P[posId[t0.c]] - P[posId[t0.a]]).Magnitude;
            double cell = Math.Max(eps * 8, tris.Count > 0 ? edgeSum / (tris.Count * 3) : 1);
            var grid = new Dictionary<(long, long, long), List<int>>();
            (long, long, long) Cell(D3 p) => ((long)Math.Floor(p.x / cell), (long)Math.Floor(p.y / cell), (long)Math.Floor(p.z / cell));
            for (int i = 0; i < P.Count; i++)
            {
                var k = Cell(P[i]);
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(4);
                l.Add(i);
            }
            void Query(D3 min, D3 max, List<int> result)
            {
                result.Clear();
                var a = Cell(min); var b = Cell(max);
                // very long edges: fall back to scanning all points
                if ((b.Item1 - a.Item1 + 1) * (b.Item2 - a.Item2 + 1) * (b.Item3 - a.Item3 + 1) > P.Count)
                {
                    for (int i = 0; i < P.Count; i++) result.Add(i);
                    return;
                }
                for (long x = a.Item1; x <= b.Item1; x++)
                for (long y = a.Item2; y <= b.Item2; y++)
                for (long z = a.Item3; z <= b.Item3; z++)
                    if (grid.TryGetValue((x, y, z), out var l)) result.AddRange(l);
            }

            int guard = verts.Count * 8 + tris.Count * 8 + 100000;
            // mask bit k: edge k (tv[k] -> tv[k+1]) lies on an original edge and must be checked;
            // edges created by a split run through the triangle's interior and never need it
            var work = new Stack<((int a, int b, int c, int sub) t, int mask)>();
            foreach (var t0 in tris) work.Push((t0, 7));
            tris.Clear();
            var cand = new List<int>();
            while (work.Count > 0)
            {
                var (t, mask) = work.Pop();
                bool didSplit = false;
                int[] tv = { t.a, t.b, t.c };
                for (int k = 0; k < 3 && !didSplit && guard > 0; k++)
                {
                    if ((mask & (1 << k)) == 0) continue;
                    int va = tv[k], vb = tv[(k + 1) % 3], vc = tv[(k + 2) % 3];
                    D3 pa = verts[va].p, pb = verts[vb].p;
                    D3 ab = pb - pa;
                    double len2 = ab.SqrMagnitude;
                    if (len2 <= eps * eps) continue;
                    double len = Math.Sqrt(len2);
                    var min = new D3(Math.Min(pa.x, pb.x) - eps, Math.Min(pa.y, pb.y) - eps, Math.Min(pa.z, pb.z) - eps);
                    var max = new D3(Math.Max(pa.x, pb.x) + eps, Math.Max(pa.y, pb.y) + eps, Math.Max(pa.z, pb.z) + eps);
                    Query(min, max, cand);
                    int ida = posId[va], idb = posId[vb];
                    int bestW = -1; double bestT = 0, bestScore = double.MaxValue;
                    foreach (int w in cand)
                    {
                        if (w == ida || w == idb) continue;
                        D3 pw = P[w];
                        double tt = D3.Dot(pw - pa, ab) / len2;
                        if (tt * len <= eps || (1 - tt) * len <= eps) continue;
                        double d = (pa + ab * tt - pw).Magnitude;
                        if (d > eps) continue;
                        double score = Math.Abs(tt - 0.5);
                        if (score < bestScore) { bestScore = score; bestW = w; bestT = tt; }
                    }
                    if (bestW < 0) continue;
                    var nv = Vtx.Lerp(verts[va], verts[vb], bestT);
                    nv.p = P[bestW];
                    int ni = verts.Count;
                    verts.Add(nv);
                    posId.Add(bestW);
                    bool nextChecked = (mask & (1 << ((k + 1) % 3))) != 0; // vb -> vc
                    bool prevChecked = (mask & (1 << ((k + 2) % 3))) != 0; // vc -> va
                    // (va, ni, vc): edge0 va->ni sub-edge, edge1 ni->vc interior, edge2 vc->va original
                    work.Push(((va, ni, vc, t.sub), 1 | (prevChecked ? 4 : 0)));
                    // (ni, vb, vc): edge0 ni->vb sub-edge, edge1 vb->vc original, edge2 vc->ni interior
                    work.Push(((ni, vb, vc, t.sub), 1 | (nextChecked ? 2 : 0)));
                    didSplit = true;
                    guard--;
                }
                if (!didSplit) tris.Add(t);
            }
        }
    }
}
