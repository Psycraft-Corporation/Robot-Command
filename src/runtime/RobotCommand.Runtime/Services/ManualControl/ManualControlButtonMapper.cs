using RobotCommand.Models;

namespace RobotCommand.Services.ManualControl;

/// <summary>
/// Maps controller edges to the command queue actions.  The mapper is kept
/// separate from polling so the same mapping can be tested without a Windows
/// gamepad and can later be reused by another input device.
/// </summary>
public static class ManualControlButtonMapper
{
    public static OperatorCommandKind? QueuedCommandFor(
        bool aPressed,
        bool rightBumperPressed,
        bool yPressed,
        bool armed,
        bool landed)
    {
        if (aPressed)
            return OperatorCommandKind.Hold;

        if (rightBumperPressed)
            return armed ? OperatorCommandKind.Disarm : OperatorCommandKind.Arm;

        if (yPressed)
            return landed ? OperatorCommandKind.Takeoff : OperatorCommandKind.Land;

        return null;
    }
}
