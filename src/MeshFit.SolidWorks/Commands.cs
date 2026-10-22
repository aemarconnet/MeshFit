namespace MeshFit.SolidWorks
{
    /// <summary>User commands shared by the task pane, the right-click menu and the context toolbar.</summary>
    internal enum Cmd
    {
        ToolBrush, ToolSmartFill, ToolFillShell, ToolFillet, ToolPick, ToolOff,
        GrowToFillets, TrimFillets, ToggleCurvatureMap,
        ModeAdd, ModeRemove, ToggleMode,
        Undo, Redo,
        NewRegion, FillIslands, GrowRing, ShrinkRing, ClearRegion, CleanUp, Optimize, ApplySuggestion,
        BrushBigger, BrushSmaller,
        Fit, FitAndGrow, CreateSurface, HideSurfaces, ShowSurfaces
    }
}
