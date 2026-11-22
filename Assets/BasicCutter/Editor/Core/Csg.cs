using System.Collections.Generic;

namespace BasicCutter.EditorTools
{
    /// <summary>Oriented plane: dot(n, p) == w.</summary>
    internal readonly struct CsgPlane
    {
        public readonly D3 n;
        public readonly double w;
        public CsgPlane(D3 n, double w) { this.n = n; this.w = w; }
        public CsgPlane Flipped => new CsgPlane(-n, -w);

        public static bool TryFromPoints(D3 a, D3 b, D3 c, out CsgPlane plane)
        {
            D3 n = D3.Cross(b - a, c - a);
            double len = n.Magnitude;
            if (len < 1e-14) { plane = default; return false; }
            n /= len;
            plane = new CsgPlane(n, D3.Dot(n, a));
            return true;
        }
    }

    /// <summary>Convex planar polygon. <see cref="tag"/> identifies the source triangle it came from.</summary>
    internal sealed class CsgPolygon
    {
        public List<D3> verts;
        public CsgPlane plane;
        public readonly int tag;

        public CsgPolygon(List<D3> verts, CsgPlane plane, int tag)
        {
            this.verts = verts; this.plane = plane; this.tag = tag;
        }

        public void Flip()
        {
            verts.Reverse();
            plane = plane.Flipped;
        }
    }

    /// <summary>
    /// BSP-tree boolean operations (after Evan Wallace's csg.js, MIT), double precision and
    /// fully iterative so large meshes cannot overflow the stack. Only positions are processed;
    /// all vertex attributes are reconstructed afterwards from the polygon's source tag.
    /// </summary>
    internal static class Csg
    {
        const int Coplanar = 0, Front = 1, Back = 2, Spanning = 3;

        sealed class Node
        {
            public bool hasPlane;
            public CsgPlane plane;
            public Node front, back;
            public List<CsgPolygon> polygons = new List<CsgPolygon>();
        }

        /// <summary>Returns solid A minus solid B. Both inputs must be closed and outward facing.</summary>
        public static List<CsgPolygon> Subtract(List<CsgPolygon> a, List<CsgPolygon> b, double eps)
        {
            var na = new Node(); Build(na, Clone(a), eps);
            var nb = new Node(); Build(nb, Clone(b), eps);
            Invert(na);
            ClipTo(na, nb, eps);
            ClipTo(nb, na, eps);
            Invert(nb);
            ClipTo(nb, na, eps);
            Invert(nb);
            Build(na, AllPolygons(nb), eps);
            Invert(na);
            return AllPolygons(na);
        }

        static List<CsgPolygon> Clone(List<CsgPolygon> src)
        {
            var r = new List<CsgPolygon>(src.Count);
            foreach (var p in src) r.Add(new CsgPolygon(new List<D3>(p.verts), p.plane, p.tag));
            return r;
        }

        static void SplitPolygon(in CsgPlane plane, CsgPolygon poly, List<CsgPolygon> coplanarFront, List<CsgPolygon> coplanarBack,
            List<CsgPolygon> front, List<CsgPolygon> back, double eps, int[] typesBuffer)
        {
            int count = poly.verts.Count;
            int[] types = typesBuffer.Length >= count ? typesBuffer : new int[count];
            int polygonType = 0;
            for (int i = 0; i < count; i++)
            {
                double t = D3.Dot(plane.n, poly.verts[i]) - plane.w;
                int type = t < -eps ? Back : t > eps ? Front : Coplanar;
                polygonType |= type;
                types[i] = type;
            }

            switch (polygonType)
            {
                case Coplanar:
                    (D3.Dot(plane.n, poly.plane.n) > 0 ? coplanarFront : coplanarBack).Add(poly);
                    break;
                case Front:
                    front.Add(poly);
                    break;
                case Back:
                    back.Add(poly);
                    break;
                default:
                {
                    var f = new List<D3>(count + 1);
                    var b = new List<D3>(count + 1);
                    for (int i = 0; i < count; i++)
                    {
                        int j = (i + 1) % count;
                        int ti = types[i], tj = types[j];
                        D3 vi = poly.verts[i], vj = poly.verts[j];
                        if (ti != Back) f.Add(vi);
                        if (ti != Front) b.Add(vi);
                        if ((ti | tj) == Spanning)
                        {
                            double denom = D3.Dot(plane.n, vj - vi);
                            double t = (plane.w - D3.Dot(plane.n, vi)) / denom;
                            t = t < 0 ? 0 : t > 1 ? 1 : t;
                            D3 v = D3.Lerp(vi, vj, t);
                            f.Add(v);
                            b.Add(v);
                        }
                    }
                    if (f.Count >= 3) front.Add(new CsgPolygon(f, poly.plane, poly.tag));
                    if (b.Count >= 3) back.Add(new CsgPolygon(b, poly.plane, poly.tag));
                    break;
                }
            }
        }

        static void Build(Node root, List<CsgPolygon> polygons, double eps)
        {
            var stack = new Stack<(Node, List<CsgPolygon>)>();
            stack.Push((root, polygons));
            var types = new int[64];
            while (stack.Count > 0)
            {
                var (node, list) = stack.Pop();
                if (list.Count == 0) continue;
                if (!node.hasPlane) { node.plane = list[0].plane; node.hasPlane = true; }
                var front = new List<CsgPolygon>();
                var back = new List<CsgPolygon>();
                foreach (var p in list)
                    SplitPolygon(node.plane, p, node.polygons, node.polygons, front, back, eps, types);
                if (front.Count > 0) { if (node.front == null) node.front = new Node(); stack.Push((node.front, front)); }
                if (back.Count > 0) { if (node.back == null) node.back = new Node(); stack.Push((node.back, back)); }
            }
        }

        static void ForEachNode(Node root, System.Action<Node> action)
        {
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                action(n);
                if (n.front != null) stack.Push(n.front);
                if (n.back != null) stack.Push(n.back);
            }
        }

        static void Invert(Node root)
        {
            ForEachNode(root, n =>
            {
                foreach (var p in n.polygons) p.Flip();
                if (n.hasPlane) n.plane = n.plane.Flipped;
                var t = n.front; n.front = n.back; n.back = t;
            });
        }

        /// <summary>Removes the parts of <paramref name="polygons"/> that are inside the solid of <paramref name="bsp"/>.</summary>
        static List<CsgPolygon> ClipPolygons(Node bsp, List<CsgPolygon> polygons, double eps)
        {
            var result = new List<CsgPolygon>();
            if (!bsp.hasPlane) { result.AddRange(polygons); return result; }
            var stack = new Stack<(Node, CsgPolygon)>();
            foreach (var p in polygons) stack.Push((bsp, p));
            var front = new List<CsgPolygon>();
            var back = new List<CsgPolygon>();
            var types = new int[64];
            while (stack.Count > 0)
            {
                var (node, poly) = stack.Pop();
                front.Clear(); back.Clear();
                SplitPolygon(node.plane, poly, front, back, front, back, eps, types);
                foreach (var f in front)
                {
                    if (node.front != null && node.front.hasPlane) stack.Push((node.front, f));
                    else result.Add(f);
                }
                if (node.back != null && node.back.hasPlane)
                    foreach (var b in back) stack.Push((node.back, b));
                // no back child: these fragments are inside the solid and are dropped
            }
            return result;
        }

        static void ClipTo(Node target, Node bsp, double eps)
        {
            ForEachNode(target, n => n.polygons = ClipPolygons(bsp, n.polygons, eps));
        }

        static List<CsgPolygon> AllPolygons(Node root)
        {
            var r = new List<CsgPolygon>();
            ForEachNode(root, n => r.AddRange(n.polygons));
            return r;
        }
    }
}
