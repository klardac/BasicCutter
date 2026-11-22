using System;
using System.Collections.Generic;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>Double precision 3D vector used by the geometry kernel.</summary>
    internal readonly struct D3
    {
        public readonly double x, y, z;
        public D3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        public D3(Vector3 v) { x = v.x; y = v.y; z = v.z; }

        public static readonly D3 Zero = new D3(0, 0, 0);
        public static D3 operator +(D3 a, D3 b) => new D3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static D3 operator -(D3 a, D3 b) => new D3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static D3 operator -(D3 a) => new D3(-a.x, -a.y, -a.z);
        public static D3 operator *(D3 a, double s) => new D3(a.x * s, a.y * s, a.z * s);
        public static D3 operator *(double s, D3 a) => new D3(a.x * s, a.y * s, a.z * s);
        public static D3 operator /(D3 a, double s) => new D3(a.x / s, a.y / s, a.z / s);
        public static double Dot(D3 a, D3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static D3 Cross(D3 a, D3 b) => new D3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static D3 Lerp(D3 a, D3 b, double t) => new D3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        public static D3 Min(D3 a, D3 b) => new D3(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z));
        public static D3 Max(D3 a, D3 b) => new D3(Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z));
        public double SqrMagnitude => x * x + y * y + z * z;
        public double Magnitude => Math.Sqrt(SqrMagnitude);
        public D3 Normalized { get { double m = Magnitude; return m > 1e-300 ? this / m : Zero; } }
        public Vector3 ToVector3() => new Vector3((float)x, (float)y, (float)z);
        public double this[int i] => i == 0 ? x : i == 1 ? y : z;
        public override string ToString() => $"({x:0.#####}, {y:0.#####}, {z:0.#####})";

        /// <summary>Any unit vector perpendicular to <paramref name="n"/>.</summary>
        public static D3 AnyPerpendicular(D3 n)
        {
            D3 a = Math.Abs(n.x) < 0.6 ? new D3(1, 0, 0) : new D3(0, 1, 0);
            return Cross(n, a).Normalized;
        }
    }

    /// <summary>Double precision 2D vector.</summary>
    internal readonly struct D2
    {
        public readonly double x, y;
        public D2(double x, double y) { this.x = x; this.y = y; }
        public static D2 operator +(D2 a, D2 b) => new D2(a.x + b.x, a.y + b.y);
        public static D2 operator -(D2 a, D2 b) => new D2(a.x - b.x, a.y - b.y);
        public static D2 operator *(D2 a, double s) => new D2(a.x * s, a.y * s);
        public static D2 operator /(D2 a, double s) => new D2(a.x / s, a.y / s);
        public static double Dot(D2 a, D2 b) => a.x * b.x + a.y * b.y;
        public static double Cross(D2 a, D2 b) => a.x * b.y - a.y * b.x;
        public static D2 Lerp(D2 a, D2 b, double t) => new D2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
        public double SqrMagnitude => x * x + y * y;
        public double Magnitude => Math.Sqrt(SqrMagnitude);
        public D2 Normalized { get { double m = Magnitude; return m > 1e-300 ? this / m : this; } }
        /// <summary>Left-hand perpendicular (rotated +90 degrees).</summary>
        public D2 PerpLeft => new D2(-y, x);
        public override string ToString() => $"({x:0.#####}, {y:0.#####})";
    }

    /// <summary>Affine 4x4 transform in double precision (row-major 3x4).</summary>
    internal readonly struct DMatrix
    {
        readonly double m00, m01, m02, m03, m10, m11, m12, m13, m20, m21, m22, m23;
        readonly double n00, n01, n02, n10, n11, n12, n20, n21, n22; // inverse-transpose of the 3x3 part

        public DMatrix(Matrix4x4 m)
        {
            m00 = m.m00; m01 = m.m01; m02 = m.m02; m03 = m.m03;
            m10 = m.m10; m11 = m.m11; m12 = m.m12; m13 = m.m13;
            m20 = m.m20; m21 = m.m21; m22 = m.m22; m23 = m.m23;
            // cofactor matrix == det * inverse-transpose; scale is irrelevant for normals
            n00 = m11 * m22 - m12 * m21; n01 = m12 * m20 - m10 * m22; n02 = m10 * m21 - m11 * m20;
            n10 = m02 * m21 - m01 * m22; n11 = m00 * m22 - m02 * m20; n12 = m01 * m20 - m00 * m21;
            n20 = m01 * m12 - m02 * m11; n21 = m02 * m10 - m00 * m12; n22 = m00 * m11 - m01 * m10;
            double det = m00 * n00 + m01 * n01 + m02 * n02;
            if (det < 0) { n00 = -n00; n01 = -n01; n02 = -n02; n10 = -n10; n11 = -n11; n12 = -n12; n20 = -n20; n21 = -n21; n22 = -n22; }
            Determinant = det;
        }

        public double Determinant { get; }

        public D3 Point(D3 p) => new D3(
            m00 * p.x + m01 * p.y + m02 * p.z + m03,
            m10 * p.x + m11 * p.y + m12 * p.z + m13,
            m20 * p.x + m21 * p.y + m22 * p.z + m23);

        public D3 Vector(D3 v) => new D3(
            m00 * v.x + m01 * v.y + m02 * v.z,
            m10 * v.x + m11 * v.y + m12 * v.z,
            m20 * v.x + m21 * v.y + m22 * v.z);

        public D3 Normal(D3 n) => new D3(
            n00 * n.x + n01 * n.y + n02 * n.z,
            n10 * n.x + n11 * n.y + n12 * n.z,
            n20 * n.x + n21 * n.y + n22 * n.z).Normalized;
    }

    /// <summary>Merges points closer than a tolerance into one index (spatial hash).</summary>
    internal sealed class PointWelder3
    {
        readonly double eps, cell;
        readonly Dictionary<(long, long, long), List<int>> cells = new Dictionary<(long, long, long), List<int>>();
        public readonly List<D3> Points = new List<D3>();

        public PointWelder3(double eps) { this.eps = eps; cell = eps * 4; }

        (long, long, long) Key(D3 p) => ((long)Math.Floor(p.x / cell), (long)Math.Floor(p.y / cell), (long)Math.Floor(p.z / cell));

        public int Find(D3 p)
        {
            var k = Key(p);
            double e2 = eps * eps;
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            for (long dz = -1; dz <= 1; dz++)
            {
                if (!cells.TryGetValue((k.Item1 + dx, k.Item2 + dy, k.Item3 + dz), out var list)) continue;
                foreach (int i in list)
                    if ((Points[i] - p).SqrMagnitude <= e2) return i;
            }
            return -1;
        }

        public int Add(D3 p)
        {
            int i = Find(p);
            if (i >= 0) return i;
            i = Points.Count;
            Points.Add(p);
            var k = Key(p);
            if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>(2);
            l.Add(i);
            return i;
        }

        /// <summary>Indices of all points whose cell lies in the given box (caller filters exactly).</summary>
        public void Query(D3 min, D3 max, List<int> result)
        {
            result.Clear();
            var a = Key(min); var b = Key(max);
            long count = (b.Item1 - a.Item1 + 1) * (b.Item2 - a.Item2 + 1) * (b.Item3 - a.Item3 + 1);
            if (count > cells.Count)
            {
                foreach (var kv in cells)
                {
                    var k = kv.Key;
                    if (k.Item1 < a.Item1 || k.Item1 > b.Item1 || k.Item2 < a.Item2 || k.Item2 > b.Item2 || k.Item3 < a.Item3 || k.Item3 > b.Item3) continue;
                    result.AddRange(kv.Value);
                }
                return;
            }
            for (long x = a.Item1; x <= b.Item1; x++)
            for (long y = a.Item2; y <= b.Item2; y++)
            for (long z = a.Item3; z <= b.Item3; z++)
                if (cells.TryGetValue((x, y, z), out var list)) result.AddRange(list);
        }
    }

    /// <summary>2D variant of <see cref="PointWelder3"/>.</summary>
    internal sealed class PointWelder2
    {
        readonly double eps, cell;
        readonly Dictionary<(long, long), List<int>> cells = new Dictionary<(long, long), List<int>>();
        public readonly List<D2> Points = new List<D2>();

        public PointWelder2(double eps) { this.eps = eps; cell = eps * 4; }

        (long, long) Key(D2 p) => ((long)Math.Floor(p.x / cell), (long)Math.Floor(p.y / cell));

        public int Add(D2 p)
        {
            var k = Key(p);
            double e2 = eps * eps;
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            {
                if (!cells.TryGetValue((k.Item1 + dx, k.Item2 + dy), out var list)) continue;
                foreach (int i in list)
                    if ((Points[i] - p).SqrMagnitude <= e2) return i;
            }
            int idx = Points.Count;
            Points.Add(p);
            if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>(2);
            l.Add(idx);
            return idx;
        }
    }

    /// <summary>Stable 64-bit FNV-1a hash (identical across editor sessions).</summary>
    internal struct Hash64
    {
        ulong h;
        public static Hash64 Create() => new Hash64 { h = 14695981039346656037UL };
        public void Add(ulong v)
        {
            for (int i = 0; i < 8; i++) { h ^= (v >> (i * 8)) & 0xFF; h *= 1099511628211UL; }
        }
        public void Add(int v) => Add((ulong)(uint)v);
        public void Add(bool v) => Add(v ? 1UL : 0UL);
        /// <summary>Adds a float rounded to the given step so tiny noise does not change the hash.</summary>
        public void Add(double v, double step = 1e-5) => Add((ulong)(long)Math.Round(v / step));
        public void Add(string s) { if (s == null) { Add(0); return; } foreach (char c in s) Add((int)c); Add(s.Length); }
        public void Add(Matrix4x4 m) { for (int i = 0; i < 16; i++) Add(m[i]); }
        public ulong Value => h;
        public override string ToString() => h.ToString("x16");
    }
}
