using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Rendering;
using RobotCommand.Services.Workflows;
using Xunit;

namespace RobotCommand.Tests;

public sealed class FormationAuthoringTests
{
    [Fact]
    public async Task WorkflowPersistsAndMutatesOrderedMembers()
    {
        var root = Path.Combine(Path.GetTempPath(), "robotcommand-formation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FormationLibraryStore(root);
            var workflow = new FormationAuthoringWorkflow(store);
            var created = await workflow.CreateAsync(new FormationCreateRequest("Line"));
            Assert.Empty(created.Members);
            var added = await workflow.AddMemberAsync(created.Id, new FormationMemberRequest("Unit 2", 10, 5, 20));
            Assert.Equal(["Unit 2"], added.Members.Select(item => item.Name));
            var member = added.Members[0];
            var updated = await workflow.UpdateMemberAsync(created.Id, member.Id, new FormationMemberRequest("Right", 12, 6, 22));
            Assert.Equal((12, 6, 22), (updated.Members[0].EastMetres, updated.Members[0].UpMetres, updated.Members[0].NorthMetres));
            var reloaded = new FormationLibraryStore(root);
            Assert.True(reloaded.TryGet(created.Id, out var document));
            Assert.Equal("Right", document!.Members[0].Name);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AuthoringSceneKeepsOriginAndStableMemberIds()
    {
        var formation = new FormationWorkflowSnapshot("line", "Line", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new("unit-1", "Unit 1", 10, 5, 20)]);
        var scene = FormationAuthoringSceneBuilder.Build(formation, "unit-1");
        Assert.Equal(ThreeDProjection.DefaultOrbitPitchDegrees, scene.Camera.PitchDegrees);
        Assert.Contains(scene.Primitives, item => item.Id == "formation-origin" && item.Transform.Position == ThreeDVector3.Zero);
        var member = Assert.Single(scene.Primitives.Where(item => item.Id == "formation-member:unit-1"));
        Assert.True(member.Selected);
        Assert.Equal(new ThreeDVector3(10, 5, 20), member.Transform.Position);
        Assert.Contains(scene.Lines, item => item.Id == "formation-guide:unit-1");
    }

    [Fact]
    public void InvalidFormationCoordinatesAreRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var document = new FormationDocument(FormationDocument.CurrentSchemaVersion, "bad", "Bad", now, now,
            [new("unit-1", "Unit 1", double.NaN, 0, 0)]);
        Assert.Throws<InvalidDataException>(() => FormationLibraryStore.Validate(document));
    }
}
