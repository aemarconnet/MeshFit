using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;
using MeshFit.Core.Selection;

namespace MeshFit.Core.Project
{
    /// <summary>
    /// Text project file (*.meshfit): mesh file reference, unit scale, regions and the run-length
    /// encoded triangle ownership. Fits are recomputed after loading.
    /// </summary>
    public static class ProjectFile
    {
        private const string Magic = "MESHFIT 1";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Header
        {
            /// <summary>Mesh path, relative to the project file when possible.</summary>
            public string MeshPath;
            /// <summary>Scale applied to the mesh file coordinates on load (e.g. 0.001 for mm → m).</summary>
            public double Scale = 1;
            public int TriangleCount;
            /// <summary>Placement applied to the (scaled) mesh after loading.</summary>
            public RigidTransform Transform = RigidTransform.Identity;
        }

        public static void Save(string path, RegionSet set, string meshPath, double scale, RigidTransform transform = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Magic);
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            string rel = meshPath;
            if (!string.IsNullOrEmpty(meshPath) && Path.GetDirectoryName(Path.GetFullPath(meshPath)) == dir)
                rel = Path.GetFileName(meshPath);
            sb.AppendLine("mesh " + rel);
            sb.AppendLine("scale " + scale.ToString("R", Inv));
            sb.AppendLine("triangles " + set.Mesh.TriangleCount.ToString(Inv));
            if (transform != null && !transform.IsIdentity()) sb.AppendLine("transform " + transform);
            foreach (var r in set.Regions)
                sb.AppendLine(string.Format(Inv, "region {0} {1:X6} {2} {3}", r.Id, r.Color, r.Request, r.Name));
            foreach (var r in set.Regions)
                if (!string.IsNullOrEmpty(r.FeatureName))
                    sb.AppendLine(string.Format(Inv, "feature {0} {1} {2}", r.Id, r.SurfaceHidden ? 1 : 0, r.FeatureName));
            sb.Append("owners");
            var o = set.Owners;
            int i = 0;
            while (i < o.Length)
            {
                int j = i;
                while (j < o.Length && o[j] == o[i]) j++;
                sb.Append(' ').Append(o[i].ToString(Inv)).Append(':').Append((j - i).ToString(Inv));
                i = j;
            }
            sb.AppendLine();
            File.WriteAllText(path, sb.ToString());
        }

        public static Header ReadHeader(string path)
        {
            var h = new Header();
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("mesh ")) h.MeshPath = ResolveMesh(path, line.Substring(5).Trim());
                else if (line.StartsWith("scale ")) h.Scale = double.Parse(line.Substring(6), Inv);
                else if (line.StartsWith("triangles ")) h.TriangleCount = int.Parse(line.Substring(10), Inv);
                else if (line.StartsWith("transform ")) h.Transform = RigidTransform.Parse(line.Substring(10));
            }
            return h;
        }

        private static string ResolveMesh(string projectPath, string mesh)
        {
            if (string.IsNullOrEmpty(mesh) || Path.IsPathRooted(mesh)) return mesh;
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath)), mesh);
        }

        /// <summary>Loads regions into a fresh region set for an already loaded mesh.</summary>
        public static RegionSet Load(string path, TriMesh mesh)
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != Magic) throw new InvalidDataException("Not a MeshFit project file.");
            var set = new RegionSet(mesh);
            foreach (var line in lines)
            {
                if (line.StartsWith("triangles "))
                {
                    int n = int.Parse(line.Substring(10), Inv);
                    if (n != mesh.TriangleCount)
                        throw new InvalidDataException($"Project was painted on a mesh with {n} triangles, this mesh has {mesh.TriangleCount}.");
                }
                else if (line.StartsWith("region "))
                {
                    var p = line.Split(new[] { ' ' }, 5);
                    var req = (FitRequest)Enum.Parse(typeof(FitRequest), p[3]);
                    set.RestoreRegion(int.Parse(p[1], Inv), p.Length > 4 ? p[4] : "Region " + p[1],
                        int.Parse(p[2], NumberStyles.HexNumber, Inv), req);
                }
                else if (line.StartsWith("feature "))
                {
                    var p = line.Split(new[] { ' ' }, 4);
                    var r = set.Find(int.Parse(p[1], Inv));
                    if (r != null && p.Length == 4) { r.SurfaceHidden = p[2] == "1"; r.FeatureName = p[3]; }
                }
                else if (line.StartsWith("owners"))
                {
                    int t = 0;
                    foreach (var tok in line.Substring(6).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int c = tok.IndexOf(':');
                        int owner = int.Parse(tok.Substring(0, c), Inv), count = int.Parse(tok.Substring(c + 1), Inv);
                        for (int k = 0; k < count && t < mesh.TriangleCount; k++) set.Assign(t++, owner);
                    }
                }
            }
            set.UpdateCounts();
            return set;
        }
    }
}
