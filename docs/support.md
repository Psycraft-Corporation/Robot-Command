# Support matrix

| Area | Status |
| --- | --- |
| Windows desktop GUI | Supported target |
| Core, Runtime, CLI | Portable where project dependencies allow |
| Rendering software path | Required fallback; works without a dedicated GPU |
| Veldrid hardware path | Optional and platform-dependent |
| Ghost simulation | Supported for development and deterministic tests |
| PX4 MAVLink | Supported with backend-specific validation |
| ArduPilot Copter MAVLink | Supported with backend-specific validation |
| Logos vehicle execution | Depends on available Logos services; unsupported paths remain explicit |
| GStreamer packaging | Windows packaging path; native runtime notices are required |
| Hardware validation | Operator-gated; not implied by automated tests |

The desktop application is not currently promised as a macOS or Linux GUI product. Portable libraries and headless tools are kept independent so they can be built and tested separately.

Backend support is intentionally not presented as identical firmware behavior. Robot Command keeps generic frontend labels while using native PX4 and ArduPilot modes, acknowledgements, telemetry, and safety release paths internally.
