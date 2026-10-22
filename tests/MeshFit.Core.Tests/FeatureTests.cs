using System;
using System.Linq;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;
using MeshFit.Core.Selection;
using Xunit;

namespace MeshFit.Core.Tests
{
    public class FeatureTests
    {
        private const double R = 5;                     // fillet radius
        private static readonly double ArcLen = Math.PI / 2 * R;

        /// <summary>Floor (z=0, x&lt;0) → R5 tangent fillet → wall (x=R), sampled by arc length u.</summary>
        private static Vec3 Profile(double u, double y)
        {
            if (u < 0) return new Vec3(u, y, 0);
            if (u <= ArcLen) { double a = u / R; return new Vec3(R * Math.Sin(a), y, R * (1 - Math.Cos(a))); }
            return new Vec3(R, y, R + (u - ArcLen));
        }

        private static TriMesh Bracket() => TestMeshes.Grid(Profile, -40, ArcLen + 40, 0, 40, 170, 80, noise: 0.003);

        private static bool OnFloor(Vec3 c) => c.Z < 1e-3 && c.X < 0.1;
        private static bool OnFillet(Vec3 c) => c.X > 0.3 && c.Z > 0.05 && c.X < R - 0.05 && c.Z < R - 0.3;

        [Fact]
        public void Curvature_SeparatesFilletFromFlats()
        {
            var m = Bracket();
            var c = Curvature.Compute(m, Curvature.DefaultScale(m));
            var floor = Enumerable.Range(0, m.TriangleCount).Where(t => m.Centroids[t].X < -5).ToList();
            var fillet = Enumerable.Range(0, m.TriangleCount).Where(t => { var p = m.Centroids[t]; return p.X > 1.5 && p.X < 3.5 && p.Z < 3; }).ToList();
            Assert.True(c.MedianKMax(floor) < 0.02, c.MedianKMax(floor).ToString());
            Assert.InRange(c.MedianKMax(fillet), 0.15, 0.25);                // 1/R = 0.2
        }

        [Fact]
        public void SmartFill_LeaksOverTangentFillet_UnlessStoppedByCurvature()
        {
            var m = Bracket();
            var c = Curvature.Compute(m, Curvature.DefaultScale(m));
            int seed = m.Bvh.Nearest(new Vec3(-20, 20, 0), out _);

            var leak = new RegionSet(m);
            var r1 = leak.AddRegion();
            PaintTools.GrowSmooth(leak, seed, r1.Id, 8);
            Assert.Contains(leak.TrianglesOf(r1.Id), t => m.Centroids[t].Z > 10); // angle test alone climbs the wall

            var set = new RegionSet(m);
            var r = set.AddRegion();
            var stop = FeatureTools.IsFeature(c, FeatureTools.SeedBaseline(m, c, seed), 30);
            PaintTools.GrowSmooth(set, seed, r.Id, 8, 0, stop);
            var tris = set.TriangleListOf(r.Id);
            Assert.All(tris, t => Assert.True(m.Centroids[t].Z < 0.2, m.Centroids[t].ToString()));
            int floorTris = Enumerable.Range(0, m.TriangleCount).Count(t => m.Centroids[t].X < -1);
            Assert.True(tris.Count > 0.95 * floorTris);
        }

        [Fact]
        public void SelectFeatureBand_SelectsTheFillet()
        {
            var m = Bracket();
            var c = Curvature.Compute(m, Curvature.DefaultScale(m));
            var set = new RegionSet(m);
            var r = set.AddRegion();
            int seed = m.Bvh.Nearest(Profile(ArcLen / 2, 20), out _);
            FeatureTools.SelectFeatureBand(set, seed, r.Id, c, 30, true);
            var tris = set.TriangleListOf(r.Id);
            int filletTris = Enumerable.Range(0, m.TriangleCount).Count(t => OnFillet(m.Centroids[t]));
            Assert.True(tris.Count(t => OnFillet(m.Centroids[t])) > 0.95 * filletTris);
            Assert.All(tris, t => Assert.True(m.Centroids[t].X > -2.5 && m.Centroids[t].Z < R + 2.5));
        }

        [Fact]
        public void TrimFeatures_RemovesFilletRunOutAfterGrow()
        {
            var m = Bracket();
            var c = Curvature.Compute(m, Curvature.DefaultScale(m));
            var set = new RegionSet(m);
            var r = set.AddRegion();
            foreach (int t in Enumerable.Range(0, m.TriangleCount).Where(t => m.Centroids[t].X < -10)) set.Assign(t, r.Id);
            var fit = SurfaceFitter.Fit(m, set.TrianglesOf(r.Id), new FitOptions { Request = FitRequest.Plane, Tolerance = 0.1 });
            PaintTools.GrowToSurface(set, r.Id, fit.Surface, 0.1, 30);
            Assert.Contains(set.TrianglesOf(r.Id), t => m.Centroids[t].X > 0.4);   // crept into the fillet
            FeatureTools.TrimFeatures(set, r.Id, c, 30);
            Assert.DoesNotContain(set.TrianglesOf(r.Id), t => m.Centroids[t].X > 0.6);
            Assert.True(set.TriangleListOf(r.Id).Count > 0.9 * Enumerable.Range(0, m.TriangleCount).Count(t => m.Centroids[t].X < 0));
        }
    }
}
