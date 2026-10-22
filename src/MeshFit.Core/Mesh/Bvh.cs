using System;
using System.Collections.Generic;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Mesh
{
    /// <summary>Axis-aligned bounding volume hierarchy over the triangles of a mesh (ray casting and nearest point).</summary>
    public sealed class Bvh
    {
        private struct Node
        {
            public Vec3 Min, Max;
            public int Left, Right;   // child nodes, -1 for leaf
            public int Start, Count;  // range in _tris for leaves
        }

        private readonly TriMesh _mesh;
        private readonly int[] _tris;
        private readonly List<Node> _nodes = new List<Node>();
        private const int LeafSize = 8;

        public Bvh(TriMesh mesh)
        {
            _mesh = mesh;
            _tris = new int[mesh.TriangleCount];
            for (int i = 0; i < _tris.Length; i++) _tris[i] = i;
            if (_tris.Length > 0) Build(0, _tris.Length);
        }

        private int Build(int start, int count)
        {
            var mn = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            var mx = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            var cmn = mn; var cmx = mx;
            for (int i = start; i < start + count; i++)
            {
                int t = _tris[i];
                for (int k = 0; k < 3; k++) { var p = _mesh.P(t, k); mn = Vec3.Min(mn, p); mx = Vec3.Max(mx, p); }
                var c = _mesh.Centroids[t]; cmn = Vec3.Min(cmn, c); cmx = Vec3.Max(cmx, c);
            }
            int idx = _nodes.Count;
            _nodes.Add(new Node { Min = mn, Max = mx, Left = -1, Right = -1, Start = start, Count = count });
            if (count <= LeafSize) return idx;

            var ext = cmx - cmn;
            int axis = ext.X > ext.Y ? (ext.X > ext.Z ? 0 : 2) : (ext.Y > ext.Z ? 1 : 2);
            if (ext[axis] <= 0) return idx;
            Array.Sort(_tris, start, count, Comparer<int>.Create((a, b) =>
                _mesh.Centroids[a][axis].CompareTo(_mesh.Centroids[b][axis])));
            int half = count / 2;
            int l = Build(start, half);
            int r = Build(start + half, count - half);
            var n = _nodes[idx];
            n.Left = l; n.Right = r; n.Count = 0;
            _nodes[idx] = n;
            return idx;
        }

        /// <summary>Closest intersection of the ray with the mesh. Returns triangle index or -1.</summary>
        public int RayCast(Vec3 origin, Vec3 dir, out double tHit, double tMin = double.NegativeInfinity)
        {
            tHit = double.PositiveInfinity;
            int best = -1;
            if (_nodes.Count == 0) return -1;
            var inv = new Vec3(1 / dir.X, 1 / dir.Y, 1 / dir.Z);
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                var n = _nodes[stack.Pop()];
                if (!RayBox(origin, inv, n.Min, n.Max, tMin, tHit)) continue;
                if (n.Left < 0)
                {
                    for (int i = n.Start; i < n.Start + n.Count; i++)
                    {
                        int t = _tris[i];
                        if (RayTriangle(origin, dir, _mesh.P(t, 0), _mesh.P(t, 1), _mesh.P(t, 2), out double th)
                            && th >= tMin && th < tHit)
                        { tHit = th; best = t; }
                    }
                }
                else { stack.Push(n.Left); stack.Push(n.Right); }
            }
            return best;
        }

        /// <summary>All intersections of the infinite line with the mesh, as (triangle, t) pairs sorted by t.</summary>
        public List<KeyValuePair<int, double>> LineHits(Vec3 origin, Vec3 dir)
        {
            var hits = new List<KeyValuePair<int, double>>();
            if (_nodes.Count == 0) return hits;
            var inv = new Vec3(1 / dir.X, 1 / dir.Y, 1 / dir.Z);
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                var n = _nodes[stack.Pop()];
                if (!RayBox(origin, inv, n.Min, n.Max, double.NegativeInfinity, double.PositiveInfinity)) continue;
                if (n.Left < 0)
                {
                    for (int i = n.Start; i < n.Start + n.Count; i++)
                    {
                        int t = _tris[i];
                        if (RayTriangle(origin, dir, _mesh.P(t, 0), _mesh.P(t, 1), _mesh.P(t, 2), out double th))
                            hits.Add(new KeyValuePair<int, double>(t, th));
                    }
                }
                else { stack.Push(n.Left); stack.Push(n.Right); }
            }
            hits.Sort((a, b) => a.Value.CompareTo(b.Value));
            return hits;
        }

        /// <summary>Triangle nearest to point p (and the closest point on it).</summary>
        public int Nearest(Vec3 p, out Vec3 closest)
        {
            closest = p;
            int best = -1;
            double bestD = double.PositiveInfinity;
            if (_nodes.Count == 0) return -1;
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                var n = _nodes[stack.Pop()];
                if (BoxDistSq(p, n.Min, n.Max) >= bestD) continue;
                if (n.Left < 0)
                {
                    for (int i = n.Start; i < n.Start + n.Count; i++)
                    {
                        int t = _tris[i];
                        var c = ClosestPointOnTriangle(p, _mesh.P(t, 0), _mesh.P(t, 1), _mesh.P(t, 2));
                        double d = c.DistanceSquaredTo(p);
                        if (d < bestD) { bestD = d; best = t; closest = c; }
                    }
                }
                else
                {
                    var l = _nodes[n.Left]; var r = _nodes[n.Right];
                    double dl = BoxDistSq(p, l.Min, l.Max), dr = BoxDistSq(p, r.Min, r.Max);
                    if (dl < dr) { stack.Push(n.Right); stack.Push(n.Left); }
                    else { stack.Push(n.Left); stack.Push(n.Right); }
                }
            }
            return best;
        }

        private static double BoxDistSq(Vec3 p, Vec3 mn, Vec3 mx)
        {
            double dx = Math.Max(0, Math.Max(mn.X - p.X, p.X - mx.X));
            double dy = Math.Max(0, Math.Max(mn.Y - p.Y, p.Y - mx.Y));
            double dz = Math.Max(0, Math.Max(mn.Z - p.Z, p.Z - mx.Z));
            return dx * dx + dy * dy + dz * dz;
        }

        private static bool RayBox(Vec3 o, Vec3 inv, Vec3 mn, Vec3 mx, double t0, double t1)
        {
            for (int a = 0; a < 3; a++)
            {
                double ta = (mn[a] - o[a]) * inv[a], tb = (mx[a] - o[a]) * inv[a];
                if (double.IsNaN(ta) || double.IsNaN(tb))
                {
                    if (o[a] < mn[a] || o[a] > mx[a]) return false;
                    continue;
                }
                if (ta > tb) { var tmp = ta; ta = tb; tb = tmp; }
                t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb);
                if (t0 > t1) return false;
            }
            return true;
        }

        /// <summary>Möller–Trumbore, two-sided.</summary>
        public static bool RayTriangle(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c, out double t)
        {
            t = 0;
            Vec3 e1 = b - a, e2 = c - a;
            Vec3 p = d.Cross(e2);
            double det = e1.Dot(p);
            if (Math.Abs(det) < 1e-300) return false;
            double inv = 1 / det;
            Vec3 s = o - a;
            double u = s.Dot(p) * inv;
            if (u < -1e-12 || u > 1 + 1e-12) return false;
            Vec3 q = s.Cross(e1);
            double v = d.Dot(q) * inv;
            if (v < -1e-12 || u + v > 1 + 1e-12) return false;
            t = e2.Dot(q) * inv;
            return true;
        }

        public static Vec3 ClosestPointOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
        {
            Vec3 ab = b - a, ac = c - a, ap = p - a;
            double d1 = ab.Dot(ap), d2 = ac.Dot(ap);
            if (d1 <= 0 && d2 <= 0) return a;
            Vec3 bp = p - b;
            double d3 = ab.Dot(bp), d4 = ac.Dot(bp);
            if (d3 >= 0 && d4 <= d3) return b;
            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            Vec3 cp = p - c;
            double d5 = ab.Dot(cp), d6 = ac.Dot(cp);
            if (d6 >= 0 && d5 <= d6) return c;
            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            double va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
                return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            double den = 1 / (va + vb + vc);
            return a + ab * (vb * den) + ac * (vc * den);
        }
    }
}
