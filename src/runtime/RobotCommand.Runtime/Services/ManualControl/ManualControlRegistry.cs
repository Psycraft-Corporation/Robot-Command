namespace RobotCommand.Services.ManualControl;

public sealed class ManualControlRegistry : IManualControlRegistry
{
    private readonly AsyncLocal<int> _manualSubmissionDepth = new();
    private string? _activeVehicleId;
    public string? ActiveVehicleId => Volatile.Read(ref _activeVehicleId);
    public bool IsManualSubmission => _manualSubmissionDepth.Value > 0;
    public void Acquire(string vehicleId) => Volatile.Write(ref _activeVehicleId, vehicleId);
    public void Release(string vehicleId)
    {
        if (string.Equals(ActiveVehicleId, vehicleId, StringComparison.Ordinal))
            Volatile.Write(ref _activeVehicleId, null);
    }
    public async Task<T> RunManualSubmissionAsync<T>(Func<Task<T>> operation)
    {
        _manualSubmissionDepth.Value++;
        try { return await operation(); }
        finally { _manualSubmissionDepth.Value--; }
    }
}
