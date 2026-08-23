# Robot Command architecture

The solution keeps the application boundary separate from portable contracts,
simulation, rendering, and the worker process. The project graph is intentional
and is validated by `ArchitectureBoundaryTests`.

## Project boundaries

| Boundary | Responsibility |
| --- | --- |
| `RobotCommand.Core` | Immutable UI-neutral contracts and workflow snapshots. |
| `RobotCommand.Rendering` | Portable 3D math, scene projection, primitive rendering, and mesh loading/preview data. |
| `RobotCommand.Rendering.Veldrid` | Optional hardware rendering backend. |
| `RobotCommand.Simulation` | Portable Ghost state, physics, IPC records, and deterministic simulation logic. |
| `RobotCommand.Simulator` | Separate simulation worker executable. |
| `RobotCommand.Runtime` | Persistence, connections, MAVLink, workflows, reconciliation, and application services. |
| `RobotCommand.Cli` | Standalone and embedded command adapters. |
| `RobotCommand` | Avalonia desktop application, presentation composition, and Avalonia rendering adapter. |
| `RobotCommand.Sdk` | Packable observer SDK and protobuf contracts. |
| `tests/RobotCommand.Tests` | Cross-boundary unit, integration, contract, and architecture tests. |
| `examples/RobotCommand.Observer` | Minimal SDK consumer example. |
| `tools/RobotCommand.MeshProbe` | Test-only mesh inspection and CPU preview harness. |

Mesh loading remains in the portable Rendering assembly, but its source
namespace remains `RobotCommand.Rendering.Meshes` to avoid unnecessary
source-level churn. The Avalonia adapter is compiled by the GUI because it is
GUI-specific; Veldrid remains separate for portable consumers such as MeshProbe.
Simulation and Simulator remain separate because the latter is launched as a
worker process.

## Canonical implementation owners

- `OperationalMapSceneBuilder` owns structural map projection; motion state is
  published separately and rendered without rebuilding static layers.
- `ThreeDProjection` owns camera projection and shared scene math.
- `FormationLockWorkflow` owns formation transforms; authored assignment and
  backend executors delegate to it.
- `FlightMissionWorkflow` owns mission lifecycle and artifacts; backend
  executors only translate and observe native protocol state.
- `FenceWorkflow` owns target-neutral validation and lifecycle; backend fence
  executors perform native/local transfer or enforcement.
- `GhostProfileWorkflow` owns profile persistence; asset handling updates it
  through the profile workflow rather than maintaining a second catalog.
- `OperatorCommandWorkflow` owns reviewed operation plans, dispatch, results,
  and normalized failure messages.
- CLI and GUI layers format snapshots and collect input; they do not reimplement
  workflow validation or backend routing.

## Dependency rules

- Core has no references to Avalonia, Mapsui, Veldrid, Windows APIs, Runtime, or
  transport implementations.
- Portable Rendering references Core only.
- Simulation has no GUI or Windows dependency.
- Runtime may consume portable Core, Rendering, Simulation, SDK, and backend
  packages, but Core does not depend on Runtime.
- Optional hardware and Avalonia integrations do not enter portable rendering or
  simulation projects.
- Generated protobuf output is owned by the SDK build and is not hand-edited.

## Compatibility and cleanup policy

The public SDK, protobuf schema, command names, and existing public namespaces
remain compatibility-sensitive. Internal project consolidation is allowed for
the 0.1.0 release preparation, but proven compatibility adapters and safety
paths are retained. Dead code is removed only when references, project
inclusion, runtime registration, and tests demonstrate that it is unused.
