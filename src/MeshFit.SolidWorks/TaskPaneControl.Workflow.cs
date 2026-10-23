using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeshFit.Core.Analysis;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Selection;
using Region = MeshFit.Core.Selection.Region;

namespace MeshFit.SolidWorks
{
    /// <summary>Operations behind the Region Groups mode and the modeling wizards (they reuse the task pane's job runner).</summary>
    internal sealed partial class TaskPaneControl
    {
        private void OpenWizard(WizardKind kind)
        {
            if (!NeedMesh()) return;
            _addin.ShowPage(_addin.Wizard(kind));
        }

        // ------------------------------------------------------------------ settings shared with the pages

        /// <summary>Extrusion directions / revolve axes within this angle of X/Y/Z snap onto the axis.</summary>
        internal double SnapDegrees
        {
            get => (double)_snapDeg.Value;
            set => _snapDeg.Value = Clamp((decimal)value, _snapDeg);
        }

        internal void SetExtendPercent(double pct)
        {
            _margin.Value = Clamp((decimal)pct, _margin);
            ApplyFitSettings();
        }

        internal void SetFreeform(int? controlPointsU, int? controlPointsV, int? smoothingExponent)
        {
            if (controlPointsU.HasValue) _ctrlU.Value = Clamp(controlPointsU.Value, _ctrlU);
            if (controlPointsV.HasValue) _ctrlV.Value = Clamp(controlPointsV.Value, _ctrlV);
            if (smoothingExponent.HasValue) _smoothExp.Value = Clamp(smoothingExponent.Value, _smoothExp);
            ApplyFitSettings();
        }

        // ------------------------------------------------------------------ Region Groups

        /// <summary>
        /// Auto Segment (Design X's name): split the unassigned scan into regions at creases and, once the curvature
        /// analysis is ready, at fillets; then optionally classify every new region by fitting it (Esc cancels).
        /// </summary>
        internal void AutoSegmentRegions(double angleDeg, double blendRadius, int minTriangles, bool classify, bool resegment)
        {
            Guard(() =>
            {
                if (!NeedMesh()) return;
                if (resegment)
                {
                    S.Regions.BeginStroke("Resegment");
                    foreach (var r in S.Regions.Regions.Where(r => r.FeatureName == null).ToList()) S.Regions.RemoveRegion(r);
                    S.Regions.EndStroke();
                    S.RaiseChanged();
                }
                // The segmentation reads a snapshot on a worker thread; the regions are created on the UI thread.
                var mesh = S.Mesh;
                var owners = (int[])S.Regions.Owners.Clone();
                var curvature = S.Curvature;
                PaintTools.Segmentation seg = null;
                Guarded.Step($"Auto Segment start: {mesh.TriangleCount:N0} triangles, curvature {(curvature != null ? "ready" : "not ready")}");
                Run(() =>
                {
                    Report(curvature != null ? "Splitting at creases and fillets" : "Splitting at creases (curvature analysis not ready yet)", null);
                    seg = PaintTools.Segment(mesh, owners, angleDeg, Math.Max(1, minTriangles), curvature, blendRadius, true, Token);
                    return $"{seg.Count} region(s) found.";
                }, background: true, title: "Auto Segment", then: () =>
                {
                    if (S.Mesh != mesh) return;
                    var created = PaintTools.ApplySegmentation(S.Regions, seg);
                    Guarded.Step($"Auto Segment created {created.Count} regions");
                    if (S.ActiveRegion == null || S.ActiveRegion.TriangleCount == 0)
                        S.ActiveRegion = created.OrderByDescending(r => r.TriangleCount).FirstOrDefault() ?? S.ActiveRegion;
                    S.RaiseChanged();
                    SetStatus($"Auto Segment: {created.Count} region(s)" + (curvature == null
                        ? " (creases only; run it again when the curvature analysis is ready to split at fillets)." : "."));
                    if (classify && created.Count > 0)
                        FitRegions(created.OrderByDescending(r => r.TriangleCount).ToList(),
                            () => SetStatus($"Auto Segment: {created.Count} region(s) classified. Pick regions for Surface Primitives or the wizards."),
                            classifyOnly: true);
                });
            });
        }

        internal void MergeInto(Region target, Region other)
        {
            Guard(() =>
            {
                if (target == null || other == null || target == other) return;
                S.Regions.BeginStroke($"Merge {other.Name} into {target.Name}");
                foreach (int t in S.Regions.TriangleListOf(other.Id)) S.Regions.Assign(t, target.Id);
                S.Regions.RemoveRegion(other);
                S.Regions.EndStroke();
                target.Fit = null;
                S.ActiveRegion = target;
                S.RaiseChanged();
                SetStatus($"Merged into '{target.Name}'. Undo splits them again.");
            });
        }

        // ------------------------------------------------------------------ wizards

        /// <summary>Fits (primitives / Mesh Fit) or traces the profile (extrusion / revolution) and reports the result.</summary>
        internal void WizardPreview(WizardKind kind, List<Region> regions, FitRequest request, Action<string> done)
        {
            Guard(() =>
            {
                if (kind == WizardKind.Extrusion || kind == WizardKind.Revolution)
                {
                    PreviewProfile(kind == WizardKind.Revolution, regions, done);
                    return;
                }
                foreach (var r in regions) { r.Request = request; r.Fit = null; }
                FitRegions(regions, () =>
                {
                    S.ShowDeviation = true;
                    UpdateLegendVisibility();
                    S.RaiseChanged();
                    done?.Invoke(Summary(regions));
                });
            });
        }

        /// <summary>Creates the SOLIDWORKS features for the wizard's regions.</summary>
        internal void WizardCreate(WizardKind kind, List<Region> regions, FitRequest request, Action<string> done)
        {
            Guard(() =>
            {
                if (kind == WizardKind.Extrusion || kind == WizardKind.Revolution)
                {
                    CreateSketched(kind == WizardKind.Revolution, autoNext: false,
                        finished: ok => done?.Invoke(ok ? _status.Text : "Not created: " + _status.Text), regions: regions);
                    return;
                }
                foreach (var r in regions.Where(r => r.Request != request || r.Fit == null)) { r.Request = request; r.Fit = null; }
                bool ok2 = CreateSurfaces(regions, autoNext: false);
                if (ok2) _addin.SetSurfacesHidden(regions.Where(r => r.FeatureName != null), false);
                done?.Invoke(_status.Text);
            });
        }

        private void PreviewProfile(bool revolve, List<Region> regions, Action<string> done)
        {
            if (!NeedMesh() || regions.Count == 0) return;
            ApplyFitSettings();
            var tris = regions.SelectMany(x => S.Regions.TriangleListOf(x.Id)).ToList();
            double tol = S.FitOptions.Tolerance, snap = (double)_snapDeg.Value;
            var hint = regions[0].Fit?.Surface;
            SketchProfile prof = null;
            Run(() =>
            {
                Report(revolve ? "Finding the axis and the profile" : "Finding the extrusion direction and the profile", null);
                prof = revolve
                    ? (SketchProfile)SketchProfiles.Revolution(S.Mesh, tris, tol, hint, 2, snap)
                    : SketchProfiles.Extrusion(S.Mesh, tris, tol, S.ExtentMargin, snap);
                return "Profile: " + prof.Describe(1000, "mm");
            }, background: true, title: revolve ? "Revolution Wizard preview" : "Extrusion Wizard preview", then: () =>
            {
                S.ClearSketchPreview();
                foreach (var seg in prof.Segments) S.SketchCurves.Add(SampleSegment(prof, seg));
                var inv = CultureInfo.InvariantCulture;
                string text = prof.Describe(1000, "mm");
                if (prof is ExtrusionProfile ep)
                    text += $"\nDepth {(ep.Depth * 1000).ToString("0.###", inv)} mm; direction {Describe(ep.Direction)}";
                if (prof is RevolutionProfile rp)
                    text += $"\nAxis {Describe(rp.Axis)}; {(rp.Sweep * 180 / Math.PI).ToString("0.#", inv)}°";
                if (prof.MaxDeviation > 3 * tol) text += "\n⚠ The profile deviates more than 3× the tolerance: the regions may not form one simple feature.";
                S.RaiseChanged();
                done?.Invoke(text);
            });
        }

        private static string Describe(Vec3 d)
        {
            var inv = CultureInfo.InvariantCulture;
            if (Math.Abs(Math.Abs(d.X) - 1) < 1e-9) return "∥ X";
            if (Math.Abs(Math.Abs(d.Y) - 1) < 1e-9) return "∥ Y";
            if (Math.Abs(Math.Abs(d.Z) - 1) < 1e-9) return "∥ Z";
            return $"({d.X.ToString("0.###", inv)}, {d.Y.ToString("0.###", inv)}, {d.Z.ToString("0.###", inv)})";
        }

        private static Vec3[] SampleSegment(SketchProfile p, ProfileSegment s)
        {
            if (!s.IsArc) return new[] { p.To3D(s.X0, s.Y0), p.To3D(s.X1, s.Y1) };
            double a0 = Math.Atan2(s.Y0 - s.Cy, s.X0 - s.Cx), a1 = Math.Atan2(s.Y1 - s.Cy, s.X1 - s.Cx);
            double sweep = s.CounterClockwise ? a1 - a0 : a0 - a1;
            while (sweep <= 1e-9) sweep += 2 * Math.PI;
            int n = Math.Max(8, (int)(sweep / (Math.PI / 36)));
            var pts = new Vec3[n + 1];
            for (int i = 0; i <= n; i++)
            {
                double a = a0 + (s.CounterClockwise ? 1 : -1) * sweep * i / n;
                pts[i] = p.To3D(s.Cx + s.Radius * Math.Cos(a), s.Cy + s.Radius * Math.Sin(a));
            }
            return pts;
        }

        private string Summary(List<Region> regions)
        {
            double tol = S.FitOptions.Tolerance;
            var lines = regions.Select(r => RegionGroupsPage.RegionLine(r, tol)).ToList();
            var applied = regions.Where(r => r.Fit?.Constraints != null && r.Fit.Constraints.Count > 0)
                                 .Select(r => $"{r.Name}: {string.Join("; ", r.Fit.Constraints)}").ToList();
            if (applied.Count > 0) lines.Add("Clean: " + string.Join(" | ", applied));
            return string.Join("\n", lines);
        }
    }
}
