namespace MeshFit.SolidWorks
{
    /// <summary>User commands shared by the task pane, the right-click menu and the context toolbar.</summary>
    internal enum Cmd
    {
        ToolBrush, ToolSmartFill, ToolFillShell, ToolFillet, ToolPick, ToolOff,
        GrowToFillets, TrimFillets, ToggleCurvatureMap,
        UnloadMesh, PositionMesh, BoundaryToSketch, AnalyzeFillet, FilletToSketch, AnalyzeCad, ToggleZebra,
        ModeAdd, ModeRemove, ToggleMode,
        Undo, Redo,
        NewRegion, FillIslands, GrowRing, ShrinkRing, ClearRegion, CleanUp, Optimize, ApplySuggestion,
        BrushBigger, BrushSmaller,
        Fit, FitAndGrow, CreateSurface, HideSurfaces, ShowSurfaces,
        CreateExtrude, CreateRevolve, NextFeature, StepPaint,
        ReverseEngineer, LoadMesh, UseBodies, ExportIges, SaveProject, OpenProject, Settings,
        TypeAuto, TypePlane, TypeCylinder, TypeCone, TypeSphere, TypeTorus, TypeFreeform, FitAll
    }
}
