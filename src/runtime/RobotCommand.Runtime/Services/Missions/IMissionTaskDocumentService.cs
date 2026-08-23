using RobotCommand.Models;

namespace RobotCommand.Services.Missions;

public interface IMissionTaskDocumentService
{
    Task<MissionPackageDocument> LoadMissionAsync(string path, CancellationToken cancellationToken = default);

    Task<TaskPackageDocument> LoadTaskAsync(string path, CancellationToken cancellationToken = default);

    Task SaveMissionAsync(
        string path,
        MissionRecord mission,
        IEnumerable<OperationalTaskRecord> tasks,
        CancellationToken cancellationToken = default);

    Task SaveTaskAsync(string path, OperationalTaskRecord task, CancellationToken cancellationToken = default);

    DocumentValidationResult Validate(MissionPackageDocument mission);

    DocumentValidationResult Validate(TaskPackageDocument task);
}
