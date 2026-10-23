# MeshFit handoff

These documents let a new engineer or AI agent continue MeshFit without the original conversation.
They describe the state of branch `ccr-d21e0b16-x4gm1v` at commit `584c5d1` (plus this handoff).

| # | Document | Read it for |
|---|---|---|
| 01 | [Goals and requirements](01-goals-and-requirements.md) | Who the user is, what they want, how they work, and every requirement so far |
| 02 | [History and decisions](02-history-and-decisions.md) | Each round of requests, what was built, and why things are the way they are |
| 03 | [Architecture](03-architecture.md) | Projects, files, classes, data flow, threading model |
| 04 | [Core algorithms](04-core-algorithms.md) | Fitting, clean geometry, curvature, segmentation, selection, sketches, alignment, export |
| 05 | [SOLIDWORKS add-in](05-solidworks-addin.md) | Registration, ribbon, PropertyManager pages, task pane, overlay, input, API notes |
| 06 | [UI and workflow](06-ui-and-workflow.md) | What the user sees and does; mapping to Geomagic Design X |
| 07 | [Crash safety and diagnostics](07-crash-safety-and-diagnostics.md) | Rules that keep SOLIDWORKS alive; the log; past crash investigations |
| 08 | [Dev environment and process](08-dev-environment-and-process.md) | Building, testing, CI, tools, git, how the user installs and reports |
| 09 | [Status, open issues and backlog](09-status-open-issues-backlog.md) | What is verified, what is not, known problems, next steps |

Start with 01, 09 and 07. Then read the [repository-level `AGENTS.md`](../../AGENTS.md) for the hard
rules, and the user manual [`README.md`](../../README.md) for the feature descriptions the user
reads.
