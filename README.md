# Robot Command

Robot Command is a Windows desktop ground-control application for inspecting and operating supported PX4, ArduPilot, and Ghost vehicles. It provides shared command review, telemetry, missions, teams, formations, fences, maps, and a CLI.

Robot Command is not a replacement for the vehicle’s flight controller or its safety systems. SITL coverage is not hardware validation. Test real aircraft only under an appropriate operator checklist.

## Current support

- Ghost simulation: full local development and deterministic test path.
- PX4 MAVLink: supported operations, missions, fences, manual control, and formation control.
- ArduPilot Copter MAVLink: supported operations, missions, fences, manual control, and formation control with ArduPilot-native modes internally.
- Logos SDK integration is available through the public API package.
- Desktop GUI: Windows is the supported desktop target. Portable Core, Runtime, Rendering, Simulation, CLI, SDK, and worker projects remain separate from the GUI.

## Requirements

- .NET SDK 10, using the version in `global.json`.
- Windows for the desktop application.
- A configured vehicle connection or Ghost simulation for operation tests.
- The public preview `Psycraft.Logos.Api.Sdk` package.

## Build and run

```powershell
dotnet restore --configfile NuGet.config.template
dotnet build RobotCommand.sln -c Release
dotnet test RobotCommand.sln -c Release
dotnet run --project src/app/RobotCommand/RobotCommand.csproj
```

The SDK package is restored from NuGet. Do not commit `NuGet.config`, tokens, local connection files, or runtime data. If a private dependency is required locally, configure it outside the source tree.

Run the command-line client with:

```powershell
dotnet run --project src/cli/RobotCommand.Cli/RobotCommand.Cli.csproj -- --help
```

SITL tests are opt-in:

```powershell
$env:ROBOT_COMMAND_PX4_SITL = "1"
$env:ROBOT_COMMAND_ARDUPILOT_SITL = "1"
dotnet test RobotCommand.sln -c Release --filter "Category=SITL"
```

The default test suite excludes SITL and hardware tests. Hardware validation is
operator-run and is not implied by a passing build or SITL run.

## CI and releases

GitHub Actions provides the repository checks through the `CI` workflow. It
uses the public NuGet configuration and does not require Azure credentials,
local connection files, or a running simulator. The `Release` workflow runs
only for a tag matching `vMAJOR.MINOR.PATCH` or a prerelease such as
`v0.1.0-beta.8`. It builds a draft GitHub release for maintainer review; it
does not publish NuGet packages.

To produce the same local package types as the Windows release job:

```powershell
./build/package.ps1 -Version 0.1.0-dev -Runtime win-x64
./build/package-cli.ps1 -Version 0.1.0-dev -Runtime win-x64
```

Release archives, checksums, and the machine-readable manifest are written to
`artifacts/packages/`. Windows binaries are unsigned in this phase. Azure
DevOps remains responsible for the generated `Psycraft.Logos.Api.Sdk` package;
Robot Command does not publish that package or LinkD.

## Repository map

```text
src/app/        Windows Avalonia application
src/cli/        CLI and embedded command adapters
src/core/       UI-neutral contracts and snapshots
src/runtime/    persistence, connections, protocols, and workflows
src/rendering/  portable rendering and Veldrid backend
src/simulation/ Ghost engine and simulator worker
src/sdk/        .NET Logos observer SDK
sdks/dart/      Dart SDK package
tests/          app-owned unit, integration, contract, and SITL tests
examples/       SDK consumer examples
tools/          validation and test-only utilities
```

## Further reading

- [Architecture](docs/architecture.md)
- [Development and testing](docs/development.md)
- [Operations and safety](docs/operations-and-safety.md)
- [Support matrix](docs/support.md)
- [SDK and packages](docs/sdk-and-packages.md)
- [Dependency inventory](docs/dependency-inventory.md)
- [Contributing](CONTRIBUTING.md)
- [Security](SECURITY.md)

## License

Robot Command is licensed under Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
