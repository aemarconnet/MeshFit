using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;
using MeshFit.Core.Selection;

namespace MeshFit.SolidWorks
{
    internal enum PaintTool { None, Brush, SmartFill, FillConnected, Fillet, PickRegion }

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
        /// <summary>When set, every tool removes paint instead of adding it (Shift inverts temporarily).</summary>
        public bool RemoveMode { get; set; }
        public double BrushRadius { get; set; } = 0.01;      // m
        public double SmartAngleDeg { get; set; } = 8;        // max dihedral angle for smart fill
        /// <summary>Tools only touch unpainted triangles (add) or the active region's triangles (remove).</summary>
        public bool ActiveRegionOnly { get; set; } = true;
        /// <summary>After a smart fill, enclosed unpainted islands up to this many triangles are filled (0 = off).</summary>
        public int AutoFillIslandLimit { get; set; } = 500;

        public FitOptions FitOptions { get; } = new FitOptions { Tolerance = 0.0002 };

        // ---- feature (fillet) detection
        /// <summary>Curvature field of the mesh; null until computed (it is computed in the background after loading).</summary>
        public Curvature Curvature { get; set; }
        /// <summary>Neighbourhood size for curvature estimation (m). 0 = automatic.</summary>
        public double FeatureScale { get; set; }
        /// <summary>Blends tighter than this radius (m) count as adjacent features.</summary>
        public double BlendRadius { get; set; } = 0.03;
        /// <summary>Smart fill and growing stop where an adjacent fillet starts.</summary>
        public bool StopAtFillets { get; set; } = true;
        public bool ShowCurvature { get; set; }
        private bool UseFeatures => StopAtFillets && Curvature != null;
        public double ExtentMargin { get; set; } = 0.1;

        public bool ShowMesh { get; set; } = true;
        public bool ShowRegions { get; set; } = true;
        public bool ShowPreview { get; set; } = true;
        public bool ShowDeviation { get; set; }

        /// <summary>A background computation is running; painting is ignored meanwhile.</summary>
        public bool Busy { get; set; }

        // Hover / brush cursor
        public bool HasHover { get; private set; }
        public Vec3 HoverPoint { get; private set; }
        public Vec3 HoverNormal { get; private set; }
        /// <summary>Signed distance from the cursor point to its region's fitted surface (m), when available.</summary>
        public double? HoverDeviation { get; private set; }
        public Region HoverRegion { get; private set; }

        private bool _stroking, _strokeRemove;
        private Vec3? _lastStrokePoint;
        private int _lastStrokeTri = -1;

        /// <summary>Where the previous stroke ended and with which brush radius (anchor for Ctrl+click lines).</summary>
        public Vec3? LastPaintPoint { get; private set; }
        public double LastPaintRadius { get; private set; }
        /// <summary>Ctrl is held: the overlay previews the straight line a Ctrl+click would paint.</summary>
        public bool LinePreview { get; set; }

        /// <summary>Raised when geometry/regions change (overlay must be rebuilt).</summary>
        public event Action Changed;
        /// <summary>Raised when only the cursor moved (cheap redraw).</summary>
        public event Action HoverChanged;
        public event Action<string> Status;

        public bool HasMesh => Mesh != null;
        public bool IsStroking => _stroking;
        /// <summary>Effective remove state for the cursor/brush display.</summary>
        public bool Removing => _stroking ? _strokeRemove : RemoveMode;

        public void SetMesh(TriMesh mesh, string description, RegionSet regions = null)
        {
            Mesh = mesh;
            Curvature = null;
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
            HoverDeviation = null;
            HoverRegion = null;
            if (HasHover)
            {
                HoverPoint = hit;
                HoverNormal = Mesh.FaceNormals[t];
                var r = Regions.Find(Regions.OwnerOf(t));
                if (r?.Fit?.Surface != null)
                {
                    HoverRegion = r;
                    try { HoverDeviation = r.Fit.Surface.Distance(hit); } catch { HoverDeviation = null; }
                }
            }
            if (HasHover || had) HoverChanged?.Invoke();
        }

        /// <summary>
        /// Starts a stroke at the pick line. Returns false when nothing was hit or the tool doesn't paint
        /// (Pick region is handled here directly).
        /// </summary>
        public bool BeginStroke(Vec3 linePoint, Vec3 dir, bool invert)
        {
            if (Mesh == null || Tool == PaintTool.None || Busy) return false;
            int t = Pick(linePoint, dir, out _);
            if (Tool == PaintTool.PickRegion)
            {
                var r = t >= 0 ? Regions.Find(Regions.OwnerOf(t)) : null;
                if (r != null) { ActiveRegion = r; RaiseChanged(); Say("Active region: " + r.Name); }
                return false;
            }
            EnsureRegion();
            _strokeRemove = RemoveMode ^ invert;
            _stroking = true;
            _lastStrokePoint = null;
            _lastStrokeTri = -1;
            Regions.BeginStroke((_strokeRemove ? "Remove (" : "Paint (") + ToolName(Tool) + ")");
            return true;
        }

        /// <summary>Paints at the pick line with the given brush radius (m). Returns false if the line misses the mesh.</summary>
        public bool StrokeSample(Vec3 linePoint, Vec3 dir, double radius)
        {
            if (!_stroking) return false;
            int t = Pick(linePoint, dir, out var hit);
            if (t < 0) return false;
            StrokeTo(t, hit, radius);
            return true;
        }

        public static string ToolName(PaintTool t) =>
            t == PaintTool.SmartFill ? "smart fill" : t == PaintTool.FillConnected ? "fill shell" :
            t == PaintTool.Fillet ? "fillet select" : t.ToString().ToLowerInvariant();

        public void EndStroke()
        {
            if (!_stroking) return;
            _stroking = false;
            if (_lastStrokePoint.HasValue) { LastPaintPoint = _lastStrokePoint; LastPaintRadius = _lastRadius; }
            _lastStrokePoint = null;
            if (Tool == PaintTool.SmartFill && !_strokeRemove && AutoFillIslandLimit > 0 && ActiveRegion != null)
                PaintTools.FillIslands(Regions, ActiveRegion.Id, AutoFillIslandLimit);
            Regions.EndStroke();
        }

        private double _lastRadius;

        /// <summary>Which triangles a stroke may modify.</summary>
        private bool CanChange(int t)
        {
            int o = Regions.OwnerOf(t);
            if (_strokeRemove) return !ActiveRegionOnly || o == ActiveRegion.Id;
            return !ActiveRegionOnly || o == RegionSet.None || o == ActiveRegion.Id;
        }

        private void StrokeTo(int tri, Vec3 hit, double radius)
        {
            _lastRadius = radius;
            int target = _strokeRemove ? RegionSet.None : ActiveRegion.Id;
            switch (Tool)
            {
                case PaintTool.Brush:
                    // Fill gaps between mouse samples so fast strokes stay continuous.
                    if (_lastStrokePoint.HasValue)
                    {
                        var a = _lastStrokePoint.Value;
                        int steps = (int)Math.Min(50, Math.Floor(a.DistanceTo(hit) / (radius * 0.5)));
                        for (int i = 1; i <= steps; i++)
                        {
                            var p = Vec3.Lerp(a, hit, (double)i / (steps + 1));
                            int t = Mesh.Bvh.Nearest(p, out var c);
                            if (t >= 0) PaintTools.Brush(Regions, t, c, radius, target, CanChange);
                        }
                    }
                    PaintTools.Brush(Regions, tri, hit, radius, target, CanChange);
                    break;

                case PaintTool.SmartFill:
                    // Dragging keeps filling every new smooth patch the cursor passes over.
                    if (tri == _lastStrokeTri || Regions.OwnerOf(tri) == target) break;
                    if (_strokeRemove) PaintTools.RemoveSmooth(Regions, tri, SmartAngleDeg, CanChange);
                    else if (CanChange(tri))
                    {
                        // Stop at tangent fillets too (edge angles stay small across them).
                        var stop = UseFeatures
                            ? FeatureTools.IsFeature(Curvature, FeatureTools.SeedBaseline(Mesh, Curvature, tri), BlendRadius)
                            : null;
                        if (stop == null || !stop(tri)) PaintTools.GrowSmooth(Regions, tri, target, SmartAngleDeg, 0, stop);
                    }
                    break;

                case PaintTool.Fillet:
                    if (tri == _lastStrokeTri || Regions.OwnerOf(tri) == target) break;
                    if (Curvature == null) { Say("Curvature is still being computed — try again in a moment."); break; }
                    if (_strokeRemove)
                    {
                        var band = new RegionSet(Mesh);
                        var tmp = band.AddRegion();
                        FeatureTools.SelectFeatureBand(band, tri, tmp.Id, Curvature, BlendRadius, false);
                        foreach (int t in band.TriangleListOf(tmp.Id)) if (Regions.OwnerOf(t) != RegionSet.None && CanChange(t)) Regions.Assign(t, RegionSet.None);
                    }
                    else if (FeatureTools.SelectFeatureBand(Regions, tri, target, Curvature, BlendRadius, ActiveRegionOnly) == 0)
                        Say("No fillet under the cursor (curvature below 1 / blend radius).");
                    break;

                case PaintTool.FillConnected:
                    if (tri == _lastStrokeTri || Regions.OwnerOf(tri) == target) break;
                    if (_strokeRemove) PaintTools.RemoveConnected(Regions, tri, CanChange);
                    else PaintTools.FillConnected(Regions, tri, target);
                    break;
            }
            _lastStrokePoint = hit;
            _lastStrokeTri = tri;
            Regions.UpdateCounts();
            RaiseChanged();
        }

        public Region EnsureRegion()
        {
            if (ActiveRegion == null || !Regions.Regions.Contains(ActiveRegion)) ActiveRegion = Regions.AddRegion();
            return ActiveRegion;
        }

        // ------------------------------------------------------------------ region commands

        public Region NewRegion()
        {
            ActiveRegion = Regions.AddRegion();
            RemoveMode = false;
            RaiseChanged();
            return ActiveRegion;
        }

        public int FillIslands(Region r, int limit = 0)
        {
            Regions.BeginStroke("Fill islands");
            int n = PaintTools.FillIslands(Regions, r.Id, limit);
            Regions.EndStroke();
            return n;
        }

        public int CleanUp(Region r)
        {
            Regions.BeginStroke("Clean up " + r.Name);
            int n = PaintTools.CleanUp(Regions, r.Id);
            Regions.EndStroke();
            return n;
        }

        /// <summary>Replaces the region's triangles with a candidate selection (one undo step).</summary>
        public void ApplyCandidate(Region r, SelectionCandidate c)
        {
            var keep = new HashSet<int>(c.Triangles);
            Regions.BeginStroke("Optimized selection");
            foreach (int t in Regions.TriangleListOf(r.Id)) if (!keep.Contains(t)) Regions.Assign(t, RegionSet.None);
            foreach (int t in c.Triangles)
            {
                int o = Regions.OwnerOf(t);
                if (o == RegionSet.None || o == r.Id) Regions.Assign(t, r.Id);
            }
            r.Fit = c.Fit;
            // Lock the type the candidate was found with, so later refits stay consistent.
            r.Request = (FitRequest)Enum.Parse(typeof(FitRequest), c.Fit.Surface.Kind.ToString());
            Regions.EndStroke();
        }

        public void ClearRegion(Region r)
        {
            Regions.BeginStroke("Clear " + r.Name);
            foreach (int t in Regions.TriangleListOf(r.Id)) Regions.Assign(t, RegionSet.None);
            Regions.EndStroke();
            r.Fit = null;
        }

        public int Ring(Region r, bool grow)
        {
            Regions.BeginStroke(grow ? "Grow ring" : "Shrink ring");
            int n = PaintTools.Dilate(Regions, r.Id, grow);
            Regions.EndStroke();
            return n;
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

        /// <summary>Which unpainted triangles growing may take (excludes adjacent fillets when enabled).</summary>
        private Func<int, bool> GrowAvailable(Region r)
        {
            Func<int, bool> free = t => Regions.OwnerOf(t) == RegionSet.None;
            if (!UseFeatures) return free;
            var feature = FeatureTools.IsFeature(Curvature, Curvature.MedianKMax(Regions.TrianglesOf(r.Id)), BlendRadius);
            return t => free(t) && !feature(t);
        }

        private int Grow(Region r)
        {
            var mask = PaintTools.MaskOf(Regions, r.Id);
            var before = (bool[])mask.Clone();
            MaskOps.GrowToSurface(Mesh, mask, GrowAvailable(r), r.Fit.Surface, FitOptions.Tolerance);
            int n = 0;
            for (int t = 0; t < mask.Length; t++) if (mask[t] && !before[t] && Regions.Assign(t, r.Id)) n++;
            return n;
        }

        public int GrowToSurface(Region r)
        {
            if (r.Fit?.Surface == null) throw new InvalidOperationException($"Fit '{r.Name}' first.");
            Regions.BeginStroke("Grow to surface");
            int n = Grow(r);
            Regions.EndStroke();
            return n;
        }

        /// <summary>
        /// Fit, grow to everything within tolerance of the fit, refit — repeated until the region stops
        /// growing. Stops at adjacent fillets when enabled. One undo step.
        /// </summary>
        public string FitAndGrow(Region r)
        {
            int start = Regions.TriangleListOf(r.Id).Count;
            if (start < 3) throw new InvalidOperationException($"'{r.Name}' has no painted triangles.");
            Regions.BeginStroke("Fit + grow");
            try
            {
                for (int pass = 0; pass < 8; pass++)
                {
                    FitRegion(r);
                    int added = Grow(r);
                    Regions.UpdateCounts();
                    if (added <= Math.Max(3, r.TriangleCount / 200)) break;
                }
                FitRegion(r);
            }
            finally { Regions.EndStroke(); }
            int end = Regions.TriangleListOf(r.Id).Count;
            return $"{r.Name}: {r.Fit.Surface.Kind}, {start:N0} → {end:N0} triangles, max dev {r.Fit.Stats.MaxAbs * 1000:0.###} mm";
        }

        public int GrowToFillets(Region r)
        {
            RequireCurvature();
            Regions.BeginStroke("Grow to fillets");
            int n = FeatureTools.GrowToFeatures(Regions, r.Id, Curvature, BlendRadius);
            Regions.EndStroke();
            return n;
        }

        public int TrimFillets(Region r)
        {
            RequireCurvature();
            Regions.BeginStroke("Trim fillets");
            int n = FeatureTools.TrimFeatures(Regions, r.Id, Curvature, BlendRadius);
            Regions.EndStroke();
            if (n > 0) r.Fit = null;
            return n;
        }

        private void RequireCurvature()
        {
            if (Curvature == null) throw new InvalidOperationException("Curvature is still being computed — try again in a moment.");
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

        /// <summary>Suggested next step for the guidance banner.</summary>
        public string NextStep()
        {
            if (!HasMesh) return "Step 1 · Load a mesh file, or select a mesh body in the part and click 'Use bodies in part'.";
            var r = ActiveRegion;
            if (r == null || r.TriangleCount == 0)
                return Tool == PaintTool.None
                    ? "Step 2 · Pick 'Smart fill' above, then click or drag on a smooth area of the panel."
                    : $"Step 2 · Click or drag to paint '{r?.Name}'. Ctrl+click = straight line, Ctrl/Shift+wheel = brush size, Shift = remove. A few dots on a cylinder are enough: MeshFit suggests the whole face.";
            if (r.Fit == null)
                return $"Step 3 · Click 'Fit + grow': it fits '{r.Name}' and extends it to every triangle on that surface.";
            if (r.FeatureName == null)
                return $"Step 4 · Check the deviation (max {r.Fit.Stats.MaxAbs * 1000:0.###} mm), then 'Create surface'. Or fix the paint and fit again.";
            return $"'{r.Name}' is done. Click 'New region' for the next surface.";
        }
    }
}
