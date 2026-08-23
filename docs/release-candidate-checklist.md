# Release-candidate checklist

Run `build/validate-release-candidate.ps1` from a clean checkout. The script
uses `NuGet.config.template`, excludes SITL and hardware tests by default,
builds the Windows application and CLI packages, verifies GStreamer and
third-party notices, and writes a machine-readable result under
`artifacts/release-candidate/`.

## Required evidence

- commit SHA and release version;
- public-only restore;
- Release build and deterministic test count;
- whitespace/style checks;
- source, dependency, and documentation validation;
- CLI help and JSON diagnostic startup;
- MeshProbe CPU validation;
- Windows application and CLI archive hashes;
- release manifest and forbidden-content scan;
- application startup/shutdown and CLI cleanup.

## External gates

- PX4 and ArduPilot SITL runs are opt-in with
  `ROBOT_COMMAND_PX4_SITL=1` and/or `ROBOT_COMMAND_ARDUPILOT_SITL=1`, plus
  `-RunSITL`.
- Hardware validation requires an operator-run props-off or secured-flight
  checklist. A passing build or SITL run never marks hardware validated.
- GitHub-hosted CI and draft-release execution are recorded separately from
  this local checklist.

## Latest local run

The local candidate check completed on 2026-08-23 for `0.1.0-rc.1` using
source revision `fed80e6165b613bdfffd399b30ee507ac65ec321` as the package
metadata revision.

- Public-only restore, Release build, formatting, source-tree, dependency, and documentation checks: passed.
- Deterministic tests: 827 passed; SITL and hardware tests were not requested.
- CLI help, JSON 3D diagnostics, and CPU MeshProbe validation: passed.
- Windows application package: passed; GStreamer `1.26.11` verified.
- Windows CLI package: passed; startup and shutdown smoke test passed.
- Release manifest, SHA-256 checksums, and archive-content validation: passed.
- Application archive SHA-256: `e201a4a47923270e6ef7fe4138789eb1f14f4e4c1c2c20def104e671125f393a`.
- CLI archive SHA-256: `0fe6548c96d6e15888cdae68ba08463409f66e2ee1edf32c7663ba3581a48924`.
- Release manifest SHA-256: `b40c6ae3d3c27cade7564f74ad5ba15fb06d42872c340aed551871a7aa44d6aa`.
- External consumer restore/build using `Psycraft.Logos.Api.Sdk` `0.1.0-beta.8` from nuget.org: passed.
- PX4/ArduPilot SITL and physical hardware validation: pending their explicit operator-run gates.

The machine-readable report is written to
`artifacts/release-candidate/0.1.0-rc.1-checklist.json`; release artifacts are
written to `artifacts/packages/`.
