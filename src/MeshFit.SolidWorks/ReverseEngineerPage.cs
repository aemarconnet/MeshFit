using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MeshFit.Core.Fitting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using Region = MeshFit.Core.Selection.Region;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// The "Reverse Engineer" PropertyManager page: the native SOLIDWORKS home of the feature loop.
    /// Paint in the graphics area, pick the surface type, Fit + grow, then ✔ OK creates the feature and the
    /// page reopens on a new region for the next feature (✖ closes it).
    /// </summary>
    [ComVisible(true)]
    public sealed class ReverseEngineerPage : IPropertyManagerPage2Handler9
    {
        private enum Id
        {
            GFeature = 1, Region, NewRegion, DeleteRegion, RegionSummary, RegionName, ClearRegion,
            GPaint = 10, OptSmart, OptBrush, OptFillet, OptShell, OptPick, OptOff, Remove, Diameter, Angle, FillIslands, CleanUp, GrowRing, ShrinkRing,
            GSurface = 40, TypeAuto, TypePlane, TypeSphere, TypeCylinder, TypeCone, TypeTorus, TypeFree, TypeLabel, Tolerance, Clean, StopFillets, FitGrow, Result,
            GCreate = 70, CreateSurface, CreateExtrude, CreateRevolve, NextAfterOk,
            GDisplay = 80, ShowDeviation, ShowCurvature, ShowPreview, ShowMesh
        }

        private readonly SwAddin _addin;
        private Session S => _addin.Session;
        private PropertyManagerPage2 _page;
        private bool _updating, _open, _reopen;
        private int _closeReason;

        private PropertyManagerPageListbox _region;
        private PropertyManagerPageLabel _regionSummary;
        private PropertyManagerPageTextbox _regionName;
        private string[] _regionItems = new string[0];
        private readonly Dictionary<Id, PropertyManagerPageOption> _tools = new Dictionary<Id, PropertyManagerPageOption>();
        private PropertyManagerPageCheckbox _remove, _clean, _stopFillets, _nextAfterOk, _showDev, _showCurv, _showPrev, _showMesh;
        private PropertyManagerPageNumberbox _diameter, _angle, _tolerance;
        private readonly Dictionary<Id, PropertyManagerPageBitmapButton> _types = new Dictionary<Id, PropertyManagerPageBitmapButton>();
        private PropertyManagerPageLabel _typeLabel, _result;
        private PropertyManagerPageOption _createSurface, _createExtrude, _createRevolve;

        private static readonly (Id id, FitRequest req, string tip)[] TypeButtons =
        {
            (Id.TypeAuto, FitRequest.Auto, "Auto: simplest surface within tolerance"),
            (Id.TypePlane, FitRequest.Plane, "Plane"),
            (Id.TypeSphere, FitRequest.Sphere, "Sphere"),
            (Id.TypeCylinder, FitRequest.Cylinder, "Cylinder"),
            (Id.TypeCone, FitRequest.Cone, "Cone"),
            (Id.TypeTorus, FitRequest.Torus, "Torus"),
            (Id.TypeFree, FitRequest.Freeform, "Freeform (B-spline)"),
        };

        internal enum CreateKind { Surface, Extrude, Revolve }
        internal CreateKind Create { get; private set; } = CreateKind.Surface;
        internal bool NextAfterOk { get; private set; } = true;

        public ReverseEngineerPage(SwAddin addin) { _addin = addin; }

        public bool IsOpen => _open;

        // ------------------------------------------------------------------ build / show

        public void Show()
        {
            if (_open) return;
            if (!S.HasMesh) { S.Say("Load a mesh first (MeshFit tab → Load Mesh)."); return; }
            int err = 0;
            int opts = (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton |
                             swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton |
                             swPropertyManagerPageOptions_e.swPropertyManagerOptions_UndoButton |
                             swPropertyManagerPageOptions_e.swPropertyManagerOptions_RedoButton |
                             swPropertyManagerPageOptions_e.swPropertyManagerOptions_LockedPage |
                             swPropertyManagerPageOptions_e.swPropertyManagerOptions_PushpinButton);
            _page = _addin.App.CreatePropertyManagerPage("Reverse Engineer", opts, this, ref err) as PropertyManagerPage2;
            if (_page == null) throw new InvalidOperationException("SOLIDWORKS could not create the PropertyManager page (error " + err + "). Open a part document and try again.");
            Build();
            _open = true;
            S.EnsureRegion();
            if (S.Tool == PaintTool.None || S.Tool == PaintTool.PickRegion) _addin.SetToolFromPage(PaintTool.SmartFill);
            Refresh();
            _page.Show2(0);
        }

        private void Build()
        {
            int vis = (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded);
            int ctl = (int)(swAddControlOptions_e.swControlOptions_Visible | swAddControlOptions_e.swControlOptions_Enabled);
            short left = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge;
            T Add<T>(PropertyManagerPageGroup g, Id id, swPropertyManagerPageControlType_e type, string caption, string tip) where T : class =>
                g.AddControl2((int)id, (short)type, caption, left, ctl, tip) as T;

            // Feature
            // Running list of every region: type, size and fit quality. Click one to make it active.
            var gf = (PropertyManagerPageGroup)_page.AddGroupBox((int)Id.GFeature, "Regions", vis);
            _region = Add<PropertyManagerPageListbox>(gf, Id.Region, swPropertyManagerPageControlType_e.swControlType_Listbox, "",
                "All regions: surface type, main size, largest deviation and status (✔ = surface created, ⚠ = outside tolerance). Click to make a region active.");
            if (_region != null) _region.Height = 120;
            _regionSummary = Add<PropertyManagerPageLabel>(gf, Id.RegionSummary, swPropertyManagerPageControlType_e.swControlType_Label, "", "Totals");
            _regionName = Add<PropertyManagerPageTextbox>(gf, Id.RegionName, swPropertyManagerPageControlType_e.swControlType_Textbox, "", "Name of the active region (type to rename)");
            Add<PropertyManagerPageButton>(gf, Id.ClearRegion, swPropertyManagerPageControlType_e.swControlType_Button, "Clear selection", "Unselect every triangle of the active region (Delete key in the view; Undo restores it)");
            Add<PropertyManagerPageButton>(gf, Id.NewRegion, swPropertyManagerPageControlType_e.swControlType_Button, "New region", "Start a new region for the next feature");
            Add<PropertyManagerPageButton>(gf, Id.DeleteRegion, swPropertyManagerPageControlType_e.swControlType_Button, "Delete region", "Delete the active region (Undo restores it)");

            // Paint
            var gp = (PropertyManagerPageGroup)_page.AddGroupBox((int)Id.GPaint, "Paint", vis);
            foreach (var (id, text, tip) in new[]
            {
                (Id.OptSmart, "Smart fill", "Click or drag: fills smooth areas up to sharp edges and fillets"),
                (Id.OptBrush, "Brush", "Drag to paint. Ctrl+click = straight line, Ctrl/Shift+wheel = size"),
                (Id.OptFillet, "Fillet band", "Click a fillet to select the whole blend band"),
                (Id.OptShell, "Whole shell", "Click to select a whole connected piece"),
                (Id.OptPick, "Pick region", "Click painted triangles to make their region active"),
                (Id.OptOff, "No tool", "Use SOLIDWORKS normally (rotate, select)"),
            })
                _tools[id] = Add<PropertyManagerPageOption>(gp, id, swPropertyManagerPageControlType_e.swControlType_Option, text, tip);
            _remove = Add<PropertyManagerPageCheckbox>(gp, Id.Remove, swPropertyManagerPageControlType_e.swControlType_Checkbox, "Remove (unselect)", "Tools unselect instead of paint (Shift = temporary)");
            Add<PropertyManagerPageLabel>(gp, (Id)99, swPropertyManagerPageControlType_e.swControlType_Label, "Brush diameter", "");
            _diameter = Add<PropertyManagerPageNumberbox>(gp, Id.Diameter, swPropertyManagerPageControlType_e.swControlType_Numberbox, "", "Brush diameter (Ctrl/Shift + wheel in the graphics area)");
            _diameter?.SetRange2((int)swNumberboxUnitType_e.swNumberBox_Length, 1e-5, 10, true, 0.001, 0.005, 0.0002);
            Add<PropertyManagerPageLabel>(gp, (Id)98, swPropertyManagerPageControlType_e.swControlType_Label, "Smart fill edge angle", "");
            _angle = Add<PropertyManagerPageNumberbox>(gp, Id.Angle, swPropertyManagerPageControlType_e.swControlType_Numberbox, "", "Smart fill stops at edges sharper than this");
            _angle?.SetRange2((int)swNumberboxUnitType_e.swNumberBox_Angle, 0.001, Math.PI / 2, true, Math.PI / 180, 5 * Math.PI / 180, 0.1 * Math.PI / 180);
            Add<PropertyManagerPageButton>(gp, Id.FillIslands, swPropertyManagerPageControlType_e.swControlType_Button, "Fill islands", "Fill unpainted holes enclosed by the region");
            Add<PropertyManagerPageButton>(gp, Id.CleanUp, swPropertyManagerPageControlType_e.swControlType_Button, "Clean up paint", "Remove specks, fill holes, smooth the brush edge");
            Add<PropertyManagerPageButton>(gp, Id.GrowRing, swPropertyManagerPageControlType_e.swControlType_Button, "Grow edge", "Add one ring of triangles");
            Add<PropertyManagerPageButton>(gp, Id.ShrinkRing, swPropertyManagerPageControlType_e.swControlType_Button, "Shrink edge", "Remove the outer ring of triangles");

            // Surface
            var gs = (PropertyManagerPageGroup)_page.AddGroupBox((int)Id.GSurface, "Surface", vis);
            string dir = Path.Combine(Path.GetTempPath(), "MeshFitIcons");
            Directory.CreateDirectory(dir);
            foreach (var (id, req, tip) in TypeButtons)
            {
                var b = Add<PropertyManagerPageBitmapButton>(gs, id, swPropertyManagerPageControlType_e.swControlType_CheckableBitmapButton, "", tip);
                if (b == null) continue;
                var files = new[] { 20, 32, 40 }.Select(sz =>
                {
                    string f = Path.Combine(dir, $"type_{req}_{sz}.png");
                    using (var src = ShapeIcons.Draw(req))
                    using (var bmp = new Bitmap(sz, sz))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, sz, sz);
                        bmp.Save(f, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    return f;
                }).ToArray();
                b.SetBitmapsByName3(files, null);
                _types[id] = b;
            }
            _typeLabel = Add<PropertyManagerPageLabel>(gs, Id.TypeLabel, swPropertyManagerPageControlType_e.swControlType_Label, "Type: Auto", "Surface type for this region");
            Add<PropertyManagerPageLabel>(gs, (Id)97, swPropertyManagerPageControlType_e.swControlType_Label, "Tolerance", "");
            _tolerance = Add<PropertyManagerPageNumberbox>(gs, Id.Tolerance, swPropertyManagerPageControlType_e.swControlType_Numberbox, "", "Allowed deviation from the scan");
            _tolerance?.SetRange2((int)swNumberboxUnitType_e.swNumberBox_Length, 1e-6, 0.1, true, 0.00005, 0.0002, 0.00001);
            _clean = Add<PropertyManagerPageCheckbox>(gs, Id.Clean, swPropertyManagerPageControlType_e.swControlType_Checkbox, "Clean geometry", "Snap directions to X/Y/Z and other features, coaxial, tangent fillets and round dimensions — within tolerance");
            _stopFillets = Add<PropertyManagerPageCheckbox>(gs, Id.StopFillets, swPropertyManagerPageControlType_e.swControlType_Checkbox, "Stop at fillets", "Growing stops where a tangent fillet / blend starts");
            Add<PropertyManagerPageButton>(gs, Id.FitGrow, swPropertyManagerPageControlType_e.swControlType_Button, "Fit + grow", "Fit the region and grow it over its surface (Esc cancels)");
            _result = Add<PropertyManagerPageLabel>(gs, Id.Result, swPropertyManagerPageControlType_e.swControlType_Label, "Not fitted yet.", "Fit result");
            if (_result != null) _result.Height = 70;

            // Create
            var gc = (PropertyManagerPageGroup)_page.AddGroupBox((int)Id.GCreate, "Create (✔ OK)", vis);
            _createSurface = Add<PropertyManagerPageOption>(gc, Id.CreateSurface, swPropertyManagerPageControlType_e.swControlType_Option, "Fitted surface", "True plane / cylinder / cone / sphere / torus, or B-spline");
            _createExtrude = Add<PropertyManagerPageOption>(gc, Id.CreateExtrude, swPropertyManagerPageControlType_e.swControlType_Option, "Sketch + extruded surface", "Editable sketch (lines and arcs) on a plane, extruded");
            _createRevolve = Add<PropertyManagerPageOption>(gc, Id.CreateRevolve, swPropertyManagerPageControlType_e.swControlType_Option, "Sketch + revolved surface", "Editable sketch (lines and arcs) through the axis, revolved");
            _nextAfterOk = Add<PropertyManagerPageCheckbox>(gc, Id.NextAfterOk, swPropertyManagerPageControlType_e.swControlType_Checkbox, "Continue with the next feature", "After ✔ OK: hide the new surface and reopen this page on a new region");

            // Display
            var gd = (PropertyManagerPageGroup)_page.AddGroupBox((int)Id.GDisplay, "Display", (int)swAddGroupBoxOptions_e.swGroupBoxOptions_Visible);
            _showDev = Add<PropertyManagerPageCheckbox>(gd, Id.ShowDeviation, swPropertyManagerPageControlType_e.swControlType_Checkbox, "Deviation map", "Colour fitted regions by distance to their surface");
            _showCurv = Add<PropertyManagerPageCheckbox>(gd, Id.ShowCurvature, swPropertyManagerPageControlType_e.swControlType_Checkbox, "Curvature map", "Show fillets and feature lines");
            _showPrev = Add<PropertyManagerPageCheckbox>(gd, Id.ShowPreview, swPropertyManagerPageControlType_e.swControlType_Checkbox, "Surface preview", "Iso-lines of the fitted surfaces");
            _showMesh = Add<PropertyManagerPageCheckbox>(gd, Id.ShowMesh, swPropertyManagerPageControlType_e.swControlType_Checkbox, "Mesh overlay", "Draw the scan");
        }

        /// <summary>Copies the session state into the page controls.</summary>
        public void Refresh()
        {
            if (!_open || _page == null) return;
            _updating = true;
            try
            {
                _page.SetMessage3(S.NextStep(), (int)swPropertyManagerPageMessageVisibility.swMessageBoxVisible,
                    (int)swPropertyManagerPageMessageExpanded.swMessageBoxExpand, "MeshFit");
                if (_region != null && S.Regions != null)
                {
                    var regions = S.Regions.Regions.ToList();
                    double tol = S.FitOptions.Tolerance;
                    var items = regions.Select(r => RegionLine(r, tol)).ToArray();
                    // Rebuilding the list on every brush dab would flicker: only when a line changed.
                    if (!items.SequenceEqual(_regionItems))
                    {
                        _region.Clear();
                        if (items.Length > 0) _region.AddItems(items);
                        _regionItems = items;
                    }
                    if (_regionName != null && S.ActiveRegion != null && _regionName.Text != S.ActiveRegion.Name) _regionName.Text = S.ActiveRegion.Name;
                    int idx = regions.IndexOf(S.ActiveRegion);
                    if (idx >= 0 && _region.CurrentSelection != idx) _region.CurrentSelection = (short)idx;
                    if (_regionSummary != null)
                    {
                        int painted = regions.Count(r => r.TriangleCount > 0), built = regions.Count(r => r.FeatureName != null);
                        int bad = regions.Count(r => r.Fit?.Surface != null && r.Fit.Stats.P95 > tol);
                        _regionSummary.Caption = $"{painted} painted · {regions.Count(r => r.Fit != null)} fitted · {built} created" + (bad > 0 ? $" · {bad} ⚠" : "");
                    }
                }
                var tool = S.Tool;
                foreach (var kv in _tools)
                    kv.Value.Checked = (kv.Key == Id.OptSmart && tool == PaintTool.SmartFill) || (kv.Key == Id.OptBrush && tool == PaintTool.Brush) ||
                                       (kv.Key == Id.OptFillet && tool == PaintTool.Fillet) || (kv.Key == Id.OptShell && tool == PaintTool.FillConnected) ||
                                       (kv.Key == Id.OptPick && tool == PaintTool.PickRegion) || (kv.Key == Id.OptOff && tool == PaintTool.None);
                if (_remove != null) _remove.Checked = S.RemoveMode;
                if (_diameter != null) _diameter.Value = S.BrushRadius * 2;
                if (_angle != null) _angle.Value = S.SmartAngleDeg * Math.PI / 180;
                if (_tolerance != null) _tolerance.Value = S.FitOptions.Tolerance;
                var req = S.ActiveRegion?.Request ?? FitRequest.Auto;
                foreach (var (id, r, tip) in TypeButtons) if (_types.TryGetValue(id, out var b)) b.Checked = r == req;
                if (_typeLabel != null) _typeLabel.Caption = "Type: " + (req == FitRequest.Freeform ? "Freeform" : req.ToString());
                if (_clean != null) _clean.Checked = S.Idealize.RoundDimensions || S.Idealize.SnapToAxes;
                if (_stopFillets != null) _stopFillets.Checked = S.StopAtFillets;
                if (_result != null) _result.Caption = ResultText(S.ActiveRegion);
                if (_createSurface != null) _createSurface.Checked = Create == CreateKind.Surface;
                if (_createExtrude != null) _createExtrude.Checked = Create == CreateKind.Extrude;
                if (_createRevolve != null) _createRevolve.Checked = Create == CreateKind.Revolve;
                if (_nextAfterOk != null) _nextAfterOk.Checked = NextAfterOk;
                if (_showDev != null) _showDev.Checked = S.ShowDeviation;
                if (_showCurv != null) _showCurv.Checked = S.ShowCurvature;
                if (_showPrev != null) _showPrev.Checked = S.ShowPreview;
                if (_showMesh != null) _showMesh.Checked = S.ShowMesh;
            }
            catch { /* page closing */ }
            finally { _updating = false; }
        }

        /// <summary>One list line: "✔ Hood top · Cylinder R25 · 0.042 mm".</summary>
        private static string RegionLine(Region r, double tol)
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

        private static string ResultText(Region r)
        {
            var f = r?.Fit;
            if (f?.Surface == null) return r == null || r.TriangleCount == 0 ? "Paint the feature, then Fit + grow." : "Not fitted yet — click Fit + grow.";
            var inv = CultureInfo.InvariantCulture;
            string s = f.Surface.Describe(1000, "mm") + "\n" +
                       $"max {(f.Stats.MaxAbs * 1000).ToString("0.###", inv)} mm, RMS {(f.Stats.Rms * 1000).ToString("0.####", inv)} mm";
            if (f.Constraints != null && f.Constraints.Count > 0) s += "\nClean: " + string.Join("; ", f.Constraints);
            return s;
        }

        // ------------------------------------------------------------------ handler

        public void AfterActivation() { }

        public void OnClose(int reason)
        {
            _closeReason = reason;
        }

        public void AfterClose()
        {
            _open = false;
            _page = null;
            bool ok = _closeReason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay ||
                      _closeReason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Apply;
            if (ok)
            {
                // Create after the page has gone (SOLIDWORKS is idle again), then reopen for the next feature.
                _reopen = NextAfterOk;
                bool next = _reopen;
                _addin.Post(() => _addin.CreateFromPage(Create, next, created => { if (created && next) _addin.Post(Show); }));
            }
            else _addin.SetToolFromPage(PaintTool.None);
        }

        public void OnButtonPress(int id)
        {
            if (id >= (int)Id.TypeAuto && id <= (int)Id.TypeFree) { TypeClicked(id); return; }
            switch ((Id)id)
            {
                case Id.NewRegion: _addin.Do(Cmd.NewRegion); break;
                case Id.DeleteRegion: _addin.DeleteActiveRegion(); break;
                case Id.ClearRegion: _addin.Do(Cmd.ClearRegion); break;
                case Id.FillIslands: _addin.Do(Cmd.FillIslands); break;
                case Id.CleanUp: _addin.Do(Cmd.CleanUp); break;
                case Id.GrowRing: _addin.Do(Cmd.GrowRing); break;
                case Id.ShrinkRing: _addin.Do(Cmd.ShrinkRing); break;
                case Id.FitGrow: _addin.Do(Cmd.FitAndGrow); break;
            }
        }

        public void OnOptionCheck(int id)
        {
            if (_updating) return;
            switch ((Id)id)
            {
                case Id.OptSmart: _addin.SetToolFromPage(PaintTool.SmartFill); break;
                case Id.OptBrush: _addin.SetToolFromPage(PaintTool.Brush); break;
                case Id.OptFillet: _addin.SetToolFromPage(PaintTool.Fillet); break;
                case Id.OptShell: _addin.SetToolFromPage(PaintTool.FillConnected); break;
                case Id.OptPick: _addin.SetToolFromPage(PaintTool.PickRegion); break;
                case Id.OptOff: _addin.SetToolFromPage(PaintTool.None); break;
                case Id.CreateSurface: Create = CreateKind.Surface; break;
                case Id.CreateExtrude: Create = CreateKind.Extrude; break;
                case Id.CreateRevolve: Create = CreateKind.Revolve; break;
            }
        }

        public void OnCheckboxCheck(int id, bool check)
        {
            if (_updating) return;
            if (id >= (int)Id.TypeAuto && id <= (int)Id.TypeFree) { TypeClicked(id); return; }
            switch ((Id)id)
            {
                case Id.Remove: S.RemoveMode = check; _addin.Redraw(); break;
                case Id.Clean:
                    S.Idealize.RoundDimensions = S.Idealize.SnapToAxes = S.Idealize.SnapToFeatures = S.Idealize.Tangency = check;
                    break;
                case Id.StopFillets: S.StopAtFillets = check; break;
                case Id.NextAfterOk: NextAfterOk = check; break;
                case Id.ShowDeviation: S.ShowDeviation = check; break;
                case Id.ShowCurvature: S.ShowCurvature = check; break;
                case Id.ShowPreview: S.ShowPreview = check; break;
                case Id.ShowMesh: S.ShowMesh = check; break;
            }
            S.RaiseChanged();
        }

        public void OnNumberboxChanged(int id, double value)
        {
            if (_updating) return;
            switch ((Id)id)
            {
                case Id.Diameter: S.BrushRadius = value / 2; _addin.Redraw(); break;
                case Id.Angle: S.SmartAngleDeg = value * 180 / Math.PI; break;
                case Id.Tolerance: _addin.SetTolerance(value); break;
            }
        }

        public void OnComboboxSelectionChanged(int id, int item) { }

        public void OnUndo() => _addin.Do(Cmd.Undo);
        public void OnRedo() => _addin.Do(Cmd.Redo);

        // Bitmap buttons report through OnButtonPress in some versions and OnCheckboxCheck in others: handle both.
        private void TypeClicked(int id)
        {
            foreach (var (bid, req, _) in TypeButtons)
                if ((int)bid == id && S.ActiveRegion != null) { S.ActiveRegion.Request = req; S.RaiseChanged(); }
        }

        // ---- remaining interface members
        public void OnGroupExpand(int id, bool expanded) { }
        public void OnGroupCheck(int id, bool check) { }
        public void OnTextboxChanged(int id, string text)
        {
            if (_updating || (Id)id != Id.RegionName || S.ActiveRegion == null || string.IsNullOrWhiteSpace(text)) return;
            S.ActiveRegion.Name = text.Trim();
            S.RaiseChanged();
        }
        public void OnComboboxEditChanged(int id, string text) { }
        public void OnListboxSelectionChanged(int id, int item)
        {
            if (_updating || (Id)id != Id.Region || S.Regions == null) return;
            var regions = S.Regions.Regions.ToList();
            if (item >= 0 && item < regions.Count && regions[item] != S.ActiveRegion) { S.ActiveRegion = regions[item]; S.RaiseChanged(); }
        }
        public void OnSelectionboxFocusChanged(int id) { }
        public void OnSelectionboxListChanged(int id, int count) { }
        public void OnSelectionboxCalloutCreated(int id) { }
        public void OnSelectionboxCalloutDestroyed(int id) { }
        public bool OnSubmitSelection(int id, object selection, int selType, ref string itemText) => true;
        public int OnActiveXControlCreated(int id, bool status) => 0;
        public int OnWindowFromHandleControlCreated(int id, bool status) => 0;
        public void OnSliderPositionChanged(int id, double value) { }
        public void OnSliderTrackingCompleted(int id, double value) { }
        public void OnNumberBoxTrackingCompleted(int id, double value) { }
        public bool OnHelp() => false;
        public bool OnPreviousPage() => false;
        public bool OnNextPage() => false;
        public bool OnPreview() => false;
        public void OnWhatsNew() { }
        public bool OnTabClicked(int id) => true;
        public void OnListboxRMBUp(int id, int x, int y) { }
        public void OnPopupMenuItem(int id) { }
        public void OnPopupMenuItemUpdate(int id, ref int retval) { }
        public void OnGainedFocus(int id) { }
        public void OnLostFocus(int id) { }
        public bool OnKeystroke(int wparam, int message, int lparam, int id) => false;
    }
}
