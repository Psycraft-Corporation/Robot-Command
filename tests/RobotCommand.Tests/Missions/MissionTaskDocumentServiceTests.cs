using RobotCommand.Models;
using RobotCommand.Services.Missions;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MissionTaskDocumentServiceTests
{
    [Fact]
    public void ValidateMission_RejectsMissingRequiredFieldsAndInvalidPayload()
    {
        var service = new MissionTaskDocumentService();
        var document = new MissionPackageDocument
        {
            SchemaVersion = "wrong",
            PayloadJson = "{not-json}"
        };

        var result = service.Validate(document);

        Assert.Equal(PlanValidationState.Invalid, result.State);
        Assert.Contains(result.Issues, item => item.Contains("schemaVersion", StringComparison.Ordinal));
        Assert.Contains(result.Issues, item => item.Contains("missionId", StringComparison.Ordinal));
        Assert.Contains(result.Issues, item => item.Contains("payloadJson", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissionRoundTrip_PreservesEmbeddedTasks()
    {
        var service = new MissionTaskDocumentService();
        var root = Path.Combine(Path.GetTempPath(), $"robot-command-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "mission.logos-mission.json");
        try
        {
            var mission = new MissionRecord(
                "mission-1",
                "Survey",
                "Draft",
                "team-1",
                "vehicle-1",
                "connection-1",
                "Survey the operating area",
                "High",
                "policy-1",
                ["area-1"],
                ["camera"],
                "{\"mode\":\"survey\"}");
            var task = new OperationalTaskRecord(
                "task-1",
                "Survey leg",
                "Draft",
                "mission-1",
                "vehicle-1",
                "connection-1",
                "team-1",
                Objective: "Fly the first leg",
                TaskType: "survey",
                BehaviourId: "survey-area",
                PackageId: "survey-package",
                ParametersJson: "{\"speed\":3}");

            await service.SaveMissionAsync(path, mission, [task]);
            var loaded = await service.LoadMissionAsync(path);

            Assert.Equal("mission-1", loaded.MissionId);
            Assert.Single(loaded.Tasks);
            Assert.Equal("task-1", loaded.Tasks[0].TaskId);
            Assert.Equal(PlanValidationState.Valid, service.Validate(loaded).State);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
