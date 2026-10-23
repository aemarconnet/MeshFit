using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Analysis;
using MeshFit.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Frame = MeshFit.Core.Geometry.Frame;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Mesh Sketch on the SOLIDWORKS side: reads the base plane from the selection and writes the fitted lines
    /// and arcs into an ordinary sketch with merged end points and tangent / horizontal / vertical relations,
    /// ready for SOLIDWORKS's own Extrude, Revolve or Cut.
    /// </summary>
    public sealed partial class SwAddin
    {
        /// <summary>Base plane of a selected reference plane or planar face. False for anything else.</summary>
        internal bool TryGetPlane(object sel, out Frame frame, out Feature planeFeature, out string name)
        {
            frame = default; planeFeature = null; name = null;
            IRefPlane rp = null;
            if (sel is Feature f && f.GetTypeName2() == "RefPlane") { rp = f.GetSpecificFeature2() as IRefPlane; planeFeature = f; name = f.Name; }
            else if (sel is IRefPlane r) { rp = r; name = "reference plane"; }
            if (rp != null)
            {
                if (!(rp.CornerPoints is object[] corners) || corners.Length < 4) return false;
                var c = corners.Select(o => (double[])((MathPoint)o).ArrayData).Select(a => new Vec3(a[0], a[1], a[2])).ToArray();
                var x = c[1] - c[0];
                var n = x.Cross(c[3] - c[0]);
                if (n.Length < 1e-12 || x.Length < 1e-12) return false;
                frame = new Frame((c[0] + c[1] + c[2] + c[3]) / 4, n.Normalized(), x);
                return true;
            }
            if (sel is Face2 face && face.GetSurface() is Surface s && s.IsPlane() && s.PlaneParams is double[] pp && pp.Length >= 6)
            {
                var n = new Vec3(pp[0], pp[1], pp[2]);
                if (!face.FaceInSurfaceSense()) n = -n;
                frame = Frame.FromAxis(new Vec3(pp[3], pp[4], pp[5]), n);
                name = "planar face";
                return true;
            }
            return false;
        }

        /// <summary>The first object selected with the given selection mark (the page's selection box).</summary>
        internal object SelectedObject(int mark)
        {
            if (!(_app.ActiveDoc is IModelDoc2 doc)) return null;
            var sm = (ISelectionMgr)doc.SelectionManager;
            return sm.GetSelectedObjectCount2(mark) > 0 ? sm.GetSelectedObject6(1, mark) : null;
        }

        /// <summary>
        /// Creates the sketch. <paramref name="onPlane"/> is used when given (a reference plane at zero offset);
        /// otherwise a plane is created from the result's frame (Front/Top/Right are reused when they coincide).
        /// </summary>
        internal string CreateMeshSketch(MeshSketchResult res, Feature onPlane, bool relations, double hvToleranceRad)
        {
            var doc = RequirePartDoc();
            int n = 1;
            while (FeatureNamed(doc, $"Mesh Sketch{n}") != null) n++;
            string name = $"Mesh Sketch{n}";
            var plane = onPlane ?? CreatePlane(doc, res.Plane, name + " plane");

            var sm = doc.SketchManager;
            doc.ClearSelection2(true);
            plane.Select2(false, 0);
            sm.InsertSketch(true);
            var sketch = sm.ActiveSketch ?? throw new InvalidOperationException("Could not open a sketch on the plane.");
            var xf = sketch.ModelToSketchTransform;
            Vec3 ToSketch(Frame f, double x, double y)
            {
                var mp = (MathPoint)_math.CreatePoint(f.ToWorld(x, y, 0).ToArray());
                var a = (double[])((MathPoint)mp.MultiplyTransform(xf)).ArrayData;
                return new Vec3(a[0], a[1], 0);
            }

            bool addToDb = sm.AddToDB, disp = sm.DisplayWhenAdded;
            sm.AddToDB = true;
            sm.DisplayWhenAdded = false;
            int segments = 0, added = 0;
            var created = new List<List<(SketchSegment seg, ProfileSegment src, Vec3 a, Vec3 b)>>();
            try
            {
                foreach (var loop in res.Loops)
                {
                    var made = new List<(SketchSegment, ProfileSegment, Vec3, Vec3)>();
                    var f = loop.Plane;
                    var segs = loop.Segments;
                    if (loop.Closed && segs.Count == 2 && segs.All(s => s.IsArc) &&
                        Math.Abs(segs[0].Cx - segs[1].Cx) + Math.Abs(segs[0].Cy - segs[1].Cy) < 1e-9)
                    {
                        var c = ToSketch(f, segs[0].Cx, segs[0].Cy);
                        sm.CreateCircleByRadius(c.X, c.Y, 0, segs[0].Radius);
                        segments++;
                        created.Add(made);
                        continue;
                    }
                    foreach (var s in segs)
                    {
                        var a = ToSketch(f, s.X0, s.Y0);
                        var b = ToSketch(f, s.X1, s.Y1);
                        SketchSegment seg;
                        if (!s.IsArc) seg = sm.CreateLine(a.X, a.Y, 0, b.X, b.Y, 0);
                        else
                        {
                            var c = ToSketch(f, s.Cx, s.Cy);
                            double s0 = Math.Atan2(s.Y0 - s.Cy, s.X0 - s.Cx), s1 = Math.Atan2(s.Y1 - s.Cy, s.X1 - s.Cx);
                            double sweep = s.CounterClockwise ? s1 - s0 : s0 - s1;
                            while (sweep <= 0) sweep += 2 * Math.PI;
                            double mid = s0 + (s.CounterClockwise ? 1 : -1) * sweep / 2;
                            var mPt = ToSketch(f, s.Cx + s.Radius * Math.Cos(mid), s.Cy + s.Radius * Math.Sin(mid));
                            double cross = (a.X - c.X) * (mPt.Y - c.Y) - (a.Y - c.Y) * (mPt.X - c.X);
                            seg = sm.CreateArc(c.X, c.Y, 0, a.X, a.Y, 0, b.X, b.Y, 0, (short)(cross > 0 ? 1 : -1));
                        }
                        if (seg == null) continue;
                        segments++;
                        made.Add((seg, s, a, b));
                    }
                    created.Add(made);
                }

                if (relations)
                {
                    for (int li = 0; li < created.Count; li++)
                    {
                        var made = created[li];
                        bool closed = res.Loops[li].Closed;
                        for (int i = 0; i < made.Count; i++)
                        {
                            var cur = made[i];
                            if (!cur.src.IsArc && hvToleranceRad > 0)
                            {
                                double ang = Math.Atan2(cur.b.Y - cur.a.Y, cur.b.X - cur.a.X);
                                double toH = Math.Abs(Math.Sin(ang)), toV = Math.Abs(Math.Cos(ang));
                                if (toH < Math.Sin(hvToleranceRad)) added += Relate(doc, "sgHORIZONTAL2D", cur.seg);
                                else if (toV < Math.Sin(hvToleranceRad)) added += Relate(doc, "sgVERTICAL2D", cur.seg);
                            }
                            if (i == made.Count - 1 && !closed) break;
                            var next = made[(i + 1) % made.Count];
                            if (ReferenceEquals(next.seg, cur.seg)) break;
                            var pa = EndPointNear(cur.seg, cur.b);
                            var pb = EndPointNear(next.seg, cur.b);
                            if (pa != null && pb != null) added += Relate(doc, "sgMERGEPOINTS", pa, pb);
                            if ((cur.src.IsArc || next.src.IsArc) && SketchProfiles.JointAngle(cur.src, next.src) < 1.5 * Math.PI / 180)
                                added += Relate(doc, "sgTANGENT", cur.seg, next.seg);
                        }
                    }
                }
            }
            finally
            {
                sm.AddToDB = addToDb;
                sm.DisplayWhenAdded = disp;
                doc.ClearSelection2(true);
                sm.InsertSketch(true);
            }
            var feat = doc.FeatureByPositionReverse(0) as Feature;
            try { if (feat != null) feat.Name = name; } catch { }
            doc.GraphicsRedraw2();
            return $"{name}: {segments} curve(s) from {res.Loops.Count} section(s)" +
                   (relations ? $", {added} relation(s)" : "") + ". Extrude, revolve or cut it with SOLIDWORKS's own commands.";
        }

        private static Feature FeatureNamed(IModelDoc2 doc, string name)
        {
            for (var f = doc.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        /// <summary>The end point of a line or arc nearest to a sketch position.</summary>
        private static SketchPoint EndPointNear(SketchSegment seg, Vec3 p)
        {
            object s = null, e = null;
            if (seg is SketchLine l) { s = l.GetStartPoint2(); e = l.GetEndPoint2(); }
            else if (seg is SketchArc a) { s = a.GetStartPoint2(); e = a.GetEndPoint2(); }
            var sp = s as SketchPoint; var ep = e as SketchPoint;
            if (sp == null || ep == null) return sp ?? ep;
            double ds = (sp.X - p.X) * (sp.X - p.X) + (sp.Y - p.Y) * (sp.Y - p.Y);
            double de = (ep.X - p.X) * (ep.X - p.X) + (ep.Y - p.Y) * (ep.Y - p.Y);
            return ds <= de ? sp : ep;
        }

        /// <summary>Selects the entities and adds one sketch relation; returns 1 when the selection succeeded.</summary>
        private static int Relate(IModelDoc2 doc, string relation, params object[] entities)
        {
            try
            {
                doc.ClearSelection2(true);
                bool append = false;
                foreach (var o in entities)
                {
                    bool ok = o is SketchSegment sg ? sg.Select4(append, null) : o is SketchPoint pt && pt.Select4(append, null);
                    if (!ok) return 0;
                    append = true;
                }
                doc.SketchAddConstraints(relation);
                return 1;
            }
            catch { return 0; }
            finally { doc.ClearSelection2(true); }
        }
    }
}
