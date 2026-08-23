# Robot Command mesh probe

This is a test-only, portable mesh harness. It is intentionally outside the
Robot Command application workflow and does not start Avalonia or Runtime.

```text
dotnet run --project tools/RobotCommand.MeshProbe -- inspect path/to/model.glb
dotnet run --project tools/RobotCommand.MeshProbe -- validate path/to/model.obj
dotnet run --project tools/RobotCommand.MeshProbe -- render path/to/model.glb --output model.ppm
dotnet run --project tools/RobotCommand.MeshProbe -- compare one.glb two.obj
dotnet run --project tools/RobotCommand.MeshProbe -- hardware-probe
```

The normal commands use the deterministic CPU renderer and require no GPU.
`hardware-probe` is opt-in and reports Veldrid initialization status; a device
failure does not affect the normal inspect, validate, render, or compare path.

Supported inputs are glTF/GLB and OBJ. FBX, textures, animation, and managed
asset storage are intentionally deferred.
