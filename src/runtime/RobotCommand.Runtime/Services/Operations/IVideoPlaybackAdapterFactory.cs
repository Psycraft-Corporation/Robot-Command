namespace RobotCommand.Services.Operations;

public sealed record VideoPlaybackSession(IVideoPlaybackAdapter Playback, RobotCommand.Services.Media.IVideoFrameSource Frames);

public interface IVideoPlaybackAdapterFactory
{
    VideoPlaybackSession Create();
}
