using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Mesh
{
    /// <summary>Readers for STL (ASCII/binary), OBJ and PLY (ASCII/binary little endian) and a binary STL writer.</summary>
    public static class MeshIO
    {
        public static TriMesh Load(string path, double scale = 1.0)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            TriMesh m;
            using (var fs = File.OpenRead(path))
            {
                switch (ext)
                {
                    case ".stl": m = ReadStl(fs); break;
                    case ".obj": m = ReadObj(fs); break;
                    case ".ply": m = ReadPly(fs); break;
                    default: throw new NotSupportedException("Unsupported mesh format: " + ext);
                }
            }
            return scale == 1.0 ? m : m.Scaled(scale);
        }

        // ---------------------------------------------------------------- STL

        public static TriMesh ReadStl(Stream s)
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] data = ms.ToArray();
            if (IsBinaryStl(data)) return ReadBinaryStl(data);
            return ReadAsciiStl(Encoding.ASCII.GetString(data));
        }

        private static bool IsBinaryStl(byte[] d)
        {
            if (d.Length < 84) return false;
            uint n = BitConverter.ToUInt32(d, 80);
            if (84 + 50L * n == d.Length) return true;
            // Some ASCII files start with "solid"; binary files sometimes do too, so size check wins above.
            string head = Encoding.ASCII.GetString(d, 0, Math.Min(d.Length, 256)).TrimStart();
            return !head.StartsWith("solid", StringComparison.OrdinalIgnoreCase);
        }

        private static TriMesh ReadBinaryStl(byte[] d)
        {
            uint n = BitConverter.ToUInt32(d, 80);
            var welder = new VertexWelder();
            var idx = new int[n * 3];
            int off = 84;
            for (int t = 0; t < n && off + 50 <= d.Length; t++, off += 50)
            {
                for (int k = 0; k < 3; k++)
                {
                    int o = off + 12 + k * 12;
                    idx[t * 3 + k] = welder.Add(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));
                }
            }
            return new TriMesh(welder.Vertices.ToArray(), idx);
        }

        private static TriMesh ReadAsciiStl(string text)
        {
            var welder = new VertexWelder();
            var idx = new List<int>();
            var corner = new List<int>(3);
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                {
                    var p = SplitWs(line);
                    corner.Add(welder.Add(ParseD(p[1]), ParseD(p[2]), ParseD(p[3])));
                }
                else if (line.StartsWith("endloop", StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 1; i + 1 < corner.Count; i++) { idx.Add(corner[0]); idx.Add(corner[i]); idx.Add(corner[i + 1]); }
                    corner.Clear();
                }
            }
            return new TriMesh(welder.Vertices.ToArray(), idx.ToArray());
        }

        public static void WriteBinaryStl(TriMesh m, Stream s)
        {
            var w = new BinaryWriter(s);
            w.Write(new byte[80]);
            w.Write((uint)m.TriangleCount);
            for (int t = 0; t < m.TriangleCount; t++)
            {
                var n = m.FaceNormals[t];
                w.Write((float)n.X); w.Write((float)n.Y); w.Write((float)n.Z);
                for (int k = 0; k < 3; k++) { var p = m.P(t, k); w.Write((float)p.X); w.Write((float)p.Y); w.Write((float)p.Z); }
                w.Write((ushort)0);
            }
            w.Flush();
        }

        // ---------------------------------------------------------------- OBJ

        public static TriMesh ReadObj(Stream s)
        {
            var verts = new List<Vec3>();
            var idx = new List<int>();
            using (var r = new StreamReader(s))
            {
                string line;
                var face = new List<int>();
                while ((line = r.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.StartsWith("v ") || line.StartsWith("v\t"))
                    {
                        var p = SplitWs(line);
                        verts.Add(new Vec3(ParseD(p[1]), ParseD(p[2]), ParseD(p[3])));
                    }
                    else if (line.StartsWith("f ") || line.StartsWith("f\t"))
                    {
                        var p = SplitWs(line);
                        face.Clear();
                        for (int i = 1; i < p.Length; i++)
                        {
                            string tok = p[i];
                            int slash = tok.IndexOf('/');
                            if (slash >= 0) tok = tok.Substring(0, slash);
                            int vi = int.Parse(tok, CultureInfo.InvariantCulture);
                            face.Add(vi > 0 ? vi - 1 : verts.Count + vi);
                        }
                        for (int i = 1; i + 1 < face.Count; i++) { idx.Add(face[0]); idx.Add(face[i]); idx.Add(face[i + 1]); }
                    }
                }
            }
            return new TriMesh(verts.ToArray(), idx.ToArray());
        }

        // ---------------------------------------------------------------- PLY

        public static TriMesh ReadPly(Stream s)
        {
            var br = new BinaryReader(s);
            string format = null;
            int nVert = 0, nFace = 0;
            var elements = new List<(string name, int count, List<(string type, string name, string countType)> props)>();
            while (true)
            {
                string line = ReadAsciiLine(br).Trim();
                if (line == "end_header") break;
                var p = SplitWs(line);
                if (p.Length == 0) continue;
                if (p[0] == "format") format = p[1];
                else if (p[0] == "element")
                {
                    elements.Add((p[1], int.Parse(p[2], CultureInfo.InvariantCulture), new List<(string, string, string)>()));
                    if (p[1] == "vertex") nVert = int.Parse(p[2], CultureInfo.InvariantCulture);
                    if (p[1] == "face") nFace = int.Parse(p[2], CultureInfo.InvariantCulture);
                }
                else if (p[0] == "property")
                {
                    var props = elements[elements.Count - 1].props;
                    if (p[1] == "list") props.Add((p[3], p[4], p[2]));
                    else props.Add((p[1], p[2], null));
                }
            }
            if (format != "ascii" && format != "binary_little_endian")
                throw new NotSupportedException("PLY format not supported: " + format);

            var verts = new Vec3[nVert];
            var idx = new List<int>(nFace * 3);
            bool ascii = format == "ascii";
            var tokens = ascii ? new AsciiTokens(br) : null;

            foreach (var el in elements)
            {
                for (int i = 0; i < el.count; i++)
                {
                    double x = 0, y = 0, z = 0;
                    foreach (var prop in el.props)
                    {
                        if (prop.countType != null)
                        {
                            int cnt = (int)(ascii ? tokens.Next() : ReadBin(br, prop.countType));
                            var poly = new int[cnt];
                            for (int k = 0; k < cnt; k++) poly[k] = (int)(ascii ? tokens.Next() : ReadBin(br, prop.type));
                            if (el.name == "face" && (prop.name == "vertex_indices" || prop.name == "vertex_index"))
                                for (int k = 1; k + 1 < cnt; k++) { idx.Add(poly[0]); idx.Add(poly[k]); idx.Add(poly[k + 1]); }
                        }
                        else
                        {
                            double v = ascii ? tokens.Next() : ReadBin(br, prop.type);
                            if (el.name == "vertex")
                            {
                                if (prop.name == "x") x = v; else if (prop.name == "y") y = v; else if (prop.name == "z") z = v;
                            }
                        }
                    }
                    if (el.name == "vertex") verts[i] = new Vec3(x, y, z);
                }
            }
            return new TriMesh(verts, idx.ToArray());
        }

        private static double ReadBin(BinaryReader br, string type)
        {
            switch (type)
            {
                case "char": case "int8": return br.ReadSByte();
                case "uchar": case "uint8": return br.ReadByte();
                case "short": case "int16": return br.ReadInt16();
                case "ushort": case "uint16": return br.ReadUInt16();
                case "int": case "int32": return br.ReadInt32();
                case "uint": case "uint32": return br.ReadUInt32();
                case "float": case "float32": return br.ReadSingle();
                case "double": case "float64": return br.ReadDouble();
                default: throw new NotSupportedException("PLY type " + type);
            }
        }

        private static string ReadAsciiLine(BinaryReader br)
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = br.BaseStream.ReadByte();
                if (b < 0 || b == '\n') break;
                if (b != '\r') sb.Append((char)b);
            }
            return sb.ToString();
        }

        private sealed class AsciiTokens
        {
            private readonly BinaryReader _br;
            private readonly Queue<string> _q = new Queue<string>();
            public AsciiTokens(BinaryReader br) { _br = br; }
            public double Next()
            {
                while (_q.Count == 0)
                {
                    if (_br.BaseStream.Position >= _br.BaseStream.Length) throw new EndOfStreamException();
                    foreach (var t in SplitWs(ReadAsciiLine(_br))) _q.Enqueue(t);
                }
                return ParseD(_q.Dequeue());
            }
        }

        // ---------------------------------------------------------------- helpers

        private static string[] SplitWs(string s) => s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        private static double ParseD(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>Merges exactly coincident vertices (STL stores each triangle's corners separately).</summary>
        private sealed class VertexWelder
        {
            public readonly List<Vec3> Vertices = new List<Vec3>();
            private readonly Dictionary<Vec3, int> _map = new Dictionary<Vec3, int>();
            public int Add(double x, double y, double z)
            {
                var v = new Vec3(x, y, z);
                if (_map.TryGetValue(v, out int i)) return i;
                i = Vertices.Count;
                Vertices.Add(v);
                _map[v] = i;
                return i;
            }
        }
    }
}
