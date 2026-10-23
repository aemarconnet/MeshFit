# 01 · Goals and requirements

## The user and the job

- **Who:** a SOLIDWORKS user who reverse engineers **car body panels** from 3D scans. Recent
  test data:
  - `fuse_mesh.ply`: 11,085,595 triangles, a fused scan.
  - `Microsoft Sculpt Keyboard.ply`: 642,507 triangles.
- **Machine** (from a SOLIDWORKS crash report):
  - SOLIDWORKS 2026 SP3.2 (internal version 34.3.2) on Windows 11 Enterprise.
  - Dell Precision 5680: 20 threads, 34 GB RAM, NVIDIA RTX 2000 Ada laptop GPU.
  - Three monitors, 1920 px wide, one at 120 DPI.
- **How they test:**
  1. Download the CI artifact.
  2. Run `register.bat` as administrator.
  3. Load scans in SOLIDWORKS and try the features.
  4. Report back with short messages, log excerpts (`%LOCALAPPDATA%\MeshFit\MeshFit.log`) and
     SOLIDWORKS crash reports.
- **What they are not:** a programmer. Explanations must be plain. Anything you cannot verify
  yourself must be stated as unverified, with exactly what to test.

## The goal

Paint or auto-segment the facets of a scan mesh, fit **planar, cylindrical, conical, spherical,
toroidal and freeform** surfaces, and create them in SOLIDWORKS. The aim is to recover the
**original design surfaces** of body panels: clean, ideal geometry, not a noisy copy of the scan.

## Quality goals for the geometry

1. **Clean, design-intent geometry** (requested explicitly):
   - directions snapped to X/Y/Z and to other features;
   - round dimensions;
   - coaxial and concentric features, and fillets tangent to their neighbours;
   - fair (smooth) freeform surfaces.

   All of it applies only within tolerance. See `Idealizer` in document 04.
2. **Sketch-based features made of lines and arcs only** (extrude, revolve, and mesh sketches),
   with proper sketch relations, so the user can edit them in SOLIDWORKS.
3. **Fillet and variable-fillet awareness.** Panels roll into neighbouring surfaces through
   fillets and blends. Selection tools must stop at, select, measure and trim fillet bands.
4. **CMM-style selection.** "Six dots on a cylinder should select the whole cylindrical face":
   growing a selection to the whole surface the dots lie on (SelectionOptimizer, Fit + Grow /
   Extend To Similar).

## UI and workflow requirements (user's words in quotes)

- **Native SOLIDWORKS look.** The user chose it from proposed options: a CommandManager ribbon tab,
  PropertyManager pages with OK/Cancel and a pushpin, and the task pane only as a companion.
- **Familiar workflow.**
  - "The current toolsets and positions are far from comparable" to established software.
  - The layout now follows **Geomagic Design X / Geomagic for SOLIDWORKS**, with Mesh2Surface and
    QUICKSURFACE as secondary references. See `docs/workflow-comparison.html` and document 06.
- **Ribbon groups.** The user wants buttons for:
  - selection modes (brush, smart/flood fill, …);
  - fitting;
  - building surfaces.
- **"Interface is too wide for screen."** Keep the ribbon compact: few large buttons, the rest
  stacked or icon-only.
- **Always-visible region list** in the PropertyManager, showing each region's type and fit
  quality.
- **Right-click menu while painting**, with:
  - brush and smart-fill settings (diameter, angle);
  - region commands;
  - an easy way to **dismiss/clear a selection** (Delete key, Clear Selection).
- **Brush behaviour like Photoshop:**
  - the brush is defined by its **diameter**;
  - Ctrl/Shift + wheel resizes it;
  - **Ctrl + click** paints a straight line from the last point, blending the brush size;
  - **Shift must not** draw a SOLIDWORKS selection box.
- **Tooltips on every control.** They must appear on screen: they used to appear above it.
- **No scrolling.** Use tabs or compact layouts.
- **Progress bars** for anything slow, so the user can tell whether SOLIDWORKS is hung; **Esc**
  aborts long operations.
- **Undo/redo** for selections; an Add/Remove mode; fill islands; clean-up of brush strokes.
- **Hide/show** the surfaces MeshFit created.
- **Units on the deviation map** (mm).
- **Mesh handling:**
  - unload the mesh;
  - a positioning wizard with numeric, mate-like and automatic placement;
  - fast loading: SOLIDWORKS must not hang after a big scan opens.
- **Feature loop:** paint → fit → create → next feature. Now expressed as Region Groups → wizard
  → ✔ with the pushpin as Lock.

## Non-goals and constraints

- No mesh repair or decimation suite. SOLIDWORKS has mesh tools, and Design X-style mesh prep is not
  requested.
- Must run inside SOLIDWORKS on the user's machine. Large scans (10M+ triangles) must not crash or
  freeze it.
- Interop: built against the SOLIDWORKS 2024 API from NuGet (works on 2026, since COM interfaces only
  grow).
