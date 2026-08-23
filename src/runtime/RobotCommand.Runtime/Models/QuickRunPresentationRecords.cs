namespace RobotCommand.Models;

public sealed record QuickRunVehicleOption(
    string VehicleId,
    string VehicleName,
    string ConnectionId,
    string ConnectionName,
    string? LogosInstanceId,
    AvailabilityState VehicleState,
    AvailabilityState ConnectionState,
    string VehicleClass,
    string Domain,
    string Readiness,
    string Health)
{
    public string Label => $"{VehicleName} - {ConnectionName}";

    public string Detail =>
        $"{VehicleClass} / {Domain} / {Readiness} / {Health} / vehicle {VehicleState} / connection {ConnectionState}";

    public bool CanPrepare =>
        (VehicleState is AvailabilityState.Online or AvailabilityState.Degraded) &&
        (ConnectionState is AvailabilityState.Online or AvailabilityState.Degraded) &&
        !string.IsNullOrWhiteSpace(ConnectionId);
}

public sealed record QuickRunStepPresentation(
    string Step,
    string State,
    string Message,
    bool Accepted)
{
    public static QuickRunStepPresentation From(OperationalRunStepResult step)
        => new(step.Step, step.State.ToString(), step.Message, step.Accepted);
}
