using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public interface IVideoFrameSource
{
    event EventHandler? FrameAvailable;

    VideoFrameInfo? LatestInfo { get; }

    bool TryCopyLatest(byte[] destination, out VideoFrameInfo? info);
}
