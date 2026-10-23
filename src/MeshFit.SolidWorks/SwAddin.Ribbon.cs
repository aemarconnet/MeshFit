using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using MeshFit.Core.Fitting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace MeshFit.SolidWorks
{
    /// <summary>MeshFit CommandManager tab (ribbon) and menu.</summary>
    public sealed partial class SwAddin
    {
        private const int RibbonGroupId = 7301;
        private const string RibbonTitle = "MeshFit";
        private ICommandManager _cmdMgr;

        // (command, button text, tooltip)
        private static readonly (Cmd cmd, string text, string tip)[] RibbonCommands =
        {
            (Cmd.ReverseEngineer, "Reverse Engineer", "Paint a feature on the mesh, fit it and create the surface (PropertyManager)"),
            (Cmd.LoadMesh, "Load Mesh", "Load an STL / OBJ / PLY scan"),
            (Cmd.UseBodies, "Mesh Body", "Use the selected mesh body (or all bodies of the part) as the scan"),
            (Cmd.PositionMesh, "Position Mesh", "Place the scan relative to the part origin: numeric, mates or automatic"),
            (Cmd.ToolSmartFill, "Smart Fill", "Click or drag: fill smooth areas up to sharp edges (and fillets when 'Stop at fillets' is on)"),
            (Cmd.ToolBrush, "Brush", "Paint inside the brush circle. Ctrl+click paints a straight line; Ctrl/Shift + wheel resizes"),
            (Cmd.ToolFillet, "Fillet", "Click a fillet or blend to select the whole band"),
            (Cmd.ToolFillShell, "Shell", "Click to fill a whole connected piece of mesh"),
            (Cmd.ToolPick, "Pick Region", "Click painted triangles to make their region active"),
            (Cmd.ToolOff, "Paint Off", "Stop painting; SOLIDWORKS works normally (Esc)"),
            (Cmd.ToggleMode, "Remove", "Pressed: the tools unselect triangles instead of adding them (Shift = temporary)"),
            (Cmd.NewRegion, "New Region", "Start a new region for the next feature"),
            (Cmd.FillIslands, "Fill Islands", "Fill unpainted islands enclosed by the active region"),
            (Cmd.CleanUp, "Clean Up", "Remove specks, fill holes and smooth the jagged brush edge"),
            (Cmd.Optimize, "Optimize Selection", "Try growing / shrinking / trimming the active region for the best fit (Esc cancels)"),
            (Cmd.TypeAuto, "Auto", "Fit the simplest surface type within tolerance"),
            (Cmd.TypePlane, "Plane", "Fit the active region as a plane"),
            (Cmd.TypeCylinder, "Cylinder", "Fit the active region as a cylinder"),
            (Cmd.TypeCone, "Cone", "Fit the active region as a cone"),
            (Cmd.TypeSphere, "Sphere", "Fit the active region as a sphere"),
            (Cmd.TypeTorus, "Torus", "Fit the active region as a torus"),
            (Cmd.TypeFreeform, "Freeform", "Fit the active region as a B-spline surface"),
            (Cmd.FitAndGrow, "Fit + Grow", "Fit the active region and grow it over its surface (Esc cancels)"),
            (Cmd.Fit, "Fit Only", "Fit the active region without growing it"),
            (Cmd.FitAll, "Fit All", "Fit every painted region (Esc cancels)"),
            (Cmd.CreateSurface, "Surface", "Create the fitted surface: true plane / cylinder / cone / sphere / torus, or B-spline"),
            (Cmd.CreateExtrude, "Extruded Surface", "Editable sketch of lines and arcs plus an extruded surface"),
            (Cmd.CreateRevolve, "Revolved Surface", "Editable sketch of lines and arcs plus a revolved surface"),
            (Cmd.NextFeature, "Next Feature", "Hide the finished surface and start painting the next feature"),
            (Cmd.HideSurfaces, "Hide Surfaces", "Hide all surfaces created by MeshFit"),
            (Cmd.ShowSurfaces, "Show Surfaces", "Show all surfaces created by MeshFit"),
            (Cmd.AnalyzeFillet, "Measure Fillet", "Measure the active region as a constant or variable fillet"),
            (Cmd.BoundaryToSketch, "Boundary Curves", "3D sketch of the active region's boundary (trim lines, tangent lines)"),
            (Cmd.AnalyzeCad, "Accuracy", "Colour the scan by its distance to the bodies in the part"),
            (Cmd.ToggleZebra, "Zebra", "Toggle zebra stripes"),
            (Cmd.ExportIges, "Export IGES", "Write the fitted surfaces to an IGES file"),
            (Cmd.SaveProject, "Save Project", "Save the painted regions and mesh placement"),
            (Cmd.OpenProject, "Open Project", "Reopen a saved MeshFit project"),
            (Cmd.Settings, "Settings", "Tolerances, clean-geometry rules, fillet detection and other options"),
            (Cmd.UnloadMesh, "Unload Mesh", "Remove the scan from MeshFit"),
        };

        private static readonly Cmd[] LargeButtons =
        {
            Cmd.ReverseEngineer, Cmd.LoadMesh, Cmd.ToolSmartFill, Cmd.ToolBrush, Cmd.FitAndGrow,
            Cmd.CreateSurface, Cmd.CreateExtrude, Cmd.CreateRevolve, Cmd.NextFeature, Cmd.AnalyzeCad,
        };

        private void AddRibbon(int cookie)
        {
            _cmdMgr = _app.GetCommandManager(cookie);
            string dir = Path.Combine(Path.GetTempPath(), "MeshFitIcons");
            Directory.CreateDirectory(dir);
            var sizes = new[] { 20, 32, 40, 64, 96, 128 };
            var strips = sizes.Select(sz => WriteStrip(dir, sz, RibbonCommands.Select(c => c.cmd).ToArray())).ToArray();
            var main = sizes.Select(sz => WriteStrip(dir, sz, new[] { Cmd.ReverseEngineer }, "main")).ToArray();

            int err = 0;
            var grp = _cmdMgr.CreateCommandGroup2(RibbonGroupId, RibbonTitle, "MeshFit reverse engineering", "MeshFit", -1, true, ref err);
            grp.IconList = strips;
            grp.MainIconList = main;
            int opts = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
            var index = new int[RibbonCommands.Length];
            for (int i = 0; i < RibbonCommands.Length; i++)
            {
                var (cmd, text, tip) = RibbonCommands[i];
                index[i] = grp.AddCommandItem2(text, -1, tip, text, i, "OnMenu" + cmd, "OnRibbonEnable" + cmd, i, opts);
            }
            grp.HasToolbar = true;
            grp.HasMenu = true;
            grp.Activate();

            foreach (int docType in new[] { (int)swDocumentTypes_e.swDocPART })
            {
                var tab = _cmdMgr.GetCommandTab(docType, RibbonTitle);
                if (tab != null) _cmdMgr.RemoveCommandTab((CommandTab)tab);
                tab = _cmdMgr.AddCommandTab(docType, RibbonTitle);
                // Mesh | Select | Fit | Build | Analyze | Files
                AddBox(tab, grp, index, new[] { 0, 1, 2, 3 });
                AddBox(tab, grp, index, new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 });
                AddBox(tab, grp, index, new[] { 15, 16, 17, 18, 19, 20, 21, 22, 23, 24 });
                AddBox(tab, grp, index, new[] { 25, 26, 27, 28, 29, 30 });
                AddBox(tab, grp, index, new[] { 31, 32, 33, 34 });
                AddBox(tab, grp, index, new[] { 35, 36, 37, 38, 39 });
            }
        }

        private static void AddBox(CommandTab tab, CommandGroup grp, int[] index, int[] which)
        {
            var box = tab.AddCommandTabBox();
            var ids = which.Select(i => grp.get_CommandID(index[i])).ToArray();
            // The main steps get large buttons; the rest stack three high with their text beside them.
            var text = which.Select(i => (int)(LargeButtons.Contains(RibbonCommands[i].cmd)
                ? swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow
                : swCommandTabButtonTextDisplay_e.swCommandTabButton_TextHorizontal)).ToArray();
            box.AddCommands(ids, text);
        }

        private static string WriteStrip(string dir, int size, Cmd[] cmds, string tag = "strip")
        {
            string path = Path.Combine(dir, $"{tag}_{size}.png");
            using (var strip = new Bitmap(size * cmds.Length, size))
            using (var g = Graphics.FromImage(strip))
            {
                g.Clear(Color.Transparent);
                for (int i = 0; i < cmds.Length; i++)
                    using (var icon = CommandIcons.Render(cmds[i], size, transparent: true)) g.DrawImage(icon, i * size, 0, size, size);
                strip.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            return path;
        }

        private void RemoveRibbon()
        {
            try
            {
                if (_cmdMgr == null) return;
                var tab = _cmdMgr.GetCommandTab((int)swDocumentTypes_e.swDocPART, RibbonTitle);
                if (tab != null) _cmdMgr.RemoveCommandTab((CommandTab)tab);
                _cmdMgr.RemoveCommandGroup2(RibbonGroupId, true);
            }
            catch (Exception ex) { Trace.WriteLine("MeshFit ribbon removal: " + ex); }
        }

        /// <summary>0 = disabled, 1 = enabled, 3 = enabled and pressed (current tool / mode / type).</summary>
        private int RibbonEnable(Cmd c)
        {
            var s = Session;
            switch (c)
            {
                case Cmd.LoadMesh:
                case Cmd.UseBodies:
                case Cmd.OpenProject:
                case Cmd.Settings:
                case Cmd.ReverseEngineer:
                case Cmd.ToggleZebra:
                    return 1;
            }
            if (!s.HasMesh) return 0;
            bool pressed;
            switch (c)
            {
                case Cmd.ToolSmartFill: pressed = s.Tool == PaintTool.SmartFill; break;
                case Cmd.ToolBrush: pressed = s.Tool == PaintTool.Brush; break;
                case Cmd.ToolFillet: pressed = s.Tool == PaintTool.Fillet; break;
                case Cmd.ToolFillShell: pressed = s.Tool == PaintTool.FillConnected; break;
                case Cmd.ToolPick: pressed = s.Tool == PaintTool.PickRegion; break;
                case Cmd.ToolOff: pressed = s.Tool == PaintTool.None; break;
                case Cmd.ToggleMode: pressed = s.RemoveMode; break;
                default:
                    var req = TypeOf(c);
                    if (req == null) return 1;
                    if (s.ActiveRegion == null) return 0;
                    pressed = s.ActiveRegion.Request == req;
                    break;
            }
            return pressed ? 3 : 1;
        }

        /// <summary>Surface type selected by a Type* command, else null.</summary>
        internal static FitRequest? TypeOf(Cmd c)
        {
            switch (c)
            {
                case Cmd.TypeAuto: return FitRequest.Auto;
                case Cmd.TypePlane: return FitRequest.Plane;
                case Cmd.TypeCylinder: return FitRequest.Cylinder;
                case Cmd.TypeCone: return FitRequest.Cone;
                case Cmd.TypeSphere: return FitRequest.Sphere;
                case Cmd.TypeTorus: return FitRequest.Torus;
                case Cmd.TypeFreeform: return FitRequest.Freeform;
                default: return null;
            }
        }

        // ---- ribbon callbacks (SOLIDWORKS calls them by name) ----
        public void OnMenuReverseEngineer() => Menu(Cmd.ReverseEngineer);
        public void OnMenuLoadMesh() => Menu(Cmd.LoadMesh);
        public void OnMenuUseBodies() => Menu(Cmd.UseBodies);
        public void OnMenuToggleMode() => Menu(Cmd.ToggleMode);
        public void OnMenuTypeAuto() => Menu(Cmd.TypeAuto);
        public void OnMenuTypePlane() => Menu(Cmd.TypePlane);
        public void OnMenuTypeCylinder() => Menu(Cmd.TypeCylinder);
        public void OnMenuTypeCone() => Menu(Cmd.TypeCone);
        public void OnMenuTypeSphere() => Menu(Cmd.TypeSphere);
        public void OnMenuTypeTorus() => Menu(Cmd.TypeTorus);
        public void OnMenuTypeFreeform() => Menu(Cmd.TypeFreeform);
        public void OnMenuFit() => Menu(Cmd.Fit);
        public void OnMenuFitAll() => Menu(Cmd.FitAll);
        public void OnMenuToggleZebra() => Menu(Cmd.ToggleZebra);
        public void OnMenuExportIges() => Menu(Cmd.ExportIges);
        public void OnMenuSaveProject() => Menu(Cmd.SaveProject);
        public void OnMenuOpenProject() => Menu(Cmd.OpenProject);
        public void OnMenuSettings() => Menu(Cmd.Settings);
        public int OnRibbonEnableReverseEngineer() => RibbonEnable(Cmd.ReverseEngineer);
        public int OnRibbonEnableLoadMesh() => RibbonEnable(Cmd.LoadMesh);
        public int OnRibbonEnableUseBodies() => RibbonEnable(Cmd.UseBodies);
        public int OnRibbonEnablePositionMesh() => RibbonEnable(Cmd.PositionMesh);
        public int OnRibbonEnableToolSmartFill() => RibbonEnable(Cmd.ToolSmartFill);
        public int OnRibbonEnableToolBrush() => RibbonEnable(Cmd.ToolBrush);
        public int OnRibbonEnableToolFillet() => RibbonEnable(Cmd.ToolFillet);
        public int OnRibbonEnableToolFillShell() => RibbonEnable(Cmd.ToolFillShell);
        public int OnRibbonEnableToolPick() => RibbonEnable(Cmd.ToolPick);
        public int OnRibbonEnableToolOff() => RibbonEnable(Cmd.ToolOff);
        public int OnRibbonEnableToggleMode() => RibbonEnable(Cmd.ToggleMode);
        public int OnRibbonEnableNewRegion() => RibbonEnable(Cmd.NewRegion);
        public int OnRibbonEnableFillIslands() => RibbonEnable(Cmd.FillIslands);
        public int OnRibbonEnableCleanUp() => RibbonEnable(Cmd.CleanUp);
        public int OnRibbonEnableOptimize() => RibbonEnable(Cmd.Optimize);
        public int OnRibbonEnableTypeAuto() => RibbonEnable(Cmd.TypeAuto);
        public int OnRibbonEnableTypePlane() => RibbonEnable(Cmd.TypePlane);
        public int OnRibbonEnableTypeCylinder() => RibbonEnable(Cmd.TypeCylinder);
        public int OnRibbonEnableTypeCone() => RibbonEnable(Cmd.TypeCone);
        public int OnRibbonEnableTypeSphere() => RibbonEnable(Cmd.TypeSphere);
        public int OnRibbonEnableTypeTorus() => RibbonEnable(Cmd.TypeTorus);
        public int OnRibbonEnableTypeFreeform() => RibbonEnable(Cmd.TypeFreeform);
        public int OnRibbonEnableFitAndGrow() => RibbonEnable(Cmd.FitAndGrow);
        public int OnRibbonEnableFit() => RibbonEnable(Cmd.Fit);
        public int OnRibbonEnableFitAll() => RibbonEnable(Cmd.FitAll);
        public int OnRibbonEnableCreateSurface() => RibbonEnable(Cmd.CreateSurface);
        public int OnRibbonEnableCreateExtrude() => RibbonEnable(Cmd.CreateExtrude);
        public int OnRibbonEnableCreateRevolve() => RibbonEnable(Cmd.CreateRevolve);
        public int OnRibbonEnableNextFeature() => RibbonEnable(Cmd.NextFeature);
        public int OnRibbonEnableHideSurfaces() => RibbonEnable(Cmd.HideSurfaces);
        public int OnRibbonEnableShowSurfaces() => RibbonEnable(Cmd.ShowSurfaces);
        public int OnRibbonEnableAnalyzeFillet() => RibbonEnable(Cmd.AnalyzeFillet);
        public int OnRibbonEnableBoundaryToSketch() => RibbonEnable(Cmd.BoundaryToSketch);
        public int OnRibbonEnableAnalyzeCad() => RibbonEnable(Cmd.AnalyzeCad);
        public int OnRibbonEnableToggleZebra() => RibbonEnable(Cmd.ToggleZebra);
        public int OnRibbonEnableExportIges() => RibbonEnable(Cmd.ExportIges);
        public int OnRibbonEnableSaveProject() => RibbonEnable(Cmd.SaveProject);
        public int OnRibbonEnableOpenProject() => RibbonEnable(Cmd.OpenProject);
        public int OnRibbonEnableSettings() => RibbonEnable(Cmd.Settings);
        public int OnRibbonEnableUnloadMesh() => RibbonEnable(Cmd.UnloadMesh);
    }
}
