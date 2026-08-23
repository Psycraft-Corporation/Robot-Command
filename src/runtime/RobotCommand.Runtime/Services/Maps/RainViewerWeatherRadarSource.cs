using System.Net.Http.Headers;
using System.Text.Json;
using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public sealed class RainViewerWeatherRadarSource : IWeatherRadarSource
{
    private static readonly Uri MetadataUri = new("https://api.rainviewer.com/public/weather-maps.json");
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly object _gate = new();
    private WeatherRadarSnapshot _current = WeatherRadarSnapshot.Empty;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private bool _enabled;
    private bool _viewActive;

    public RainViewerWeatherRadarSource(HttpClient? client = null)
    {
        _client = client ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _ownsClient = client is null;
        if (_client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("RobotCommand", "0.1"));
        }
    }

    public WeatherRadarSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler? Changed;

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? cancellationToStop = null;
        var refresh = false;

        lock (_gate)
        {
            _enabled = enabled;
            if (!enabled)
            {
                cancellationToStop = StopPollingLocked();
                _current = WeatherRadarSnapshot.Empty with
                {
                    Status = "Weather radar disabled"
                };
            }
            else if (_viewActive)
            {
                refresh = true;
            }
        }

        cancellationToStop?.Cancel();
        cancellationToStop?.Dispose();
        PublishChanged();

        if (refresh)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (_enabled && _viewActive)
                {
                    StartPollingLocked();
                }
            }
        }
    }

    public void SetViewActive(bool active)
    {
        CancellationTokenSource? cancellationToStop = null;
        lock (_gate)
        {
            _viewActive = active;
            if (active && _enabled)
            {
                StartPollingLocked();
            }
            else if (!active)
            {
                cancellationToStop = StopPollingLocked();
            }
        }

        cancellationToStop?.Cancel();
        cancellationToStop?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellationToStop;
        Task? pollTask;
        lock (_gate)
        {
            pollTask = _pollTask;
            cancellationToStop = StopPollingLocked();
            _enabled = false;
            _viewActive = false;
        }

        cancellationToStop?.Cancel();
        if (pollTask is not null)
        {
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellationToStop?.Dispose();
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetAsync(MetadataUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var retrievedAt = DateTimeOffset.UtcNow;
            var parsed = RainViewerWeatherRadarParser.Parse(json, retrievedAt);
            SetCurrent(parsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetUnavailable("Weather radar refresh timed out");
        }
        catch (HttpRequestException)
        {
            SetUnavailable("Weather radar unavailable; showing the last frame");
        }
        catch (JsonException)
        {
            SetUnavailable("Weather radar returned invalid data");
        }
        catch (InvalidDataException)
        {
            SetUnavailable("Weather radar returned no usable frame");
        }
    }

    private void StartPollingLocked()
    {
        if (_pollTask is { IsCompleted: false })
        {
            return;
        }

        _pollCancellation?.Dispose();
        _pollCancellation = new CancellationTokenSource();
        _pollTask = PollAsync(_pollCancellation.Token);
    }

    private CancellationTokenSource? StopPollingLocked()
    {
        var cancellation = _pollCancellation;
        _pollCancellation = null;
        _pollTask = null;
        return cancellation;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(RefreshInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void SetCurrent(WeatherRadarSnapshot snapshot)
    {
        lock (_gate)
        {
            _current = snapshot;
        }

        PublishChanged();
    }

    private void SetUnavailable(string status)
    {
        lock (_gate)
        {
            var current = _current;
            _current = current.HasFrame
                ? current with
                {
                    IsStale = true,
                    Status = status
                }
                : WeatherRadarSnapshot.Empty with
                {
                    Status = status
                };
        }

        PublishChanged();
    }

    private void PublishChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public static class RainViewerWeatherRadarParser
{
    public static WeatherRadarSnapshot Parse(string json, DateTimeOffset retrievedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var host = RequiredString(root, "host").TrimEnd('/');
        if (!Uri.TryCreate(host, UriKind.Absolute, out var hostUri) || hostUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("The weather radar host is not a secure absolute URL.");
        }

        if (!root.TryGetProperty("radar", out var radar) ||
            !radar.TryGetProperty("past", out var past) ||
            past.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The weather radar response has no past frames.");
        }

        var frames = new List<(long UnixTime, string Path)>();
        foreach (var frameElement in past.EnumerateArray())
        {
            if (!frameElement.TryGetProperty("time", out var time) ||
                !time.TryGetInt64(out var unixTime) ||
                !frameElement.TryGetProperty("path", out var pathElement) ||
                pathElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var framePath = pathElement.GetString();
            if (!string.IsNullOrWhiteSpace(framePath))
            {
                frames.Add((unixTime, framePath));
            }
        }

        frames.Sort((left, right) => right.UnixTime.CompareTo(left.UnixTime));

        if (frames.Count == 0)
        {
            throw new InvalidDataException("The weather radar response has no valid past frames.");
        }

        var newestFrame = frames[0];
        var tilePath = newestFrame.Path.StartsWith('/') ? newestFrame.Path : "/" + newestFrame.Path;
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(newestFrame.UnixTime);
        return new WeatherRadarSnapshot(
            newestFrame.UnixTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{host}{tilePath}/256/{{z}}/{{x}}/{{y}}/2/1_1.png",
            timestamp,
            retrievedAt,
            retrievedAt - timestamp > TimeSpan.FromMinutes(20),
            $"Radar {timestamp:HH:mm} UTC",
            "Weather data by RainViewer",
            7);
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"The weather radar response is missing '{propertyName}'.");
        }

        return property.GetString()!;
    }
}
