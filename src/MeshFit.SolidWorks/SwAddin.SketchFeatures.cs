using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Analysis;
using MeshFit.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Region = MeshFit.Core.Selection.Region;
using Frame = MeshFit.Core.Geometry.Frame;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Builds editable SOLIDWORKS features from a region: a reference plane, a 2D sketch made of lines and
    /// arcs only, and an extruded or revolved surface. The result is checked against the scan and rebuilt
    /// in the other direction if SOLIDWORKS extruded/revolved the wrong way.
    /// </summary>
    public sealed partial class SwAddin
    {
        internal string CreateExtrudedSurface(Region r, ExtrusionProfile p, Action<string, double?> progress = null)
        {
            var doc = RequirePartDoc();
            progress?.Invoke("Creating the sketch plane", 0.1);
            var plane = CreatePlane(doc, p.Plane, $"{r.Name} sketch plane");
            progress?.Invoke("Sketching lines and arcs", 0.3);
            var sketch = CreateProfileSketch(doc, plane, p, $"{r.Name} profile", null);
            progress?.Invoke("Extruding the surface", 0.6);
            Feature feat = null;
            foreach (bool reverse in new[] { false, true })
            {
                doc.ClearSelection2(true);
                sketch.Select2(false, 0);
                doc.FeatureManager.FeatureExtruRefSurface3(true, reverse, (int)swStartConditions_e.swStartSketchPlane, 0,
                    (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind, p.Depth, 0,
                    false, false, false, false, 0, 0, false, false, false, false, false, false, false, false);
                feat = doc.FeatureByPositionReverse(0) as Feature;
                if (feat == null || feat == sketch) throw new InvalidOperationException("SOLIDWORKS did not create the extruded surface.");
                if (CoversRegion(feat, r)) break;
                if (reverse) break;          // keep the second attempt either way
                DeleteFeature(doc, feat);
            }
            return Finish(doc, feat, r, $"{r.Name} extrude", $"extruded surface, depth {p.Depth * 1000:0.###} mm");
        }

        internal string CreateRevolvedSurface(Region r, RevolutionProfile p, Action<string, double?> progress = null)
        {
            var doc = RequirePartDoc();
            progress?.Invoke("Creating the sketch plane through the axis", 0.1);
            var plane = CreatePlane(doc, p.Plane, $"{r.Name} revolve plane");
            progress?.Invoke("Sketching lines, arcs and the axis", 0.3);
            // Centre line along the axis (sketch x = 0), spanning the profile height.
            double y0 = p.Segments.Min(s => Math.Min(s.Y0, s.Y1)), y1 = p.Segments.Max(s => Math.Max(s.Y0, s.Y1));
            double pad = 0.1 * (y1 - y0 + 1e-6);
            SketchSegment centre = null;
            var sketch = CreateProfileSketch(doc, plane, p, $"{r.Name} profile", (sm, toSketch) =>
            {
                var a = toSketch(p.To3D(0, y0 - pad)); var b = toSketch(p.To3D(0, y1 + pad));
                centre = sm.CreateCenterLine(a.X, a.Y, 0, b.X, b.Y, 0);
            });
            string centreName = centre?.GetName();
            progress?.Invoke("Revolving the surface", 0.6);
            bool full = p.Sweep >= 2 * Math.PI - 1e-6;
            Feature feat = null;
            foreach (bool reverse in full ? new[] { false } : new[] { false, true })
            {
                doc.ClearSelection2(true);
                sketch.Select2(false, 0);
                if (centreName != null)
                    doc.Extension.SelectByID2($"{centreName}@{sketch.Name}", "EXTSKETCHSEGMENT", 0, 0, 0, true, 4, null, 0);
                feat = doc.FeatureManager.FeatureRevolve2(true, false, false, false, reverse, false,
                    (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind, Math.Min(p.Sweep, 2 * Math.PI), 0,
                    false, false, 0, 0, 0, 0, 0, true, false, true);
                if (feat == null) throw new InvalidOperationException("SOLIDWORKS did not create the revolved surface.");
                if (full || CoversRegion(feat, r)) break;
                if (reverse) break;
                DeleteFeature(doc, feat);
            }
            return Finish(doc, feat, r, $"{r.Name} revolve", $"revolved surface, {p.Sweep * 180 / Math.PI:0.#}°");
        }

        // ------------------------------------------------------------------ helpers

        private IModelDoc2 RequirePartDoc() =>
            (ActivePart as IModelDoc2) ?? throw new InvalidOperationException("Activate the part to build the features in.");

        /// <summary>
        /// A reference plane through the frame's origin with the frame's normal: a standard plane when it
        /// coincides with Front/Top/Right, otherwise a plane through three points of a helper 3D sketch.
        /// </summary>
        private Feature CreatePlane(IModelDoc2 doc, Frame f, string name)
        {
            var standards = new[] { ("Front Plane", Vec3.UnitZ), ("Top Plane", Vec3.UnitY), ("Right Plane", Vec3.UnitX) };
            foreach (var (std, n) in standards)
            {
                if (Math.Abs(f.ZAxis.Dot(n)) > Math.Cos(1e-3) && Math.Abs(f.Origin.Dot(n)) < 1e-7)
                {
                    var fe = FindStandardPlane(doc, n);
                    if (fe != null) return fe;
                }
            }
            double l = 0.05;
            var pts = new[] { f.Origin, f.Origin + f.XAxis * l, f.Origin + f.YAxis * l };
            var sm = doc.SketchManager;
            doc.ClearSelection2(true);
            sm.Insert3DSketch(true);
            bool addToDb = sm.AddToDB;
            sm.AddToDB = true;
            var sp = pts.Select(q => sm.CreatePoint(q.X, q.Y, q.Z)).ToArray();
            sm.AddToDB = addToDb;
            sm.Insert3DSketch(true);
            var helper = doc.FeatureByPositionReverse(0) as Feature;
            try { helper.Name = name + " points"; } catch { }

            doc.ClearSelection2(true);
            var sel = (ISelectionMgr)doc.SelectionManager;
            for (int i = 0; i < 3; i++)
            {
                var data = sel.CreateSelectData();
                data.Mark = i;
                if (sp[i] == null || !sp[i].Select4(true, data))
                    throw new InvalidOperationException("Could not select the helper points for the sketch plane.");
            }
            int c = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident;
            var plane = doc.FeatureManager.InsertRefPlane(c, 0, c, 0, c, 0) as Feature;
            if (plane == null) throw new InvalidOperationException("SOLIDWORKS could not create the sketch plane.");
            try { plane.Name = name; } catch { }
            doc.ClearSelection2(true);
            return plane;
        }

        private static Feature FindStandardPlane(IModelDoc2 doc, Vec3 normal)
        {
            // The first three reference planes of a part are Front, Top, Right (names are localised).
            var planes = new List<Feature>();
            for (var f = doc.FirstFeature() as Feature; f != null && planes.Count < 3; f = f.GetNextFeature() as Feature)
                if (f.GetTypeName2() == "RefPlane") planes.Add(f);
            int idx = normal.Z != 0 ? 0 : normal.Y != 0 ? 1 : 2;
            return planes.Count == 3 ? planes[idx] : null;
        }

        /// <summary>Sketch on the plane with the profile's lines and arcs (model → sketch coordinates via the sketch transform).</summary>
        private Feature CreateProfileSketch(IModelDoc2 doc, Feature plane, SketchProfile p, string name,
                                           Action<SketchManager, Func<Vec3, Vec3>> extra)
        {
            var sm = doc.SketchManager;
            doc.ClearSelection2(true);
            plane.Select2(false, 0);
            sm.InsertSketch(true);
            var sketch = sm.ActiveSketch ?? throw new InvalidOperationException("Could not open a sketch on the plane.");
            var xf = sketch.ModelToSketchTransform;
            Func<Vec3, Vec3> toSketch = q =>
            {
                var mp = (MathPoint)_math.CreatePoint(q.ToArray());
                var a = (double[])((MathPoint)mp.MultiplyTransform(xf)).ArrayData;
                return new Vec3(a[0], a[1], a[2]);
            };
            bool addToDb = sm.AddToDB, disp = sm.DisplayWhenAdded;
            sm.AddToDB = true;
            sm.DisplayWhenAdded = false;
            try
            {
                foreach (var s in p.Segments)
                {
                    var a = toSketch(p.To3D(s.X0, s.Y0));
                    var b = toSketch(p.To3D(s.X1, s.Y1));
                    if (!s.IsArc) { sm.CreateLine(a.X, a.Y, 0, b.X, b.Y, 0); continue; }
                    var c = toSketch(p.To3D(s.Cx, s.Cy));
                    // Direction in sketch coordinates (the sketch may be mirrored relative to the profile frame).
                    double s0 = Math.Atan2(s.Y0 - s.Cy, s.X0 - s.Cx), s1 = Math.Atan2(s.Y1 - s.Cy, s.X1 - s.Cx);
                    double sweep = s.CounterClockwise ? s1 - s0 : s0 - s1;
                    while (sweep <= 0) sweep += 2 * Math.PI;
                    double mid = s0 + (s.CounterClockwise ? 1 : -1) * sweep / 2;
                    var m = toSketch(p.To3D(s.Cx + s.Radius * Math.Cos(mid), s.Cy + s.Radius * Math.Sin(mid)));
                    double cross = (a.X - c.X) * (m.Y - c.Y) - (a.Y - c.Y) * (m.X - c.X);
                    sm.CreateArc(c.X, c.Y, 0, a.X, a.Y, 0, b.X, b.Y, 0, (short)(cross > 0 ? 1 : -1));
                }
                extra?.Invoke(sm, toSketch);
            }
            finally
            {
                sm.AddToDB = addToDb;
                sm.DisplayWhenAdded = disp;
                sm.InsertSketch(true);
            }
            var feat = doc.FeatureByPositionReverse(0) as Feature;
            try { feat.Name = name; } catch { }
            return feat;
        }

        /// <summary>True when the feature's faces pass through the region (median distance well within the tolerance).</summary>
        private bool CoversRegion(Feature feat, Region r)
        {
            if (!(feat.GetFaces() is object[] faces) || faces.Length == 0) return false;
            var pts = Session.RegionPoints(r, 40);
            var d = new List<double>();
            foreach (var q in pts)
            {
                double best = double.MaxValue;
                foreach (Face2 f in faces)
                    if (f.GetClosestPointOn(q.X, q.Y, q.Z) is double[] c && c.Length >= 3)
                        best = Math.Min(best, new Vec3(c[0], c[1], c[2]).DistanceTo(q));
                d.Add(best);
            }
            d.Sort();
            return d[d.Count / 2] < Math.Max(5 * Session.FitOptions.Tolerance, 1e-4);
        }

        private static void DeleteFeature(IModelDoc2 doc, Feature f)
        {
            doc.ClearSelection2(true);
            f.Select2(false, 0);
            doc.Extension.DeleteSelection2(0);
        }

        private string Finish(IModelDoc2 doc, Feature feat, Region r, string name, string what)
        {
            try { feat.Name = name; } catch { }
            r.FeatureName = feat.Name;
            r.SurfaceHidden = false;
            doc.ClearSelection2(true);
            doc.GraphicsRedraw2();
            Session.RaiseChanged();
            return $"{r.Name}: {what} — editable sketch '{r.Name} profile'";
        }
    }
}
