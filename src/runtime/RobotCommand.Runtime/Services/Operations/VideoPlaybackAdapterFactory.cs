using Microsoft.Extensions.Logging;
using RobotCommand.Services;
using RobotCommand.Services.Media;

namespace RobotCommand.Services.Operations;

public sealed class VideoPlaybackAdapterFactory : IVideoPlaybackAdapterFactory
{
    private readonly IGStreamerVideoPipelineFactory _pipelines;
    private readonly AppConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;

    public VideoPlaybackAdapterFactory(
        IGStreamerVideoPipelineFactory pipelines,
        AppConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        _pipelines = pipelines;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
    }

    public VideoPlaybackSession Create()
    {
        var pipeline = _pipelines.Create();
        return new VideoPlaybackSession(new NativeVideoPlaybackAdapter(
            pipeline,
            _configuration,
            _loggerFactory.CreateLogger<NativeVideoPlaybackAdapter>(),
            ownsPipeline: true), pipeline.Frames);
    }
}
