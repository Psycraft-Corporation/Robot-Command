# Robot Command contributor guidance

## Scope

The repository is prepared as a clean source tree. Do not add credentials,
private endpoints, local application data, SITL state, logs, screenshots,
`bin/`, `obj/`, or generated release output.

## Architecture boundaries

- `src/core` contains UI-neutral contracts and immutable snapshots.
- `src/runtime` owns persistence, connections, protocols, workflows, and safety.
- `src/rendering` remains portable and must not depend on Avalonia or Runtime.
- `src/rendering/*Veldrid` remains portable and independently usable by tools.
- `src/app` contains the Windows Avalonia application and GUI adapters.
- `src/cli` contains CLI and embedded-terminal adapters.
- `src/simulation` contains Ghost simulation and its separately launchable worker.
- `src/sdk` contains the public SDK integration boundary.
- `tests` contains the single test project.

Keep validation and command logic in Runtime/Core workflows. GUI and CLI code
should format workflow snapshots rather than duplicate safety or persistence
rules.

## Safety and vehicle behavior

Raw telemetry remains authoritative for commands, readiness, safety,
formation convergence, and diagnostics. Display interpolation is presentation
only. Preserve backend-native control paths and observed-state completion:
PX4 Offboard, ArduPilot Guided/Brake semantics, and Ghost simulation behavior
must not be replaced with synthetic shortcuts.

Any active multi-vehicle operation must fail atomically and use the existing
safe-release paths. Do not silently change vehicle parameters or claim hardware
validation from SITL results.

## UI expectations

Keep operator screens compact and action-oriented. Avoid explanatory
paragraphs, duplicate headings, stale branding, raw endpoints, and empty-state
prose that does not help the next action. Use existing localization, styles,
tooltips, and accessible names for controls.

## Local validation

Use the public restore configuration:

```powershell
dotnet restore --configfile NuGet.config.template
dotnet build RobotCommand.sln -c Release --no-restore
dotnet test RobotCommand.sln -c Release --no-restore --filter "Category!=SITL&Category!=Hardware"
dotnet format RobotCommand.sln whitespace --verify-no-changes --no-restore
```

Run SITL tests only when explicitly requested and only with the relevant
environment variable enabled. Hardware tests remain operator-gated.

Before handing off changes:

- run `git diff --check`;
- verify no private configuration or generated output was added;
- build the affected project and run focused tests;
- report test results and any remaining warnings;
- do not hide warnings with blanket suppressions.