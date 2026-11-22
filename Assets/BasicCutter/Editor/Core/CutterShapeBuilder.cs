using System;
using System.Collections.Generic;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>Triangle of a cutting volume with per-corner normals (used for the hole's inner faces).</summary>
    internal struct VolumeTri
    {
        public D3 p0, p1, p2;
        public D3 n0, n1, n2;
    }

    /// <summary>
    /// Cutting outline of a <see cref="MeshCutter"/> in its local space: 2D loops on the plane
    /// perpendicular to <see cref="axis"/>, plus the depth range of the window meshes.
    /// </summary>
    internal sealed class CutterOutline
    {
        public int axis;                       // 0 = X, 1 = Y, 2 = Z (cutter local)
        public List<List<D2>> loops = new List<List<D2>>(); // counter-clockwise in (u, v)
        public List<List<int>> capTriangles = new List<List<int>>(); // per loop, indices into the loop
        public List<D2> interiorPoints = new List<D2>(); // one per loop
        public double windowDepthMin, windowDepthMax;
        public List<VolumeTri> customVolume;    // CustomMesh shape only
        public ulong hash;
        public string error;

        // door mode: the flat bottom edge of each loop can be moved along "up" per wall
        public bool door;
        public D2 up2 = new D2(0, 1);           // unit "up" direction in (u, v)
        public List<bool[]> bottomFlags = new List<bool[]>(); // per loop: vertex lies on the bottom edge
        public List<double> loopBottoms = new List<double>();  // per loop: up coordinate of the bottom edge

        public bool HasBottom(int loop) => door && loop < bottomFlags.Count && bottomFlags[loop] != null;

        /// <summary>The loop with its bottom edge moved by <paramref name="shift"/> along up (door mode).</summary>
        public List<D2> ShiftedLoop(int li, double shift)
        {
            var loop = loops[li];
            if (shift == 0 || !HasBottom(li)) return loop;
            var flags = bottomFlags[li];
            var r = new List<D2>(loop.Count);
            for (int i = 0; i < loop.Count; i++) r.Add(flags[i] ? loop[i] + up2 * shift : loop[i]);
            return r;
        }

        public bool IsCustom => customVolume != null;
        public bool IsValid => error == null && (IsCustom ? customVolume.Count > 0 : loops.Count > 0);

        public int U => (axis + 1) % 3;
        public int V => (axis + 2) % 3;

        public D3 ToLocal(double u, double v, double depth)
        {
            var a = new double[3];
            a[axis] = depth; a[U] = u; a[V] = v;
            return new D3(a[0], a[1], a[2]);
        }

        public D3 AxisVector => ToLocal(0, 0, 1);

        public D2 Project(D3 local) => new D2(local[U], local[V]);

        /// <summary>Closed prism of all loops between two depths, in cutter local space.</summary>
        public List<VolumeTri> BuildPrism(double d0, double d1, double bottomShift = 0)
        {
            if (IsCustom) return customVolume;
            var tris = new List<VolumeTri>();
            D3 axisVec = AxisVector;
            const double smoothCos = 0.82; // ~35 degrees: arcs look round, corners stay sharp
            for (int li = 0; li < loops.Count; li++)
            {
                var loop = ShiftedLoop(li, bottomShift);
                int n = loop.Count;
                // caps
                var cap = capTriangles[li];
                for (int t = 0; t + 2 < cap.Count; t += 3)
                {
                    D2 a = loop[cap[t]], b = loop[cap[t + 1]], c = loop[cap[t + 2]];
                    // counter-clockwise in (u, v) => normal +axis
                    tris.Add(new VolumeTri { p0 = ToLocal(a.x, a.y, d1), p1 = ToLocal(b.x, b.y, d1), p2 = ToLocal(c.x, c.y, d1), n0 = axisVec, n1 = axisVec, n2 = axisVec });
                    tris.Add(new VolumeTri { p0 = ToLocal(a.x, a.y, d0), p1 = ToLocal(c.x, c.y, d0), p2 = ToLocal(b.x, b.y, d0), n0 = -axisVec, n1 = -axisVec, n2 = -axisVec });
                }
                // side normals (outward for a counter-clockwise loop: right-hand perpendicular)
                var en = new D2[n];
                for (int i = 0; i < n; i++)
                {
                    D2 d = (loop[(i + 1) % n] - loop[i]).Normalized;
                    en[i] = new D2(d.y, -d.x);
                }
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    D2 pa = loop[i], pb = loop[j];
                    D2 nPrev = en[(i + n - 1) % n], nCur = en[i], nNext = en[j];
                    D2 na = D2.Dot(nPrev, nCur) > smoothCos ? (nPrev + nCur).Normalized : nCur;
                    D2 nb = D2.Dot(nCur, nNext) > smoothCos ? (nCur + nNext).Normalized : nCur;
                    D3 NA = ToLocal(na.x, na.y, 0), NB = ToLocal(nb.x, nb.y, 0);
                    D3 a0 = ToLocal(pa.x, pa.y, d0), b0 = ToLocal(pb.x, pb.y, d0);
                    D3 a1 = ToLocal(pa.x, pa.y, d1), b1 = ToLocal(pb.x, pb.y, d1);
                    D3 expected = ToLocal(nCur.x, nCur.y, 0);
                    AddOriented(tris, a0, b0, b1, NA, NB, NB, expected);
                    AddOriented(tris, a0, b1, a1, NA, NB, NA, expected);
                }
            }
            return tris;
        }

        static void AddOriented(List<VolumeTri> list, D3 a, D3 b, D3 c, D3 na, D3 nb, D3 nc, D3 expected)
        {
            if (D3.Dot(D3.Cross(b - a, c - a), expected) >= 0)
                list.Add(new VolumeTri { p0 = a, p1 = b, p2 = c, n0 = na, n1 = nb, n2 = nc });
            else
                list.Add(new VolumeTri { p0 = a, p1 = c, p2 = b, n0 = na, n1 = nc, n2 = nb });
        }
    }

    /// <summary>Builds (and caches) <see cref="CutterOutline"/>s from <see cref="MeshCutter"/> settings.</summary>
    internal static class CutterShapeBuilder
    {
        static readonly Dictionary<int, CutterOutline> cache = new Dictionary<int, CutterOutline>();

        public static void ClearCache() => cache.Clear();

        /// <summary>Meshes under the cutter that define the window (active renderers only).</summary>
        public static List<MeshFilter> WindowMeshes(MeshCutter cutter)
        {
            var list = new List<MeshFilter>();
            foreach (var mf in cutter.GetComponentsInChildren<MeshFilter>(false))
            {
                if (mf.sharedMesh == null) continue;
                if (mf.GetComponentInParent<CutterTrim>(true) != null) continue; // generated trim, not the window
                var r = mf.GetComponent<MeshRenderer>();
                if (r == null || !r.enabled) continue;
                list.Add(mf);
            }
            return list;
        }

        public static CutterOutline Get(MeshCutter cutter)
        {
            var meshes = WindowMeshes(cutter);
            var w2l = cutter.transform.worldToLocalMatrix;

            var h = Hash64.Create();
            h.Add((int)cutter.shape); h.Add((int)cutter.axis); h.Add(cutter.padding);
            h.Add(cutter.shapeSize.x); h.Add(cutter.shapeSize.y); h.Add(cutter.shapeCenter.x); h.Add(cutter.shapeCenter.y);
            h.Add(cutter.archRise); h.Add(cutter.segments); h.Add(cutter.door);
            var srcMeshes = new List<(SourceMesh mesh, Matrix4x4 toCutter)>();
            try
            {
                foreach (var mf in meshes)
                {
                    var sm = SourceMesh.Get(mf.sharedMesh);
                    var m = w2l * mf.transform.localToWorldMatrix;
                    srcMeshes.Add((sm, m));
                    h.Add(sm.contentHash); h.Add(m);
                }
                if (cutter.shape == CutterShape.CustomMesh && cutter.customMesh != null)
                    h.Add(SourceMesh.Get(cutter.customMesh).contentHash);
            }
            catch (Exception e)
            {
                return new CutterOutline { error = e.Message, hash = h.Value };
            }

            int id = cutter.GetInstanceID();
            if (cache.TryGetValue(id, out var cached) && cached.hash == h.Value) return cached;

            CutterOutline o;
            try { o = Build(cutter, srcMeshes); }
            catch (Exception e) { o = new CutterOutline { error = "Outline failed: " + e.Message }; }
            o.hash = h.Value;
            cache[id] = o;
            return o;
        }

        static CutterOutline Build(MeshCutter cutter, List<(SourceMesh mesh, Matrix4x4 toCutter)> meshes)
        {
            var o = new CutterOutline();

            // bounds of the window meshes in cutter space
            var bmin = new D3(double.MaxValue, double.MaxValue, double.MaxValue);
            var bmax = new D3(double.MinValue, double.MinValue, double.MinValue);
            var localPos = new List<D3[]>();
            foreach (var (mesh, toCutter) in meshes)
            {
                var dm = new DMatrix(toCutter);
                var arr = new D3[mesh.positions.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    var p = dm.Point(mesh.positions[i]);
                    arr[i] = p;
                    bmin = new D3(Math.Min(bmin.x, p.x), Math.Min(bmin.y, p.y), Math.Min(bmin.z, p.z));
                    bmax = new D3(Math.Max(bmax.x, p.x), Math.Max(bmax.y, p.y), Math.Max(bmax.z, p.z));
                }
                localPos.Add(arr);
            }
            bool hasMeshes = localPos.Count > 0 && bmin.x <= bmax.x;

            switch (cutter.axis)
            {
                case CutterAxis.X: o.axis = 0; break;
                case CutterAxis.Y: o.axis = 1; break;
                case CutterAxis.Z: o.axis = 2; break;
                default:
                    if (hasMeshes)
                    {
                        D3 size = bmax - bmin;
                        o.axis = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
                    }
                    else o.axis = 2;
                    break;
            }
            o.windowDepthMin = hasMeshes ? bmin[o.axis] : 0;
            o.windowDepthMax = hasMeshes ? bmax[o.axis] : 0;

            if (cutter.shape == CutterShape.CustomMesh)
            {
                if (cutter.customMesh == null) { o.error = "Custom Mesh is not assigned."; return o; }
                var sm = SourceMesh.Get(cutter.customMesh);
                if (!sm.IsClosed(out int open)) { o.error = $"Custom Mesh is not closed ({open} open edges)."; return o; }
                o.customVolume = new List<VolumeTri>();
                for (int t = 0; t < sm.triangles.Length; t += 3)
                {
                    int a = sm.triangles[t], b = sm.triangles[t + 1], c = sm.triangles[t + 2];
                    D3 fn = sm.TriNormal(t / 3);
                    o.customVolume.Add(new VolumeTri
                    {
                        p0 = sm.positions[a], p1 = sm.positions[b], p2 = sm.positions[c],
                        n0 = sm.normals != null ? sm.normals[a] : fn,
                        n1 = sm.normals != null ? sm.normals[b] : fn,
                        n2 = sm.normals != null ? sm.normals[c] : fn,
                    });
                }
                D3 cmin = new D3(double.MaxValue, double.MaxValue, double.MaxValue), cmax = -cmin;
                foreach (var p in sm.positions)
                {
                    cmin = new D3(Math.Min(cmin.x, p.x), Math.Min(cmin.y, p.y), Math.Min(cmin.z, p.z));
                    cmax = new D3(Math.Max(cmax.x, p.x), Math.Max(cmax.y, p.y), Math.Max(cmax.z, p.z));
                }
                o.windowDepthMin = cmin[o.axis]; o.windowDepthMax = cmax[o.axis];
                return o;
            }

            List<List<D2>> loops;
            if (cutter.shape == CutterShape.Exact)
            {
                if (!hasMeshes) { o.error = "No visible meshes found under this object. Add the window model as a child or pick another shape."; return o; }
                var pts = new List<D2>();
                var tris = new List<int>();
                for (int m = 0; m < localPos.Count; m++)
                {
                    int baseIndex = pts.Count;
                    foreach (var p in localPos[m]) pts.Add(o.Project(p));
                    foreach (int i in meshes[m].mesh.triangles) tris.Add(baseIndex + i);
                }
                loops = Silhouette.Build(pts, tris);
                if (loops.Count == 0) { o.error = "The window meshes have no visible area along the cut axis. Check the Axis setting."; return o; }
            }
            else
            {
                loops = new List<List<D2>> { ParametricLoop(cutter, o) };
            }

            SetLoops(o, loops, cutter.padding, cutter.door);
            return o;
        }

        /// <summary>Normalises loops (orientation, padding) and precomputes caps and interior points.</summary>
        internal static void SetLoops(CutterOutline o, List<List<D2>> loops, double padding, bool door = false)
        {
            o.loops.Clear(); o.capTriangles.Clear(); o.interiorPoints.Clear();
            o.bottomFlags.Clear(); o.loopBottoms.Clear();
            double extent = 0;
            foreach (var l in loops)
                foreach (var p in l) extent = Math.Max(extent, Math.Max(Math.Abs(p.x), Math.Abs(p.y)));
            double eps = Math.Max(extent, 1e-3) * 1e-7;

            ShapeFrame(o.axis, out _, out D3 up3);
            o.up2 = o.Project(up3).Normalized;
            o.door = false;

            foreach (var raw in loops)
            {
                var loop = new List<D2>(raw);
                if (Poly2D.SignedArea(loop) < 0) loop.Reverse();

                // door: find the flat bottom edge (at least two vertices on the lowest line)
                bool[] flags = null;
                double bottom = 0;
                if (door)
                {
                    bottom = double.MaxValue;
                    foreach (var p in loop) bottom = Math.Min(bottom, D2.Dot(p, o.up2));
                    double tol = Math.Max(extent, 1e-3) * 1e-6;
                    flags = new bool[loop.Count];
                    int count = 0;
                    for (int i = 0; i < loop.Count; i++)
                        if (D2.Dot(loop[i], o.up2) <= bottom + tol) { flags[i] = true; count++; }
                    if (count < 2) flags = null;
                }

                if (Math.Abs(padding) > 0)
                {
                    loop = Poly2D.Offset(loop, padding);
                    if (flags != null)
                        for (int i = 0; i < loop.Count; i++)
                            if (flags[i]) loop[i] += o.up2 * (bottom - D2.Dot(loop[i], o.up2)); // no padding at the bottom
                }
                if (Poly2D.SignedArea(loop) <= 0) continue;
                var idx = new List<int>(loop.Count);
                for (int i = 0; i < loop.Count; i++) idx.Add(i);
                var cap = Poly2D.Triangulate(loop, idx, null, eps);
                if (cap.Count < 3) continue;
                // interior point: centroid of the largest cap triangle
                double best = -1; D2 interior = loop[0];
                for (int t = 0; t + 2 < cap.Count; t += 3)
                {
                    D2 a = loop[cap[t]], b = loop[cap[t + 1]], c = loop[cap[t + 2]];
                    double ar = D2.Cross(b - a, c - a);
                    if (ar > best) { best = ar; interior = (a + b + c) / 3.0; }
                }
                o.loops.Add(loop);
                o.capTriangles.Add(cap);
                o.interiorPoints.Add(interior);
                o.bottomFlags.Add(flags);
                o.loopBottoms.Add(bottom);
                if (flags != null) o.door = true;
            }
            if (o.loops.Count == 0) o.error = "The cutting outline is empty (check Padding / Shape Size).";
        }

        /// <summary>Right and up directions (cutter local) used to lay out parametric shapes.</summary>
        static void ShapeFrame(int axis, out D3 right, out D3 up)
        {
            switch (axis)
            {
                case 0: right = new D3(0, 0, 1); up = new D3(0, 1, 0); break;
                case 1: right = new D3(1, 0, 0); up = new D3(0, 0, 1); break;
                default: right = new D3(1, 0, 0); up = new D3(0, 1, 0); break;
            }
        }

        static List<D2> ParametricLoop(MeshCutter c, CutterOutline o)
        {
            double w = Math.Max(1e-4, c.shapeSize.x), hgt = Math.Max(1e-4, c.shapeSize.y);
            double cx = c.shapeCenter.x, cy = c.shapeCenter.y;
            int seg = Mathf.Clamp(c.segments, 3, 128);
            var shape = new List<D2>();
            switch (c.shape)
            {
                case CutterShape.Box:
                    shape.Add(new D2(-w / 2, -hgt / 2)); shape.Add(new D2(w / 2, -hgt / 2));
                    shape.Add(new D2(w / 2, hgt / 2)); shape.Add(new D2(-w / 2, hgt / 2));
                    break;
                case CutterShape.Arch:
                    shape.AddRange(ArchOutline(w, hgt, c.archRise, seg));
                    break;
                default: // circle / ellipse
                    for (int i = 0; i < seg; i++)
                    {
                        double a = 2 * Math.PI * i / seg;
                        shape.Add(new D2(Math.Cos(a) * w / 2, Math.Sin(a) * hgt / 2));
                    }
                    break;
            }
            ShapeFrame(o.axis, out D3 right, out D3 up);
            var loop = new List<D2>(shape.Count);
            foreach (var s in shape)
            {
                D3 p = right * (s.x + cx) + up * (s.y + cy);
                loop.Add(o.Project(p));
            }
            return loop;
        }

        /// <summary>Counter-clockwise arch outline centred on the origin (also used by the demo).</summary>
        public static List<D2> ArchOutline(double w, double h, double rise, int segments)
        {
            var r = new List<D2>();
            double s = Math.Min(Math.Max(rise, 1e-4), Math.Min(w / 2, h));
            double R = (w * w / 4 + s * s) / (2 * s);
            double springY = h / 2 - s;
            double cy = h / 2 - R;
            double a0 = Math.Atan2(springY - cy, w / 2), a1 = Math.PI - a0;
            r.Add(new D2(-w / 2, -h / 2));
            r.Add(new D2(w / 2, -h / 2));
            if (springY > -h / 2 + 1e-9) r.Add(new D2(w / 2, springY));
            for (int i = 1; i < segments; i++)
            {
                double a = a0 + (a1 - a0) * i / segments;
                r.Add(new D2(Math.Cos(a) * R, cy + Math.Sin(a) * R));
            }
            if (springY > -h / 2 + 1e-9) r.Add(new D2(-w / 2, springY));
            return r;
        }
    }
}
