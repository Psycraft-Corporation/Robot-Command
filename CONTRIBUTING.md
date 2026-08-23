# Contributing

Robot Command is maintained by Psycraft Corporation. Issues and pull requests are welcome after public publication; maintainers review and merge changes.

## Before changing code

- Keep field configuration, credentials, telemetry, logs, and runtime data outside the repository.
- Preserve the public SDK contracts and the portable Core, Rendering, and Simulation boundaries.
- Keep frontend language generic. Backend-specific MAVLink modes belong in adapters and diagnostics.
- Add a regression test for safety, protocol, persistence, or workflow behavior that could regress.

## Local checks

```powershell
dotnet restore
dotnet format RobotCommand.sln whitespace --verify-no-changes --no-restore
dotnet format RobotCommand.sln style --verify-no-changes --no-restore
dotnet build RobotCommand.sln -c Release --no-restore
dotnet test RobotCommand.sln -c Release --no-restore
```

The test project remains single-project by design. Tests are organized by behavior. SITL and hardware tests are marked separately and require explicit environment variables; deterministic tests must not require a vehicle, controller, private feed, or GPU.

## Pull requests

Describe the behavior changed, the tests run, and any hardware or SITL validation. Keep commits focused and avoid generated output, screenshots, private configuration, and unrelated formatting churn.

Security issues should use GitHub private vulnerability reporting. Do not put credentials, connection details, telemetry, or personal data in public issues.

Contributions are licensed under the repository’s Apache-2.0 terms.
