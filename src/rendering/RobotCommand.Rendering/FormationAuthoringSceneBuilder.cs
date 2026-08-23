using RobotCommand.Core;

namespace RobotCommand.Rendering;

/// <summary>Builds the authoring-only primitive scene from a saved or draft formation.</summary>
public static class FormationAuthoringSceneBuilder
{
    public static ThreeDSceneSnapshot Build(
        FormationWorkflowSnapshot? formation,
        string? selectedMemberId,
        ThreeDCameraSnapshot? camera = null,
        long revision = 1)
    {
        var members = formation?.Members ?? [];
        var extent = members.SelectMany(item => new[] { Math.Abs(item.EastMetres), Math.Abs(item.UpMetres), Math.Abs(item.NorthMetres) }).DefaultIfEmpty(10).Max();
        var size = Math.Clamp(Math.Ceiling(extent * 2 + 20), 40, 5000);
        var defaultDistance = Math.Max(40, extent * 2.4);
        var activeCamera = camera ?? new ThreeDCameraSnapshot(
            ThreeDProjection.OrbitPosition(ThreeDVector3.Zero, 0, ThreeDProjection.DefaultOrbitPitchDegrees, defaultDistance),
            0, ThreeDProjection.DefaultOrbitPitchDegrees, 0, 60, 0.1, 10000, 2, true, ThreeDVector3.Zero, defaultDistance);
        var primitives = new List<ThreeDPrimitiveSnapshot>
        {
            new("formation-ground", ThreeDPrimitiveKind.GroundPlane,
                new ThreeDTransform(ThreeDVector3.Zero, ThreeDVector3.Zero, new(size, 1, size)), "#111A22"),
            new("formation-origin", ThreeDPrimitiveKind.Arrow,
                ThreeDTransform.Identity with { Scale = new(1, 1, 12) }, "#FFD166", "Origin")
        };
        var lines = new List<ThreeDLineSnapshot>();
        foreach (var member in members)
        {
            var position = new ThreeDVector3(member.EastMetres, member.UpMetres, member.NorthMetres);
            primitives.Add(new($"formation-member:{member.Id}", ThreeDPrimitiveKind.Sphere,
                new(position, ThreeDVector3.Zero, new(1, 1, 1)), "#3E9FE8", member.Name, member.Id == selectedMemberId));
            lines.Add(new($"formation-guide:{member.Id}", [ThreeDVector3.Zero, position], "#557EA0", 1));
        }

        var status = new ThreeDRendererStatus("Software", false, true, false, "Authoring preview", 0, 0);
        var hud = new ThreeDHudSnapshot(string.Empty, string.Empty, string.Empty, 0, 0);
        return new ThreeDSceneSnapshot(revision, DateTimeOffset.UtcNow, new(0, 0, 0, DateTimeOffset.UtcNow), activeCamera,
            new(true, size, Math.Max(1, Math.Round(size / 20)), "#26313B"), new(false, Math.Max(10, size / 4)), primitives, lines, hud, status);
    }
}
