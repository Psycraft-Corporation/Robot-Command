using RobotCommand.Core;
using RobotCommand.Models;

namespace RobotCommand.Services.Workflows;

public sealed class FormationAuthoringWorkflow : IFormationAuthoringWorkflow, IDisposable
{
    private readonly FormationLibraryStore _store;
    public FormationAuthoringWorkflow(FormationLibraryStore store) { _store = store; _store.Changed += OnChanged; }
    public event EventHandler? Changed;
    public IReadOnlyList<FormationWorkflowSnapshot> Formations => _store.Documents.Select(Map).ToArray();
    public IReadOnlyList<string> LibraryIssues => _store.Issues;
    public bool TryGet(string id, out FormationWorkflowSnapshot? formation)
    {
        formation = _store.Documents.FirstOrDefault(item => item.FormationId == id) is { } document ? Map(document) : null;
        return formation is not null;
    }
    public async Task<FormationWorkflowSnapshot> CreateAsync(FormationCreateRequest request, CancellationToken cancellationToken = default)
    {
        var name = RequireName(request.Name);
        if (_store.Documents.Any(item => item.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"Formation '{name}' already exists.");
        var now = DateTimeOffset.UtcNow;
        var document = new FormationDocument(FormationDocument.CurrentSchemaVersion, CreateId(name), name, now, now,
            []);
        return Map(await _store.SaveAsync(document, cancellationToken, allowEmpty: true));
    }
    public async Task<FormationWorkflowSnapshot> SaveAsync(FormationWorkflowSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (!_store.TryGet(snapshot.Id, out var existing) || existing is null) throw new KeyNotFoundException("Formation was not found.");
        EnsureUniqueName(snapshot.Name, snapshot.Id);
        var document = new FormationDocument(FormationDocument.CurrentSchemaVersion, snapshot.Id, RequireName(snapshot.Name), existing.CreatedAt,
            snapshot.UpdatedAt, snapshot.Members.Select(item => new FormationMember(item.Id, item.Name, item.EastMetres, item.UpMetres, item.NorthMetres)).ToArray());
        return Map(await _store.SaveAsync(document, cancellationToken));
    }
    public Task<FormationWorkflowSnapshot> RenameAsync(string formationId, string name, CancellationToken cancellationToken = default)
        => MutateAsync(formationId, document =>
        {
            var normalized = RequireName(name);
            EnsureUniqueName(normalized, formationId);
            return document with { DisplayName = normalized };
        }, cancellationToken);
    public Task<FormationWorkflowSnapshot> AddMemberAsync(string formationId, FormationMemberRequest request, CancellationToken cancellationToken = default)
        => MutateAsync(formationId, document =>
        {
            var index = document.Members.Count + 1;
            var memberName = string.IsNullOrWhiteSpace(request.Name) ? $"Unit {index}" : request.Name.Trim();
            var suffix = index;
            while (document.Members.Any(item => item.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase))) memberName = $"Unit {++suffix}";
            var memberId = $"unit-{Guid.NewGuid():N}";
            return document with { Members = document.Members.Append(new(memberId, memberName, request.EastMetres, request.UpMetres, request.NorthMetres)).ToArray() };
        }, cancellationToken);
    public Task<FormationWorkflowSnapshot> UpdateMemberAsync(string formationId, string memberId, FormationMemberRequest request, CancellationToken cancellationToken = default)
        => MutateAsync(formationId, document =>
        {
            var member = document.Members.FirstOrDefault(item => item.Id == memberId) ?? throw new KeyNotFoundException("Formation member was not found.");
            var name = string.IsNullOrWhiteSpace(request.Name) ? member.Name : request.Name.Trim();
            return document with { Members = document.Members.Select(item => item.Id == memberId ? member with { Name = name, EastMetres = request.EastMetres, UpMetres = request.UpMetres, NorthMetres = request.NorthMetres } : item).ToArray() };
        }, cancellationToken);
    public Task<FormationWorkflowSnapshot> RemoveMemberAsync(string formationId, string memberId, CancellationToken cancellationToken = default)
        => MutateAsync(formationId, document => document with { Members = document.Members.Where(item => item.Id != memberId).ToArray() }, cancellationToken);
    public async Task RemoveAsync(string formationId, CancellationToken cancellationToken = default)
    {
        if (!_store.TryGet(formationId, out _)) throw new KeyNotFoundException("Formation was not found.");
        await _store.RemoveAsync(formationId, cancellationToken);
    }
    private async Task<FormationWorkflowSnapshot> MutateAsync(string id, Func<FormationDocument, FormationDocument> mutate, CancellationToken token)
    {
        if (!_store.TryGet(id, out var document) || document is null) throw new KeyNotFoundException("Formation was not found.");
        return Map(await _store.SaveAsync(mutate(document), token));
    }
    private static FormationWorkflowSnapshot Map(FormationDocument document) => new(document.FormationId, document.DisplayName, document.CreatedAt, document.UpdatedAt, document.Members.Select(item => new FormationMemberSnapshot(item.Id, item.Name, item.EastMetres, item.UpMetres, item.NorthMetres)).ToArray());
    private static string RequireName(string name) { var value = name?.Trim() ?? string.Empty; if (value.Length == 0 || value.Length > 120) throw new ArgumentException("Formation name is required and must be 120 characters or fewer."); return value; }
    private string CreateId(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(value => char.IsLetterOrDigit(value) ? value : '-').ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(slug)) slug = "formation";
        var id = slug; var suffix = 2;
        while (_store.Documents.Any(item => item.FormationId == id)) id = $"{slug}-{suffix++}";
        return id;
    }
    private void EnsureUniqueName(string name, string? exceptId = null)
    {
        var normalized = RequireName(name);
        if (_store.Documents.Any(item => item.FormationId != exceptId && item.DisplayName.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Formation '{normalized}' already exists.");
    }
    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke(this, e);
    public void Dispose() => _store.Changed -= OnChanged;
}
