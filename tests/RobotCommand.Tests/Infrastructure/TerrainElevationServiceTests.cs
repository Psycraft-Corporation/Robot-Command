using System.Net;
using System.Text;
using System.Text.Json;
using RobotCommand.Services.Terrain;
using Xunit;

namespace RobotCommand.Tests;

public sealed class TerrainElevationServiceTests
{
    [Fact]
    public async Task CopernicusProvider_ParsesCarpetTilesAndInterpolatesValues()
    {
        Uri? requestedUri = null;
        using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"status\":\"success\",\"data\":{\"bounds\":{\"sw\":[43.7,-79.4],\"ne\":[43.71,-79.39]},\"carpet\":[[112.5,118.5],[212.5,218.5]],\"stats\":{\"min\":112.5,\"max\":218.5,\"avg\":165.5}}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        using var provider = new CopernicusTerrainElevationProvider(
            new TerrainOptions(Endpoint: "https://terrain.example"),
            httpClient: client);

        var coordinates = new[]
        {
            new TerrainCoordinate(43.7, -79.4),
            new TerrainCoordinate(43.705, -79.395)
        };

        var result = await provider.QueryAsync(coordinates, 30);

        Assert.True(result.Succeeded);
        Assert.Equal(112.5, result.ElevationsMetres[0]);
        Assert.Equal(165.5, result.ElevationsMetres[1]!.Value, precision: 8);
        Assert.Equal(TerrainVerticalReference.ProviderNative, result.VerticalReference);
        Assert.Equal(30, result.ResolutionMetres);
        Assert.Contains("/api/v1/carpet?points=", requestedUri!.ToString());
    }

    [Theory]
    [InlineData("[1]", "INVALID_RESPONSE")]
    [InlineData("[\"not-a-number\"]", "INVALID_RESPONSE")]
    [InlineData("{\"height\": 1}", "INVALID_RESPONSE")]
    public async Task CopernicusProvider_RejectsMalformedResponses(string payload, string expectedCode)
    {
        using var client = new HttpClient(new DelegateHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            }));
        using var provider = new CopernicusTerrainElevationProvider(
            new TerrainOptions(Endpoint: "https://terrain.example"),
            httpClient: client);

        var result = await provider.QueryAsync(
            [new TerrainCoordinate(43.7, -79.4), new TerrainCoordinate(43.8, -79.3)],
            null);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Fact]
    public async Task CopernicusProvider_BoundsVeryBroadQueriesBeforeOpeningManyTiles()
    {
        var requests = 0;
        using var client = new HttpClient(new DelegateHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("The broad query should be rejected before HTTP.");
        }));
        using var provider = new CopernicusTerrainElevationProvider(
            new TerrainOptions(Endpoint: "https://terrain.example"),
            httpClient: client);

        var coordinates = Enumerable.Range(0, 65)
            .Select(index => new TerrainCoordinate(43 + index * 0.011, -79.4))
            .ToArray();

        var result = await provider.QueryAsync(coordinates, 30);

        Assert.False(result.Succeeded);
        Assert.Equal("VIEW_TOO_BROAD", result.ErrorCode);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Service_RejectsInvalidCoordinatesWithoutCallingProvider()
    {
        var provider = new FakeTerrainProvider();
        using var service = CreateService(provider);

        var result = await service.GetElevationAsync(91, -79.4);

        Assert.Equal(TerrainQueryStatus.InvalidRequest, result.Status);
        Assert.Equal("INVALID_COORDINATE", result.DiagnosticCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Service_CachesFreshSamplesInMemoryAndOnDisk()
    {
        var directory = CreateTempDirectory();
        try
        {
            var firstProvider = new FakeTerrainProvider
            {
                ResponseFactory = coordinates => Success(coordinates, 150)
            };
            using (var firstService = CreateService(firstProvider, directory))
            {
                var first = await firstService.GetElevationAsync(43.7, -79.4);
                Assert.Equal(TerrainQueryStatus.Available, first.Status);
                Assert.False(first.Sample!.FromCache);
            }

            var secondProvider = new FakeTerrainProvider
            {
                ResponseFactory = _ => throw new InvalidOperationException("The disk cache should satisfy this request.")
            };
            using var secondService = CreateService(secondProvider, directory);
            var second = await secondService.GetElevationAsync(
                43.7,
                -79.4,
                new TerrainQueryOptions(MaximumAge: TimeSpan.FromDays(1)));

            Assert.Equal(TerrainQueryStatus.Available, second.Status);
            Assert.True(second.Sample!.FromCache);
            Assert.Equal(150, second.Sample.ElevationMetres);
            Assert.Equal(0, secondProvider.CallCount);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task Service_ReturnsStaleCacheWhenRefreshFails()
    {
        var provider = new FakeTerrainProvider
        {
            NumberedResponseFactory = (call, coordinates) => call == 1
                ? Success(coordinates, 150)
                : new TerrainProviderResponse(
                    false,
                    [],
                    TerrainVerticalReference.ProviderNative,
                    "provider-native",
                    null,
                    "NETWORK_ERROR",
                    "offline")
        };
        using var service = CreateService(provider);

        _ = await service.GetElevationAsync(43.7, -79.4);
        await Task.Delay(20);
        var result = await service.GetElevationAsync(
            43.7,
            -79.4,
            new TerrainQueryOptions(MaximumAge: TimeSpan.Zero, AllowStale: true));

        Assert.Equal(TerrainQueryStatus.Stale, result.Status);
        Assert.True(result.Sample!.IsStale);
        Assert.Equal(150, result.Sample.ElevationMetres);
        Assert.Equal("STALE_CACHE", result.DiagnosticCode);
    }

    [Fact]
    public async Task Service_DeduplicatesConcurrentPointRequests()
    {
        var provider = new FakeTerrainProvider
        {
            Delay = TimeSpan.FromMilliseconds(100),
            ResponseFactory = coordinates => Success(coordinates, 150)
        };
        using var service = CreateService(provider);

        var requests = Enumerable.Range(0, 8)
            .Select(_ => service.GetElevationAsync(43.7, -79.4))
            .ToArray();
        var results = await Task.WhenAll(requests);

        Assert.All(results, result => Assert.Equal(TerrainQueryStatus.Available, result.Status));
        Assert.Equal(1, provider.CallCount);
    }

    private static readonly double[] expected = new[] { 1d, 2d, 3d };

    [Fact]
    public async Task Service_PreservesBatchOrderAndVerticalMetadata()
    {
        var coordinates = new[]
        {
            new TerrainCoordinate(43.7, -79.4),
            new TerrainCoordinate(43.8, -79.3),
            new TerrainCoordinate(43.9, -79.2)
        };
        var provider = new FakeTerrainProvider
        {
            ResponseFactory = requested => new TerrainProviderResponse(
                true,
                requested.Select((_, index) => (double?)(index + 1)).ToArray(),
                TerrainVerticalReference.Orthometric,
                "EGM96",
                30)
        };
        using var service = CreateService(provider);

        var result = await service.GetElevationsAsync(coordinates);

        Assert.Equal(TerrainQueryStatus.Available, result.Status);
        Assert.Equal(coordinates, result.Samples.Select(sample => sample.Coordinate));
        Assert.Equal(expected, result.Samples.Select(sample => sample.ElevationMetres));
        Assert.All(result.Samples, sample => Assert.Equal(TerrainVerticalReference.Orthometric, sample.VerticalReference));
        Assert.All(result.Samples, sample => Assert.Equal("EGM96", sample.VerticalDatum));
    }

    [Fact]
    public async Task Service_HonorsCancellation()
    {
        var provider = new FakeTerrainProvider { Delay = Timeout.InfiniteTimeSpan };
        using var service = CreateService(provider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetElevationAsync(43.7, -79.4, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Service_ReturnsUnavailableForNoData()
    {
        var provider = new FakeTerrainProvider
        {
            ResponseFactory = coordinates => new TerrainProviderResponse(
                true,
                coordinates.Select(_ => (double?)null).ToArray(),
                TerrainVerticalReference.ProviderNative,
                "provider-native",
                null)
        };
        using var service = CreateService(provider);

        var result = await service.GetElevationAsync(43.7, -79.4);

        Assert.Equal(TerrainQueryStatus.NoData, result.Status);
        Assert.Equal("NO_DATA", result.DiagnosticCode);
    }

    [Fact]
    public async Task Service_ConvertsProviderFailuresToUnavailableResults()
    {
        var provider = new FakeTerrainProvider
        {
            ResponseFactory = _ => throw new InvalidOperationException("provider unavailable")
        };
        using var service = CreateService(provider);

        var result = await service.GetElevationAsync(43.7, -79.4);

        Assert.Equal(TerrainQueryStatus.Unavailable, result.Status);
        Assert.Equal("PROVIDER_ERROR", result.DiagnosticCode);
    }

    [Fact]
    public async Task Service_BoundsPersistentCacheAndWritesValidJson()
    {
        var directory = CreateTempDirectory();
        try
        {
            var provider = new FakeTerrainProvider
            {
                ResponseFactory = coordinates => Success(coordinates, 150)
            };
            using var service = new TerrainElevationService(
                provider,
                new TerrainOptions(
                    ProviderId: "fake",
                    CacheMaximumEntries: 2,
                    RequestsPerSecond: 60,
                    CachePath: Path.Combine(directory, "nested", "elevation-cache.json")),
                directory);

            await service.GetElevationAsync(43.7, -79.4);
            await service.GetElevationAsync(43.8, -79.3);
            await service.GetElevationAsync(43.9, -79.2);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(service.CachePath));
            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
            Assert.InRange(document.RootElement.GetArrayLength(), 1, 2);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static TerrainElevationService CreateService(
        FakeTerrainProvider provider,
        string? directory = null)
    {
        directory ??= CreateTempDirectory();
        return new TerrainElevationService(
            provider,
            new TerrainOptions(
                ProviderId: "fake",
                CacheMaximumEntries: 10,
                RequestsPerSecond: 60,
                CachePath: Path.Combine(directory, "terrain-cache.json")),
            directory);
    }

    private static TerrainProviderResponse Success(
        IReadOnlyList<TerrainCoordinate> coordinates,
        double elevation)
        => new(
            true,
            coordinates.Select(_ => (double?)elevation).ToArray(),
            TerrainVerticalReference.ProviderNative,
            "provider-native",
            null);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LogosRobotCommandTerrain", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FakeTerrainProvider : ITerrainElevationProvider
    {
        private int _callCount;

        public string ProviderId => "fake";

        public string DisplayName => "Fake terrain";

        public int CallCount => _callCount;

        public TimeSpan Delay { get; init; }

        public Func<int, IReadOnlyList<TerrainCoordinate>, TerrainProviderResponse>? NumberedResponseFactory { get; init; }

        public Func<IReadOnlyList<TerrainCoordinate>, TerrainProviderResponse>? ResponseFactory { get; init; }

        public async Task<TerrainProviderResponse> QueryAsync(
            IReadOnlyList<TerrainCoordinate> coordinates,
            int? resolutionMetres,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (Delay == Timeout.InfiniteTimeSpan)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (NumberedResponseFactory is not null)
            {
                return NumberedResponseFactory(call, coordinates);
            }

            return ResponseFactory?.Invoke(coordinates)
                ?? Success(coordinates, 150);
        }
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
