# 02 · History and decisions

Each round is the user's request, then what was built (commit), then the decisions that matter
later. The rounds are in chronological order. `git log --oneline` shows the same commits.

## Round 1: the initial add-in
**Asked:** "an add-on for SOLIDWORKS to paint facets of a mesh body and fit surfaces that are
planar, conical, freeform, etc… to reverse engineer the original surfaces of car body panels."

**Built** (`29edaae`, `1ab9cba`):
- `MeshFit.Core`:
  - STL/OBJ/PLY IO, `TriMesh` with adjacency, BVH, `RegionSet` with undo;
  - geometric Levenberg–Marquardt fitters for plane, sphere, cylinder, cone and torus;
  - B-spline freeform fitting, automatic type selection;
  - exact NURBS conversion, IGES writer, project files.
- The COM add-in: task pane, OpenGL overlay drawn in `BufferSwapNotify`, picking, surface creation
  through `IModeler`.
- The CLI, CI, and the README.

**Decisions:**
- The geometry lives in a dependency-free netstandard library so it can be tested on Linux.
- The add-in compiles against the NuGet interop (2024) with `EmbedInteropTypes=false`, and is
  registered with `RegAsm /codebase` through `register.bat`.
- The user first hit "No .NET SDKs were found". This is why the README now gives two routes: the
  CI artifact or the .NET 8 SDK.

## Round 2: usability
**Asked:**
- the UI wasn't intuitive;
- deviation map without units;
- hide surfaces after creation;
- fill islands;
- a clearer way to unselect;
- undo.

**Built** (`35c1053`):
- Add/Remove mode, undo/redo of paint strokes, Fill islands.
- Hide/show created surfaces, mm units on the legend, the right-click menu.

## Round 3: painting like Photoshop, fillets, background optimizer
**Asked:**
- click-and-drag with Smart fill;
- a right-click toolbar;
- icon buttons for the surface type;
- tooltips everywhere;
- Shift must not draw a selection box;
- Ctrl + click straight lines with brush-size blending;
- the brush defined by its diameter;
- Ctrl/Shift + wheel resizing;
- paint clean-up;
- a multithreaded background optimizer ("6 dots on a cylinder → whole face", CMM-like);
- grow/shrink aware of adjacent fillets and variable fillets.

**Built** (`858d897`):
- The view window is subclassed (`ViewHook.InputWindow`) to take left-button and modified-wheel
  input while painting, so SOLIDWORKS sees no selection box.
- Ctrl-line painting, brush diameter, `PaintTools.CleanUp`.
- Per-triangle curvature (`Curvature`), fillet detection (`FeatureTools`), and
  `SelectionOptimizer` + `SuggestionEngine`.

## Round 4: research and proposals
**Asked:** "research Geomagic and Altair", then picked items 1, 2 and 3.

**Built** (`fa03967`, `99153c2`):
1. Feature curves → 3D sketches (`FeatureCurves`, `SwAddin.CreateSketch`).
2. Fillet measurement (`FilletAnalysis`: rolling-ball stations, constant or variable).
3. Accuracy analyzer (`CadComparison` + `SwAddin.AnalyzeCad`).

## Round 5: Esc, layout, unload, positioning, progress
**Asked:**
- Esc to abort;
- no scrolling, tabs are fine;
- unload the mesh;
- a positioning wizard (numeric, mate-like, automatic);
- progress bars.

**Built** (`74ef820`):
- A tabbed task pane with a progress strip and Cancel.
- `EscKey` polling (`GetAsyncKeyState` plus a foreground-process check).
- `PositionDialog` with `MeshAlignment`: mates via Horn's quaternion method, principal axes and the
  symmetry plane.
- The `SwProgress` status-bar progress.

## Round 6: sketch-based features and the feature loop
**Asked:**
- break simple surfaces down into a sketch plus an extruded or revolved surface, "simple lines and
  arcs only";
- a better paint → pick → create → next feature workflow.

**Built** (`578ab4b`):
- `SketchProfiles.Extrusion/Revolution`: direction or axis detection (Pottmann line complex for
  axes), centre-line tracing (`OrderBand`), a DP fit with the fewest lines and arcs (`LinesAndArcs`),
  a refit with tangent joints (`Refine`), `MergeRedundant`, `ExtendEnds`.
- On the SOLIDWORKS side, `CreateExtrudedSurface` / `CreateRevolvedSurface` build a reference
  plane, a sketch and the feature. If the feature misses the scan, it is rebuilt flipped.
- The step bar ①②③④.

## Round 7: "UI is terrible" + clean geometry
**Asked:** the UI is terrible, and fitted surfaces should be closer to the ideal clean geometry.

The user chose:
- **Native SOLIDWORKS UI** (ribbon tab + PropertyManager with pushpin);
- all four clean-geometry options: snap directions, round dimensions, shared axes/tangency, fairer
  freeform.

**Built:**
- `afb1612`: the `Idealizer` (masked constrained LM refits) and fairer freeform (tolerance on
  P95/RMS, then the strongest smoothing that still meets it).
- `6008646`: the CommandManager tab, the Reverse Engineer PropertyManager (since replaced), and a
  Settings window.

## Round 8: speed, ribbon groups, region list in the PropertyManager
**Asked:**
- SOLIDWORKS unresponsive after opening a mesh;
- ribbon buttons for selection modes, fitting and building;
- an always-visible region list with type and fit quality.

**Built** (`03f5343`):
- The BVH is built while loading, thread-safe.
- The static mesh layer is drawn from an OpenGL display list.
- Parallel work leaves one core free (`Work.Options`).
- Ribbon groups Select/Fit/Build, and the region list in the PropertyManager.

## Round 9: field-test feedback
**Asked:**
- Reverse Engineer and Position Mesh seemed unwired;
- right-click menus didn't exist after a selection;
- dismissing a selection should be easier;
- tooltips appeared off-screen;
- the region list was hard to find;
- the brush and smart-fill properties should be easy to reach;
- the interface was too wide;
- a CI warning about Node 20.

**Built** (`24bafac`):
- Right-click while painting opens MeshFit's own menu. SOLIDWORKS's menu only knows SOLIDWORKS
  selections.
- Delete clears the selection.
- A narrower ribbon (few large buttons).
- MeshFit messages are mirrored to the SOLIDWORKS status bar.
- Windows are owned by the SOLIDWORKS main window, so they don't open behind it.
- `HoverTips` places each tooltip just below its control.
- CI actions moved to Node 24 versions.

## Round 10: crash when opening Reverse Engineer
**Asked:** SOLIDWORKS crashed as soon as the mesh loaded, via the Reverse Engineer button. The user
pasted a SOLIDWORKS crash report showing a managed exception escaping a window procedure.

**Built** (`29ffc78`):
- The crash-safety layer: `Guarded`, `WndProc` overrides, guarded handlers everywhere.
- The diagnostic log.
- PropertyManager notifications are ignored while a page is being built.

See document 07.

## Round 11: familiar workflow (benchmark against established tools)
**Asked:** "a UI comparison to established software and a more familiar workflow."

**Built** (`eab468d`, `2a39b9d`):
- The research behind `docs/workflow-comparison.html`, published to the user as an artifact page.
- The ribbon in workflow order: Scan | Align | Regions | Sketch | Model | Analyze | Output.
- The **Region Groups** mode: Auto Segment on entry, curvature-aware.
- The **Surface Primitives / Extrusion Wizard / Revolution Wizard / Mesh Fit** wizards (one
  PropertyManager each).
- **Mesh Sketch**: section + Auto Sketch → a SOLIDWORKS sketch with relations.
- Rectangle and Freehand selection.
- `PageBase` for all pages.

**Decision:**
- Two shortcuts deliberately differ from Design X because the user asked for them earlier:
  - Ctrl + click straight strokes and Ctrl/Shift + wheel brush size are kept;
  - Design X uses Shift add / Ctrl remove / Alt + drag size.

  Offer a Design X shortcut option if the user wants it.

## Round 12: crashes during region creation + Position Mesh error
**Reported:** the log showed:
- `NullReferenceException` in `PositionDialog.FillMateLists`;
- SOLIDWORKS closing, with no .NET error, while regions were being created (Region Groups on an
  11M scan and on a 642k scan).

**Built** (`584c5d1`):
- `RegionSet` notifies once per step: Auto Segment used to cause one synchronous refresh per region.
- Coalesced refresh and redraw.
- GDI hygiene: a shared bold font, cached swatches, disposed menu icons.
- Background Auto Segment (`PaintTools.Segment`) with linear absorption of small patches.
- Thread-safe classification (`Session.ComputeFit` on snapshots, `FirstWithinTolerance`).
- Limits on background suggestions.
- Batched overlay draws, and a corrupted-state exception catch in the draw callback.
- Breadcrumbs with memory and GDI/USER handle counts.
- The `PositionDialog` fix.

**Not yet confirmed by the user.** See document 09.

## Standing decisions

- **Region-first, wizard-per-feature UI**, modelled on Design X. The old single "Reverse
  Engineer" page is gone. `Cmd.ReverseEngineer` now opens Region Groups.
- **The task pane is a companion**: region list, reports, settings tabs. The ribbon and the
  PropertyManager pages are primary.
- **Every heavy computation is a pure Core function** on snapshots. The add-in only marshals
  inputs and outputs.
- **Tolerance semantics:** "within tolerance" means the 95th-percentile deviation ≤ tolerance (P95).
  The maximum is reported but not used for decisions (scan outliers).
