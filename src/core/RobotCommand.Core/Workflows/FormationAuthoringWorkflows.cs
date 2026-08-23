namespace RobotCommand.Core;

public sealed record FormationMember(
    string Id,
    string Name,
    double EastMetres,
    double UpMetres,
    double NorthMetres);

public sealed record FormationDocument(
    string SchemaVersion,
    string FormationId,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<FormationMember> Members)
{
    public const string CurrentSchemaVersion = "robotcommand.formation.v1";
}

public sealed record FormationMemberSnapshot(
    string Id,
    string Name,
    double EastMetres,
    double UpMetres,
    double NorthMetres);

public sealed record FormationWorkflowSnapshot(
    string Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<FormationMemberSnapshot> Members);

public sealed record FormationCreateRequest(string Name);

public sealed record FormationMemberRequest(
    string? Name,
    double EastMetres,
    double UpMetres,
    double NorthMetres);

public interface IFormationAuthoringWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<FormationWorkflowSnapshot> Formations { get; }
    IReadOnlyList<string> LibraryIssues { get; }
    bool TryGet(string id, out FormationWorkflowSnapshot? formation);
    Task<FormationWorkflowSnapshot> CreateAsync(FormationCreateRequest request, CancellationToken cancellationToken = default);
    Task<FormationWorkflowSnapshot> SaveAsync(FormationWorkflowSnapshot formation, CancellationToken cancellationToken = default);
    Task<FormationWorkflowSnapshot> RenameAsync(string formationId, string name, CancellationToken cancellationToken = default);
    Task<FormationWorkflowSnapshot> AddMemberAsync(string formationId, FormationMemberRequest request, CancellationToken cancellationToken = default);
    Task<FormationWorkflowSnapshot> UpdateMemberAsync(string formationId, string memberId, FormationMemberRequest request, CancellationToken cancellationToken = default);
    Task<FormationWorkflowSnapshot> RemoveMemberAsync(string formationId, string memberId, CancellationToken cancellationToken = default);
    Task RemoveAsync(string formationId, CancellationToken cancellationToken = default);
}
