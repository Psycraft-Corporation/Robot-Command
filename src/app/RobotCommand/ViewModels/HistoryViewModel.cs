namespace RobotCommand.ViewModels;

/// <summary>
/// Presents the command audit trail and operational event stream as one workspace.
/// The child view models retain their existing live-store projections.
/// </summary>
public sealed class HistoryViewModel
{
    public HistoryViewModel(CommandsViewModel commands, EventsViewModel events)
    {
        Commands = commands;
        Events = events;
    }

    public CommandsViewModel Commands { get; }

    public EventsViewModel Events { get; }
}
