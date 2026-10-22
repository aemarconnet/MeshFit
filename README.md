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
  - Brush and Erase with an adjustable radius. Hold Shift to erase.
  - Smart fill grows a region until it reaches an edge sharper than the angle you set.
  - Fill shell fills a whole connected piece.
  - Pick region makes the region under the cursor the active one.
  - Grow ring and Shrink ring add or remove one band of triangles at the region edge.
  - Undo and redo work per stroke.
  - Auto segment splits the unpainted mesh into smooth patches.
- **Grow to surface**: paint a small, clean patch, fit it, then grow the region to every connected
  triangle that lies on that surface. This finds the true edge of each surface on the panel, up to
  the tangent blend lines.
- **Fit report**: RMS, maximum and 95th-percentile deviation, the share of points within tolerance,
  and every candidate type the Auto mode tried.
- **Deviation map**: colours the active region by distance to its fit. Blue → green → red spans
  −tol to +tol. Magenta means the point is out of tolerance.
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

## Workflow for body panels

1. **Bring in the scan**. Use either of these:
   - **Load mesh file…** reads an STL/OBJ/PLY file. Choose its units. If no part is open,
     MeshFit creates one, and the mesh is drawn as an overlay. Recommended for large scans.
   - **Use bodies in part** uses a mesh body that is already in SOLIDWORKS. Import the STL with
     *File > Open*, choose *Import as: Solid body* or *Surface body* under Options, then
     select the body or one of its faces.
2. **Make the first region**. Choose a paint tool and left-drag on the mesh. Use the middle mouse
   button to rotate and pan while a tool is active. For a large, smooth panel, **Smart fill** with an
   angle of 5–10° is usually the fastest start.
3. **Fit**. Pick a type, or leave it on *Auto*, and set the tolerance. Typical values are
   0.05–0.2 mm for a good scan. Then click **Fit active**. Check the report and turn on the
   **Deviation map**.
4. **Grow to surface**, then **Fit active** again. Repeat until the region stops growing. The region
   now covers exactly the triangles that belong to that surface.
5. Click **New** and repeat for the next surface. Usually you do the large primary surfaces first
   (skins, flanges, roll-overs), then the feature lines.
6. Click **Create surface** or **Create all**. Each region becomes a surface feature named after it,
   extended past the painted area by *Extend %* so that adjacent surfaces overlap.
7. Finish in SOLIDWORKS:
   - Trim the overlapping surfaces against each other with *Trim Surface* (mutual trim).
   - Rebuild the small blends with *Fillet*, or with *Boundary Surface* for curvature continuity.
     Don't fit blends directly.
   - *Knit* the result and thicken it if needed.
8. Save the painting with **Save project…** so you can come back to it.

### Tips

- Fit each tangent-continuous primary surface on its own. Leave the narrow blend strips unpainted.
- A freeform region must be a single-valued "height field" over its average plane. If the report
  warns that a region *folds over*, split it into smaller regions.
- Freeform controls:
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
