using System.Text.Json;
using RobotCommand.Models;

namespace RobotCommand.Services.Missions;

public sealed class MissionTaskDocumentService : IMissionTaskDocumentService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public async Task<MissionPackageDocument> LoadMissionAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MissionPackageDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Mission document was empty.");
    }

    public async Task<TaskPackageDocument> LoadTaskAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TaskPackageDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Task document was empty.");
    }

    public async Task SaveMissionAsync(
        string path,
        MissionRecord mission,
        IEnumerable<OperationalTaskRecord> tasks,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        EnsureParentDirectory(path);
        var document = new MissionPackageDocument
        {
            MissionId = mission.Id,
            Name = mission.Name,
            Objective = mission.Objective,
            Priority = mission.Priority,
            PolicyId = mission.PolicyId,
            TeamId = mission.AssignedTeamId,
            VehicleId = mission.AssignedVehicleId,
            ConnectionId = mission.ConnectionId,
            GeometryIds = mission.GeometryIds?.ToList() ?? [],
            RequiredCapabilities = mission.RequiredCapabilities?.ToList() ?? [],
            PayloadJson = mission.PayloadJson,
            Tasks = tasks.Select(ToDocument).ToList()
        };

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
    }

    public async Task SaveTaskAsync(
        string path,
        OperationalTaskRecord task,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        EnsureParentDirectory(path);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, ToDocument(task), JsonOptions, cancellationToken);
    }

    public DocumentValidationResult Validate(MissionPackageDocument mission)
    {
        var issues = new List<string>();
        if (!string.Equals(mission.SchemaVersion, "logos.mission.v1", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("schemaVersion must be 'logos.mission.v1'.");
        }

        Require(mission.MissionId, "missionId", issues);
        Require(mission.Name, "name", issues);
        Require(mission.Objective, "objective", issues);
        ValidateJson(mission.PayloadJson, "payloadJson", issues);
        ValidateGeometryIds(mission.GeometryIds, "geometryIds", issues);

        foreach (var task in mission.Tasks)
        {
            var result = Validate(task);
            issues.AddRange(result.Issues.Select(issue => $"Task '{task.TaskId}': {issue}"));
        }

        return FromIssues(issues, "Mission document is structurally valid.");
    }

    public DocumentValidationResult Validate(TaskPackageDocument task)
    {
        var issues = new List<string>();
        if (!string.Equals(task.SchemaVersion, "logos.task.v1", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("schemaVersion must be 'logos.task.v1'.");
        }

        Require(task.TaskId, "taskId", issues);
        Require(task.Name, "name", issues);
        Require(task.Objective, "objective", issues);
        Require(task.TaskType, "taskType", issues);
        Require(task.BehaviourId, "behaviourId", issues);
        ValidateGeometryIds(task.GeometryIds, "geometryIds", issues);
        ValidateJson(task.ParametersJson, "parametersJson", issues);
        return FromIssues(issues, "Task document is structurally valid.");
    }

    private static TaskPackageDocument ToDocument(OperationalTaskRecord task) => new()
    {
        TaskId = task.Id,
        Name = task.Name,
        MissionId = task.MissionId,
        TeamId = task.TeamId,
        ConnectionId = task.ConnectionId,
        AssignedVehicleId = task.AssignedVehicleId,
        AssignedMemberId = task.AssignedMemberId,
        AssignedLogosInstanceId = task.AssignedLogosInstanceId,
        Objective = task.Objective,
        TaskType = task.TaskType,
        Priority = task.Priority,
        BehaviourId = task.BehaviourId,
        BehaviourVersion = task.BehaviourVersion,
        PackageId = task.PackageId,
        GeometryIds = task.GeometryIds?.ToList() ?? [],
        ParametersJson = task.ParametersJson
    };

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A document path is required.", nameof(path));
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static void Require(string? value, string name, ICollection<string> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add($"{name} is required.");
        }
    }

    private static void ValidateGeometryIds(
        IReadOnlyList<string>? geometryIds,
        string name,
        ICollection<string> issues)
    {
        geometryIds ??= [];
        if (geometryIds.Any(string.IsNullOrWhiteSpace))
        {
            issues.Add($"{name} cannot contain an empty geometry ID.");
        }

        var duplicate = geometryIds
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .GroupBy(item => item, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            issues.Add($"{name} contains duplicate geometry ID '{duplicate.Key}'.");
        }
    }

    private static void ValidateJson(string? value, string name, ICollection<string> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException ex)
        {
            issues.Add($"{name} is not valid JSON: {ex.Message}");
        }
    }

    private static DocumentValidationResult FromIssues(IReadOnlyList<string> issues, string validSummary)
        => issues.Count == 0
            ? new DocumentValidationResult(PlanValidationState.Valid, validSummary, [])
            : new DocumentValidationResult(
                PlanValidationState.Invalid,
                $"{issues.Count} validation issue(s).",
                issues);
}
