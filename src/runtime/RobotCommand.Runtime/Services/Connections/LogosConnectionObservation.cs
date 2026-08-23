using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public sealed record RuntimeObservation(
    string LogosInstanceId,
    string DisplayName,
    string Role,
    string RuntimeMode,
    string PlatformKind,
    string PlatformProfile,
    string LogosVersion,
    string Health,
    string Readiness,
    string HealthCode,
    string HealthMessage,
    IReadOnlyList<string> CapabilityKeys);

public sealed record TeamObservation(
    string TeamId,
    string DisplayName,
    string? ManagerLogosInstanceId,
    string? VehicleId);

public sealed record VehicleObservation(
    string VehicleId,
    string DisplayName,
    string LogosInstanceId,
    string? TeamId,
    string VehicleClass,
    string Domain,
    string ProfileKey,
    string Readiness,
    string Lifecycle,
    string ArmState,
    string Health,
    IReadOnlyList<string> CapabilityKeys);

public sealed record LogosConnectionObservation(
    RuntimeObservation Runtime,
    TeamObservation? Team,
    VehicleObservation? Vehicle,
    AvailabilityState Availability,
    DateTimeOffset ObservedAt);
