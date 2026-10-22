using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MeshFit.Core.Export;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Selection;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Draws the mesh, painted regions, deviation map, surface previews and the brush cursor with
    /// OpenGL from the model view's BufferSwapNotify callback. Coordinates are model space (metres).
    /// Vertex/colour arrays are rebuilt only when the session reports a change.
    /// </summary>
    internal sealed class OverlayRenderer
    {
        private readonly Session _s;
        private bool _dirty = true;
        private float[] _meshV = new float[0]; private byte[] _meshC = new byte[0];
        private float[] _paintV = new float[0]; private byte[] _paintC = new byte[0];
        private float[] _lineV = new float[0]; private byte[] _lineC = new byte[0];
        private object _meshBuiltFor;
        // Preview lines per region, valid while the fit and the triangle count are unchanged.
        private readonly Dictionary<int, (FitResult fit, int tris, List<float> v, List<byte> c)> _previewCache =
            new Dictionary<int, (FitResult, int, List<float>, List<byte>)>();

        public OverlayRenderer(Session s)
        {
            _s = s;
            _s.Changed += () => _dirty = true;
        }

        public void Invalidate() => _dirty = true;

        public void Draw(double pixelSize)
        {
            if (!_s.HasMesh) return;
            if (_dirty) { Rebuild(); _dirty = false; }

            Gl.glPushAttrib(Gl.GL_ALL_ATTRIB_BITS);
            Gl.glPushClientAttrib(Gl.GL_CLIENT_ALL_ATTRIB_BITS);
            try
            {
                Gl.glDisable(Gl.GL_LIGHTING);
                Gl.glDisable(Gl.GL_TEXTURE_2D);
                Gl.glDisable(Gl.GL_CULL_FACE);
                Gl.glEnable(Gl.GL_BLEND);
                Gl.glBlendFunc(Gl.GL_SRC_ALPHA, Gl.GL_ONE_MINUS_SRC_ALPHA);
                Gl.glEnable(Gl.GL_DEPTH_TEST);
                Gl.glDepthFunc(Gl.GL_LEQUAL);
                Gl.glEnable(Gl.GL_POLYGON_OFFSET_FILL);

                // Painted regions sit slightly in front of the mesh (and of a SOLIDWORKS mesh body).
                Gl.glPolygonOffset(-1f, -2f);
                if (_s.ShowMesh) DrawArrays(Gl.GL_TRIANGLES, _meshV, _meshC);
                Gl.glPolygonOffset(-2f, -4f);
                DrawArrays(Gl.GL_TRIANGLES, _paintV, _paintC);

                Gl.glLineWidth(1.5f);
                DrawArrays(Gl.GL_LINES, _lineV, _lineC);
                DrawCursor(pixelSize);
            }
            finally
            {
                Gl.glPopClientAttrib();
                Gl.glPopAttrib();
            }
        }

        private static void DrawArrays(uint mode, float[] v, byte[] c)
        {
            if (v.Length == 0) return;
            var hv = GCHandle.Alloc(v, GCHandleType.Pinned);
            var hc = GCHandle.Alloc(c, GCHandleType.Pinned);
            try
            {
                Gl.glEnableClientState(Gl.GL_VERTEX_ARRAY);
                Gl.glEnableClientState(Gl.GL_COLOR_ARRAY);
                Gl.glVertexPointer(3, Gl.GL_FLOAT, 0, hv.AddrOfPinnedObject());
                Gl.glColorPointer(4, Gl.GL_UNSIGNED_BYTE, 0, hc.AddrOfPinnedObject());
                Gl.glDrawArrays(mode, 0, v.Length / 3);
            }
            finally
            {
                Gl.glDisableClientState(Gl.GL_VERTEX_ARRAY);
                Gl.glDisableClientState(Gl.GL_COLOR_ARRAY);
                hv.Free(); hc.Free();
            }
        }

        private void DrawCursor(double pixelSize)
        {
            if (!_s.HasHover || _s.Tool == PaintTool.None) return;
            var c = _s.HoverPoint;
            var f = Frame.FromAxis(c, _s.HoverNormal);
            bool brush = _s.Tool == PaintTool.Brush;
            double r = brush ? _s.BrushRadius : 8 * pixelSize;
            int color = _s.Removing ? 0xFF4040 : _s.ActiveRegion?.Color ?? 0xFFFFFF;
            Gl.glDisable(Gl.GL_DEPTH_TEST);
            Gl.glLineWidth(2f);
            Gl.Color(color, 255);
            Gl.glBegin(Gl.GL_LINE_LOOP);
            for (int i = 0; i < 48; i++)
            {
                double a = 2 * Math.PI * i / 48;
                var p = f.ToWorld(r * Math.Cos(a), r * Math.Sin(a), 0);
                Gl.glVertex3d(p.X, p.Y, p.Z);
            }
            Gl.glEnd();
            if (_s.Removing)
            {
                // A minus sign inside the cursor marks remove mode.
                var a = f.ToWorld(-0.5 * r, 0, 0); var b = f.ToWorld(0.5 * r, 0, 0);
                Gl.glBegin(Gl.GL_LINES);
                Gl.glVertex3d(a.X, a.Y, a.Z); Gl.glVertex3d(b.X, b.Y, b.Z);
                Gl.glEnd();
            }
            Gl.glBegin(Gl.GL_LINES);
            var n = c + _s.HoverNormal * (r * 0.5);
            Gl.glVertex3d(c.X, c.Y, c.Z); Gl.glVertex3d(n.X, n.Y, n.Z);
            Gl.glEnd();
        }

        private void Rebuild()
        {
            var m = _s.Mesh;
            var set = _s.Regions;
            int nt = m.TriangleCount;

            // Whole mesh in neutral grey, shaded by a fixed head light on the normal so it reads as 3D.
            // The grey mesh only changes when a different mesh is loaded.
            if (_s.ShowMesh && !ReferenceEquals(_meshBuiltFor, m))
            {
                _meshBuiltFor = m;
                _meshV = new float[nt * 9]; _meshC = new byte[nt * 12];
                var light = new Vec3(0.3, 0.5, 0.81).Normalized();
                for (int t = 0; t < nt; t++)
                {
                    PutTri(_meshV, t, m.P(t, 0), m.P(t, 1), m.P(t, 2));
                    byte g = (byte)(110 + 110 * Math.Abs(m.FaceNormals[t].Dot(light)));
                    for (int k = 0; k < 3; k++) PutColor(_meshC, t * 3 + k, g, g, g, 255);
                }
            }

            // Painted triangles: region colour, or the deviation map for every fitted region.
            var regionById = new Dictionary<int, Region>();
            foreach (var r in set.Regions) regionById[r.Id] = r;
            var active = _s.ActiveRegion;
            bool devMap = _s.ShowDeviation;
            double tol = _s.FitOptions.Tolerance;
            int count = 0;
            if (_s.ShowRegions) for (int t = 0; t < nt; t++) if (set.OwnerOf(t) != RegionSet.None) count++;
            _paintV = new float[count * 9];
            _paintC = new byte[count * 12];
            int i = 0;
            for (int t = 0; t < nt; t++)
            {
                int o = set.OwnerOf(t);
                if (!_s.ShowRegions || o == RegionSet.None || !regionById.TryGetValue(o, out var reg)) continue;
                PutTri(_paintV, i, m.P(t, 0), m.P(t, 1), m.P(t, 2));
                for (int k = 0; k < 3; k++)
                {
                    if (devMap && reg.Fit?.Surface != null)
                    {
                        double d = VertexDeviation(reg, m.V(t, k)) / tol;
                        DeviationColor(d, out byte r, out byte g, out byte b);
                        PutColor(_paintC, i * 3 + k, r, g, b, 255);
                    }
                    else
                    {
                        byte a = (byte)(o == active?.Id ? 200 : 140);
                        PutColor(_paintC, i * 3 + k, (byte)(reg.Color >> 16), (byte)(reg.Color >> 8), (byte)reg.Color, a);
                    }
                }
                i++;
            }

            // Iso-line previews of the fitted surfaces.
            var lv = new List<float>();
            var lc = new List<byte>();
            if (_s.ShowPreview)
            {
                foreach (var r in set.Regions)
                {
                    if (r.Fit?.Surface == null || r.TriangleCount == 0) continue;
                    if (!_previewCache.TryGetValue(r.Id, out var cached) || cached.fit != r.Fit || cached.tris != r.TriangleCount)
                    {
                        cached = (r.Fit, r.TriangleCount, new List<float>(), new List<byte>());
                        try { AddIsoLines(r, cached.v, cached.c); } catch { /* preview only */ }
                        _previewCache[r.Id] = cached;
                    }
                    lv.AddRange(cached.v);
                    // Active region's preview in yellow, others in white.
                    int color = r == active ? 0xFFFF00 : 0xFFFFFF;
                    for (int k = 0; k < cached.c.Count; k += 4)
                    {
                        lc.Add((byte)(color >> 16)); lc.Add((byte)(color >> 8)); lc.Add((byte)color); lc.Add(220);
                    }
                }
            }
            _lineV = lv.ToArray();
            _lineC = lc.ToArray();
        }

        private void AddIsoLines(Region r, List<float> v, List<byte> c)
        {
            const int n = 12, seg = 24;
            Func<double, double, Vec3> eval;
            double s0, s1, t0, t1;
            if (r.Fit.Surface is AnalyticSurface a)
            {
                var e = SurfaceExtents.Compute(a, _s.RegionPoints(r, 2000), _s.ExtentMargin);
                eval = a.PointAt; s0 = e.S0; s1 = e.S1; t0 = e.T0; t1 = e.T1;
            }
            else
            {
                var nurbs = NurbsConversion.ToNurbs(r.Fit.Surface, default);
                eval = nurbs.Evaluate; s0 = nurbs.UMin; s1 = nurbs.UMax; t0 = nurbs.VMin; t1 = nurbs.VMax;
            }
            for (int dir = 0; dir < 2; dir++)
                for (int i = 0; i <= n; i++)
                {
                    double fixedP = (double)i / n;
                    Vec3 prev = default;
                    for (int j = 0; j <= seg; j++)
                    {
                        double run = (double)j / seg;
                        double s = dir == 0 ? s0 + (s1 - s0) * fixedP : s0 + (s1 - s0) * run;
                        double t = dir == 0 ? t0 + (t1 - t0) * run : t0 + (t1 - t0) * fixedP;
                        var p = eval(s, t);
                        if (j > 0)
                        {
                            v.Add((float)prev.X); v.Add((float)prev.Y); v.Add((float)prev.Z);
                            v.Add((float)p.X); v.Add((float)p.Y); v.Add((float)p.Z);
                            for (int k = 0; k < 2; k++) { c.Add(255); c.Add(255); c.Add(255); c.Add(220); }
                        }
                        prev = p;
                    }
                }
        }

        // Per-vertex distances to each region's fit, reused across rebuilds while the fit is unchanged.
        private readonly Dictionary<int, (FitResult fit, Dictionary<int, double> d)> _devCache =
            new Dictionary<int, (FitResult, Dictionary<int, double>)>();

        private double VertexDeviation(Region r, int vertex)
        {
            if (!_devCache.TryGetValue(r.Id, out var c) || c.fit != r.Fit)
            {
                c = (r.Fit, new Dictionary<int, double>());
                _devCache[r.Id] = c;
            }
            if (!c.d.TryGetValue(vertex, out double d))
            {
                d = r.Fit.Surface.Distance(_s.Mesh.Vertices[vertex]);
                c.d[vertex] = d;
            }
            return d;
        }

        /// <summary>Blue (-tol) → green (0) → red (+tol); magenta beyond tolerance.</summary>
        private static void DeviationColor(double d, out byte r, out byte g, out byte b)
        {
            if (Math.Abs(d) > 1) { r = 255; g = 0; b = 255; return; }
            if (d < 0) { r = 0; g = (byte)(255 * (1 + d)); b = (byte)(255 * -d); }
            else { r = (byte)(255 * d); g = (byte)(255 * (1 - d)); b = 0; }
        }

        private static void PutTri(float[] a, int i, Vec3 p, Vec3 q, Vec3 r)
        {
            int o = i * 9;
            a[o] = (float)p.X; a[o + 1] = (float)p.Y; a[o + 2] = (float)p.Z;
            a[o + 3] = (float)q.X; a[o + 4] = (float)q.Y; a[o + 5] = (float)q.Z;
            a[o + 6] = (float)r.X; a[o + 7] = (float)r.Y; a[o + 8] = (float)r.Z;
        }

        private static void PutColor(byte[] a, int vertex, byte r, byte g, byte b, byte alpha)
        {
            int o = vertex * 4;
            a[o] = r; a[o + 1] = g; a[o + 2] = b; a[o + 3] = alpha;
        }
    }
}
