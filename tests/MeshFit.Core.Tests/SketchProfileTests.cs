using System;
using System.Linq;
using MeshFit.Core.Analysis;
using MeshFit.Core.Geometry;
using Xunit;

namespace MeshFit.Core.Tests
{
    public class SketchProfileTests
    {
        [Fact]
        public void Extrusion_FloorFilletWall_GivesLineArcLine()
        {
            const double R = 5, arc = Math.PI / 2 * R;
            Vec3 P(double u, double y)
            {
                if (u < 0) return new Vec3(u, y, 0);
                if (u <= arc) { double a = u / R; return new Vec3(R * Math.Sin(a), y, R * (1 - Math.Cos(a))); }
                return new Vec3(R, y, R + (u - arc));
            }
            var m = TestMeshes.Grid(P, -30, arc + 40, 0, 60, 160, 60, noise: 0.003);
            var prof = SketchProfiles.Extrusion(m, TestMeshes.All(m), tolerance: 0.05);
            Assert.True(Math.Abs(Math.Abs(prof.Direction.Y) - 1) < 1e-9, prof.Direction.ToString());   // snapped to Y
            Assert.InRange(prof.Depth, 60, 67);
            Assert.Equal(3, prof.Segments.Count);
            Assert.False(prof.Segments[0].IsArc);
            Assert.True(prof.Segments[1].IsArc, string.Join(", ", prof.Segments));
            Assert.False(prof.Segments[2].IsArc);
            Assert.InRange(prof.Segments[1].Radius, 4.85, 5.15);
            Assert.True(prof.MaxDeviation < 0.06, prof.MaxDeviation.ToString());
            // The arc centre maps back to the fillet axis (x = 0, z = 5).
            var c = prof.To3D(prof.Segments[1].Cx, prof.Segments[1].Cy);
            Assert.True(Math.Abs(c.X) < 0.1 && Math.Abs(c.Z - 5) < 0.1, c.ToString());
        }

        [Fact]
        public void Revolution_CylinderArcPlaneProfile_IsRecovered()
        {
            // Profile (radius, height): cylinder R50 from h 0..20, tangent arc R10, then flat annulus out to R90.
            var axis = new Vec3(0.2, 0.1, 1).Normalized();
            var f = Frame.FromAxis(new Vec3(10, -5, 3), axis);
            const double Rc = 10;
            double l1 = 20, l2 = Math.PI / 2 * Rc, l3 = 20;
            (double rho, double h) Prof(double u)
            {
                if (u < l1) return (50, u);
                if (u < l1 + l2) { double a = (u - l1) / Rc; return (60 - Rc * Math.Cos(a), 20 + Rc * Math.Sin(a)); }
                return (60 + (u - l1 - l2), 30);
            }
            Vec3 S(double ang, double u) { var (r, h) = Prof(u); return f.ToWorld(r * Math.Cos(ang), r * Math.Sin(ang), h); }
            double sweep = 120 * Math.PI / 180;
            var m = TestMeshes.Grid(S, 0, sweep, 0, l1 + l2 + l3, 120, 120, noise: 0.003);
            var prof = SketchProfiles.Revolution(m, TestMeshes.All(m), tolerance: 0.05);
            Assert.True(Math.Abs(Math.Abs(prof.Axis.Dot(axis)) - 1) < 1e-4, prof.Axis.ToString());
            Assert.InRange(prof.Sweep * 180 / Math.PI, 120, 126);
            Assert.Equal(3, prof.Segments.Count);
            Assert.True(prof.Segments[1].IsArc, string.Join(", ", prof.Segments));
            Assert.InRange(prof.Segments[1].Radius, 9.8, 10.2);
            Assert.True(prof.MaxDeviation < 0.06, prof.MaxDeviation.ToString());
            // Every profile x (radius) is positive: the sketch lies on one side of the axis.
            Assert.All(prof.Segments, s => Assert.True(s.X0 > 0 && s.X1 > 0));
        }
    }
}
