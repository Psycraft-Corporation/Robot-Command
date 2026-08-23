using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Media;
using RobotCommand.Services.Operations;
using Xunit;

namespace RobotCommand.Tests;

public sealed class NativeVideoPlaybackAdapterTests
{
    [Fact]
    public async Task AttachAsync_StartsNegotiatedRtspStreamAndPublishesLiveState()
    {
        var pipeline = new RecordingPipeline();
        await using var adapter = CreateAdapter(pipeline);

        await adapter.AttachAsync(CreateStream());
        pipeline.Publish(NativeVideoPipelineState.Playing, "playing");
        await WaitUntilAsync(() => adapter.Status.State == VideoPlaybackState.Live);

        Assert.Equal(1, pipeline.RtspStartCount);
        Assert.Equal("rtsp://camera.local:8554/front", pipeline.LastRtspOptions?.Endpoint);
        Assert.Equal("RTSP", adapter.Status.Protocol);
    }

    [Theory]
    [InlineData("Custom", "srt://camera.local:8890?streamid=read:front", NativeVideoProtocol.Srt)]
    [InlineData("Hls", "https://camera.local/front/index.m3u8", NativeVideoProtocol.Hls)]
    [InlineData("Webrtc", "https://camera.local/front/whep", NativeVideoProtocol.Whep)]
    public async Task AttachAsync_StartsAdditionalNegotiatedProtocols(
        string protocol,
        string endpoint,
        NativeVideoProtocol expected)
    {
        var pipeline = new RecordingPipeline();
        await using var adapter = CreateAdapter(pipeline);

        await adapter.AttachAsync(CreateStream(protocol, endpoint));

        Assert.Equal(expected, pipeline.LastProtocol);
        Assert.Equal(VideoPlaybackState.Connecting, adapter.Status.State);
    }

    [Fact]
    public async Task AttachAsync_MapsWhepNegotiationPayloadWithoutExposingToken()
    {
        var pipeline = new RecordingPipeline();
        await using var adapter = CreateAdapter(pipeline);
        var payload = "{\"authToken\":\"secret-token\",\"videoPayloadType\":127,\"useLinkHeaders\":true}";

        await adapter.AttachAsync(CreateStream("Webrtc", "https://camera.local/front/whep", payload));

        Assert.Equal("secret-token", pipeline.LastWhepOptions?.AuthToken);
        Assert.Equal(127, pipeline.LastWhepOptions?.PayloadType);
        Assert.DoesNotContain("secret-token", adapter.Status.Detail);
    }


    [Fact]
    public async Task AttachAsync_RejectsUnsupportedWhepCodecWithoutStartingPipeline()
    {
        var pipeline = new RecordingPipeline();
        await using var adapter = CreateAdapter(pipeline);

        await adapter.AttachAsync(CreateStream(
            "Webrtc",
            "https://camera.local/front/whep",
            codec: "theora"));

        Assert.Equal(VideoPlaybackState.Unsupported, adapter.Status.State);
        Assert.Equal(0, pipeline.WhepStartCount);
    }

    [Fact]
    public async Task AttachAsync_RejectsUnknownProtocolWithoutStartingPipeline()
    {
        var pipeline = new RecordingPipeline();
        await using var adapter = CreateAdapter(pipeline);

        await adapter.AttachAsync(CreateStream("Custom", "custom://camera/front"));

        Assert.Equal(VideoPlaybackState.Unsupported, adapter.Status.State);
        Assert.Equal(NativeVideoProtocol.Unknown, pipeline.LastProtocol);
    }


    [Fact]
    public async Task AttachAsync_MissingProtocolPlugin_IsImmediatelyUnsupported()
    {
        var pipeline = new RecordingPipeline
        {
            StartException = new InvalidOperationException(
                "The installed GStreamer runtime is missing required playback plugins: hlsdemux.")
        };
        await using var adapter = CreateAdapter(pipeline);

        await adapter.AttachAsync(CreateStream(
            "Hls",
            "https://camera.local/front/index.m3u8"));

        Assert.Equal(VideoPlaybackState.Unsupported, adapter.Status.State);
        Assert.Contains("hlsdemux", adapter.Status.Detail);
    }

    [Fact]
    public async Task PipelineFault_RestartsSameNegotiatedEndpoint()
    {
        var pipeline = new RecordingPipeline();
        await using var adapter = CreateAdapter(
            pipeline,
            new AppConfiguration
            {
                GStreamerRtspReconnectAttempts = 1,
                GStreamerRtspReconnectDelayMilliseconds = 1
            });

        await adapter.AttachAsync(CreateStream("Hls", "https://camera.local/front/index.m3u8"));
        pipeline.Publish(NativeVideoPipelineState.Playing, "playing");
        await WaitUntilAsync(() => adapter.Status.State == VideoPlaybackState.Live);

        pipeline.Publish(NativeVideoPipelineState.Faulted, "link lost", "link lost");
        await WaitUntilAsync(() => pipeline.HlsStartCount >= 2);
        pipeline.Publish(NativeVideoPipelineState.Playing, "playing again");
        await WaitUntilAsync(() => adapter.Status.State == VideoPlaybackState.Live);

        Assert.Equal(2, pipeline.HlsStartCount);
    }

    [Fact]
    public async Task PipelineFault_RedactsCredentialsAndTokensFromPlaybackStatus()
    {
        var pipeline = new RecordingPipeline();
        await using var adapter = CreateAdapter(pipeline);
        await adapter.AttachAsync(CreateStream(
            "Custom",
            "srt://camera.local:8890?streamid=read:front&passphrase=secret"));

        pipeline.Publish(
            NativeVideoPipelineState.Faulted,
            "failed",
            "Could not open srt://camera.local:8890?streamid=read:front&passphrase=secret");
        await WaitUntilAsync(() => adapter.Status.State == VideoPlaybackState.Faulted);

        Assert.DoesNotContain("passphrase=secret", adapter.Status.Detail);
        Assert.Contains("?redacted", adapter.Status.Detail);
    }

    [Fact]
    public async Task DetachAsync_StopsNativePipelineAndClearsPlaybackState()
    {
        var pipeline = new RecordingPipeline();
        await using var adapter = CreateAdapter(pipeline);
        await adapter.AttachAsync(CreateStream());

        await adapter.DetachAsync();

        Assert.True(pipeline.StopCount >= 2);
        Assert.Equal(VideoPlaybackState.Detached, adapter.Status.State);
    }

    private static NativeVideoPlaybackAdapter CreateAdapter(
        RecordingPipeline pipeline,
        AppConfiguration? configuration = null)
        => new(
            pipeline,
            configuration ?? new AppConfiguration
            {
                GStreamerRtspReconnectAttempts = 0,
                GStreamerRtspReconnectDelayMilliseconds = 1
            },
            NullLogger<NativeVideoPlaybackAdapter>.Instance);

    private static CameraStreamRecord CreateStream(
        string protocol = "Rtsp",
        string endpoint = "rtsp://camera.local:8554/front",
        string negotiationPayload = "",
        string codec = "h264")
        => new(
            "connection:stream-1", "stream-1", "front", "connection", "logos-1",
            protocol, "Active", endpoint, negotiationPayload, codec,
            1280, 720, 30, 2500, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5), string.Empty, string.Empty, DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected asynchronous state was not reached.");
            }
            await Task.Delay(10);
        }
    }

    private sealed class RecordingPipeline : IGStreamerVideoPipeline
    {
        private readonly VideoFrameBuffer _frames = new();
        public event EventHandler? Changed;
        public NativeVideoPipelineStatus Status { get; private set; } = NativeVideoPipelineStatus.Stopped;
        public IVideoFrameSource Frames => _frames;
        public NativeVideoProtocol LastProtocol { get; private set; }
        public int RtspStartCount { get; private set; }
        public int SrtStartCount { get; private set; }
        public int HlsStartCount { get; private set; }
        public int WhepStartCount { get; private set; }
        public int StopCount { get; private set; }
        public RtspPlaybackOptions? LastRtspOptions { get; private set; }
        public WhepPlaybackOptions? LastWhepOptions { get; private set; }
        public Exception? StartException { get; init; }

        public Task StartTestPatternAsync(GStreamerTestSourceOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartFileAsync(string path, GStreamerRawVideoOptions output, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartRtspAsync(RtspPlaybackOptions options, CancellationToken cancellationToken = default)
        {
            LastProtocol = NativeVideoProtocol.Rtsp; RtspStartCount++; LastRtspOptions = options; return Starting(cancellationToken);
        }
        public Task StartSrtAsync(SrtPlaybackOptions options, CancellationToken cancellationToken = default)
        {
            LastProtocol = NativeVideoProtocol.Srt; SrtStartCount++; return Starting(cancellationToken);
        }
        public Task StartHlsAsync(HlsPlaybackOptions options, CancellationToken cancellationToken = default)
        {
            LastProtocol = NativeVideoProtocol.Hls; HlsStartCount++; return Starting(cancellationToken);
        }
        public Task StartWhepAsync(WhepPlaybackOptions options, CancellationToken cancellationToken = default)
        {
            LastProtocol = NativeVideoProtocol.Whep; WhepStartCount++; LastWhepOptions = options; return Starting(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            Publish(NativeVideoPipelineState.Stopped, "stopped");
            return Task.CompletedTask;
        }

        private Task Starting(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StartException is not null)
            {
                throw StartException;
            }
            Publish(NativeVideoPipelineState.Starting, "starting");
            return Task.CompletedTask;
        }

        public void Publish(NativeVideoPipelineState state, string detail, string? error = null)
        {
            Status = new NativeVideoPipelineStatus(state, state.ToString(), detail, DateTimeOffset.UtcNow, LastError: error);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
