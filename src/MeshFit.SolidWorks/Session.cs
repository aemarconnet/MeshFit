using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;
using MeshFit.Core.Selection;

namespace MeshFit.SolidWorks
{
    internal enum PaintTool { None, Brush, Erase, SmartFill, FillConnected, PickRegion }

    /// <summary>
    /// All state of one reverse engineering session (mesh, regions, tool settings) plus the painting
    /// logic driven by view mouse events. Independent of WinForms; SOLIDWORKS only supplies pick lines.
    /// Model units are metres (SOLIDWORKS API units).
    /// </summary>
    internal sealed class Session
    {
        public TriMesh Mesh { get; private set; }
        public RegionSet Regions { get; private set; }
        /// <summary>Mesh file (or saved copy of a SOLIDWORKS body) and the scale used to bring it to metres.</summary>
        public string MeshPath { get; set; }
        public double MeshScale { get; set; } = 0.001;
        public string MeshDescription { get; private set; }

        public Region ActiveRegion { get; set; }
        public PaintTool Tool { get; set; } = PaintTool.None;
        public double BrushRadius { get; set; } = 0.01;      // m
        public double SmartAngleDeg { get; set; } = 8;        // max dihedral angle for smart fill
        public bool ProtectOtherRegions { get; set; } = true;

        public FitOptions FitOptions { get; } = new FitOptions { Tolerance = 0.0002 };
        public double ExtentMargin { get; set; } = 0.1;

        public bool ShowMesh { get; set; } = true;
        public bool ShowPreview { get; set; } = true;
        public bool ShowDeviation { get; set; }

        // Hover / brush cursor
        public bool HasHover { get; private set; }
        public Vec3 HoverPoint { get; private set; }
        public Vec3 HoverNormal { get; private set; }

        private bool _stroking;
        private Vec3? _lastStrokePoint;

        /// <summary>Raised when geometry/regions change (overlay must be rebuilt).</summary>
        public event Action Changed;
        /// <summary>Raised when only the cursor moved (cheap redraw).</summary>
        public event Action HoverChanged;
        public event Action<string> Status;

        public bool HasMesh => Mesh != null;

        public void SetMesh(TriMesh mesh, string description, RegionSet regions = null)
        {
            Mesh = mesh;
            Regions = regions ?? new RegionSet(mesh);
            Regions.Changed += (s, e) => RaiseChanged();
            ActiveRegion = Regions.Regions.FirstOrDefault() ?? Regions.AddRegion();
            MeshDescription = description;
            BrushRadius = Math.Max(mesh.AverageEdgeLength * 4, mesh.Bounds.Diagonal / 100);
            RaiseChanged();
        }

        public void RaiseChanged() => Changed?.Invoke();
        public void Say(string msg) => Status?.Invoke(msg);

        // ------------------------------------------------------------------ picking

        /// <summary>Front-most triangle on the pick line (dir points toward the viewer).</summary>
        public int Pick(Vec3 linePoint, Vec3 dirTowardViewer, out Vec3 hit)
        {
            hit = linePoint;
            if (Mesh == null) return -1;
            var hits = Mesh.Bvh.LineHits(linePoint, dirTowardViewer);
            if (hits.Count == 0) return -1;
            var front = hits[hits.Count - 1];
            hit = linePoint + dirTowardViewer * front.Value;
            return front.Key;
        }

        public void Hover(Vec3 linePoint, Vec3 dir)
        {
            int t = Pick(linePoint, dir, out var hit);
            bool had = HasHover;
            HasHover = t >= 0;
            if (HasHover) { HoverPoint = hit; HoverNormal = Mesh.FaceNormals[t]; }
            if (_stroking && t >= 0) StrokeTo(t, hit);
            else if (HasHover || had) HoverChanged?.Invoke();
        }

        public void MouseDown(Vec3 linePoint, Vec3 dir, bool shift, bool ctrl)
        {
            if (Mesh == null || Tool == PaintTool.None) return;
            int t = Pick(linePoint, dir, out var hit);
            if (t < 0) return;
            switch (Tool)
            {
                case PaintTool.PickRegion:
                {
                    var r = Regions.Find(Regions.OwnerOf(t));
                    if (r != null) { ActiveRegion = r; RaiseChanged(); Say("Active region: " + r.Name); }
                    return;
                }
                case PaintTool.SmartFill:
                    if (!EnsureRegion()) return;
                    Regions.BeginStroke();
                    if (shift) EraseSmooth(t);
                    else PaintTools.GrowSmooth(Regions, t, ActiveRegion.Id, SmartAngleDeg);
                    Regions.EndStroke();
                    return;
                case PaintTool.FillConnected:
                    if (!EnsureRegion()) return;
                    Regions.BeginStroke();
                    PaintTools.FillConnected(Regions, t, shift ? RegionSet.None : ActiveRegion.Id);
                    Regions.EndStroke();
                    return;
                default:
                    if (!EnsureRegion()) return;
                    _eraseStroke = Tool == PaintTool.Erase || shift;
                    _stroking = true;
                    _lastStrokePoint = null;
                    Regions.BeginStroke();
                    StrokeTo(t, hit);
                    return;
            }
        }

        private bool _eraseStroke;

        public void MouseUp()
        {
            if (!_stroking) return;
            _stroking = false;
            _lastStrokePoint = null;
            Regions.EndStroke();
        }

        public bool IsStroking => _stroking;

        private void StrokeTo(int tri, Vec3 hit)
        {
            int target = _eraseStroke ? RegionSet.None : ActiveRegion.Id;
            // Fill gaps between mouse samples so fast strokes stay continuous.
            if (_lastStrokePoint.HasValue)
            {
                var a = _lastStrokePoint.Value;
                double len = a.DistanceTo(hit);
                int steps = (int)Math.Min(50, Math.Floor(len / (BrushRadius * 0.5)));
                for (int i = 1; i <= steps; i++)
                {
                    var p = Vec3.Lerp(a, hit, (double)i / (steps + 1));
                    int t = Mesh.Bvh.Nearest(p, out var c);
                    if (t >= 0) PaintTools.Brush(Regions, t, c, BrushRadius, target, ProtectOtherRegions && !_eraseStroke);
                }
            }
            PaintTools.Brush(Regions, tri, hit, BrushRadius, target, ProtectOtherRegions && !_eraseStroke);
            _lastStrokePoint = hit;
            Regions.UpdateCounts();
            RaiseChanged();
        }

        private void EraseSmooth(int t)
        {
            int owner = Regions.OwnerOf(t);
            if (owner == RegionSet.None) return;
            // Remove the smooth patch belonging to the clicked region.
            var tmp = new HashSet<int>();
            var q = new Queue<int>();
            q.Enqueue(t); tmp.Add(t);
            double cosEdge = Math.Cos(SmartAngleDeg * Math.PI / 180);
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                Regions.Assign(c, RegionSet.None);
                for (int k = 0; k < 3; k++)
                {
                    int nb = Mesh.Neighbors[c * 3 + k];
                    if (nb < 0 || tmp.Contains(nb) || Regions.OwnerOf(nb) != owner) continue;
                    if (Mesh.FaceNormals[c].Dot(Mesh.FaceNormals[nb]) < cosEdge) continue;
                    tmp.Add(nb); q.Enqueue(nb);
                }
            }
        }

        private bool EnsureRegion()
        {
            if (ActiveRegion != null && Regions.Regions.Contains(ActiveRegion)) return true;
            ActiveRegion = Regions.AddRegion();
            return true;
        }

        // ------------------------------------------------------------------ fitting

        public FitResult FitRegion(Region r)
        {
            var tris = Regions.TriangleListOf(r.Id);
            if (tris.Count < 3) throw new InvalidOperationException($"'{r.Name}' has no painted triangles.");
            FitOptions.Request = r.Request;
            var res = SurfaceFitter.Fit(Mesh, tris, FitOptions);
            r.Fit = res;
            return res;
        }

        public Vec3[] RegionPoints(Region r, int max = int.MaxValue)
        {
            var v = Mesh.VerticesOf(Regions.TrianglesOf(r.Id));
            if (v.Length > max)
            {
                double step = (double)v.Length / max;
                v = Enumerable.Range(0, max).Select(i => v[(int)(i * step)]).ToArray();
            }
            return v.Select(i => Mesh.Vertices[i]).ToArray();
        }
    }
}
