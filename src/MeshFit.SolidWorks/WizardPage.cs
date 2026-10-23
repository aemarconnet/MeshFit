using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MeshFit.Core.Fitting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Region = MeshFit.Core.Selection.Region;

namespace MeshFit.SolidWorks
{
    internal enum WizardKind { Primitives, Extrusion, Revolution, MeshFit }

    /// <summary>
    /// Modeling wizards named after Geomagic Design X's: Surface Primitives, Extrusion Wizard, Revolution Wizard
    /// and Mesh Fit. Each takes regions as input (click them on the scan), previews the result with the deviation
    /// map, and creates SOLIDWORKS features on ✔. The pushpin works as Design X's Lock: the wizard stays open.
    /// </summary>
    [ComVisible(true)]
    public sealed class WizardPage : PageBase
    {
        private enum Id
        {
            GSel = 1, List, Summary, AddActive, RemovePicked, ClearAll,
            GShape = 20, Auto, Plane, Cylinder, Cone, Sphere, Torus,
            GOpt = 40, TolLbl, Tol, Clean, ExtLbl, Extend, SnapLbl, Snap, CpuLbl, Cpu, CpvLbl, Cpv, SmoothLbl, Smooth,
            GRes = 70, Preview, Result,
        }

        private static readonly (Id id, FitRequest req, string text)[] Shapes =
        {
            (Id.Auto, FitRequest.Auto, "Automatic (simplest within tolerance)"), (Id.Plane, FitRequest.Plane, "Plane"),
            (Id.Cylinder, FitRequest.Cylinder, "Cylinder"), (Id.Cone, FitRequest.Cone, "Cone"),
            (Id.Sphere, FitRequest.Sphere, "Sphere"), (Id.Torus, FitRequest.Torus, "Torus"),
        };

        private readonly WizardKind _kind;
        private PropertyManagerPageListbox _list;
        private PropertyManagerPageLabel _summary, _result;
        private PropertyManagerPageNumberbox _tol, _extend, _snap, _cpu, _cpv, _smooth;
        private PropertyManagerPageCheckbox _clean;
        private readonly Dictionary<Id, PropertyManagerPageOption> _shapes = new Dictionary<Id, PropertyManagerPageOption>();
        private string[] _shownList;
        private int _picked = -1;
        private FitRequest _shape = FitRequest.Auto;
        private string _resultText = "Click Preview to fit the selected regions.";

        internal WizardPage(SwAddin addin, WizardKind kind) : base(addin, TitleOf(kind)) { _kind = kind; }

        internal static string TitleOf(WizardKind k) =>
            k == WizardKind.Primitives ? "Surface Primitives" : k == WizardKind.Extrusion ? "Extrusion Wizard" :
            k == WizardKind.Revolution ? "Revolution Wizard" : "Mesh Fit";

        private string Guide =>
            _kind == WizardKind.Primitives ? "Click the regions to turn into planes, cylinders, cones, spheres or tori. Preview fits them; ✔ creates the surfaces." :
            _kind == WizardKind.Extrusion ? "Click the side regions of the extruded feature. MeshFit finds the direction and sketches the profile with lines and arcs; ✔ creates the sketch and the extruded surface." :
            _kind == WizardKind.Revolution ? "Click the regions of the revolved feature. MeshFit finds the axis and sketches the profile with lines and arcs; ✔ creates the sketch and the revolved surface." :
            "Click the freeform region(s). Preview fits a B-spline surface; ✔ creates it.";

        protected override void Build()
        {
            var gs = Group((int)Id.GSel, "Regions");
            _list = List(gs, (int)Id.List, "Regions this feature is built from. Click regions on the scan to add or remove them.", 90);
            _summary = Label(gs, (int)Id.Summary, "", "");
            Button(gs, (int)Id.AddActive, "Add active region", "Add the active region (the one being painted) to the selection");
            Button(gs, (int)Id.RemovePicked, "Remove from list", "Remove the region selected in the list");
            Button(gs, (int)Id.ClearAll, "Clear", "Remove every region from the selection");

            if (_kind == WizardKind.Primitives)
            {
                var gm = Group((int)Id.GShape, "Shape");
                foreach (var (id, _, text) in Shapes) _shapes[id] = Option(gm, (int)id, text, text == "Plane" ? "Flat face" : text);
            }

            var go = Group((int)Id.GOpt, "Options");
            _tol = Number(go, (int)Id.TolLbl, (int)Id.Tol, "Tolerance", "Allowed deviation from the scan", swNumberboxUnitType_e.swNumberBox_Length, 1e-6, 0.1, 0.00005);
            if (_kind != WizardKind.MeshFit)
                _clean = Check(go, (int)Id.Clean, "Clean geometry", "Snap directions to X/Y/Z and other features, coaxial, tangent fillets, round dimensions — within tolerance");
            if (_kind == WizardKind.Primitives || _kind == WizardKind.MeshFit)
                _extend = Number(go, (int)Id.ExtLbl, (int)Id.Extend, "Extend past the region (%)", "How far the surface runs past the region's edge",
                    swNumberboxUnitType_e.swNumberBox_UnitlessDouble, 0, 200, 5);
            if (_kind == WizardKind.Extrusion || _kind == WizardKind.Revolution)
                _snap = Number(go, (int)Id.SnapLbl, (int)Id.Snap, "Snap direction to X/Y/Z within", "Directions and axes this close to a model axis become exactly parallel to it",
                    swNumberboxUnitType_e.swNumberBox_Angle, 0, 10 * Math.PI / 180, Math.PI / 180);
            if (_kind == WizardKind.MeshFit)
            {
                _cpu = Number(go, (int)Id.CpuLbl, (int)Id.Cpu, "Control points U (0 = auto)", "B-spline control points along U", swNumberboxUnitType_e.swNumberBox_UnitlessInteger, 0, 200, 1);
                _cpv = Number(go, (int)Id.CpvLbl, (int)Id.Cpv, "Control points V (0 = auto)", "B-spline control points along V", swNumberboxUnitType_e.swNumberBox_UnitlessInteger, 0, 200, 1);
                _smooth = Number(go, (int)Id.SmoothLbl, (int)Id.Smooth, "Smoothing (10^x)", "Higher (e.g. -8) = fairer, lower = closer to the scan", swNumberboxUnitType_e.swNumberBox_UnitlessInteger, -14, -2, 1);
            }

            var gr = Group((int)Id.GRes, "Result");
            Button(gr, (int)Id.Preview, "Preview", "Fit now and show the deviation map (Esc cancels)");
            _result = Label(gr, (int)Id.Result, _resultText, "Fit result");
            try { if (_result != null) _result.Height = 80; } catch { /* cosmetic */ }
        }

        protected override void Opened()
        {
            S.WizardRegions.Clear();
            if (S.ActiveRegion != null && S.ActiveRegion.TriangleCount > 0) S.WizardRegions.Add(S.ActiveRegion.Id);
            S.WizardActive = true;
            S.PickHandler = Toggle;
            _resultText = "Click Preview to fit the selected regions.";
            Addin.SetToolFromPage(PaintTool.PickRegion);
            S.RaiseChanged();
        }

        private bool Toggle(Region r)
        {
            if (!S.WizardRegions.Remove(r.Id)) S.WizardRegions.Add(r.Id);
            S.ActiveRegion = r;
            S.ClearSketchPreview();
            S.RaiseChanged();
            return true;
        }

        protected override void Closed(bool ok)
        {
            S.WizardActive = false;
            S.PickHandler = null;
            S.WizardRegions.Clear();
            S.ClearSketchPreview();
            Addin.SetToolFromPage(PaintTool.None);
            S.RaiseChanged();
        }

        private List<Region> Selected() =>
            S.Regions == null ? new List<Region>() :
            S.WizardRegions.Select(id => S.Regions.Find(id)).Where(r => r != null && r.TriangleCount > 0).ToList();

        protected override void RefreshControls()
        {
            Message(Guide);
            var sel = Selected();
            double tol = S.FitOptions.Tolerance;
            SetItems(_list, sel.Select(r => RegionGroupsPage.RegionLine(r, tol)).ToArray(), ref _shownList);
            SetCaption(_summary, sel.Count == 0 ? "No region selected." : $"{sel.Count} region(s), {sel.Sum(r => r.TriangleCount):N0} triangles");
            SetValue(_tol, tol);
            SetChecked(_clean, S.Idealize.RoundDimensions || S.Idealize.SnapToAxes);
            SetValue(_extend, S.ExtentMargin * 100);
            var c = Addin.Control;
            if (c != null)
            {
                SetValue(_snap, c.SnapDegrees * Math.PI / 180);
                SetValue(_cpu, S.FitOptions.Freeform.ControlPointsU);
                SetValue(_cpv, S.FitOptions.Freeform.ControlPointsV);
                SetValue(_smooth, Math.Round(Math.Log10(Math.Max(1e-14, S.FitOptions.Freeform.Smoothing))));
            }
            foreach (var (id, req, _) in Shapes) if (_shapes.TryGetValue(id, out var o)) SetChecked(o, _shape == req);
            SetCaption(_result, _resultText);
        }

        private FitRequest Request => _kind == WizardKind.MeshFit ? FitRequest.Freeform : _shape;

        protected override void ButtonPressed(int id)
        {
            switch ((Id)id)
            {
                case Id.AddActive:
                    if (S.ActiveRegion != null && S.ActiveRegion.TriangleCount > 0) { S.WizardRegions.Add(S.ActiveRegion.Id); S.RaiseChanged(); }
                    break;
                case Id.RemovePicked:
                    var sel = Selected();
                    if (_picked >= 0 && _picked < sel.Count) { S.WizardRegions.Remove(sel[_picked].Id); _picked = -1; S.RaiseChanged(); }
                    break;
                case Id.ClearAll: S.WizardRegions.Clear(); S.ClearSketchPreview(); S.RaiseChanged(); break;
                case Id.Preview:
                    var regions = Selected();
                    if (regions.Count == 0) { S.Say("Click one or more regions on the scan first."); return; }
                    _resultText = "Fitting…";
                    Refresh();
                    Addin.Control?.WizardPreview(_kind, regions, Request, text => { _resultText = text; Refresh(); });
                    break;
            }
        }

        protected override Action Accept()
        {
            var regions = Selected();
            if (regions.Count == 0) { S.Say($"{Title}: no region selected, nothing created."); return null; }
            var kind = _kind; var req = Request;
            return () => Addin.Control?.WizardCreate(kind, regions, req, text => { _resultText = text; });
        }

        protected override void AcceptedWhilePinned()
        {
            // Locked: start the next feature with an empty selection.
            S.WizardRegions.Clear();
            S.ClearSketchPreview();
            _resultText = "Created. Click the regions of the next feature.";
            S.RaiseChanged();
            Refresh();
        }

        protected override void OptionChecked(int id)
        {
            foreach (var (sid, req, _) in Shapes) if ((int)sid == id) _shape = req;
        }

        protected override void CheckChanged(int id, bool check)
        {
            if ((Id)id == Id.Clean)
                S.Idealize.RoundDimensions = S.Idealize.SnapToAxes = S.Idealize.SnapToFeatures = S.Idealize.Tangency = check;
        }

        protected override void NumberChanged(int id, double value)
        {
            var c = Addin.Control;
            switch ((Id)id)
            {
                case Id.Tol: if (value > 0) Addin.SetTolerance(value); break;
                case Id.Extend: if (value >= 0) c?.SetExtendPercent(value); break;
                case Id.Snap: if (value >= 0 && c != null) c.SnapDegrees = value * 180 / Math.PI; break;
                case Id.Cpu: c?.SetFreeform((int)Math.Round(value), null, null); break;
                case Id.Cpv: c?.SetFreeform(null, (int)Math.Round(value), null); break;
                case Id.Smooth: c?.SetFreeform(null, null, (int)Math.Round(value)); break;
            }
        }

        protected override void ListChanged(int id, int item)
        {
            if ((Id)id == Id.List) _picked = item;
        }
    }
}
