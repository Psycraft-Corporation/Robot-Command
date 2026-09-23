using RobotCommand.Models;

namespace RobotCommand.Services.Media;

/// <summary>Provider boundary for camera streams carried by a media connection.</summary>
public interface ICameraMediaSourceProvider
{
    string Key { get; }
    string DisplayName { get; }
    void Validate(string endpoint);
    Task<CameraMediaSourceProbeResult> ProbeAsync(string endpoint, CancellationToken cancellationToken = default);
    CameraSourceRecord CreateSource(string id, string name, string endpoint, bool available, string message);
    Task<CameraStreamRecord> OpenAsync(string id, string name, string endpoint, CancellationToken cancellationToken = default);
    Task CloseAsync(CameraStreamRecord stream, CancellationToken cancellationToken = default);
}

public sealed record CameraMediaSourceProbeResult(bool Succeeded, string Message, DateTimeOffset CheckedAt);
