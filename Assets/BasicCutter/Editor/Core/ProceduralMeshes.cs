using System;
using System.Collections.Generic;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>Simple closed meshes for the demo scene and the tests.</summary>
    internal static class ProceduralMeshes
    {
        /// <summary>Closed box with per-face UVs in world units (textures tile at 1 unit).</summary>
        public static Mesh Box(Vector3 size, Vector3 center, string name = "Box")
        {
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            Vector3[] normals = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (var n in normals)
            {
                Vector3 u = Mathf.Abs(n.y) > 0.5f ? Vector3.right : Vector3.Cross(Vector3.up, n).normalized;
                Vector3 v = Vector3.Cross(n, u);
                float hu = Mathf.Abs(Vector3.Dot(u, size)) * 0.5f, hv = Mathf.Abs(Vector3.Dot(v, size)) * 0.5f, hn = Mathf.Abs(Vector3.Dot(n, size)) * 0.5f;
                int b = pos.Count;
                Vector3 c = center + n * hn;
                Vector2[] corners = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
                foreach (var k in corners)
                {
                    Vector3 p = c + u * (k.x * hu) + v * (k.y * hv);
                    pos.Add(p); nrm.Add(n);
                    uv.Add(new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)));
                }
                tri.Add(b); tri.Add(b + 1); tri.Add(b + 2);
                tri.Add(b); tri.Add(b + 2); tri.Add(b + 3);
            }
            var m = new Mesh { name = name };
            m.SetVertices(pos); m.SetNormals(nrm); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
            m.RecalculateTangents(); m.RecalculateBounds();
            return m;
        }

        /// <summary>Closed curved wall: a ring segment around the Y axis, starting at the +Z axis.</summary>
        public static Mesh CurvedWall(float radius, float thickness, float height, float angleDeg, int segments, string name = "CurvedWall")
        {
            var b = new TriBuilder();
            float r0 = radius - thickness * 0.5f, r1 = radius + thickness * 0.5f;
            float a0 = -angleDeg * 0.5f * Mathf.Deg2Rad, a1 = angleDeg * 0.5f * Mathf.Deg2Rad;
            Vector3 P(float r, float a, float y) => new Vector3(Mathf.Sin(a) * r, y, Mathf.Cos(a) * r);
            for (int i = 0; i < segments; i++)
            {
                float aa = Mathf.Lerp(a0, a1, (float)i / segments), ab = Mathf.Lerp(a0, a1, (float)(i + 1) / segments);
                float ua = aa * radius, ub = ab * radius;
                // outer (smooth)
                b.QuadSmooth(P(r1, aa, 0), P(r1, ab, 0), P(r1, ab, height), P(r1, aa, height),
                    P(1, aa, 0), P(1, ab, 0), new Vector2(ua, 0), new Vector2(ub, 0), new Vector2(ub, height), new Vector2(ua, height), true);
                // inner (smooth, facing the centre)
                b.QuadSmooth(P(r0, ab, 0), P(r0, aa, 0), P(r0, aa, height), P(r0, ab, height),
                    -P(1, ab, 0), -P(1, aa, 0), new Vector2(-ub, 0), new Vector2(-ua, 0), new Vector2(-ua, height), new Vector2(-ub, height), true);
                // top and bottom
                b.Quad(P(r0, aa, height), P(r1, aa, height), P(r1, ab, height), P(r0, ab, height), Vector3.up);
                b.Quad(P(r0, ab, 0), P(r1, ab, 0), P(r1, aa, 0), P(r0, aa, 0), Vector3.down);
            }
            // end caps
            b.Quad(P(r0, a0, 0), P(r1, a0, 0), P(r1, a0, height), P(r0, a0, height), -new Vector3(Mathf.Cos(a0), 0, -Mathf.Sin(a0)));
            b.Quad(P(r1, a1, 0), P(r0, a1, 0), P(r0, a1, height), P(r1, a1, height), new Vector3(Mathf.Cos(a1), 0, -Mathf.Sin(a1)));
            return b.ToMesh(name);
        }

        /// <summary>
        /// Closed frame between an outer and an inner loop (same vertex count, both counter-clockwise
        /// in XY), extruded along Z and centred on z = 0.
        /// </summary>
        public static Mesh Frame(List<Vector2> outer, List<Vector2> inner, float depth, string name = "Frame")
        {
            var b = new TriBuilder();
            int n = outer.Count;
            float z0 = -depth * 0.5f, z1 = depth * 0.5f;
            Vector3 V(Vector2 p, float z) => new Vector3(p.x, p.y, z);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                b.Quad(V(outer[i], z0), V(outer[j], z0), V(inner[j], z0), V(inner[i], z0), Vector3.back);
                b.Quad(V(outer[j], z1), V(outer[i], z1), V(inner[i], z1), V(inner[j], z1), Vector3.forward);
                Vector2 eo = outer[j] - outer[i];
                b.Quad(V(outer[i], z0), V(outer[i], z1), V(outer[j], z1), V(outer[j], z0), new Vector3(eo.y, -eo.x, 0));
                Vector2 ei = inner[j] - inner[i];
                b.Quad(V(inner[j], z0), V(inner[j], z1), V(inner[i], z1), V(inner[i], z0), new Vector3(-ei.y, ei.x, 0));
            }
            return b.ToMesh(name);
        }

        /// <summary>Single-sided flat panel (not closed) filling a convex loop, e.g. glass.</summary>
        public static Mesh Panel(List<Vector2> loop, float z, string name = "Glass")
        {
            var b = new TriBuilder();
            Vector2 c = Vector2.zero;
            foreach (var p in loop) c += p;
            c /= loop.Count;
            for (int i = 0; i < loop.Count; i++)
            {
                var p = loop[i]; var q = loop[(i + 1) % loop.Count];
                b.Tri(new Vector3(c.x, c.y, z), new Vector3(p.x, p.y, z), new Vector3(q.x, q.y, z), Vector3.back);
            }
            return b.ToMesh(name);
        }

        public static List<Vector2> ToVector2(List<D2> l)
        {
            var r = new List<Vector2>(l.Count);
            foreach (var p in l) r.Add(new Vector2((float)p.x, (float)p.y));
            return r;
        }

        public static List<Vector2> Rect(float w, float h, float cx = 0, float cy = 0) => new List<Vector2>
        {
            new Vector2(cx - w / 2, cy - h / 2), new Vector2(cx + w / 2, cy - h / 2),
            new Vector2(cx + w / 2, cy + h / 2), new Vector2(cx - w / 2, cy + h / 2),
        };

        public static List<Vector2> Circle(float r, int segments, float cx = 0, float cy = 0)
        {
            var l = new List<Vector2>();
            for (int i = 0; i < segments; i++)
            {
                float a = Mathf.PI * 2 * i / segments;
                l.Add(new Vector2(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r));
            }
            return l;
        }

        /// <summary>Triangle soup builder that orients every triangle towards an expected normal.</summary>
        sealed class TriBuilder
        {
            readonly List<Vector3> pos = new List<Vector3>();
            readonly List<Vector3> nrm = new List<Vector3>();
            readonly List<Vector2> uv = new List<Vector2>();
            readonly List<int> tri = new List<int>();

            public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 expected)
            {
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-14f) return;
                if (Vector3.Dot(n, expected) < 0) { var t = b; b = c; c = t; n = -n; }
                n.Normalize();
                Vector3 u = Mathf.Abs(n.y) > 0.7f ? Vector3.right : Vector3.Cross(Vector3.up, n).normalized;
                Vector3 v = Vector3.Cross(n, u);
                int i = pos.Count;
                foreach (var p in new[] { a, b, c })
                {
                    pos.Add(p); nrm.Add(n); uv.Add(new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)));
                }
                tri.Add(i); tri.Add(i + 1); tri.Add(i + 2);
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 expected)
            {
                Tri(a, b, c, expected);
                Tri(a, c, d, expected);
            }

            /// <summary>Quad a-b-c-d with smooth normals (na at a/d, nb at b/c) and explicit UVs.</summary>
            public void QuadSmooth(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 na, Vector3 nb,
                Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, bool _)
            {
                Vector3 expected = (na + nb) * 0.5f;
                bool flip = Vector3.Dot(Vector3.Cross(b - a, c - a), expected) < 0;
                int i = pos.Count;
                pos.Add(a); pos.Add(b); pos.Add(c); pos.Add(d);
                nrm.Add(na.normalized); nrm.Add(nb.normalized); nrm.Add(nb.normalized); nrm.Add(na.normalized);
                uv.Add(ua); uv.Add(ub); uv.Add(uc); uv.Add(ud);
                if (!flip) { tri.Add(i); tri.Add(i + 1); tri.Add(i + 2); tri.Add(i); tri.Add(i + 2); tri.Add(i + 3); }
                else { tri.Add(i); tri.Add(i + 2); tri.Add(i + 1); tri.Add(i); tri.Add(i + 3); tri.Add(i + 2); }
            }

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                if (pos.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.SetVertices(pos); m.SetNormals(nrm); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
                m.RecalculateTangents(); m.RecalculateBounds();
                return m;
            }
        }
    }
}
