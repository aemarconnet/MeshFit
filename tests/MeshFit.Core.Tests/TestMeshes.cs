using System;
using System.Collections.Generic;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Tests
{
    /// <summary>Synthetic meshes sampled from known surfaces.</summary>
    internal static class TestMeshes
    {
        public static TriMesh Grid(Func<double, double, Vec3> f, double s0, double s1, double t0, double t1,
                                   int ns, int nt, double noise = 0, int seed = 1)
        {
            var rnd = new Random(seed);
            var v = new Vec3[(ns + 1) * (nt + 1)];
            for (int i = 0; i <= ns; i++)
                for (int j = 0; j <= nt; j++)
                {
                    var p = f(s0 + (s1 - s0) * i / ns, t0 + (t1 - t0) * j / nt);
                    if (noise > 0) p += new Vec3(Gauss(rnd), Gauss(rnd), Gauss(rnd)) * noise;
                    v[i * (nt + 1) + j] = p;
                }
            var idx = new List<int>();
            for (int i = 0; i < ns; i++)
                for (int j = 0; j < nt; j++)
                {
                    int a = i * (nt + 1) + j, b = a + nt + 1, c = b + 1, d = a + 1;
                    idx.AddRange(new[] { a, b, c, a, c, d });
                }
            return new TriMesh(v, idx.ToArray());
        }

        public static double Gauss(Random r)
        {
            double u1 = 1 - r.NextDouble(), u2 = r.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        /// <summary>Closed axis aligned box [0,size]^3 made of n×n quads per face, welded.</summary>
        public static TriMesh Box(double size, int n)
        {
            var verts = new List<Vec3>();
            var map = new Dictionary<(long, long, long), int>();
            var idx = new List<int>();
            int Vtx(Vec3 p)
            {
                var key = ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6), (long)Math.Round(p.Z * 1e6));
                if (!map.TryGetValue(key, out int i)) { i = verts.Count; verts.Add(p); map[key] = i; }
                return i;
            }
            // Each face: origin, u dir, v dir with outward normal u x v.
            var faces = new[]
            {
                (new Vec3(0,0,0), Vec3.UnitY, Vec3.UnitX), (new Vec3(0,0,1), Vec3.UnitX, Vec3.UnitY),
                (new Vec3(0,0,0), Vec3.UnitX, Vec3.UnitZ), (new Vec3(0,1,0), Vec3.UnitZ, Vec3.UnitX),
                (new Vec3(0,0,0), Vec3.UnitZ, Vec3.UnitY), (new Vec3(1,0,0), Vec3.UnitY, Vec3.UnitZ),
            };
            foreach (var (o, u, w) in faces)
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        Vec3 P(int a, int b) => (o + u * ((double)a / n) + w * ((double)b / n)) * size;
                        int a0 = Vtx(P(i, j)), a1 = Vtx(P(i + 1, j)), a2 = Vtx(P(i + 1, j + 1)), a3 = Vtx(P(i, j + 1));
                        idx.AddRange(new[] { a0, a1, a2, a0, a2, a3 });
                    }
            return new TriMesh(verts.ToArray(), idx.ToArray());
        }

        public static int[] All(TriMesh m)
        {
            var r = new int[m.TriangleCount];
            for (int i = 0; i < r.Length; i++) r[i] = i;
            return r;
        }
    }
}
