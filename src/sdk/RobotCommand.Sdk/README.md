# RobotCommand.Sdk

Read-only client SDK for the Robot Command LAN Observer API.

Observer sessions expose a `Disconnected` event with a typed reason. Hosts can require
re-authentication while running; clients should use that event to prompt for the current
session passphrase and request a new observer session.

Clients first probe the server, present its SHA-256 certificate fingerprint to the user, then create a pinned client, request session access, and consume snapshots. Approval and bearer credentials last only for the connected observer session.

A host generates or accepts a two-word passphrase for its running server session. `RobotCommandPairingInvitation` carries the endpoint, certificate fingerprint, and passphrase for QR/deep-link clients; the host still explicitly approves every observer session. Clients may also call `RequestAccessAsync` with the session passphrase after probing the endpoint.

The API carries canonical SI and WGS84 values. It does not expose command, control, video, file-transfer, or mutation operations.
