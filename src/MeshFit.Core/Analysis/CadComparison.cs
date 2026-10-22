using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Analysis
{
    /// <summary>
    /// Accuracy analysis of finished CAD surfaces against the scan: for every scan vertex, the signed
    /// distance to the nearest CAD face (from the CAD tessellation, optionally refined to the exact
    /// surface by the caller). Scan areas farther than <c>maxDistance</c> count as "not modelled".
    /// </summary>
    public sealed class CadComparison
    {
        /// <summary>Signed distance per scan vertex (model units); NaN = not covered by the CAD.</summary>
        public float[] Deviation { get; private set; }
        /// <summary>CAD tessellation triangle nearest to each scan vertex (-1 if not covered).</summary>
        public int[] NearestCadTriangle { get; private set; }
        public double Tolerance { get; private set; }

        public sealed class Stats
        {
            public string Name;
            public int Points;
            public double Rms, MaxAbs, Mean, WithinTolerance;
            public override string ToString() =>
                $"{Name}: {Points:N0} pts, RMS {Rms:G4}, max {MaxAbs:G4}, {WithinTolerance:P1} within tolerance";
        }

        /// <param name="cad">Tessellation of the CAD bodies.</param>
        /// <param name="sampleVertices">Scan vertices to evaluate (null = all).</param>
        public static CadComparison Compute(TriMesh scan, TriMesh cad, double maxDistance, double tolerance, IList<int> sampleVertices = null)
        {
            var bvh = cad.Bvh; // build once before going parallel
            int n = scan.VertexCount;
            var dev = new float[n];
            var near = new int[n];
            for (int i = 0; i < n; i++) { dev[i] = float.NaN; near[i] = -1; }
            var list = sampleVertices ?? AllIndices(n);
            Parallel.For(0, list.Count, i =>
            {
                int v = list[i];
                var p = scan.Vertices[v];
                int t = bvh.Nearest(p, out var c);
                if (t < 0) return;
                double d = c.DistanceTo(p);
                if (d > maxDistance) return;
                if ((p - c).Dot(cad.FaceNormals[t]) < 0) d = -d;
                dev[v] = (float)d;
                near[v] = t;
            });
            return new CadComparison { Deviation = dev, NearestCadTriangle = near, Tolerance = tolerance };
        }

        /// <summary>Replaces a vertex's distance with an exact value (e.g. from the CAD kernel).</summary>
        public void SetExact(int vertex, double signedDistance) => Deviation[vertex] = (float)signedDistance;

        public Stats Summarize(string name, Func<int, bool> include = null)
        {
            int count = 0, within = 0;
            double sum = 0, sum2 = 0, max = 0;
            for (int v = 0; v < Deviation.Length; v++)
            {
                float d = Deviation[v];
                if (float.IsNaN(d) || (include != null && !include(v))) continue;
                count++; sum += d; sum2 += d * d;
                max = Math.Max(max, Math.Abs(d));
                if (Math.Abs(d) <= Tolerance) within++;
            }
            int c = Math.Max(1, count);
            return new Stats { Name = name, Points = count, Rms = Math.Sqrt(sum2 / c), Mean = sum / c, MaxAbs = max, WithinTolerance = (double)within / c };
        }

        public double Coverage(IList<int> sampleVertices = null)
        {
            int total = 0, covered = 0;
            var list = sampleVertices ?? AllIndices(Deviation.Length);
            foreach (int v in list) { total++; if (!float.IsNaN(Deviation[v])) covered++; }
            return total == 0 ? 0 : (double)covered / total;
        }

        private static IList<int> AllIndices(int n)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = i;
            return a;
        }
    }
}
