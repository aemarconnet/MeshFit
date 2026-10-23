using System;
using System.Linq;
using MeshFit.Core.Analysis;
using MeshFit.Core.Geometry;
using Xunit;

namespace MeshFit.Core.Tests
{
    public class MeshSketchTests
    {
        [Fact]
        public void Slice_Box_GivesOneClosedSquare()
        {
            var box = TestMeshes.Box(10, 6);
            var polys = MeshSection.Slice(box, new Vec3(0, 0, 3.3), Vec3.UnitZ);
            Assert.Single(polys);
            Assert.True(polys[0].Closed);
            Assert.InRange(polys[0].Length, 39.99, 40.01);
            Assert.All(polys[0].Points, p => Assert.InRange(p.Z, 3.3 - 1e-9, 3.3 + 1e-9));
        }

        [Fact]
        public void MeshSketch_Box_GivesFourLines()
        {
            var box = TestMeshes.Box(10, 6);
            var plane = new Frame(new Vec3(0, 0, 4), Vec3.UnitZ, Vec3.UnitX);
            var res = MeshSketch.Build(box, plane, tolerance: 0.01);
            Assert.Single(res.Loops);
            var loop = res.Loops[0];
            Assert.True(loop.Closed);
            Assert.Equal(4, loop.Segments.Count);
            Assert.All(loop.Segments, s => Assert.False(s.IsArc));
            Assert.All(loop.Segments, s => Assert.InRange(Math.Sqrt((s.X1 - s.X0) * (s.X1 - s.X0) + (s.Y1 - s.Y0) * (s.Y1 - s.Y0)), 9.99, 10.01));
            Assert.True(res.MaxDeviation < 1e-6);
        }

        [Fact]
        public void MeshSketch_FloorFilletWall_GivesLineArcLineWithTangentJoints()
        {
            const double R = 5, arc = Math.PI / 2 * R;
            Vec3 P(double u, double y)
            {
                if (u < 0) return new Vec3(u, y, 0);
                if (u <= arc) { double a = u / R; return new Vec3(R * Math.Sin(a), y, R * (1 - Math.Cos(a))); }
                return new Vec3(R, y, R + (u - arc));
            }
            var m = TestMeshes.Grid(P, -30, arc + 40, 0, 60, 200, 60, noise: 0.003);
            var plane = new Frame(new Vec3(0, 30.2, 0), Vec3.UnitY, Vec3.UnitX);
            var res = MeshSketch.Build(m, plane, tolerance: 0.05);
            Assert.Single(res.Loops);
            var segs = res.Loops[0].Segments;
            Assert.Equal(3, segs.Count);
            Assert.False(segs[0].IsArc);
            Assert.True(segs[1].IsArc, string.Join(", ", segs));
            Assert.False(segs[2].IsArc);
            Assert.InRange(segs[1].Radius, 4.85, 5.15);
            Assert.True(SketchProfiles.JointAngle(segs[0], segs[1]) < 2 * Math.PI / 180);
            Assert.True(SketchProfiles.JointAngle(segs[1], segs[2]) < 2 * Math.PI / 180);
            Assert.True(res.MaxDeviation < 0.06, res.MaxDeviation.ToString());
        }

        [Fact]
        public void MeshSketch_TubeSection_IsAFullCircle()
        {
            var axis = Vec3.UnitZ;
            Vec3 S(double ang, double h) => new Vec3(3 + 12 * Math.Cos(ang), -2 + 12 * Math.Sin(ang), h);
            var m = TestMeshes.Grid(S, 0, 2 * Math.PI, 0, 30, 180, 30, noise: 0.002);
            var plane = new Frame(new Vec3(0, 0, 15.5), axis, Vec3.UnitX);
            var res = MeshSketch.Build(m, plane, tolerance: 0.03);
            Assert.Single(res.Loops);
            var segs = res.Loops[0].Segments;
            Assert.Equal(2, segs.Count);
            Assert.All(segs, s => Assert.True(s.IsArc));
            Assert.InRange(segs[0].Radius, 11.95, 12.05);
            Assert.InRange(segs[0].Cx, 2.95, 3.05);
            Assert.InRange(segs[0].Cy, -2.05, -1.95);
        }
    }
}
