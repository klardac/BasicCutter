using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BasicCutter.EditorTools
{
    /// <summary>Trim (frame around the opening) parameters, in cutter local units.</summary>
    internal sealed class TrimSettings
    {
        public bool nearSide, farSide;
        public double width = 0.08, thickness = 0.03;
        public bool sill;
        public double sillDepth = 0.05, sillOverhang = 0.04;
    }

    /// <summary>Vertex streams of a generated trim mesh.</summary>
    internal sealed class TrimMeshData
    {
        public readonly List<Vector3> positions = new List<Vector3>();
        public readonly List<Vector3> normals = new List<Vector3>();
        public readonly List<Vector2> uvs = new List<Vector2>();
        public readonly List<int> triangles = new List<int>();

        public bool IsEmpty => triangles.Count == 0;

        public void WriteTo(Mesh mesh)
        {
            mesh.Clear();
            mesh.indexFormat = positions.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateTangents();
        }

        /// <summary>Adds the quad a-b-c-d, wound so that its front faces <paramref name="outward"/>.</summary>
        public void Quad(D3 a, D3 b, D3 c, D3 d, D3 na, D3 nb, D3 nc, D3 nd, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, D3 outward)
        {
            D3 n = D3.Cross(b - a, c - a) + D3.Cross(c - a, d - a);
            if (n.SqrMagnitude < 1e-24) return;
            if (D3.Dot(n, outward) < 0)
            {
                (b, d) = (d, b); (nb, nd) = (nd, nb); (ub, ud) = (ud, ub);
            }
            int i = positions.Count;
            positions.Add(a.ToVector3()); positions.Add(b.ToVector3()); positions.Add(c.ToVector3()); positions.Add(d.ToVector3());
            normals.Add(na.ToVector3()); normals.Add(nb.ToVector3()); normals.Add(nc.ToVector3()); normals.Add(nd.ToVector3());
            uvs.Add(ua); uvs.Add(ub); uvs.Add(uc); uvs.Add(ud);
            triangles.Add(i); triangles.Add(i + 1); triangles.Add(i + 2);
            triangles.Add(i); triangles.Add(i + 2); triangles.Add(i + 3);
        }

        /// <summary>Flat quad: the normal is computed from the geometry.</summary>
        public void FlatQuad(D3 a, D3 b, D3 c, D3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, D3 outward)
        {
            D3 n = (D3.Cross(b - a, c - a) + D3.Cross(c - a, d - a)).Normalized;
            if (D3.Dot(n, outward) < 0) n = -n;
            Quad(a, b, c, d, n, n, n, n, ua, ub, uc, ud, outward);
        }
    }

    /// <summary>
    /// Builds a frame around a cutter's opening that lies on the wall surface (each vertex is
    /// projected onto the wall with a ray, so it follows curved walls), and an optional sill.
    /// Pure computation: the mesh is in the cutter's local space.
    /// </summary>
    internal static class TrimBuilder
    {
        const double SmoothCos = 0.82; // ~35 degrees, same as the cutting prism

        public static TrimMeshData Build(CutterOutline o, CutterPlacement.Result place, Matrix4x4 cutterToWall, SourceMesh wall, TrimSettings s)
        {
            var data = new TrimMeshData();
            if (o == null || !o.IsValid || o.IsCustom || !place.hit) return data;

            bool near = s.nearSide, far = s.farSide;
            if (place.niche) { near &= place.nicheNear; far &= !place.nicheNear; }
            if (!near && !far) return data;

            var toWall = new DMatrix(cutterToWall);
            D3 dirW = toWall.Vector(o.AxisVector);
            double eps = CutEngine.Epsilon(wall);
            double thick = place.wallFar - place.wallNear;
            double start = Math.Min(place.wallNear, o.windowDepthMin) - thick - 1.0;
            double tol = Math.Max(0.02, 0.5 * thick);
            var hits = new List<SourceMesh.Hit>();
            var intervals = new List<(double start, double end)>();

            // depth of the wall surface (near or far face) under a point of the outline plane
            double Surface(D2 p, bool nearFace)
            {
                wall.Raycast(toWall.Point(o.ToLocal(p.x, p.y, start)), dirW, hits);
                SourceMesh.HitsToIntervals(hits, eps, intervals);
                double best = 0; (double s, double e) bestIv = default;
                foreach (var iv in intervals)
                {
                    double a = start + iv.start, b = start + iv.end;
                    double ov = Math.Min(b, place.wallFar + tol) - Math.Max(a, place.wallNear - tol);
                    if (ov > best) { best = ov; bestIv = (a, b); }
                }
                if (best <= 0) return nearFace ? place.wallNear : place.wallFar;
                return nearFace ? bestIv.s : bestIv.e;
            }

            for (int li = 0; li < o.loops.Count; li++)
            {
                var loop = o.ShiftedLoop(li, place.bottomShift);
                if (near) BuildSide(data, o, li, loop, true, s, p => Surface(p, true));
                if (far) BuildSide(data, o, li, loop, false, s, p => Surface(p, false));
            }
            return data;
        }

        static void BuildSide(TrimMeshData data, CutterOutline o, int li, List<D2> loop, bool nearFace, TrimSettings s, Func<D2, double> surface)
        {
            int n = loop.Count;
            if (n < 3) return;
            double sign = nearFace ? -1 : 1;               // outward along the cut axis
            D3 axis = o.AxisVector;
            D3 outward = axis * sign;
            double width = s.width, thickness = s.thickness;
            double embed = Math.Min(0.002, thickness * 0.25); // reach slightly into the wall: no gap
            D2 up = o.up2, right = new D2(up.y, -up.x);

            // bottom edge: doors have no trim there, a sill replaces it
            bool door = o.HasBottom(li);
            double bottom = double.MaxValue;
            foreach (var p in loop) bottom = Math.Min(bottom, D2.Dot(p, up));
            var isBottom = new bool[n];
            for (int i = 0; i < n; i++)
                isBottom[i] = door ? o.bottomFlags[li][i] : D2.Dot(loop[i], up) <= bottom + 1e-5;
            bool useSill = s.sill && !door;
            var skip = new bool[n]; // edge i -> i+1
            for (int i = 0; i < n; i++)
                skip[i] = (door || useSill) && isBottom[i] && isBottom[(i + 1) % n];

            // edge directions and outward (away from the opening) normals; loops are counter-clockwise
            var en = new D2[n];
            for (int i = 0; i < n; i++)
            {
                D2 d = (loop[(i + 1) % n] - loop[i]).Normalized;
                en[i] = new D2(d.y, -d.x);
            }
            var outer = Poly2D.Offset(loop, width);
            if (door)
                for (int i = 0; i < n; i++)
                {
                    if (!isBottom[i]) continue;
                    // square end at the floor: offset only sideways
                    int prev = (i + n - 1) % n;
                    D2 side = !skip[i] ? en[i] : !skip[prev] ? en[prev] : default;
                    if (side.x == 0 && side.y == 0) continue;
                    outer[i] = loop[i] + side * width;
                }

            var sIn = new double[n];
            var sOut = new double[n];
            for (int i = 0; i < n; i++) { sIn[i] = surface(loop[i]); sOut[i] = surface(outer[i]); }

            D3 P(D2 p, double depth) => o.ToLocal(p.x, p.y, depth);
            double Back(double surf) => surf - sign * embed;
            double Front(double surf) => surf + sign * thickness;
            Vector2 Uv(D2 p) => new Vector2((float)D2.Dot(p, right), (float)D2.Dot(p, up));

            // smoothed side normals at the vertices (arcs look round, corners stay sharp)
            D3 N(D2 v) => o.ToLocal(v.x, v.y, 0).Normalized;
            D2 VertexNormal(int i, bool atStart)
            {
                // normal of edge e at its start (vertex i) or end (vertex i+1)
                int e = i;
                int other = atStart ? (e + n - 1) % n : (e + 1) % n;
                if (skip[other] || D2.Dot(en[e], en[other]) <= SmoothCos) return en[e];
                return (en[e] + en[other]).Normalized;
            }

            double run = 0;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double len = (loop[j] - loop[i]).Magnitude;
                if (skip[i]) { run += len; continue; }
                float u0 = (float)run, u1 = (float)(run + len);
                run += len;

                // face pointing away from the wall
                data.FlatQuad(P(loop[i], Front(sIn[i])), P(loop[j], Front(sIn[j])), P(outer[j], Front(sOut[j])), P(outer[i], Front(sOut[i])),
                    Uv(loop[i]), Uv(loop[j]), Uv(outer[j]), Uv(outer[i]), outward);

                // outer side
                D3 na = N(VertexNormal(i, true)), nb = N(VertexNormal(i, false));
                float t = (float)thickness;
                data.Quad(P(outer[i], Back(sOut[i])), P(outer[j], Back(sOut[j])), P(outer[j], Front(sOut[j])), P(outer[i], Front(sOut[i])),
                    na, nb, nb, na, new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u1, t), new Vector2(u0, t), N(en[i]));

                // inner side (faces into the opening); it starts exactly at the surface so it never
                // overlaps the opening's own inner faces
                data.Quad(P(loop[i], sIn[i]), P(loop[j], sIn[j]), P(loop[j], Front(sIn[j])), P(loop[i], Front(sIn[i])),
                    -na, -nb, -nb, -na, new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u1, t), new Vector2(u0, t), -N(en[i]));

                // square ends where the trim stops (door bottom)
                int prevEdge = (i + n - 1) % n;
                if (skip[prevEdge] && door)
                    data.FlatQuad(P(loop[i], Back(sIn[i])), P(outer[i], Back(sOut[i])), P(outer[i], Front(sOut[i])), P(loop[i], Front(sIn[i])),
                        Vector2.zero, new Vector2((float)width, 0), new Vector2((float)width, t), new Vector2(0, t), -o.ToLocal(loop[j].x - loop[i].x, loop[j].y - loop[i].y, 0));
                if (skip[j] && door)
                    data.FlatQuad(P(loop[j], Back(sIn[j])), P(outer[j], Back(sOut[j])), P(outer[j], Front(sOut[j])), P(loop[j], Front(sIn[j])),
                        Vector2.zero, new Vector2((float)width, 0), new Vector2((float)width, t), new Vector2(0, t), o.ToLocal(loop[j].x - loop[i].x, loop[j].y - loop[i].y, 0));
            }

            if (useSill) BuildSill(data, o, loop, nearFace, s, surface, bottom, up, right, outward, sign, embed);
        }

        static void BuildSill(TrimMeshData data, CutterOutline o, List<D2> loop, bool nearFace, TrimSettings s, Func<D2, double> surface,
            double bottom, D2 up, D2 right, D3 outward, double sign, double embed)
        {
            double rMin = double.MaxValue, rMax = double.MinValue;
            foreach (var p in loop) { double r = D2.Dot(p, right); rMin = Math.Min(rMin, r); rMax = Math.Max(rMax, r); }
            double x0 = rMin - s.width - s.sillOverhang, x1 = rMax + s.width + s.sillOverhang;
            double y0 = bottom - s.width, y1 = bottom;
            D2 At(double x, double y) => right * x + up * y;

            // flat box: its back reaches the deepest wall point, its front the outermost one
            double deep = nearFace ? double.MinValue : double.MaxValue, outer = nearFace ? double.MaxValue : double.MinValue;
            for (int k = 0; k <= 6; k++)
                foreach (double y in new[] { y0, y1 })
                {
                    double d = surface(At(x0 + (x1 - x0) * k / 6.0, y));
                    deep = nearFace ? Math.Max(deep, d) : Math.Min(deep, d);
                    outer = nearFace ? Math.Min(outer, d) : Math.Max(outer, d);
                }
            double back = deep - sign * embed;
            double front = outer + sign * (s.thickness + s.sillDepth);
            D3 P(double x, double y, double depth) { D2 p = At(x, y); return o.ToLocal(p.x, p.y, depth); }
            D3 upV = o.ToLocal(up.x, up.y, 0), rightV = o.ToLocal(right.x, right.y, 0);
            float w = (float)(x1 - x0), h = (float)(y1 - y0), dd = (float)Math.Abs(front - back);

            data.FlatQuad(P(x0, y0, front), P(x1, y0, front), P(x1, y1, front), P(x0, y1, front),
                new Vector2((float)x0, (float)y0), new Vector2((float)x1, (float)y0), new Vector2((float)x1, (float)y1), new Vector2((float)x0, (float)y1), outward);
            data.FlatQuad(P(x0, y1, back), P(x1, y1, back), P(x1, y1, front), P(x0, y1, front),
                Vector2.zero, new Vector2(w, 0), new Vector2(w, dd), new Vector2(0, dd), upV);
            data.FlatQuad(P(x0, y0, back), P(x1, y0, back), P(x1, y0, front), P(x0, y0, front),
                Vector2.zero, new Vector2(w, 0), new Vector2(w, dd), new Vector2(0, dd), -upV);
            data.FlatQuad(P(x0, y0, back), P(x0, y1, back), P(x0, y1, front), P(x0, y0, front),
                Vector2.zero, new Vector2(h, 0), new Vector2(h, dd), new Vector2(0, dd), -rightV);
            data.FlatQuad(P(x1, y0, back), P(x1, y1, back), P(x1, y1, front), P(x1, y0, front),
                Vector2.zero, new Vector2(h, 0), new Vector2(h, dd), new Vector2(0, dd), rightV);
        }
    }
}
