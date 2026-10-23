# 03 · Architecture

## Projects

| Project | Target | Role |
|---|---|---|
| `src/MeshFit.Core` | netstandard2.0, no dependencies | All geometry: mesh, regions, fitting, analysis, alignment, export. Pure and testable on Linux. |
| `src/MeshFit.SolidWorks` | net48, x64, COM-visible types | The SOLIDWORKS add-in: UI, input, overlay drawing, SOLIDWORKS feature creation. |
| `src/MeshFit.Cli` | net8.0 | `meshfit fit <mesh> [options]` and `meshfit sample <out.stl>`: batch fitting and IGES output. |
| `tests/MeshFit.Core.Tests` | net8.0, xUnit | 57 tests on synthetic noisy meshes (`TestMeshes.Grid`, `TestMeshes.Box`). |
| `tools/ApiInspector` | net8.0 | Prints SOLIDWORKS interop members and enum values from the NuGet DLLs. Not in the .sln. |

## Units and conventions

- **Model units are metres** everywhere in code (SOLIDWORKS API units). `MeshIO.Load(path,
  scale)` multiplies file coordinates by `scale` (0.001 for mm). The UI shows mm (`× 1000`).
- **Tolerance:** `FitOptions.Tolerance` in metres, 0.0002 (0.2 mm) by default. A fit is "within
  tolerance" when `FitStatistics.P95 ≤ Tolerance`.
- **Triangle-based regions:** each triangle has one owner region id or `RegionSet.None` (-1).
- **Angles:** degrees in options; radians in SOLIDWORKS number boxes (`swNumberBox_Angle`).

## MeshFit.Core: file map

| Folder / file | Key types and methods |
|---|---|
| `Geometry/Vec3.cs` | `Vec3` (struct), `Frame` (origin + orthonormal axes; `FromAxis`, `ToLocal`, `ToWorld`), `BoundingBox` |
| `Geometry/LinearAlgebra.cs` | `SymmetricEigen3`, `SymmetricEigen` (Jacobi n×n), `Cholesky`, `SolveSymmetric`, `LeastSquares` |
| `Geometry/BandedCholesky.cs` | `BandedSymmetricMatrix` (B-spline normal equations) |
| `Geometry/Nurbs.cs` | `BSplineBasis` (spans, derivative basis, clamped knots), `NurbsSurface` (`Evaluate`, `Project`) |
| `Geometry/RigidTransform.cs` | `Apply`, `ApplyVector`, `After`, `Inverse`, `Rotation`, `RotationBetween`, `Parse`/`ToString` (12 numbers), `ToEulerXYZ` |
| `Mesh/TriMesh.cs` | vertices, indices, face normals, centroids, areas, vertex normals, `Neighbors[t*3+k]`, `Bounds`, `AverageEdgeLength`, `Bvh` (lazy and thread-safe; `BvhReady`), `VerticesOf`, `Transformed`, `Scaled` |
| `Mesh/Bvh.cs` | `RayCast`, `LineHits` (all hits along a line, sorted), `Nearest` |
| `Mesh/MeshIO.cs` | `Load` (STL ascii/binary, OBJ, PLY ascii/binary), `WriteBinaryStl`; welds STL vertices |
| `Mesh/Curvature.cs` | `Compute(mesh, scale, ct, progress)` → `KMax`/`KMin` per triangle; `DefaultScale` = 4 × edge; `MedianKMax` |
| `Selection/RegionSet.cs` | `Region` (Id, Name, Color, Request, Fit, TriangleCount, FeatureName, SurfaceHidden); `RegionSet`: `Owners`, `Assign`, `AddRegion`, `RemoveRegion`, `BeginStroke`/`EndStroke` (one undo step, one `Changed`), `Undo`/`Redo`, `UndoCount`, `TriangleListOf` |
| `Selection/PaintTools.cs` | `Brush`, `GrowSmooth`, `RemoveSmooth`, `RemoveConnected`, `FillConnected`, `GrowToSurface`, `CleanUp`, `Dilate`, `FillIslands`, `AutoSegment` (legacy 3-arg and region-group 5-arg), `Segment` (pure), `ApplySegmentation`, `MaskOf` |
| `Selection/MaskOps.cs` | bool-mask helpers: `GrowToSurface`, `Shrink`, `Components`, `Area`, `ToList` |
| `Selection/FeatureTools.cs` | curvature-based fillet tools: `IsFeature`, `SeedBaseline`, `GrowToFeatures`, `TrimFeatures`, `SelectFeatureBand` |
| `Selection/SelectionOptimizer.cs` | `Explore` (parallel grow/shrink/trim candidates), `Rank`; `SelectionCandidate` |
| `Fitting/Surfaces.cs` | `FittedSurface` → `AnalyticSurface` (`PlaneSurface`, `SphereSurface`, `CylinderSurface`, `ConeSurface`, `TorusSurface`) and `FreeformSurface`; `Distance`, `Project`, `ParamOf`/`PointAt`, `Describe`, `Transformed`; `AngleUtil` |
| `Fitting/AnalyticFitters.cs` | initial guesses + LM refinement per type; `FitCircle2D` (Kåsa), `Pca`, `Scale` |
| `Fitting/LevenbergMarquardt.cs` | `ILsqState` (`Step`, `Residual`), `Minimize`, `Evaluate` |
| `Fitting/FreeformFitter.cs` | `FreeformOptions`, `Fit` (B-spline least squares + smoothing + parameter correction), `BaseFrame` |
| `Fitting/SurfaceFitter.cs` | `FitRequest`, `FitOptions` (Tolerance, RejectOutliers, MaxPoints = 20000, FirstWithinTolerance, Freeform), `FitStatistics` (Rms, MaxAbs, Mean, P95, WithinTolerance), `FitResult` (Surface, Stats, Constraints, Alternatives), `Fit`, `Evaluate` |
| `Fitting/SurfaceExtents.cs` | parameter extents of the region on an analytic surface (+ margin) |
| `Fitting/Idealizer.cs` | `IdealizeOptions` (+`Clone`), `IdealizeResult`, `Idealize` (clean geometry), `DirectionOf` |
| `Analysis/SketchProfiles.cs` | `ProfileSegment`, `SketchProfile`/`ExtrusionProfile`/`RevolutionProfile`; `Extrusion`, `Revolution`, `OrderBand`, `LinesAndArcs`, `FitPolyline`, `StartTangent`/`EndTangent`/`JointAngle`, `DistanceToSegment` |
| `Analysis/MeshSection.cs` | `MeshSection.Slice` (plane cut → `SectionPolyline`s), `MeshSketch.Build` → `MeshSketchResult` |
| `Analysis/FeatureCurves.cs` | `RegionBoundaries`, `BoundaryChains`, `Taubin`, `Resample`, `SplitAtCorners` |
| `Analysis/FilletAnalysis.cs` | `Analyze(mesh, band)` → stations, spine, tangent lines, `RadiusAt`, `Report` |
| `Analysis/CadComparison.cs` | `Compute(scan, cadTess, maxDistance, tol)` → per-vertex signed deviation, `Summarize`, `Coverage` |
| `Alignment/MeshAlignment.cs` | `AlignFeature`, `AlignMate`, `Solve` (3-2-1 mates), `CentroidToOrigin`, `BoxCenterToOrigin`, `PrincipalAxes`, `SymmetryPlane`, `AreaCentroid`, `SecondMoment` |
| `Export/NurbsConversion.cs` | `ToNurbs(surface, extents)`: exact rational NURBS for analytic surfaces |
| `Export/IgesWriter.cs` | IGES 5.3 (entity 128) writer |
| `Project/ProjectFile.cs` | `.meshfit` text project: mesh path, scale, transform, regions, RLE ownership; `Save`, `ReadHeader`, `Load` |
| `Work.cs` | `Work.Options(ct)`: parallel options that leave one core free for SOLIDWORKS |

## MeshFit.SolidWorks: file map

| File | Role |
|---|---|
| `SwAddin.cs` | `ISwAddin` entry (`ConnectToSW`, `DisconnectFromSW`), COM registration, view hooking (`AttachToActiveView`), curvature job, mesh install/unload, project IO, `CreateSurfaces`, hide/show, IGES export, coalesced redraw |
| `SwAddin.Ribbon.cs` | CommandManager tab: `RibbonCommands` table, boxes, button text modes, icons, `RibbonEnable` (pressed states), generated `OnMenu*`/`OnRibbonEnable*` callbacks |
| `SwAddin.Menus.cs` | SOLIDWORKS right-click menu items and context-toolbar icons (`MenuItems`), `Menu(cmd)`, `Enable(cmd)` |
| `SwAddin.Page.cs` | page registry (`RegionGroups`, `Wizard(kind)`, `MeshSketch`), `ShowPage`, coalesced page refresh, `Post`, `MainWindow`, `StatusBar`, `ShowTaskPane` |
| `SwAddin.SketchFeatures.cs` | `CreateExtrudedSurface`, `CreateRevolvedSurface`, `CreatePlane` (standard plane or 3-point plane), `CreateProfileSketch`, `CoversRegion` |
| `SwAddin.MeshSketch.cs` | `TryGetPlane` (ref plane via corner points, planar face via `PlaneParams`), `SelectedObject(mark)`, `CreateMeshSketch` (lines/arcs/circles + relations) |
| `SwAddin.Analysis.cs` | 3D sketches of curves, fillet sketches, `AnalyzeCad` (accuracy analyzer), zebra, SOLIDWORKS curvature display |
| `SwAddin.Placement.cs` | `CaptureSelectedTarget` (selection → `AlignFeature`), `SwProgress` (status-bar progress) |
| `SwSurfaceBuilder.cs` | `IModeler` surfaces (planar, cylindrical, conical, spherical, toroidal, B-spline), sheet bodies, `CreateFeatureFromBody3` |
| `SwMeshSource.cs` | builds a `TriMesh` from SOLIDWORKS bodies (tessellation) |
| `Session.cs` | session state (mesh, regions, active region, tool, options, display flags) + painting logic (`BeginStroke`/`StrokeSample`/`EndStroke`), picking, region operations, `FitRegion`, `ComputeFit` (thread-safe), `FitAndGrow`, `NextStep` text |
| `Session.Workflow.cs` | wizard selection (`WizardRegions`, `WizardActive`, `PickHandler`), sketch preview curves, shape outline, `SelectInScreenPolygon` (Rectangle/Freehand) |
| `PageBase.cs` | base of all PropertyManager pages: show/build/refresh, control helpers, guarded handler interface, accept/pushpin logic |
| `RegionGroupsPage.cs` | Region Groups mode page |
| `WizardPage.cs` | Surface Primitives / Extrusion Wizard / Revolution Wizard / Mesh Fit page (`WizardKind`) |
| `MeshSketchPage.cs` | Mesh Sketch page |
| `TaskPaneControl.cs` | task pane UI (header, tabs, region list), `Execute(Cmd)` command router, `Run` job runner, progress strip, Esc watcher, right-click paint menu, settings window, most commands |
| `TaskPaneControl.Workflow.cs` | operations for the pages: `AutoSegmentRegions`, `MergeInto`, `WizardPreview`, `WizardCreate`, settings bridges |
| `ViewHook.cs` | per-view hook: `BufferSwapNotify` → overlay draw, input subclass (`InputWindow`) for painting / shapes / right-click / Delete, wheel brush resize |
| `OverlayRenderer.cs` | OpenGL overlay: mesh layer (display list), paint layer (reused buffers), iso-lines, sketch preview, shape outline, cursor |
| `ScreenProjector.cs` | pixel ↔ model mapping from `IModelView.Transform` / `Orientation3` (orthographic) |
| `Gl.cs` | opengl32 P/Invoke |
| `PositionDialog.cs` | Align Scan window (numeric / mates / auto) |
| `SuggestionEngine.cs` | debounced background `SelectionOptimizer.Explore` for the active region |
| `Guarded.cs` | exception containment + log (`Run`, `Report`, `Step`, `Write`) |
| `HoverTips.cs`, `EscKey.cs`, `CommandIcons.cs`, `ShapeIcons.cs`, `Commands.cs` | tooltips, Esc polling, drawn icons, the `Cmd` enum |

## Data flow

```
scan file ──MeshIO.Load──▶ TriMesh (worker) ──InstallMesh──▶ Session.SetMesh (UI)
                                         └─ Bvh built on the worker; Curvature.Compute in background
user input (view window) ─ViewHook─▶ Session.BeginStroke/StrokeSample/EndStroke ─▶ RegionSet (undo step)
Region Groups ─▶ PaintTools.Segment (worker, snapshot) ─▶ ApplySegmentation (UI) ─▶ FitRegions (worker) ─▶ ApplyFits (UI)
wizard ✔ ─▶ PageBase.OnClose → Accept() captures inputs → Post → TaskPaneControl.WizardCreate
          ─▶ SwAddin.CreateSurfaces / CreateExtrudedSurface / CreateRevolvedSurface (UI, SOLIDWORKS API)
Mesh Sketch ─▶ MeshSketch.Build (worker) ─▶ preview ─▶ ✔ SwAddin.CreateMeshSketch (UI)
Session.Changed ─▶ QueueRefreshAll (task pane), QueueRedraw (view), QueuePageRefresh (pages), overlay dirty, SuggestionEngine.Poke
```

## Threading model

- **UI thread** = the SOLIDWORKS main thread (STA). The task pane control, the pages, every SOLIDWORKS
  API call and every `RegionSet` change happen here.
- **Background jobs** go through `TaskPaneControl.Run(work, background: true, then, title, failed)`:
  - `work` runs on the thread pool and may call only pure Core code on **copied inputs**, plus
    `Report(stage, fraction)` for progress.
  - `then` and `failed` run on the UI thread.
  - `Token` is the cancellation token; Esc cancels.
- **Exceptions to that rule** (existing, keep them safe):
  - `Session.FitAndGrow` runs on a worker and modifies the region set. Overlay rebuilds guard their
    indices against it.
  - `SuggestionEngine` and the curvature job post results back through the synchronization context.
    Both are wrapped in `Guarded.Run`.
- **`Session.Changed` can be raised on a worker.** Its handlers all marshal through `BeginInvoke`
  or flags.

## Events and refresh

- `RegionSet.Changed` → `Session.RaiseChanged` → `Session.Changed`.
- `RegionSet` raises once per `EndStroke` (and once for `AddRegion` / `RemoveRegion` outside a
  stroke). Bulk operations must run inside `BeginStroke`/`EndStroke`.
- Handlers coalesce work:
  - task pane: `QueueRefreshAll`;
  - view: `QueueRedraw`;
  - pages: `QueuePageRefresh`;
  - overlay: a `_dirty` flag, rebuilt on the next draw.
