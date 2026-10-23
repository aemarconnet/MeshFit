using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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
            (Cmd.Optimize, "Optimize Selection", "Try growing / shrinking / trimming the active region for the best fit"),
            (Cmd.AnalyzeFillet, "Measure Fillet", "Measure the active region as a constant or variable fillet"),
            (Cmd.BoundaryToSketch, "Boundary Curves", "3D sketch of the active region's boundary (trim lines, tangent lines)"),
            (Cmd.AnalyzeCad, "Accuracy", "Colour the scan by its distance to the bodies in the part"),
            (Cmd.ToggleZebra, "Zebra", "Toggle zebra stripes"),
            (Cmd.HideSurfaces, "Hide Surfaces", "Hide all surfaces created by MeshFit"),
            (Cmd.ShowSurfaces, "Show Surfaces", "Show all surfaces created by MeshFit"),
            (Cmd.ExportIges, "Export IGES", "Write the fitted surfaces to an IGES file"),
            (Cmd.SaveProject, "Save Project", "Save the painted regions and mesh placement"),
            (Cmd.OpenProject, "Open Project", "Reopen a saved MeshFit project"),
            (Cmd.Settings, "Settings", "Tolerances, clean-geometry rules, fillet detection and other options"),
            (Cmd.UnloadMesh, "Unload Mesh", "Remove the scan from MeshFit"),
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
                // Main workflow box, then analysis, then files.
                AddBox(tab, grp, index, new[] { 0, 1, 2, 3 });
                AddBox(tab, grp, index, new[] { 4, 5, 6, 7, 8 });
                AddBox(tab, grp, index, new[] { 9, 10, 11, 12, 13, 14, 15 });
            }
        }

        private static void AddBox(CommandTab tab, CommandGroup grp, int[] index, int[] which)
        {
            var box = tab.AddCommandTabBox();
            var ids = which.Select(i => grp.get_CommandID(index[i])).ToArray();
            var text = which.Select(_ => (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow).ToArray();
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

        private int RibbonEnable(Cmd c)
        {
            switch (c)
            {
                case Cmd.LoadMesh:
                case Cmd.UseBodies:
                case Cmd.OpenProject:
                case Cmd.Settings:
                case Cmd.ReverseEngineer:
                case Cmd.ToggleZebra:
                    return 1;
                default:
                    return Session.HasMesh ? 1 : 0;
            }
        }

        // ---- ribbon callbacks (SOLIDWORKS calls them by name) ----
        public void OnMenuReverseEngineer() => Menu(Cmd.ReverseEngineer);
        public void OnMenuLoadMesh() => Menu(Cmd.LoadMesh);
        public void OnMenuUseBodies() => Menu(Cmd.UseBodies);
        public void OnMenuToggleZebra() => Menu(Cmd.ToggleZebra);
        public void OnMenuExportIges() => Menu(Cmd.ExportIges);
        public void OnMenuSaveProject() => Menu(Cmd.SaveProject);
        public void OnMenuOpenProject() => Menu(Cmd.OpenProject);
        public void OnMenuSettings() => Menu(Cmd.Settings);

        public int OnRibbonEnableReverseEngineer() => RibbonEnable(Cmd.ReverseEngineer);
        public int OnRibbonEnableLoadMesh() => RibbonEnable(Cmd.LoadMesh);
        public int OnRibbonEnableUseBodies() => RibbonEnable(Cmd.UseBodies);
        public int OnRibbonEnablePositionMesh() => RibbonEnable(Cmd.PositionMesh);
        public int OnRibbonEnableOptimize() => RibbonEnable(Cmd.Optimize);
        public int OnRibbonEnableAnalyzeFillet() => RibbonEnable(Cmd.AnalyzeFillet);
        public int OnRibbonEnableBoundaryToSketch() => RibbonEnable(Cmd.BoundaryToSketch);
        public int OnRibbonEnableAnalyzeCad() => RibbonEnable(Cmd.AnalyzeCad);
        public int OnRibbonEnableToggleZebra() => RibbonEnable(Cmd.ToggleZebra);
        public int OnRibbonEnableHideSurfaces() => RibbonEnable(Cmd.HideSurfaces);
        public int OnRibbonEnableShowSurfaces() => RibbonEnable(Cmd.ShowSurfaces);
        public int OnRibbonEnableExportIges() => RibbonEnable(Cmd.ExportIges);
        public int OnRibbonEnableSaveProject() => RibbonEnable(Cmd.SaveProject);
        public int OnRibbonEnableOpenProject() => RibbonEnable(Cmd.OpenProject);
        public int OnRibbonEnableSettings() => RibbonEnable(Cmd.Settings);
        public int OnRibbonEnableUnloadMesh() => RibbonEnable(Cmd.UnloadMesh);
    }
}
