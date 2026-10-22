using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Analysis
{
    /// <summary>Circle fitted to one cross-section of a fillet band.</summary>
    public sealed class FilletStation
    {
        /// <summary>Distance along the spine from its start (model units).</summary>
        public double S { get; set; }
        public Vec3 Center { get; set; }
        public Vec3 Tangent { get; set; }
        public double Radius { get; set; }
        public double Rms { get; set; }
        /// <summary>Angular extent of the measured arc (degrees).</summary>
        public double SpanDeg { get; set; }
        /// <summary>Arc end points: where the fillet meets the adjacent faces (tangent lines).</summary>
        public Vec3 Edge1 { get; set; }
        public Vec3 Edge2 { get; set; }
        public int PointCount { get; set; }
    }

    /// <summary>
    /// Measures a fillet / blend band like a rolling ball: the band is parameterised along its length,
    /// cut into stations, and a circle is fitted to each cross-section. Gives the radius along the
    /// fillet (constant or variable), the spine (ball centre path) and both tangent lines.
    /// </summary>
    public sealed class FilletAnalysis
    {
        public List<FilletStation> Stations { get; } = new List<FilletStation>();
        public double Length { get; private set; }
        public double MeanRadius => Stations.Count == 0 ? 0 : Stations.Average(s => s.Radius);
        public double MinRadius => Stations.Count == 0 ? 0 : Stations.Min(s => s.Radius);
        public double MaxRadius => Stations.Count == 0 ? 0 : Stations.Max(s => s.Radius);
        /// <summary>True when the radius varies less than the measurement scatter (or 3% of the radius).</summary>
        public bool IsConstant { get; private set; }

        public static FilletAnalysis Analyze(TriMesh m, IList<int> bandTriangles, double stationSpacing = 0)
        {
            var verts = m.VerticesOf(bandTriangles);
            if (verts.Length < 20) throw new ArgumentException("The fillet band is too small to analyse.");
            var pts = verts.Select(v => m.Vertices[v]).ToArray();

            // 1. Arc-length parameter along the band: geodesic distance from one end.
            var local = new Dictionary<int, int>();
            for (int i = 0; i < verts.Length; i++) local[verts[i]] = i;
            var adj = new List<int>[verts.Length];
            for (int i = 0; i < adj.Length; i++) adj[i] = new List<int>();
            foreach (int t in bandTriangles)
                for (int k = 0; k < 3; k++)
                {
                    int a = local[m.V(t, k)], b = local[m.V(t, (k + 1) % 3)];
                    if (!adj[a].Contains(b)) { adj[a].Add(b); adj[b].Add(a); }
                }
            var d0 = Dijkstra(pts, adj, 0);
            int endA = ArgMax(d0);
            var dA = Dijkstra(pts, adj, endA);
            int endB = ArgMax(dA);
            var dB = Dijkstra(pts, adj, endB);
            double length = dA[endB];
            // Distances from both ends: their difference cancels most of the across-the-band variation.
            var s = new double[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                s[i] = double.IsInfinity(dA[i]) || double.IsInfinity(dB[i]) ? double.PositiveInfinity : 0.5 * (dA[i] + length - dB[i]);

            double width = EstimateWidth(m, bandTriangles, length);
            // 2. Coarse centre line (bins about one band width long), smoothed; points re-parameterised by
            //    projection onto it so that cross-sections are cut perpendicular to the fillet.
            double coarse = Math.Max(width, 4 * m.AverageEdgeLength);
            int nCoarse = Math.Max(3, (int)Math.Round(length / coarse));
            var spine = BinCentroids(pts, s, nCoarse, length / nCoarse);
            for (int iter = 0; iter < 3 && spine.Count >= 3; iter++)
            {
                spine = FeatureCurves.Taubin(spine, false, 5);
                s = ProjectOnPolyline(pts, spine, out double total);
                length = total;
                spine = BinCentroids(pts, s, nCoarse, length / nCoarse);
            }
            spine = FeatureCurves.Taubin(spine, false, 5);
            // Bin centroids stop half a bin short of the band ends: extend the centre line there.
            if (spine.Count >= 2)
            {
                double ext = 0.5 * length / nCoarse;
                spine.Insert(0, spine[0] + (spine[0] - spine[1]).Normalized() * ext);
                int last = spine.Count - 1;
                spine.Add(spine[last] + (spine[last] - spine[last - 1]).Normalized() * ext);
            }
            s = ProjectOnPolyline(pts, spine, out double spineLength);
            length = spineLength;

            // 3. Fine stations along the centre line.
            double spacing = stationSpacing > 0 ? stationSpacing : Math.Max(Math.Min(width, length / 30), 4 * m.AverageEdgeLength);
            int nst = Math.Max(3, (int)Math.Round(length / spacing));
            spacing = length / nst;
            var res = new FilletAnalysis { Length = length };
            for (int k = 0; k < nst; k++)
            {
                double sk = (k + 0.5) * spacing;
                PointOnPolyline(spine, sk, out var origin, out var tan);
                double lo = sk - spacing * 0.5, hi = sk + spacing * 0.5;
                var section = new List<Vec3>();
                for (int i = 0; i < pts.Length; i++) if (s[i] >= lo && s[i] <= hi) section.Add(pts[i]);
                var st = FitSection(section, origin, tan);
                if (st == null) continue;
                st.S = sk;
                res.Stations.Add(st);
            }
            // Keep Edge1 on the same side of the fillet from station to station.
            for (int k = 1; k < res.Stations.Count; k++)
            {
                var a = res.Stations[k - 1]; var b = res.Stations[k];
                if (b.Edge1.DistanceTo(a.Edge1) > b.Edge1.DistanceTo(a.Edge2)) { var t = b.Edge1; b.Edge1 = b.Edge2; b.Edge2 = t; }
            }
            // Outlier stations (cuts across band ends / junctions) are dropped by a robust radius test.
            if (res.Stations.Count >= 5)
            {
                var radii = res.Stations.Select(x => x.Radius).OrderBy(x => x).ToList();
                double med = radii[radii.Count / 2];
                double mad = res.Stations.Select(x => Math.Abs(x.Radius - med)).OrderBy(x => x).ElementAt(res.Stations.Count / 2) * 1.4826;
                double lim = Math.Max(4 * mad, 0.25 * med);
                res.Stations.RemoveAll(x => Math.Abs(x.Radius - med) > lim && !IsTrend(res.Stations, x));
            }
            if (res.Stations.Count > 0)
            {
                double scatter = res.Stations.Average(x => x.Rms);
                double spread = res.MaxRadius - res.MinRadius;
                res.IsConstant = spread <= Math.Max(4 * scatter, 0.03 * res.MeanRadius);
            }
            return res;
        }

        /// <summary>A station is part of a smooth trend if it agrees with its neighbours (variable fillets).</summary>
        private static bool IsTrend(List<FilletStation> st, FilletStation x)
        {
            int i = st.IndexOf(x);
            if (i <= 0 || i >= st.Count - 1) return false;
            double interp = 0.5 * (st[i - 1].Radius + st[i + 1].Radius);
            return Math.Abs(x.Radius - interp) < 0.1 * interp;
        }

        private static FilletStation FitSection(List<Vec3> section, Vec3 center, Vec3 tangent)
        {
            if (section.Count < 8) return null;
            var f = Frame.FromAxis(center, tangent);
            var xs = new double[section.Count]; var ys = new double[section.Count];
            for (int i = 0; i < section.Count; i++) { var l = f.ToLocal(section[i]); xs[i] = l.X; ys[i] = l.Y; }
            if (!AnalyticFitters.FitCircle2D(xs, ys, out double cx, out double cy, out double r)) return null;
            // Geometric refinement (Gauss-Newton on orthogonal distances).
            for (int it = 0; it < 15; it++)
            {
                double a11 = 0, a12 = 0, a13 = 0, a22 = 0, a23 = 0, a33 = 0, b1 = 0, b2 = 0, b3 = 0;
                for (int i = 0; i < xs.Length; i++)
                {
                    double dx = xs[i] - cx, dy = ys[i] - cy, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1e-12) continue;
                    double j1 = -dx / d, j2 = -dy / d, j3 = -1, e = d - r;
                    a11 += j1 * j1; a12 += j1 * j2; a13 += j1 * j3; a22 += j2 * j2; a23 += j2 * j3; a33 += j3 * j3;
                    b1 -= j1 * e; b2 -= j2 * e; b3 -= j3 * e;
                }
                var sol = LinearAlgebra.SolveSymmetric(new[,] { { a11, a12, a13 }, { a12, a22, a23 }, { a13, a23, a33 } }, new[] { b1, b2, b3 });
                if (sol == null) break;
                cx += sol[0]; cy += sol[1]; r += sol[2];
                if (Math.Abs(sol[0]) + Math.Abs(sol[1]) + Math.Abs(sol[2]) < 1e-9 * Math.Max(r, 1e-9)) break;
            }
            double ss = 0;
            var ang = new double[xs.Length];
            for (int i = 0; i < xs.Length; i++)
            {
                double dx = xs[i] - cx, dy = ys[i] - cy;
                double e = Math.Sqrt(dx * dx + dy * dy) - r;
                ss += e * e;
                ang[i] = Math.Atan2(dy, dx);
            }
            AngleUtil.CoveringArc(ang, out double a0, out double span);
            if (span < 10 * Math.PI / 180 || r <= 0) return null;
            var c3 = f.ToWorld(cx, cy, 0);
            return new FilletStation
            {
                Center = c3, Tangent = tangent, Radius = r, Rms = Math.Sqrt(ss / xs.Length), SpanDeg = span * 180 / Math.PI,
                Edge1 = f.ToWorld(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0), 0),
                Edge2 = f.ToWorld(cx + r * Math.Cos(a0 + span), cy + r * Math.Sin(a0 + span), 0),
                PointCount = xs.Length
            };
        }

        private static void PointOnPolyline(List<Vec3> poly, double sAt, out Vec3 p, out Vec3 tangent)
        {
            double acc = 0;
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                double seg = poly[i].DistanceTo(poly[i + 1]);
                if (acc + seg >= sAt || i + 2 == poly.Count)
                {
                    double t = seg > 0 ? Math.Max(0, Math.Min(1, (sAt - acc) / seg)) : 0;
                    p = Vec3.Lerp(poly[i], poly[i + 1], t);
                    tangent = (poly[i + 1] - poly[i]).Normalized();
                    return;
                }
                acc += seg;
            }
            p = poly[0];
            tangent = poly.Count > 1 ? (poly[1] - poly[0]).Normalized() : Vec3.UnitX;
        }

        private static double EstimateWidth(TriMesh m, IList<int> tris, double length)
        {
            double area = tris.Sum(t => m.FaceAreas[t]);
            return length > 0 ? area / length : Math.Sqrt(area);
        }

        private static List<Vec3> BinCentroids(Vec3[] pts, double[] s, int n, double spacing)
        {
            var sum = new Vec3[n]; var cnt = new int[n];
            for (int i = 0; i < pts.Length; i++)
            {
                if (double.IsInfinity(s[i])) continue;
                int b = Math.Max(0, Math.Min(n - 1, (int)(s[i] / spacing)));
                sum[b] += pts[i]; cnt[b]++;
            }
            var l = new List<Vec3>();
            for (int b = 0; b < n; b++) if (cnt[b] > 0) l.Add(sum[b] / cnt[b]);
            return l;
        }

        /// <summary>Arc-length parameter of each point's projection on the polyline.</summary>
        private static double[] ProjectOnPolyline(Vec3[] pts, List<Vec3> poly, out double total)
        {
            var cum = new double[poly.Count];
            for (int i = 1; i < poly.Count; i++) cum[i] = cum[i - 1] + poly[i].DistanceTo(poly[i - 1]);
            total = cum[cum.Length - 1];
            var s = new double[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                double best = double.MaxValue, bs = 0;
                for (int k = 0; k + 1 < poly.Count; k++)
                {
                    var a = poly[k]; var d = poly[k + 1] - a;
                    double len2 = d.LengthSquared;
                    double t = len2 > 0 ? Math.Max(0, Math.Min(1, (pts[i] - a).Dot(d) / len2)) : 0;
                    double dist = (a + d * t).DistanceSquaredTo(pts[i]);
                    if (dist < best) { best = dist; bs = cum[k] + t * Math.Sqrt(len2); }
                }
                s[i] = bs;
            }
            return s;
        }

        private static double[] Dijkstra(Vec3[] pts, List<int>[] adj, int src)
        {
            var d = Enumerable.Repeat(double.PositiveInfinity, pts.Length).ToArray();
            d[src] = 0;
            var heap = new SortedSet<(double, int)> { (0, src) };
            while (heap.Count > 0)
            {
                var (dist, v) = heap.Min;
                heap.Remove(heap.Min);
                if (dist > d[v]) continue;
                foreach (int w in adj[v])
                {
                    double nd = dist + pts[v].DistanceTo(pts[w]);
                    if (nd < d[w]) { heap.Remove((d[w], w)); d[w] = nd; heap.Add((nd, w)); }
                }
            }
            return d;
        }

        private static int ArgMax(double[] d)
        {
            int best = 0;
            for (int i = 1; i < d.Length; i++) if (!double.IsInfinity(d[i]) && d[i] > d[best]) best = i;
            return best;
        }

        /// <summary>Radius at fractions of the length (for a SOLIDWORKS variable-size fillet).</summary>
        public double RadiusAt(double fraction)
        {
            if (Stations.Count == 0) return 0;
            double target = fraction * Stations[Stations.Count - 1].S + (1 - fraction) * Stations[0].S;
            for (int i = 1; i < Stations.Count; i++)
                if (Stations[i].S >= target)
                {
                    var a = Stations[i - 1]; var b = Stations[i];
                    double t = (target - a.S) / Math.Max(b.S - a.S, 1e-12);
                    return a.Radius + (b.Radius - a.Radius) * t;
                }
            return Stations[Stations.Count - 1].Radius;
        }

        public string Report(double k, string unit)
        {
            if (Stations.Count == 0) return "No usable cross-sections — select the fillet band only (Fillet tool).";
            var inv = CultureInfo.InvariantCulture;
            string F(double v) => (v * k).ToString("0.###", inv);
            var lines = new List<string>
            {
                IsConstant
                    ? $"Constant fillet  R {F(MeanRadius)} {unit}  (measured {F(MinRadius)}–{F(MaxRadius)})"
                    : $"Variable fillet  R {F(MinRadius)} → {F(MaxRadius)} {unit}",
                $"Length {F(Length)} {unit}, {Stations.Count} cross-sections, arc ≈ {Stations.Average(s => s.SpanDeg):0}°, " +
                $"fit RMS {F(Stations.Average(s => s.Rms))} {unit}",
                "Variable-size fillet control points (radius at % of length):",
                "  " + string.Join("  ", new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }.Select(f => $"{f * 100:0}%: R{F(RadiusAt(f))}"))
            };
            return string.Join(Environment.NewLine, lines);
        }
    }
}
