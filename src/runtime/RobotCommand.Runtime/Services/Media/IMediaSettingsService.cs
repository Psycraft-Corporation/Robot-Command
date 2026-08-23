using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public interface IMediaSettingsService
{
    MediaSettingsSnapshot Current { get; }

    event EventHandler? Changed;

    Task SetAsync(MediaSettingsSnapshot settings, CancellationToken cancellationToken = default);
}

