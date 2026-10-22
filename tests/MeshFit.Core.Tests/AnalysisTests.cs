using System;
using System.Linq;
using MeshFit.Core.Analysis;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;
using MeshFit.Core.Selection;
using Xunit;

namespace MeshFit.Core.Tests
{
    public class AnalysisTests
    {
        /// <summary>Floor → quarter-round fillet of radius r(y) → wall, sampled by arc length u.</summary>
        private static TriMesh Bracket(Func<double, double> radius, double noise = 0.003)
        {
            Vec3 P(double u, double y)
            {
                double r = radius(y), arc = Math.PI / 2 * r;
                if (u < 0) return new Vec3(u, y, 0);
                if (u <= arc) { double a = u / r; return new Vec3(r * Math.Sin(a), y, r * (1 - Math.Cos(a))); }
                return new Vec3(r, y, r + (u - arc));
            }
            return TestMeshes.Grid(P, -30, 40, 0, 60, 200, 120, noise);
        }

        private static (TriMesh m, RegionSet set, Region band) SelectBand(Func<double, double> radius)
        {
            var m = Bracket(radius);
            var c = Curvature.Compute(m, Curvature.DefaultScale(m));
            var set = new RegionSet(m);
            var r = set.AddRegion();
            double r0 = radius(30);
            int seed = m.Bvh.Nearest(new Vec3(r0 * Math.Sin(Math.PI / 4), 30, r0 * (1 - Math.Cos(Math.PI / 4))), out _);
            FeatureTools.SelectFeatureBand(set, seed, r.Id, c, 30, true);
            set.UpdateCounts();
            return (m, set, r);
        }

        [Fact]
        public void Fillet_ConstantRadiusIsMeasured()
        {
            var (m, set, band) = SelectBand(_ => 5);
            var fa = FilletAnalysis.Analyze(m, set.TriangleListOf(band.Id));
            Assert.True(fa.Stations.Count >= 10);
            Assert.InRange(fa.MeanRadius, 4.9, 5.1);
            Assert.True(fa.IsConstant, fa.Report(1, "mm"));
            Assert.InRange(fa.Length, 50, 62);
            // Spine lies on the ball-centre line x = 0, z = 5 (y varies).
            Assert.All(fa.Stations, s => Assert.True(Math.Abs(s.Center.X) < 0.2 && Math.Abs(s.Center.Z - 5) < 0.2, s.Center.ToString()));
        }

        [Fact]
        public void Fillet_VariableRadiusIsTracked()
        {
            Func<double, double> R = y => 3 + 5 * y / 60;        // 3 → 8 along y
            var (m, set, band) = SelectBand(R);
            var fa = FilletAnalysis.Analyze(m, set.TriangleListOf(band.Id));
            Assert.False(fa.IsConstant, fa.Report(1, "mm"));
            // Each station's radius matches r(y) at its centre.
            Assert.All(fa.Stations.Where(s => s.Center.Y > 8 && s.Center.Y < 52),
                s => Assert.InRange(s.Radius, R(s.Center.Y) - 0.25, R(s.Center.Y) + 0.25));
            Assert.InRange(fa.MinRadius, 2.6, 4.0);
            Assert.InRange(fa.MaxRadius, 7.0, 8.4);
        }

        [Fact]
        public void BoundaryCurves_RectangleSplitsIntoFourStraightSides()
        {
            var m = TestMeshes.Grid((s, t) => new Vec3(s, t, 0), 0, 100, 0, 100, 50, 50);
            var mask = new bool[m.TriangleCount];
            for (int t = 0; t < mask.Length; t++) { var c = m.Centroids[t]; mask[t] = c.X > 20 && c.X < 80 && c.Y > 30 && c.Y < 70; }
            var curves = FeatureCurves.RegionBoundaries(m, mask);
            Assert.Equal(4, curves.Count);
            var lengths = curves.Select(c => c.Length).OrderBy(x => x).ToList();
            Assert.InRange(lengths[0], 30, 42); Assert.InRange(lengths[3], 50, 62);
            // Sides are straight after smoothing (points stay close to the chord).
            foreach (var c in curves)
            {
                var a = c.Points.First(); var b = c.Points.Last(); var d = (b - a).Normalized();
                double maxOff = c.Points.Skip(2).Take(c.Points.Count - 4).Max(p => ((p - a) - d * (p - a).Dot(d)).Length);
                Assert.True(maxOff < 1.5, maxOff.ToString()); // under one mesh edge (2 mm)
            }
        }

        [Fact]
        public void BoundaryCurves_FilletBandGivesTwoTangentLines()
        {
            var (m, set, band) = SelectBand(_ => 5);
            var mask = PaintTools.MaskOf(set, band.Id);
            var curves = FeatureCurves.RegionBoundaries(m, mask);   // scan border excluded → open chains
            Assert.True(curves.Count >= 2);
            Assert.All(curves.Take(2), c => Assert.InRange(c.Length, 50, 62));
        }

        [Fact]
        public void CadComparison_MeasuresOffsetAndCoverage()
        {
            var scan = TestMeshes.Grid((s, t) => new Vec3(s, t, 0), 0, 100, 0, 100, 50, 50, noise: 0.002);
            // CAD covers half the scan, offset 0.05 above it.
            var cad = TestMeshes.Grid((s, t) => new Vec3(s, t, 0.05), 0, 50, 0, 100, 10, 10);
            var cmp = CadComparison.Compute(scan, cad, maxDistance: 1.0, tolerance: 0.1);
            var st = cmp.Summarize("all");
            Assert.InRange(st.Rms, 0.045, 0.06);
            Assert.Equal(1.0, st.WithinTolerance);
            Assert.InRange(cmp.Coverage(), 0.45, 0.6);
        }
    }
}
