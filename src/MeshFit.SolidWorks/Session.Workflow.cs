using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeshFit.Core;
using MeshFit.Core.Geometry;
using MeshFit.Core.Selection;
using Region = MeshFit.Core.Selection.Region;

namespace MeshFit.SolidWorks
{
    /// <summary>Session state for the region-first workflow: wizard selections, previews and shape selection.</summary>
    internal sealed partial class Session
    {
        /// <summary>Regions chosen in the open modeling wizard; highlighted in the view and used on ✔.</summary>
        public HashSet<int> WizardRegions { get; } = new HashSet<int>();

        /// <summary>True while a modeling wizard is open (other regions are drawn dimmed).</summary>
        public bool WizardActive { get; set; }

        /// <summary>While set, a Pick Region click goes here (return true when handled) instead of activating the region.</summary>
        public Func<Region, bool> PickHandler { get; set; }

        /// <summary>Mesh Sketch preview: section polylines (grey) and the fitted lines and arcs (yellow), model space.</summary>
        public List<Vec3[]> SketchSections { get; } = new List<Vec3[]>();
        public List<Vec3[]> SketchCurves { get; } = new List<Vec3[]>();

        /// <summary>Outline of a Rectangle / Freehand selection being dragged (drawn on top of everything).</summary>
        public List<Vec3> ShapeOutline { get; } = new List<Vec3>();

        public bool IsShapeTool => Tool == PaintTool.Rectangle || Tool == PaintTool.Freehand;

        public void ClearSketchPreview()
        {
            SketchSections.Clear();
            SketchCurves.Clear();
        }

        /// <summary>
        /// Rectangle / Freehand selection: every front-facing, unobstructed triangle whose centre projects inside
        /// the screen polygon is added to (or removed from) the active region. Returns the number changed.
        /// </summary>
        public int SelectInScreenPolygon(ScreenProjector proj, IList<(double x, double y)> poly, bool remove)
        {
            if (Mesh == null || poly.Count < 3) return 0;
            if (!Mesh.BvhReady) { Say("Preparing the mesh for picking…"); return 0; }
            EnsureRegion();
            var m = Mesh;
            var toward = proj.TowardViewer;
            double minX = poly.Min(p => p.x), maxX = poly.Max(p => p.x), minY = poly.Min(p => p.y), maxY = poly.Max(p => p.y);
            double lift = 1e-3 * m.AverageEdgeLength;
            var hit = new bool[m.TriangleCount];
            Parallel.For(0, m.TriangleCount, Work.Options(), t =>
            {
                if (m.FaceNormals[t].Dot(toward) <= 0) return;
                var c = m.Centroids[t];
                proj.ToPixel(c, out double x, out double y);
                if (x < minX || x > maxX || y < minY || y > maxY || !Inside(poly, x, y)) return;
                // Visible only: nothing between the triangle and the viewer.
                if (m.Bvh.RayCast(c + toward * lift, toward, out _, 0) >= 0) return;
                hit[t] = true;
            });
            var r = ActiveRegion;
            int changed = 0;
            Regions.BeginStroke((remove ? "Remove (" : "Select (") + ToolName(Tool) + ")");
            for (int t = 0; t < hit.Length; t++)
            {
                if (!hit[t]) continue;
                int o = Regions.OwnerOf(t);
                if (remove)
                {
                    if (o == RegionSet.None || (ActiveRegionOnly && o != r.Id)) continue;
                    Regions.Assign(t, RegionSet.None);
                }
                else
                {
                    if (o == r.Id || (ActiveRegionOnly && o != RegionSet.None)) continue;
                    Regions.Assign(t, r.Id);
                }
                changed++;
            }
            Regions.EndStroke();
            if (r.Fit != null && changed > 0) r.Fit = null;
            RaiseChanged();
            return changed;
        }

        private static bool Inside(IList<(double x, double y)> poly, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }
    }
}
