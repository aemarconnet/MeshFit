using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MeshFit.Core.Analysis;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Frame = MeshFit.Core.Geometry.Frame;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Mesh Sketch (as in Geomagic Design X): pick a base plane (a reference plane, a planar face or a planar
    /// region) and an offset; the scan's section is previewed with the fitted lines and arcs (Auto Sketch), and ✔
    /// writes them into an ordinary SOLIDWORKS sketch with relations, ready for Extrude / Revolve / Cut.
    /// </summary>
    [ComVisible(true)]
    public sealed class MeshSketchPage : PageBase
    {
        private enum Id
        {
            GPlane = 1, Base, UseRegion, PlaneInfo, OffLbl, Offset,
            GAuto = 20, TolLbl, Tol, MinLbl, MinLen, Relations, HvLbl, Hv,
            GRes = 40, Result,
        }

        private const int SelectionMark = 1;
        private PropertyManagerPageSelectionbox _base;
        private PropertyManagerPageLabel _planeInfo, _result;
        private PropertyManagerPageNumberbox _offset, _tol, _minLen, _hv;
        private PropertyManagerPageCheckbox _relations;

        private Frame? _plane;
        private Feature _planeFeature;
        private string _planeName = "none";
        private double _offsetValue, _tolValue, _minLength;
        private bool _relationsOn = true;
        private double _hvRad = Math.PI / 180;
        private MeshSketchResult _sketch;
        private string _resultText = "Select a plane or a planar face.";
        private CancellationTokenSource _cts;
        private int _generation;

        internal MeshSketchPage(SwAddin addin) : base(addin, "Mesh Sketch") { }

        protected override void Build()
        {
            var gp = Group((int)Id.GPlane, "Base Plane");
            _base = Add<PropertyManagerPageSelectionbox>(gp, (int)Id.Base, swPropertyManagerPageControlType_e.swControlType_Selectionbox, "Plane",
                "Select a reference plane (also Front / Top / Right in the flyout tree) or a planar face");
            if (_base != null)
            {
                try
                {
                    _base.SingleEntityOnly = true;
                    _base.Mark = SelectionMark;
                    _base.SetSelectionFilters(new[] { (int)swSelectType_e.swSelDATUMPLANES, (int)swSelectType_e.swSelFACES });
                    _base.Height = 20;
                }
                catch { /* cosmetic */ }
            }
            Button(gp, (int)Id.UseRegion, "Use the active region's plane", "Base the sketch on the plane fitted to the active region (fit it as a Plane first)");
            _planeInfo = Label(gp, (int)Id.PlaneInfo, "Base: none");
            _offset = Number(gp, (int)Id.OffLbl, (int)Id.Offset, "Offset distance", "Section this far from the base plane (negative = other side)",
                swNumberboxUnitType_e.swNumberBox_Length, -10, 10, 0.001);

            var ga = Group((int)Id.GAuto, "Auto Sketch");
            _tol = Number(ga, (int)Id.TolLbl, (int)Id.Tol, "Tolerance", "Lines and arcs stay this close to the section",
                swNumberboxUnitType_e.swNumberBox_Length, 1e-6, 0.01, 0.00005);
            _minLen = Number(ga, (int)Id.MinLbl, (int)Id.MinLen, "Ignore sections shorter than", "Drops scan noise and tiny islands",
                swNumberboxUnitType_e.swNumberBox_Length, 0, 1, 0.001);
            _relations = Check(ga, (int)Id.Relations, "Add relations", "Merge end points; tangent arcs; horizontal / vertical lines");
            _hv = Number(ga, (int)Id.HvLbl, (int)Id.Hv, "Horizontal / vertical within", "Lines this close to the sketch axes get a horizontal or vertical relation",
                swNumberboxUnitType_e.swNumberBox_Angle, 0, 10 * Math.PI / 180, 0.5 * Math.PI / 180);

            var gr = Group((int)Id.GRes, "Result");
            _result = Label(gr, (int)Id.Result, _resultText);
            try { if (_result != null) _result.Height = 50; } catch { /* cosmetic */ }
        }

        protected override void Opened()
        {
            // SOLIDWORKS selection must work for the plane: no paint tool while this page is open.
            Addin.SetToolFromPage(PaintTool.None);
            if (_tolValue <= 0) _tolValue = S.FitOptions.Tolerance;
            if (_minLength <= 0 && S.Mesh != null) _minLength = Math.Max(10 * S.Mesh.AverageEdgeLength, 20 * _tolValue);
            _plane = null; _planeFeature = null; _planeName = "none"; _sketch = null;
            _resultText = "Select a plane or a planar face.";
            S.ClearSketchPreview();
        }

        protected override void Closed(bool ok)
        {
            _cts?.Cancel();
            S.ClearSketchPreview();
            S.RaiseChanged();
        }

        protected override void RefreshControls()
        {
            Message("Select a base plane, set the offset, check the yellow lines and arcs on the scan, then ✔ to create the sketch.");
            if (_planeInfo != null) _planeInfo.Caption = "Base: " + _planeName;
            if (_offset != null) _offset.Value = _offsetValue;
            if (_tol != null) _tol.Value = _tolValue;
            if (_minLen != null) _minLen.Value = _minLength;
            if (_relations != null) _relations.Checked = _relationsOn;
            if (_hv != null) _hv.Value = _hvRad;
            if (_result != null) _result.Caption = _resultText;
        }

        protected override bool AcceptSelection(int id, object selection, int selType)
        {
            if (selType == (int)swSelectType_e.swSelDATUMPLANES) return true;
            return selection is Face2 f && f.GetSurface() is Surface s && s.IsPlane();
        }

        protected override void SelectionChanged(int id, int count)
        {
            if ((Id)id != Id.Base) return;
            var sel = count > 0 ? Addin.SelectedObject(SelectionMark) : null;
            if (sel != null && Addin.TryGetPlane(sel, out var frame, out var feature, out var name))
            {
                _plane = frame; _planeFeature = feature; _planeName = name;
                Recompute();
            }
            else if (count == 0) { _plane = null; _planeName = "none"; S.ClearSketchPreview(); _resultText = "Select a plane or a planar face."; S.RaiseChanged(); }
            Refresh();
        }

        protected override void ButtonPressed(int id)
        {
            if ((Id)id != Id.UseRegion) return;
            var r = S.ActiveRegion;
            if (!(r?.Fit?.Surface is PlaneSurface p)) { S.Say("Fit the active region as a Plane first (Surface Primitives → Plane → Preview)."); return; }
            _plane = Frame.FromAxis(p.Origin, p.Normal);
            _planeFeature = null;
            _planeName = $"plane of region '{r.Name}'";
            Recompute();
            Refresh();
        }

        protected override void NumberChanged(int id, double value)
        {
            switch ((Id)id)
            {
                case Id.Offset: _offsetValue = value; break;
                case Id.Tol: if (value <= 0) return; _tolValue = value; break;
                case Id.MinLen: if (value < 0) return; _minLength = value; break;
                case Id.Hv: if (value < 0) return; _hvRad = value; return;
                default: return;
            }
            Recompute();
        }

        protected override void CheckChanged(int id, bool check)
        {
            if ((Id)id == Id.Relations) _relationsOn = check;
        }

        /// <summary>Sections the scan and fits lines and arcs on a worker thread; the newest request wins.</summary>
        private void Recompute()
        {
            if (_plane == null || S.Mesh == null) return;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            int gen = ++_generation;
            var basePlane = _plane.Value;
            var frame = new Frame(basePlane.Origin + basePlane.ZAxis * _offsetValue, basePlane.ZAxis, basePlane.XAxis);
            var mesh = S.Mesh;
            double tol = _tolValue, minLen = _minLength;
            _resultText = "Sectioning…";
            Refresh();
            Task.Run(() => MeshFit.Core.Analysis.MeshSketch.Build(mesh, frame, tol, minLen, null, cts.Token), cts.Token).ContinueWith(t =>
            {
                if (cts.IsCancellationRequested || gen != _generation) return;
                Addin.Post(() =>
                {
                    if (gen != _generation || !IsOpen) return;
                    S.ClearSketchPreview();
                    if (t.IsFaulted)
                    {
                        _sketch = null;
                        _resultText = "Section failed: " + t.Exception.GetBaseException().Message;
                    }
                    else
                    {
                        _sketch = t.Result;
                        foreach (var sec in _sketch.Sections) S.SketchSections.Add(sec.Points.ToArray());
                        foreach (var loop in _sketch.Loops) foreach (var seg in loop.Segments) S.SketchCurves.Add(Sample(loop.Plane, seg));
                        _resultText = _sketch.Loops.Count == 0
                            ? "The plane does not cut the scan here. Change the offset or the plane."
                            : _sketch.Describe(1000, "mm");
                    }
                    S.RaiseChanged();
                    Refresh();
                });
            });
        }

        private static Vec3[] Sample(Frame f, ProfileSegment s)
        {
            if (!s.IsArc) return new[] { f.ToWorld(s.X0, s.Y0, 0), f.ToWorld(s.X1, s.Y1, 0) };
            double a0 = Math.Atan2(s.Y0 - s.Cy, s.X0 - s.Cx), a1 = Math.Atan2(s.Y1 - s.Cy, s.X1 - s.Cx);
            double sweep = s.CounterClockwise ? a1 - a0 : a0 - a1;
            while (sweep <= 1e-9) sweep += 2 * Math.PI;
            int n = Math.Max(8, (int)(sweep / (Math.PI / 36)));
            var pts = new Vec3[n + 1];
            for (int i = 0; i <= n; i++)
            {
                double a = a0 + (s.CounterClockwise ? 1 : -1) * sweep * i / n;
                pts[i] = f.ToWorld(s.Cx + s.Radius * Math.Cos(a), s.Cy + s.Radius * Math.Sin(a), 0);
            }
            return pts;
        }

        protected override Action Accept()
        {
            var res = _sketch;
            if (res == null || res.Loops.Count == 0) { S.Say("Mesh Sketch: nothing to sketch — select a plane that cuts the scan."); return null; }
            // Sketch directly on a selected reference plane at zero offset; otherwise on a new plane.
            var onPlane = Math.Abs(_offsetValue) < 1e-9 ? _planeFeature : null;
            bool rel = _relationsOn; double hv = _hvRad;
            return () => S.Say(Addin.CreateMeshSketch(res, onPlane, rel, hv));
        }

        protected override void AcceptedWhilePinned()
        {
            _resultText = "Created. Change the offset or the plane for the next sketch.";
            Recompute();
        }
    }
}
