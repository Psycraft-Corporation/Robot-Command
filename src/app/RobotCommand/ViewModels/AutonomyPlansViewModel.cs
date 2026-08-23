namespace RobotCommand.ViewModels;

public sealed class AutonomyPlansViewModel
{
    public AutonomyPlansViewModel(
        MissionsViewModel missions,
        TasksViewModel tasks,
        TaskBehaviourComposerViewModel taskComposer)
    {
        Missions = missions;
        Tasks = tasks;
        TaskComposer = taskComposer;
    }

    public MissionsViewModel Missions { get; }

    public TasksViewModel Tasks { get; }

    public TaskBehaviourComposerViewModel TaskComposer { get; }
}
