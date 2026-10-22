namespace MeshFit.SolidWorks
{
    /// <summary>User commands shared by the task pane, the right-click menu and the context toolbar.</summary>
    internal enum Cmd
    {
        ToolBrush, ToolSmartFill, ToolFillShell, ToolPick, ToolOff,
        ModeAdd, ModeRemove, ToggleMode,
        Undo, Redo,
        NewRegion, FillIslands, GrowRing, ShrinkRing, ClearRegion,
        Fit, FitAndGrow, CreateSurface, HideSurfaces, ShowSurfaces
    }
}
