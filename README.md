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
  - A MeshFit submenu and context toolbar in the SOLIDWORKS right-click menu.
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

## Task pane layout

- **Header**, visible on every tab:
  - The next-step guide.
  - **Undo / Redo**.
  - The **active region** dropdown.
  - The paint tools.
  - **+ Add / − Remove**.
  - Brush **Ø** and smart-fill angle.
  - The deviation legend.
  - A **progress strip** while anything runs: task, current step, % bar and elapsed seconds, with
    **Cancel (Esc)**. Long operations that block SOLIDWORKS also show progress in its status bar.
- **Tabs:**
  - **Mesh:** load, use bodies, **Unload mesh**, **Position mesh…**, project and settings.
  - **Regions:** region list and selection tools.
  - **Fit:** surface type, tolerance, Fit + grow, report and suggestions.
  - **Fillets:** fillet detection, measurement and 3D sketch curves.
  - **Output:** SOLIDWORKS surfaces, hide/show, accuracy analyzer, IGES.
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
   - **Right-click menu:** right-click in the graphics area. The *MeshFit* submenu and the context
     toolbar hold the tools, Add/Remove, Undo, New region, Fill islands and Fit + grow.
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
  Selection/             regions, undo/redo, brush, smart fill, grow-to-surface, auto segmentation
  Fitting/               orthogonal-distance LM fitters (plane, sphere, cylinder, cone, torus),
                         B-spline approximation with thin-plate smoothing + parameter correction,
                         automatic type selection, outlier rejection, statistics
  Export/                exact NURBS conversion of analytic surfaces, IGES 5.3 writer (entity 128)
  Project/               .meshfit project files
src/MeshFit.SolidWorks   net48 COM add-in: task pane UI, OpenGL overlay, mouse picking,
                         SOLIDWORKS surface creation
src/MeshFit.Cli          net8.0 batch tool
tests/MeshFit.Core.Tests xUnit tests: fitters on synthetic noisy data, NURBS exactness, IO, painting, IGES
```

Run the tests with `dotnet test`.

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
