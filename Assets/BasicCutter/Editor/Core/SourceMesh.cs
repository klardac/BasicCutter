using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BasicCutter.EditorTools
{
    /// <summary>CPU copy of a mesh in double precision plus helpers (ray casts, closed check, hash).</summary>
    internal sealed class SourceMesh
    {
        public const int UvChannels = 4;

        public D3[] positions;
        public D3[] normals;          // null when the mesh has none
        public bool hasTangents;
        public Color[] colors;        // null when the mesh has none
        public List<Vector2>[] uvs = new List<Vector2>[UvChannels]; // entries null when unused
        public int[] triangles;       // all triangle submeshes concatenated
        public int[] triSubmesh;      // submesh index per triangle
        public int subMeshCount;
        public Bounds bounds;
        public string name;
        public ulong contentHash;

        int vertexCountCheck, indexCountCheck;
        Bounds boundsCheck;

        public int TriangleCount => triangles.Length / 3;

        // ------------------------------------------------------------------ cache

        static readonly Dictionary<int, SourceMesh> cache = new Dictionary<int, SourceMesh>();

        /// <summary>Cached read of a mesh. Cache entries are validated against cheap mesh properties.</summary>
        public static SourceMesh Get(Mesh mesh)
        {
            if (mesh == null) return null;
            int id = mesh.GetInstanceID();
            if (cache.TryGetValue(id, out var sm) && sm.vertexCountCheck == mesh.vertexCount &&
                sm.indexCountCheck == TotalIndexCount(mesh) && sm.boundsCheck == mesh.bounds)
                return sm;
            sm = Read(mesh);
            cache[id] = sm;
            return sm;
        }

        public static void ClearCache() => cache.Clear();

        static int TotalIndexCount(Mesh m)
        {
            long c = 0;
            for (int i = 0; i < m.subMeshCount; i++) c += m.GetIndexCount(i);
            return (int)c;
        }

        public static SourceMesh Read(Mesh mesh)
        {
            var sm = new SourceMesh();
            sm.name = mesh.name;
            sm.vertexCountCheck = mesh.vertexCount;
            sm.indexCountCheck = TotalIndexCount(mesh);
            sm.boundsCheck = mesh.bounds;
            sm.bounds = mesh.bounds;

            var v = mesh.vertices;
            if (mesh.vertexCount > 0 && (v == null || v.Length == 0))
                throw new InvalidOperationException($"Mesh '{mesh.name}' is not readable. Enable Read/Write in its import settings.");
            sm.positions = new D3[v.Length];
            for (int i = 0; i < v.Length; i++) sm.positions[i] = new D3(v[i]);

            var n = mesh.normals;
            if (n != null && n.Length == v.Length)
            {
                sm.normals = new D3[n.Length];
                for (int i = 0; i < n.Length; i++) sm.normals[i] = new D3(n[i]);
            }
            sm.hasTangents = mesh.HasVertexAttribute(VertexAttribute.Tangent);
            if (mesh.HasVertexAttribute(VertexAttribute.Color)) sm.colors = mesh.colors;
            for (int c = 0; c < UvChannels; c++)
            {
                if (!mesh.HasVertexAttribute((VertexAttribute)((int)VertexAttribute.TexCoord0 + c))) continue;
                var list = new List<Vector2>();
                mesh.GetUVs(c, list);
                if (list.Count == v.Length) sm.uvs[c] = list;
            }

            sm.subMeshCount = mesh.subMeshCount;
            var tris = new List<int>();
            var sub = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var t = mesh.GetTriangles(s);
                tris.AddRange(t);
                for (int k = 0; k < t.Length / 3; k++) sub.Add(s);
            }
            sm.triangles = tris.ToArray();
            sm.triSubmesh = sub.ToArray();

            var h = Hash64.Create();
            h.Add(sm.positions.Length);
            foreach (var p in sm.positions) { h.Add(p.x, 1e-6); h.Add(p.y, 1e-6); h.Add(p.z, 1e-6); }
            foreach (int i in sm.triangles) h.Add(i);
            h.Add(sm.subMeshCount);
            sm.contentHash = h.Value;
            return sm;
        }

        public D3 TriNormal(int tri)
        {
            D3 a = positions[triangles[tri * 3]], b = positions[triangles[tri * 3 + 1]], c = positions[triangles[tri * 3 + 2]];
            return D3.Cross(b - a, c - a).Normalized;
        }

        /// <summary>Largest bounds extent, used to scale tolerances.</summary>
        public double Size => Math.Max(1e-6, Math.Max(bounds.size.x, Math.Max(bounds.size.y, bounds.size.z)));

        // ------------------------------------------------------------------ ray casting

        /// <summary>Hit along a line: parameter and whether the ray enters (true) or leaves the solid.</summary>
        public struct Hit { public double t; public bool entering; public int tri; }

        /// <summary>All intersections of the line origin + t*dir (any t) with the mesh.</summary>
        public void Raycast(D3 origin, D3 dir, List<Hit> hits)
        {
            hits.Clear();
            for (int t = 0; t < triangles.Length; t += 3)
            {
                D3 a = positions[triangles[t]], b = positions[triangles[t + 1]], c = positions[triangles[t + 2]];
                D3 e1 = b - a, e2 = c - a;
                D3 p = D3.Cross(dir, e2);
                double det = D3.Dot(e1, p);
                if (Math.Abs(det) < 1e-18) continue;
                double inv = 1.0 / det;
                D3 s = origin - a;
                double u = D3.Dot(s, p) * inv;
                if (u < -1e-9 || u > 1 + 1e-9) continue;
                D3 q = D3.Cross(s, e1);
                double v = D3.Dot(dir, q) * inv;
                if (v < -1e-9 || u + v > 1 + 1e-9) continue;
                double tt = D3.Dot(e2, q) * inv;
                D3 nrm = D3.Cross(e1, e2);
                hits.Add(new Hit { t = tt, entering = D3.Dot(nrm, dir) < 0, tri = t / 3 });
            }
            hits.Sort((x, y) => x.t.CompareTo(y.t));
        }

        /// <summary>
        /// Converts sorted hits into solid intervals [start, end] along the line. Duplicate hits on
        /// shared edges are merged.
        /// </summary>
        public static void HitsToIntervals(List<Hit> hits, double eps, List<(double start, double end)> intervals)
        {
            intervals.Clear();
            int depth = 0;
            double start = 0;
            double lastT = double.NegativeInfinity;
            bool lastEntering = false;
            foreach (var h in hits)
            {
                if (Math.Abs(h.t - lastT) <= eps && h.entering == lastEntering) continue; // edge duplicate
                lastT = h.t; lastEntering = h.entering;
                if (h.entering)
                {
                    if (depth == 0) start = h.t;
                    depth++;
                }
                else if (depth > 0)
                {
                    depth--;
                    if (depth == 0 && h.t - start > eps) intervals.Add((start, h.t));
                }
            }
        }

        // ------------------------------------------------------------------ topology

        bool? closedCached;
        int openEdgesCached;

        /// <summary>Cached <see cref="IsClosed"/>.</summary>
        public bool Closed
        {
            get
            {
                if (closedCached == null) closedCached = IsClosed(out openEdgesCached);
                return closedCached.Value;
            }
        }

        public int OpenEdges { get { _ = Closed; return openEdgesCached; } }

        /// <summary>
        /// True when every edge (vertices welded by position) is used by exactly two triangles with
        /// opposite directions, i.e. the mesh is watertight and consistently oriented.
        /// </summary>
        public bool IsClosed(out int openEdges, double weldEpsilon = -1)
        {
            var welder = new PointWelder3(weldEpsilon > 0 ? weldEpsilon : Size * 1e-6);
            var ids = new int[positions.Length];
            for (int i = 0; i < positions.Length; i++) ids[i] = welder.Add(positions[i]);
            var count = new Dictionary<(int, int), int>();
            for (int t = 0; t < triangles.Length; t += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int a = ids[triangles[t + k]], b = ids[triangles[t + (k + 1) % 3]];
                    if (a == b) continue;
                    count.TryGetValue((a, b), out int c);
                    count[(a, b)] = c + 1;
                }
            }
            openEdges = 0;
            foreach (var kv in count)
            {
                count.TryGetValue((kv.Key.Item2, kv.Key.Item1), out int back);
                if (back != kv.Value) openEdges++;
            }
            return openEdges == 0;
        }

        /// <summary>Signed volume (positive for closed, outward-facing meshes).</summary>
        public double Volume()
        {
            double v = 0;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                D3 a = positions[triangles[t]], b = positions[triangles[t + 1]], c = positions[triangles[t + 2]];
                v += D3.Dot(a, D3.Cross(b, c));
            }
            return v / 6.0;
        }
    }
}
