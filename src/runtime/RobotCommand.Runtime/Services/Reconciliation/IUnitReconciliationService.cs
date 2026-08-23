using RobotCommand.Models;

namespace RobotCommand.Services.Reconciliation;

public interface IUnitDefinitionService
{
    IReadOnlyList<ManualUnitDefinition> Definitions { get; }
    event EventHandler? Changed;

    ManualUnitDefinition? FindBySource(string sourceVehicleId);
    ManualUnitDefinition? FindByBinding(string connectionId, string vehicleId);
    string ResolveCommandSource(string sourceVehicleId);
    string ResolveTelemetrySource(string sourceVehicleId);
    string ResolveDiagnosticsSource(string sourceVehicleId);
    string DisplayNameFor(string sourceVehicleId, string fallback);
    IReadOnlyList<VehicleRecord> ProjectVehicles(IReadOnlyList<VehicleRecord> vehicles);
    IReadOnlyList<RuntimeRecord> ProjectRuntimes(
        IReadOnlyList<RuntimeRecord> runtimes,
        IReadOnlyList<VehicleRecord> vehicles);

    Task<ManualUnitDefinition> SaveAsync(
        string? unitId,
        string displayName,
        IReadOnlyList<UnitConnectionBinding> connections,
        string commandConnectionId,
        string telemetryConnectionId,
        string diagnosticsConnectionId,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string associationId, CancellationToken cancellationToken = default);
}
