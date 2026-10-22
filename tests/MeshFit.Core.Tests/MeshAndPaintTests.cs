using System;
using System.IO;
using System.Linq;
using System.Text;
using MeshFit.Core.Export;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;
using MeshFit.Core.Project;
using MeshFit.Core.Selection;
using Xunit;

namespace MeshFit.Core.Tests
{
    public class MeshAndPaintTests
    {
        [Fact]
        public void Stl_RoundTrip_WeldsVertices()
        {
            var box = TestMeshes.Box(10, 4);
            var ms = new MemoryStream();
            MeshIO.WriteBinaryStl(box, ms);
            ms.Position = 0;
            var back = MeshIO.ReadStl(ms);
            Assert.Equal(box.TriangleCount, back.TriangleCount);
            Assert.Equal(box.VertexCount, back.VertexCount);
            Assert.All(back.Neighbors, n => Assert.True(n >= 0)); // closed, manifold
        }

        [Fact]
        public void AsciiStl_ObjAndPly_AreRead()
        {
            const string stl = "solid t\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\n" +
                               "facet normal 0 0 1\nouter loop\nvertex 1 0 0\nvertex 1 1 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid t\n";
            var m1 = MeshIO.ReadStl(new MemoryStream(Encoding.ASCII.GetBytes(stl)));
            Assert.Equal(2, m1.TriangleCount); Assert.Equal(4, m1.VertexCount);

            const string obj = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1/1/1 2/2/2 3/3/3 4/4/4\n";
            var m2 = MeshIO.ReadObj(new MemoryStream(Encoding.ASCII.GetBytes(obj)));
            Assert.Equal(2, m2.TriangleCount);

            const string ply = "ply\nformat ascii 1.0\nelement vertex 4\nproperty float x\nproperty float y\nproperty float z\n" +
                               "element face 1\nproperty list uchar int vertex_indices\nend_header\n0 0 0\n1 0 0\n1 1 0\n0 1 0\n4 0 1 2 3\n";
            var m3 = MeshIO.ReadPly(new MemoryStream(Encoding.ASCII.GetBytes(ply)));
            Assert.Equal(2, m3.TriangleCount);
        }

        [Fact]
        public void BinaryPly_IsRead()
        {
            var ms = new MemoryStream();
            var hdr = "ply\nformat binary_little_endian 1.0\nelement vertex 3\nproperty double x\nproperty double y\nproperty double z\n" +
                      "element face 1\nproperty list uchar uint vertex_indices\nend_header\n";
            ms.Write(Encoding.ASCII.GetBytes(hdr));
            var w = new BinaryWriter(ms);
            foreach (var v in new[] { 0.0, 0, 0, 1, 0, 0, 0, 1, 0 }) w.Write(v);
            w.Write((byte)3); w.Write(0u); w.Write(1u); w.Write(2u);
            ms.Position = 0;
            var m = MeshIO.ReadPly(ms);
            Assert.Equal(1, m.TriangleCount);
            Assert.Equal(1.0, m.Vertices[1].X);
        }

        [Fact]
        public void Bvh_RayCastAndNearest()
        {
            var box = TestMeshes.Box(10, 5);
            int t = box.Bvh.RayCast(new Vec3(3, 4, 50), new Vec3(0, 0, -1), out double th, 0);
            Assert.True(t >= 0);
            Assert.InRange(th, 40 - 1e-9, 40 + 1e-9); // hits the top face z = 10
            int n = box.Bvh.Nearest(new Vec3(5, 5, -3), out var c);
            Assert.True(n >= 0);
            Assert.InRange(c.Z, -1e-9, 1e-9);
            Assert.Equal(2, box.Bvh.LineHits(new Vec3(3.3, 4.1, 50), new Vec3(0, 0, 1)).Count);
        }

        [Fact]
        public void GrowSmooth_StopsAtSharpEdges_AndUndoRedo()
        {
            var box = TestMeshes.Box(10, 6);
            var set = new RegionSet(box);
            var r = set.AddRegion();
            int seed = box.Bvh.RayCast(new Vec3(3, 4, 50), new Vec3(0, 0, -1), out _, 0);
            set.BeginStroke();
            int n = PaintTools.GrowSmooth(set, seed, r.Id, 30);
            set.EndStroke();
            Assert.Equal(6 * 6 * 2, n);              // exactly the top face
            Assert.Equal(72, r.TriangleCount);
            set.Undo();
            Assert.Equal(0, r.TriangleCount);
            set.Redo();
            Assert.Equal(72, r.TriangleCount);
        }

        [Fact]
        public void Brush_PaintsOnlyNearbyConnectedTriangles()
        {
            var m = TestMeshes.Grid((s, t) => new Vec3(s, t, 0), 0, 100, 0, 100, 50, 50);
            var set = new RegionSet(m);
            var r = set.AddRegion();
            int seed = m.Bvh.Nearest(new Vec3(50, 50, 0), out var hit);
            set.BeginStroke();
            PaintTools.Brush(set, seed, hit, 10, r.Id);
            set.EndStroke();
            foreach (int t in set.TrianglesOf(r.Id)) Assert.True(m.Centroids[t].DistanceTo(hit) <= 10 + 1e-9 || t == seed);
            // ~ π r² / triangle area (2 tris per 2x2 cell)
            Assert.InRange(r.TriangleCount, 120, 200);
        }

        [Fact]
        public void GrowToSurface_ExtendsToTheCylinderBoundary()
        {
            // Plane z=0 for x<0 joined tangentially to a cylinder for x>0: the cylinder fit must not leak onto the plane.
            const double R = 100;
            Vec3 F(double s, double t) => s < 0 ? new Vec3(s, t, 0) : new Vec3(R * Math.Sin(s / R), t, R - R * Math.Cos(s / R));
            var m = TestMeshes.Grid(F, -100, 150, 0, 100, 100, 40);
            var set = new RegionSet(m);
            var r = set.AddRegion();
            set.BeginStroke();
            for (int t = 0; t < m.TriangleCount; t++)
            {
                var c = m.Centroids[t];
                if (c.X > 60 && c.X < 80 && c.Y > 40 && c.Y < 60) set.Assign(t, r.Id);
            }
            set.EndStroke();
            var fit = SurfaceFitter.Fit(m, set.TrianglesOf(r.Id), new FitOptions { Request = FitRequest.Cylinder, Tolerance = 0.01 });
            set.BeginStroke();
            PaintTools.GrowToSurface(set, r.Id, fit.Surface, 0.02);
            set.EndStroke();
            var tris = set.TriangleListOf(r.Id);
            Assert.True(tris.Count > 1500, tris.Count.ToString());
            Assert.True(tris.All(t => m.Centroids[t].X > -3), "leaked onto the plane");
        }

        [Fact]
        public void AutoSegment_SplitsBoxIntoSixFaces()
        {
            var set = new RegionSet(TestMeshes.Box(10, 4));
            var regions = PaintTools.AutoSegment(set, 20, 4);
            Assert.Equal(6, regions.Count);
            Assert.All(regions, r => Assert.Equal(32, r.TriangleCount));
        }

        [Fact]
        public void Project_RoundTrip()
        {
            var box = TestMeshes.Box(10, 3);
            var set = new RegionSet(box);
            PaintTools.AutoSegment(set, 20, 1);
            set.Regions[2].Request = FitRequest.Plane;
            set.Regions[2].Name = "Roof panel";
            set.Regions[2].FeatureName = "Roof panel surface";
            set.Regions[2].SurfaceHidden = true;
            string dir = Path.Combine(Path.GetTempPath(), "meshfit-test-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                string meshPath = Path.Combine(dir, "box.stl");
                using (var fs = File.Create(meshPath)) MeshIO.WriteBinaryStl(box, fs);
                string proj = Path.Combine(dir, "box.meshfit");
                ProjectFile.Save(proj, set, meshPath, 0.001);
                var h = ProjectFile.ReadHeader(proj);
                Assert.Equal(meshPath, h.MeshPath);
                Assert.Equal(0.001, h.Scale);
                var mesh2 = MeshIO.Load(h.MeshPath);
                var set2 = ProjectFile.Load(proj, mesh2);
                Assert.Equal(6, set2.Regions.Count);
                Assert.Equal("Roof panel", set2.Regions[2].Name);
                Assert.Equal(FitRequest.Plane, set2.Regions[2].Request);
                Assert.Equal("Roof panel surface", set2.Regions[2].FeatureName);
                Assert.True(set2.Regions[2].SurfaceHidden);
                Assert.Null(set2.Regions[1].FeatureName);
                Assert.Equal(set.Owners, set2.Owners);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Iges_IsWellFormed()
        {
            var w = new IgesWriter { ToMillimetres = 1000 };
            var cyl = new CylinderSurface(Vec3.Zero, Vec3.UnitZ, 0.05, Vec3.UnitX);
            w.Add(NurbsConversion.ToNurbs(cyl, new Extents { S0 = 0, S1 = 2, T0 = 0, T1 = 0.1 }), "Fender arch");
            var m = TestMeshes.Grid((s, t) => new Vec3(s, t, 0.01 * Math.Sin(s * 20)), -0.1, 0.1, -0.1, 0.1, 20, 20);
            var ff = SurfaceFitter.Fit(m, TestMeshes.All(m), new FitOptions { Request = FitRequest.Freeform, Tolerance = 1e-5 });
            w.Add(NurbsConversion.ToNurbs(ff.Surface, default), "Hood");
            var sw = new StringWriter();
            w.Write(sw, "test.igs");
            var lines = sw.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.All(lines, l => Assert.Equal(80, l.Length));
            Assert.Equal('T', lines.Last()[72]);
            var d = lines.Where(l => l[72] == 'D').ToList();
            Assert.Equal(4, d.Count);
            Assert.StartsWith("     128", d[0]);
            Assert.Contains("FENDERAR", d[1]);
            // Parameter data of the first entity ends with ';' and points back to DE 1.
            var p1 = lines.Where(l => l[72] == 'P' && l.Substring(64, 8).Trim() == "1").ToList();
            Assert.EndsWith(";", p1.Last().Substring(0, 64).TrimEnd());
            var p1Text = string.Concat(p1.Select(l => l.Substring(0, 64)));
            var toks = p1Text.Replace(" ", "").TrimEnd(';').Split(',');
            Assert.Equal("128", toks[0]);
            int k1 = int.Parse(toks[1]), k2 = int.Parse(toks[2]), m1 = int.Parse(toks[3]), m2 = int.Parse(toks[4]);
            int expected = 10 + (k1 + m1 + 2) + (k2 + m2 + 2) + (k1 + 1) * (k2 + 1) * 4 + 4;
            Assert.Equal(expected, toks.Length);
        }

        [Fact]
        public void FillIslands_FillsEnclosedHolesOnly()
        {
            // 40x40 grid plane; region = everything with x < 60, leaving an enclosed 10x10 hole and an
            // unpainted band on the right that touches the open mesh border.
            var m = TestMeshes.Grid((s, t) => new Vec3(s, t, 0), 0, 100, 0, 100, 40, 40);
            var set = new RegionSet(m);
            var r = set.AddRegion();
            var other = set.AddRegion();
            set.BeginStroke();
            for (int t = 0; t < m.TriangleCount; t++)
            {
                var c = m.Centroids[t];
                bool hole = c.X > 20 && c.X < 30 && c.Y > 20 && c.Y < 30;
                bool otherIsland = c.X > 40 && c.X < 45 && c.Y > 40 && c.Y < 45;
                if (c.X < 60 && !hole && !otherIsland) set.Assign(t, r.Id);
                if (otherIsland) set.Assign(t, other.Id);
            }
            set.EndStroke();
            int holeTris = Enumerable.Range(0, m.TriangleCount).Count(t => { var c = m.Centroids[t]; return c.X > 20 && c.X < 30 && c.Y > 20 && c.Y < 30; });
            int before = set.TriangleListOf(r.Id).Count;

            set.BeginStroke();
            int added = PaintTools.FillIslands(set, r.Id);
            set.EndStroke();

            Assert.Equal(holeTris, added);
            Assert.Equal(before + holeTris, set.TriangleListOf(r.Id).Count);
            Assert.True(set.TriangleListOf(other.Id).Count > 0);                       // other region untouched
            Assert.True(set.TrianglesOf(r.Id).All(t => m.Centroids[t].X < 60));        // border band not filled

            // Size limit: an island larger than the limit stays empty.
            set.Undo();
            Assert.Equal(0, PaintTools.FillIslands(set, r.Id, maxIslandTriangles: holeTris - 1));
        }

        [Fact]
        public void Undo_RestoresDeletedRegions_AndRemovesAutoSegmentRegions()
        {
            var set = new RegionSet(TestMeshes.Box(10, 4));
            var regions = PaintTools.AutoSegment(set, 20, 4);
            Assert.Equal("Auto segment", set.UndoLabel);
            var victim = regions[2];
            set.RemoveRegion(victim);
            Assert.Equal(5, set.Regions.Count);
            Assert.Equal("Delete " + victim.Name, set.UndoLabel);
            set.Undo();
            Assert.Equal(6, set.Regions.Count);
            Assert.Same(victim, set.Regions[2]);
            Assert.Equal(32, victim.TriangleCount);
            set.Undo();
            Assert.Empty(set.Regions);
            Assert.All(set.Owners, o => Assert.Equal(RegionSet.None, o));
            set.Redo(); set.Redo();
            Assert.Equal(5, set.Regions.Count);
            Assert.DoesNotContain(victim, set.Regions);
        }
    }
}
