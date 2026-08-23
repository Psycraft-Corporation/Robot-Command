using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public interface IWeatherRadarSource : IAsyncDisposable
{
    WeatherRadarSnapshot Current { get; }

    event EventHandler? Changed;

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task RefreshAsync(CancellationToken cancellationToken = default);

    void SetViewActive(bool active);
}
