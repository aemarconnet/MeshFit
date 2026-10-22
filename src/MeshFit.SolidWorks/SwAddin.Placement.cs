using System;
using MeshFit.Core.Alignment;
using MeshFit.Core.Geometry;
using SolidWorks.Interop.sldworks;

namespace MeshFit.SolidWorks
{
    public sealed partial class SwAddin
    {
        /// <summary>
        /// Converts the first selected SOLIDWORKS entity into a mate target: reference/planar face → plane,
        /// reference axis / cylindrical face / linear or circular edge → axis, vertex → point.
        /// </summary>
        internal AlignFeature CaptureSelectedTarget()
        {
            var doc = _app.ActiveDoc as IModelDoc2 ?? throw new InvalidOperationException("No active document.");
            var sel = (ISelectionMgr)doc.SelectionManager;
            if (sel.GetSelectedObjectCount2(-1) < 1) throw new InvalidOperationException("Select a plane, planar/cylindrical face, axis, edge or vertex in SOLIDWORKS first.");
            object o = sel.GetSelectedObject6(1, -1);
            string featName = null;
            if (o is Feature f) { featName = f.Name; o = f.GetSpecificFeature2() ?? o; }

            switch (o)
            {
                case RefPlane rp:
                {
                    var a = (double[])rp.Transform.ArrayData;
                    // Rotation rows are the plane's X, Y, Z axes; Z is the normal. [9..11] = origin.
                    return AlignFeature.Plane($"Plane '{featName ?? "selected"}'", new Vec3(a[9], a[10], a[11]), new Vec3(a[6], a[7], a[8]));
                }
                case RefAxis ax:
                {
                    var p = (double[])ax.GetRefAxisParams();
                    var p0 = new Vec3(p[0], p[1], p[2]); var p1 = new Vec3(p[3], p[4], p[5]);
                    return AlignFeature.Axis($"Axis '{featName ?? "selected"}'", p0, p1 - p0);
                }
                case Face2 face:
                {
                    var surf = (Surface)face.GetSurface();
                    if (surf.IsPlane())
                    {
                        var p = (double[])surf.PlaneParams; // normal (3), root point (3)
                        return AlignFeature.Plane("Selected planar face", new Vec3(p[3], p[4], p[5]), new Vec3(p[0], p[1], p[2]));
                    }
                    if (surf.IsCylinder())
                    {
                        var p = (double[])surf.CylinderParams; // origin (3), axis (3), radius
                        return AlignFeature.Axis("Selected cylindrical face axis", new Vec3(p[0], p[1], p[2]), new Vec3(p[3], p[4], p[5]));
                    }
                    throw new InvalidOperationException("Only planar or cylindrical faces can be used as mate targets.");
                }
                case Edge edge:
                {
                    var c = (Curve)edge.GetCurve();
                    if (c.IsLine())
                    {
                        var p = (double[])c.LineParams; // root (3), direction (3)
                        return AlignFeature.Axis("Selected linear edge", new Vec3(p[0], p[1], p[2]), new Vec3(p[3], p[4], p[5]));
                    }
                    if (c.IsCircle())
                    {
                        var p = (double[])c.CircleParams; // centre (3), axis (3), radius
                        return AlignFeature.Axis("Selected circular edge axis", new Vec3(p[0], p[1], p[2]), new Vec3(p[3], p[4], p[5]));
                    }
                    throw new InvalidOperationException("Only straight or circular edges can be used as mate targets.");
                }
                case Vertex v:
                {
                    var p = (double[])v.GetPoint();
                    return AlignFeature.At("Selected vertex", new Vec3(p[0], p[1], p[2]));
                }
            }
            throw new InvalidOperationException("Unsupported selection. Use a plane, planar/cylindrical face, axis, edge or vertex.");
        }

        /// <summary>SOLIDWORKS status-bar progress for operations that block the UI thread.</summary>
        internal SwProgress BeginSwProgress(string title, int max) => new SwProgress(_app, title, max);

        internal sealed class SwProgress : IDisposable
        {
            private readonly UserProgressBar _bar;
            private readonly int _max;
            public SwProgress(ISldWorks app, string title, int max)
            {
                _max = Math.Max(1, max);
                try
                {
                    if (app.GetUserProgressBar(out var bar) && bar != null) { _bar = bar; _bar.Start(0, _max, title); }
                }
                catch { _bar = null; }
            }
            public void Update(int value, string title = null)
            {
                try
                {
                    if (_bar == null) return;
                    if (title != null) _bar.UpdateTitle(title);
                    _bar.UpdateProgress(Math.Min(_max, value));
                }
                catch { }
            }
            public void Dispose() { try { _bar?.End(); } catch { } }
        }
    }
}
