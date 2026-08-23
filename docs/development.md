# Development

## Tooling

Use the .NET SDK selected by `global.json`. The desktop application targets Windows; the Core, Runtime, Rendering, Simulation, CLI, SDK, and simulator projects are kept portable where their dependencies allow.

## Build and test

```powershell
dotnet restore RobotCommand.sln --configfile NuGet.config.template
dotnet build RobotCommand.sln -c Release --no-restore
dotnet test RobotCommand.sln -c Release --no-restore
dotnet format RobotCommand.sln whitespace --verify-no-changes --no-restore
dotnet format RobotCommand.sln style --verify-no-changes --no-restore
```

The test project is intentionally not split into many assemblies. Its folders follow behaviour: contracts, workflows, MAVLink, simulation, teams, missions, fences, rendering, GUI/CLI, persistence, and infrastructure.

Tests that need live systems are marked `SITL` or `Hardware`. They are not part of the deterministic default suite:

```powershell
dotnet test RobotCommand.sln -c Release --filter "Category!=SITL&Category!=Hardware"
dotnet test RobotCommand.sln -c Release --filter "Category=SITL"
```

Set `ROBOT_COMMAND_PX4_SITL=1` or `ROBOT_COMMAND_ARDUPILOT_SITL=1` before running the corresponding live tests. Hardware tests use their own explicit opt-in variables. Never silently change vehicle parameters during validation.

## Repository checks

```powershell
./build/validate-source-tree.ps1 -SourceOnly
./tools/validate-docs.ps1
```

The source-tree check is intended for a clean staging directory. It rejects credentials, private endpoints, generated output, local state, and unapproved binaries.

## CI-equivalent validation

Use the public template configuration for a source-only restore. Do not use a
local `NuGet.config` in a clean checkout.

```powershell
dotnet restore RobotCommand.sln --configfile NuGet.config.template
dotnet build RobotCommand.sln -c Release --no-restore
dotnet test RobotCommand.sln -c Release --no-restore --filter "Category!=SITL&Category!=Hardware"
dotnet format RobotCommand.sln whitespace --verify-no-changes --no-restore
dotnet format RobotCommand.sln style --verify-no-changes --no-restore
./build/validate-source-tree.ps1 -SourceOnly
./build/validate-dependency-inventory.ps1
./tools/validate-docs.ps1
```

The `CI` GitHub Actions workflow runs these checks on pull requests and the
`main` branch. Portable projects are built on Ubuntu and macOS; the Avalonia
application, CLI, and Windows packaging run on Windows. SITL and hardware
checks are opt-in and are not part of the required deterministic checks.

## Release engineering

Tags are the only release version source. Use `v0.1.0` for a stable release or
an explicitly prerelease tag such as `v0.1.0-preview`. A valid tag starts the
`Release` workflow, which validates the source, builds the Windows application
and CLI archives, writes SHA-256 checksums and a release manifest.

Local packaging commands are:

```powershell
./build/package.ps1 -Runtime win-x64 -Version 0.1.0-dev
./build/package-cli.ps1 -Runtime win-x64 -Version 0.1.0-dev
```

The archives are written to `artifacts/packages/`. The application package
contains the pinned GStreamer runtime and its notice. Windows binaries are
unsigned for this release. Robot Command does not publish application
NuGet packages. The generated `Psycraft.Logos.Api.Sdk` dependency is consumed
from NuGet.org.

For a complete local release-candidate check, including public-only restore,
MeshProbe, package validation, and startup smoke tests, run:

```powershell
./build/validate-release-candidate.ps1 -Version 0.1.0-rc.1
```

See `docs/release-candidate-checklist.md` for the evidence record and the
separate SITL and hardware gates.

## Boundaries

Core contains contracts only. Runtime owns workflows, persistence, connections, and protocol adapters. The GUI owns Avalonia presentation. Rendering remains portable; Veldrid and GStreamer are optional. Simulation is portable, while the simulator worker is a separate executable. The SDK is consumed as a package rather than as a project reference.
