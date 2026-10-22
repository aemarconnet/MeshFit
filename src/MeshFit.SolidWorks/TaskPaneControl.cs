using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MeshFit.Core.Fitting;
using MeshFit.Core.Selection;
using Region = MeshFit.Core.Selection.Region;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// MeshFit task pane. A fixed header holds what is used constantly (guidance, undo/redo, tools,
    /// add/remove mode); the scrolling body holds the mesh, region list, fitting, surfaces and settings.
    /// </summary>
    internal sealed class TaskPaneControl : UserControl
    {
        private const int W = 280;
        private readonly SwAddin _addin;
        private Session S => _addin.Session;
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };

        // header
        private Label _guide, _activeLabel;
        private Panel _activeSwatch;
        private Button _undo, _redo;
        private readonly Dictionary<PaintTool, CheckBox> _toolButtons = new Dictionary<PaintTool, CheckBox>();
        private CheckBox _addMode, _removeMode;
        private NumericUpDown _brushRadius, _smartAngle;

        // body
        private ComboBox _units;
        private readonly Dictionary<FitRequest, CheckBox> _typeButtons = new Dictionary<FitRequest, CheckBox>();
        private Label _meshInfo, _status, _hoverReadout;
        private CheckBox _showMesh, _showRegions, _activeOnly, _rejectOutliers, _showPreview, _showDeviation, _advanced;
        private ListView _regions;
        private ImageList _swatches;
        private NumericUpDown _segAngle, _segMin, _tolerance, _margin, _ctrlU, _ctrlV, _smoothExp, _islandLimit;
        private TextBox _result;
        private DeviationLegend _legend;
        private Control _advancedPanel;
        private bool _busy, _updating;

        private static readonly (FitRequest req, string tip)[] TypeTips =
        {
            (FitRequest.Auto, "Auto: try every type and keep the simplest one within tolerance"),
            (FitRequest.Plane, "Plane: flat face"),
            (FitRequest.Sphere, "Sphere: constant radius in every direction"),
            (FitRequest.Cylinder, "Cylinder: straight axis, constant radius (roll-overs, hems, tubes)"),
            (FitRequest.Cone, "Cone: straight axis, radius changing linearly (draft, flared features)"),
            (FitRequest.Torus, "Torus: radius swept around a circle (curved tubes, wheel-arch lips)"),
            (FitRequest.Freeform, "Freeform: B-spline surface for doubly curved skins"),
        };

        private static readonly (string name, double scale)[] Units =
            { ("mm", 0.001), ("cm", 0.01), ("m", 1.0), ("in", 0.0254) };

        public TaskPaneControl(SwAddin addin)
        {
            _addin = addin;
            Font = new Font("Segoe UI", 8.25f);
            BuildUi();
            S.Changed += () => { if (!IsDisposed) RefreshAll(); };
            S.HoverChanged += UpdateHoverReadout;
            S.Status += SetStatus;
            RefreshAll();
        }

        // ================================================================== layout

        private void BuildUi()
        {
            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var header = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4, 4, 4, 2),
                BackColor = SystemColors.ControlLight
            };
            Controls.Add(body);
            Controls.Add(header); // added last = docked first (top)

            // ---- header ------------------------------------------------------------
            _guide = new Label
            {
                AutoSize = true, MaximumSize = new Size(W - 8, 0), Padding = new Padding(4),
                BackColor = Color.FromArgb(255, 248, 205), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 4)
            };
            header.Controls.Add(_guide);

            _undo = Btn("↶ Undo", () => Execute(Cmd.Undo), "Undo the last paint step (also region delete/merge)");
            _redo = Btn("↷ Redo", () => Execute(Cmd.Redo), "Redo");
            _activeSwatch = new Panel { Width = 14, Height = 14, Margin = new Padding(8, 7, 2, 0), BorderStyle = BorderStyle.FixedSingle };
            _activeLabel = new Label { AutoSize = true, Padding = new Padding(0, 7, 0, 0), Font = new Font(Font, FontStyle.Bold) };
            header.Controls.Add(Row(_undo, _redo, _activeSwatch, _activeLabel));

            var tools = new[]
            {
                (PaintTool.None, "Off", "No paint tool: SOLIDWORKS works normally."),
                (PaintTool.SmartFill, "Smart fill", "Click or drag: fills smooth areas up to edges sharper than the angle."),
                (PaintTool.Brush, "Brush", "Drag to paint inside the brush circle."),
                (PaintTool.FillConnected, "Fill shell", "Click to fill a whole connected piece of mesh."),
                (PaintTool.PickRegion, "Pick", "Click painted triangles to make their region active."),
            };
            var toolRow = Row();
            foreach (var (tool, text, tip) in tools)
            {
                var b = Toggle(text, tip);
                b.Click += (s, e) => Guard(() => SetTool(tool));
                _toolButtons[tool] = b;
                toolRow.Controls.Add(b);
            }
            header.Controls.Add(toolRow);

            _addMode = Toggle("+ Add", "Tools add triangles to the active region.");
            _removeMode = Toggle("− Remove", "Tools unselect triangles (hold Shift for a temporary switch).");
            _addMode.Click += (s, e) => Execute(Cmd.ModeAdd);
            _removeMode.Click += (s, e) => Execute(Cmd.ModeRemove);
            _brushRadius = Num(0.01m, 100000, 10, 2, "Brush radius in mm");
            _brushRadius.ValueChanged += (s, e) => { if (!_updating) { S.BrushRadius = (double)_brushRadius.Value / 1000; _addin.Redraw(); } };
            _smartAngle = Num(0.5m, 90, (decimal)S.SmartAngleDeg, 1, "Smart fill stops at edges sharper than this angle (degrees). Lower = stricter.");
            _smartAngle.ValueChanged += (s, e) => S.SmartAngleDeg = (double)_smartAngle.Value;
            header.Controls.Add(Row(_addMode, _removeMode, Lbl("r mm"), _brushRadius, Lbl("∠°"), _smartAngle));

            // ---- body --------------------------------------------------------------
            var root = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(0, 0), Padding = new Padding(4)
            };
            body.Controls.Add(root);

            // 1. Mesh
            var g1 = Group("Mesh");
            _units = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 50 };
            foreach (var u in Units) _units.Items.Add(u.name);
            _units.SelectedIndex = 0;
            _tips.SetToolTip(_units, "Units the mesh file was saved in");
            g1.Add(Row(Btn("Load mesh file…", LoadMesh, "Load an STL/OBJ/PLY scan (drawn as an overlay)"), Lbl("units"), _units));
            g1.Add(Btn("Use bodies in part", () => Run(() => { _addin.UseBodies(); return "Mesh taken from SOLIDWORKS bodies."; }),
                       "Use the selected mesh body (or all bodies of the part) as the mesh"));
            _meshInfo = new Label { AutoSize = true, MaximumSize = new Size(W - 20, 0) };
            g1.Add(_meshInfo);
            _showMesh = Check("Mesh", true, v => { S.ShowMesh = v; S.RaiseChanged(); }, "Show the mesh overlay");
            _showRegions = Check("Paint", true, v => { S.ShowRegions = v; S.RaiseChanged(); }, "Show painted regions");
            g1.Add(Row(Lbl("Show:"), _showMesh, _showRegions, Btn("Zoom to mesh", () => _addin.ZoomToMesh(), "Zoom the view to fit the whole mesh")));
            root.Controls.Add(g1.Box);

            // 2. Regions
            var g2 = Group("Regions  (right-click a region for more)");
            _swatches = new ImageList { ImageSize = new Size(12, 12) };
            _regions = new ListView
            {
                View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true,
                LabelEdit = true, Width = W - 16, Height = 140, SmallImageList = _swatches
            };
            _regions.Columns.Add("Region", 88); _regions.Columns.Add("Type", 58);
            _regions.Columns.Add("Max dev mm", 66); _regions.Columns.Add("Status", 54);
            _regions.SelectedIndexChanged += (s, e) =>
            {
                if (_updating || _regions.SelectedItems.Count == 0) return;
                S.ActiveRegion = (Region)_regions.SelectedItems[0].Tag;
                S.RaiseChanged();
            };
            _regions.AfterLabelEdit += (s, e) =>
            {
                if (e.Label == null) return;
                ((Region)_regions.Items[e.Item].Tag).Name = e.Label;
                BeginInvoke((Action)RefreshAll);
            };
            _regions.DoubleClick += (s, e) => _regions.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.BeginEdit();
            _regions.ContextMenuStrip = BuildRegionMenu();
            g2.Add(_regions);
            g2.Add(Row(Btn("New region", () => Execute(Cmd.NewRegion), "Start the next surface"),
                       Btn("Fill islands", () => Execute(Cmd.FillIslands), "Fill unpainted islands completely enclosed by the active region"),
                       Btn("Clear", () => Execute(Cmd.ClearRegion), "Unselect every triangle of the active region")));
            g2.Add(Row(Btn("Grow ring", () => Execute(Cmd.GrowRing), "Add one ring of triangles around the region"),
                       Btn("Shrink ring", () => Execute(Cmd.ShrinkRing), "Remove the outer ring of triangles"),
                       Btn("Delete", DeleteRegions, "Delete the selected regions (undoable)")));
            root.Controls.Add(g2.Box);

            // 3. Fit
            var g3 = Group("Fit");
            var typeRow = Row();
            foreach (var (req, tip) in TypeTips)
            {
                var b = Toggle(req == FitRequest.Freeform ? "Free" : req.ToString(), tip);
                b.Image = ShapeIcons.Draw(req);
                b.TextImageRelation = TextImageRelation.ImageAboveText;
                b.Font = new Font(Font.FontFamily, 7f);
                b.AutoSize = false;
                b.Size = new Size(37, 44);
                b.Click += (s2, e2) => Guard(() =>
                {
                    if (S.ActiveRegion == null) return;
                    S.ActiveRegion.Request = req;
                    RefreshAll();
                    SetStatus($"'{S.ActiveRegion.Name}' will be fitted as {(req == FitRequest.Auto ? "the simplest type within tolerance" : req.ToString())}. Click Fit + grow.");
                });
                _typeButtons[req] = b;
                typeRow.Controls.Add(b);
            }
            g3.Add(Lbl("Surface type for the active region:"));
            g3.Add(typeRow);
            _tolerance = Num(0.001m, 100, 0.2m, 3, "Allowed deviation in mm. Drives Auto type choice, Grow and the deviation colours.");
            _tolerance.ValueChanged += (s, e) => { ApplyFitSettings(); _legend.Invalidate(); if (S.ShowDeviation) S.RaiseChanged(); };
            g3.Add(Row(Lbl("Tolerance mm"), _tolerance));
            var fitGrow = Btn("Fit + grow", () => Execute(Cmd.FitAndGrow),
                "Fit the active region, extend it to every connected triangle within tolerance of the surface, refit; repeat until it stops growing.");
            fitGrow.Font = new Font(Font, FontStyle.Bold);
            g3.Add(Row(fitGrow, Btn("Fit only", () => Execute(Cmd.Fit), "Fit without growing"),
                       Btn("Fit all", () => FitRegions(All()), "Fit every painted region")));
            _showPreview = Check("Preview surface", true, v => { S.ShowPreview = v; S.RaiseChanged(); }, "Draw iso-lines of the fitted surfaces");
            _showDeviation = Check("Deviation map", false, v => { S.ShowDeviation = v; _legend.Visible = v; _hoverReadout.Visible = v; S.RaiseChanged(); },
                "Colour fitted regions by their distance to the surface");
            g3.Add(Row(_showPreview, _showDeviation));
            _legend = new DeviationLegend(() => (double)_tolerance.Value) { Width = W - 20, Height = 34, Visible = false };
            g3.Add(_legend);
            _hoverReadout = new Label { AutoSize = true, Visible = false, Text = "Move the cursor over a fitted region to read its deviation." };
            g3.Add(_hoverReadout);
            _result = new TextBox { Multiline = true, ReadOnly = true, Width = W - 16, Height = 96, ScrollBars = ScrollBars.Vertical };
            g3.Add(_result);
            root.Controls.Add(g3.Box);

            // 4. Surfaces
            var g4 = Group("SOLIDWORKS surfaces");
            g4.Add(Row(Btn("Create surface", () => Execute(Cmd.CreateSurface), "Create (or replace) the surface for the active region"),
                       Btn("Create all", () => CreateSurfaces(All()), "Create surfaces for every painted region")));
            g4.Add(Row(Btn("Hide surfaces", () => Execute(Cmd.HideSurfaces), "Hide all surfaces created by MeshFit"),
                       Btn("Show surfaces", () => Execute(Cmd.ShowSurfaces), "Show all surfaces created by MeshFit"),
                       Btn("Export IGES…", ExportIges, "Write all fitted surfaces to an IGES file (mm)")));
            root.Controls.Add(g4.Box);

            // 5. Project + advanced
            var g5 = Group("Project");
            g5.Add(Row(Btn("Save project…", SaveProject, "Save the painted regions (and the mesh reference) to a .meshfit file"), Btn("Open project…", OpenProject, "Reopen a saved .meshfit project")));
            _advanced = Check("Show advanced settings", false, v => _advancedPanel.Visible = v, "Show less frequently used options");
            g5.Add(_advanced);
            var adv = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Visible = false };
            _advancedPanel = adv;
            _activeOnly = Check("Tools only affect the active region", true, v => S.ActiveRegionOnly = v,
                "Add never steals triangles from other regions; Remove only unselects the active region");
            adv.Controls.Add(_activeOnly);
            _islandLimit = Num(0, 1000000, S.AutoFillIslandLimit, 0, "After each Smart fill, enclosed unpainted islands up to this many triangles are filled automatically (0 = off)");
            _islandLimit.ValueChanged += (s, e) => S.AutoFillIslandLimit = (int)_islandLimit.Value;
            adv.Controls.Add(Row(Lbl("Auto-fill islands up to"), _islandLimit, Lbl("tris")));
            _margin = Num(0, 200, 10, 0, "How far created surfaces extend past the painted region (% of its size)");
            _rejectOutliers = Check("Reject outliers", true, v => S.FitOptions.RejectOutliers = v, "Ignore stray points when fitting analytic surfaces");
            adv.Controls.Add(Row(Lbl("Extend %"), _margin, _rejectOutliers));
            _ctrlU = Num(0, 200, 0, 0, "Freeform control points along u (0 = automatic)");
            _ctrlV = Num(0, 200, 0, 0, "Freeform control points along v (0 = automatic)");
            _smoothExp = Num(-14, -2, -10, 0, "Freeform smoothing 10^x. Higher (e.g. -8) = fairer, lower = closer to the scan");
            adv.Controls.Add(Row(Lbl("Freeform CP u"), _ctrlU, Lbl("v"), _ctrlV));
            adv.Controls.Add(Row(Lbl("Freeform smoothing 1e"), _smoothExp));
            _segAngle = Num(1, 90, 20, 0, "Edge angle for automatic segmentation");
            _segMin = Num(1, 1000000, 200, 0, "Smallest patch kept by automatic segmentation");
            adv.Controls.Add(Row(Btn("Auto segment", AutoSegment, "Split all unpainted triangles into smooth patches"), Lbl("∠°"), _segAngle, Lbl("min"), _segMin));
            g5.Add(adv);
            root.Controls.Add(g5.Box);

            _status = new Label { AutoSize = true, MaximumSize = new Size(W, 0), Text = "Ready." };
            root.Controls.Add(_status);
        }

        private ContextMenuStrip BuildRegionMenu()
        {
            var m = new ContextMenuStrip();
            void Item(string text, Action a) => m.Items.Add(text, null, (s, e) => Guard(a));
            Item("Rename", () => _regions.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.BeginEdit());
            Item("Change colour…", ChangeColour);
            m.Items.Add(new ToolStripSeparator());
            Item("Fit + grow", () => Execute(Cmd.FitAndGrow));
            Item("Fit only", () => Execute(Cmd.Fit));
            Item("Grow to surface", GrowToSurface);
            m.Items.Add(new ToolStripSeparator());
            Item("Fill islands", () => Execute(Cmd.FillIslands));
            Item("Grow ring", () => Execute(Cmd.GrowRing));
            Item("Shrink ring", () => Execute(Cmd.ShrinkRing));
            Item("Clear (unselect all)", () => Execute(Cmd.ClearRegion));
            Item("Merge selected", MergeRegions);
            Item("Delete", DeleteRegions);
            m.Items.Add(new ToolStripSeparator());
            Item("Create surface", () => CreateSurfaces(Selected()));
            Item("Hide surface", () => SetStatus($"Hid {_addin.SetSurfacesHidden(Selected(), true)} surface(s)."));
            Item("Show surface", () => SetStatus($"Showed {_addin.SetSurfacesHidden(Selected(), false)} surface(s)."));
            return m;
        }

        private sealed class GroupBuilder
        {
            public GroupBox Box;
            public FlowLayoutPanel Panel;
            public void Add(Control c) => Panel.Controls.Add(c);
        }

        private static GroupBuilder Group(string title)
        {
            var box = new GroupBox { Text = title, Width = W, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4) };
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                Dock = DockStyle.Fill, MinimumSize = new Size(W - 12, 0)
            };
            box.Controls.Add(panel);
            return new GroupBuilder { Box = box, Panel = panel };
        }

        private static FlowLayoutPanel Row(params Control[] cs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(W - 8, 0), Margin = new Padding(0) };
            p.Controls.AddRange(cs);
            return p;
        }

        private Button Btn(string text, Action a, string tip)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(1) };
            b.Click += (s, e) => Guard(a);
            _tips.SetToolTip(b, tip);
            return b;
        }

        private CheckBox Toggle(string text, string tip)
        {
            var c = new CheckBox
            {
                Text = text, Appearance = Appearance.Button, AutoSize = true, AutoCheck = false,
                FlatStyle = FlatStyle.Flat, Margin = new Padding(1), TextAlign = ContentAlignment.MiddleCenter
            };
            c.FlatAppearance.CheckedBackColor = Color.FromArgb(150, 200, 255);
            _tips.SetToolTip(c, tip);
            return c;
        }

        private static Label Lbl(string t) => new Label { Text = t, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

        private NumericUpDown Num(decimal min, decimal max, decimal val, int decimals, string tip)
        {
            var n = new NumericUpDown
            {
                Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, val)), DecimalPlaces = decimals, Width = 58,
                Increment = decimals == 0 ? 1 : (decimal)Math.Pow(10, -Math.Min(decimals, 2))
            };
            _tips.SetToolTip(n, tip);
            return n;
        }

        private CheckBox Check(string text, bool value, Action<bool> changed, string tip)
        {
            var c = new CheckBox { Text = text, Checked = value, AutoSize = true };
            c.CheckedChanged += (s, e) => changed(c.Checked);
            _tips.SetToolTip(c, tip);
            return c;
        }

        // ================================================================== commands

        /// <summary>Entry point for buttons, the region menu and the SOLIDWORKS right-click menu.</summary>
        internal void Execute(Cmd c)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => Execute(c))); return; }
            Guard(() =>
            {
                switch (c)
                {
                    case Cmd.ToolBrush: SetTool(PaintTool.Brush); break;
                    case Cmd.ToolSmartFill: SetTool(PaintTool.SmartFill); break;
                    case Cmd.ToolFillShell: SetTool(PaintTool.FillConnected); break;
                    case Cmd.ToolPick: SetTool(PaintTool.PickRegion); break;
                    case Cmd.ToolOff: SetTool(PaintTool.None); break;
                    case Cmd.ModeAdd: S.RemoveMode = false; RefreshAll(); _addin.Redraw(); break;
                    case Cmd.ModeRemove: S.RemoveMode = true; RefreshAll(); _addin.Redraw(); break;
                    case Cmd.ToggleMode: S.RemoveMode = !S.RemoveMode; RefreshAll(); _addin.Redraw(); break;
                    case Cmd.Undo:
                        if (S.Regions?.CanUndo == true) { string l = S.Regions.UndoLabel; S.Regions.Undo(); SetStatus("Undid: " + l); }
                        break;
                    case Cmd.Redo:
                        if (S.Regions?.CanRedo == true) { string l = S.Regions.RedoLabel; S.Regions.Redo(); SetStatus("Redid: " + l); }
                        break;
                    case Cmd.NewRegion:
                        if (!NeedMesh()) return;
                        var r = S.NewRegion();
                        if (S.Tool == PaintTool.None || S.Tool == PaintTool.PickRegion) SetTool(PaintTool.SmartFill);
                        SetStatus($"Painting '{r.Name}'.");
                        break;
                    case Cmd.FillIslands:
                        if (!NeedRegion()) return;
                        SetStatus($"Filled {S.FillIslands(S.ActiveRegion):N0} enclosed triangles.");
                        break;
                    case Cmd.GrowRing: if (NeedRegion()) S.Ring(S.ActiveRegion, true); break;
                    case Cmd.ShrinkRing: if (NeedRegion()) S.Ring(S.ActiveRegion, false); break;
                    case Cmd.ClearRegion: if (NeedRegion()) S.ClearRegion(S.ActiveRegion); break;
                    case Cmd.Fit: FitRegions(Active()); break;
                    case Cmd.FitAndGrow: FitAndGrow(); break;
                    case Cmd.CreateSurface: CreateSurfaces(Active()); break;
                    case Cmd.HideSurfaces: SetStatus($"Hid {_addin.SetSurfacesHidden(S.Regions?.Regions ?? new List<Region>(), true)} surface(s)."); break;
                    case Cmd.ShowSurfaces: SetStatus($"Showed {_addin.SetSurfacesHidden(S.Regions?.Regions ?? new List<Region>(), false)} surface(s)."); break;
                }
            });
        }

        private void SetTool(PaintTool t)
        {
            if (t != PaintTool.None && !NeedMesh()) t = PaintTool.None;
            S.Tool = t;
            if (t != PaintTool.None) _addin.AttachToActiveView();
            RefreshAll();
            _addin.Redraw();
        }

        // ================================================================== helpers

        private void Guard(Action a)
        {
            if (_busy) { SetStatus("Busy, please wait…"); return; }
            try { a(); }
            catch (Exception ex) { SetStatus("Error: " + ex.Message); }
        }

        /// <summary>Runs work on a worker thread (pure computation only, no SOLIDWORKS calls) or inline.</summary>
        private void Run(Func<string> work, bool background = false)
        {
            if (!background)
            {
                Cursor = Cursors.WaitCursor;
                try { SetStatus(work()); }
                catch (Exception ex) { SetStatus("Error: " + ex.Message); }
                finally { Cursor = Cursors.Default; }
                return;
            }
            _busy = true;
            S.Busy = true;
            UseWaitCursor = true;
            SetStatus("Working…");
            Task.Run(work).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                _busy = false;
                S.Busy = false;
                UseWaitCursor = false;
                SetStatus(t.IsFaulted ? "Error: " + t.Exception.GetBaseException().Message : t.Result);
                ShowResult(S.ActiveRegion);
                S.RaiseChanged();
            })));
        }

        private void SetStatus(string msg)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)(() => SetStatus(msg))); return; }
            _status.Text = msg;
        }

        private bool NeedMesh()
        {
            if (S.HasMesh) return true;
            SetStatus("Load a mesh first.");
            return false;
        }

        private bool NeedRegion()
        {
            if (!NeedMesh()) return false;
            if (S.ActiveRegion?.TriangleCount > 0) return true;
            SetStatus("The active region is empty — paint it first.");
            return false;
        }

        private List<Region> Active() => S.ActiveRegion == null ? new List<Region>() : new List<Region> { S.ActiveRegion };
        private List<Region> All() => S.Regions?.Regions.Where(r => r.TriangleCount > 0).ToList() ?? new List<Region>();
        private List<Region> Selected()
        {
            var sel = _regions.SelectedItems.Cast<ListViewItem>().Select(i => (Region)i.Tag).ToList();
            return sel.Count > 0 ? sel : Active();
        }

        private void ApplyFitSettings()
        {
            var o = S.FitOptions;
            o.Tolerance = (double)_tolerance.Value / 1000;
            o.Freeform.ControlPointsU = (int)_ctrlU.Value;
            o.Freeform.ControlPointsV = (int)_ctrlV.Value;
            o.Freeform.Smoothing = Math.Pow(10, (double)_smoothExp.Value);
            S.ExtentMargin = (double)_margin.Value / 100;
            o.Freeform.Margin = S.ExtentMargin;
        }

        // ================================================================== refresh

        private void RefreshAll()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)RefreshAll); return; }
            _updating = true;
            try
            {
                _guide.Text = S.NextStep();
                var a = S.ActiveRegion;
                _activeSwatch.BackColor = a == null ? SystemColors.Control : Color.FromArgb(255, Color.FromArgb(a.Color));
                _activeLabel.Text = a == null ? "(no region)" : $"{a.Name}  ({a.TriangleCount:N0})";
                _undo.Enabled = S.Regions?.CanUndo == true;
                _redo.Enabled = S.Regions?.CanRedo == true;
                _tips.SetToolTip(_undo, S.Regions?.UndoLabel != null ? "Undo: " + S.Regions.UndoLabel : "Nothing to undo");
                _tips.SetToolTip(_redo, S.Regions?.RedoLabel != null ? "Redo: " + S.Regions.RedoLabel : "Nothing to redo");
                foreach (var kv in _toolButtons) kv.Value.Checked = S.Tool == kv.Key;
                _addMode.Checked = !S.RemoveMode;
                _removeMode.Checked = S.RemoveMode;
                _removeMode.FlatAppearance.CheckedBackColor = Color.FromArgb(255, 170, 170);
                _brushRadius.Value = Clamp((decimal)(S.BrushRadius * 1000), _brushRadius);
                _meshInfo.Text = S.HasMesh ? S.MeshDescription : "No mesh loaded.";
                foreach (var kv in _typeButtons) kv.Value.Checked = a != null && a.Request == kv.Key;

                _regions.BeginUpdate();
                _regions.Items.Clear();
                _swatches.Images.Clear();
                if (S.Regions != null)
                {
                    foreach (var r in S.Regions.Regions)
                    {
                        var bmp = new Bitmap(12, 12);
                        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(255, Color.FromArgb(r.Color)));
                        _swatches.Images.Add(bmp);
                        var it = new ListViewItem(r.Name, _swatches.Images.Count - 1) { Tag = r };
                        it.SubItems.Add(r.Fit?.Surface?.Kind.ToString() ?? (r.Request == FitRequest.Auto ? "" : "(" + r.Request + ")"));
                        it.SubItems.Add(r.Fit != null ? (r.Fit.Stats.MaxAbs * 1000).ToString("0.###", CultureInfo.InvariantCulture) : "");
                        it.SubItems.Add(StatusOf(r));
                        if (r == a) { it.Selected = true; it.Font = new Font(Font, FontStyle.Bold); }
                        _regions.Items.Add(it);
                    }
                }
                _regions.EndUpdate();
            }
            finally { _updating = false; }
        }

        private static string StatusOf(Region r)
        {
            if (r.FeatureName != null) return r.SurfaceHidden ? "hidden" : "surface";
            if (r.Fit != null) return "fitted";
            return r.TriangleCount > 0 ? $"{r.TriangleCount:N0}" : "empty";
        }

        private static decimal Clamp(decimal v, NumericUpDown n) => Math.Max(n.Minimum, Math.Min(n.Maximum, Math.Round(v, n.DecimalPlaces)));

        private void UpdateHoverReadout()
        {
            if (!S.ShowDeviation || IsDisposed) return;
            _hoverReadout.Text = S.HoverDeviation.HasValue
                ? $"At cursor: {S.HoverDeviation.Value * 1000:+0.000;-0.000;0.000} mm from '{S.HoverRegion?.Name}' ({S.HoverRegion?.Fit.Surface.Kind})"
                : "At cursor: not on a fitted region";
        }

        private void ShowResult(Region r)
        {
            var f = r?.Fit;
            if (f?.Surface == null) { _result.Text = r == null ? "" : $"{r.Name}: not fitted yet."; return; }
            var lines = new List<string>
            {
                f.Surface.Describe(1000, "mm"),
                $"Deviation: RMS {f.Stats.Rms * 1000:0.####} mm, max {f.Stats.MaxAbs * 1000:0.####} mm, 95% within {f.Stats.P95 * 1000:0.####} mm",
                $"{f.Stats.WithinTolerance:P1} of {f.Stats.PointCount:N0} points within ±{S.FitOptions.Tolerance * 1000:0.###} mm"
            };
            if (f.Warning != null) lines.Add("WARNING: " + f.Warning);
            if (f.Alternatives.Count > 1)
            {
                lines.Add("Candidates (RMS / max, mm):");
                foreach (var alt in f.Alternatives)
                    lines.Add($"  {alt.Surface.Kind,-9} {alt.Stats.Rms * 1000:0.####} / {alt.Stats.MaxAbs * 1000:0.####}");
            }
            _result.Text = string.Join(Environment.NewLine, lines);
        }

        // ================================================================== region operations

        private void DeleteRegions()
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            S.Regions.BeginStroke(sel.Count == 1 ? "Delete " + sel[0].Name : $"Delete {sel.Count} regions");
            foreach (var r in sel) S.Regions.RemoveRegion(r);
            S.Regions.EndStroke();
            S.ActiveRegion = S.Regions.Regions.FirstOrDefault();
            S.RaiseChanged();
            SetStatus("Deleted. Use Undo to bring it back.");
        }

        private void MergeRegions()
        {
            var sel = Selected();
            if (sel.Count < 2) { SetStatus("Select two or more regions (Ctrl+click) to merge."); return; }
            var target = sel[0];
            S.Regions.BeginStroke("Merge regions");
            foreach (var r in sel.Skip(1))
            {
                foreach (int t in S.Regions.TriangleListOf(r.Id)) S.Regions.Assign(t, target.Id);
                S.Regions.RemoveRegion(r);
            }
            S.Regions.EndStroke();
            target.Fit = null;
            S.ActiveRegion = target;
            S.RaiseChanged();
        }

        private void ChangeColour()
        {
            var r = Selected().FirstOrDefault();
            if (r == null) return;
            using (var dlg = new ColorDialog { Color = Color.FromArgb(255, Color.FromArgb(r.Color)), FullOpen = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                r.Color = dlg.Color.ToArgb() & 0xFFFFFF;
                S.RaiseChanged();
            }
        }

        private void AutoSegment()
        {
            if (!NeedMesh()) return;
            var created = PaintTools.AutoSegment(S.Regions, (double)_segAngle.Value, (int)_segMin.Value);
            SetStatus($"Auto segmentation created {created.Count} regions (Undo removes them).");
        }

        // ================================================================== fitting

        private void FitRegions(List<Region> regions)
        {
            if (!NeedMesh()) return;
            regions = regions.Where(r => r.TriangleCount > 0).ToList();
            if (regions.Count == 0) { SetStatus("Paint a region first."); return; }
            ApplyFitSettings();
            Run(() =>
            {
                var msgs = new List<string>();
                foreach (var r in regions)
                {
                    try { var f = S.FitRegion(r); msgs.Add($"{r.Name}: {f.Surface.Kind}, max {f.Stats.MaxAbs * 1000:0.###} mm"); }
                    catch (Exception ex) { msgs.Add($"{r.Name}: {ex.Message}"); }
                }
                return string.Join("; ", msgs);
            }, background: true);
        }

        private void FitAndGrow()
        {
            if (!NeedRegion()) return;
            ApplyFitSettings();
            var r = S.ActiveRegion;
            Run(() => S.FitAndGrow(r), background: true);
        }

        private void GrowToSurface()
        {
            var r = S.ActiveRegion;
            if (r?.Fit?.Surface == null) { SetStatus("Fit the active region first."); return; }
            ApplyFitSettings();
            SetStatus($"Added {S.GrowToSurface(r):N0} triangles within tolerance. Fit again to refine.");
        }

        // ================================================================== files / output

        private void LoadMesh()
        {
            using (var dlg = new OpenFileDialog { Filter = "Meshes (*.stl;*.obj;*.ply)|*.stl;*.obj;*.ply|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                double scale = Units[_units.SelectedIndex].scale;
                Run(() =>
                {
                    _addin.LoadMeshFile(dlg.FileName, scale);
                    SetTool(PaintTool.SmartFill);
                    return "Mesh loaded. Click or drag on a smooth area to paint the first region.";
                });
            }
        }

        private void CreateSurfaces(List<Region> regions)
        {
            regions = regions.Where(r => r.TriangleCount > 0).ToList();
            if (regions.Count == 0) { SetStatus("No painted region to create a surface from."); return; }
            ApplyFitSettings();
            foreach (var r in regions.Where(r => r.Fit == null)) S.FitRegion(r);

            var existing = regions.Where(r => _addin.FindFeature(r) != null).ToList();
            bool replace = false;
            if (existing.Count > 0)
            {
                var answer = MessageBox.Show(this,
                    $"{string.Join(", ", existing.Select(r => r.Name))} already {(existing.Count == 1 ? "has a surface" : "have surfaces")}.\n\n" +
                    "Yes = replace (delete the old surface feature)\nNo = keep the old one and add a new surface",
                    "MeshFit", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                replace = answer == DialogResult.Yes;
            }
            Run(() => _addin.CreateSurfaces(regions, r => replace));
            _result.Text = _status.Text;
        }

        private void ExportIges()
        {
            var fitted = All().Where(r => r.Fit != null).ToList();
            if (fitted.Count == 0) { SetStatus("Fit some regions first."); return; }
            ApplyFitSettings();
            using (var dlg = new SaveFileDialog { Filter = "IGES (*.igs)|*.igs", FileName = "MeshFit surfaces.igs" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Run(() => { _addin.ExportIges(dlg.FileName, fitted); return $"Exported {fitted.Count} surfaces to {dlg.FileName}"; });
            }
        }

        private void SaveProject()
        {
            if (!NeedMesh()) return;
            using (var dlg = new SaveFileDialog { Filter = "MeshFit project (*.meshfit)|*.meshfit" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Run(() => { _addin.SaveProject(dlg.FileName); return "Project saved."; });
            }
        }

        private void OpenProject()
        {
            using (var dlg = new OpenFileDialog { Filter = "MeshFit project (*.meshfit)|*.meshfit" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Run(() => { _addin.LoadProject(dlg.FileName); return "Project loaded. Use Fit all to recompute the surfaces."; });
            }
        }

        // ================================================================== legend

        /// <summary>Colour bar for the deviation map, labelled in millimetres.</summary>
        private sealed class DeviationLegend : Control
        {
            private readonly Func<double> _tolMm;
            public DeviationLegend(Func<double> tolMm)
            {
                _tolMm = tolMm;
                DoubleBuffered = true;
                Margin = new Padding(0, 2, 0, 2);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                double tol = _tolMm();
                int barW = Width - 50, x0 = 4, h = 12;
                var bar = new Rectangle(x0, 2, barW, h);
                using (var lg = new LinearGradientBrush(bar, Color.Blue, Color.Red, LinearGradientMode.Horizontal))
                {
                    lg.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(0, 0, 255), Color.FromArgb(0, 255, 0), Color.FromArgb(255, 0, 0) },
                        Positions = new[] { 0f, 0.5f, 1f }
                    };
                    g.FillRectangle(lg, bar);
                }
                g.DrawRectangle(Pens.Gray, bar);
                using (var mag = new SolidBrush(Color.Magenta)) g.FillRectangle(mag, x0 + barW + 6, 2, 12, h);
                using (var f = new Font("Segoe UI", 7.5f))
                {
                    string t = tol.ToString("0.###", CultureInfo.InvariantCulture);
                    g.DrawString($"−{t} mm", f, Brushes.Black, x0 - 2, h + 3);
                    var zero = g.MeasureString("0", f);
                    g.DrawString("0", f, Brushes.Black, x0 + barW / 2f - zero.Width / 2, h + 3);
                    var plus = $"+{t} mm";
                    var ps = g.MeasureString(plus, f);
                    g.DrawString(plus, f, Brushes.Black, x0 + barW - ps.Width + 2, h + 3);
                    g.DrawString($">±{t}", f, Brushes.Black, x0 + barW + 2, h + 3);
                }
            }
        }
    }
}
