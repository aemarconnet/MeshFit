# 05 · The SOLIDWORKS add-in (MeshFit.SolidWorks)

## Registration and start-up

- **COM class:** `SwAddin` is `[ComVisible(true)]`, with `Guid("6F1C2A7E-3B4D-4E8A-9C21-5D7B0E9A4F13")`
  and `ProgId("MeshFit.SwAddin")`, and implements `ISwAddin`.
- **Registration:** `register.bat` (copied next to the DLL) unblocks the downloaded files and runs
  64-bit `RegAsm /codebase MeshFit.SolidWorks.dll`. The `[ComRegisterFunction]` writes
  `HKLM\SOFTWARE\SolidWorks\Addins\{guid}` and
  `HKCU\Software\SolidWorks\AddInsStartup\{guid}=1`. `unregister.bat` removes both.
- **Dependency loading:** a static `AssemblyResolve` handler loads `MeshFit.Core.dll` and the interop
  DLLs from the add-in folder. `CopyLocalLockFileAssemblies=true` copies them there.
- **`ConnectToSW` order:**
  1. Log line.
  2. AppDomain unhandled-exception logger.
  3. `SetAddinCallbackInfo2(0, this, cookie)`, so menu and ribbon callbacks reach this object by name.
  4. `OverlayRenderer`, `Session.Changed += QueueRedraw`, `SuggestionEngine`.
  5. Task pane: `CreateTaskpaneView2(icon, "MeshFit")`, then a `TaskPaneControl` hosted with
     `DisplayWindowFromHandlex64`.
  6. `ActiveModelDocChangeNotify` (re-hooks the view).
  7. `AddContextMenus()`, then `AddRibbon(cookie)`.
- **`DisconnectFromSW`** removes the menus and the ribbon, disposes the hooks and the pane, and
  releases COM.

## The ribbon (`SwAddin.Ribbon.cs`)

- One CommandManager command group: id `RibbonGroupId = 7301`, title "MeshFit". It is created with
  `CreateCommandGroup2(..., ignorePreviousVersion: true, ...)`.
- One tab, "MeshFit", for part documents. Its boxes are built from the `RibbonCommands` table
  (command, text, tooltip). Each box starts at the first command of its group: Scan | Align |
  Regions | Sketch | Model | Analyze | Output.
- **Button display modes**, to keep the tab narrow:
  - `LargeButtons` → `swCommandTabButton_TextBelow`;
  - `NamedButtons` → `TextHorizontal`, stacked three high;
  - everything else → `NoText` (icon and tooltip only).
- **Icons:** strips at 20/32/40/64/96/128 px, drawn in code (`CommandIcons.Render`), saved as PNG
  in `%TEMP%\MeshFitIcons`.
- **Callbacks** are found by name on the add-in object: `OnMenu<Cmd>` (action) and
  `OnRibbonEnable<Cmd>` (state).
  - The ribbon's `OnMenu*` / `OnRibbonEnable*` methods are generated at the bottom of
    `SwAddin.Ribbon.cs`.
  - The `OnMenu*` callbacks for the right-click menu items are in `SwAddin.Menus.cs`.
  - **Every command in the table needs both methods.** Check with the script in document 08.
- **Enable return values:** 0 = disabled, 1 = enabled, 3 = enabled + pressed (active tool, Remove
  mode, Curvature Map). Context menus also use 4 = hidden.
- **When the command list changes**, SOLIDWORKS may keep the old layout. Ask the user to reset the
  CommandManager (right-click the tabs).

## Right-click menus (`SwAddin.Menus.cs`)

- `MenuItems` are added with `AddMenuPopupItem3` for selection types NOTHING, FACES, SOLIDBODIES,
  SURFACEBODIES, MESHSOLIDBODIES and GRAPHICSBODY. Items marked `icon` also get a context-toolbar
  icon (`AddMenuPopupIcon3`). Callbacks: `OnMenu<Cmd>` / `OnMenuEnable<Cmd>`.
- **While a selection mode is active**, SOLIDWORKS's menu is not used at all. `ViewHook` swallows
  the right button and opens **MeshFit's own WinForms menu** (`TaskPaneControl.ShowPaintMenu`):
  modes, brush Ø and edge-angle number boxes, Clear selection, Regions, Model, Quick create.

## PropertyManager pages

All pages derive from **`PageBase`**, which implements `IPropertyManagerPage2Handler9`
(37 members, all guarded):

- **Lifecycle:** `Show()` →
  1. `CreatePropertyManagerPage(title, options, this, ref err)`;
  2. `Build()` (controls) with `Building=true`, so notifications are ignored;
  3. `Opened()`, `Refresh()` → `RefreshControls()`;
  4. `Show2(0)`.
- **Options:** OK, Cancel, Undo, Redo, locked page, **pushpin**.
- **Accept:** `OnClose(Okay)` calls `Accept()` synchronously to **capture inputs** and returns an
  action. That action is `Post`ed, so it runs after SOLIDWORKS finishes closing the page.
  - If the page was **pinned** (`Page.Pinned`): `AcceptedWhilePinned()` when SOLIDWORKS kept it
    open, otherwise `Show()` again.
  - This is Design X's "Lock" (OK keeps the command).
- **`Closed(ok)`** runs from `AfterClose`. `CancelledByUser` is true only for the ✖ button (close
  reason 2), not for Esc (5) or another page opening.
- **Helpers:**
  - `Group`, `Button`, `Check`, `Option`, `Label`, `Number` (label + number box with
    `SetRange2(unit, min, max, true, inc, 10·inc, inc/10)`), `List`, `SetItems` (replaces items
    only on change);
  - `SetChecked` / `SetValue` / `SetCaption` / `SetText` (write only on change);
  - `Message` (`SetMessage3`, only when the text changes).
- **Registry:** `SwAddin.Page.cs` keeps one instance per page (`RegionGroups`, `Wizard(kind)`,
  `MeshSketch`). It coalesces refreshes on `Session.Changed` (`QueuePageRefresh`). `ShowPage`
  logs, and shows a message box if a page can't open.

### Region Groups (`RegionGroupsPage`)
- **Groups:**
  - **Auto Segment:** crease angle; fillets up to radius (`Session.BlendRadius`, 30 mm default);
    smallest region (triangles); Classify; Auto Segment; Resegment all.
  - **Regions:** list (`RegionLine`: ✔/○/⚠/· + name + type/size + max dev), summary, Name, Merge
    with + Merge, Insert region, Clear selection, Delete, Extend to similar, Enlarge, Shrink, Fill
    islands, Clean up.
  - **Selection Mode:** Flood Fill, Paint Brush, Rectangle, Freehand, Fillet Band, Whole Shell, Pick
    Region; Remove; brush diameter; Flood Fill edge angle.
  - **Display** (collapsed).
- **`Opened`:**
  - records `RegionSet.UndoCount` and sets Flood Fill;
  - if the scan has no painted regions and the curvature is ready, it auto-runs Auto Segment
    (posted).
- **`Closed`:** ✖ undoes back to the recorded count (Redo restores); the tool is set to None.

### Modeling wizards (`WizardPage`, `WizardKind` = Primitives / Extrusion / Revolution / MeshFit)
- **Groups:**
  - **Regions:** the list of `Session.WizardRegions`, plus Add active region / Remove from list /
    Clear.
  - **Shape** (Primitives only): Automatic, Plane, Cylinder, Cone, Sphere, Torus.
  - **Options:**
    - tolerance (all), Clean geometry (all except Mesh Fit);
    - extend % (Primitives, Mesh Fit);
    - snap to X/Y/Z (Extrusion, Revolution);
    - control points U/V and smoothing exponent (Mesh Fit).
  - **Result:** Preview, result label.
- **`Opened`:**
  - `WizardActive = true`; the active region is preselected;
  - tool → Pick Region; `Session.PickHandler` toggles regions in the selection;
  - the overlay highlights the selection amber and dims the rest.
- **Preview:** `TaskPaneControl.WizardPreview` refits on a worker (the deviation map turns on), or
  computes the profile (the preview curves are drawn).
- **✔:** `TaskPaneControl.WizardCreate`:
  - Primitives and Mesh Fit: `CreateSurfaces`;
  - Extrusion and Revolution: `CreateSketched(..., regions)`, whose union of triangles becomes one
    feature owned by the first region.

### Mesh Sketch (`MeshSketchPage`)
- **Groups:**
  - **Base Plane:** a selection box (mark 1, filters `swSelDATUMPLANES` + `swSelFACES`, single
    entity; faces only if planar); "Use the active region's plane"; offset distance.
  - **Auto Sketch:** tolerance; ignore sections shorter than; Add relations; H/V within (angle).
  - **Result.**
- **`Opened`** sets the tool to None, because SOLIDWORKS selection must work.
- **Any change** → `Recompute()`: `MeshSketch.Build` on a worker; the newest request wins
  (generation counter). Sections are drawn grey and the fitted curves amber.
- **✔** → `SwAddin.CreateMeshSketch(result, planeFeatureIfZeroOffset, relations, hvTol)`.

## Task pane (`TaskPaneControl`, about 1,700 lines)

- **Header:**
  - guide text;
  - **Region Groups…**, **Next ▶**, **Settings…**;
  - Undo/Redo, the active-region combo;
  - the progress strip (stage, %, elapsed, Cancel);
  - deviation legend, hover readout, status line.
- **Tabs:**
  - **Regions** (first): tool toggles, Add/Remove, Ø, angle, region list (ListView), selection
    tools;
  - **Mesh**: load, bodies, unload, position, project;
  - **Fit**: type buttons, tolerance, Fit + grow, report, suggestions;
  - **Fillets**: stop at fillets, blend radius, measure, curves;
  - **Output**: create, sketch features, hide/show, accuracy analyzer, IGES.
- **Settings window:** selection behaviour, island limit, snap angle, extend %, freeform control
  points and smoothing, curvature scale, clean-geometry toggles.
- **`Execute(Cmd)`** is the single command router for the ribbon, menus, pages and buttons. It
  marshals to the UI thread and runs `Guard` (busy check + try/catch).
- **`Run(work, background, then, title, failed)`** is the job runner. It provides the progress
  strip, `Token` (Esc / Cancel) and breadcrumbs.
- **Refresh:** `QueueRefreshAll()` coalesces; the list rebuilds only when its content signature
  changes.
- **Esc watcher:** an 80 ms timer polls `EscKey.IsDown()`. `CancelWork` cancels the job, then the
  suggestion, then the curvature, then the stroke, then the tool.

## View hook and input (`ViewHook`)

- **One hook per active `ModelView`**, attached by `AttachToActiveView` (which creates a part from
  the default template if none is open). It subscribes to `BufferSwapNotify` (draw),
  `DestroyNotify2` and `ViewChangeNotify`.
- **`InputWindow`** subclasses the view HWND (`GetViewHWndx64`). While a tool is active it:
  - takes the left button: paint strokes with mouse capture, or Rectangle/Freehand outlines;
  - takes Ctrl/Shift + wheel (brush size ±10% per notch);
  - takes the **right button**: MeshFit menu, and swallows `WM_CONTEXTMENU`;
  - takes **Delete**: clears the active region.

  The middle button and plain wheel still reach SOLIDWORKS (rotate, pan, zoom). If subclassing
  fails, the `Mouse` notifications are the fallback.
- **Picking:** `ScreenProjector` builds the pick line from `IModelView.Transform` (model → pixels)
  and `Orientation3` (+Z toward the viewer). Orthographic views only.
- **Painting:**
  - `Session.BeginStroke` → `StrokeSample` (per move; the brush radius is in metres) → `EndStroke`
    (one undo step, auto fill islands).
  - **Ctrl + click** paints a straight line from `LastPaintPoint` in screen space, blending the
    radius.
  - **Shift** inverts add/remove for the stroke.
- **Rectangle / Freehand:** the screen polygon goes to `Session.SelectInScreenPolygon`. It keeps
  front-facing triangles whose centroid lies inside the polygon and are not occluded (a `RayCast`
  toward the viewer), computed in parallel.

## Overlay (`OverlayRenderer`, OpenGL immediate / client arrays)

- `Draw` runs inside `BufferSwapNotify`, with `glPushAttrib` / `glPushClientAttrib`.
- **Layers:**
  1. **Mesh:** grey head-light shading, curvature colours, or the CAD deviation. Compiled into a
     **display list per GL context**, rebuilt when the mesh or display mode changes.
  2. **Paint:** region colours, the deviation map, wizard highlight. Reused buffers, drawn with
     polygon offset in front.
  3. **Iso-line previews** of fitted surfaces: cached per region; only the active and wizard regions
     when there are more than 40 fitted regions.
  4. Mesh Sketch sections and curves; the Rectangle/Freehand outline; the brush cursor and the
     Ctrl-line preview.
- `DrawArrays` clamps the vertex count to both arrays and draws in **batches of 1.5M vertices**.
- `Failed` switches the overlay off after a driver fault (see document 07).

## Creating SOLIDWORKS geometry

- **Fitted surfaces** (`SwSurfaceBuilder`):
  - analytic types → `IModeler.CreatePlanarSurface2`, `CreateCylindricalSurface2`,
    `CreateConicalSurface2`, `CreateSphericalSurface2`, `CreateToroidalSurface`;
  - then `CreateSheetFromSurface(surface, uvRange)` → `IPartDoc.CreateFeatureFromBody3(body, true,
    0)`, renamed after the region;
  - every surface is **checked by closest-point queries** against the fit, and alternative
    conventions are tried. The fallback is an exact rational B-spline;
  - B-splines → `CreateBsplineSurface(props, knotsU, knotsV, coords)`. The props are 8 integers
    packed in 4 doubles. The control-point order and weight convention are undocumented, so the
    variants are tried until one reproduces the surface.
- **Sketch features** (`SwAddin.SketchFeatures.cs`):
  - `CreatePlane(frame)` reuses Front/Top/Right when they coincide; otherwise it creates three
    points in a 3D sketch and calls `InsertRefPlane` (coincident ×3).
  - `CreateProfileSketch`: `InsertSketch` on the plane, `ModelToSketchTransform` for coordinates,
    `CreateLine` / `CreateArc` (direction from a mid-arc point), `AddToDB = true`.
  - Extrude: `FeatureExtruRefSurface3`. Revolve: a centreline with `FeatureRevolve2`. If
    `CoversRegion` fails, the feature is rebuilt in the other direction.
- **Mesh Sketch** (`SwAddin.MeshSketch.cs`):
  - `TryGetPlane`:
    - a reference plane uses `IRefPlane.CornerPoints` (4 `MathPoint`s);
    - a planar face uses `ISurface.PlaneParams` (normal + root) and `Face2.FaceInSurfaceSense`.
  - `CreateMeshSketch`:
    - lines and arcs; two half arcs with the same centre become `CreateCircleByRadius`;
    - relations via `SketchAddConstraints` after `Select4` of points or segments: `sgMERGEPOINTS`
      (consecutive end points), `sgTANGENT` (joints under 1.5° involving an arc), `sgHORIZONTAL2D`
      / `sgVERTICAL2D` (lines within the H/V tolerance in sketch coordinates).
- **3D sketches of curves:** `Insert3DSketch` with `CreateSpline2`.
- **Accuracy analyzer** (`AnalyzeCad`): tessellates the visible bodies (`GetTessTriangles`), runs
  `CadComparison`, then refines with `Face2.GetClosestPointOn`. Progress goes through `SwProgress`
  (`GetUserProgressBar`).

## SOLIDWORKS API notes (verified signatures and pitfalls)

Use `tools/ApiInspector` to check any member before use, and `tools/swhelp.py` for semantics.

| API | Notes |
|---|---|
| `ISldWorks.CreatePropertyManagerPage(title, options, handler, ref err)` | The handler must be a COM-visible object implementing `IPropertyManagerPage2Handler9`. |
| `IPropertyManagerPageGroup.AddControl2(id, type, caption, align, options, tip)` | IDs must be unique within the page. Cast the result to the control interface; null = not created. |
| `IPropertyManagerPageNumberbox.SetRange2(unit, min, max, inclusive, inc, fast, slow)` | SOLIDWORKS may fire `OnNumberboxChanged` (value 0) while ranging: ignore during `Building`. |
| `IPropertyManagerPageLabel.Height` | A property (`set_Height` direct call doesn't compile). Wrap it in try (cosmetic). |
| `IPropertyManagerPageListbox` | `Clear`, `AddItems(object)`, `CurrentSelection` (short), `Height`. |
| `IPropertyManagerPageSelectionbox` | `Mark`, `SingleEntityOnly`, `SetSelectionFilters(int[])`, `Height`. Read the selection with `ISelectionMgr.GetSelectedObject6(i, mark)`. |
| `IPropertyManagerPage2.Pinned` | Read in `OnClose` to emulate Lock. |
| `swPropertyManagerPageCloseReasons_e` | 0 Unknown, 1 Okay, 2 Cancel, 3 ParentClosed, 4 Closed, 5 UserEscape, 6 Apply, 7 Preview. |
| `IPropertyManagerPageBitmapButton.SetBitmapsByName3(files, masks)` | Was the last call before an early crash. Kept in try; the current pages don't use bitmap buttons. |
| `ICommandManager.CreateCommandGroup2(..., ignorePreviousVersion: true, ref err)` | Changing commands may still need a CommandManager reset by the user. |
| `IModelDoc2.SketchAddConstraints(string)` | Strings like `sgMERGEPOINTS`, `sgTANGENT`, `sgHORIZONTAL2D`, `sgVERTICAL2D` (SOLIDWORKS 2006+ names). Needs an active sketch and a selection. |
| `IRefPlane.CornerPoints` | An array of 4 `MathPoint` objects in model coordinates. |
| `ISurface.PlaneParams` | 6 doubles: normal xyz, root point xyz (metres). |
| `IFrame.GetHWndx64()`, `SetStatusBarText` | Owner for MeshFit windows; status-bar feedback. |
| `IModelView.GetViewHWndx64()`, `Transform`, `Orientation3`, `GraphicsRedraw(null)` | Input subclassing and projection. |
| `ISldWorks.GetUserProgressBar(out bar)` | SOLIDWORKS's status-bar progress (`SwProgress`). |
| `IModeler.CreateBsplineSurface(props, knotsU, knotsV, coords)` | Packed props; see the notes in `SwSurfaceBuilder`. |

The interop assemblies come from NuGet packages `SolidWorks.Interop.sldworks/swconst/swpublished`
32.1.0 (the 2024 API). To build against a local install, use
`-p:SolidWorksInteropDir="C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"`.
