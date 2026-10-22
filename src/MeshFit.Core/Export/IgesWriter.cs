using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Export
{
    /// <summary>
    /// Minimal IGES 5.3 writer for rational B-spline surfaces (entity 128). Every fitted surface,
    /// analytic or freeform, is written as an exact NURBS so any CAD system (including SOLIDWORKS via
    /// File &gt; Open or Insert &gt; Features &gt; Imported) can read it.
    /// </summary>
    public sealed class IgesWriter
    {
        private readonly List<(NurbsSurface surf, string name)> _surfaces = new List<(NurbsSurface, string)>();
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Multiplier from model units to millimetres (the file is written in mm).</summary>
        public double ToMillimetres { get; set; } = 1.0;
        public string Author { get; set; } = "MeshFit";

        public void Add(NurbsSurface s, string name) => _surfaces.Add((s, name));

        public void Save(string path)
        {
            using (var w = new StreamWriter(path, false, Encoding.ASCII)) Write(w, Path.GetFileName(path));
        }

        public void Write(TextWriter w, string fileName)
        {
            var start = new List<string> { "MeshFit surfaces fitted to mesh regions." };
            var global = BuildGlobal(fileName);

            var dirLines = new List<string>();
            var parLines = new List<string>();
            for (int e = 0; e < _surfaces.Count; e++)
            {
                int deSeq = 2 * e + 1;
                int pStart = parLines.Count + 1;
                var plines = FormatParams(SurfaceParams(_surfaces[e].surf), deSeq);
                parLines.AddRange(plines);
                string label = Label(_surfaces[e].name);
                dirLines.Add(DirLine(new[] { "128", pStart.ToString(Inv), "0", "0", "0", "0", "0", "0", "00000000" }));
                dirLines.Add(DirLine(new[] { "128", "0", "0", plines.Count.ToString(Inv), "0", "", "", label, "0" }));
            }

            int seq = 0;
            foreach (var l in start) w.Write(Card(l, 'S', ++seq));
            int sCount = seq; seq = 0;
            foreach (var l in Pack(global, 72)) w.Write(Card(l, 'G', ++seq));
            int gCount = seq; seq = 0;
            foreach (var l in dirLines) w.Write(Card(l, 'D', ++seq));
            int dCount = seq; seq = 0;
            foreach (var l in parLines) w.Write(Card(l, 'P', ++seq));
            int pCount = seq;
            string term = $"S{sCount,7}G{gCount,7}D{dCount,7}P{pCount,7}";
            w.Write(Card(term, 'T', 1));
        }

        private List<string> BuildGlobal(string fileName)
        {
            var maxCoord = 1.0;
            foreach (var (s, _) in _surfaces)
                foreach (var p in s.ControlPoints)
                    maxCoord = Math.Max(maxCoord, Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) * ToMillimetres);
            string now = DateTime.Now.ToString("yyyyMMdd.HHmmss", Inv);
            var g = new[]
            {
                "1H,", "1H;", H("MeshFit"), H(fileName), H("MeshFit"), H("MeshFit 0.1"),
                "32", "38", "6", "308", "15", H("MeshFit"), "1.0", "2", H("MM"), "1", "1.0",
                H(now), D(1e-6), D(maxCoord), H(Author), H(""), "11", "0", H(now)
            };
            var tokens = new List<string>();
            for (int i = 0; i < g.Length; i++) tokens.Add(g[i] + (i == g.Length - 1 ? ";" : ","));
            return tokens;
        }

        private List<string> SurfaceParams(NurbsSurface s)
        {
            int k1 = s.CountU - 1, k2 = s.CountV - 1;
            var p = new List<string>
            {
                "128", k1.ToString(Inv), k2.ToString(Inv), s.DegreeU.ToString(Inv), s.DegreeV.ToString(Inv),
                "0", "0", s.IsRational ? "0" : "1", "0", "0"
            };
            foreach (var k in s.KnotsU) p.Add(D(k));
            foreach (var k in s.KnotsV) p.Add(D(k));
            for (int j = 0; j <= k2; j++)
                for (int i = 0; i <= k1; i++) p.Add(D(s.IsRational ? s.Weights[i, j] : 1.0));
            for (int j = 0; j <= k2; j++)
                for (int i = 0; i <= k1; i++)
                {
                    var c = s.ControlPoints[i, j] * ToMillimetres;
                    p.Add(D(c.X)); p.Add(D(c.Y)); p.Add(D(c.Z));
                }
            p.Add(D(s.UMin)); p.Add(D(s.UMax)); p.Add(D(s.VMin)); p.Add(D(s.VMax));
            return p;
        }

        /// <summary>Packs parameters into 64-column records ending with ';', with the DE pointer in columns 66-72.</summary>
        private static List<string> FormatParams(List<string> items, int dePointer)
        {
            var tokens = new List<string>();
            for (int i = 0; i < items.Count; i++) tokens.Add(items[i] + (i == items.Count - 1 ? ";" : ","));
            var lines = Pack(tokens, 64);
            for (int i = 0; i < lines.Count; i++)
                lines[i] = lines[i].PadRight(64) + " " + dePointer.ToString(Inv).PadLeft(7);
            return lines;
        }

        private static string DirLine(string[] fields)
        {
            var sb = new StringBuilder();
            foreach (var f in fields) sb.Append(f.PadLeft(8));
            return sb.ToString();
        }

        private static string Card(string data, char section, int seq) =>
            data.PadRight(72).Substring(0, 72) + section + seq.ToString(Inv).PadLeft(7) + "\r\n";

        /// <summary>Packs delimiter-terminated tokens into lines without splitting a token.</summary>
        private static List<string> Pack(List<string> tokens, int width)
        {
            var lines = new List<string>();
            var sb = new StringBuilder();
            foreach (var t in tokens)
            {
                if (sb.Length + t.Length > width && sb.Length > 0) { lines.Add(sb.ToString()); sb.Clear(); }
                sb.Append(t);
            }
            if (sb.Length > 0) lines.Add(sb.ToString());
            return lines;
        }

        private static string H(string s) => s.Length.ToString(Inv) + "H" + s;
        private static string D(double v) => v.ToString("0.0##############E+0", Inv).Replace("E", "D");

        private static string Label(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in (name ?? "").ToUpperInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            var l = sb.ToString();
            return l.Length > 8 ? l.Substring(0, 8) : l;
        }
    }
}
