using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MeshFit.Core.Export;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;
using MeshFit.Core.Project;
using MeshFit.Core.Selection;

namespace MeshFit.Cli
{
    /// <summary>
    /// Command line front end for batch work outside SOLIDWORKS:
    ///   meshfit fit &lt;mesh&gt; [--units mm] [--project p.meshfit | --segment 15] [--min 200] [--tol 0.2] [--out out.igs]
    ///   meshfit sample &lt;out.stl&gt;
    /// All lengths on the command line are in the mesh units.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                if (args.Length >= 2 && args[0] == "fit") return Fit(args[1], Options(args, 2));
                if (args.Length >= 2 && args[0] == "sample") { Sample(args[1]); return 0; }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
            Console.WriteLine("usage:\n  meshfit fit <mesh.stl|obj|ply> [--project regions.meshfit | --segment <edge deg>] [--min <tris>]\n" +
                              "              [--tol <deviation>] [--type auto|plane|cylinder|...] [--margin <fraction>] [--out surfaces.igs]\n" +
                              "  meshfit sample <out.stl>     write a synthetic body panel (mm) for trying the tools");
            return 2;
        }

        private static Dictionary<string, string> Options(string[] a, int start)
        {
            var o = new Dictionary<string, string>();
            for (int i = start; i < a.Length; i++)
                if (a[i].StartsWith("--")) o[a[i].Substring(2)] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
            return o;
        }

        private static double D(Dictionary<string, string> o, string k, double def) =>
            o.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;

        private static int Fit(string meshPath, Dictionary<string, string> o)
        {
            var mesh = MeshIO.Load(meshPath);
            Console.WriteLine($"{Path.GetFileName(meshPath)}: {mesh.TriangleCount} triangles, {mesh.VertexCount} vertices, size {mesh.Bounds.Diagonal:G5}");
            RegionSet set;
            if (o.TryGetValue("project", out var proj)) set = ProjectFile.Load(proj, mesh);
            else
            {
                set = new RegionSet(mesh);
                PaintTools.AutoSegment(set, D(o, "segment", 15), (int)D(o, "min", 200));
            }
            var opt = new FitOptions { Tolerance = D(o, "tol", 0.2) };
            opt.Freeform.Margin = D(o, "margin", 0.05);
            FitRequest forced = FitRequest.Auto;
            if (o.TryGetValue("type", out var type)) forced = (FitRequest)Enum.Parse(typeof(FitRequest), type, true);

            var writer = new IgesWriter { ToMillimetres = D(o, "to-mm", 1.0) };
            Console.WriteLine($"{"region",-12} {"tris",7}  {"type",-9} {"rms",9} {"max",9}  surface");
            foreach (var r in set.Regions.Where(r => r.TriangleCount > 0))
            {
                opt.Request = forced != FitRequest.Auto ? forced : r.Request;
                try
                {
                    var tris = set.TriangleListOf(r.Id);
                    var f = SurfaceFitter.Fit(mesh, tris, opt);
                    r.Fit = f;
                    Console.WriteLine($"{r.Name,-12} {r.TriangleCount,7}  {f.Surface.Kind,-9} {f.Stats.Rms,9:G3} {f.Stats.MaxAbs,9:G3}  {f.Surface.Describe(1, "")}" +
                                      (f.Warning != null ? "  ! " + f.Warning : ""));
                    Extents ext = default;
                    if (f.Surface is AnalyticSurface a)
                    {
                        var pts = mesh.VerticesOf(tris).Select(i => mesh.Vertices[i]).ToArray();
                        ext = SurfaceExtents.Compute(a, pts, D(o, "margin", 0.05));
                    }
                    writer.Add(NurbsConversion.ToNurbs(f.Surface, ext), r.Name);
                }
                catch (Exception ex) { Console.WriteLine($"{r.Name,-12} {r.TriangleCount,7}  failed: {ex.Message}"); }
            }
            if (o.TryGetValue("out", out var outPath))
            {
                writer.Save(outPath);
                Console.WriteLine("wrote " + outPath);
            }
            return 0;
        }

        /// <summary>
        /// Synthetic door-like panel in mm: flat flange, a cylindrical roll-over, a doubly curved
        /// freeform skin and a conical feature line, with a little scan noise.
        /// </summary>
        private static void Sample(string path)
        {
            var rnd = new Random(7);
            var verts = new List<Vec3>();
            var idx = new List<int>();
            void Patch(Func<double, double, Vec3> f, int nu, int nv)
            {
                int b = verts.Count;
                for (int i = 0; i <= nu; i++)
                    for (int j = 0; j <= nv; j++)
                    {
                        var p = f((double)i / nu, (double)j / nv);
                        verts.Add(p + new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5) * 0.02);
                    }
                for (int i = 0; i < nu; i++)
                    for (int j = 0; j < nv; j++)
                    {
                        int a = b + i * (nv + 1) + j, c = a + nv + 1;
                        idx.AddRange(new[] { a, c, c + 1, a, c + 1, a + 1 });
                    }
            }
            const double L = 900, H = 500, R = 25;
            // Freeform skin z = bulge(x, y) for y in [0, H]
            Func<double, double, double> skin = (x, y) => 30 * Math.Sin(Math.PI * x / L) * Math.Sin(Math.PI * y / (2 * H)) + 0.00002 * x * y;
            Patch((u, v) => new Vec3(u * L, v * H, skin(u * L, v * H)), 90, 50);
            // Cone (feature line) above the skin: radius grows along x
            Patch((u, v) => { double x = u * L, r = 60 + 0.05 * x, a = -Math.PI / 2 + v * Math.PI / 3; return new Vec3(x, H + 150 + r * Math.Cos(a), 40 + r * Math.Sin(a)); }, 90, 20);
            // Cylindrical roll-over along the bottom edge and a flat flange below it
            Patch((u, v) => { double a = v * Math.PI / 2; return new Vec3(u * L, -R * Math.Sin(a), -R + R * Math.Cos(a)); }, 90, 12);
            Patch((u, v) => new Vec3(u * L, -R, -R - v * 60), 90, 8);
            var mesh = new TriMesh(verts.ToArray(), idx.ToArray());
            using (var fs = File.Create(path)) MeshIO.WriteBinaryStl(mesh, fs);
            Console.WriteLine($"wrote {path}: {mesh.TriangleCount} triangles (mm)");
        }
    }
}
