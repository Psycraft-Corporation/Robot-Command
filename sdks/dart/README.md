# Robot Command Dart SDK

This package contains the Dart protobuf and gRPC bindings plus the high-level
certificate-pinned observer client for the Robot Command Team Observer API.

The public client surface includes `RobotCommandLanClient` for probing and
requesting access, `RobotCommandObserverSession` for maintaining an approved
read-only stream, and `RobotCommandPairingInvitation` for Robot Command QR
links. Sessions keep the bearer token in memory and discard snapshots until a
later FieldApp milestone adds unit mirroring.

## Temporary local distribution

For now, FieldApp consumes this package through a local Dart `path`
dependency. This is intentionally temporary while the Robot Command and
FieldApp repositories are being developed together. The package is not yet
published to a hosted Dart package repository.

When the public SDK is ready, remove `publish_to: none`, publish this package
to the selected hosted Dart repository, and replace FieldApp's `path`
dependency with a versioned `hosted` dependency.

## Regenerating bindings

The generated files under `lib/team/v1` come from the canonical
`team.proto` in the .NET SDK project:

```powershell
protoc --proto_path=..\RobotCommand.Sdk\Proto `
  --proto_path=C:\path\to\protoc\include `
  --dart_out=grpc:lib\src\generated `
  ..\RobotCommand.Sdk\Proto\team\v1\team.proto
```
