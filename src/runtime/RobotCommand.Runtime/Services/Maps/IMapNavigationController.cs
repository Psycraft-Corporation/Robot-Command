using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

/// <summary>
/// Coordinates one-shot map jumps and session-only camera following. Follow
/// targets are captured when following starts and are independent of the
/// current unit selection thereafter.
/// </summary>
public interface IMapNavigationController
{
    bool IsFollowing { get; }

    string FollowLabel { get; }

    bool CanNavigateSelection { get; }

    System.Windows.Input.ICommand JumpToSelectedCommand { get; }

    System.Windows.Input.ICommand FollowSelectedCommand { get; }

    System.Windows.Input.ICommand StopFollowingCommand { get; }

    System.Windows.Input.ICommand JumpToOperatorCommand { get; }

    event EventHandler? Changed;

    void JumpToSelected();

    void FollowSelected();

    void StopFollowing();

    void JumpToOperator();

    void UserPanned();
}
