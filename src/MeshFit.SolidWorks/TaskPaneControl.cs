using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MeshFit.Core.Analysis;
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
        private const int W = 290;
        private readonly SwAddin _addin;
        private Session S => _addin.Session;
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };

        // header
        private Label _guide;
        private ComboBox _activeCombo;
        private TabControl _tabs;
        private Button _cancel;
        private FlowLayoutPanel _progressPanel;
        private Label _progressLabel;
        private ProgressBar _progressBar;
        private readonly System.Diagnostics.Stopwatch _progressClock = new System.Diagnostics.Stopwatch();
        private string _progressStage;
        private double? _progressFraction;
        private PositionDialog _positionDialog;
        private Button _stepPaint, _stepFit, _stepSurface, _stepExtrude, _stepRevolve, _stepNext;
        private CheckBox _autoNext;
        private NumericUpDown _snapDeg;
        private TextBox _accuracyText;
        private System.Threading.CancellationTokenSource _cts;
        private Button _undo, _redo;
        private readonly Dictionary<PaintTool, CheckBox> _toolButtons = new Dictionary<PaintTool, CheckBox>();
        private CheckBox _addMode, _removeMode;
        private NumericUpDown _brushDiameter, _smartAngle, _blendRadius, _featureScale;
        private CheckBox _stopAtFillets, _showCurvature, _suggestOn, _showCad;
        private NumericUpDown _cornerAngle, _cadMaxDist;
        private TextBox _analysisText;
        private Button _filletSketch;
        private Label _suggestText;
        private Button _suggestApply, _suggestDismiss;
        private List<SelectionCandidate> _suggestion;
        private Region _suggestionRegion;

        // body
        private ComboBox _units;
        private readonly Dictionary<FitRequest, CheckBox> _typeButtons = new Dictionary<FitRequest, CheckBox>();
        private Label _meshInfo, _status, _hoverReadout;
        private CheckBox _showMesh, _showRegions, _activeOnly, _rejectOutliers, _showPreview, _showDeviation;
        private ListView _regions;
        private ImageList _swatches;
        private NumericUpDown _segAngle, _segMin, _tolerance, _margin, _ctrlU, _ctrlV, _smoothExp, _islandLimit;
        private TextBox _result;
        private DeviationLegend _legend;
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
            S.Changed += () =>
            {
                if (IsDisposed) return;
                RefreshAll();
                if (InvokeRequired) BeginInvoke((Action)(() => _addin.Suggestions?.Poke())); else _addin.Suggestions?.Poke();
            };
            _addin.BrushResized += () => { if (!IsDisposed) RefreshAll(); };
            _addin.Suggestions.Suggested += ShowSuggestion;
            _addin.Suggestions.RunningChanged += running =>
            {
                if (IsDisposed) return;
                if (running) ShowSuggestion(null, null);
                SetStatus(running ? "Exploring alternative selections in the background…" : _status.Text.StartsWith("Exploring") ? "Ready." : _status.Text);
            };
            S.HoverChanged += UpdateHoverReadout;
            S.Status += SetStatus;
            StartEscWatcher();
            RefreshAll();
        }

        // ================================================================== layout

        private void BuildUi()
        {
            var header = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(3, 3, 3, 1),
                BackColor = SystemColors.ControlLight
            };
            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(6, 3) };
            Controls.Add(_tabs);
            Controls.Add(header); // added last = docked first (top)

            // ---- header: what is needed on every tab -------------------------------------
            _guide = new Label
            {
                AutoSize = true, MaximumSize = new Size(W - 6, 0), Padding = new Padding(3),
                BackColor = Color.FromArgb(255, 248, 205), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 3)
            };
            header.Controls.Add(_guide);

            // Feature loop: paint → fit → create → next. The current step is highlighted.
            _stepPaint = Btn("① Paint", () => Execute(Cmd.StepPaint), "Step 1: paint the next feature (selects Smart fill if no tool is active)");
            _stepFit = Btn("② Fit + grow", () => Execute(Cmd.FitAndGrow), "Step 2: fit the region and grow it over its surface (Esc cancels)");
            _stepNext = Btn("④ Next ▶", () => Execute(Cmd.NextFeature), "Step 4: finish this region (hide its surface) and start the next feature");
            header.Controls.Add(Row(_stepPaint, _stepFit, _stepNext));
            _stepSurface = Btn("Surface", () => Execute(Cmd.CreateSurface), "Create the fitted surface (true plane/cylinder/cone/sphere/torus, or B-spline)");
            _stepExtrude = Btn("Extrude", () => Execute(Cmd.CreateExtrude), "Create an editable sketch (lines + arcs) on a plane and an extruded surface from it");
            _stepRevolve = Btn("Revolve", () => Execute(Cmd.CreateRevolve), "Create an editable sketch (lines + arcs) through the axis and a revolved surface from it");
            header.Controls.Add(Row(Lbl("③ Create:"), _stepSurface, _stepExtrude, _stepRevolve));

            _undo = Btn("↶ Undo", () => Execute(Cmd.Undo), "Undo the last paint step (also region delete/merge)");
            _redo = Btn("↷ Redo", () => Execute(Cmd.Redo), "Redo");
            _activeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, DrawMode = DrawMode.OwnerDrawFixed };
            _activeCombo.DrawItem += (s, e) =>
            {
                e.DrawBackground();
                if (e.Index < 0) return;
                var r = (Region)_activeCombo.Items[e.Index];
                using (var b = new SolidBrush(Color.FromArgb(255, Color.FromArgb(r.Color)))) e.Graphics.FillRectangle(b, e.Bounds.X + 2, e.Bounds.Y + 2, 10, e.Bounds.Height - 4);
                TextRenderer.DrawText(e.Graphics, $"{r.Name} ({r.TriangleCount:N0})", e.Font, new Point(e.Bounds.X + 15, e.Bounds.Y + 1), e.ForeColor);
            };
            _activeCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_updating || _activeCombo.SelectedItem == null) return;
                S.ActiveRegion = (Region)_activeCombo.SelectedItem;
                S.RaiseChanged();
            };
            _tips.SetToolTip(_activeCombo, "Active region: the region the tools paint into and the commands act on");
            _cancel = Btn("✖ Cancel (Esc)", CancelWork, "Abort the running operation (same as pressing Esc)");
            _cancel.ForeColor = Color.DarkRed;
            header.Controls.Add(Row(_undo, _redo, _activeCombo));

            // Progress strip: task, step, % and elapsed time, with Cancel (Esc). Shown only while working.
            _progressPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Visible = false,
                BackColor = Color.FromArgb(225, 235, 250), Padding = new Padding(3), Margin = new Padding(0, 2, 0, 2)
            };
            _progressLabel = new Label { AutoSize = true, MaximumSize = new Size(W - 14, 0), Font = new Font(Font, FontStyle.Bold) };
            _progressBar = new ProgressBar { Width = W - 120, Height = 14, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
            _tips.SetToolTip(_progressBar, "Progress of the running task (moving bar = step of unknown length)");
            _progressPanel.Controls.Add(_progressLabel);
            _progressPanel.Controls.Add(Row(_progressBar, _cancel));
            header.Controls.Add(_progressPanel);

            var tools = new[]
            {
                (PaintTool.None, "Off", "No paint tool: SOLIDWORKS works normally (Esc)."),
                (PaintTool.SmartFill, "Smart fill", "Click or drag: fills smooth areas up to edges sharper than the angle (and fillets when 'Stop at fillets' is on)."),
                (PaintTool.Brush, "Brush", "Drag to paint inside the brush circle. Ctrl+click paints a straight line from the last painted point (view direction), blending the brush size. Ctrl/Shift + wheel resizes."),
                (PaintTool.FillConnected, "Shell", "Click to fill a whole connected piece of mesh."),
                (PaintTool.Fillet, "Fillet", "Click a fillet or blend (constant or variable radius) to select the whole band, detected from curvature."),
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
            _brushDiameter = Num(0.01m, 100000, 20, 2, "Brush diameter in mm. Ctrl or Shift + mouse wheel in the graphics area resizes it.");
            _brushDiameter.ValueChanged += (s, e) => { if (!_updating) { S.BrushRadius = (double)_brushDiameter.Value / 2000; _addin.Redraw(); } };
            _smartAngle = Num(0.5m, 90, (decimal)S.SmartAngleDeg, 1, "Smart fill stops at edges sharper than this angle (degrees). Lower = stricter.");
            _smartAngle.ValueChanged += (s, e) => S.SmartAngleDeg = (double)_smartAngle.Value;
            _brushDiameter.Width = _smartAngle.Width = 52;
            header.Controls.Add(Row(_addMode, _removeMode, Lbl("Ø mm"), _brushDiameter, Lbl("∠°"), _smartAngle));

            _legend = new DeviationLegend(() => (double)_tolerance.Value) { Width = W - 10, Height = 30, Visible = false };
            _tips.SetToolTip(_legend, "Deviation colours: blue = below the surface, green = on it, red = above, magenta = beyond ±tolerance, grey = not modelled");
            header.Controls.Add(_legend);
            _hoverReadout = new Label { AutoSize = true, Visible = false, Text = "Move the cursor over the mesh to read the deviation." };
            header.Controls.Add(_hoverReadout);
            _status = new Label { AutoSize = true, MaximumSize = new Size(W - 6, 0), Text = "Ready.", ForeColor = Color.DimGray };
            header.Controls.Add(_status);

            // ---- tab: Mesh -------------------------------------------------------------------
            var tMesh = Tab("Mesh", "Load, unload and position the scan; project files; settings");
            var g1 = Group("Scan");
            _units = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 50 };
            foreach (var u in Units) _units.Items.Add(u.name);
            _units.SelectedIndex = 0;
            _tips.SetToolTip(_units, "Units the mesh file was saved in");
            g1.Add(Row(Btn("Load mesh file…", LoadMesh, "Load an STL/OBJ/PLY scan (drawn as an overlay)"), Lbl("units"), _units));
            g1.Add(Row(Btn("Use bodies in part", () => Run(() => { Report("Reading the facets of the SOLIDWORKS bodies", null); _addin.UseBodies(); return "Mesh taken from SOLIDWORKS bodies."; }, title: "Reading bodies"),
                           "Use the selected mesh body (or all bodies of the part) as the mesh"),
                       Btn("Unload mesh", UnloadMesh, "Remove the mesh and its painted regions from MeshFit (offers to save the project first). SOLIDWORKS bodies are not touched.")));
            _meshInfo = new Label { AutoSize = true, MaximumSize = new Size(W - 24, 0) };
            g1.Add(_meshInfo);
            g1.Add(Row(Btn("Position mesh…", PositionMesh, "Move the mesh relative to the part origin: numeric, mate-like (planes/axes/points) or automatic"),
                       Btn("Zoom to mesh", () => _addin.ZoomToMesh(), "Zoom the view to fit the whole mesh")));
            _showMesh = Check("Mesh", true, v => { S.ShowMesh = v; S.RaiseChanged(); }, "Show the mesh overlay");
            _showRegions = Check("Paint", true, v => { S.ShowRegions = v; S.RaiseChanged(); }, "Show painted regions");
            g1.Add(Row(Lbl("Show:"), _showMesh, _showRegions));
            tMesh.Add(g1.Box);

            var gp = Group("Project");
            gp.Add(Row(Btn("Save project…", SaveProject, "Save the painted regions, mesh reference and mesh position to a .meshfit file"),
                       Btn("Open project…", OpenProject, "Reopen a saved .meshfit project")));
            tMesh.Add(gp.Box);

            var gs = Group("Settings");
            _activeOnly = Check("Tools only affect the active region", true, v => S.ActiveRegionOnly = v,
                "Add never steals triangles from other regions; Remove only unselects the active region");
            gs.Add(_activeOnly);
            _autoNext = Check("After Create: hide the surface and start the next feature", true, v => { },
                "Completes the paint → fit → create loop automatically: the created surface is hidden and a new region is started with Smart fill");
            gs.Add(_autoNext);
            _snapDeg = Num(0, 10, 1, 1, "Extrusion directions and revolve axes within this angle (degrees) of X/Y/Z are snapped exactly onto the axis (0 = never)");
            gs.Add(Row(Lbl("Snap sketch directions to X/Y/Z within °"), _snapDeg));
            _islandLimit = Num(0, 1000000, S.AutoFillIslandLimit, 0, "After each Smart fill, enclosed unpainted islands up to this many triangles are filled automatically (0 = off)");
            _islandLimit.ValueChanged += (s, e) => S.AutoFillIslandLimit = (int)_islandLimit.Value;
            gs.Add(Row(Lbl("Auto-fill islands up to"), _islandLimit, Lbl("tris")));
            _margin = Num(0, 200, 10, 0, "How far created surfaces extend past the painted region (% of its size)");
            _rejectOutliers = Check("Reject outliers", true, v => S.FitOptions.RejectOutliers = v, "Ignore stray points when fitting analytic surfaces");
            gs.Add(Row(Lbl("Extend %"), _margin, _rejectOutliers));
            _ctrlU = Num(0, 200, 0, 0, "Freeform control points along u (0 = automatic)");
            _ctrlV = Num(0, 200, 0, 0, "Freeform control points along v (0 = automatic)");
            _smoothExp = Num(-14, -2, -10, 0, "Freeform smoothing 10^x. Higher (e.g. -8) = fairer, lower = closer to the scan");
            gs.Add(Row(Lbl("Freeform CP u"), _ctrlU, Lbl("v"), _ctrlV));
            gs.Add(Row(Lbl("Freeform smoothing 1e"), _smoothExp));
            _featureScale = Num(0, 1000, 0, 2, "Neighbourhood (mm) used to estimate curvature. Larger = less sensitive to scan noise, smaller = resolves tighter fillets. 0 = automatic (4× mesh edge length).");
            gs.Add(Row(Lbl("Curvature scale mm"), _featureScale,
                Btn("Recompute", () => { S.FeatureScale = (double)_featureScale.Value / 1000; _addin.ComputeCurvature(); }, "Recompute curvature with this scale")));
            tMesh.Add(gs.Box);

            // ---- tab: Regions ----------------------------------------------------------------
            var tReg = Tab("Regions", "Region list and selection tools");
            _swatches = new ImageList { ImageSize = new Size(12, 12) };
            _regions = new ListView
            {
                View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true,
                LabelEdit = true, Width = W - 10, Height = 230, SmallImageList = _swatches
            };
            _regions.Columns.Add("Region", 92); _regions.Columns.Add("Type", 58);
            _regions.Columns.Add("Max dev mm", 66); _regions.Columns.Add("Status", 54);
            _tips.SetToolTip(_regions, "Click to activate, double-click to rename, Ctrl+click to multi-select, right-click for all region commands");
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
            tReg.Add(_regions);
            tReg.Add(Row(Btn("New region", () => Execute(Cmd.NewRegion), "Start the next surface"),
                         Btn("Fill islands", () => Execute(Cmd.FillIslands), "Fill unpainted islands completely enclosed by the active region"),
                         Btn("Clean up", () => Execute(Cmd.CleanUp), "Remove stray specks, fill enclosed holes and smooth the jagged brush edge")));
            tReg.Add(Row(Btn("Grow ring", () => Execute(Cmd.GrowRing), "Add one ring of triangles around the region"),
                         Btn("Shrink ring", () => Execute(Cmd.ShrinkRing), "Remove the outer ring of triangles"),
                         Btn("Clear", () => Execute(Cmd.ClearRegion), "Unselect every triangle of the active region")));
            tReg.Add(Row(Btn("Optimize selection", () => Execute(Cmd.Optimize),
                             "Try growing, shrinking and trimming the selection (in parallel) and list the alternatives ranked by fit (Esc cancels)"),
                         Btn("Merge", MergeRegions, "Merge the selected regions into the first one"),
                         Btn("Delete", DeleteRegions, "Delete the selected regions (undoable)")));
            _segAngle = Num(1, 90, 20, 0, "Edge angle for automatic segmentation");
            _segMin = Num(1, 1000000, 200, 0, "Smallest patch kept by automatic segmentation");
            tReg.Add(Row(Btn("Auto segment", AutoSegment, "Split all unpainted triangles into smooth patches"), Lbl("∠°"), _segAngle, Lbl("min"), _segMin));

            // ---- tab: Fit ------------------------------------------------------------------
            var tFit = Tab("Fit", "Surface type, tolerance, fitting and suggestions");
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
            tFit.Add(typeRow);
            _tolerance = Num(0.001m, 100, 0.2m, 3, "Allowed deviation in mm. Drives Auto type choice, Grow and the deviation colours.");
            _tolerance.ValueChanged += (s, e) => { ApplyFitSettings(); _legend.Invalidate(); if (S.ShowDeviation || S.ShowCadDeviation) S.RaiseChanged(); };
            var fitGrow = Btn("Fit + grow", () => Execute(Cmd.FitAndGrow),
                "Fit the active region, extend it to every connected triangle within tolerance of the surface, refit; repeat until it stops growing (Esc cancels).");
            fitGrow.Font = new Font(Font, FontStyle.Bold);
            tFit.Add(Row(Lbl("Tol. mm"), _tolerance, fitGrow));
            tFit.Add(Row(Btn("Fit only", () => Execute(Cmd.Fit), "Fit without growing"),
                         Btn("Fit all", () => FitRegions(All()), "Fit every painted region (Esc cancels)"),
                         Btn("Grow to surface", GrowToSurface, "Add connected triangles within tolerance of the current fit")));
            _showPreview = Check("Preview surface", true, v => { S.ShowPreview = v; S.RaiseChanged(); }, "Draw iso-lines of the fitted surfaces");
            _showDeviation = Check("Deviation map", false, v => { S.ShowDeviation = v; UpdateLegendVisibility(); S.RaiseChanged(); },
                "Colour fitted regions by their distance to the surface (legend at the top)");
            tFit.Add(Row(_showPreview, _showDeviation));
            _result = new TextBox { Multiline = true, ReadOnly = true, Width = W - 10, Height = 110, ScrollBars = ScrollBars.Vertical };
            _tips.SetToolTip(_result, "Fit report of the active region (mm)");
            tFit.Add(_result);
            _suggestOn = Check("Suggest better selections in the background", true, v =>
            {
                _addin.Suggestions.Enabled = v;
                if (!v) { _addin.Suggestions.Cancel(); ShowSuggestion(null, null); } else _addin.Suggestions.Poke();
            }, "After you paint, MeshFit tries growing/shrinking/trimming the region and other surface types on spare CPU cores, and offers the best-fitting selection");
            tFit.Add(_suggestOn);
            _suggestText = new Label
            {
                AutoSize = true, MaximumSize = new Size(W - 14, 0), Visible = false, Padding = new Padding(3),
                BackColor = Color.FromArgb(220, 240, 220), BorderStyle = BorderStyle.FixedSingle
            };
            tFit.Add(_suggestText);
            _suggestApply = Btn("Apply suggestion", () => Execute(Cmd.ApplySuggestion), "Replace the region's selection with the suggested one (undoable)");
            _suggestDismiss = Btn("Dismiss", () => ShowSuggestion(null, null), "Ignore this suggestion");
            _suggestApply.Visible = _suggestDismiss.Visible = false;
            tFit.Add(Row(_suggestApply, _suggestDismiss));

            // ---- tab: Fillets ----------------------------------------------------------------
            var tFil = Tab("Fillets", "Adjacent fillet detection, fillet measurement and curves");
            var gf = Group("Adjacent fillets & blends");
            _stopAtFillets = Check("Stop at fillets", true, v => { S.StopAtFillets = v; _addin.Suggestions.Poke(); },
                "Smart fill, Fit + grow and the optimizer stop where a tangent fillet or blend starts");
            _blendRadius = Num(0.5m, 10000, (decimal)(S.BlendRadius * 1000), 1,
                "Blends tighter than this radius (mm) count as adjacent features. Use roughly 1.5× the largest fillet you want detected.");
            _blendRadius.ValueChanged += (s, e) => { S.BlendRadius = (double)_blendRadius.Value / 1000; if (S.ShowCurvature) S.RaiseChanged(); };
            gf.Add(Row(_stopAtFillets, Lbl("up to R mm"), _blendRadius));
            gf.Add(Row(Btn("Grow to fillets", () => Execute(Cmd.GrowToFillets), "Grow the active region over unpainted triangles until it reaches adjacent fillets or sharp edges"),
                       Btn("Trim fillets", () => Execute(Cmd.TrimFillets), "Remove the part of the active region that runs into adjacent fillets (tangent run-out)")));
            _showCurvature = Check("Curvature map", false, v => { S.ShowCurvature = v; S.RaiseChanged(); },
                "Colour the mesh by curvature: grey = flat, orange/red = fillets and feature lines");
            gf.Add(_showCurvature);
            tFil.Add(gf.Box);

            var gc = Group("Measure & export curves");
            _filletSketch = Btn("Fillet curves → 3D sketch", () => Execute(Cmd.FilletToSketch),
                "Create a 3D sketch with the measured fillet's spine (ball centre path) and both tangent lines");
            _filletSketch.Enabled = false;
            gc.Add(Row(Btn("Analyze fillet", () => Execute(Cmd.AnalyzeFillet),
                           "Measure the active region as a fillet band: cross-section circles along its length give a constant or variable radius"),
                       _filletSketch));
            _cornerAngle = Num(5, 180, 50, 0, "Boundary curves are split into separate splines where they turn sharper than this angle (degrees)");
            gc.Add(Row(Btn("Boundary → 3D sketch", () => Execute(Cmd.BoundaryToSketch),
                           "Create a 3D sketch with smooth splines along the active region's boundary (trim lines, fillet tangent lines)"),
                       Lbl("split °"), _cornerAngle));
            _analysisText = new TextBox { Multiline = true, ReadOnly = true, Width = W - 20, Height = 120, ScrollBars = ScrollBars.Vertical };
            _tips.SetToolTip(_analysisText, "Fillet measurement (mm)");
            gc.Add(_analysisText);
            tFil.Add(gc.Box);

            // ---- tab: Output -----------------------------------------------------------------
            var tOut = Tab("Output", "SOLIDWORKS surfaces, accuracy analyzer and export");
            var g4 = Group("SOLIDWORKS surfaces");
            g4.Add(Row(Btn("Create surface", () => Execute(Cmd.CreateSurface), "Create (or replace) the surface for the active region"),
                       Btn("Create all", () => CreateSurfaces(All()), "Create surfaces for every painted region")));
            g4.Add(Row(Btn("Extruded surface (sketch)", () => Execute(Cmd.CreateExtrude), "Sketch the active region's profile with lines and arcs and extrude it"),
                       Btn("Revolved surface (sketch)", () => Execute(Cmd.CreateRevolve), "Sketch the active region's profile with lines and arcs and revolve it")));
            g4.Add(Row(Btn("Hide surfaces", () => Execute(Cmd.HideSurfaces), "Hide all surfaces created by MeshFit"),
                       Btn("Show surfaces", () => Execute(Cmd.ShowSurfaces), "Show all surfaces created by MeshFit"),
                       Btn("Export IGES…", ExportIges, "Write all fitted surfaces to an IGES file (mm)")));
            tOut.Add(g4.Box);

            var ga = Group("Accuracy analyzer (CAD vs scan)");
            _cadMaxDist = Num(0.01m, 1000, 1.0m, 2, "Scan farther than this (mm) from every body counts as 'not modelled' (grey) and is left out of the statistics");
            ga.Add(Row(Btn("Analyze bodies", () => Execute(Cmd.AnalyzeCad),
                           "Compare every visible surface/solid body in the part with the scan; the scan is coloured by deviation (Esc stops the exact refinement)"),
                       Lbl("ignore > mm"), _cadMaxDist));
            _showCad = Check("Show CAD deviation", false, v => { S.ShowCadDeviation = v; UpdateLegendVisibility(); S.RaiseChanged(); },
                "Colour the scan by its distance to the CAD bodies (blue below, red above, magenta out of tolerance, grey not modelled)");
            ga.Add(Row(_showCad,
                       Btn("Zebra", () => Execute(Cmd.ToggleZebra), "Toggle SOLIDWORKS zebra stripes to judge surface continuity"),
                       Btn("Curvature", () => _addin.ToggleSwCurvature(), "Toggle SOLIDWORKS curvature display on the bodies")));
            _accuracyText = new TextBox { Multiline = true, ReadOnly = true, Width = W - 20, Height = 120, ScrollBars = ScrollBars.Vertical };
            _tips.SetToolTip(_accuracyText, "Accuracy analyzer report (mm)");
            ga.Add(_accuracyText);
            tOut.Add(ga.Box);

            try { _tabs.SelectedIndex = Math.Max(0, Math.Min(_tabs.TabCount - 1, LastTab)); } catch { }
            _tabs.SelectedIndexChanged += (s, e) => LastTab = _tabs.SelectedIndex;
        }

        private static int LastTab = 0;

        /// <summary>Tab page with a vertical, scroll-as-last-resort layout.</summary>
        private sealed class TabBuilder
        {
            public FlowLayoutPanel Panel;
            public void Add(Control c) => Panel.Controls.Add(c);
        }

        private TabBuilder Tab(string title, string tip)
        {
            var page = new TabPage(title) { AutoScroll = true, ToolTipText = tip, UseVisualStyleBackColor = true };
            _tabs.ShowToolTips = true;
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(0, 0), Padding = new Padding(2)
            };
            page.Controls.Add(panel);
            _tabs.TabPages.Add(page);
            return new TabBuilder { Panel = panel };
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
            var box = new GroupBox { Text = title, Width = W - 10, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4) };
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                Dock = DockStyle.Fill, MinimumSize = new Size(W - 22, 0)
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
                    case Cmd.CleanUp:
                        if (NeedRegion()) SetStatus($"Clean up changed {S.CleanUp(S.ActiveRegion):N0} triangles.");
                        break;
                    case Cmd.ToolFillet: SetTool(PaintTool.Fillet); break;
                    case Cmd.GrowToFillets:
                        if (NeedRegion()) SetStatus($"Added {S.GrowToFillets(S.ActiveRegion):N0} triangles up to the adjacent features.");
                        break;
                    case Cmd.TrimFillets:
                        if (NeedRegion()) SetStatus($"Removed {S.TrimFillets(S.ActiveRegion):N0} triangles running into fillets.");
                        break;
                    case Cmd.BoundaryToSketch:
                        if (NeedRegion()) SetStatus("Created " + _addin.RegionBoundaryToSketch(S.ActiveRegion, (double)_cornerAngle.Value));
                        break;
                    case Cmd.AnalyzeFillet:
                        if (!NeedRegion()) return;
                        var fa = S.AnalyzeFillet(S.ActiveRegion);
                        _analysisText.Text = $"{S.ActiveRegion.Name}:{Environment.NewLine}{fa.Report(1000, "mm")}";
                        _filletSketch.Enabled = fa.Stations.Count >= 2;
                        SetStatus(fa.IsConstant ? $"Constant fillet R {fa.MeanRadius * 1000:0.##} mm." : $"Variable fillet R {fa.MinRadius * 1000:0.##} → {fa.MaxRadius * 1000:0.##} mm.");
                        break;
                    case Cmd.FilletToSketch:
                        SetStatus("Created " + _addin.FilletToSketch(S.LastFilletRegion, S.LastFillet));
                        break;
                    case Cmd.AnalyzeCad:
                        ApplyFitSettings();
                        Run(() =>
                        {
                            var report = _addin.AnalyzeCad((double)_cadMaxDist.Value / 1000, Report);
                            _accuracyText.Text = report;
                            _showCad.Checked = true;
                            UpdateLegendVisibility();
                            return "Accuracy analysis done — hover over the scan to read deviations.";
                        }, title: "Accuracy analyzer");
                        break;
                    case Cmd.UnloadMesh: UnloadMesh(); break;
                    case Cmd.StepPaint:
                        if (!NeedMesh()) return;
                        S.EnsureRegion();
                        if (S.Tool == PaintTool.None || S.Tool == PaintTool.PickRegion) SetTool(PaintTool.SmartFill);
                        SetStatus($"Paint '{S.ActiveRegion.Name}' in the graphics area.");
                        break;
                    case Cmd.NextFeature: NextFeature(); break;
                    case Cmd.CreateExtrude: CreateSketched(revolve: false); break;
                    case Cmd.CreateRevolve: CreateSketched(revolve: true); break;
                    case Cmd.PositionMesh: PositionMesh(); break;
                    case Cmd.ToggleZebra: SetStatus(_addin.ToggleZebra() ? "Zebra stripes on." : "Zebra stripes off."); break;
                    case Cmd.ToggleCurvatureMap: _showCurvature.Checked = !_showCurvature.Checked; break;
                    case Cmd.BrushBigger: S.BrushRadius *= 1.25; RefreshAll(); _addin.Redraw(); break;
                    case Cmd.BrushSmaller: S.BrushRadius /= 1.25; RefreshAll(); _addin.Redraw(); break;
                    case Cmd.Optimize: Optimize(); break;
                    case Cmd.ApplySuggestion:
                        if (_suggestion != null && _suggestion.Count > 0 && _suggestionRegion != null)
                        {
                            S.ApplyCandidate(_suggestionRegion, _suggestion[0]);
                            SetStatus($"Applied: {_suggestion[0].Description}. Undo reverts it.");
                            ShowSuggestion(null, null);
                            ShowResult(S.ActiveRegion);
                        }
                        break;
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
            if (_busy) { SetStatus("Busy — press Esc to cancel."); return; }
            try { a(); }
            catch (Exception ex) { SetStatus("Error: " + ex.Message); }
        }

        /// <summary>Token of the running background job (cancelled by Esc / the Cancel button).</summary>
        private System.Threading.CancellationToken Token => _cts?.Token ?? System.Threading.CancellationToken.None;

        /// <summary>Runs work on a worker thread (pure computation only, no SOLIDWORKS calls) or inline.</summary>
        private void Run(Func<string> work, bool background = false, Action then = null, string title = null)
        {
            if (!background)
            {
                Cursor = Cursors.WaitCursor;
                if (title != null) BeginProgress(title);
                try { SetStatus(work()); }
                catch (OperationCanceledException) { SetStatus("Cancelled."); }
                catch (Exception ex) { SetStatus("Error: " + ex.Message); }
                finally { Cursor = Cursors.Default; if (title != null) EndProgress(); }
                return;
            }
            _busy = true;
            S.Busy = true;
            _cts = new System.Threading.CancellationTokenSource();
            var cts = _cts;
            BeginProgress(title ?? "Working");
            UseWaitCursor = true;
            SetStatus("Working… (Esc to cancel)");
            Task.Run(work, cts.Token).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                _busy = false;
                S.Busy = false;
                _cts = null;
                EndProgress();
                UseWaitCursor = false;
                bool cancelled = t.IsCanceled || cts.IsCancellationRequested ||
                                 (t.IsFaulted && t.Exception.GetBaseException() is OperationCanceledException);
                SetStatus(cancelled ? "Cancelled — nothing was changed." :
                          t.IsFaulted ? "Error: " + t.Exception.GetBaseException().Message : t.Result);
                ShowResult(S.ActiveRegion);
                S.RaiseChanged();
                if (!t.IsFaulted && !cancelled) then?.Invoke();
            })));
        }

        /// <summary>Esc: cancel the running job; otherwise end a stroke; otherwise leave the paint tool.</summary>
        internal void CancelWork()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                SetStatus("Cancelling…");
            }
            else if (_addin.Suggestions.Running) { _addin.Suggestions.Cancel(); SetStatus("Background suggestion cancelled."); }
            else if (_addin.CancelCurvature()) SetStatus("Curvature analysis cancelled.");
            else if (S.IsStroking) { S.EndStroke(); _addin.Redraw(); }
            else if (S.Tool != PaintTool.None) SetTool(PaintTool.None);
        }

        /// <summary>Shows the progress strip for a task.</summary>
        private void BeginProgress(string title)
        {
            _progressStage = title;
            _progressFraction = null;
            _progressClock.Restart();
            _cancel.Visible = true;
            _progressPanel.Visible = true;
            UpdateProgressDisplay();
            _progressPanel.Refresh();
        }

        private void EndProgress()
        {
            _progressClock.Stop();
            _progressPanel.Visible = false;
            _cancel.Visible = false;
        }

        /// <summary>Thread-safe progress report: current step and completed fraction (null = unknown).</summary>
        internal void Report(string stage, double? fraction)
        {
            _progressStage = stage ?? _progressStage;
            _progressFraction = fraction;
            if (InvokeRequired) { BeginInvoke((Action)UpdateProgressDisplay); return; }
            UpdateProgressDisplay();
            _progressPanel.Refresh(); // paints even while the UI thread is busy with SOLIDWORKS calls
        }

        private void UpdateProgressDisplay()
        {
            if (!_progressPanel.Visible) return;
            double secs = _progressClock.Elapsed.TotalSeconds;
            var f = _progressFraction;
            _progressLabel.Text = f.HasValue ? $"{_progressStage}  —  {f.Value:P0}, {secs:0.0} s" : $"{_progressStage}  —  {secs:0.0} s";
            if (f.HasValue)
            {
                if (_progressBar.Style != ProgressBarStyle.Continuous) _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Value = Math.Max(0, Math.Min(100, (int)(f.Value * 100)));
            }
            else if (_progressBar.Style != ProgressBarStyle.Marquee) _progressBar.Style = ProgressBarStyle.Marquee;
        }

        private bool _escWasDown;

        private void StartEscWatcher()
        {
            var timer = new Timer { Interval = 80 };
            timer.Tick += (s, e) =>
            {
                if (_progressPanel.Visible) UpdateProgressDisplay();
                bool down = EscKey.IsDown();
                if (down && !_escWasDown) CancelWork();
                _escWasDown = down;
            };
            timer.Start();
            Disposed += (s, e) => timer.Dispose();
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
                _activeCombo.BeginUpdate();
                _activeCombo.Items.Clear();
                if (S.Regions != null) foreach (var r in S.Regions.Regions) _activeCombo.Items.Add(r);
                _activeCombo.SelectedItem = a;
                _activeCombo.EndUpdate();
                _undo.Enabled = S.Regions?.CanUndo == true;
                _redo.Enabled = S.Regions?.CanRedo == true;
                _tips.SetToolTip(_undo, S.Regions?.UndoLabel != null ? "Undo: " + S.Regions.UndoLabel : "Nothing to undo");
                _tips.SetToolTip(_redo, S.Regions?.RedoLabel != null ? "Redo: " + S.Regions.RedoLabel : "Nothing to redo");
                foreach (var kv in _toolButtons) kv.Value.Checked = S.Tool == kv.Key;
                _addMode.Checked = !S.RemoveMode;
                _removeMode.Checked = S.RemoveMode;
                _removeMode.FlatAppearance.CheckedBackColor = Color.FromArgb(255, 170, 170);
                _brushDiameter.Value = Clamp((decimal)(S.BrushRadius * 2000), _brushDiameter);
                _meshInfo.Text = S.HasMesh ? S.MeshDescription : "No mesh loaded.";
                _showMesh.Checked = S.ShowMesh;
                foreach (var kv in _typeButtons) kv.Value.Checked = a != null && a.Request == kv.Key;
                int step = !S.HasMesh || a == null || a.TriangleCount == 0 ? 1 : a.Fit == null ? 2 : a.FeatureName == null ? 3 : 4;
                var hi = Color.FromArgb(255, 220, 120);
                _stepPaint.BackColor = step == 1 ? hi : SystemColors.Control;
                _stepFit.BackColor = step == 2 ? hi : SystemColors.Control;
                _stepSurface.BackColor = _stepExtrude.BackColor = _stepRevolve.BackColor = step == 3 ? hi : SystemColors.Control;
                _stepNext.BackColor = step == 4 ? hi : SystemColors.Control;
                foreach (var b in new[] { _stepPaint, _stepFit, _stepSurface, _stepExtrude, _stepRevolve, _stepNext }) b.UseVisualStyleBackColor = b.BackColor == SystemColors.Control;

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

        private void UpdateLegendVisibility()
        {
            bool v = S.ShowDeviation || (S.ShowCadDeviation && S.CadResult != null);
            _legend.Visible = v;
            _hoverReadout.Visible = v;
        }

        private void UpdateHoverReadout()
        {
            if (IsDisposed) return;
            if (S.ShowCadDeviation && S.CadResult != null)
            {
                _hoverReadout.Text = S.HoverCadDeviation.HasValue
                    ? $"At cursor: {S.HoverCadDeviation.Value * 1000:+0.000;-0.000;0.000} mm from the CAD bodies"
                    : "At cursor: not modelled (no body within the limit)";
                return;
            }
            if (!S.ShowDeviation) return;
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

        // ================================================================== feature loop

        /// <summary>Hide the finished surface, start a new region and go back to painting.</summary>
        private void NextFeature()
        {
            if (!NeedMesh()) return;
            var done = S.ActiveRegion;
            if (done != null && done.FeatureName != null && !done.SurfaceHidden)
                _addin.SetSurfacesHidden(new[] { done }, true);
            var r = S.NewRegion();
            SetTool(PaintTool.SmartFill);
            ShowSuggestion(null, null);
            SetStatus(done != null && done.TriangleCount > 0
                ? $"'{done.Name}' finished. Painting '{r.Name}' — click or drag on the next feature."
                : $"Painting '{r.Name}'.");
        }

        private void AfterCreate()
        {
            if (_autoNext.Checked) NextFeature();
        }

        /// <summary>Analyse the region as an extrusion / revolution (worker thread), then build sketch + feature.</summary>
        private void CreateSketched(bool revolve)
        {
            if (!NeedRegion()) return;
            ApplyFitSettings();
            var r = S.ActiveRegion;
            var tris = S.Regions.TriangleListOf(r.Id);
            double tol = S.FitOptions.Tolerance, snap = (double)_snapDeg.Value;
            var hint = r.Fit?.Surface;
            SketchProfile prof = null;
            Run(() =>
            {
                Report(revolve ? "Finding the axis and the profile" : "Finding the extrusion direction and the profile", null);
                prof = revolve
                    ? (SketchProfile)SketchProfiles.Revolution(S.Mesh, tris, tol, hint, 2, snap)
                    : SketchProfiles.Extrusion(S.Mesh, tris, tol, S.ExtentMargin, snap);
                return "Profile: " + prof.Describe(1000, "mm");
            }, background: true, title: revolve ? $"Revolve '{r.Name}'" : $"Extrude '{r.Name}'", then: () =>
            {
                string warn = null;
                if (prof is ExtrusionProfile ep && ep.DirectionResidual > Math.Sin(3 * Math.PI / 180))
                    warn = $"The surface normals vary {Math.Asin(Math.Min(1, ep.DirectionResidual)) * 180 / Math.PI:0.#}° from a single extrusion direction.";
                if (prof is RevolutionProfile rp && rp.AxisResidual > 5 * tol)
                    warn = $"The normals miss a common axis by {rp.AxisResidual * 1000:0.###} mm on average.";
                if (prof.MaxDeviation > 3 * tol)
                    warn = (warn == null ? "" : warn + "\n") + $"The lines-and-arcs profile deviates up to {prof.MaxDeviation * 1000:0.###} mm from the scan.";
                if (warn != null && MessageBox.Show(this, warn + "\n\nThe region may not be a simple " + (revolve ? "revolution" : "extrusion") + ". Create it anyway?",
                        "MeshFit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                Run(() =>
                {
                    string msg = revolve ? _addin.CreateRevolvedSurface(r, (RevolutionProfile)prof, Report)
                                         : _addin.CreateExtrudedSurface(r, (ExtrusionProfile)prof, Report);
                    _result.Text = msg + Environment.NewLine + "Profile: " + prof.Describe(1000, "mm");
                    AfterCreate();
                    return msg;
                }, title: revolve ? "Creating revolved surface" : "Creating extruded surface");
            });
        }

        // ================================================================== mesh

        private void UnloadMesh()
        {
            if (!S.HasMesh) { SetStatus("No mesh is loaded."); return; }
            if (S.Regions.Regions.Any(r => r.TriangleCount > 0))
            {
                var a = MessageBox.Show(this, "Save the painted regions as a project before unloading the mesh?",
                    "MeshFit – unload mesh", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (a == DialogResult.Cancel) return;
                if (a == DialogResult.Yes) { SaveProject(); if (!_lastSaveOk) return; }
            }
            _positionDialog?.Close();
            _addin.UnloadMesh();
            ShowSuggestion(null, null);
            SetStatus("Mesh unloaded. SOLIDWORKS bodies and created surfaces are unchanged.");
        }

        private void PositionMesh()
        {
            if (!NeedMesh()) return;
            if (_positionDialog == null || _positionDialog.IsDisposed)
            {
                _positionDialog = new PositionDialog(_addin);
                _positionDialog.Show(this);
            }
            else _positionDialog.Activate();
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
                int i = 0;
                foreach (var r in regions)
                {
                    Token.ThrowIfCancellationRequested();
                    Report($"Fitting '{r.Name}' ({++i} of {regions.Count})", (double)(i - 1) / regions.Count);
                    try { var f = S.FitRegion(r); msgs.Add($"{r.Name}: {f.Surface.Kind}, max {f.Stats.MaxAbs * 1000:0.###} mm"); }
                    catch (Exception ex) { msgs.Add($"{r.Name}: {ex.Message}"); }
                }
                return string.Join("; ", msgs);
            }, background: true, title: "Fitting");
        }

        private void FitAndGrow()
        {
            if (!NeedRegion()) return;
            ApplyFitSettings();
            var r = S.ActiveRegion;
            Run(() => S.FitAndGrow(r, Token, Report), background: true, title: $"Fit + grow '{r.Name}'");
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
                string file = dlg.FileName;
                MeshFit.Core.Mesh.TriMesh mesh = null;
                Run(() =>
                {
                    Report($"Reading {System.IO.Path.GetFileName(file)}", null);
                    mesh = MeshFit.Core.Mesh.MeshIO.Load(file, scale);
                    return $"Read {mesh.TriangleCount:N0} triangles.";
                }, background: true, title: "Loading mesh", then: () =>
                {
                    _addin.InstallMesh(mesh, file, scale);
                    SetTool(PaintTool.SmartFill);
                    SetStatus("Mesh loaded. Click or drag on a smooth area to paint the first region.");
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
            Run(() => _addin.CreateSurfaces(regions, r => replace, Report), title: "Creating surfaces");
            if (regions.Count == 1 && regions[0] == S.ActiveRegion && regions[0].FeatureName != null) AfterCreate();
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

        private bool _lastSaveOk;

        private void SaveProject()
        {
            _lastSaveOk = false;
            if (!NeedMesh()) return;
            using (var dlg = new SaveFileDialog { Filter = "MeshFit project (*.meshfit)|*.meshfit" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Run(() => { _addin.SaveProject(dlg.FileName); _lastSaveOk = true; return "Project saved."; });
            }
        }

        private void OpenProject()
        {
            using (var dlg = new OpenFileDialog { Filter = "MeshFit project (*.meshfit)|*.meshfit" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string file = dlg.FileName;
                (MeshFit.Core.Mesh.TriMesh, MeshFit.Core.Selection.RegionSet, MeshFit.Core.Project.ProjectFile.Header) project = default;
                Run(() =>
                {
                    Report("Reading project and mesh", null);
                    project = SwAddin.ReadProject(file);
                    return "Project read.";
                }, background: true, title: "Opening project", then: () =>
                {
                    _addin.InstallProject(project);
                    SetStatus("Project loaded. Use Fit all to recompute the surfaces.");
                });
            }
        }

        // ================================================================== selection optimizer

        /// <summary>Shows (or hides with null) the background suggestion.</summary>
        internal void ShowSuggestion(Region r, List<SelectionCandidate> cands)
        {
            _suggestion = cands;
            _suggestionRegion = r;
            bool show = r != null && cands != null && cands.Count > 0;
            _suggestText.Visible = _suggestApply.Visible = _suggestDismiss.Visible = show;
            if (!show) return;
            var best = cands[0];
            var painted = cands.FirstOrDefault(c => c.Description == "As painted");
            string delta = painted == null ? "" : $" ({painted.Triangles.Length:N0} → {best.Triangles.Length:N0} triangles)";
            _suggestText.Text = $"Suggestion for '{r.Name}': {best.Description}{delta}\n" +
                                $"{best.Fit.Surface.Describe(1000, "mm")}\n" +
                                $"RMS {best.Fit.Stats.Rms * 1000:0.###} mm, 95% within {best.Fit.Stats.P95 * 1000:0.###} mm" +
                                (painted != null ? $" (painted: RMS {painted.Fit.Stats.Rms * 1000:0.###}, 95% {painted.Fit.Stats.P95 * 1000:0.###} mm)" : "");
        }

        private void Optimize()
        {
            if (!NeedRegion()) return;
            ApplyFitSettings();
            var r = S.ActiveRegion;
            var owners = (int[])S.Regions.Owners.Clone();
            var opt = S.FitOptions.Clone();
            opt.Request = r.Request;
            var curvature = S.StopAtFillets ? S.Curvature : null;
            double blend = S.BlendRadius;
            List<SelectionCandidate> found = null;
            Run(() =>
            {
                found = SelectionOptimizer.Explore(S.Mesh, owners, r.Id, opt, Token, curvature, blend,
                    new Progress<string>(msg => Report(msg, null)));
                return $"Optimizer tried {found.Count} selections for '{r.Name}'.";
            }, background: true, title: $"Optimizing selection '{r.Name}'", then: () =>
            {
                if (found == null || found.Count == 0) return;
                using (var dlg = new CandidateDialog(found, S.FitOptions.Tolerance))
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Chosen != null)
                    {
                        S.ApplyCandidate(r, dlg.Chosen);
                        ShowResult(r);
                        SetStatus($"Applied: {dlg.Chosen.Description}. Undo reverts it.");
                    }
            });
        }

        /// <summary>Lists optimizer candidates (best first) for the user to pick from.</summary>
        private sealed class CandidateDialog : Form
        {
            public SelectionCandidate Chosen { get; private set; }

            public CandidateDialog(List<SelectionCandidate> cands, double tol)
            {
                Text = "MeshFit – selection alternatives";
                Width = 640; Height = 340;
                StartPosition = FormStartPosition.CenterParent;
                Font = new Font("Segoe UI", 8.25f);
                var tips = new ToolTip();
                var lv = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, MultiSelect = false };
                lv.Columns.Add("Selection", 210); lv.Columns.Add("Type", 70); lv.Columns.Add("Triangles", 70);
                lv.Columns.Add("RMS mm", 60); lv.Columns.Add("95% mm", 60); lv.Columns.Add("Max mm", 60); lv.Columns.Add("OK", 40);
                foreach (var c in cands)
                {
                    var it = new ListViewItem(c.Description) { Tag = c };
                    it.SubItems.Add(c.Fit.Surface.Kind.ToString());
                    it.SubItems.Add(c.Triangles.Length.ToString("N0", CultureInfo.InvariantCulture));
                    it.SubItems.Add((c.Fit.Stats.Rms * 1000).ToString("0.###", CultureInfo.InvariantCulture));
                    it.SubItems.Add((c.Fit.Stats.P95 * 1000).ToString("0.###", CultureInfo.InvariantCulture));
                    it.SubItems.Add((c.Fit.Stats.MaxAbs * 1000).ToString("0.###", CultureInfo.InvariantCulture));
                    it.SubItems.Add(c.Valid ? "✓" : "");
                    if (!c.Valid) it.ForeColor = SystemColors.GrayText;
                    lv.Items.Add(it);
                }
                if (lv.Items.Count > 0) lv.Items[0].Selected = true;
                tips.SetToolTip(lv, $"Ranked: largest selection whose 95% deviation is within ±{tol * 1000:0.###} mm first; ✓ = meets tolerance");
                var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(4) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var apply = new Button { Text = "Apply selected", AutoSize = true };
                tips.SetToolTip(apply, "Replace the region's triangles with the selected alternative (undoable)");
                tips.SetToolTip(cancel, "Keep the current selection");
                apply.Click += (s, e) =>
                {
                    Chosen = lv.SelectedItems.Count > 0 ? (SelectionCandidate)lv.SelectedItems[0].Tag : null;
                    DialogResult = DialogResult.OK;
                };
                lv.DoubleClick += (s, e) => apply.PerformClick();
                bottom.Controls.Add(cancel); bottom.Controls.Add(apply);
                Controls.Add(lv); Controls.Add(bottom);
                AcceptButton = apply; CancelButton = cancel;
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
