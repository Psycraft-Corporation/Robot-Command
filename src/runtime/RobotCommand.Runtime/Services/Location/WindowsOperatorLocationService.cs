using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using Windows.Devices.Geolocation;

namespace RobotCommand.Services.Location;

public sealed class WindowsOperatorLocationService :
    IOperatorLocationService,
    IHostedService,
    IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PositionTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<WindowsOperatorLocationService> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _shutdown;
    private Task? _monitor;
    private OperatorLocationSnapshot _snapshot = OperatorLocationSnapshot.Unavailable();
    private string? _lastLoggedStatus;

    public WindowsOperatorLocationService(ILogger<WindowsOperatorLocationService> logger)
    {
        _logger = logger;
    }

    public event EventHandler? Changed;

    public OperatorLocationSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_monitor is not null)
        {
            return Task.CompletedTask;
        }

        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Location permission and the first fix must never delay host startup.
        // MonitorAsync is deliberately started directly. Its first operation is
        // RequestAccessAsync, which Windows requires to be invoked from the
        // foreground UI thread. The method returns at its first await, so host
        // startup is still not blocked while the permission prompt/fix runs.
        _monitor = MonitorAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var shutdown = Interlocked.Exchange(ref _shutdown, null);
        if (shutdown is null)
        {
            return;
        }

        shutdown.Cancel();
        if (_monitor is not null)
        {
            try
            {
                await _monitor.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A WinRT location request may finish after the host's shutdown
                // grace period. Do not turn that normal shutdown race into a
                // fatal application error.
                _logger.LogWarning("Operator location monitoring did not finish before shutdown timed out.");
            }
        }

        _monitor = null;
        shutdown.Dispose();
    }

    public void Dispose()
    {
        _shutdown?.Cancel();
        _shutdown?.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                SetUnavailable($"Location permission: {access}.");
                return;
            }

            var locator = new Geolocator
            {
                DesiredAccuracy = PositionAccuracy.Default,
                MovementThreshold = 10
            };
            locator.PositionChanged += OnPositionChanged;
            locator.StatusChanged += OnStatusChanged;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var position = await locator.GetGeopositionAsync(
                            MaximumAge,
                            PositionTimeout);
                        PublishPosition(position);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        SetUnavailable("No current location fix is available.");
                        _logger.LogDebug(ex, "Operator location update failed.");
                    }

                    await Task.Delay(PollInterval, cancellationToken);
                }
            }
            finally
            {
                locator.PositionChanged -= OnPositionChanged;
                locator.StatusChanged -= OnStatusChanged;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetUnavailable("Windows location services are unavailable.");
            _logger.LogInformation(ex, "Operator location monitoring is unavailable.");
        }
    }

    private void SetUnavailable(string status)
        => SetSnapshot(OperatorLocationSnapshot.Unavailable(status));

    private void OnPositionChanged(Geolocator sender, PositionChangedEventArgs args)
    {
        try
        {
            PublishPosition(args.Position);
        }
        catch (Exception ex)
        {
            SetUnavailable("No current location fix is available.");
            _logger.LogDebug(ex, "Operator location movement update failed.");
        }
    }

    private void OnStatusChanged(Geolocator sender, StatusChangedEventArgs args)
    {
        var status = args.Status switch
        {
            PositionStatus.Disabled => "Windows Location Services are disabled.",
            PositionStatus.NoData => "Windows has no current location fix.",
            PositionStatus.NotAvailable => "Windows location hardware or provider is unavailable.",
            PositionStatus.Initializing => "Windows is initializing the location provider.",
            _ => null
        };

        if (status is not null)
        {
            SetUnavailable(status);
        }
    }

    private void PublishPosition(Geoposition position)
    {
        var coordinate = position.Coordinate;
        var point = coordinate.Point.Position;
        if (DateTimeOffset.UtcNow - coordinate.Timestamp > MaximumAge)
        {
            SetUnavailable("The latest location fix is stale.");
            return;
        }

        if (!double.IsFinite(point.Longitude) ||
            !double.IsFinite(point.Latitude) ||
            point.Longitude is < -180 or > 180 ||
            point.Latitude is < -90 or > 90)
        {
            SetUnavailable("The location provider returned an invalid fix.");
            return;
        }

        SetSnapshot(OperatorLocationSnapshot.AvailableAt(
            point.Longitude,
            point.Latitude,
            double.IsFinite(coordinate.Accuracy) ? coordinate.Accuracy : null,
            coordinate.Timestamp));
    }

    private void SetSnapshot(OperatorLocationSnapshot snapshot)
    {
        var shouldLog = false;
        lock (_gate)
        {
            _snapshot = snapshot;
            if (!string.Equals(_lastLoggedStatus, snapshot.Status, StringComparison.Ordinal))
            {
                _lastLoggedStatus = snapshot.Status;
                shouldLog = true;
            }
        }

        if (shouldLog)
        {
            _logger.LogInformation("Operator location state: {State}; {Status}", snapshot.State, snapshot.Status);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
