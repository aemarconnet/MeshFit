using System;
using System.Linq;
using MeshFit.Core.Export;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using Xunit;

namespace MeshFit.Core.Tests
{
    public class FittingTests
    {
        private static FitResult FitAll(MeshFit.Core.Mesh.TriMesh m, FitRequest req, double tol = 0.05)
            => SurfaceFitter.Fit(m, TestMeshes.All(m), new FitOptions { Request = req, Tolerance = tol });

        [Fact]
        public void Plane_IsRecovered()
        {
            var n = new Vec3(0.2, -0.3, 0.9).Normalized();
            var f = Frame.FromAxis(new Vec3(5, 6, 7), n);
            var m = TestMeshes.Grid((s, t) => f.ToWorld(s, t, 0), -50, 50, -20, 30, 30, 20, noise: 0.01);
            var r = FitAll(m, FitRequest.Plane);
            var p = Assert.IsType<PlaneSurface>(r.Surface);
            Assert.True(Math.Abs(Math.Abs(p.Normal.Dot(n)) - 1) < 1e-5);
            Assert.True(r.Stats.Rms < 0.02);
        }

        [Fact]
        public void Sphere_IsRecovered()
        {
            var c = new Vec3(10, -20, 30);
            var m = TestMeshes.Grid((s, t) => c + new Vec3(Math.Cos(t) * Math.Cos(s), Math.Cos(t) * Math.Sin(s), Math.Sin(t)) * 250,
                0, 0.8, 0.1, 0.6, 30, 30, noise: 0.02);
            var r = FitAll(m, FitRequest.Sphere);
            var sp = Assert.IsType<SphereSurface>(r.Surface);
            Assert.InRange(sp.Radius, 249.9, 250.1);
            Assert.True(sp.Center.DistanceTo(c) < 0.2);
        }

        [Fact]
        public void Cylinder_PartialArc_IsRecovered()
        {
            var axis = new Vec3(1, 2, 0.5).Normalized();
            var f = Frame.FromAxis(new Vec3(100, 0, -40), axis);
            const double R = 400;
            var m = TestMeshes.Grid((s, t) => f.ToWorld(R * Math.Cos(s), R * Math.Sin(s), t), 0.2, 0.9, -100, 150, 40, 30, noise: 0.02);
            var r = FitAll(m, FitRequest.Cylinder);
            var cy = Assert.IsType<CylinderSurface>(r.Surface);
            Assert.InRange(cy.Radius, R - 0.5, R + 0.5);
            Assert.True(Math.Abs(Math.Abs(cy.Axis.Dot(axis)) - 1) < 1e-5);
            Assert.True(r.Stats.Rms < 0.04);
        }

        [Fact]
        public void Cone_IsRecovered()
        {
            var axis = new Vec3(0, 0.3, 1).Normalized();
            var f = Frame.FromAxis(new Vec3(0, 0, 0), axis);
            double alpha = 15 * Math.PI / 180, R0 = 80;
            var m = TestMeshes.Grid((s, t) => f.ToWorld((R0 + t * Math.Tan(alpha)) * Math.Cos(s), (R0 + t * Math.Tan(alpha)) * Math.Sin(s), t),
                -0.8, 0.8, 0, 120, 40, 30, noise: 0.01);
            var r = FitAll(m, FitRequest.Cone);
            var co = Assert.IsType<ConeSurface>(r.Surface);
            Assert.InRange(co.HalfAngle * 180 / Math.PI, 14.9, 15.1);
            Assert.True(Math.Abs(co.Axis.Dot(axis) - 1) < 1e-4);
            Assert.True(r.Stats.Rms < 0.02);
        }

        [Fact]
        public void Torus_IsRecovered()
        {
            var f = Frame.FromAxis(new Vec3(5, 5, 5), new Vec3(0.3, 0.1, 1).Normalized());
            double Rm = 200, rm = 30;
            var m = TestMeshes.Grid((s, t) => f.ToWorld((Rm + rm * Math.Cos(t)) * Math.Cos(s), (Rm + rm * Math.Cos(t)) * Math.Sin(s), rm * Math.Sin(t)),
                0, 0.7, -1.2, 1.2, 40, 30, noise: 0.005);
            var r = FitAll(m, FitRequest.Torus);
            var to = Assert.IsType<TorusSurface>(r.Surface);
            Assert.InRange(to.MajorRadius, Rm - 0.5, Rm + 0.5);
            Assert.InRange(to.MinorRadius, rm - 0.2, rm + 0.2);
            Assert.True(r.Stats.Rms < 0.01);
        }

        [Fact]
        public void Freeform_FitsWavyPanelWithinTolerance()
        {
            var m = TestMeshes.Grid((s, t) => new Vec3(s, t, 20 * Math.Sin(s / 150) * Math.Cos(t / 90) + 0.0005 * s * t),
                -300, 300, -200, 200, 60, 40);
            var r = SurfaceFitter.Fit(m, TestMeshes.All(m), new FitOptions { Request = FitRequest.Freeform, Tolerance = 0.02 });
            var ff = Assert.IsType<FreeformSurface>(r.Surface);
            Assert.True(r.Stats.MaxAbs < 0.05, r.Stats.MaxAbs.ToString());
            Assert.True(ff.Nurbs.CountU >= 4);
        }

        [Theory]
        [InlineData("plane", SurfaceKind.Plane)]
        [InlineData("cylinder", SurfaceKind.Cylinder)]
        [InlineData("sphere", SurfaceKind.Sphere)]
        [InlineData("cone", SurfaceKind.Cone)]
        [InlineData("wave", SurfaceKind.Freeform)]
        public void Auto_PicksSimplestMatchingType(string shape, SurfaceKind expected)
        {
            Func<double, double, Vec3> f;
            switch (shape)
            {
                case "plane": f = (s, t) => new Vec3(s, t, 0.1 * s + 3); break;
                case "cylinder": f = (s, t) => new Vec3(300 * Math.Cos(s / 300), t, 300 * Math.Sin(s / 300)); break;
                case "sphere": f = (s, t) => new Vec3(0, 0, -400) + new Vec3(s, t, 400).Normalized() * 400; break;
                case "cone": f = (s, t) => { double r = 100 + 0.4 * t; return new Vec3(r * Math.Cos(s / 100), r * Math.Sin(s / 100), t); }; break;
                default: f = (s, t) => new Vec3(s, t, 10 * Math.Sin(s / 40) * Math.Sin(t / 30)); break;
            }
            var m = TestMeshes.Grid(f, -100, 100, -80, 80, 40, 32, noise: 0.003);
            var r = SurfaceFitter.Fit(m, TestMeshes.All(m), new FitOptions { Tolerance = 0.02 });
            Assert.Equal(expected, r.Surface.Kind);
        }

        [Fact]
        public void NurbsConversion_IsExactForAnalyticSurfaces()
        {
            var f = Frame.FromAxis(new Vec3(1, 2, 3), new Vec3(0.2, 0.5, 1).Normalized());
            var surfaces = new AnalyticSurface[]
            {
                new PlaneSurface(f.Origin, f.ZAxis, f.XAxis),
                new CylinderSurface(f.Origin, f.ZAxis, 50, f.XAxis),
                new ConeSurface(f.Origin, f.ZAxis, 50, 0.3, f.XAxis),
                new SphereSurface(f.Origin, 70, f),
                new TorusSurface(f.Origin, f.ZAxis, 100, 25, f.XAxis),
            };
            var ext = new Extents { S0 = -1.0, S1 = 2.5, T0 = -0.5, T1 = 1.2 };
            foreach (var s in surfaces)
            {
                var n = NurbsConversion.ToNurbs(s, ext);
                for (int i = 0; i <= 20; i++)
                    for (int j = 0; j <= 20; j++)
                    {
                        var p = n.Evaluate(n.UMin + (n.UMax - n.UMin) * i / 20, n.VMin + (n.VMax - n.VMin) * j / 20);
                        Assert.True(Math.Abs(s.Distance(p)) < 1e-9, $"{s.Kind}: {s.Distance(p)}");
                    }
                // Corners map to the requested extents.
                Assert.True(n.Evaluate(n.UMin, n.VMin).DistanceTo(s.PointAt(ext.S0, ext.T0)) < 1e-9, s.Kind.ToString());
                Assert.True(n.Evaluate(n.UMax, n.VMax).DistanceTo(s.PointAt(ext.S1, ext.T1)) < 1e-9, s.Kind.ToString());
            }
        }

        [Fact]
        public void Extents_HandleAngleWrapAround()
        {
            var cyl = new CylinderSurface(Vec3.Zero, Vec3.UnitZ, 10, Vec3.UnitX);
            // Points spanning 170°..190° (across the ±180° seam).
            var pts = Enumerable.Range(0, 21).Select(i => cyl.PointAt((170 + i) * Math.PI / 180, i)).ToArray();
            var e = SurfaceExtents.Compute(cyl, pts, 0);
            Assert.InRange(e.S1 - e.S0, 20 * Math.PI / 180 - 1e-9, 20 * Math.PI / 180 + 1e-9);
        }
    }
}
