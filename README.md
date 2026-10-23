# MeshFit: mesh-to-surface fitting for SOLIDWORKS

MeshFit is a SOLIDWORKS add-in for reverse engineering scanned parts such as car body panels.
You paint regions of a mesh (STL/OBJ/PLY scan, or a mesh body already in SOLIDWORKS) and fit a
surface to each region. Each fit becomes a real surface feature in the part:

| Type      | Created in SOLIDWORKS as |
|-----------|--------------------------|
| Plane     | analytic planar surface |
| Cylinder  | analytic cylindrical surface |
| Cone      | analytic conical surface |
| Sphere    | analytic spherical surface |
| Torus     | analytic toroidal surface |
| Freeform  | cubic B-spline surface (smoothed least squares, adaptive control net) |
| Auto      | the simplest of the above that meets your tolerance |

Analytic fits stay analytic, so the faces can be used for mates, axes, sketch references and dimensions.
The add-in verifies every surface it creates against the fit. If SOLIDWORKS builds an analytic surface
that doesn't match, MeshFit falls back to an exact rational B-spline.

## Features

- **Painting in the graphics area**
  - Smart fill grows a region until it reaches an edge sharper than the angle you set. Click, or
    drag across several patches.
  - Brush with an adjustable radius.
  - Fill shell fills a whole connected piece.
  - **+ Add / − Remove** mode switches every tool between painting and unselecting. Hold Shift for
    a temporary switch.
  - Fill islands fills unpainted holes enclosed by the region; it also runs automatically after
    each Smart fill.
  - Pick region makes the region under the cursor the active one.
  - Grow ring and Shrink ring add or remove one band of triangles at the region edge.
  - Undo and redo cover every paint step, plus region delete, merge and auto segment.
  - **Right-click while painting** opens the MeshFit paint menu. It has:
    - The paint tools and Remove mode.
    - Number boxes for the brush Ø and the smart-fill edge angle, and Stop at fillets.
    - **Clear selection**, Undo, and selection tools (fill islands, clean up, grow/shrink, fillets,
      optimize).
    - A **Regions** submenu: switch, new, rename, colour, delete, and open the region list.
    - Surface type, Fit + grow, Create (surface / extruded / revolved) and Next feature.
  - **Delete** in the view clears the active region's selection (undoable).
  - When no paint tool is active, SOLIDWORKS's own right-click menu also has a MeshFit submenu.
  - Auto segment splits the unpainted mesh into smooth patches.
- **Mouse handling while painting**
  - A paint tool takes the left mouse button directly, so SOLIDWORKS doesn't draw a selection box.
  - **Ctrl+click** paints a straight line, in the current view, from the last painted point. The
    brush size blends from the previous size to the current one. A dashed preview shows while Ctrl
    is held.
  - **Ctrl/Shift + mouse wheel** resizes the brush. The brush size is shown as a diameter.
- **Clean up** removes stray specks, fills enclosed holes and smooths the jagged brush edge.
- **Adjacent fillets and blends.** Detection is based on curvature, because the angle between
  neighbouring triangles stays small across a tangent blend.
  - *Stop at fillets* keeps Smart fill, Fit + grow and the optimizer from creeping into fillets.
  - *Grow to fillets* and *Trim fillets* grow or trim the region at the fillet line.
  - The *Fillet* tool selects a whole constant- or variable-radius blend band with one click.
  - A *Curvature map* overlay shows the feature lines.
- **Curves to SOLIDWORKS 3D sketches.** The active region's boundary becomes smooth splines, split
  at corners, ready for Boundary Surface, Trim and Fillet. For a fillet band, the boundaries are
  its tangent lines.
- **Fillet measurement.** Select a blend with the Fillet tool and choose *Analyze fillet*. MeshFit
  fits circles to cross-sections along the band, like a rolling ball, and reports whether the
  radius is constant or variable. It gives the radius at 0/25/50/75/100% of the length for a
  SOLIDWORKS variable-size fillet. *Fillet curves → 3D sketch* exports the spine (the ball-centre
  path) and both tangent lines.
- **Accuracy analyzer.** Compares every visible body in the part with the scan.
  - The scan is coloured by its distance to the CAD, with the same legend in mm. Grey means no
    body is close enough, i.e. that area isn't modelled yet.
  - You get coverage, RMS, maximum and the share within tolerance, both overall and per body.
  - Distances are refined to the exact faces, so they don't depend on the tessellation.
  - The zebra stripes and curvature buttons turn on SOLIDWORKS' own continuity checks.
- **Selection optimizer and background suggestions.** MeshFit tries growing the selection over
  each surface type, shrinking its edge, trimming outliers and other tolerances, in parallel. It
  then proposes the largest selection that still fits within tolerance. A few brush dots on a
  cylinder are enough: like a CMM fitting a cylinder from a handful of probe points, the suggestion
  grows to the whole face.
- **Fit + grow**: paint a small, clean patch, then fit it and grow the region to every connected
  triangle that lies on that surface. This finds the true edge of each surface on the panel, up to
  the tangent blend lines.
- **Fit report**: RMS, maximum and 95th-percentile deviation, the share of points within tolerance,
  and every candidate type the Auto mode tried.
- **Deviation map**: colours every fitted region by its distance to its fit, with a legend in mm
  and a live readout at the cursor. Blue → green → red spans −tol to +tol. Magenta means the point
  is out of tolerance.
- **Hide / show** the created SOLIDWORKS surfaces, all at once or per region.
- **Iso-line previews** of each fitted surface before you create it.
- **Outlier rejection**: overspray from painting and scan noise are left out of the final fit.
- **Project files** (`*.meshfit`) save your painted regions so you can continue later.
- **IGES export** of all fitted surfaces as exact NURBS, for use in any CAD system.
- **Command line tool** (`meshfit`) for batch segmentation and fitting outside SOLIDWORKS.

## Installation

Requirements: SOLIDWORKS 2018 or newer (64-bit) and .NET Framework 4.8.

1. Get the binaries. Use either of these:
   - Download the `MeshFit-SolidWorks-addin` artifact from the latest successful **build** workflow
     run on GitHub (Actions tab).
   - Build them yourself: `dotnet build src/MeshFit.SolidWorks -c Release`. You need the .NET 8 SDK.
     The output is in `src/MeshFit.SolidWorks/bin/Release/net48/`.
2. Copy that folder to a permanent location, for example `C:\Tools\MeshFit\`.
3. Right-click `register.bat` and choose **Run as administrator**. The script unblocks the
   downloaded files and registers the DLL with `RegAsm /codebase`.
4. Start SOLIDWORKS. The **MeshFit** tab appears in the task pane. If it doesn't, enable MeshFit
   under *Tools > Add-Ins*.

To uninstall, run `unregister.bat` as administrator.

By default the build compiles against the SOLIDWORKS 2024 API from NuGet. Because SOLIDWORKS COM
interfaces only ever grow, the same build also runs on older and newer versions. To compile against
your own installation instead, run:

```
dotnet build src/MeshFit.SolidWorks -c Release -p:SolidWorksInteropDir="C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"
```

## Workflow: the MeshFit tab, left to right

The layout follows the established scan-to-CAD tools (Geomagic Design X and Geomagic for SOLIDWORKS,
Mesh2Surface, QUICKSURFACE, ScanTo3D). The comparison is in
[docs/workflow-comparison.html](docs/workflow-comparison.html). The ribbon reads in the order you
work:

| Group | Commands |
|---|---|
| **Scan** | **Import Mesh**, Mesh Body, Unload Mesh |
| **Align** | **Align Scan** (numeric, mates or automatic) |
| **Regions** | **Region Groups**; selection modes Paint Brush, Flood Fill, Rectangle, Freehand, Fillet Band, Pick Region; Extend To Similar, Remove, Clear Selection, Region List |
| **Sketch** | **Mesh Sketch**, Boundary Curves |
| **Model** | **Surface Primitives**, Extrusion Wizard, Revolution Wizard, Mesh Fit |
| **Analyze** | **Accuracy Analyzer**, Measure Fillet, Curvature Map, Zebra |
| **Output** | Hide / Show Surfaces, Export IGES, Save / Open Project, Settings |

Bold commands are the large buttons. The paint tools and wizards are stacked with their names, and
the rest are icons with tooltips. The active selection mode, Remove and Curvature Map show as
pressed.

### 1. Region Groups (regions first)

**Region Groups** is a mode, as in Design X. When you enter it on a scan that has no regions yet,
**Auto Segment** runs and splits the scan:
- at creases sharper than the *crease angle*;
- at fillets tighter than *fillets up to radius*, using the curvature analysis.

A face and the fillets running into it become separate regions. Pieces smaller than *smallest
region* join their neighbour. *Classify* fits every new region so the list shows its type.

- **Regions list.** It is always visible. Each line shows the status (✔ feature created, ○ fitted
  within tolerance, ⚠ outside tolerance, · not fitted), the name, the type and size (e.g.
  `Cylinder R25`) and the largest deviation. Click a line to make that region active.
- **Edit regions.** Rename, Merge with, Insert region, Clear selection, Delete, Extend to similar
  (fit and grow over the same surface), Enlarge, Shrink, Fill islands, Clean up.
- **Selection mode.** Flood Fill, Paint Brush, Rectangle, Freehand, Fillet Band, Whole Shell,
  Pick Region, plus Remove, the brush diameter and the Flood Fill edge angle.
  - Rectangle and Freehand select only triangles that face you and are not hidden.
- **Leaving the mode.** ✔ keeps the regions. ✖ undoes everything done in the mode, and Redo
  brings it back. Esc, or opening another command, keeps the regions.

### 2. Modeling wizards

**Surface Primitives**, **Extrusion Wizard**, **Revolution Wizard** and **Mesh Fit** each open
their own PropertyManager:

- **Regions:**
  - The active region is preselected.
  - Click regions on the scan to add or remove them. They light up amber and the rest dim.
- **Shape:** Surface Primitives only. Automatic (the simplest type within tolerance), or Plane,
  Cylinder, Cone, Sphere or Torus.
- **Options:** tolerance and Clean geometry. Also:
  - Extension past the region (Surface Primitives, Mesh Fit).
  - Snap to X/Y/Z (Extrusion Wizard, Revolution Wizard).
  - Control points and smoothing (Mesh Fit).
- **Result:**
  - Surface Primitives and Mesh Fit: **Preview** fits the regions and turns on the deviation map.
  - Extrusion and Revolution Wizards: Preview draws the profile on the scan, with its direction or
    axis.
- **✔** creates the features:
  - Surface Primitives and Mesh Fit: a surface for each region.
  - Extrusion and Revolution Wizards: one feature, an editable lines-and-arcs sketch with an
    extruded or revolved surface.
- **Pushpin.** It works as Design X's *Lock*: after ✔ the wizard stays open with an empty selection
  for the next feature.

### 3. Mesh Sketch

**Mesh Sketch** sections the scan and turns the section into an ordinary SOLIDWORKS sketch.

1. Select a base plane: a reference plane (Front / Top / Right from the flyout tree), a planar face,
   or *the active region's plane*.
2. Set an **offset distance**.
3. Check the preview: the section is drawn in grey and the fitted lines and arcs in amber. Auto
   Sketch fits the fewest lines and arcs within the tolerance, and closed round sections become
   circles.
4. ✔ writes the sketch with merged end points, tangent arcs and horizontal/vertical lines. Its
   plane is created if needed.

Then use SOLIDWORKS's own Extrude, Revolve or Cut.

### Shortcuts

- **Right click while a selection mode is active** opens the MeshFit menu. It has:
  - The selection modes and brush settings.
  - Clear selection and Undo.
  - The Regions submenu.
  - The Model submenu (the wizards and Mesh Sketch).
  - Quick create for the active region.
- **Delete** clears the active region's selection.
- **Ctrl + click** paints a straight stroke.
- **Ctrl/Shift + wheel** sizes the brush.
- **Shift** removes while held.
- **Esc** cancels a running job or leaves the selection mode.

The task pane is a companion. It has the region list, the progress strip, the deviation legend and
readout, and the reports. Its **Region Groups…** button opens the mode.

### Clean geometry

With *Clean geometry* on, every fit is refined toward the design intent the scan still supports. A
constraint is kept only if the 95th-percentile deviation stays within tolerance. The refinements,
in order:

- **Snap directions.** Axes and normals within 1° of X/Y/Z, or of an already fitted feature, become
  exactly parallel or perpendicular.
- **Shared axes and tangency.**
  - Cylinders, cones and tori become coaxial with fitted neighbours.
  - Spheres become concentric with fitted neighbours.
  - A fillet cylinder is made tangent to the two planes it blends.
- **Round dimensions.** Radii, cone angles, plane offsets and axis positions are rounded to the
  coarsest step the data agrees with: 1, 0.5, 0.1, 0.05 or 0.01 mm, and 1°, 0.5°, 0.1° or 0.05°.

The fit report lists what was applied, e.g. `axis ∥ Z; R → 25 mm`. Freeform surfaces use the
fairest smoothing and fewest control points that still meet the tolerance.

You can toggle each refinement and set the snap angle in **Settings…** (ribbon or task pane).

### Sketch-based surfaces (Extrude / Revolve)

For regions that are extrusions or revolutions, e.g. a flange with a rolled hem, a feature line, or
a wheel-arch lip:

- **Extrude:** the direction is found from the surface normals, which are all perpendicular to an
  extrusion direction.
- **Revolve:** the axis comes from the fit, or from the normal lines, which all meet the axis
  (Pottmann's line-complex method, then refined).
- The region is collapsed onto a sketch plane, and its centre line is traced and broken into the
  **fewest lines and arcs within tolerance**. Each line and arc is then refitted to the scan points,
  with tangent joints made exactly tangent.
- MeshFit creates a reference plane, a 2D sketch made only of lines and arcs (plus a centreline for
  revolves), and a SOLIDWORKS **Extruded Surface** or **Revolved Surface**. Everything stays
  editable in the feature tree.
- Directions and axes within 1° of X/Y/Z snap onto the axis. Align the mesh first with
  *Position mesh…* to get clean sketch planes.
- The created feature is checked against the scan. If SOLIDWORKS extruded or revolved the wrong
  way, it is rebuilt in the other direction.
- You are warned when the region isn't really an extrusion or revolution: the normals vary, or the
  profile deviates by more than three times the tolerance.

## Task pane layout

- **Header:**
  - The next-step guide.
  - **Region Groups…**, **Next ▶** and **Settings…**.
  - **Undo / Redo**.
  - The **active region** dropdown.
  - A **progress strip** while anything runs: task, current step, % bar and elapsed seconds, with
    **Cancel (Esc)**. Long operations that block SOLIDWORKS also show progress in its status bar.
  - The deviation legend, the hover readout and the status line.
- **Tabs:**
  - **Regions:** paint tools, Add/Remove, brush Ø, region list and selection tools.
  - **Mesh:** load, use bodies, **Unload mesh**, **Position mesh…** and project files.
  - **Fit:** surface type, tolerance, Fit + grow, report and suggestions.
  - **Fillets:** fillet detection, measurement and 3D sketch curves.
  - **Output:** SOLIDWORKS surfaces, hide/show, accuracy analyzer, IGES.
- **Settings…** opens a small window with these settings:
  - Selection behaviour and auto-fill of islands.
  - Clean geometry.
  - Sketch snapping.
  - Surface extension.
  - Freeform control points and smoothing.
  - Curvature scale.
- **Esc** cancels the running job: Fit + grow, Fit all, Optimize, curvature analysis, the accuracy
  analyzer's exact pass, or surface creation. A cancelled Fit + grow leaves the region unchanged.
  If nothing is running, Esc leaves the paint tool.

### Positioning the mesh (Mesh tab → Position mesh…)

- **Numeric:** move X/Y/Z in mm and rotate about X/Y/Z in degrees, around the part origin or the
  mesh centroid.
- **Mates:** pair mesh features with part references, with *Flip* to reverse direction. Mate 1 has
  priority, then 2, then 3, like a 3-2-1 alignment. *Sel.* uses whatever is selected in SOLIDWORKS
  as the target.
  - Mesh features: fitted planes, cylinder/cone/torus axes, sphere centres, cone apexes, the mesh
    centroid and the detected symmetry plane.
  - Part references: Front, Top or Right plane, the X/Y/Z axes, the origin, or a selected plane,
    face, axis, edge or vertex.
- **Auto:** centroid or bounding box to the origin, principal axes to X/Y/Z (length, width,
  height), or **symmetry plane detection** onto the Right plane for full vehicle scans.
- Fitted surfaces move with the mesh, so nothing has to be refitted. *Undo last move* and *Reset to
  original* are available. The placement is saved in the project file.
- A mesh taken from a SOLIDWORKS body moves only inside MeshFit. The overlay is switched on so you
  can see where it is.

## Workflow for body panels

1. **Bring in the scan**. Use either of these:
   - **Load mesh file…** reads an STL/OBJ/PLY file. Choose its units. If no part is open,
     MeshFit creates one, and the mesh is drawn as an overlay. Recommended for large scans.
   - **Use bodies in part** uses a mesh body that is already in SOLIDWORKS. Import the STL with
     *File > Open*, choose *Import as: Solid body* or *Surface body* under Options, then
     select the body or one of its faces.
2. **Paint the first region**. The yellow banner at the top of the task pane always shows the next
   step. Pick **Smart fill**: it is selected automatically after loading. Click or drag across a
   smooth area of the panel; every patch you drag over is filled up to edges sharper than the
   angle (∠°). Use **Brush** for fine edits. Rotate and pan with the middle mouse button.
   - **Removing paint:** switch to **− Remove**, or hold **Shift** for a temporary switch. Every
     tool then unselects instead of painting. *Clear* unselects the whole region.
   - **Undo / Redo:** the buttons are at the top of the pane. Their tooltips name the step they
     revert. Undo also brings back deleted or merged regions.
   - **Fill islands** fills unpainted speckles that are completely enclosed by the region. After
     every Smart fill, islands up to 500 triangles are filled automatically; you can change that
     limit in the advanced settings.
   - **Grow ring / Shrink ring** add or remove one band of triangles at the region's edge.
   - **Right-click menu:** right-click in the graphics area while painting. The menu has the tools,
     brush Ø and edge angle, Clear selection, regions, surface type, Fit + grow, Create and Next.
3. **Pick the surface type** with the shape buttons (Auto, Plane, Sphere, Cylinder, Cone, Torus,
   Free), then set the tolerance in mm. Typical values are 0.05–0.2 mm for a good scan.
4. Click **Fit + grow**. MeshFit fits the region, grows it to every connected triangle within
   tolerance of that surface, and refits, repeating until the region stops growing. This counts as
   one undo step.
5. **Check the result.** Turn on **Deviation map** to colour every fitted region by its distance to
   the surface. The legend is in mm. The readout under it shows the deviation at the cursor.
6. Click **New region** and repeat for the next surface. Usually you do the large primary surfaces
   first (skins, flanges, roll-overs), then the feature lines.
7. Click **Create surface** or **Create all**. Each region becomes a surface feature named after it,
   extended past the painted area by *Extend %* so that adjacent surfaces overlap. If a region
   already has a surface, MeshFit asks whether to replace it.
   - **Hide surfaces / Show surfaces** toggles all MeshFit surfaces so you can keep painting on the
     mesh. To toggle a single region's surface, right-click it in the region list.
8. Finish in SOLIDWORKS:
   - Trim the overlapping surfaces against each other with *Trim Surface* (mutual trim).
   - Rebuild the small blends with *Fillet*, or with *Boundary Surface* for curvature continuity.
     Don't fit blends directly.
   - *Knit* the result and thicken it if needed.
9. Save the painting with **Save project…** so you can come back to it.

### Tips

- Fit each tangent-continuous primary surface on its own. Leave the narrow blend strips unpainted.
- A freeform region must be a single-valued "height field" over its average plane. If the report
  warns that a region *folds over*, split it into smaller regions.
- Freeform controls (under *Show advanced settings*):
  - *CP u / v* set the size of the control net. 0 means automatic: MeshFit keeps growing the net
    until the tolerance is met.
  - *smooth* sets the bending penalty. Raise it (for example to 1e-8) for a fairer, less wavy
    surface. Lower it to follow the scan more closely.
- If Auto picks a cone with a tiny half angle or a cylinder with a huge radius, force the type you
  know is correct. On car panels a plane is often intended where the scan shows slight crown.
- Exported IGES files are in millimetres. You can insert them with *Insert > Features > Imported*.

## Command line tool

```
dotnet run --project src/MeshFit.Cli -c Release -- sample panel.stl
dotnet run --project src/MeshFit.Cli -c Release -- fit panel.stl --segment 10 --min 100 --tol 0.1 --out panel.igs
dotnet run --project src/MeshFit.Cli -c Release -- fit scan.stl --project scan.meshfit --tol 0.1 --out scan.igs
```

- `--segment` auto-segments the mesh at that edge angle.
- `--project` uses regions painted in SOLIDWORKS instead.
- `--type` forces one surface type for every region.

Lengths are in the mesh units.

## Repository layout

```
src/MeshFit.Core         netstandard2.0, no dependencies
  Mesh/                  STL/OBJ/PLY readers, indexed mesh with adjacency, BVH (ray casts, nearest point)
  Selection/             regions, undo/redo, brush, smart fill, grow-to-surface,
                         auto segmentation (creases + curvature-based fillet splitting)
  Fitting/               orthogonal-distance LM fitters (plane, sphere, cylinder, cone, torus),
                         B-spline approximation with thin-plate smoothing + parameter correction,
                         automatic type selection, outlier rejection, statistics
  Analysis/              mesh sections + Auto Sketch (lines, arcs, circles), extrusion / revolution
                         profiles, fillet measurement, feature curves, CAD-vs-scan comparison
  Alignment/             mesh positioning (mates, principal axes, symmetry)
  Export/                exact NURBS conversion of analytic surfaces, IGES 5.3 writer (entity 128)
  Project/               .meshfit project files
src/MeshFit.SolidWorks   net48 COM add-in: MeshFit ribbon tab, PropertyManager pages (Region Groups,
                         Surface Primitives / Extrusion / Revolution / Mesh Fit wizards, Mesh Sketch),
                         task pane, OpenGL overlay, mouse picking, SOLIDWORKS feature creation
docs/                    workflow-comparison.html: UI comparison with the established tools
docs/handoff/            handoff for developers / AI agents: goals, history, architecture, algorithms,
                         SOLIDWORKS integration, crash safety, process, status and backlog
tools/                   ApiInspector (SOLIDWORKS interop signatures offline), swhelp.py (API help text),
                         check_callbacks.py (ribbon / menu callback names)
AGENTS.md, CLAUDE.md     entry point and hard rules for AI agents working on the code
src/MeshFit.Cli          net8.0 batch tool
tests/MeshFit.Core.Tests xUnit tests: fitters on synthetic noisy data, NURBS exactness, IO, painting, IGES
```

Run the tests with `dotnet test`.

## Troubleshooting

MeshFit writes a log to `%LOCALAPPDATA%\MeshFit\MeshFit.log`. It contains:
- Every internal error, with its stack trace.
- A breadcrumb (`·` lines) before and after each heavy step: page builds, Auto Segment, fitting jobs,
  background suggestions, large paint strokes, overlay rebuilds of big scans, and display-list
  compiles.
- With each breadcrumb, the process memory and its GDI/USER handle counts.

If SOLIDWORKS closes, the last breadcrumb shows what was running. Send the end of the log together
with SOLIDWORKS's crash report.

Large scans (millions of triangles):
- Auto Segment and classification run in the background. Esc cancels classification; regions
  fitted so far keep their type.
- Classification stops at the simplest surface type within tolerance and does the biggest regions
  first.
- Background selection suggestions are off for regions over 150,000 triangles, for scans over
  4 million, and while a wizard is open.
- The overlay draws in batches. If the graphics driver faults, the overlay switches off instead of
  closing SOLIDWORKS. Reloading the mesh turns it back on.

## Status and known limitations

**What has been tested**
- The fitting library, the IGES export and the command line tool are covered by unit tests.
- An end-to-end run on a synthetic panel recovered the plane, the cylinder (R25), the cone
  (2.862° half angle) and a freeform skin.
- The exported IGES file was read back with an independent CAD kernel (OpenCascade).

**What has not been tested**
- The add-in compiles against the SOLIDWORKS 2024 API but has **not yet been run inside
  SOLIDWORKS**.

**Things to check on first use inside SOLIDWORKS**
- **Overlay drawing.** The painted overlay is drawn with OpenGL in the view's `BufferSwapNotify`
  callback, which is the standard approach for custom graphics. If nothing appears, try toggling
  *Tools > Options > Performance > Enhanced graphics performance*.
- **Picking.** Picking uses `IModelView.Transform` and assumes an **orthographic** view. Turn off
  perspective while painting. With Windows display scaling other than 100% the brush may be
  offset from the cursor.
- **B-spline creation.** `IModeler.CreateBsplineSurface` doesn't fully document its
  control-point ordering. MeshFit tries each possible layout and keeps the one that reproduces the
  fit. If none matches, the feature isn't created and the region reports the failure; use IGES
  export as the fallback.

**Other limitations**
- The UI always shows lengths in millimetres, whatever the part's units are.
- One freeform patch is untrimmed and four-sided. Trim it in SOLIDWORKS.
