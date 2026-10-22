using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// MeshFit entries in the SOLIDWORKS right-click menu and its context toolbar. SOLIDWORKS calls the
    /// public callback methods below by name (see SetAddinCallbackInfo2); the enable methods return
    /// 0 disabled, 1 enabled, 3 enabled + checked, 4 hidden.
    /// </summary>
    public sealed partial class SwAddin
    {
        private static readonly swSelectType_e[] MenuSelectTypes =
        {
            swSelectType_e.swSelNOTHING, swSelectType_e.swSelFACES, swSelectType_e.swSelSOLIDBODIES,
            swSelectType_e.swSelSURFACEBODIES, swSelectType_e.swSelMESHSOLIDBODIES, swSelectType_e.swSelGRAPHICSBODY
        };

        // (menu text, command, hint, show on context toolbar)
        private static readonly (string text, Cmd cmd, string hint, bool icon)[] MenuItems =
        {
            ("Smart fill", Cmd.ToolSmartFill, "Click or drag to fill smooth areas", true),
            ("Brush", Cmd.ToolBrush, "Drag to paint with the brush", true),
            ("Fill shell", Cmd.ToolFillShell, "Click to fill a whole connected piece", false),
            ("Pick region", Cmd.ToolPick, "Click a painted area to make its region active", false),
            ("\"Stop painting@\"", Cmd.ToolOff, "Leave paint mode", true),
            ("Add (paint)", Cmd.ModeAdd, "Tools add triangles to the active region", false),
            ("\"Remove (unselect)@\"", Cmd.ModeRemove, "Tools remove triangles from the region", true),
            ("Undo", Cmd.Undo, "Undo the last MeshFit paint step", true),
            ("\"Redo@\"", Cmd.Redo, "Redo", false),
            ("New region", Cmd.NewRegion, "Start painting the next surface", true),
            ("Fill islands", Cmd.FillIslands, "Fill unpainted islands enclosed by the active region", true),
            ("Grow ring", Cmd.GrowRing, "Add one ring of triangles around the active region", false),
            ("Shrink ring", Cmd.ShrinkRing, "Remove the outer ring of triangles", false),
            ("\"Clear region@\"", Cmd.ClearRegion, "Unselect every triangle of the active region", false),
            ("Fit + grow", Cmd.FitAndGrow, "Fit the active region and grow it over its surface", true),
            ("\"Create surface@\"", Cmd.CreateSurface, "Create the SOLIDWORKS surface for the active region", false),
            ("Hide MeshFit surfaces", Cmd.HideSurfaces, "Hide all surfaces created by MeshFit", false),
            ("Show MeshFit surfaces", Cmd.ShowSurfaces, "Show all surfaces created by MeshFit", false),
        };

        private readonly List<(int selType, string name, string cb, string en, string hint)> _addedItems =
            new List<(int, string, string, string, string)>();
        private readonly List<int> _iconSelTypes = new List<int>();

        private void AddContextMenus()
        {
            var frame = _app.Frame() as Frame;
            string iconDir = Path.Combine(Path.GetTempPath(), "MeshFitIcons");
            Directory.CreateDirectory(iconDir);
            foreach (var sel in MenuSelectTypes)
            {
                int st = (int)sel;
                foreach (var (text, cmd, hint, icon) in MenuItems)
                {
                    string name = text.StartsWith("\"") ? "\"MeshFit@" + text.Substring(1) : "MeshFit@" + text;
                    string cb = "OnMenu" + cmd, en = "OnMenuEnable" + cmd;
                    if (_app.AddMenuPopupItem3((int)swDocumentTypes_e.swDocPART, _cookie, st, name, cb, en, "," + hint, "") >= 0)
                        _addedItems.Add((st, name, cb, en, "," + hint));
                    if (icon && frame != null)
                    {
                        string img = WriteMenuIcon(iconDir, cmd);
                        frame.AddMenuPopupIcon3((int)swDocumentTypes_e.swDocPART, st, "MeshFit: " + text.Trim('"', '@'),
                            _cookie, cb, en, "", new[] { img });
                    }
                }
                _iconSelTypes.Add(st);
            }
        }

        private void RemoveContextMenus()
        {
            try
            {
                foreach (var (st, name, cb, en, hint) in _addedItems)
                    _app.RemoveMenuPopupItem2((int)swDocumentTypes_e.swDocPART, _cookie, st, name, cb, en, hint, "");
                var frame = _app.Frame() as Frame;
                if (frame != null)
                    foreach (int st in _iconSelTypes) frame.RemoveMenuPopupIcon((int)swDocumentTypes_e.swDocPART, st, _cookie);
            }
            catch (Exception ex) { Trace.WriteLine("MeshFit menu removal: " + ex); }
            _addedItems.Clear();
            _iconSelTypes.Clear();
        }

        private void Menu(Cmd c)
        {
            try { _control?.Execute(c); }
            catch (Exception ex) { Session.Say("Error: " + ex.Message); }
        }

        private int Enable(Cmd c)
        {
            if (!Session.HasMesh) return 4;
            switch (c)
            {
                case Cmd.ToolBrush: return Session.Tool == PaintTool.Brush ? 3 : 1;
                case Cmd.ToolSmartFill: return Session.Tool == PaintTool.SmartFill ? 3 : 1;
                case Cmd.ToolFillShell: return Session.Tool == PaintTool.FillConnected ? 3 : 1;
                case Cmd.ToolPick: return Session.Tool == PaintTool.PickRegion ? 3 : 1;
                case Cmd.ToolOff: return Session.Tool == PaintTool.None ? 4 : 1;
                case Cmd.ModeAdd: return Session.RemoveMode ? 1 : 3;
                case Cmd.ModeRemove: return Session.RemoveMode ? 3 : 1;
                case Cmd.Undo: return Session.Regions.CanUndo ? 1 : 0;
                case Cmd.Redo: return Session.Regions.CanRedo ? 1 : 0;
                case Cmd.CreateSurface:
                case Cmd.FitAndGrow:
                case Cmd.FillIslands:
                case Cmd.GrowRing:
                case Cmd.ShrinkRing:
                case Cmd.ClearRegion:
                    return Session.ActiveRegion?.TriangleCount > 0 ? 1 : 0;
                default: return 1;
            }
        }

        private static string WriteMenuIcon(string dir, Cmd cmd)
        {
            string path = Path.Combine(dir, cmd + ".bmp");
            using (var bmp = new Bitmap(20, 20))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.White);
                using (var pen = new Pen(Color.FromArgb(40, 90, 160), 2f))
                using (var fill = new SolidBrush(Color.FromArgb(70, 150, 230)))
                using (var red = new Pen(Color.FromArgb(200, 40, 40), 2.5f))
                {
                    switch (cmd)
                    {
                        case Cmd.ToolSmartFill: // paint bucket
                            g.FillPolygon(fill, new[] { new PointF(4, 9), new PointF(10, 3), new PointF(16, 9), new PointF(10, 15) });
                            g.DrawPolygon(pen, new[] { new PointF(4, 9), new PointF(10, 3), new PointF(16, 9), new PointF(10, 15) });
                            g.FillEllipse(fill, 14, 13, 4, 5);
                            break;
                        case Cmd.ToolBrush:
                            g.DrawLine(pen, 15, 3, 8, 11);
                            g.FillEllipse(fill, 3, 10, 7, 7);
                            break;
                        case Cmd.ToolOff:
                            g.DrawEllipse(pen, 3, 3, 14, 14);
                            g.DrawLine(red, 6, 6, 14, 14);
                            break;
                        case Cmd.ModeRemove:
                            g.DrawEllipse(pen, 2, 2, 16, 16);
                            g.DrawLine(red, 6, 10, 14, 10);
                            break;
                        case Cmd.Undo:
                            g.DrawArc(pen, 4, 5, 12, 10, 180, 270);
                            g.FillPolygon(fill, new[] { new PointF(1, 8), new PointF(7, 8), new PointF(4, 13) });
                            break;
                        case Cmd.NewRegion:
                            g.DrawRectangle(pen, 3, 3, 14, 14);
                            g.DrawLine(pen, 10, 6, 10, 14); g.DrawLine(pen, 6, 10, 14, 10);
                            break;
                        case Cmd.FillIslands:
                            g.FillRectangle(fill, 2, 2, 16, 16);
                            g.FillEllipse(Brushes.White, 7, 7, 6, 6);
                            g.DrawEllipse(red, 7, 7, 6, 6);
                            break;
                        case Cmd.FitAndGrow:
                            g.DrawArc(pen, 2, 6, 16, 16, 200, 140);
                            g.DrawLine(pen, 10, 2, 10, 9); g.DrawLine(pen, 7, 5, 10, 2); g.DrawLine(pen, 13, 5, 10, 2);
                            break;
                        default:
                            g.FillEllipse(fill, 4, 4, 12, 12);
                            break;
                    }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Bmp);
            }
            return path;
        }

        // ---- callbacks invoked by SOLIDWORKS by name (must stay public) ----
        public void OnMenuToolBrush() => Menu(Cmd.ToolBrush);
        public void OnMenuToolSmartFill() => Menu(Cmd.ToolSmartFill);
        public void OnMenuToolFillShell() => Menu(Cmd.ToolFillShell);
        public void OnMenuToolPick() => Menu(Cmd.ToolPick);
        public void OnMenuToolOff() => Menu(Cmd.ToolOff);
        public void OnMenuModeAdd() => Menu(Cmd.ModeAdd);
        public void OnMenuModeRemove() => Menu(Cmd.ToggleMode);
        public void OnMenuUndo() => Menu(Cmd.Undo);
        public void OnMenuRedo() => Menu(Cmd.Redo);
        public void OnMenuNewRegion() => Menu(Cmd.NewRegion);
        public void OnMenuFillIslands() => Menu(Cmd.FillIslands);
        public void OnMenuGrowRing() => Menu(Cmd.GrowRing);
        public void OnMenuShrinkRing() => Menu(Cmd.ShrinkRing);
        public void OnMenuClearRegion() => Menu(Cmd.ClearRegion);
        public void OnMenuFitAndGrow() => Menu(Cmd.FitAndGrow);
        public void OnMenuCreateSurface() => Menu(Cmd.CreateSurface);
        public void OnMenuHideSurfaces() => Menu(Cmd.HideSurfaces);
        public void OnMenuShowSurfaces() => Menu(Cmd.ShowSurfaces);

        public int OnMenuEnableToolBrush() => Enable(Cmd.ToolBrush);
        public int OnMenuEnableToolSmartFill() => Enable(Cmd.ToolSmartFill);
        public int OnMenuEnableToolFillShell() => Enable(Cmd.ToolFillShell);
        public int OnMenuEnableToolPick() => Enable(Cmd.ToolPick);
        public int OnMenuEnableToolOff() => Enable(Cmd.ToolOff);
        public int OnMenuEnableModeAdd() => Enable(Cmd.ModeAdd);
        public int OnMenuEnableModeRemove() => Enable(Cmd.ModeRemove);
        public int OnMenuEnableUndo() => Enable(Cmd.Undo);
        public int OnMenuEnableRedo() => Enable(Cmd.Redo);
        public int OnMenuEnableNewRegion() => Enable(Cmd.NewRegion);
        public int OnMenuEnableFillIslands() => Enable(Cmd.FillIslands);
        public int OnMenuEnableGrowRing() => Enable(Cmd.GrowRing);
        public int OnMenuEnableShrinkRing() => Enable(Cmd.ShrinkRing);
        public int OnMenuEnableClearRegion() => Enable(Cmd.ClearRegion);
        public int OnMenuEnableFitAndGrow() => Enable(Cmd.FitAndGrow);
        public int OnMenuEnableCreateSurface() => Enable(Cmd.CreateSurface);
        public int OnMenuEnableHideSurfaces() => Enable(Cmd.HideSurfaces);
        public int OnMenuEnableShowSurfaces() => Enable(Cmd.ShowSurfaces);
    }
}
