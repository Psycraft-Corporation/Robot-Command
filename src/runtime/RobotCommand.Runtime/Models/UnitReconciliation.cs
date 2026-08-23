namespace RobotCommand.Models;

using RobotCommand.Core;

/// <summary>
/// One connection's observation of a physical unit. VehicleId remains the
/// backend-native routing identity; the binding associates it with the unit.
/// </summary>
public sealed record UnitConnectionBinding(string ConnectionId, string VehicleId);

/// <summary>
/// A persisted, operator-confirmed physical unit with one or more associated
/// connections and explicit authority assignments.
/// </summary>
public sealed record ManualUnitDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<UnitConnectionBinding> Connections,
    string CommandAuthorityConnectionId,
    string TelemetryAuthorityConnectionId,
    string DiagnosticsAuthorityConnectionId,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<UnitCameraSourceBinding>? CameraSources = null,
    IReadOnlyList<string>? ConnectionIds = null);
