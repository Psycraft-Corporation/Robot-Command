namespace RobotCommand.Services.ManualControl;

/// <summary>Small shared authority gate used by queue preparation without coupling it to controller input.</summary>
public interface IManualControlRegistry
{
    string? ActiveVehicleId { get; }
    bool IsManualSubmission { get; }
    void Acquire(string vehicleId);
    void Release(string vehicleId);
    Task<T> RunManualSubmissionAsync<T>(Func<Task<T>> operation);
}
