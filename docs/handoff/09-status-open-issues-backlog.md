# 09 · Status, open issues and backlog

State at commit `584c5d1` plus this handoff, on branch `ccr-d21e0b16-x4gm1v`. Published as GitHub
pre-release **v0.2.0** (see `docs/release-notes/v0.2.0.md`).

## Verification status

| Area | Verified by | Status |
|---|---|---|
| Core geometry (fitting, clean geometry, sketches, sections, segmentation, alignment, IGES) | 57 xUnit tests on synthetic noisy data, plus reproductions at 660k and 11M triangles | ✅ Passing |
| Add-in compiles against SOLIDWORKS 2024 interop | Linux build + Windows CI | ✅ |
| Add-in loads, ribbon tab appears, mesh import works | User (SOLIDWORKS 2026 SP3.2): "I love the tab in the command manager"; the log shows the mesh installing | ✅ |
| Position Mesh (Align Scan) dialog | User log: NullReferenceException in `FillMateLists` | 🔧 Fixed in `584c5d1`, not re-tested |
| Region Groups / region creation | User: SOLIDWORKS crashed part-way through region creation (11M and 642k scans) | 🔧 Fix in `584c5d1`, **waiting for the user's test** |
| Surface Primitives / Extrusion / Revolution / Mesh Fit wizards | Never run in SOLIDWORKS (built after the last successful session) | ❓ Unverified |
| Mesh Sketch (plane selection, section preview, sketch + relations) | Never run in SOLIDWORKS | ❓ Unverified |
| Rectangle / Freehand selection, right-click paint menu, Delete to clear | Never confirmed by the user | ❓ Unverified |
| Pushpin = Lock behaviour of the pages | SOLIDWORKS behaviour for pinned add-in pages assumed; both paths handled | ❓ Unverified |
| Surface creation (analytic and B-spline), extrude and revolve features | Built in earlier rounds; the user hasn't reported results yet | ❓ Unverified |

## Immediate next step

1. Ask the user to install the CI build of `584c5d1` (or later) and repeat their test:
   - load `Microsoft Sculpt Keyboard.ply` (642k), open **Region Groups**, run Auto Segment;
   - then the 11M `fuse_mesh.ply`.
2. If SOLIDWORKS still closes:
   - get the end of `MeshFit.log` (the breadcrumbs show the last step, memory and GDI/USER counts)
     and the SOLIDWORKS crash report;
   - diagnose with document 07.
3. If it works, walk the user through a wizard (Surface Primitives on a fitted plane or cylinder) and
   Mesh Sketch, and collect feedback.

## Known issues and risks (most important first)

1. **Region creation crash:** the fix is unconfirmed (see above).
2. **Classification time on huge scans.** Auto Segment on 11M triangles can create thousands of
   regions; classifying them (each region fitted) can take a very long time in the background. It is
   cancellable and the biggest regions go first, but consider:
   - classifying only regions above a size;
   - classifying on demand (when a region is selected or a wizard previews).
3. **`ApplySegmentation` on the UI thread** takes about 4 s for 11M triangles. Most of it is
   `RegionSet.EndStroke` building the undo record with LINQ over 11M entries. Optimise: a direct
   array-based record, or a special "bulk assign" undo record.
4. **`Session.FitAndGrow` runs on a worker and mutates the `RegionSet`.** Convert it to the snapshot
   pattern: grow on a mask copy, apply on the UI thread.
5. **`CreateSurfaces` fits missing regions synchronously on the UI thread** (`Session.FitRegion`).
   Big regions freeze SOLIDWORKS. Fit on a worker first (like `FitRegions`), then create.
6. **Rendering of 10M+ triangle scans:**
   - the mesh layer is a display list (copied into driver memory);
   - the paint layer is drawn every frame from client arrays, in batches.

   If frames are slow or the driver struggles, switch to VBOs (`glGenBuffers` via
   `wglGetProcAddress`), or draw a decimated proxy of the grey mesh.
7. **Rectangle/Freehand on huge scans:** one ray cast per candidate triangle (in parallel). A
   screen-wide box over millions of triangles can pause for seconds. Consider a depth-buffer
   visibility pass, or limiting candidates by screen-space binning.
8. **Mesh Sketch details to verify in SOLIDWORKS:**
   - which object `GetSelectedObject6` returns for a plane (`Feature` vs `IRefPlane`; both handled,
     the latter creates its own plane);
   - `sgMERGEPOINTS` and `sgTANGENT` applied with `AddToDB = true` geometry;
   - arc direction when the sketch is mirrored relative to the frame (handled by mid-point
     orientation);
   - H/V relations relative to the sketch axes.
9. **Unicode marks** (✔ ⚠ ○ ·) in PropertyManager list and label text: rendering not confirmed.
   Fall back to ASCII if the user sees boxes.
10. **The task pane duplicates many commands** from before the redesign: tabs Fit/Fillets/Output and
    the step bar. Consider trimming it to the region list, reports and settings once the
    PropertyManager workflow is confirmed.
11. **Legacy aliases:**
    - `Cmd.ReverseEngineer` routes to Region Groups;
    - the `Type*` commands and the old quick-create items remain;
    - the 3-argument `PaintTools.AutoSegment` is still used by the task pane's legacy button.

## Backlog: user-visible gaps against the reference tools

From `docs/workflow-comparison.html` and Design X features not yet implemented:

- **Mesh Sketch:**
  - *rotational* method (plane rotated about an axis);
  - draft-angle compensation;
  - several sections in one sketch;
  - choose which loops to keep.
- **Sweep / Loft / Pipe wizards.**
- **Output options** like Mesh2Surface: primitives as **reference geometry** (planes, axes) or
  **solid bodies**, not only surfaces.
- **Region tools:**
  - **Split** a region;
  - **Resegment** a selected region only;
  - "Extend To Sudden Change";
  - Line, Circle and Polyline selection shapes;
  - a "Visible only" toggle for the brush.
- **Accuracy Analyzer as a live panel** during all wizards (Design X style). Only the deviation map
  during Preview exists today.
- **Design X shortcut option:** Shift add, Ctrl remove, Alt + drag size, as an alternative to the
  user's current shortcuts.
- **Variable-size fillet feature from `FilletAnalysis`:** a SOLIDWORKS variable fillet from the
  measured radii along the spine.
- **Trim / knit** created surfaces into a solid, and symmetry mirroring of features about the
  detected symmetry plane.
- **STEP export** in addition to IGES.

## Data and environment facts worth remembering

- The user's scans:
  - `C:\Users\aem30\Downloads\fuse_mesh.ply` (11,085,595 triangles);
  - `D:\OneDrive - Pennsylvania College of Technology\26 Fall\Microsoft Sculpt Keyboard.ply`
    (642,507 triangles).
- **Defaults:**
  - tolerance 0.2 mm; smart-fill angle 8°; blend (fillet) radius 30 mm; auto-fill islands 500
    triangles;
  - Auto Segment crease angle 20° and smallest region 200 triangles;
  - freeform smoothing 1e-10; snap angle 1°; length steps 1/0.5/0.1/0.05/0.01 mm.
- **SOLIDWORKS 2026 SP3.2** is version 34.3.2 (`RevisionNumber()`), on 64-bit Windows 11 with an
  NVIDIA RTX 2000 Ada laptop GPU.
