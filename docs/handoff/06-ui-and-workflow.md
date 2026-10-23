# 06 · UI and workflow

The user-facing description is in `README.md` (section *Workflow: the MeshFit tab, left to
right*). The benchmark against established tools is in `docs/workflow-comparison.html`, published
to the user as an artifact page. This document gives the design rules behind the UI and the
mapping to the reference product.

## Reference product: Geomagic Design X (and Geomagic for SOLIDWORKS)

From the Design X 2014 user guide, the 2024.1 release notes and vendor material for the other tools:

| Design X concept | MeshFit equivalent |
|---|---|
| Linear left-to-right workflow: Initialize/Scan → Mesh → Align → Region → Sketch → Model → Accuracy | Ribbon boxes Scan → Align → Regions → Sketch → Model → Analyze → Output |
| **Region Group mode**: Auto Segment on entry (sensitivity, mesh roughness), Merge, Split, Insert, Enlarge, Shrink; Accept/Cancel the mode | **Region Groups** page: Auto Segment on entry (crease angle, fillet radius, smallest region, Classify), Merge with, Insert, Enlarge, Shrink, Clear, Delete; ✔ keeps, ✖ rolls back |
| Selection modes (bottom toolbar): Rectangle (default), Line, Circle, Polyline, Freehand, Paint Brush, Flood Fill, Extend To Similar, Extend To Sudden Change, Visible Only | Paint Brush, Flood Fill, Rectangle, Freehand (visible only), Fillet Band, Pick Region; **Extend To Similar** = fit + grow over the same surface |
| Shift adds, Ctrl removes, Alt + drag sizes the brush | **Different by request:** drag adds, Shift removes (temporary), Remove toggle, Ctrl/Shift + wheel sizes, Ctrl + click straight stroke |
| Dialog tree: Preview, OK, Cancel, **Lock** (OK restarts the command) | PropertyManager: Preview button, ✔, ✖, **pushpin = Lock** |
| Yellow active selection box: click regions | Regions list in each wizard; click regions on the scan (Pick Region) to add or remove them |
| Modeling wizards: Surface Primitives, Extrusion Wizard, Revolution Wizard, Sweep/Loft/Pipe Wizard | Surface Primitives, Extrusion Wizard, Revolution Wizard, Mesh Fit (no Sweep/Loft/Pipe yet) |
| Mesh Fit (freeform NURBS on a region; control points, iso-line flow) | Mesh Fit wizard (B-spline; control points U/V, smoothing) |
| **Mesh Sketch** (planar or rotational sections, offset, draft angle) + **Auto Sketch** (lines/arcs with constraints) | **Mesh Sketch**: planar method + offset; Auto Sketch to a SOLIDWORKS sketch with merge/tangent/H/V relations. (Rotational method and draft compensation: not yet) |
| Accuracy Analyzer pane (live in every mode, also during primitive preview) | Accuracy Analyzer command (CAD vs scan); the deviation map during wizard Preview |
| Align Wizard, Interactive Alignment, Best Fit | Align Scan (numeric, 3-2-1 mates, centroid/box/principal axes/symmetry) |

Secondary references:
- **Mesh2Surface:** primitives as reference geometry, surface or solid; parallel, rotational and
  outline sections.
- **QUICKSURFACE:** constrained best fit (parallel to an axis, perpendicular); a deviation-coloured
  sketch; slot and rectangle recognition.
- **ScanTo3D:** Mesh Prep, Surface Wizard, Curve Wizard, Deviation Analysis.

## Ribbon (current)

| Box | Large | Named (stacked) | Icon only |
|---|---|---|---|
| Scan | Import Mesh | – | Mesh Body, Unload Mesh |
| Align | Align Scan | – | – |
| Regions | Region Groups | Paint Brush, Flood Fill, Rectangle, Freehand, Extend To Similar, Fillet Band | Pick Region, Remove, Clear Selection, Region List |
| Sketch | Mesh Sketch | – | Boundary Curves |
| Model | Surface Primitives | Extrusion Wizard, Revolution Wizard, Mesh Fit | – |
| Analyze | Accuracy Analyzer | – | Measure Fillet, Curvature Map, Zebra |
| Output | – | – | Hide/Show Surfaces, Export IGES, Save/Open Project, Settings |

The width must stay comfortably under 1920 px: the user complained "interface is too wide for
screen". Add new commands as icon-only or stacked buttons.

## The typical session

1. **Import Mesh** (STL/OBJ/PLY, choose units), or **Mesh Body** (a SOLIDWORKS mesh body). The
   BVH is built while loading; curvature runs in the background.
2. **Align Scan**: numeric moves, mates (mesh plane/axis/point → part plane/axis/origin), or auto
   (centroid, box, principal axes, symmetry plane).
3. **Region Groups**:
   - Auto Segment runs if the scan has no regions and the curvature is ready;
   - fix regions with the selection modes, merge, enlarge or shrink, then ✔.
4. **Model:**
   - **Surface Primitives**: click the regions, Automatic or a specific shape, Preview, then ✔;
   - **Extrusion / Revolution Wizard** for extrusions and revolutions;
   - **Mesh Fit** for the doubly-curved skin.

   Pin a wizard to keep it open for the next feature.
5. **Mesh Sketch** on a plane through a flange, then SOLIDWORKS's own Extrude, Revolve or Cut.
6. **Accuracy Analyzer** to check the CAD against the scan. **Hide/Show Surfaces**, **Export
   IGES**, **Save Project**.

The older quick loop still works:
- paint the active region;
- right-click → Extend To Similar / Quick create;
- **Next ▶** hides the surface and starts a new region.

## Shortcuts while a selection mode is active

| Input | Action |
|---|---|
| Left drag | Paint, flood, or draw a rectangle / freehand outline |
| Shift + drag | The opposite of the current Add/Remove mode, for this stroke |
| Ctrl + click | Straight stroke from the last painted point, blending the brush size |
| Ctrl/Shift + wheel | Brush diameter ±10% per notch |
| Right click | MeshFit menu: modes, Ø, edge angle, Stop at fillets, Clear, Undo, Selection ▸, Regions ▸, Surface type ▸, Extend To Similar, Model ▸, Quick create ▸, Next, Region Groups, Stop |
| Delete | Clear the active region's selection |
| Esc | Cancel the running job, otherwise leave the selection mode |
| Middle button / wheel | SOLIDWORKS rotate, pan, zoom (unchanged) |

## UI rules that come from the user's feedback

- Native SOLIDWORKS patterns first: ribbon, PropertyManager, pushpin, status bar. The task pane is a
  companion.
- **Every control has a tooltip.** WinForms tips go through `HoverTips` (placed below the control).
- **No scrolling:** use tabs and collapsible groups.
- **Feedback for everything slow:**
  - the progress strip (stage, %, elapsed, Cancel);
  - SOLIDWORKS status-bar text for every message;
  - `SwProgress` for blocking SOLIDWORKS operations.
- **Never fail silently.** If a command can't run, say why in the status bar. If a page can't
  open, show a message box. A "dead button" is reported as a bug.
- Windows are owned by the SOLIDWORKS main window (`SwAddin.MainWindow`), so they never hide behind
  it.
- Names follow Design X where one exists (Region Groups, Flood Fill, Extend To Similar, Surface
  Primitives, Mesh Sketch, Accuracy Analyzer).
- Default units are mm in the UI, metres in code.
