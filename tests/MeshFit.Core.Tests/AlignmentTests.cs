using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Alignment;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;
using Xunit;

namespace MeshFit.Core.Tests
{
    public class AlignmentTests
    {
        private static readonly RigidTransform Misplace =
            RigidTransform.Translation(new Vec3(120, -40, 75)).After(RigidTransform.Rotation(new Vec3(0.3, 1, 0.2), 0.7));

        private static void Near(Vec3 a, Vec3 b, double tol) => Assert.True(a.DistanceTo(b) < tol, $"{a} vs {b}");

        [Fact]
        public void Transform_ParseInverseCompose()
        {
            var x = Misplace;
            var back = RigidTransform.Parse(x.ToString());
            var p = new Vec3(1, 2, 3);
            Near(back.Apply(p), x.Apply(p), 1e-9);
            Near(x.Inverse().Apply(x.Apply(p)), p, 1e-9);
            Near(x.After(x.Inverse()).Apply(p), p, 1e-9);
        }

        [Fact]
        public void ThreePlaneMates_RecoverTheOriginalPlacement()
        {
            // Box corner at the origin; its three faces were scanned in a misplaced position.
            var floor = AlignFeature.Plane("floor", Misplace.Apply(Vec3.Zero), Misplace.ApplyVector(Vec3.UnitZ));
            var side = AlignFeature.Plane("side", Misplace.Apply(new Vec3(0, 5, 5)), Misplace.ApplyVector(Vec3.UnitX));
            var back = AlignFeature.Plane("back", Misplace.Apply(new Vec3(5, 0, 5)), Misplace.ApplyVector(Vec3.UnitY));
            var refs = AlignFeature.PartReferences();
            var mates = new List<AlignMate>
            {
                new AlignMate { Source = floor, Target = refs[0] },  // → Front (XY)
                new AlignMate { Source = side, Target = refs[2] },   // → Right (YZ)
                new AlignMate { Source = back, Target = refs[1] },   // → Top (XZ)
            };
            var x = MeshAlignment.Solve(mates);
            var p = new Vec3(3, 4, 5);
            Near(x.Apply(Misplace.Apply(p)), p, 1e-6);
        }

        [Fact]
        public void AxisAndPlaneMates_AlignCylinderToZAndFaceToFront()
        {
            var axis = AlignFeature.Axis("bore", Misplace.Apply(new Vec3(0, 0, 10)), Misplace.ApplyVector(Vec3.UnitZ));
            var face = AlignFeature.Plane("face", Misplace.Apply(new Vec3(7, 3, 0)), Misplace.ApplyVector(Vec3.UnitZ));
            var refs = AlignFeature.PartReferences();
            var x = MeshAlignment.Solve(new[]
            {
                new AlignMate { Source = axis, Target = refs[5] },  // Z axis
                new AlignMate { Source = face, Target = refs[0] },  // Front plane
            });
            // Points on the original axis land on the Z axis, the face lands on z = 0.
            var a = x.Apply(Misplace.Apply(new Vec3(0, 0, 25)));
            Assert.True(Math.Sqrt(a.X * a.X + a.Y * a.Y) < 1e-6);
            Assert.InRange(x.Apply(Misplace.Apply(new Vec3(4, -2, 0))).Z, -1e-6, 1e-6);
        }

        [Fact]
        public void Flip_ReversesThePlanePairing()
        {
            var face = AlignFeature.Plane("face", new Vec3(0, 0, 5), Vec3.UnitZ);
            var refs = AlignFeature.PartReferences();
            var x = MeshAlignment.Solve(new[] { new AlignMate { Source = face, Target = refs[0], Flip = true } });
            Assert.True(x.ApplyVector(Vec3.UnitZ).Dot(Vec3.UnitZ) < -0.999);
            Assert.InRange(x.Apply(new Vec3(0, 0, 5)).Z, -1e-6, 1e-6);
        }

        [Fact]
        public void SymmetryPlane_IsFoundOnAMisplacedSymmetricBody()
        {
            // Symmetric about x = 0: a box with two bumps at ±x, plus an asymmetric-looking feature mirrored.
            Vec3 F(double s, double t)
            {
                double x = s, y = t;
                double z = 10 * Math.Exp(-((x - 30) * (x - 30) + y * y) / 200) + 10 * Math.Exp(-((x + 30) * (x + 30) + y * y) / 200)
                         + 6 * Math.Exp(-((Math.Abs(x) - 10) * (Math.Abs(x) - 10) + (y - 25) * (y - 25)) / 50);
                return new Vec3(x, y, z);
            }
            var m = TestMeshes.Grid(F, -60, 60, -40, 40, 120, 80).Transformed(Misplace);
            var plane = MeshAlignment.SymmetryPlane(m, out double rms);
            var trueN = Misplace.ApplyVector(Vec3.UnitX);
            Assert.True(Math.Abs(plane.Direction.Dot(trueN)) > Math.Cos(1 * Math.PI / 180), plane.Direction.ToString());
            Assert.InRange(Math.Abs((plane.Point - Misplace.Apply(Vec3.Zero)).Dot(trueN)), 0, 0.3);
            Assert.True(rms < 0.5, rms.ToString());
        }

        [Fact]
        public void PrincipalAxes_PutsLongestDirectionOnX()
        {
            var m = TestMeshes.Box(1, 4).Transformed(new RigidTransform(new double[,] { { 4, 0, 0 }, { 0, 2, 0 }, { 0, 0, 1 } }, Vec3.Zero));
            // (scaled box: 4 x 2 x 1) then misplaced
            var mm = m.Transformed(Misplace);
            var x = MeshAlignment.PrincipalAxes(mm);
            var aligned = mm.Transformed(x);
            var b = aligned.Bounds;
            Assert.InRange(b.Max.X - b.Min.X, 3.99, 4.01);
            Assert.InRange(b.Max.Y - b.Min.Y, 1.99, 2.01);
            Assert.InRange(b.Max.Z - b.Min.Z, 0.99, 1.01);
            Near(MeshAlignment.AreaCentroid(aligned), Vec3.Zero, 1e-6);
        }

        [Fact]
        public void FittedSurfaces_MoveWithTheMesh()
        {
            var f = Frame.FromAxis(new Vec3(1, 2, 3), new Vec3(0.2, 0.5, 1).Normalized());
            var surfaces = new FittedSurface[]
            {
                new PlaneSurface(f.Origin, f.ZAxis, f.XAxis), new CylinderSurface(f.Origin, f.ZAxis, 50, f.XAxis),
                new ConeSurface(f.Origin, f.ZAxis, 50, 0.3, f.XAxis), new SphereSurface(f.Origin, 70, f),
                new TorusSurface(f.Origin, f.ZAxis, 100, 25, f.XAxis),
            };
            foreach (var s in surfaces)
            {
                var a = (AnalyticSurface)s;
                var moved = s.Transformed(Misplace);
                for (int i = 0; i < 10; i++)
                {
                    var p = a.PointAt(0.3 * i - 1, 0.1 * i - 0.4);
                    Assert.True(Math.Abs(moved.Distance(Misplace.Apply(p))) < 1e-9, s.Kind.ToString());
                }
            }
            var mesh = TestMeshes.Grid((u, v) => new Vec3(u, v, 3 * Math.Sin(u / 20)), 0, 100, 0, 60, 40, 24);
            var ff = SurfaceFitter.Fit(mesh, TestMeshes.All(mesh), new FitOptions { Request = FitRequest.Freeform, Tolerance = 0.01 }).Surface;
            var ffm = ff.Transformed(Misplace);
            Assert.True(Math.Abs(ffm.Distance(Misplace.Apply(new Vec3(50, 30, 3 * Math.Sin(2.5))))) < 0.01);
        }
    }
}
