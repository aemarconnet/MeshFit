using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Mesh
{
    /// <summary>
    /// Principal curvature magnitudes estimated per vertex by fitting a quadric height field
    /// z = ax² + bxy + cy² + dx + ey + f to the neighbourhood within <see cref="Scale"/> (local frame from
    /// the vertex normal), then averaged to triangles. A larger scale suppresses scan noise; a smaller one
    /// resolves tighter fillets. Units: 1 / model length.
    /// </summary>
    public sealed class Curvature
    {
        public double Scale { get; }
        /// <summary>Largest absolute principal curvature per triangle.</summary>
        public float[] KMax { get; }
        /// <summary>Smallest absolute principal curvature per triangle.</summary>
        public float[] KMin { get; }

        private Curvature(double scale, float[] kmax, float[] kmin) { Scale = scale; KMax = kmax; KMin = kmin; }

        public static double DefaultScale(TriMesh m) => 4 * m.AverageEdgeLength;

        /// <param name="progress">Receives the completed fraction (0..1).</param>
        public static Curvature Compute(TriMesh m, double scale, CancellationToken ct = default, IProgress<double> progress = null)
        {
            int nv = m.VertexCount;
            var adj = VertexAdjacency(m);
            var vmax = new float[nv];
            var vmin = new float[nv];
            double r2 = scale * scale;
            int done = 0, step = Math.Max(1, nv / 50);
            Parallel.For(0, nv, Work.Options(ct), () => (new List<int>(), new HashSet<int>()), (v, state, buf) =>
            {
                var (list, seen) = buf;
                list.Clear(); seen.Clear();
                var p0 = m.Vertices[v];
                list.Add(v); seen.Add(v);
                for (int i = 0; i < list.Count && list.Count < 400; i++)
                    foreach (int w in adj[list[i]])
                        if (seen.Add(w) && m.Vertices[w].DistanceSquaredTo(p0) <= r2) list.Add(w);
                int d = Interlocked.Increment(ref done);
                if (progress != null && d % step == 0) progress.Report((double)d / nv);
                if (list.Count < 7) return buf;
                var f = Frame.FromAxis(p0, m.VertexNormals[v]);
                var ata = new double[6, 6];
                var atb = new double[6];
                var row = new double[6];
                foreach (int w in list)
                {
                    var l = f.ToLocal(m.Vertices[w]);
                    row[0] = l.X * l.X; row[1] = l.X * l.Y; row[2] = l.Y * l.Y; row[3] = l.X; row[4] = l.Y; row[5] = 1;
                    for (int a = 0; a < 6; a++)
                    {
                        atb[a] += row[a] * l.Z;
                        for (int b = 0; b <= a; b++) ata[a, b] += row[a] * row[b];
                    }
                }
                for (int a = 0; a < 6; a++) for (int b = 0; b < a; b++) ata[b, a] = ata[a, b];
                var c = LinearAlgebra.SolveSymmetric(ata, atb);
                if (c == null) return buf;
                // Shape operator of the graph at the origin (with the fitted slope).
                double fx = c[3], fy = c[4], fxx = 2 * c[0], fxy = c[1], fyy = 2 * c[2];
                double E = 1 + fx * fx, F = fx * fy, G = 1 + fy * fy, w2 = Math.Sqrt(1 + fx * fx + fy * fy);
                double L = fxx / w2, M = fxy / w2, N = fyy / w2;
                double det = E * G - F * F;
                double H = (E * N - 2 * F * M + G * L) / (2 * det);
                double K = (L * N - M * M) / det;
                double disc = Math.Sqrt(Math.Max(0, H * H - K));
                double k1 = Math.Abs(H + disc), k2 = Math.Abs(H - disc);
                vmax[v] = (float)Math.Max(k1, k2);
                vmin[v] = (float)Math.Min(k1, k2);
                return buf;
            }, _ => { });

            int nt = m.TriangleCount;
            var tmax = new float[nt];
            var tmin = new float[nt];
            for (int t = 0; t < nt; t++)
            {
                tmax[t] = (vmax[m.V(t, 0)] + vmax[m.V(t, 1)] + vmax[m.V(t, 2)]) / 3f;
                tmin[t] = (vmin[m.V(t, 0)] + vmin[m.V(t, 1)] + vmin[m.V(t, 2)]) / 3f;
            }
            return new Curvature(scale, tmax, tmin);
        }

        /// <summary>Median of KMax over the triangles (typical curvature of a region).</summary>
        public double MedianKMax(IEnumerable<int> tris)
        {
            var l = new List<float>();
            foreach (int t in tris) l.Add(KMax[t]);
            if (l.Count == 0) return 0;
            l.Sort();
            return l[l.Count / 2];
        }

        private static List<int>[] VertexAdjacency(TriMesh m)
        {
            var adj = new List<int>[m.VertexCount];
            for (int i = 0; i < adj.Length; i++) adj[i] = new List<int>(6);
            for (int t = 0; t < m.TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = m.V(t, k), b = m.V(t, (k + 1) % 3);
                    if (!adj[a].Contains(b)) adj[a].Add(b);
                    if (!adj[b].Contains(a)) adj[b].Add(a);
                }
            return adj;
        }
    }
}
