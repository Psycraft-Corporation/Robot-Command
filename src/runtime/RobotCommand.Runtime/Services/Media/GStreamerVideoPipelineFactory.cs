using Microsoft.Extensions.Logging;
using RobotCommand.Services;

namespace RobotCommand.Services.Media;

public sealed class GStreamerVideoPipelineFactory : IGStreamerVideoPipelineFactory
{
    private readonly IGStreamerRuntime _runtime;
    private readonly AppConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;

    public GStreamerVideoPipelineFactory(
        IGStreamerRuntime runtime,
        AppConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        _runtime = runtime;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
    }

    public IGStreamerVideoPipeline Create()
        => new GStreamerVideoPipeline(
            _runtime,
            _configuration,
            _loggerFactory.CreateLogger<GStreamerVideoPipeline>());
}
