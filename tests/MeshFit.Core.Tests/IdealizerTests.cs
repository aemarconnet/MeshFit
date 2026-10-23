using System;
using System.Linq;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;
using Xunit;

namespace MeshFit.Core.Tests
{
    /// <summary>Model units are mm here, so rounding steps are given in mm.</summary>
    public class IdealizerTests
    {
        private static IdealizeOptions Opt(double tol) => new IdealizeOptions
        {
            Tolerance = tol, LengthSteps = new[] { 1, 0.5, 0.1, 0.05, 0.01 }, ReportScale = 1, ReportUnit = "mm"
        };

        private static Vec3[] Points(TriMesh m) => m.Vertices;

        [Fact]
        public void Cylinder_SnapsToZ_AndRoundsRadiusAndPosition()
        {
            // True design: axis ∥ Z through (10, 20), R 25. Scan noise makes the raw fit slightly off.
            var m = TestMeshes.Grid((a, z) => new Vec3(10 + 25 * Math.Cos(a), 20 + 25 * Math.Sin(a), z), 0.2, 1.6, 0, 60, 60, 40, noise: 0.01);
            var fit = SurfaceFitter.Fit(m, TestMeshes.All(m), new FitOptions { Request = FitRequest.Cylinder, Tolerance = 0.05 });
            var r = Idealizer.Idealize(fit.Surface, Points(m), null, Opt(0.05));
            var c = Assert.IsType<CylinderSurface>(r.Surface);
            Assert.Equal(1.0, Math.Abs(c.Axis.Z), 12);
            Assert.Equal(25.0, c.Radius, 9);
            var foot = c.AxisPoint;
            Assert.Equal(10.0, foot.X, 6); Assert.Equal(20.0, foot.Y, 6);
            Assert.Contains("axis ∥ Z", r.Applied);
            Assert.True(r.Stats.P95 <= 0.05, string.Join("; ", r.Applied));
        }

        [Fact]
        public void Plane_SnapsNormalAndOffset()
        {
            var n = new Vec3(1, 0.004, -0.003).Normalized();
            var f = new Frame(new Vec3(50.004, 3, 3), n, Vec3.UnitY);
            var m = TestMeshes.Grid((u, v) => f.ToWorld(u, v, 0), -40, 40, -30, 30, 40, 30, noise: 0.005);
            var fit = SurfaceFitter.Fit(m, TestMeshes.All(m), new FitOptions { Request = FitRequest.Plane, Tolerance = 0.5 });
            var r = Idealizer.Idealize(fit.Surface, Points(m), null, Opt(0.5));
            var p = Assert.IsType<PlaneSurface>(r.Surface);
            Assert.Equal(1.0, Math.Abs(p.Normal.X), 12);
            Assert.Equal(50.0, Math.Abs(p.Origin.Dot(p.Normal)), 9);
        }

        [Fact]
        public void Fillet_IsMadeTangentToBothPlanes_WithRoundRadius()
        {
            const double R = 5;
            var floor = new PlaneSurface(Vec3.Zero, Vec3.UnitZ, Vec3.UnitX);
            var wall = new PlaneSurface(new Vec3(R, 0, 0), Vec3.UnitX, Vec3.UnitY);
            // Fillet band only (quarter arc), noisy.
            var m = TestMeshes.Grid((a, y) => new Vec3(R * Math.Sin(a), y, R * (1 - Math.Cos(a))), 0.05, Math.PI / 2 - 0.05, 0, 50, 30, 50, noise: 0.01);
            var fit = SurfaceFitter.Fit(m, TestMeshes.All(m), new FitOptions { Request = FitRequest.Cylinder, Tolerance = 0.05 });
            var raw = (CylinderSurface)fit.Surface;
            var others = new[] { ("Floor", (FittedSurface)floor), ("Wall", (FittedSurface)wall) };
            var r = Idealizer.Idealize(fit.Surface, Points(m), others, Opt(0.05));
            var c = Assert.IsType<CylinderSurface>(r.Surface);
            Assert.Contains(r.Applied, a => a.StartsWith("tangent to") && a.Contains("Floor") && a.Contains("Wall"));
            Assert.Equal(5.0, c.Radius, 9);
            Assert.Equal(R, Math.Abs(floor.Distance(c.AxisPoint)), 9);   // exactly tangent
            Assert.Equal(R, Math.Abs(wall.Distance(c.AxisPoint)), 9);
            Assert.True(Math.Abs(raw.Radius - 5) > 1e-6);                 // the raw fit was not exact
        }

        [Fact]
        public void SecondCylinder_BecomesCoaxial()
        {
            var main = new CylinderSurface(new Vec3(0, 0, 0), Vec3.UnitZ, 40, Vec3.UnitX);
            // Counterbore-like smaller cylinder whose scan axis is 0.03 off.
            var m = TestMeshes.Grid((a, z) => new Vec3(0.03 + 12.3 * Math.Cos(a), 12.3 * Math.Sin(a), z), 0, 3, 5, 25, 60, 20, noise: 0.005);
            var fit = SurfaceFitter.Fit(m, TestMeshes.All(m), new FitOptions { Request = FitRequest.Cylinder, Tolerance = 0.1 });
            var r = Idealizer.Idealize(fit.Surface, Points(m), new[] { ("Bore", (FittedSurface)main) }, Opt(0.1));
            var c = Assert.IsType<CylinderSurface>(r.Surface);
            Assert.Contains(r.Applied, a => a.Contains("coaxial with Bore") || a.Contains("axis at"));
            Assert.True(Math.Sqrt(c.AxisPoint.X * c.AxisPoint.X + c.AxisPoint.Y * c.AxisPoint.Y) < 1e-9);
        }

        [Fact]
        public void Rounding_StopsWhereTheDataDisagrees()
        {
            var m = TestMeshes.Grid((a, z) => new Vec3(25.37 * Math.Cos(a), 25.37 * Math.Sin(a), z), 0, 6.2, 0, 40, 120, 30, noise: 0.001);
            var fit = SurfaceFitter.Fit(m, TestMeshes.All(m), new FitOptions { Request = FitRequest.Cylinder, Tolerance = 0.01 });
            var r = Idealizer.Idealize(fit.Surface, Points(m), null, Opt(0.01));
            Assert.Equal(25.37, ((CylinderSurface)r.Surface).Radius, 9);
            Assert.True(r.Stats.P95 <= 0.01);
        }

        [Fact]
        public void Freeform_PrefersFewControlPointsOnNoisyData()
        {
            var rnd = new Random(5);
            var m = TestMeshes.Grid((s, t) => new Vec3(s, t, 8 * Math.Sin(s / 70) * Math.Cos(t / 60)), -100, 100, -80, 80, 80, 64, noise: 0.02);
            var fit = SurfaceFitter.Fit(m, TestMeshes.All(m), new FitOptions { Request = FitRequest.Freeform, Tolerance = 0.06 });
            var ff = Assert.IsType<FreeformSurface>(fit.Surface);
            // Smooth design + noise: a small control net is enough when judged on RMS / 95 %, not the noisiest point.
            Assert.True(ff.Nurbs.CountU * ff.Nurbs.CountV <= 120, $"{ff.Nurbs.CountU}x{ff.Nurbs.CountV}");
            Assert.True(fit.Stats.P95 <= 0.06, fit.Stats.P95.ToString());
        }
    }
}
