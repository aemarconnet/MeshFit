using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using MeshFit.Core.Export;
using MeshFit.Core.Fitting;
using MeshFit.Core.Mesh;
using MeshFit.Core.Project;
using MeshFit.Core.Selection;
using Region = MeshFit.Core.Selection.Region;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// SOLIDWORKS add-in entry point. Registers with SOLIDWORKS through RegAsm (see tools\register.bat),
    /// shows the MeshFit task pane and owns the session, overlay and view hooks.
    /// </summary>
    [ComVisible(true)]
    [Guid("6F1C2A7E-3B4D-4E8A-9C21-5D7B0E9A4F13")]
    [ProgId("MeshFit.SwAddin")]
    public sealed partial class SwAddin : ISwAddin
    {
        private const string Title = "MeshFit";
        private const string Description = "Paint mesh regions and fit planes, cylinders, cones, spheres, tori and freeform surfaces.";

        private ISldWorks _app;
        private IMathUtility _math;
        private TaskpaneView _pane;
        private TaskPaneControl _control;
        private ViewHook _hook;
        private IModelDoc2 _hookedDoc;
        private int _cookie;

        static SwAddin()
        {
            // SOLIDWORKS loads the add-in from its registered codebase; make sure MeshFit.Core and the
            // interop assemblies next to it resolve even when the CLR probes SOLIDWORKS' own folder.
            string dir = Path.GetDirectoryName(typeof(SwAddin).Assembly.Location);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string file = Path.Combine(dir, new System.Reflection.AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? System.Reflection.Assembly.LoadFrom(file) : null;
            };
        }

        internal Session Session { get; } = new Session();
        internal SuggestionEngine Suggestions { get; private set; }
        /// <summary>Raised when the brush was resized from the view (Ctrl/Shift + wheel).</summary>
        internal event Action BrushResized;
        internal OverlayRenderer Renderer { get; private set; }
        internal ISldWorks App => _app;

        // ------------------------------------------------------------------ ISwAddin

        public bool ConnectToSW(object thisSw, int cookie)
        {
            try
            {
                _app = (ISldWorks)thisSw;
                _cookie = cookie;
                _app.SetAddinCallbackInfo2(0, this, cookie);
                _math = (IMathUtility)_app.GetMathUtility();
                Renderer = new OverlayRenderer(Session);
                Session.Changed += Redraw;
                Suggestions = new SuggestionEngine(Session);

                _pane = _app.CreateTaskpaneView2(WriteIcon(), Title);
                _control = new TaskPaneControl(this);
                _control.CreateControl();
                _pane.DisplayWindowFromHandlex64(_control.Handle.ToInt64());

                ((SldWorks)_app).ActiveModelDocChangeNotify += OnActiveDocChanged;
                try { AddContextMenus(); }
                catch (Exception ex) { Trace.WriteLine("MeshFit context menu: " + ex); }
                try { AddRibbon(cookie); }
                catch (Exception ex) { Trace.WriteLine("MeshFit ribbon: " + ex); }
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("MeshFit connect failed: " + ex);
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            try
            {
                ((SldWorks)_app).ActiveModelDocChangeNotify -= OnActiveDocChanged;
                RemoveContextMenus();
                RemoveRibbon();
                Suggestions?.Dispose();
                Unhook();
                _pane?.DeleteView();
                _control?.Dispose();
            }
            catch (Exception ex) { Trace.WriteLine("MeshFit disconnect: " + ex); }
            if (_pane != null) Marshal.ReleaseComObject(_pane);
            _pane = null; _app = null; _math = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        private int OnActiveDocChanged()
        {
            if (Session.HasMesh) AttachToActiveView();
            return 0;
        }

        // ------------------------------------------------------------------ views

        /// <summary>Hooks the active view of the active part (creating a part if none is open).</summary>
        internal void AttachToActiveView(bool createPartIfNeeded = false)
        {
            var doc = _app.ActiveDoc as IModelDoc2;
            if (doc == null && createPartIfNeeded)
            {
                string template = _app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                doc = _app.NewDocument(template, 0, 0, 0) as IModelDoc2;
            }
            if (doc == null) return;
            if (doc.ActiveView is ModelView view && (_hook == null || !ReferenceEquals(_hook.View, view)))
            {
                Unhook();
                _hookedDoc = doc;
                _hook = new ViewHook(view, doc, _math, Session, Renderer);
                _hook.Destroyed += () => { _hook = null; _hookedDoc = null; };
                _hook.BrushResized += () => BrushResized?.Invoke();
            }
            Redraw();
        }

        private void Unhook()
        {
            _hook?.Dispose();
            _hook = null;
            _hookedDoc = null;
        }

        /// <summary>Repaints the hooked view. Safe to call from worker threads (marshals to the UI thread).</summary>
        internal void Redraw()
        {
            if (_control != null && _control.IsHandleCreated && _control.InvokeRequired)
            {
                _control.BeginInvoke((Action)Redraw);
                return;
            }
            _hook?.Redraw();
        }

        internal void ZoomToMesh()
        {
            if (!Session.HasMesh || _hookedDoc == null) return;
            var b = Session.Mesh.Bounds;
            _hookedDoc.ViewZoomTo2(b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z);
            Redraw();
        }

        internal IPartDoc ActivePart => _app.ActiveDoc as IPartDoc;

        private System.Threading.CancellationTokenSource _curvatureCts;

        /// <summary>Computes the curvature field for fillet detection on a worker thread (Esc cancels).</summary>
        internal void ComputeCurvature()
        {
            var mesh = Session.Mesh;
            if (mesh == null) return;
            double scale = Session.FeatureScale > 0 ? Session.FeatureScale : Curvature.DefaultScale(mesh);
            _curvatureCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _curvatureCts = cts;
            Session.Curvature = null;
            Session.Say("Analysing curvature for fillet detection… (Esc cancels)");
            int lastPct = -1;
            var progress = new Progress<double>(f =>
            {
                int pct = (int)(f * 100);
                if (pct / 10 == lastPct / 10 || cts.IsCancellationRequested) return;
                lastPct = pct;
                Session.Say($"Analysing curvature for fillet detection… {pct}% (Esc cancels)");
            });
            System.Threading.Tasks.Task.Run(() => Curvature.Compute(mesh, scale, cts.Token, progress), cts.Token).ContinueWith(t =>
            {
                if (_curvatureCts == cts) _curvatureCts = null;
                if (t.IsFaulted || t.IsCanceled || Session.Mesh != mesh) return;
                void Apply()
                {
                    Session.Curvature = t.Result;
                    Session.Say($"Fillet detection ready (scale {scale * 1000:0.##} mm).");
                    Session.RaiseChanged();
                }
                if (_control != null && _control.IsHandleCreated) _control.BeginInvoke((Action)Apply); else Apply();
            });
        }

        /// <summary>Cancels a running curvature analysis; returns false if none was running.</summary>
        internal bool CancelCurvature()
        {
            var c = _curvatureCts;
            if (c == null) return false;
            c.Cancel();
            _curvatureCts = null;
            return true;
        }

        // ------------------------------------------------------------------ commands

        /// <summary>Installs a mesh loaded on a worker thread (UI thread).</summary>
        internal void InstallMesh(TriMesh mesh, string path, double scaleToMetres)
        {
            Session.MeshPath = path;
            Session.MeshScale = scaleToMetres;
            Session.ShowMesh = true;
            Session.SetMesh(mesh, $"{Path.GetFileName(path)}: {mesh.TriangleCount:N0} triangles");
            ComputeCurvature();
            AttachToActiveView(createPartIfNeeded: true);
            ZoomToMesh();
        }

        internal void UnloadMesh()
        {
            CancelCurvature();
            Suggestions.Cancel();
            Session.Unload();
            Redraw();
        }

        internal void UseBodies()
        {
            var doc = _app.ActiveDoc as IModelDoc2 ?? throw new InvalidOperationException("Open the part containing the mesh body first.");
            var mesh = SwMeshSource.FromSelectionOrPart(doc, out string desc);
            if (mesh.TriangleCount == 0) throw new InvalidOperationException("The selected bodies have no facets.");
            // Keep a copy so the project can be reopened later without the part.
            string copy = Path.Combine(Path.GetTempPath(), "MeshFit_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".stl");
            using (var fs = File.Create(copy)) MeshIO.WriteBinaryStl(mesh, fs);
            Session.MeshPath = copy;
            Session.MeshScale = 1;
            Session.ShowMesh = false; // SOLIDWORKS already shows the body
            Session.SetMesh(mesh, $"{desc}: {mesh.TriangleCount:N0} triangles");
            ComputeCurvature();
            doc.ClearSelection2(true);
            AttachToActiveView();
        }

        internal void SaveProject(string path)
        {
            string meshPath = Session.MeshPath;
            // A mesh taken from a SOLIDWORKS body lives in a temp file: store it beside the project.
            if (meshPath != null && meshPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                string dest = Path.ChangeExtension(path, ".stl");
                File.Copy(meshPath, dest, true);
                Session.MeshPath = meshPath = dest;
            }
            ProjectFile.Save(path, Session.Regions, meshPath, Session.MeshScale, Session.MeshTransform);
        }

        /// <summary>Reads a project and its mesh (worker thread safe: no SOLIDWORKS calls).</summary>
        internal static (TriMesh mesh, RegionSet regions, ProjectFile.Header header) ReadProject(string path)
        {
            var h = ProjectFile.ReadHeader(path);
            if (string.IsNullOrEmpty(h.MeshPath) || !File.Exists(h.MeshPath))
                throw new FileNotFoundException("The project's mesh file was not found.", h.MeshPath);
            var mesh = MeshIO.Load(h.MeshPath, h.Scale);
            var regions = ProjectFile.Load(path, mesh);
            return (mesh, regions, h);
        }

        /// <summary>Installs a project read by <see cref="ReadProject"/> (UI thread), re-applying its placement.</summary>
        internal void InstallProject((TriMesh mesh, RegionSet regions, ProjectFile.Header header) p)
        {
            Session.MeshPath = p.header.MeshPath;
            Session.MeshScale = p.header.Scale;
            Session.SetMesh(p.mesh, $"{Path.GetFileName(p.header.MeshPath)}: {p.mesh.TriangleCount:N0} triangles", p.regions);
            Session.ApplyPlacement(p.header.Transform, record: false);
            ComputeCurvature();
            AttachToActiveView(createPartIfNeeded: true);
            ZoomToMesh();
        }

        /// <summary>
        /// Creates SOLIDWORKS surface features for the given (already fitted) regions.
        /// <paramref name="replace"/> decides per region whether an existing MeshFit surface is deleted first.
        /// </summary>
        internal string CreateSurfaces(IEnumerable<Region> regions, Func<Region, bool> replace, Action<string, double?> progress = null)
        {
            var part = ActivePart ?? throw new InvalidOperationException("Activate a part document to create surfaces in.");
            var doc = (IModelDoc2)part;
            var builder = new SwSurfaceBuilder(_app);
            var log = new List<string>();
            var list = regions.ToList();
            using (var bar = BeginSwProgress("MeshFit: creating surfaces", list.Count))
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (EscKey.IsDown()) { log.Add("Stopped (Esc)."); break; }
                progress?.Invoke($"Creating surface for '{r.Name}' ({i + 1} of {list.Count})", (double)i / list.Count);
                bar.Update(i, $"MeshFit: creating surface '{r.Name}' ({i + 1}/{list.Count})");
                if (r.Fit?.Surface == null) { log.Add($"{r.Name}: not fitted"); continue; }
                try
                {
                    var old = FindFeature(r);
                    if (old != null && replace(r))
                    {
                        doc.ClearSelection2(true);
                        old.Select2(false, 0);
                        doc.Extension.DeleteSelection2(0);
                    }
                    var feat = builder.CreateFeature(part, r.Fit.Surface, Session.RegionPoints(r, 3000), Session.ExtentMargin, r.Name, out string how);
                    r.FeatureName = feat.Name;
                    r.SurfaceHidden = false;
                    log.Add($"{r.Name}: created '{feat.Name}' ({how})");
                }
                catch (Exception ex) { log.Add($"{r.Name}: FAILED – {ex.Message}"); }
            }
            doc.ClearSelection2(true);
            doc.GraphicsRedraw2();
            Session.RaiseChanged();
            return string.Join(System.Environment.NewLine, log);
        }

        internal Feature FindFeature(Region r)
        {
            if (string.IsNullOrEmpty(r.FeatureName) || ActivePart == null) return null;
            return ActivePart.FeatureByName(r.FeatureName) as Feature;
        }

        /// <summary>Hides or shows the surface bodies created from the regions. Returns how many changed.</summary>
        internal int SetSurfacesHidden(IEnumerable<Region> regions, bool hidden)
        {
            int n = 0;
            foreach (var r in regions)
            {
                var feat = FindFeature(r);
                if (feat == null) continue;
                var bodies = new List<Body2>();
                if (feat.GetFaces() is object[] faces)
                    foreach (Face2 f in faces)
                        if (f.GetBody() is Body2 b && !bodies.Any(x => x.Name == b.Name)) bodies.Add(b);
                foreach (var b in bodies) b.HideBody(hidden);
                r.SurfaceHidden = hidden;
                n++;
            }
            (_app.ActiveDoc as IModelDoc2)?.GraphicsRedraw2();
            Session.RaiseChanged();
            return n;
        }

        internal void ExportIges(string path, IEnumerable<Region> regions)
        {
            var w = new IgesWriter { ToMillimetres = 1000 };
            foreach (var r in regions)
            {
                if (r.Fit?.Surface == null) continue;
                var s = r.Fit.Surface;
                Extents ext = default;
                if (s is AnalyticSurface a) ext = SurfaceExtents.Compute(a, Session.RegionPoints(r, 5000), Session.ExtentMargin);
                w.Add(NurbsConversion.ToNurbs(s, ext), r.Name);
            }
            w.Save(path);
        }

        // ------------------------------------------------------------------ registration

        private static string WriteIcon()
        {
            string path = Path.Combine(Path.GetTempPath(), "MeshFit_taskpane.bmp");
            using (var bmp = new Bitmap(16, 18))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                var pts = new[] { new Point(1, 15), new Point(8, 2), new Point(15, 15) };
                g.FillPolygon(Brushes.SteelBlue, pts);
                g.DrawLine(Pens.Orange, 1, 10, 15, 6);
                g.DrawLine(Pens.Orange, 1, 12, 15, 9);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Bmp);
            }
            return path;
        }

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            try
            {
                string key = @"SOFTWARE\SolidWorks\Addins\{" + t.GUID + "}";
                using (var k = Registry.LocalMachine.CreateSubKey(key))
                {
                    k.SetValue(null, 0);
                    k.SetValue("Description", Description);
                    k.SetValue("Title", Title);
                }
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\{" + t.GUID + "}"))
                    k.SetValue(null, 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Console.WriteLine("MeshFit: registry update failed (run as administrator): " + ex.Message);
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            try
            {
                Registry.LocalMachine.DeleteSubKey(@"SOFTWARE\SolidWorks\Addins\{" + t.GUID + "}", false);
                Registry.CurrentUser.DeleteSubKey(@"Software\SolidWorks\AddInsStartup\{" + t.GUID + "}", false);
            }
            catch (Exception ex) { Console.WriteLine("MeshFit: " + ex.Message); }
        }
    }
}
