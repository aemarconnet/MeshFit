using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using MeshFit.Core.Fitting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Region = MeshFit.Core.Selection.Region;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// "Region Groups" mode (as in Geomagic Design X): Auto Segment splits the scan into regions on entry,
    /// the list shows each region's surface type and fit, and the selection modes fix regions by hand.
    /// ✔ keeps the changes; ✖ undoes everything done in the mode (Redo brings it back).
    /// </summary>
    [ComVisible(true)]
    public sealed class RegionGroupsPage : PageBase
    {
        private enum Id
        {
            GSegment = 1, AngleLbl, Angle, BlendLbl, Blend, MinLbl, MinSize, Classify, AutoSegment, Resegment,
            GRegions = 20, List, Summary, NameLbl, Name, MergeLbl, MergeWith, Merge, Clear, New, Delete, Enlarge, Shrink, FillIslands, CleanUp, Extend,
            GSelect = 50, OptFlood, OptBrush, OptRect, OptFree, OptFillet, OptShell, OptPick, Remove, DiaLbl, Diameter, FloodLbl, FloodAngle,
            GDisplay = 80, ShowDeviation, ShowCurvature, ShowPreview, ShowMesh,
        }

        private PropertyManagerPageNumberbox _angle, _blend, _minSize, _diameter, _floodAngle;
        private PropertyManagerPageCheckbox _classify, _remove, _showDev, _showCurv, _showPrev, _showMesh;
        private PropertyManagerPageListbox _list;
        private PropertyManagerPageLabel _summary;
        private PropertyManagerPageTextbox _name;
        private PropertyManagerPageCombobox _mergeWith;
        private readonly Dictionary<Id, PropertyManagerPageOption> _tools = new Dictionary<Id, PropertyManagerPageOption>();
        private string[] _shownList, _shownMerge;
        private List<Region> _mergeCandidates = new List<Region>();
        private int _mergeIndex = -1;

        private double _angleDeg = 20;
        private int _minTriangles = 200;
        private bool _classifyOn = true;
        private int _startUndo;

        private static readonly (Id id, PaintTool tool, string text, string tip)[] Tools =
        {
            (Id.OptFlood, PaintTool.SmartFill, "Flood Fill", "Click or drag: fills the smooth area up to creases and (with Stop at fillets) blends"),
            (Id.OptBrush, PaintTool.Brush, "Paint Brush", "Drag to paint inside the circle. Ctrl+click = straight stroke, Ctrl/Shift+wheel = size"),
            (Id.OptRect, PaintTool.Rectangle, "Rectangle", "Drag a box: selects the visible triangles inside it"),
            (Id.OptFree, PaintTool.Freehand, "Freehand", "Drag an outline: selects the visible triangles inside it"),
            (Id.OptFillet, PaintTool.Fillet, "Fillet Band", "Click a fillet or blend to select the whole band"),
            (Id.OptShell, PaintTool.FillConnected, "Whole Shell", "Click to select a whole connected piece"),
            (Id.OptPick, PaintTool.PickRegion, "Pick Region", "Click a region to make it the active one"),
        };

        internal RegionGroupsPage(SwAddin addin) : base(addin, "Region Groups") { }

        protected override void Build()
        {
            var gs = Group((int)Id.GSegment, "Auto Segment");
            _angle = Number(gs, (int)Id.AngleLbl, (int)Id.Angle, "Crease angle", "Regions split where neighbouring triangles turn sharper than this",
                swNumberboxUnitType_e.swNumberBox_Angle, Math.PI / 180, Math.PI / 2, Math.PI / 180);
            _blend = Number(gs, (int)Id.BlendLbl, (int)Id.Blend, "Fillets up to radius", "Blends tighter than this become their own regions (needs the curvature analysis)",
                swNumberboxUnitType_e.swNumberBox_Length, 1e-4, 1, 0.001);
            _minSize = Number(gs, (int)Id.MinLbl, (int)Id.MinSize, "Smallest region (triangles)", "Smaller pieces join the neighbouring region",
                swNumberboxUnitType_e.swNumberBox_UnitlessInteger, 1, 1e6, 10);
            _classify = Check(gs, (int)Id.Classify, "Classify (fit every region)", "Fit each new region so the list shows plane, cylinder, cone, sphere, torus or freeform");
            Button(gs, (int)Id.AutoSegment, "Auto Segment", "Split the unassigned part of the scan into regions");
            Button(gs, (int)Id.Resegment, "Resegment all", "Delete every region that has no SOLIDWORKS feature yet and segment again");

            var gr = Group((int)Id.GRegions, "Regions");
            _list = List(gr, (int)Id.List, "Every region: ✔ feature created, ○ fitted within tolerance, ⚠ outside tolerance, · not fitted. Click to make it active.", 140);
            _summary = Label(gr, (int)Id.Summary, "", "Totals");
            Label(gr, (int)Id.NameLbl, "Name");
            _name = Add<PropertyManagerPageTextbox>(gr, (int)Id.Name, swPropertyManagerPageControlType_e.swControlType_Textbox, "", "Rename the active region");
            Label(gr, (int)Id.MergeLbl, "Merge with");
            _mergeWith = Add<PropertyManagerPageCombobox>(gr, (int)Id.MergeWith, swPropertyManagerPageControlType_e.swControlType_Combobox, "", "Region to merge into the active one");
            Button(gr, (int)Id.Merge, "Merge", "Move every triangle of the chosen region into the active region");
            Button(gr, (int)Id.New, "Insert region", "Start a new empty region; paint it with a selection mode");
            Button(gr, (int)Id.Clear, "Clear selection", "Unselect every triangle of the active region (Delete key in the view)");
            Button(gr, (int)Id.Delete, "Delete region", "Delete the active region");
            Button(gr, (int)Id.Extend, "Extend to similar", "Fit the active region and grow it over every connected triangle on the same surface (Esc cancels)");
            Button(gr, (int)Id.Enlarge, "Enlarge", "Add one ring of triangles around the active region");
            Button(gr, (int)Id.Shrink, "Shrink", "Remove the outer ring of triangles");
            Button(gr, (int)Id.FillIslands, "Fill islands", "Fill unpainted holes enclosed by the active region");
            Button(gr, (int)Id.CleanUp, "Clean up", "Remove specks, fill holes, smooth the jagged edge");

            var gsel = Group((int)Id.GSelect, "Selection Mode");
            foreach (var (id, _, text, tip) in Tools) _tools[id] = Option(gsel, (int)id, text, tip);
            _remove = Check(gsel, (int)Id.Remove, "Remove (unselect)", "The selection modes remove triangles instead of adding them (Shift = temporary)");
            _diameter = Number(gsel, (int)Id.DiaLbl, (int)Id.Diameter, "Brush diameter", "Paint Brush size (Ctrl/Shift + wheel in the view)",
                swNumberboxUnitType_e.swNumberBox_Length, 1e-5, 10, 0.001);
            _floodAngle = Number(gsel, (int)Id.FloodLbl, (int)Id.FloodAngle, "Flood Fill edge angle", "Flood Fill stops at edges sharper than this",
                swNumberboxUnitType_e.swNumberBox_Angle, 0.001, Math.PI / 2, Math.PI / 180);

            var gd = Group((int)Id.GDisplay, "Display", expanded: false);
            _showDev = Check(gd, (int)Id.ShowDeviation, "Deviation map", "Colour fitted regions by distance to their surface");
            _showCurv = Check(gd, (int)Id.ShowCurvature, "Curvature map", "Show fillets and feature lines");
            _showPrev = Check(gd, (int)Id.ShowPreview, "Surface preview", "Iso-lines of the fitted surfaces");
            _showMesh = Check(gd, (int)Id.ShowMesh, "Mesh", "Draw the scan");
        }

        protected override void Opened()
        {
            _startUndo = S.Regions?.UndoCount ?? 0;
            S.EnsureRegion();
            if (S.Tool == PaintTool.None) Addin.SetToolFromPage(PaintTool.SmartFill);
            // Like Design X: entering the mode segments a scan that has no regions yet.
            bool painted = S.Regions != null && S.Regions.Regions.Any(r => r.TriangleCount > 0);
            if (!painted && S.Curvature != null) Addin.Post(RunAutoSegment);
        }

        protected override void Closed(bool ok)
        {
            // Only an explicit ✖ rolls back (Design X's Cancel); Esc or opening another page keeps the regions.
            if (CancelledByUser && S.Regions != null && S.Regions.UndoCount > _startUndo)
            {
                while (S.Regions.UndoCount > _startUndo) S.Regions.Undo();
                S.RaiseChanged();
                S.Say("Region Groups cancelled: its changes were undone (Redo brings them back).");
            }
            Addin.SetToolFromPage(PaintTool.None);
        }

        protected override void RefreshControls()
        {
            var regions = S.Regions?.Regions.ToList() ?? new List<Region>();
            double tol = S.FitOptions.Tolerance;
            Message(regions.All(r => r.TriangleCount == 0)
                ? (S.Curvature == null
                    ? "The curvature analysis is still running. Click Auto Segment now to split at creases only, or wait to split at fillets too."
                    : "Click Auto Segment to split the scan into regions.")
                : "Click a region (Pick Region) or a line in the list to make it active; fix it with a selection mode. ✔ keeps the regions, ✖ undoes this session's changes.");
            SetItems(_list, regions.Select(r => RegionLine(r, tol)).ToArray(), ref _shownList);
            int idx = regions.IndexOf(S.ActiveRegion);
            if (_list != null && idx >= 0 && _list.CurrentSelection != idx) _list.CurrentSelection = (short)idx;
            if (_summary != null)
            {
                int painted = regions.Count(r => r.TriangleCount > 0), fitted = regions.Count(r => r.Fit != null);
                int bad = regions.Count(r => r.Fit?.Surface != null && r.Fit.Stats.P95 > tol);
                _summary.Caption = $"{painted} regions · {fitted} classified · {regions.Count(r => r.FeatureName != null)} built" + (bad > 0 ? $" · {bad} ⚠" : "");
            }
            if (_name != null && S.ActiveRegion != null && _name.Text != S.ActiveRegion.Name) _name.Text = S.ActiveRegion.Name;
            _mergeCandidates = regions.Where(r => r != S.ActiveRegion && r.TriangleCount > 0).ToList();
            if (_mergeWith != null)
            {
                var names = _mergeCandidates.Select(r => r.Name).ToArray();
                if (_shownMerge == null || !names.SequenceEqual(_shownMerge))
                {
                    _mergeWith.Clear();
                    if (names.Length > 0) _mergeWith.AddItems(names);
                    _shownMerge = names;
                    _mergeIndex = -1;
                }
            }
            if (_angle != null) _angle.Value = _angleDeg * Math.PI / 180;
            if (_blend != null) _blend.Value = S.BlendRadius;
            if (_minSize != null) _minSize.Value = _minTriangles;
            if (_classify != null) _classify.Checked = _classifyOn;
            foreach (var (id, tool, _, _) in Tools) if (_tools.TryGetValue(id, out var o)) o.Checked = S.Tool == tool;
            if (_remove != null) _remove.Checked = S.RemoveMode;
            if (_diameter != null) _diameter.Value = S.BrushRadius * 2;
            if (_floodAngle != null) _floodAngle.Value = S.SmartAngleDeg * Math.PI / 180;
            if (_showDev != null) _showDev.Checked = S.ShowDeviation;
            if (_showCurv != null) _showCurv.Checked = S.ShowCurvature;
            if (_showPrev != null) _showPrev.Checked = S.ShowPreview;
            if (_showMesh != null) _showMesh.Checked = S.ShowMesh;
        }

        private void RunAutoSegment() =>
            Addin.Control?.AutoSegmentRegions(_angleDeg, S.BlendRadius, _minTriangles, _classifyOn, resegment: false);

        protected override void ButtonPressed(int id)
        {
            switch ((Id)id)
            {
                case Id.AutoSegment: RunAutoSegment(); break;
                case Id.Resegment:
                    if (System.Windows.Forms.MessageBox.Show(Addin.MainWindow,
                            "Delete every region that has no SOLIDWORKS feature yet and segment the scan again?\n(Undo restores them.)",
                            "Region Groups", System.Windows.Forms.MessageBoxButtons.OKCancel) == System.Windows.Forms.DialogResult.OK)
                        Addin.Control?.AutoSegmentRegions(_angleDeg, S.BlendRadius, _minTriangles, _classifyOn, resegment: true);
                    break;
                case Id.Merge:
                    if (_mergeIndex >= 0 && _mergeIndex < _mergeCandidates.Count && S.ActiveRegion != null)
                        Addin.Control?.MergeInto(S.ActiveRegion, _mergeCandidates[_mergeIndex]);
                    else S.Say("Choose the region to merge in the 'Merge with' list first.");
                    break;
                case Id.New: Addin.Do(Cmd.NewRegion); break;
                case Id.Clear: Addin.Do(Cmd.ClearRegion); break;
                case Id.Delete: Addin.DeleteActiveRegion(); break;
                case Id.Extend: Addin.Do(Cmd.FitAndGrow); break;
                case Id.Enlarge: Addin.Do(Cmd.GrowRing); break;
                case Id.Shrink: Addin.Do(Cmd.ShrinkRing); break;
                case Id.FillIslands: Addin.Do(Cmd.FillIslands); break;
                case Id.CleanUp: Addin.Do(Cmd.CleanUp); break;
            }
        }

        protected override void OptionChecked(int id)
        {
            foreach (var (tid, tool, _, _) in Tools) if ((int)tid == id) Addin.SetToolFromPage(tool);
        }

        protected override void CheckChanged(int id, bool check)
        {
            switch ((Id)id)
            {
                case Id.Classify: _classifyOn = check; break;
                case Id.Remove: S.RemoveMode = check; Addin.Redraw(); break;
                case Id.ShowDeviation: S.ShowDeviation = check; break;
                case Id.ShowCurvature: S.ShowCurvature = check; break;
                case Id.ShowPreview: S.ShowPreview = check; break;
                case Id.ShowMesh: S.ShowMesh = check; break;
            }
            S.RaiseChanged();
        }

        protected override void NumberChanged(int id, double value)
        {
            if (value <= 0) return;
            switch ((Id)id)
            {
                case Id.Angle: _angleDeg = value * 180 / Math.PI; break;
                case Id.Blend: S.BlendRadius = value; break;
                case Id.MinSize: _minTriangles = Math.Max(1, (int)Math.Round(value)); break;
                case Id.Diameter: S.BrushRadius = value / 2; Addin.Redraw(); break;
                case Id.FloodAngle: S.SmartAngleDeg = value * 180 / Math.PI; break;
            }
        }

        protected override void TextChanged(int id, string text)
        {
            if ((Id)id != Id.Name || S.ActiveRegion == null || string.IsNullOrWhiteSpace(text)) return;
            S.ActiveRegion.Name = text.Trim();
            S.RaiseChanged();
        }

        protected override void ListChanged(int id, int item)
        {
            if ((Id)id != Id.List || S.Regions == null) return;
            var regions = S.Regions.Regions.ToList();
            if (item >= 0 && item < regions.Count && regions[item] != S.ActiveRegion) { S.ActiveRegion = regions[item]; S.RaiseChanged(); }
        }

        protected override void ComboChanged(int id, int item)
        {
            if ((Id)id == Id.MergeWith) _mergeIndex = item;
        }

        /// <summary>One list line: "✔ Hood top · Cylinder R25 · 0.042 mm".</summary>
        internal static string RegionLine(Region r, double tol)
        {
            var inv = CultureInfo.InvariantCulture;
            string mark = r.FeatureName != null ? "✔" : r.Fit?.Surface != null ? (r.Fit.Stats.P95 > tol ? "⚠" : "○") : "·";
            string type;
            switch (r.Fit?.Surface)
            {
                case null: type = r.TriangleCount == 0 ? "empty" : r.Request == FitRequest.Auto ? "not fitted" : r.Request + "?"; break;
                case SphereSurface sp: type = "Sphere R" + (sp.Radius * 1000).ToString("0.###", inv); break;
                case CylinderSurface c: type = "Cylinder R" + (c.Radius * 1000).ToString("0.###", inv); break;
                case ConeSurface co: type = "Cone " + (co.HalfAngle * 180 / Math.PI).ToString("0.##", inv) + "°"; break;
                case TorusSurface t: type = "Torus R" + (t.MajorRadius * 1000).ToString("0.##", inv) + "/r" + (t.MinorRadius * 1000).ToString("0.##", inv); break;
                case var f: type = f.Kind.ToString(); break;
            }
            string dev = r.Fit?.Surface != null ? " · " + (r.Fit.Stats.MaxAbs * 1000).ToString("0.###", inv) + " mm" : "";
            return $"{mark} {r.Name} · {type}{dev}";
        }
    }
}
