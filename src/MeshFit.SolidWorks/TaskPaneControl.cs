using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MeshFit.Core.Fitting;
using MeshFit.Core.Selection;
using Region = MeshFit.Core.Selection.Region;

namespace MeshFit.SolidWorks
{
    /// <summary>MeshFit task pane: mesh source, regions, paint tools, fitting and output.</summary>
    internal sealed class TaskPaneControl : UserControl
    {
        private readonly SwAddin _addin;
        private Session S => _addin.Session;

        private ComboBox _units;
        private Label _meshInfo, _status;
        private CheckBox _showMesh, _protect, _rejectOutliers, _showPreview, _showDeviation;
        private ListView _regions;
        private ImageList _swatches;
        private NumericUpDown _brushRadius, _smartAngle, _segAngle, _segMin, _tolerance, _margin, _ctrlU, _ctrlV, _smoothExp;
        private ComboBox _fitType;
        private TextBox _result;
        private readonly Dictionary<PaintTool, RadioButton> _toolButtons = new Dictionary<PaintTool, RadioButton>();
        private bool _busy, _updating;

        private static readonly (string name, double scale)[] Units =
            { ("mm", 0.001), ("cm", 0.01), ("m", 1.0), ("in", 0.0254) };

        public TaskPaneControl(SwAddin addin)
        {
            _addin = addin;
            AutoScroll = true;
            Font = new Font("Segoe UI", 8.25f);
            BuildUi();
            S.Changed += () => { if (!IsDisposed) RefreshRegions(); };
            S.Status += SetStatus;
        }

        // ------------------------------------------------------------------ layout

        private void BuildUi()
        {
            var root = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(4)
            };
            Controls.Add(root);
            const int W = 270;

            // 1. Mesh
            var g1 = Group("1. Mesh", W);
            _units = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
            foreach (var u in Units) _units.Items.Add(u.name);
            _units.SelectedIndex = 0;
            g1.Add(Row(Btn("Load mesh file…", LoadMesh), new Label { Text = "units", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _units));
            g1.Add(Btn("Use bodies in part (selected or all)", () => Run(() => { _addin.UseBodies(); return "Mesh taken from SOLIDWORKS bodies."; })));
            _meshInfo = new Label { AutoSize = true, MaximumSize = new Size(W - 20, 0), Text = "No mesh loaded." };
            g1.Add(_meshInfo);
            _showMesh = Check("Show mesh overlay", true, v => { S.ShowMesh = v; S.RaiseChanged(); });
            g1.Add(Row(_showMesh, Btn("Zoom to mesh", () => _addin.ZoomToMesh())));
            root.Controls.Add(g1.Box);

            // 2. Regions
            var g2 = Group("2. Regions", W);
            _swatches = new ImageList { ImageSize = new Size(12, 12) };
            _regions = new ListView
            {
                View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true,
                LabelEdit = true, Width = W - 16, Height = 150, SmallImageList = _swatches
            };
            _regions.Columns.Add("Region", 90); _regions.Columns.Add("Tris", 45);
            _regions.Columns.Add("Fit", 60); _regions.Columns.Add("Max dev", 55);
            _regions.SelectedIndexChanged += (s, e) =>
            {
                if (_updating || _regions.SelectedItems.Count == 0) return;
                S.ActiveRegion = (Region)_regions.SelectedItems[0].Tag;
                SyncFitType();
                S.RaiseChanged();
            };
            _regions.AfterLabelEdit += (s, e) =>
            {
                if (e.Label == null) return;
                ((Region)_regions.Items[e.Item].Tag).Name = e.Label;
            };
            g2.Add(_regions);
            g2.Add(Row(Btn("New", () => { if (!NeedMesh()) return; S.ActiveRegion = S.Regions.AddRegion(); SetTool(PaintTool.Brush); }),
                       Btn("Delete", DeleteRegions), Btn("Merge", MergeRegions),
                       Btn("Undo", () => S.Regions?.Undo()), Btn("Redo", () => S.Regions?.Redo())));
            _segAngle = Num(1, 90, 20, 0); _segMin = Num(1, 1000000, 200, 0);
            g2.Add(Row(Btn("Auto segment", AutoSegment), Lbl("edge°"), _segAngle, Lbl("min tris"), _segMin));
            root.Controls.Add(g2.Box);

            // 3. Paint
            var g3 = Group("3. Paint (in the graphics area)", W);
            var tools = new[] { (PaintTool.None, "Off"), (PaintTool.Brush, "Brush"), (PaintTool.Erase, "Erase"),
                                (PaintTool.SmartFill, "Smart fill"), (PaintTool.FillConnected, "Fill shell"), (PaintTool.PickRegion, "Pick region") };
            var toolRow = new FlowLayoutPanel { AutoSize = true, Width = W - 16, MaximumSize = new Size(W - 16, 0) };
            foreach (var (tool, text) in tools)
            {
                var rb = new RadioButton { Text = text, AutoSize = true, Checked = tool == PaintTool.None };
                rb.CheckedChanged += (s, e) => { if (rb.Checked) { S.Tool = tool; _addin.AttachToActiveView(); _addin.Redraw(); } };
                _toolButtons[tool] = rb;
                toolRow.Controls.Add(rb);
            }
            g3.Add(toolRow);
            _brushRadius = Num(0.01m, 100000, 10, 2);
            _brushRadius.ValueChanged += (s, e) => { S.BrushRadius = (double)_brushRadius.Value / 1000; _addin.Redraw(); };
            _smartAngle = Num(0.5m, 90, (decimal)S.SmartAngleDeg, 1);
            _smartAngle.ValueChanged += (s, e) => S.SmartAngleDeg = (double)_smartAngle.Value;
            g3.Add(Row(Lbl("Brush radius mm"), _brushRadius, Lbl("Smart fill °"), _smartAngle));
            _protect = Check("Don't paint over other regions", true, v => S.ProtectOtherRegions = v);
            g3.Add(_protect);
            g3.Add(Row(Btn("Grow ring", () => Ring(true)), Btn("Shrink ring", () => Ring(false))));
            g3.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(W - 20, 0), ForeColor = SystemColors.GrayText,
                Text = "Left-drag to paint. Shift = erase. Smart fill grows across edges smoother than the angle. " +
                       "Rotate/pan with the middle mouse button while a tool is active."
            });
            root.Controls.Add(g3.Box);

            // 4. Fit
            var g4 = Group("4. Fit", W);
            _fitType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
            foreach (var n in Enum.GetNames(typeof(FitRequest))) _fitType.Items.Add(n);
            _fitType.SelectedIndex = 0;
            _fitType.SelectedIndexChanged += (s, e) =>
            {
                if (_updating || S.ActiveRegion == null) return;
                S.ActiveRegion.Request = (FitRequest)_fitType.SelectedIndex;
            };
            _tolerance = Num(0.001m, 100, 0.2m, 3);
            g4.Add(Row(Lbl("Type"), _fitType, Lbl("Tol. mm"), _tolerance));
            _margin = Num(0, 200, 10, 0);
            _rejectOutliers = Check("Reject outliers", true, v => S.FitOptions.RejectOutliers = v);
            g4.Add(Row(Lbl("Extend %"), _margin, _rejectOutliers));
            _ctrlU = Num(0, 200, 0, 0); _ctrlV = Num(0, 200, 0, 0); _smoothExp = Num(-14, -2, -10, 0);
            g4.Add(Row(Lbl("Freeform CP u"), _ctrlU, Lbl("v"), _ctrlV, Lbl("smooth 1e"), _smoothExp));
            _showPreview = Check("Preview", true, v => { S.ShowPreview = v; S.RaiseChanged(); });
            _showDeviation = Check("Deviation map", false, v => { S.ShowDeviation = v; S.RaiseChanged(); });
            g4.Add(Row(_showPreview, _showDeviation));
            g4.Add(Row(Btn("Fit active", () => FitRegions(Active())), Btn("Fit all", () => FitRegions(All())),
                       Btn("Grow to surface", GrowToSurface)));
            _result = new TextBox { Multiline = true, ReadOnly = true, Width = W - 16, Height = 110, ScrollBars = ScrollBars.Vertical };
            g4.Add(_result);
            root.Controls.Add(g4.Box);

            // 5. Output
            var g5 = Group("5. Output", W);
            g5.Add(Row(Btn("Create surface", () => CreateSurfaces(Active())), Btn("Create all", () => CreateSurfaces(All()))));
            g5.Add(Row(Btn("Export IGES…", ExportIges), Btn("Save project…", SaveProject), Btn("Open project…", OpenProject)));
            root.Controls.Add(g5.Box);

            _status = new Label { AutoSize = true, MaximumSize = new Size(W, 0), Text = "Ready." };
            root.Controls.Add(_status);
        }

        private sealed class GroupBuilder
        {
            public GroupBox Box;
            public FlowLayoutPanel Panel;
            public void Add(Control c) => Panel.Controls.Add(c);
        }

        private static GroupBuilder Group(string title, int width)
        {
            var box = new GroupBox { Text = title, Width = width, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4) };
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                Dock = DockStyle.Fill, MinimumSize = new Size(width - 12, 0)
            };
            box.Controls.Add(panel);
            return new GroupBuilder { Box = box, Panel = panel };
        }

        private static FlowLayoutPanel Row(params Control[] cs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(262, 0), Margin = new Padding(0) };
            p.Controls.AddRange(cs);
            return p;
        }

        private Button Btn(string text, Action a)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            b.Click += (s, e) => Guard(a);
            return b;
        }

        private static Label Lbl(string t) => new Label { Text = t, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

        private static NumericUpDown Num(decimal min, decimal max, decimal val, int decimals) =>
            new NumericUpDown { Minimum = min, Maximum = max, Value = val, DecimalPlaces = decimals, Width = 58,
                                Increment = decimals == 0 ? 1 : (decimal)Math.Pow(10, -Math.Min(decimals, 2)) };

        private static CheckBox Check(string text, bool value, Action<bool> changed)
        {
            var c = new CheckBox { Text = text, Checked = value, AutoSize = true };
            c.CheckedChanged += (s, e) => changed(c.Checked);
            return c;
        }

        // ------------------------------------------------------------------ helpers

        private void Guard(Action a)
        {
            if (_busy) return;
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
            Enabled = false;
            SetStatus("Working…");
            Task.Run(work).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                _busy = false;
                Enabled = true;
                SetStatus(t.IsFaulted ? "Error: " + t.Exception.GetBaseException().Message : t.Result);
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

        private void SetTool(PaintTool t) => _toolButtons[t].Checked = true;

        private List<Region> Active() => S.ActiveRegion == null ? new List<Region>() : new List<Region> { S.ActiveRegion };
        private List<Region> All() => S.Regions?.Regions.Where(r => r.TriangleCount > 0).ToList() ?? new List<Region>();

        private List<Region> Selected() => _regions.SelectedItems.Cast<ListViewItem>().Select(i => (Region)i.Tag).ToList();

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

        private void SyncFitType()
        {
            if (S.ActiveRegion == null) return;
            _updating = true;
            _fitType.SelectedIndex = (int)S.ActiveRegion.Request;
            _updating = false;
            ShowResult(S.ActiveRegion);
        }

        // ------------------------------------------------------------------ region list

        private void RefreshRegions()
        {
            if (InvokeRequired) { BeginInvoke((Action)RefreshRegions); return; }
            _updating = true;
            try
            {
                _meshInfo.Text = S.HasMesh ? S.MeshDescription : "No mesh loaded.";
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
                        it.SubItems.Add(r.TriangleCount.ToString(CultureInfo.InvariantCulture));
                        it.SubItems.Add(r.Fit?.Surface?.Kind.ToString() ?? (r.Request == FitRequest.Auto ? "" : "(" + r.Request + ")"));
                        it.SubItems.Add(r.Fit != null ? (r.Fit.Stats.MaxAbs * 1000).ToString("0.###", CultureInfo.InvariantCulture) : "");
                        if (r == S.ActiveRegion) { it.Selected = true; it.Font = new Font(Font, FontStyle.Bold); }
                        _regions.Items.Add(it);
                    }
                }
                _regions.EndUpdate();
                _brushRadius.Value = Clamp((decimal)(S.BrushRadius * 1000), _brushRadius);
            }
            finally { _updating = false; }
        }

        private static decimal Clamp(decimal v, NumericUpDown n) => Math.Max(n.Minimum, Math.Min(n.Maximum, Math.Round(v, n.DecimalPlaces)));

        private void DeleteRegions()
        {
            foreach (var r in Selected()) S.Regions.RemoveRegion(r);
            S.ActiveRegion = S.Regions.Regions.FirstOrDefault();
            S.RaiseChanged();
        }

        private void MergeRegions()
        {
            var sel = Selected();
            if (sel.Count < 2) { SetStatus("Select two or more regions to merge."); return; }
            var target = sel[0];
            S.Regions.BeginStroke();
            foreach (var r in sel.Skip(1))
                foreach (int t in S.Regions.TriangleListOf(r.Id)) S.Regions.Assign(t, target.Id);
            S.Regions.EndStroke();
            foreach (var r in sel.Skip(1)) S.Regions.RemoveRegion(r);
            target.Fit = null;
            S.ActiveRegion = target;
            S.RaiseChanged();
        }

        private void AutoSegment()
        {
            if (!NeedMesh()) return;
            var created = PaintTools.AutoSegment(S.Regions, (double)_segAngle.Value, (int)_segMin.Value);
            SetStatus($"Auto segmentation created {created.Count} regions from unpainted triangles.");
        }

        private void Ring(bool grow)
        {
            if (!NeedMesh() || S.ActiveRegion == null) return;
            S.Regions.BeginStroke();
            PaintTools.Dilate(S.Regions, S.ActiveRegion.Id, grow);
            S.Regions.EndStroke();
        }

        // ------------------------------------------------------------------ fitting

        private void FitRegions(List<Region> regions)
        {
            if (!NeedMesh()) return;
            if (regions.Count == 0) { SetStatus("No region to fit."); return; }
            ApplyFitSettings();
            Run(() =>
            {
                var msgs = new List<string>();
                foreach (var r in regions)
                {
                    try { var f = S.FitRegion(r); msgs.Add($"{r.Name}: {f.Surface.Kind}, max {f.Stats.MaxAbs * 1000:0.###} mm"); }
                    catch (Exception ex) { msgs.Add($"{r.Name}: {ex.Message}"); }
                }
                BeginInvoke((Action)(() => ShowResult(S.ActiveRegion)));
                return string.Join("; ", msgs);
            }, background: true);
        }

        private void ShowResult(Region r)
        {
            var f = r?.Fit;
            if (f?.Surface == null) { _result.Text = r == null ? "" : $"{r.Name}: not fitted yet."; return; }
            var lines = new List<string>
            {
                f.Surface.Describe(1000, "mm"),
                $"RMS {f.Stats.Rms * 1000:0.####} mm, max {f.Stats.MaxAbs * 1000:0.####} mm, " +
                $"95% {f.Stats.P95 * 1000:0.####} mm, {f.Stats.WithinTolerance:P1} within tolerance ({f.Stats.PointCount} pts)"
            };
            if (f.Warning != null) lines.Add("WARNING: " + f.Warning);
            if (f.Alternatives.Count > 1)
            {
                lines.Add("Candidates:");
                foreach (var a in f.Alternatives)
                    lines.Add($"  {a.Surface.Kind,-9} rms {a.Stats.Rms * 1000:0.####}  max {a.Stats.MaxAbs * 1000:0.####}");
            }
            _result.Text = string.Join(Environment.NewLine, lines);
        }

        private void GrowToSurface()
        {
            var r = S.ActiveRegion;
            if (r?.Fit?.Surface == null) { SetStatus("Fit the active region first."); return; }
            ApplyFitSettings();
            S.Regions.BeginStroke();
            int n = PaintTools.GrowToSurface(S.Regions, r.Id, r.Fit.Surface, S.FitOptions.Tolerance);
            S.Regions.EndStroke();
            SetStatus($"Added {n} triangles within tolerance. Fit again to refine the surface.");
        }

        // ------------------------------------------------------------------ files / output

        private void LoadMesh()
        {
            using (var dlg = new OpenFileDialog { Filter = "Meshes (*.stl;*.obj;*.ply)|*.stl;*.obj;*.ply|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                double scale = Units[_units.SelectedIndex].scale;
                Run(() => { _addin.LoadMeshFile(dlg.FileName, scale); return "Mesh loaded. Pick a paint tool and paint the first region."; });
            }
        }

        private void CreateSurfaces(List<Region> regions)
        {
            if (regions.Count == 0) { SetStatus("No region selected."); return; }
            ApplyFitSettings();
            var missing = regions.Where(r => r.Fit == null).ToList();
            foreach (var r in missing) S.FitRegion(r);
            Run(() => _addin.CreateSurfaces(regions));
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
    }
}
