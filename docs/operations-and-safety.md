# Operations and safety

Robot Command uses a reviewed operation workflow. A command is not complete because a request was sent or an acknowledgement was received. Completion requires fresh authoritative telemetry showing the requested state.

Critical operations validate readiness, dispatch the native command, observe the resulting state, and end as completed, interrupted, rejected, or timed out. Mode loss, failsafe, stale telemetry, link loss, cancellation, target deletion, and operator takeover interrupt the operation and preserve the reason.

## Hold and release

The frontend calls the operation **Hold**. The backend implementation remains native:

- PX4 uses its supported Hold path.
- ArduPilot uses Brake mode for command, mission, and formation Hold. Brake is confirmed with fresh stable position, altitude, and velocity telemetry.
- ArduPilot manual-control release uses Loiter when position is valid, or AltHold when only altitude evidence is available.
- Ghost uses its local deterministic hold controller.

Formation and mixed-Team operations are atomic. Every active member must be eligible, controlled, current, and converged. If one member loses control or falls out of formation, the whole operation stops and releases all remaining members through their safe backend path.

## Missions and fences

Mission upload, start, pause, resume, and completion use backend-specific protocol and mode confirmation. Fence upload replaces the target’s active polygon fence set only after review and validation. Mission fence association validates a route; it does not silently change vehicle fences.

Unsupported commands, frames, altitude bounds, malformed polygons, stale telemetry, and missing position are rejected before mutation. A failed transfer never partially updates the local library.

## Hardware boundary

Ghost and SITL tests are useful for deterministic development, but they do not prove aircraft behavior. Props-off bench testing, secured flight testing, radio-link testing, and parameter review remain operator responsibilities.
