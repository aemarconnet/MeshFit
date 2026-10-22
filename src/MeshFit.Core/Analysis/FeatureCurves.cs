using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Analysis
{
    /// <summary>A polyline curve extracted from the mesh (model units).</summary>
    public sealed class FeatureCurve
    {
        public List<Vec3> Points { get; set; } = new List<Vec3>();
        public bool Closed { get; set; }
        public string Name { get; set; }
        public double Length
        {
            get
            {
                double l = 0;
                for (int i = 1; i < Points.Count; i++) l += Points[i].DistanceTo(Points[i - 1]);
                if (Closed && Points.Count > 2) l += Points[0].DistanceTo(Points[Points.Count - 1]);
                return l;
            }
        }
    }

    /// <summary>
    /// Extracts region boundary curves (the trim lines between painted surfaces, or the tangent lines of a
    /// fillet band) as smooth polylines ready to become 3D sketch splines.
    /// </summary>
    public static class FeatureCurves
    {
        public sealed class Options
        {
            /// <summary>Include edges on the open border of the scan (usually not a design feature).</summary>
            public bool IncludeMeshBorder { get; set; }
            /// <summary>Split curves where they turn sharper than this over a short distance (degrees; ≤ 0 = never).</summary>
            public double CornerAngleDeg { get; set; } = 50;
            /// <summary>Smoothing passes that remove the triangle staircase (Taubin, no shrinking).</summary>
            public int SmoothingPasses { get; set; } = 30;
            /// <summary>Point spacing of the output (model units, 0 = 3× mesh edge length).</summary>
            public double Spacing { get; set; }
            /// <summary>Drop curves shorter than this many spacings.</summary>
            public double MinLengthSpacings { get; set; } = 4;
        }

        public static List<FeatureCurve> RegionBoundaries(TriMesh m, bool[] mask, Options opt = null)
        {
            opt = opt ?? new Options();
            double spacing = opt.Spacing > 0 ? opt.Spacing : 3 * m.AverageEdgeLength;
            var raw = BoundaryChains(m, mask, opt.IncludeMeshBorder);
            var result = new List<FeatureCurve>();
            foreach (var (pts, closed) in raw)
            {
                // Corners are found on a lightly smoothed curve, then each piece is smoothed with its
                // corner points pinned so the corners stay sharp.
                double fine = m.AverageEdgeLength;
                var light = Resample(Taubin(pts, closed, 3), closed, fine);
                int window = Math.Max(2, (int)Math.Round(spacing / fine));
                foreach (var piece in SplitAtCorners(light, closed, opt.CornerAngleDeg, window))
                {
                    var sm = Taubin(piece.Points, piece.Closed, opt.SmoothingPasses);
                    piece.Points = Resample(sm, piece.Closed, spacing);
                    if (piece.Length >= opt.MinLengthSpacings * spacing) result.Add(piece);
                }
            }
            result.Sort((a, b) => b.Length.CompareTo(a.Length));
            for (int i = 0; i < result.Count; i++) result[i].Name = "Boundary " + (i + 1);
            return result;
        }

        /// <summary>Chains the boundary edges of the mask into polylines (closed loops or open chains).</summary>
        public static List<(List<Vec3> pts, bool closed)> BoundaryChains(TriMesh m, bool[] mask, bool includeMeshBorder)
        {
            // Directed boundary edges a→b following the triangle winding.
            var next = new Dictionary<int, List<int>>();
            int edgeCount = 0;
            for (int t = 0; t < mask.Length; t++)
            {
                if (!mask[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int nb = m.Neighbors[t * 3 + k];
                    bool boundary = nb < 0 ? includeMeshBorder : !mask[nb];
                    if (!boundary) continue;
                    int a = m.V(t, k), b = m.V(t, (k + 1) % 3);
                    if (!next.TryGetValue(a, out var l)) next[a] = l = new List<int>(1);
                    l.Add(b);
                    edgeCount++;
                }
            }
            var indegree = new Dictionary<int, int>();
            foreach (var kv in next) foreach (int b in kv.Value) indegree[b] = indegree.TryGetValue(b, out int d) ? d + 1 : 1;

            var chains = new List<(List<Vec3>, bool)>();
            // Open chains start at vertices with no incoming boundary edge.
            var starts = next.Keys.Where(v => !indegree.ContainsKey(v)).ToList();
            foreach (int s in starts) chains.Add((Walk(m, next, s, out _), false));
            // Remaining edges form loops.
            while (true)
            {
                int s = -1;
                foreach (var kv in next) if (kv.Value.Count > 0) { s = kv.Key; break; }
                if (s < 0) break;
                var pts = Walk(m, next, s, out bool closed);
                chains.Add((pts, closed));
            }
            return chains.Where(c => c.Item1.Count >= 2).ToList();
        }

        private static List<Vec3> Walk(TriMesh m, Dictionary<int, List<int>> next, int start, out bool closed)
        {
            var pts = new List<Vec3> { m.Vertices[start] };
            int v = start;
            closed = false;
            while (next.TryGetValue(v, out var l) && l.Count > 0)
            {
                int w = l[l.Count - 1];
                l.RemoveAt(l.Count - 1);
                if (w == start) { closed = true; break; }
                pts.Add(m.Vertices[w]);
                v = w;
            }
            return pts;
        }

        /// <summary>Taubin λ/μ smoothing: removes zig-zag without shrinking the curve. Open ends stay fixed.</summary>
        public static List<Vec3> Taubin(List<Vec3> p, bool closed, int passes, double lambda = 0.5, double mu = -0.53)
        {
            var a = p.ToArray();
            int n = a.Length;
            if (n < 3) return p;
            var b = new Vec3[n];
            for (int it = 0; it < 2 * passes; it++)
            {
                double f = it % 2 == 0 ? lambda : mu;
                for (int i = 0; i < n; i++)
                {
                    if (!closed && (i == 0 || i == n - 1)) { b[i] = a[i]; continue; }
                    var prev = a[(i - 1 + n) % n];
                    var nxt = a[(i + 1) % n];
                    b[i] = a[i] + ((prev + nxt) * 0.5 - a[i]) * f;
                }
                var tmp = a; a = b; b = tmp;
            }
            return a.ToList();
        }

        /// <summary>Uniform arc-length resampling.</summary>
        public static List<Vec3> Resample(List<Vec3> p, bool closed, double spacing)
        {
            var pts = new List<Vec3>(p);
            if (closed) pts.Add(p[0]);
            double total = 0;
            for (int i = 1; i < pts.Count; i++) total += pts[i].DistanceTo(pts[i - 1]);
            int n = Math.Max(closed ? 3 : 1, (int)Math.Round(total / spacing));
            double step = total / n;
            var outp = new List<Vec3> { pts[0] };
            double acc = 0, target = step;
            for (int i = 1; i < pts.Count && outp.Count < n + (closed ? 0 : 1); i++)
            {
                double seg = pts[i].DistanceTo(pts[i - 1]);
                while (seg > 0 && acc + seg >= target - 1e-12 && outp.Count < n + (closed ? 0 : 1))
                {
                    outp.Add(Vec3.Lerp(pts[i - 1], pts[i], (target - acc) / seg));
                    target += step;
                }
                acc += seg;
            }
            if (!closed) outp[outp.Count - 1] = pts[pts.Count - 1];
            else if (outp.Count > n) outp.RemoveAt(outp.Count - 1);
            return outp;
        }

        /// <summary>Splits a polyline where the direction turns by more than the corner angle.</summary>
        /// <param name="w">Half window (points) over which the turning angle is measured.</param>
        public static List<FeatureCurve> SplitAtCorners(List<Vec3> p, bool closed, double cornerDeg, int w)
        {
            var single = new List<FeatureCurve> { new FeatureCurve { Points = p, Closed = closed } };
            if (cornerDeg <= 0 || p.Count < 2 * w + 3) return single;
            double cosLimit = Math.Cos(cornerDeg * Math.PI / 180);
            int n = p.Count;
            var corners = new List<int>();
            var turn = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (!closed && (i < w || i >= n - w)) continue;
                var d1 = (p[i] - p[(i - w + n) % n]).Normalized();
                var d2 = (p[(i + w) % n] - p[i]).Normalized();
                turn[i] = d1.Dot(d2);
                if (turn[i] < cosLimit) corners.Add(i);
            }
            // One index per corner cluster: the sharpest turn.
            var picked = new List<int>();
            for (int i = 0; i < corners.Count; i++)
            {
                int j = i, best = corners[i];
                while (j + 1 < corners.Count && corners[j + 1] == corners[j] + 1)
                {
                    j++;
                    if (turn[corners[j]] < turn[best]) best = corners[j];
                }
                picked.Add(best);
                i = j;
            }
            // A closed loop's cluster may wrap around index 0.
            if (closed && picked.Count > 1 && corners[0] == 0 && corners[corners.Count - 1] == n - 1)
            {
                int a = picked[0], b = picked[picked.Count - 1];
                picked.RemoveAt(picked.Count - 1);
                picked[0] = turn[a] <= turn[b] ? a : b;
                picked.Sort();
            }
            if (picked.Count == 0) return single;
            var pieces = new List<FeatureCurve>();
            if (closed)
            {
                for (int c = 0; c < picked.Count; c++)
                {
                    int a = picked[c], b = picked[(c + 1) % picked.Count];
                    var seg = new List<Vec3>();
                    for (int i = a; ; i = (i + 1) % n) { seg.Add(p[i]); if (i == b) break; if (seg.Count > n) break; }
                    pieces.Add(new FeatureCurve { Points = seg });
                }
            }
            else
            {
                int a = 0;
                foreach (int c in picked.Concat(new[] { n - 1 }))
                {
                    pieces.Add(new FeatureCurve { Points = p.GetRange(a, c - a + 1) });
                    a = c;
                }
            }
            return pieces;
        }
    }
}
