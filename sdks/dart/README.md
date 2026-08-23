# Robot Command Dart SDK

This package contains the Dart protobuf and gRPC bindings plus the high-level
certificate-pinned observer client for the Robot Command Team Observer API.

The public client surface includes `RobotCommandLanClient` for probing and
requesting access, `RobotCommandObserverSession` for maintaining an approved
read-only stream, and `RobotCommandPairingInvitation` for Robot Command QR
links. Sessions keep the bearer token in memory and discard snapshots until a
later FieldApp milestone adds unit mirroring.

## Distribution

The package is currently consumed through a local Dart `path` dependency and
is not published to a hosted Dart package repository.

## Regenerating bindings

The generated files under `lib/team/v1` come from the canonical
`team.proto` in the .NET SDK project:

```powershell
protoc --proto_path=..\RobotCommand.Sdk\Proto `
  --proto_path=C:\path\to\protoc\include `
  --dart_out=grpc:lib\src\generated `
  ..\RobotCommand.Sdk\Proto\team\v1\team.proto
```
