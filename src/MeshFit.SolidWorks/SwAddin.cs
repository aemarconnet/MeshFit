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
    public sealed class SwAddin : ISwAddin
    {
        private const string Title = "MeshFit";
        private const string Description = "Paint mesh regions and fit planes, cylinders, cones, spheres, tori and freeform surfaces.";

        private ISldWorks _app;
        private IMathUtility _math;
        private TaskpaneView _pane;
        private TaskPaneControl _control;
        private ViewHook _hook;
        private IModelDoc2 _hookedDoc;

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
        internal OverlayRenderer Renderer { get; private set; }
        internal ISldWorks App => _app;

        // ------------------------------------------------------------------ ISwAddin

        public bool ConnectToSW(object thisSw, int cookie)
        {
            try
            {
                _app = (ISldWorks)thisSw;
                _app.SetAddinCallbackInfo2(0, this, cookie);
                _math = (IMathUtility)_app.GetMathUtility();
                Renderer = new OverlayRenderer(Session);
                Session.Changed += Redraw;

                _pane = _app.CreateTaskpaneView2(WriteIcon(), Title);
                _control = new TaskPaneControl(this);
                _control.CreateControl();
                _pane.DisplayWindowFromHandlex64(_control.Handle.ToInt64());

                ((SldWorks)_app).ActiveModelDocChangeNotify += OnActiveDocChanged;
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
            }
            Redraw();
        }

        private void Unhook()
        {
            _hook?.Dispose();
            _hook = null;
            _hookedDoc = null;
        }

        internal void Redraw() => _hook?.Redraw();

        internal void ZoomToMesh()
        {
            if (!Session.HasMesh || _hookedDoc == null) return;
            var b = Session.Mesh.Bounds;
            _hookedDoc.ViewZoomTo2(b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z);
            Redraw();
        }

        internal IPartDoc ActivePart => _app.ActiveDoc as IPartDoc;

        // ------------------------------------------------------------------ commands

        internal void LoadMeshFile(string path, double scaleToMetres)
        {
            var mesh = MeshIO.Load(path, scaleToMetres);
            Session.MeshPath = path;
            Session.MeshScale = scaleToMetres;
            Session.ShowMesh = true;
            Session.SetMesh(mesh, $"{Path.GetFileName(path)}: {mesh.TriangleCount:N0} triangles");
            AttachToActiveView(createPartIfNeeded: true);
            ZoomToMesh();
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
            ProjectFile.Save(path, Session.Regions, meshPath, Session.MeshScale);
        }

        internal void LoadProject(string path)
        {
            var h = ProjectFile.ReadHeader(path);
            if (string.IsNullOrEmpty(h.MeshPath) || !File.Exists(h.MeshPath))
                throw new FileNotFoundException("The project's mesh file was not found.", h.MeshPath);
            var mesh = MeshIO.Load(h.MeshPath, h.Scale);
            var regions = ProjectFile.Load(path, mesh);
            Session.MeshPath = h.MeshPath;
            Session.MeshScale = h.Scale;
            Session.SetMesh(mesh, $"{Path.GetFileName(h.MeshPath)}: {mesh.TriangleCount:N0} triangles", regions);
            AttachToActiveView(createPartIfNeeded: true);
            ZoomToMesh();
        }

        /// <summary>Creates SOLIDWORKS surface features for the given (already fitted) regions.</summary>
        internal string CreateSurfaces(IEnumerable<Region> regions)
        {
            var part = ActivePart ?? throw new InvalidOperationException("Activate a part document to create surfaces in.");
            var builder = new SwSurfaceBuilder(_app);
            var log = new List<string>();
            foreach (var r in regions)
            {
                if (r.Fit?.Surface == null) { log.Add($"{r.Name}: not fitted"); continue; }
                try
                {
                    builder.CreateFeature(part, r.Fit.Surface, Session.RegionPoints(r, 3000), Session.ExtentMargin, r.Name, out string how);
                    log.Add($"{r.Name}: created ({how})");
                }
                catch (Exception ex) { log.Add($"{r.Name}: FAILED – {ex.Message}"); }
            }
            ((IModelDoc2)part).GraphicsRedraw2();
            return string.Join(System.Environment.NewLine, log);
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
