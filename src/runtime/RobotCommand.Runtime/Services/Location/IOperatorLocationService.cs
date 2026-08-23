using RobotCommand.Models;

namespace RobotCommand.Services.Location;

public interface IOperatorLocationService
{
    event EventHandler? Changed;

    OperatorLocationSnapshot Snapshot { get; }
}
