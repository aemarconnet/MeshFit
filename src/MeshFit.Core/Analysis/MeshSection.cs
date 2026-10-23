using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Analysis
{
    /// <summary>One connected piece of a mesh cross-section.</summary>
    public sealed class SectionPolyline
    {
        public List<Vec3> Points { get; } = new List<Vec3>();
        public bool Closed { get; set; }
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

    /// <summary>Plane sections of a triangle mesh (the "section polylines" of a mesh sketch).</summary>
    public static class MeshSection
    {
        /// <summary>
        /// Intersects the mesh (or only <paramref name="tris"/>) with the plane through <paramref name="origin"/>
        /// with normal <paramref name="normal"/> and chains the cuts into polylines. Vertices lying exactly on the
        /// plane are nudged to one side so every crossing is counted once.
        /// </summary>
        public static List<SectionPolyline> Slice(TriMesh m, Vec3 origin, Vec3 normal, IEnumerable<int> tris = null,
                                                  CancellationToken ct = default)
        {
            var n = normal.Normalized();
            int nv = m.VertexCount;
            var d = new double[nv];
            double tiny = Math.Max(1e-12, m.AverageEdgeLength * 1e-9);
            for (int v = 0; v < nv; v++)
            {
                double s = (m.Vertices[v] - origin).Dot(n);
                d[v] = Math.Abs(s) < tiny ? tiny : s;
            }

            var pointOf = new Dictionary<long, int>();
            var pts = new List<Vec3>();
            var adj = new List<List<int>>();
            int EdgePoint(int a, int b)
            {
                if (a > b) { int t = a; a = b; b = t; }
                long key = (long)a * nv + b;
                if (pointOf.TryGetValue(key, out int id)) return id;
                double f = d[a] / (d[a] - d[b]);
                id = pts.Count;
                pts.Add(m.Vertices[a] + (m.Vertices[b] - m.Vertices[a]) * f);
                adj.Add(new List<int>(2));
                pointOf[key] = id;
                return id;
            }

            var cut = new int[2];
            foreach (int t in tris ?? Enumerable.Range(0, m.TriangleCount))
            {
                if ((t & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                int a = m.V(t, 0), b = m.V(t, 1), c = m.V(t, 2);
                bool sa = d[a] > 0, sb = d[b] > 0, sc = d[c] > 0;
                if (sa == sb && sb == sc) continue;
                int k = 0;
                if (sa != sb) cut[k++] = EdgePoint(a, b);
                if (sb != sc) cut[k++] = EdgePoint(b, c);
                if (sc != sa && k < 2) cut[k++] = EdgePoint(c, a);
                if (k != 2 || cut[0] == cut[1]) continue;
                adj[cut[0]].Add(cut[1]);
                adj[cut[1]].Add(cut[0]);
            }

            // Chain: open pieces start at their ends (degree 1), the rest are loops.
            var used = new bool[pts.Count];
            var result = new List<SectionPolyline>();
            void Walk(int start)
            {
                var poly = new SectionPolyline();
                int prev = -1, cur = start;
                while (cur >= 0 && !used[cur])
                {
                    used[cur] = true;
                    poly.Points.Add(pts[cur]);
                    int next = -1;
                    foreach (int w in adj[cur]) if (w != prev && !used[w]) { next = w; break; }
                    prev = cur;
                    cur = next;
                }
                poly.Closed = poly.Points.Count > 2 && adj[prev].Contains(start) && adj[start].Count >= 2;
                if (poly.Points.Count >= 2) result.Add(poly);
            }
            for (int i = 0; i < pts.Count; i++) if (!used[i] && adj[i].Count == 1) Walk(i);
            for (int i = 0; i < pts.Count; i++) if (!used[i] && adj[i].Count > 0) Walk(i);
            return result;
        }
    }

    /// <summary>Result of <see cref="MeshSketch.Build"/>: the section polylines and their lines-and-arcs fits.</summary>
    public sealed class MeshSketchResult
    {
        public Frame Plane { get; set; }
        public List<SectionPolyline> Sections { get; } = new List<SectionPolyline>();
        /// <summary>One profile per kept section polyline (same plane; sketch coordinates = plane X/Y).</summary>
        public List<SketchProfile> Loops { get; } = new List<SketchProfile>();
        public int Lines => Loops.Sum(l => l.Segments.Count(s => !s.IsArc));
        public int Arcs => Loops.Sum(l => l.Segments.Count(s => s.IsArc));
        public double MaxDeviation => Loops.Count == 0 ? 0 : Loops.Max(l => l.MaxDeviation);

        public string Describe(double k, string unit)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return $"{Loops.Count} section(s): {Lines} line(s), {Arcs} arc(s); deviation max " +
                   (MaxDeviation * k).ToString("0.###", inv) + " " + unit;
        }
    }

    /// <summary>
    /// Mesh sketch: section the scan with a plane and fit the fewest lines and arcs within tolerance to each
    /// section polyline (Design X calls the second step "Auto Sketch").
    /// </summary>
    public static class MeshSketch
    {
        /// <param name="minLength">Section pieces shorter than this are ignored (scan noise, tiny islands).</param>
        public static MeshSketchResult Build(TriMesh m, Frame plane, double tolerance, double minLength = 0,
                                             IEnumerable<int> tris = null, CancellationToken ct = default,
                                             IProgress<double> progress = null)
        {
            var res = new MeshSketchResult { Plane = plane };
            if (minLength <= 0) minLength = Math.Max(10 * m.AverageEdgeLength, 20 * tolerance);
            var polys = MeshSection.Slice(m, plane.Origin, plane.ZAxis, tris, ct)
                .Where(p => p.Length >= minLength).OrderByDescending(p => p.Length).ToList();
            for (int i = 0; i < polys.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)i / polys.Count);
                var poly = polys[i];
                // Scans are often not welded at a seam: a piece whose ends nearly meet is a loop.
                if (!poly.Closed && poly.Points.Count > 8 &&
                    poly.Points[0].DistanceTo(poly.Points[poly.Points.Count - 1]) < 3 * m.AverageEdgeLength)
                    poly.Closed = true;
                var pts = Thin(poly.Points.Select(p => { var l = plane.ToLocal(p); return (l.X, l.Y); }).ToList(),
                               0.25 * m.AverageEdgeLength);
                if (pts.Count < 2) continue;
                List<ProfileSegment> segs;
                double max, rms;
                try { segs = SketchProfiles.FitPolyline(pts, poly.Closed && pts.Count > 3, tolerance, out max, out rms); }
                catch (InvalidOperationException) { continue; }
                if (segs.Count == 0) continue;
                res.Sections.Add(poly);
                res.Loops.Add(new SketchProfile { Plane = plane, Segments = segs, Closed = poly.Closed, MaxDeviation = max, RmsDeviation = rms });
            }
            progress?.Report(1);
            return res;
        }

        /// <summary>Drops points closer than <paramref name="minStep"/> to the previous kept point.</summary>
        private static List<(double x, double y)> Thin(List<(double x, double y)> p, double minStep)
        {
            var o = new List<(double x, double y)>(p.Count);
            double m2 = minStep * minStep;
            foreach (var q in p)
            {
                if (o.Count > 0)
                {
                    var l = o[o.Count - 1];
                    double dx = q.x - l.x, dy = q.y - l.y;
                    if (dx * dx + dy * dy < m2) continue;
                }
                o.Add(q);
            }
            return o;
        }
    }
}
