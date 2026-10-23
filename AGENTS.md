# MeshFit: guide for AI agents taking over this project

MeshFit is a SOLIDWORKS add-in (C#, .NET Framework 4.8, COM) plus a geometry library (.NET Standard
2.0) for **reverse engineering 3D scans**, mainly car body panels. The user loads a scan mesh,
splits it into regions (automatically or by painting), and MeshFit fits planes, cylinders,
cones, spheres, tori, freeform B-splines, and line-and-arc sketches to them. The results become
native SOLIDWORKS surfaces, sketches and features.

The user is a SOLIDWORKS engineer, not a programmer. They test each build in SOLIDWORKS and report
back with screenshots, log excerpts and SOLIDWORKS crash reports. You cannot run SOLIDWORKS. Code
on the SOLIDWORKS side is verified only by compiling it and by the user's tests.

## Read these first

The detailed handoff is in [`docs/handoff/`](docs/handoff/README.md). Read it in this order:

1. [Goals and requirements](docs/handoff/01-goals-and-requirements.md): what the user wants and
   how they like to work.
2. [Status, open issues and backlog](docs/handoff/09-status-open-issues-backlog.md): where things
   stand right now.
3. [Crash safety and diagnostics](docs/handoff/07-crash-safety-and-diagnostics.md): mandatory
   before touching any add-in code.
4. The rest as needed: architecture, algorithms, SOLIDWORKS integration, UI, dev environment,
   history.

## Repository map

```
src/MeshFit.Core/        netstandard2.0, no dependencies: mesh, regions, fitting, analysis, export
src/MeshFit.SolidWorks/  net48 x64 COM add-in: ribbon, PropertyManager pages, task pane, OpenGL overlay
src/MeshFit.Cli/         net8.0 batch tool (fit a mesh file from the command line)
tests/MeshFit.Core.Tests xUnit tests on synthetic noisy meshes (57 tests, all passing)
tools/ApiInspector       offline lookup of SOLIDWORKS interop signatures and enum values
tools/swhelp.py          fetches SOLIDWORKS API help pages as text
docs/workflow-comparison.html  UI benchmark vs Geomagic Design X, Mesh2Surface, QUICKSURFACE, ScanTo3D
docs/handoff/            this handoff
README.md                user-facing manual (installation, workflow, troubleshooting)
```

## Build and test

```bash
# Linux container (as used so far): .NET 8 SDK in ~/.dotnet
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet test tests/MeshFit.Core.Tests -c Release          # must stay green
dotnet build src/MeshFit.SolidWorks -c Release           # compiles the net48 add-in on Linux too
dotnet run --project tools/ApiInspector -- IModelDoc2 Sketch   # check an API signature
```

CI (`.github/workflows/ci.yml`, windows-latest) runs the tests and builds the add-in. It uploads the
`MeshFit-SolidWorks-addin` artifact. The user downloads that artifact, copies it to a folder and runs
`register.bat` as administrator.

## Rules that must not be broken

- **Never let an exception escape into SOLIDWORKS.** Every callback that SOLIDWORKS or Windows calls
  (PropertyManager handlers, menu/ribbon callbacks, view events, WinForms events, timers,
  `BeginInvoke` or `SynchronizationContext` posts, `Progress<T>`) goes through `Guarded.Run` or an
  equivalent try/catch. One escaped exception terminates SOLIDWORKS and the user loses their work.
  See document 07.
- **Threading.** All SOLIDWORKS API calls and all `RegionSet` / `Session` mutations run on the UI
  thread. Background work (`TaskPaneControl.Run(..., background: true)`) gets copied inputs and
  must not touch SOLIDWORKS or shared state. Results are applied in the `then:` callback.
- **No GDI/USER handle leaks, no refresh storms.** Coalesce refreshes. Don't create fonts or
  bitmaps per refresh. Raise `RegionSet.Changed` once per step. Running out of GDI handles kills
  SOLIDWORKS with no .NET error.
- **Verify SOLIDWORKS API signatures** with `tools/ApiInspector` (and `tools/swhelp.py` for
  semantics) before using an API. The interop is the 2024 NuGet package (32.1.0); the user runs
  SOLIDWORKS 2026.
- **Units.** Internal model units are **metres** (SOLIDWORKS API units). Meshes are scaled on load
  (mm → ×0.001). UI text shows mm.
- **Tests.** Core changes need xUnit tests on synthetic noisy data (`TestMeshes`). Run the whole
  suite before committing.
- **Logging.** Add a `Guarded.Step("…")` breadcrumb before and after any new heavy or native step.
  The user's log at `%LOCALAPPDATA%\MeshFit\MeshFit.log` is the main diagnostic channel.
- **Git.** Work on the branch you are told to use (so far `ccr-d21e0b16-x4gm1v` on
  `aemarconnet/MeshFit`). Commit with clear messages and push. Do not open a pull request unless the
  user asks.
- **Communication.** Plain language. Tell the user what to test, what is unverified, and what to
  send back if it fails.
