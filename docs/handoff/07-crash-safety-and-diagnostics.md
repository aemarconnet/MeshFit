# 07 · Crash safety and diagnostics

MeshFit runs **inside the SOLIDWORKS process**. If MeshFit crashes, SOLIDWORKS crashes and the user
loses unsaved work. This has already happened several times (see "Incidents" below). Treat the
rules here as mandatory.

## Why SOLIDWORKS dies

- SOLIDWORKS runs WinForms callbacks in the mode where **exceptions escaping a window procedure are
  not caught**. There is no ThreadExceptionDialog; the process terminates. The first crash report
  showed exactly that: a CLR exception raised through `USER32 → clr → System.Windows.Forms →
  ExecutionContext.Run` from SOLIDWORKS's message loop.
- **Native faults** end SOLIDWORKS with no .NET trace at all:
  - an access violation in the OpenGL driver;
  - SOLIDWORKS itself failing when the process runs out of **GDI/USER handles** (10,000 each) or
    memory.
- **Stack overflow** cannot be caught. There is no recursion in the code base today (checked); keep
  it that way.

## Rules

1. **Contain every callback.** Anything SOLIDWORKS or Windows calls must catch everything:
   - menu and ribbon callbacks: `Menu(cmd)`; enable callbacks: try/catch and return 1;
   - PropertyManager handlers: `PageBase` wraps all of them;
   - view events: mouse notify lambdas `Safe(...)`, `OnBufferSwap` try/catch;
     `ActiveModelDocChangeNotify` → `Guarded.Run`;
   - WinForms events: `Safe(...)`, `Guard(...)`, `Guarded.Run`;
   - timers; `Progress<T>` callbacks; `SynchronizationContext.Post`; `async void` continuations;
     custom `OnPaint`.

   Use `Guarded.Run("where", () => …)`. It logs the stack trace and shows a one-line message in the
   status bar.
2. **Marshalled callbacks go through `TaskPaneControl`**:
   - `_control.BeginInvoke`, `SwAddin.Post(action)` (already wrapped);
   - `TaskPaneControl.WndProc` and `PositionDialog.WndProc` are overridden with try/catch, so
     anything marshalled to them is contained;
   - **callbacks posted through `SynchronizationContext` run on a hidden WinForms window** and are
     *not* covered: wrap them yourself.
3. **Threading.**
   - SOLIDWORKS API calls only on the UI thread.
   - Background work gets **snapshots**:
     - `owners = (int[])S.Regions.Owners.Clone()`;
     - triangle lists;
     - `FitOptions.Clone()`, `IdealizeOptions.Clone()`;
     - lists of other surfaces.
   - Background work never changes `Session`, `RegionSet` or `Region.Fit` directly. Apply results
     in `then:`.
   - See `TaskPaneControl.FitRegions` and `AutoSegmentRegions` for the pattern.
4. **No refresh storms.**
   - `RegionSet.Changed` fires once per `EndStroke`. Do bulk edits inside
     `BeginStroke`/`EndStroke`; never call `AddRegion` in a loop outside a stroke.
   - UI refreshes are coalesced: `QueueRefreshAll`, `QueueRedraw`, `QueuePageRefresh`.
   - Don't call `RefreshAll` or `GraphicsRedraw` in loops.
5. **GDI/USER hygiene.**
   - Never create a `Font`, `Bitmap`, `Pen`, `Brush` or `Icon` per refresh or per item without
     disposing it.
   - Cache them: `_boldFont`, swatches keyed by colour in the ImageList.
   - Dispose the images of transient menus (see `ShowPaintMenu`).
   - Breadcrumbs log the GDI/USER counts and warn above 8,000.
6. **PropertyManager.**
   - Ignore notifications while `Building` / `Updating`.
   - Write controls only when the value changes (`SetChecked`, `SetValue`, …).
   - Wrap cosmetic calls (`Height`, bitmaps) in try.
   - Capture the inputs in `Accept()`; do the SOLIDWORKS work after the page closes (`Post`).
7. **OpenGL.**
   - Every array passed to `glDrawArrays` must hold at least `count` vertices of every attribute
     (`DrawArrays` clamps).
   - Draw in batches (1.5M vertices max).
   - `OnBufferSwap` has `[HandleProcessCorruptedStateExceptions]` to catch a driver access
     violation, log it, and set `Renderer.Failed` (the overlay stays off until the mesh is
     reloaded).
8. **Big scans (10M+ triangles).**
   - Anything per triangle on the UI thread must be O(n) with small constants.
   - Anything heavier goes to a worker with progress and cancellation.
   - Keep the suggestion engine limits (regions ≤ 150k triangles, scans ≤ 4M).
   - Reuse large buffers instead of reallocating per change (LOH churn).
9. **Never fail silently.** If something can't run, `S.Say(...)` the reason (also shown in the
   status bar).

## The log

- **Path:** `%LOCALAPPDATA%\MeshFit\MeshFit.log`. Appended with `File.AppendAllText` under a lock;
  rolled over (deleted) above 4 MB.
- **Lines:**
  - `MeshFit <version> connecting to SOLIDWORKS <rev>` on start-up;
  - `Installing mesh <path>: N triangles`;
  - `ERROR in <where>: <exception with stack>` from `Guarded.Report`;
  - `Command failed: …` from `TaskPaneControl.Guard`;
  - `UNHANDLED (terminating=…)` from the AppDomain handler (a last-chance log before death);
  - **breadcrumbs**: `· <step> [<private MB> MB, GDI n, USER m]` (with `⚠ handle count near the
    Windows limit`). They are written before and after:
    - page builds, opens and closes;
    - Auto Segment start, end, and regions created;
    - every `Run` job start and end (`Job: …`, `Job done/failed/cancelled: …`);
    - fitting batches;
    - background suggestions;
    - paint strokes changing > 200k triangles;
    - overlay rebuilds of > 1M painted triangles;
    - display-list compiles.
- **When SOLIDWORKS dies without an `ERROR` line**, the last breadcrumb tells you what was running.
  Ask the user for:
  1. the end of `MeshFit.log`;
  2. SOLIDWORKS's crash report. The user has pasted one before: the "SolidWorks generated log file"
     with `<SWEXH>`, `<AD5>` (last API calls), `<RAWSTACK>` and `<PROCMEM>` sections.

  Interpret them as below.

### Reading a SOLIDWORKS crash report
- **`<AD5>`:** the last SOLIDWORKS API calls made by add-ins (most recent first). It shows what
  MeshFit was doing, e.g. building a page.
- **`<RAWSTACK>`:**
  - `clr` + `mscorlib` + `System` frames under `USER32` mean a managed exception escaped a window
    procedure (a missing guard);
  - only native frames (driver DLLs, SLDWORKS) mean a native fault.
- **`<PROCMEM>` / `GDIHandlesUsed`:** memory and handle pressure.

## Incidents

### 1. Crash when opening the Reverse Engineer page (fixed in `29ffc78`)
- **Symptom:** SOLIDWORKS closed right after the mesh loaded, via Reverse Engineer.
- **Crash report:** `<AD5>` ended in PropertyManager building (`AddGroupBox`, `AddControl2`,
  `SetRange2`, `SetBitmapsByName3`); `<RAWSTACK>` showed a managed exception escaping a
  `BeginInvoke` callback (`DynamicInvoke` → `TargetInvocationException`).
- **Fix:**
  - the guard layer (`Guarded`, `WndProc` overrides, all handlers guarded);
  - the log;
  - ignore PropertyManager notifications while building (number boxes report 0 during `SetRange2`);
  - cosmetic calls in try.

### 2. Crash part-way through region creation (fix in `584c5d1`, unconfirmed)
- **Log:**
  - `Opening Region Groups`, then the next line is a new SOLIDWORKS start-up: no ERROR and no
    UNHANDLED;
  - this happened on an 11M scan and on a 642k scan;
  - earlier, the same happened after `Opening the Reverse Engineer page` on the 11M scan.
- **Analysis:**
  - Core Auto Segment + classification reproduced fine outside SOLIDWORKS (keyboard-like test
    mesh: 660k triangles, 505 regions, no crash), so the cause was in the add-in;
  - no recursion exists, so not a stack overflow;
  - **found:** `RegionSet.AddRegion` raised `Changed` for every region even inside a step, so Auto
    Segment ran hundreds or thousands of synchronous task pane rebuilds (each creating a new GDI
    font), page refreshes and redraws. This is quadratic UI work plus a GDI handle build-up;
  - also: classification mutated shared state from a worker; background suggestions ran several
    full-region fits in parallel (gigabytes on 11M); 500 MB paint buffers were reallocated per
    change; very large single draw calls went to the driver; the absorption pass was quadratic.
- **Fix:** all of the above; see the commit message. New breadcrumbs now include handle counts and
  memory.
- **Status:** waiting for the user's next test. If it still crashes, the breadcrumbs plus the
  SOLIDWORKS crash report should pinpoint it.

### 3. `PositionDialog.FillMateLists` NullReferenceException (fixed in `584c5d1`)
- The status label was written before it was created when no regions were fitted.

## Checklist for any new add-in code

- [ ] Every new event handler, callback, timer or post is guarded.
- [ ] No SOLIDWORKS API call off the UI thread; background work uses snapshots.
- [ ] Bulk region changes are inside one stroke; no per-item refresh or redraw.
- [ ] No per-refresh GDI objects; transient images are disposed.
- [ ] O(n) or better on the UI thread for per-triangle work; heavier work goes to `Run(background:
      true)` with `Report` and `Token`.
- [ ] Breadcrumbs around heavy or native steps.
- [ ] API signatures checked with `tools/ApiInspector`.
- [ ] `dotnet build src/MeshFit.SolidWorks` and `dotnet test` are both clean.
