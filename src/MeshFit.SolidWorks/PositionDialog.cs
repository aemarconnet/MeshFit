using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MeshFit.Core.Alignment;
using MeshFit.Core.Geometry;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Modeless wizard that places the mesh relative to the part origin: numeric moves, mate-like
    /// alignment of fitted planes/axes/points to part references or selected SOLIDWORKS entities, and
    /// automatic placements (centroid, bounding box, principal axes, symmetry plane).
    /// </summary>
    internal sealed class PositionDialog : Form
    {
        private readonly SwAddin _addin;
        private Session S => _addin.Session;
        private readonly HoverTips _tips = new HoverTips();
        private readonly NumericUpDown[] _move = new NumericUpDown[3];
        private readonly NumericUpDown[] _rot = new NumericUpDown[3];
        private ComboBox _pivot;
        private readonly ComboBox[] _src = new ComboBox[3];
        private readonly ComboBox[] _dst = new ComboBox[3];
        private readonly CheckBox[] _flip = new CheckBox[3];
        private Label _placement, _status;
        private Button _undo;
        private AlignFeature _symmetry;

        public PositionDialog(SwAddin addin)
        {
            _addin = addin;
            Text = "MeshFit – position mesh";
            Font = new Font("Segoe UI", 8.25f);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(470, 400);
            MinimumSize = new Size(440, 360);
            Build();
            S.Changed += OnSessionChanged;
            FormClosed += (s, e) => S.Changed -= OnSessionChanged;
            RefreshPlacement();
        }

        private void OnSessionChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)OnSessionChanged); return; }
            if (!S.HasMesh) { Close(); return; }
            RefreshPlacement();
        }

        // ------------------------------------------------------------------ layout

        private void Build()
        {
            var tabs = new TabControl { Dock = DockStyle.Fill, ShowToolTips = false };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(4) };
            Controls.Add(tabs);
            Controls.Add(bottom);

            // Numeric
            var num = Page(tabs, "Numeric", "Move and rotate by exact amounts");
            var grid = new TableLayoutPanel { ColumnCount = 4, AutoSize = true };
            grid.Controls.Add(new Label { Text = "", AutoSize = true });
            foreach (var a in new[] { "X", "Y", "Z" }) grid.Controls.Add(new Label { Text = a, AutoSize = true, TextAlign = ContentAlignment.MiddleCenter });
            grid.Controls.Add(new Label { Text = "Move mm", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            for (int i = 0; i < 3; i++) { _move[i] = Num(-1e6m, 1e6m, 3, $"Move along {"XYZ"[i]} (mm)"); grid.Controls.Add(_move[i]); }
            grid.Controls.Add(new Label { Text = "Rotate °", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            for (int i = 0; i < 3; i++) { _rot[i] = Num(-360, 360, 3, $"Rotate about the {"XYZ"[i]} axis (degrees, right-hand rule). Rotations apply X, then Y, then Z."); grid.Controls.Add(_rot[i]); }
            num.Controls.Add(grid);
            _pivot = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            _pivot.Items.AddRange(new object[] { "Part origin", "Mesh centroid" });
            _pivot.SelectedIndex = 1;
            _tips.SetToolTip(_pivot, "Point the rotations turn about");
            num.Controls.Add(Row(new Label { Text = "Rotate about", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _pivot));
            num.Controls.Add(Row(Btn("Apply", ApplyNumeric, "Move the mesh by these amounts (relative to its current position)"),
                                 Btn("Clear fields", () => { foreach (var n in _move.Concat(_rot)) n.Value = 0; }, "Reset all fields to zero")));

            // Mates
            var mates = Page(tabs, "Mates", "Align fitted mesh features to part planes, axes and the origin (like assembly mates)");
            mates.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(430, 0),
                Text = "Mesh feature (from fitted regions) → part reference. Mate 1 has priority, then 2, then 3 (3-2-1 alignment)."
            });
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                _src[i] = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
                _dst[i] = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
                _flip[i] = new CheckBox { Text = "Flip", AutoSize = true };
                _tips.SetToolTip(_src[i], $"Mate {i + 1}: mesh feature — plane, axis or point taken from a fitted region");
                _tips.SetToolTip(_dst[i], $"Mate {i + 1}: part reference it should coincide with");
                _tips.SetToolTip(_flip[i], "Reverse the direction (put the mesh on the other side / point the axis the other way)");
                var pick = Btn("Sel.", () => CaptureTarget(k), "Use the plane, face, axis, edge or vertex currently selected in SOLIDWORKS as the target");
                mates.Controls.Add(Row(new Label { Text = $"{i + 1}.", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _src[i], _dst[i], pick, _flip[i]));
            }
            mates.Controls.Add(Row(Btn("Apply mates", ApplyMates, "Solve the mates and move the mesh"),
                                   Btn("Refresh features", FillMateLists, "Reload the mesh features after fitting more regions")));
            FillMateLists();

            // Auto
            var auto = Page(tabs, "Auto", "Automatic placements");
            auto.Controls.Add(Btn("Centroid → origin", () => Apply(MeshAlignment.CentroidToOrigin(S.Mesh), "Centroid moved to the origin."),
                "Translate so the area centroid of the scan is at the part origin (no rotation)"));
            auto.Controls.Add(Btn("Bounding box centre → origin", () => Apply(MeshAlignment.BoxCenterToOrigin(S.Mesh), "Bounding box centre moved to the origin."),
                "Translate so the centre of the axis-aligned bounding box is at the origin"));
            auto.Controls.Add(Btn("Bounding box min corner → origin", () => Apply(MeshAlignment.BoxMinToOrigin(S.Mesh), "Bounding box corner moved to the origin."),
                "Translate so the lowest X/Y/Z corner of the bounding box is at the origin"));
            auto.Controls.Add(Btn("Principal axes → X / Y / Z", () => Apply(MeshAlignment.PrincipalAxes(S.Mesh), "Principal axes aligned (longest along X)."),
                "Centroid to the origin and the longest / middle / shortest extent along X / Y / Z (vehicle length, width, height)"));
            auto.Controls.Add(Btn("Detect symmetry plane → Right plane (YZ)", DetectSymmetry,
                "Find the mirror plane of the scan (e.g. the vehicle centre plane) and put it on the Right plane; also adds it to the mate features"));

            // Bottom
            _placement = new Label { AutoSize = true, MaximumSize = new Size(440, 0) };
            _tips.SetToolTip(_placement, "Total placement applied since the mesh was loaded (saved with the project)");
            bottom.Controls.Add(_placement);
            _undo = Btn("Undo last move", () => { S.UndoPlacement(); Say("Last move undone."); }, "Revert the most recent placement step");
            bottom.Controls.Add(Row(_undo,
                Btn("Reset to original", () => { S.ResetPlacement(); Say("Mesh back at its file position."); }, "Return the mesh to the position it had in the file"),
                Btn("Zoom to mesh", () => _addin.ZoomToMesh(), "Zoom the view to the mesh"),
                Btn("Close", Close, "Close the wizard (the placement stays)")));
            _status = new Label { AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.DimGray };
            bottom.Controls.Add(_status);
        }

        private FlowLayoutPanel Page(TabControl tabs, string title, string tip)
        {
            var page = new TabPage(title) { ToolTipText = tip, UseVisualStyleBackColor = true, AutoScroll = true };
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4), AutoScroll = true };
            page.Controls.Add(panel);
            tabs.TabPages.Add(page);
            return panel;
        }

        private static FlowLayoutPanel Row(params Control[] cs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            p.Controls.AddRange(cs);
            return p;
        }

        private Button Btn(string text, Action a, string tip)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            b.Click += (s, e) =>
            {
                try { a(); }
                catch (Exception ex) { Say("Error: " + ex.Message); }
            };
            _tips.SetToolTip(b, tip);
            return b;
        }

        private NumericUpDown Num(decimal min, decimal max, int decimals, string tip)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Width = 90, Increment = 1 };
            _tips.SetToolTip(n, tip);
            return n;
        }

        private void Say(string msg) => _status.Text = msg;

        // ------------------------------------------------------------------ actions

        private void Apply(RigidTransform x, string message)
        {
            S.ApplyPlacement(x);
            Say(message);
        }

        private void ApplyNumeric()
        {
            var pivot = _pivot.SelectedIndex == 1 ? MeshAlignment.AreaCentroid(S.Mesh) : Vec3.Zero;
            var x = RigidTransform.Identity;
            var axes = new[] { Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ };
            for (int i = 0; i < 3; i++)
                if (_rot[i].Value != 0) x = RigidTransform.Rotation(axes[i], (double)_rot[i].Value * Math.PI / 180, pivot).After(x);
            var t = new Vec3((double)_move[0].Value, (double)_move[1].Value, (double)_move[2].Value) / 1000;
            x = RigidTransform.Translation(t).After(x);
            if (x.IsIdentity()) { Say("All fields are zero."); return; }
            Apply(x, "Moved.");
        }

        private List<AlignFeature> MeshFeatures()
        {
            var list = new List<AlignFeature>();
            if (!S.HasMesh) return list;
            foreach (var r in S.Regions.Regions)
                if (r.Fit?.Surface != null) list.AddRange(AlignFeature.FromSurface(r.Name, r.Fit.Surface));
            if (_symmetry != null) list.Add(_symmetry);
            list.Add(AlignFeature.At("Mesh centroid", MeshAlignment.AreaCentroid(S.Mesh)));
            list.Add(AlignFeature.At("Mesh bounding box centre", S.Mesh.Bounds.Center));
            return list;
        }

        private void FillMateLists()
        {
            var feats = MeshFeatures();
            var refs = AlignFeature.PartReferences();
            for (int i = 0; i < 3; i++)
            {
                var keepSrc = (_src[i].SelectedItem as AlignFeature)?.Name;
                var keepDst = _dst[i].SelectedItem;
                _src[i].Items.Clear();
                _src[i].Items.Add("(none)");
                foreach (var f in feats) _src[i].Items.Add(f);
                _src[i].SelectedIndex = 0;
                for (int k = 1; k < _src[i].Items.Count; k++)
                    if (((AlignFeature)_src[i].Items[k]).Name == keepSrc) _src[i].SelectedIndex = k;
                var captured = _dst[i].Items.Cast<object>().OfType<AlignFeature>().Where(f => !refs.Any(r => r.Name == f.Name)).ToList();
                _dst[i].Items.Clear();
                foreach (var r in refs) _dst[i].Items.Add(r);
                foreach (var c in captured) _dst[i].Items.Add(c);
                _dst[i].SelectedIndex = 0;
                if (keepDst != null) for (int k = 0; k < _dst[i].Items.Count; k++)
                    if (((AlignFeature)_dst[i].Items[k]).Name == ((AlignFeature)keepDst).Name) _dst[i].SelectedIndex = k;
            }
            if (feats.Count <= 2) Say("Fit some regions (planes, cylinders…) to get mesh features for mates.");
        }

        private void CaptureTarget(int row)
        {
            var f = _addin.CaptureSelectedTarget();
            _dst[row].Items.Add(f);
            _dst[row].SelectedItem = f;
            Say($"Mate {row + 1} target: {f.Name}");
        }

        private void ApplyMates()
        {
            var mates = new List<AlignMate>();
            for (int i = 0; i < 3; i++)
            {
                if (!(_src[i].SelectedItem is AlignFeature src) || !(_dst[i].SelectedItem is AlignFeature dst)) continue;
                mates.Add(new AlignMate { Source = src, Target = dst, Flip = _flip[i].Checked });
            }
            if (mates.Count == 0) { Say("Choose at least one mesh feature."); return; }
            var x = MeshAlignment.Solve(mates);
            Apply(x, $"{mates.Count} mate(s) applied.");
            FillMateLists();
        }

        private async void DetectSymmetry()
        {
            var mesh = S.Mesh;
            Say("Detecting the symmetry plane… (a few seconds)");
            UseWaitCursor = true;
            Enabled = false;
            try
            {
                double rms = 0;
                var plane = await Task.Run(() => MeshAlignment.SymmetryPlane(mesh, out rms));
                if (S.Mesh != mesh) return;
                _symmetry = plane;
                var x = MeshAlignment.Solve(new[] { new AlignMate { Source = plane, Target = AlignFeature.PartReferences()[2] } });
                Apply(x, $"Symmetry plane put on the Right plane (mirror RMS {rms * 1000:0.###} mm{(rms > 0.002 ? " — check: the scan may not be symmetric" : "")}).");
                _symmetry = AlignFeature.Plane("Symmetry plane", x.Apply(plane.Point), x.ApplyVector(plane.Direction));
                FillMateLists();
            }
            catch (Exception ex) { Say("Error: " + ex.Message); }
            finally { UseWaitCursor = false; Enabled = true; }
        }

        private void RefreshPlacement()
        {
            if (!S.HasMesh) return;
            var x = S.MeshTransform;
            x.ToEulerXYZ(out double rx, out double ry, out double rz);
            var inv = CultureInfo.InvariantCulture;
            _placement.Text = x.IsIdentity()
                ? "Placement: as in the file."
                : string.Format(inv, "Placement: move ({0:0.###}, {1:0.###}, {2:0.###}) mm, rotate X {3:0.###}° Y {4:0.###}° Z {5:0.###}°",
                    x.T.X * 1000, x.T.Y * 1000, x.T.Z * 1000, rx, ry, rz);
            _undo.Enabled = S.CanUndoPlacement;
        }
    }
}
