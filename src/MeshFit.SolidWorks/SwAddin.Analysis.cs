using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Analysis;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;
using MeshFit.Core.Selection;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Region = MeshFit.Core.Selection.Region;

namespace MeshFit.SolidWorks
{
    /// <summary>Curves to 3D sketches, fillet analysis output and the CAD-vs-scan accuracy analyzer.</summary>
    public sealed partial class SwAddin
    {
        // ------------------------------------------------------------------ 3D sketches

        /// <summary>Creates one 3D sketch holding the curves as splines; returns the sketch feature name.</summary>
        internal string CreateSketch(string name, IEnumerable<FeatureCurve> curves)
        {
            var doc = _app.ActiveDoc as IModelDoc2 ?? throw new InvalidOperationException("Activate a part document first.");
            var sm = doc.SketchManager;
            doc.ClearSelection2(true);
            sm.Insert3DSketch(true);
            bool addToDb = sm.AddToDB, display = sm.DisplayWhenAdded;
            sm.AddToDB = true;          // no snapping / inferencing
            sm.DisplayWhenAdded = false;
            int n = 0;
            try
            {
                foreach (var c in curves)
                {
                    var pts = new List<Vec3>(c.Points);
                    if (c.Closed && pts.Count > 2) pts.Add(pts[0]);
                    if (pts.Count < 2) continue;
                    if (pts.Count == 2)
                        sm.CreateLine(pts[0].X, pts[0].Y, pts[0].Z, pts[1].X, pts[1].Y, pts[1].Z);
                    else
                        sm.CreateSpline2(pts.SelectMany(p => p.ToArray()).ToArray(), false);
                    n++;
                }
            }
            finally
            {
                sm.AddToDB = addToDb;
                sm.DisplayWhenAdded = display;
                sm.Insert3DSketch(true);
            }
            var feat = doc.FeatureByPositionReverse(0) as Feature;
            if (feat != null) { try { feat.Name = name; } catch { /* name taken */ } }
            doc.GraphicsRedraw2();
            return $"{feat?.Name ?? name}: {n} curve(s)";
        }

        internal string RegionBoundaryToSketch(Region r, double cornerAngleDeg)
        {
            var mask = PaintTools.MaskOf(Session.Regions, r.Id);
            var curves = FeatureCurves.RegionBoundaries(Session.Mesh, mask, new FeatureCurves.Options { CornerAngleDeg = cornerAngleDeg });
            if (curves.Count == 0) throw new InvalidOperationException($"'{r.Name}' has no usable boundary (scan border edges are skipped).");
            return CreateSketch($"{r.Name} boundary", curves);
        }

        internal string FilletToSketch(Region r, FilletAnalysis fa)
        {
            if (fa == null || fa.Stations.Count < 2) throw new InvalidOperationException("Analyze the fillet first.");
            var curves = new List<FeatureCurve>
            {
                new FeatureCurve { Points = fa.Stations.Select(s => s.Center).ToList(), Name = "spine" },
                new FeatureCurve { Points = fa.Stations.Select(s => s.Edge1).ToList(), Name = "tangent 1" },
                new FeatureCurve { Points = fa.Stations.Select(s => s.Edge2).ToList(), Name = "tangent 2" },
            };
            foreach (var c in curves) c.Points = FeatureCurves.Taubin(c.Points, false, 3);
            return CreateSketch($"{r.Name} fillet R{fa.MeanRadius * 1000:0.##}", curves);
        }

        // ------------------------------------------------------------------ accuracy analyzer

        /// <summary>
        /// Compares every visible non-mesh body in the part with the scan. Distances come from the CAD
        /// tessellation and are refined to the exact faces with IFace2.GetClosestPointOn (up to
        /// <paramref name="maxExact"/> points). Returns the report text.
        /// </summary>
        internal string AnalyzeCad(double maxDistance, int maxExact = 60000)
        {
            var part = ActivePart ?? throw new InvalidOperationException("Activate the part with the surfaces.");
            if (!Session.HasMesh) throw new InvalidOperationException("Load the scan mesh first.");
            var bodies = (part.GetBodies2((int)swBodyType_e.swAllBodies, true) as object[] ?? new object[0])
                .Cast<Body2>().Where(b => !b.IsMeshBody() && b.Visible).ToList();
            if (bodies.Count == 0) throw new InvalidOperationException("No visible surface or solid bodies to compare.");

            // Tessellate, remembering which face/body each triangle came from.
            var verts = new List<Vec3>();
            var map = new Dictionary<(float, float, float), int>();
            var idx = new List<int>();
            var triFace = new List<Face2>();
            var triBody = new List<int>();
            for (int bi = 0; bi < bodies.Count; bi++)
            {
                if (!(bodies[bi].GetFaces() is object[] faces)) continue;
                foreach (Face2 f in faces)
                {
                    if (!(f.GetTessTriangles(true) is float[] tess)) continue;
                    for (int k = 0; k + 8 < tess.Length; k += 9)
                    {
                        var tri = new int[3];
                        for (int c = 0; c < 3; c++)
                        {
                            var key = (tess[k + 3 * c], tess[k + 3 * c + 1], tess[k + 3 * c + 2]);
                            if (!map.TryGetValue(key, out int vi)) { vi = verts.Count; verts.Add(new Vec3(key.Item1, key.Item2, key.Item3)); map[key] = vi; }
                            tri[c] = vi;
                        }
                        // Same degenerate test as TriMesh so triangle indices stay aligned.
                        if (tri[0] == tri[1] || tri[1] == tri[2] || tri[0] == tri[2]) continue;
                        if ((verts[tri[1]] - verts[tri[0]]).Cross(verts[tri[2]] - verts[tri[0]]).LengthSquared <= 0) continue;
                        idx.AddRange(tri);
                        triFace.Add(f);
                        triBody.Add(bi);
                    }
                }
            }
            var cad = new TriMesh(verts.ToArray(), idx.ToArray());
            bool aligned = cad.TriangleCount == triFace.Count;

            var scan = Session.Mesh;
            double tol = Session.FitOptions.Tolerance;
            var cmp = CadComparison.Compute(scan, cad, maxDistance, tol);

            // Exact refinement on the real faces (tessellation chords can be off by more than the tolerance).
            int exact = 0;
            if (aligned)
            {
                var covered = Enumerable.Range(0, scan.VertexCount).Where(v => cmp.NearestCadTriangle[v] >= 0).ToList();
                int step = Math.Max(1, covered.Count / Math.Max(1, maxExact));
                for (int i = 0; i < covered.Count; i += step)
                {
                    int v = covered[i];
                    var p = scan.Vertices[v];
                    int t = cmp.NearestCadTriangle[v];
                    if (!(triFace[t].GetClosestPointOn(p.X, p.Y, p.Z) is double[] c) || c.Length < 3) continue;
                    double d = new Vec3(c[0], c[1], c[2]).DistanceTo(p);
                    if (cmp.Deviation[v] < 0) d = -d;
                    cmp.SetExact(v, d);
                    exact++;
                }
            }

            Session.CadResult = cmp;
            Session.ShowCadDeviation = true;
            Session.RaiseChanged();

            var lines = new List<string>();
            double coverage = cmp.Coverage();
            var all = cmp.Summarize("All bodies");
            lines.Add($"Scan covered by CAD (within {maxDistance * 1000:0.##} mm): {coverage:P1}");
            lines.Add(Format(all));
            if (aligned)
                for (int bi = 0; bi < bodies.Count; bi++)
                {
                    int b = bi;
                    var st = cmp.Summarize(bodies[bi].Name, v => cmp.NearestCadTriangle[v] >= 0 && triBody[cmp.NearestCadTriangle[v]] == b);
                    if (st.Points > 0) lines.Add(Format(st));
                }
            lines.Add(exact > 0 ? $"Distances to the exact faces on {exact:N0} points; the rest from the CAD tessellation." :
                                  "Distances from the CAD tessellation.");
            return string.Join(System.Environment.NewLine, lines);
        }

        private static string Format(CadComparison.Stats s) =>
            $"{s.Name}: RMS {s.Rms * 1000:0.###} mm, max {s.MaxAbs * 1000:0.###} mm, {s.WithinTolerance:P1} within tolerance ({s.Points:N0} pts)";

        internal bool ToggleZebra()
        {
            var doc = _app.ActiveDoc as IModelDoc2 ?? throw new InvalidOperationException("No active document.");
            var view = doc.ActiveView as ModelView ?? throw new InvalidOperationException("No active view.");
            view.DisplayZebraStripes = !view.DisplayZebraStripes;
            doc.GraphicsRedraw2();
            return view.DisplayZebraStripes;
        }

        internal void ToggleSwCurvature()
        {
            var doc = _app.ActiveDoc as IModelDoc2 ?? throw new InvalidOperationException("No active document.");
            doc.ViewDisplayCurvature();
            doc.GraphicsRedraw2();
        }
    }
}
