# 04 · Core algorithms (MeshFit.Core)

All of this is pure C# with no dependencies, tested in `tests/MeshFit.Core.Tests` on synthetic noisy
meshes. Units are metres.

## Mesh

- **`MeshIO.Load(path, scale)`:**
  - STL: ASCII and binary; vertices welded by rounded position.
  - OBJ: polygons triangulated.
  - PLY: ASCII and binary little-endian; vertex and face elements.
  - Coordinates are multiplied by `scale`.
  - Degenerate triangles are removed in the `TriMesh` constructor.
- **`TriMesh`:**
  - precomputes face normals, centroids, areas, area-weighted vertex normals, `Bounds` and
    `AverageEdgeLength`;
  - builds edge adjacency: `Neighbors[t*3+k]` is the triangle across edge k (corner k → k+1), or -1;
  - `Bvh` is built lazily under a lock (`BvhReady` lets the UI avoid blocking). The add-in builds it
    on the loading thread.
- **`Bvh`:**
  - median split on the longest centroid axis, 8 triangles per leaf;
  - `RayCast` returns the closest hit;
  - `LineHits` returns all hits along an infinite line, sorted. Picking uses the hit nearest the
    viewer;
  - `Nearest` returns the closest point.
- **`Curvature.Compute(mesh, scale)`:**
  - per vertex, gather neighbours within `scale` (breadth-first, at most 400);
  - fit the quadric height field z = ax² + bxy + cy² + dx + ey + f in the vertex's normal frame;
  - principal curvatures come from the Hessian, averaged to triangles → `KMax`, `KMin` (1/m);
  - `DefaultScale` = 4 × average edge;
  - parallel via `Work.Options(ct)`, with progress.

## Regions and selection

- **`RegionSet`:**
  - `int[] Owners` per triangle; a list of `Region`.
  - Undo works per step: `BeginStroke(label)` … `Assign(t, id)` … `EndStroke()` records
    (tri, before, after) plus added and removed regions.
  - Nested `BeginStroke` calls join the open step.
  - `Changed` fires once per step. Keep bulk changes inside a step.
- **Painting tools** (`PaintTools`), all honouring a `canChange` filter ("tools only affect the
  active region"):
  - **Brush:** triangles connected to the hit triangle whose centroid lies within the radius.
    Connectivity stops painting through to the back side.
  - **Smart fill / Flood Fill** (`GrowSmooth`): breadth-first growth across edges with a dihedral
    angle below `SmartAngleDeg` (8° default). An optional seed-normal limit, and an optional `stop`
    predicate (curvature fillet detection when *Stop at fillets* is on). After the stroke,
    `FillIslands` fills enclosed holes up to `AutoFillIslandLimit` (500) triangles.
  - **Fill shell** (`FillConnected`), **RemoveSmooth**, **RemoveConnected**.
  - **`CleanUp`:** drop fragments, fill islands, majority-vote boundary smoothing.
  - **`Dilate`:** grow or shrink one ring. **`FillIslands`:** enclosed unassigned groups.
  - **`GrowToSurface`:** add connected triangles within tolerance of the fitted surface whose normals
    agree.
- **Auto Segment** (region groups):
  - `Segment(mesh, ownersSnapshot, creaseDeg, minTris, curvature, blendRadius)` is pure and
    worker-safe.
  - It floods unassigned triangles across edges below the crease angle, and only between triangles
    of the same class. The class is "fillet" when `KMax > 1/blendRadius`, else "face", so faces and
    their fillet bands become separate patches.
  - Patches under `minTris` are absorbed by a **linear multi-source BFS** from the kept patches.
  - `ApplySegmentation` creates the regions in one undo step on the UI thread.
  - The legacy 3-argument `AutoSegment` (task pane button) leaves small patches unassigned.
  - Measured: about 1.4 s on 11M triangles, linear.
- **Fillet tools** (`FeatureTools`):
  - `IsFeature`: `KMax > baseline + 1/blendRadius`. The baseline is the region's median curvature,
    or the seed neighbourhood's (`SeedBaseline`).
  - `GrowToFeatures`, `TrimFeatures` (remove fillet run-out), `SelectFeatureBand` (click a fillet:
    flood the band of high curvature; 10th-percentile baseline within a reach of 2/κ).
- **`SelectionOptimizer.Explore`** ("6 dots on a cylinder → the whole face"):
  - Phase 1, in parallel:
    - "As painted";
    - grow the painted mask over each candidate type (fit → grow to surface → refit until it
      stops).
  - Phase 2: for the 2 best types:
    - shrink the edge, then grow;
    - trim > 2.5σ, then grow;
    - grow at 0.5× and 2× tolerance.
  - `Rank`: valid candidates (P95 ≤ tol) by area; the simpler type wins near-ties (within 2%).
  - Used by the Optimize command and by the background `SuggestionEngine`. That engine is debounced
    700 ms and limited to regions ≤ 150k triangles, scans ≤ 4M, with no job running and no wizard
    open.

## Fitting

- **`SurfaceFitter.Fit(points, normals, FitOptions)`:**
  - subsample to `MaxPoints` (20,000) for fitting; statistics use all points.
  - **Specific type:** fit it, or throw.
  - **Auto:** fit Plane, Sphere, Cylinder, Cone, Torus, Freeform in that order, then pick the
    simplest whose P95 ≤ tolerance and that passes `IsSane` (rejects degenerate huge radii, cone
    half-angle < 0.5°). If none passes, take the lowest RMS. `Alternatives` keeps all results.
  - **`FirstWithinTolerance`:** stop at the first type that passes. Used by classification; much
    faster, and freeform is only fitted when nothing analytic fits.
  - **Outlier rejection** (analytic): residual threshold = max(3·1.4826·median|r|, tol/2). Refit
    on the inliers if ≥ 50% are kept.
- **`AnalyticFitters`:** orthogonal-distance least squares with `LevenbergMarquardt.Minimize`
  over an `ILsqState` (parameters → `Step(delta)`, `Residual(point)`). Initial guesses:
  - **Plane:** PCA of the points, oriented like the mesh normals.
  - **Sphere:** algebraic fit, then LM.
  - **Cylinder:** axis candidates from the normal field (smallest-eigenvalue direction of the
    normals) and two from point PCA. For each candidate: circle fit in the perpendicular plane,
    then LM over axis, point and radius. The lowest cost wins.
  - **Cone:** n·a is constant on a cone, and every tangent plane contains the apex; then LM.
  - **Torus:** axis from the normals; offsetting the points inward by the minor radius puts them on
    the spine circle; then LM.
- **`FreeformFitter`** (B-spline, degree 3):
  - Base plane: the average normal when the normals agree, otherwise PCA. The X axis is along the
    principal direction. The patch must be a height field over that plane.
  - Parameters come from projection, with a `Margin` extension.
  - Least squares with **thin-plate bending smoothing** (`Smoothing`, default 1e-10) on banded
    normal equations (`BandedSymmetricMatrix`), plus 3 parameter-correction passes (re-projection).
  - **Auto control points:** grow the net (up to 40 × 40) until `MeetsTolerance` (sampled P95 ≤ tol
    and RMS ≤ tol/2). Then take the strongest smoothing from {1e-6, 1e-7, 1e-8, 1e-9} that still
    meets tolerance, for the fairest surface.
- **`FitStatistics`:** Rms, MaxAbs, Mean, P95 and WithinTolerance (fraction of points). Decisions
  use P95.
- **`SurfaceExtents`:** the region's parameter rectangle on an analytic surface plus a margin.
  Angles are handled with `AngleUtil.CoveringArc` (wrap-around).

## Clean geometry: `Idealizer.Idealize(surface, regionPoints, others, IdealizeOptions)`

The goal is the design-intent surface. Each candidate constraint pins some parameters and refits the
rest (a masked LM over a re-based axis model). It is **kept only if P95 ≤ max(tolerance, 1.05 ×
original P95)**. The steps, in order:

1. **Direction:**
   - parallel to X/Y/Z or to another fitted feature's direction, within `SnapAngleDeg` (1°);
   - otherwise perpendicular to one.
   - Axis-aligned features also get axis-aligned position offsets.
2. **Coaxial / concentric** with other features (`PositionSnap`, default 3 × tolerance).
3. **Tangency** (fillet cylinders):
   - tangent to two planes (axis ∥ n1 × n2 at distance R from both; golden-section search on R);
   - or tangent to one plane (LM, optional fixed R).
   - The radius is then rounded while keeping tangency.
4. **Rounding:**
   - radii, minor radius, cone half angle;
   - plane offset (when axis-aligned), axis position coordinates, sphere centre;
   - each tries the coarsest step first: `LengthSteps` 1, 0.5, 0.1, 0.05, 0.01 mm and
     `AngleStepsDeg` 1, 0.5, 0.1, 0.05.

`IdealizeResult.Applied` lists what was applied (e.g. "axis ∥ Z", "R = 25 mm"), shown as
`FitResult.Constraints`. Freeform surfaces are not idealized; their fairness comes from the
fitter.

## Sketch profiles (lines and arcs only)

- **`SketchProfiles.Extrusion(mesh, tris, tol, margin, snapDeg)`:**
  - the extrusion direction minimises Σ(n·d)², the smallest eigenvector of the normal covariance;
    snapped to X/Y/Z within `snapDeg`;
  - points are collapsed onto the perpendicular plane, the band is ordered into a centre line
    (`OrderBand`: MST longest path), then `BuildProfile`. Depth comes from the extent.
- **`SketchProfiles.Revolution(...)`:**
  - the axis comes from the **Pottmann line complex** (every normal line meets the axis: smallest
    eigenvector of a 6×6 Plücker system), or from a cylinder/cone/torus hint;
  - refined by LM on the unnormalised Plücker residual and snapped to X/Y/Z;
  - points map to (radius, height), giving the profile and the sweep angle.
- **`BuildProfile` / `FitPolyline`:**
  - `LinesAndArcs` uses dynamic programming over break points for the **fewest lines and arcs
    within tolerance** (lines win ties; arcs must progress monotonically);
  - `Refine` refits each segment to the points it owns (line least squares, geometric circle fit)
    and rebuilds the joints: tangent joints are made exactly tangent, others become intersections;
  - `MergeRedundant` merges neighbours that one segment can cover;
  - `ExtendEnds` (open profiles) covers the end points.
- **`FitPolyline`** (mesh sketch) adds three steps:
  - a full-circle test for closed loops (Kåsa fit; every point within tolerance → two half arcs);
  - closed loops start at their sharpest turn (`RotateToCorner`) so no segment straddles the seam;
  - Douglas–Peucker simplification at 0.3 × tolerance, plus the real midpoint of each kept span,
    keeps the DP fast on long sections.
- **`JointAngle`**, `StartTangent`, `EndTangent`: used to add tangent relations in SOLIDWORKS.

## Mesh sections and Auto Sketch

- **`MeshSection.Slice(mesh, origin, normal, tris?)`:**
  - signed distance per vertex; vertices exactly on the plane are nudged;
  - one point per crossed edge (shared through an edge-key dictionary), one segment per crossed
    triangle;
  - chained into polylines: degree-1 ends first, then loops.
- **`MeshSketch.Build(mesh, frame, tol, minLength)`:**
  - slice at the frame's plane and drop pieces shorter than `minLength` (default max(10 × edge, 20 ×
    tol));
  - a piece whose ends are within 3 edges becomes **closed** (unwelded scan seams);
  - thin the points to 0.25 × edge spacing, then `FitPolyline` in frame coordinates;
  - returns `MeshSketchResult` (Loops, Sections, Lines, Arcs, MaxDeviation).

## Fillet measurement, curves, accuracy

- **`FilletAnalysis.Analyze(mesh, band)`:**
  - a rolling-ball model: the band is parameterised along its length (from both ends plus a
    smoothed coarse spine) and cut into stations;
  - a circle is fitted to each cross-section;
  - outputs the radius along the fillet (`IsConstant` within the measurement scatter or 3%), the
    spine (ball centre path), both tangent lines and `RadiusAt(fraction)` for a SOLIDWORKS
    variable-size fillet.
- **`FeatureCurves.RegionBoundaries`:**
  - boundary edge chains → corners detected on a fine resample (`SplitAtCorners`);
  - each piece is Taubin-smoothed with pinned ends (no shrinkage), then resampled;
  - becomes splines in a 3D sketch.
- **`CadComparison.Compute(scan, cadTessellation, maxDistance, tol)`:**
  - per scan vertex, the signed distance to the nearest CAD triangle (BVH);
  - beyond `maxDistance` counts as "not modelled" (NaN);
  - the add-in refines up to 60k points to exact distances with `Face2.GetClosestPointOn`.

## Alignment (Align Scan)

- **`AlignFeature`:** plane, axis or point, taken from fitted surfaces (`FromSurface`) or part
  references.
- **`MeshAlignment.Solve(mates)`:** up to three mates in priority order (3-2-1). The rotation comes
  from Horn's quaternion method on weighted direction pairs; `Flip` reverses a pairing.
- **`PrincipalAxes`:**
  - the centroid goes to the origin;
  - the exact area second moment gives the axes: longest → X, thinnest → Z (vehicle convention);
  - axis signs are chosen nearest the current orientation.
- **`SymmetryPlane`:** candidate planes through the centroid, normal to each principal axis, are
  refined by minimising the mirrored-scan distance. Returns the plane and its RMS mirror error.
- **`Session.ApplyPlacement`** moves the mesh and every fitted surface (`Transformed`), with undo.
  The transform is saved in the project.

## Export and projects

- **`NurbsConversion.ToNurbs`** gives exact rational NURBS for the analytic types: planes are
  bilinear; cylinder, cone, sphere and torus are revolved profiles built from quadratic rational
  arcs (≤ 90° per segment). Freeform surfaces are already NURBS.
- **`IgesWriter`:** IGES 5.3, entity 128, in mm.
- **`ProjectFile` (.meshfit):** text with the mesh path (relative when possible), scale, transform,
  regions (id, name, colour, requested type) and RLE triangle ownership. Fits are recomputed on load.
