# 08 · Dev environment and process

## Building and testing (Linux container, as used so far)

The add-in targets net48 but compiles on Linux with the .NET 8 SDK: the reference assemblies come from
NuGet, and the SOLIDWORKS interop from NuGet too. It cannot *run* there.

```bash
# one-time: install the SDK if missing
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh && bash dotnet-install.sh --channel 8.0 --install-dir ~/.dotnet
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet test tests/MeshFit.Core.Tests -c Release        # 57 tests, about 5 s
dotnet build src/MeshFit.SolidWorks -c Release         # add-in (net48 x64)
dotnet build src/MeshFit.Cli -c Release                # CLI
python3 tools/check_callbacks.py                       # every ribbon/menu command has its callbacks
dotnet run --project tools/ApiInspector -- IPropertyManagerPageListbox          # API members
dotnet run --project tools/ApiInspector -- swNumberboxUnitType_e                # enum values
python3 tools/swhelp.py IModelDoc2~SketchAddConstraints                         # API help text (network)
```

On Windows the same `dotnet` commands work. `-p:SolidWorksInteropDir=...` builds against a local
SOLIDWORKS install.

## Before every push

1. `dotnet test` is green.
2. `dotnet build src/MeshFit.SolidWorks` has no errors (warnings are acceptable but keep them
   at 0 if you can).
3. `python3 tools/check_callbacks.py` passes.
4. Re-read your diff with the crash-safety checklist (document 07).
5. Update `README.md` (user manual) when behaviour changes, and these handoff docs when the
   architecture or status changes.

## CI

`.github/workflows/ci.yml` runs on every push, on `windows-latest`:
1. `dotnet test tests/MeshFit.Core.Tests`;
2. `dotnet build src/MeshFit.SolidWorks`;
3. upload the **`MeshFit-SolidWorks-addin`** artifact (`src/MeshFit.SolidWorks/bin/Release/net48/`).

The actions are on Node-24 versions (`checkout@v6`, `setup-dotnet@v5`, `upload-artifact@v6`).
Check runs with the GitHub tools available to you (the previous agent used the GitHub MCP
`actions_list` on `aemarconnet/meshfit`).

## Releases

`.github/workflows/release.yml` builds and publishes a GitHub Release when a tag `v<major>.<minor>.<patch>`
(optionally `-suffix`) is pushed. It can also be run manually with a tag input.

1. Bump `<Version>` in `Directory.Build.props`. The workflow also passes the tag's version to the
   build, so the log's start-up line shows it.
2. Write `docs/release-notes/<tag>.md`, in plain language for the user. If it's missing, GitHub
   generates notes.
3. Commit, push the branch, and wait for the CI build to go green.
4. `git tag -a vX.Y.Z -m "MeshFit vX.Y.Z" && git push origin vX.Y.Z`.
   - **If your git access can't push tags** (HTTP 403, as for the agent that made v0.2.0), run
     the workflow manually on the branch with input `tag = vX.Y.Z`: Actions → release → *Run
     workflow*, or the GitHub API `workflow_dispatch`. The workflow then creates the tag at the
     branch head itself.
5. The workflow:
   - tests, then builds the add-in with `-p:Version=X.Y.Z`;
   - zips `bin/Release/net48` plus `README.md` as `MeshFit-SolidWorks-addin-<tag>.zip`;
   - runs `gh release create` with the notes.
6. Versions below 1.0 and suffixed tags are marked **pre-release**.
7. Check the run (Actions → release) and the Releases page. Tell the user the download link and what
   to test.

Released so far: **v0.2.0** (first packaged release, pre-release).

## How the user installs and tests

1. Downloads the CI artifact for the latest successful run and unzips it to a permanent folder.
2. Right-click `register.bat` → **Run as administrator**. It unblocks the files and runs `RegAsm
   /codebase`.
3. Restarts SOLIDWORKS. If the ribbon layout changed, resets the CommandManager.
4. Tests with real scans and reports back: screenshots, `%LOCALAPPDATA%\MeshFit\MeshFit.log`, and the
   SOLIDWORKS crash report.

When you hand over a build, tell the user exactly:
- what changed;
- what is unverified;
- what to try first;
- what to send back if it fails.

## Git

- **Repository:** `aemarconnet/MeshFit` (GitHub).
- **Working branch so far:** `ccr-d21e0b16-x4gm1v`. Use the branch your session is given.
- Push with `git push -u origin <branch>`. Retry on network errors.
- Do **not** open a pull request unless the user asks.
- **Commit messages:**
  - a summary line, then a body that explains *why*;
  - previous commits end with attribution trailers required by the previous agent's environment.
    Follow whatever your own environment requires.
- `git log --oneline` gives the history. Document 02 maps it to the user's requests.

## Writing tests

- Synthetic meshes: `TestMeshes.Grid(f(s,t), s0, s1, t0, t1, ns, nt, noise)` samples a parametric
  surface, with Gaussian noise; `TestMeshes.Box(size, n)` is a welded cube.
- Typical pattern: build a known shape with noise (about 0.003 mm-scale relative to the size), run
  the algorithm, and assert recovered parameters within tolerances. Examples:
  - `FittingTests`: recover a cylinder;
  - `SketchProfileTests`: line + R5 arc + line;
  - `MeshSketchTests`: box → 4 lines, tube → circle;
  - `FeatureTests`: Auto Segment splits floor/fillet/wall; many regions → one change event.
- Performance or crash questions in Core: write a throwaway console project in a scratch folder
  that references `src/MeshFit.Core/MeshFit.Core.csproj` (and `TestMeshes.cs` via `<Compile
  Include=…>`), build large synthetic meshes (10M+ triangles), and time the steps. That's how the
  region-creation crash was narrowed down (document 07).

## Code style

- Match the surrounding code: C# with expression-bodied members where short, XML doc comments that
  explain *why*, and UI strings in plain language with mm units.
- Core stays dependency-free and testable. Add-in code is thin: marshal inputs, call Core, apply the
  results, create SOLIDWORKS geometry.
- New commands:
  1. add a `Cmd` value;
  2. route it in `TaskPaneControl.Execute`;
  3. add it to the `RibbonCommands` table and/or `MenuItems` with an icon (`CommandIcons.Render`);
  4. regenerate or add the `OnMenu*`, `OnRibbonEnable*` and `OnMenuEnable*` methods;
  5. run `check_callbacks.py`;
  6. give it a tooltip.
- New PropertyManager pages: derive from `PageBase`, use its helpers, register them in
  `SwAddin.Page.cs`, and open them with `ShowPage`.

## Helpful facts

- Interop: the NuGet 32.1.0 SOLIDWORKS packages are in `~/.nuget/packages/solidworks.interop.*`
  after one build. `tools/ApiInspector` reads them by metadata.
- The help site pages are named like
  `SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IModelDoc2~SketchAddConstraints.html`.
  `tools/swhelp.py` extracts their text from the embedded `__NEXT_DATA__` JSON.
- The Design X 2014 user guide PDF (University of Pittsburgh mirror) was the main reference for the
  workflow redesign. See the source list in `docs/workflow-comparison.html`.
