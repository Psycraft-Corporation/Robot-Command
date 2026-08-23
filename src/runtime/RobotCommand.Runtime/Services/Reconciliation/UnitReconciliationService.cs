using System.Text.Json;
using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.State;

namespace RobotCommand.Services.Reconciliation;

public sealed class UnitDefinitionService : IUnitDefinitionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly IEntityStore<string, VehicleRecord>? _vehicles;
    private readonly IEntityStore<string, CameraSourceRecord>? _cameraSources;
    private IReadOnlyList<ManualUnitDefinition> _definitions;

    public UnitDefinitionService(
        string baseDirectory,
        IEntityStore<string, VehicleRecord>? vehicles = null,
        IEntityStore<string, CameraSourceRecord>? cameraSources = null)
    {
        _path = Path.Combine(baseDirectory, "data", "units.json");
        _definitions = Load(_path);
        _vehicles = vehicles;
        _cameraSources = cameraSources;
    }

    public IReadOnlyList<UnitDefinitionSnapshot> Units
    {
        get
        {
            lock (_gate)
                return _definitions.Select(ToSnapshot).ToArray();
        }
    }

    public IReadOnlyList<ManualUnitDefinition> Definitions
    {
        get
        {
            lock (_gate) return _definitions.ToArray();
        }
    }

    public event EventHandler? Changed;

    public bool TryGet(string unitId, out UnitDefinitionSnapshot? definition)
    {
        definition = Units.FirstOrDefault(item => string.Equals(item.Id, unitId, StringComparison.Ordinal));
        return definition is not null;
    }

    public UnitDefinitionSnapshot? FindByVehicle(string vehicleId)
    {
        lock (_gate)
        {
            var definition = _definitions.FirstOrDefault(item => EffectiveBindings(item).Any(source =>
                string.Equals(source.VehicleId, vehicleId, StringComparison.Ordinal)));
            return definition is null ? null : ToSnapshot(definition);
        }
    }

    public UnitDefinitionSnapshot? FindByVehicleBinding(string connectionId, string vehicleId)
    {
        lock (_gate)
        {
            var definition = _definitions.FirstOrDefault(item => EffectiveBindings(item).Any(source =>
                string.Equals(source.ConnectionId, connectionId, StringComparison.Ordinal) &&
                string.Equals(source.VehicleId, vehicleId, StringComparison.Ordinal)));
            return definition is null ? null : ToSnapshot(definition);
        }
    }

    public async Task<UnitDefinitionSnapshot> SaveAsync(
        string? unitId,
        UnitDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bindings = request.VehicleSources
            .Where(item => !string.IsNullOrWhiteSpace(item.ConnectionId) && !string.IsNullOrWhiteSpace(item.VehicleId))
            .Distinct()
            .ToArray();
        var cameras = (request.CameraSources ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.ConnectionId) && !string.IsNullOrWhiteSpace(item.CameraSourceId))
            .Distinct()
            .ToArray();
        var connectionIds = (request.ConnectionIds ?? [])
            .Concat(bindings.Select(item => item.ConnectionId))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (string.IsNullOrWhiteSpace(request.DisplayName)) throw new InvalidOperationException("Enter a unit name.");
        if (request.DisplayName.Trim().Length > 120) throw new InvalidOperationException("Unit names cannot exceed 120 characters.");
        if (connectionIds.Length == 0) throw new InvalidOperationException("Select at least one connection.");
        if (bindings.Length != request.VehicleSources.Count || cameras.Length != (request.CameraSources?.Count ?? 0))
            throw new InvalidOperationException("A unit cannot contain duplicate or empty sources.");

        var command = request.CommandAuthorityConnectionId ?? connectionIds[0];
        var telemetry = request.TelemetryAuthorityConnectionId ?? connectionIds[0];
        var diagnostics = request.DiagnosticsAuthorityConnectionId ?? connectionIds[0];
        ValidateAuthority("command", command, connectionIds);
        ValidateAuthority("telemetry", telemetry, connectionIds);
        ValidateAuthority("diagnostics", diagnostics, connectionIds);

        ManualUnitDefinition saved;
        IReadOnlyList<ManualUnitDefinition> next;
        lock (_gate)
        {
            var id = string.IsNullOrWhiteSpace(unitId) ? $"unit-{Guid.NewGuid():N}" : unitId.Trim();
            var conflict = _definitions.FirstOrDefault(existing =>
                !string.Equals(existing.Id, id, StringComparison.Ordinal) &&
                (DeclaredConnectionIds(existing).Intersect(connectionIds, StringComparer.Ordinal).Any() ||
                 (existing.CameraSources ?? []).Any(binding => cameras.Contains(binding))));
            if (conflict is not null)
                throw new InvalidOperationException($"One of these sources is already associated with '{conflict.DisplayName}'. Remove it there first.");

            saved = new ManualUnitDefinition(id, request.DisplayName.Trim(),
                bindings.Select(item => new UnitConnectionBinding(item.ConnectionId, item.VehicleId))
                    .OrderBy(item => item.ConnectionId, StringComparer.Ordinal).ThenBy(item => item.VehicleId, StringComparer.Ordinal).ToArray(),
                command, telemetry, diagnostics, DateTimeOffset.UtcNow, cameras, connectionIds);
            next = _definitions.Where(item => item.Id != saved.Id).Append(saved).ToArray();
        }

        await PersistAsync(next, cancellationToken);
        lock (_gate) _definitions = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return ToSnapshot(saved);
    }

    public Task DeleteAsync(string unitId, CancellationToken cancellationToken = default)
        => RemoveAsync(unitId, cancellationToken);

    public ManualUnitDefinition? FindBySource(string sourceVehicleId)
    {
        if (string.IsNullOrWhiteSpace(sourceVehicleId)) return null;
        lock (_gate)
            return _definitions.FirstOrDefault(item =>
                EffectiveBindings(item).Any(binding =>
                    string.Equals(binding.VehicleId, sourceVehicleId, StringComparison.Ordinal)));
    }

    public ManualUnitDefinition? FindByBinding(string connectionId, string vehicleId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(vehicleId)) return null;
        lock (_gate)
            return _definitions.FirstOrDefault(item => EffectiveBindings(item).Any(binding =>
                string.Equals(binding.ConnectionId, connectionId, StringComparison.Ordinal) &&
                string.Equals(binding.VehicleId, vehicleId, StringComparison.Ordinal)));
    }

    public string ResolveCommandSource(string sourceVehicleId)
        => Resolve(sourceVehicleId, item => VehicleForConnection(EffectiveBindings(item), item.CommandAuthorityConnectionId));

    public string ResolveTelemetrySource(string sourceVehicleId)
        => Resolve(sourceVehicleId, item => VehicleForConnection(EffectiveBindings(item), item.TelemetryAuthorityConnectionId));

    public string ResolveDiagnosticsSource(string sourceVehicleId)
        => Resolve(sourceVehicleId, item => VehicleForConnection(EffectiveBindings(item), item.DiagnosticsAuthorityConnectionId));

    public string DisplayNameFor(string sourceVehicleId, string fallback)
        => FindBySource(sourceVehicleId)?.DisplayName is { Length: > 0 } name ? name : fallback;

    public IReadOnlyList<VehicleRecord> ProjectVehicles(IReadOnlyList<VehicleRecord> vehicles)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        var byId = vehicles.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var definitions = Definitions;
        var effectiveBindings = definitions.ToDictionary(item => item.Id, item => EffectiveBindings(item, vehicles));
        var associatedIds = effectiveBindings.Values.SelectMany(item => item.Select(binding => binding.VehicleId))
            .ToHashSet(StringComparer.Ordinal);
        var result = vehicles.Where(item => !associatedIds.Contains(item.Id)).ToList();

        foreach (var definition in definitions)
        {
            var sources = effectiveBindings[definition.Id].Select(binding => binding.VehicleId)
                .Select(id => byId.GetValueOrDefault(id))
                .Where(item => item is not null)
                .Cast<VehicleRecord>()
                .ToArray();
            if (sources.Length == 0) continue;

            var representativeId = VehicleForConnection(effectiveBindings[definition.Id], definition.CommandAuthorityConnectionId);
            var representative = byId.GetValueOrDefault(representativeId) ?? sources[0];
            result.Add(representative with
            {
                Name = definition.DisplayName,
                ConnectionIds = DeclaredConnectionIds(definition)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray()
            });
        }

        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<RuntimeRecord> ProjectRuntimes(
        IReadOnlyList<RuntimeRecord> runtimes,
        IReadOnlyList<VehicleRecord> vehicles)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(vehicles);
        var definitions = Definitions;
        var effectiveBindings = definitions.ToDictionary(item => item.Id, item => EffectiveBindings(item, vehicles));
        var associatedIds = effectiveBindings.Values.SelectMany(item => item.Select(binding => binding.VehicleId))
            .ToHashSet(StringComparer.Ordinal);
        var result = runtimes.Where(item => item.VehicleId is null || !associatedIds.Contains(item.VehicleId)).ToList();

        foreach (var definition in definitions)
        {
            var sourceRuntimes = runtimes.Where(item =>
                    item.VehicleId is not null &&
                    effectiveBindings[definition.Id].Any(binding => binding.VehicleId == item.VehicleId))
                .ToArray();
            if (sourceRuntimes.Length == 0) continue;

            var representative = sourceRuntimes.FirstOrDefault(item =>
                                     string.Equals(item.VehicleId, VehicleForConnection(effectiveBindings[definition.Id], definition.CommandAuthorityConnectionId),
                                         StringComparison.Ordinal))
                                 ?? sourceRuntimes[0];
            result.Add(representative with
            {
                Name = definition.DisplayName,
                VehicleName = definition.DisplayName,
                VehicleId = VehicleForConnection(effectiveBindings[definition.Id], definition.CommandAuthorityConnectionId),
                ConnectionIds = DeclaredConnectionIds(definition)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray()
            });
        }

        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ManualUnitDefinition> SaveAsync(
        string? unitId,
        string displayName,
        IReadOnlyList<UnitConnectionBinding> connections,
        string commandConnectionId,
        string telemetryConnectionId,
        string diagnosticsConnectionId,
        CancellationToken cancellationToken = default)
    {
        var bindings = connections
            .Where(item => !string.IsNullOrWhiteSpace(item.ConnectionId) && !string.IsNullOrWhiteSpace(item.VehicleId))
            .Distinct()
            .ToArray();
        if (bindings.Length == 0)
            throw new InvalidOperationException("A manually defined unit requires at least one vehicle source.");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new InvalidOperationException("Enter a unit name.");
        ValidateConnection("command authority", commandConnectionId, bindings);
        ValidateConnection("telemetry authority", telemetryConnectionId, bindings);
        ValidateConnection("diagnostics authority", diagnosticsConnectionId, bindings);

        ManualUnitDefinition saved;
        IReadOnlyList<ManualUnitDefinition> next;
        lock (_gate)
        {
            var conflict = _definitions.FirstOrDefault(existing =>
                !string.Equals(existing.Id, unitId, StringComparison.Ordinal) &&
                existing.Connections.Any(existingBinding => bindings.Contains(existingBinding)));
            if (conflict is not null)
                throw new InvalidOperationException(
                    $"One of these connection observations is already associated with '{conflict.DisplayName}'. Remove it there first.");

            saved = new ManualUnitDefinition(
                string.IsNullOrWhiteSpace(unitId) ? $"unit-{Guid.NewGuid():N}" : unitId,
                displayName.Trim(),
                bindings.OrderBy(item => item.ConnectionId, StringComparer.Ordinal).ToArray(),
                commandConnectionId,
                telemetryConnectionId,
                diagnosticsConnectionId,
                DateTimeOffset.UtcNow);
            next = _definitions.Where(item => item.Id != saved.Id).Append(saved).ToArray();
        }

        await PersistAsync(next, cancellationToken);
        lock (_gate) _definitions = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public async Task RemoveAsync(string associationId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ManualUnitDefinition> next;
        lock (_gate) next = _definitions.Where(item => item.Id != associationId).ToArray();
        await PersistAsync(next, cancellationToken);
        lock (_gate) _definitions = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string Resolve(string sourceVehicleId, Func<ManualUnitDefinition, string> selector)
        => FindBySource(sourceVehicleId) is { } definition ? selector(definition) : sourceVehicleId;

    private static string VehicleForConnection(
        IReadOnlyList<UnitConnectionBinding> bindings,
        string connectionId)
        => bindings.FirstOrDefault(item => string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal))?.VehicleId
            ?? (bindings.Count > 0 ? bindings[0].VehicleId : null)
            ?? string.Empty;

    private static void ValidateConnection(
        string kind,
        string connectionId,
        IReadOnlyList<UnitConnectionBinding> available)
    {
        if (!available.Any(item => item.ConnectionId == connectionId))
            throw new InvalidOperationException($"The selected {kind} connection is not part of this unit.");
    }

    private static void ValidateAuthority(string role, string connectionId, IReadOnlyList<string> connectionIds)
    {
        if (!connectionIds.Contains(connectionId, StringComparer.Ordinal))
            throw new InvalidOperationException($"The selected {role} authority is not part of this unit.");
    }

    private static string[] DeclaredConnectionIds(ManualUnitDefinition definition)
        => (definition.ConnectionIds ?? [])
            .Concat(definition.Connections.Select(item => item.ConnectionId))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    private IReadOnlyList<UnitConnectionBinding> EffectiveBindings(
        ManualUnitDefinition definition,
        IReadOnlyList<VehicleRecord>? vehicles = null)
    {
        var declared = DeclaredConnectionIds(definition);
        var bindings = definition.Connections.ToList();
        IReadOnlyList<VehicleRecord> availableVehicles = vehicles ?? (_vehicles?.Items.ToArray() ?? Array.Empty<VehicleRecord>());
        foreach (var vehicle in availableVehicles)
        {
            foreach (var connectionId in vehicle.ConnectionIds.Where(declared.Contains))
            {
                if (bindings.Any(item => item.ConnectionId == connectionId && item.VehicleId == vehicle.Id)) continue;
                bindings.Add(new UnitConnectionBinding(connectionId, vehicle.Id));
            }
        }

        return bindings
            .Distinct()
            .OrderBy(item => item.ConnectionId, StringComparer.Ordinal)
            .ThenBy(item => item.VehicleId, StringComparer.Ordinal)
            .ToArray();
    }

    private UnitDefinitionSnapshot ToSnapshot(ManualUnitDefinition definition)
    {
        var vehicleSources = EffectiveBindings(definition)
            .Select(item => new UnitVehicleSourceBinding(item.ConnectionId, item.VehicleId))
            .ToArray();
        var cameraSources = definition.CameraSources ?? [];
        var statuses = vehicleSources.Select(item =>
        {
            var vehicle = _vehicles?.Items.FirstOrDefault(candidate => candidate.Id == item.VehicleId);
            return new UnitSourceStatus(UnitSourceKind.Vehicle, item.ConnectionId, item.VehicleId,
                vehicle?.Name ?? item.VehicleId, vehicle is not null, vehicle is null ? "Unavailable" : vehicle.Health);
        }).Concat(cameraSources.Select(item =>
        {
            var camera = _cameraSources?.Items.FirstOrDefault(candidate => candidate.ConnectionId == item.ConnectionId && candidate.CameraSourceId == item.CameraSourceId);
            return new UnitSourceStatus(UnitSourceKind.Camera, item.ConnectionId, item.CameraSourceId,
                camera?.Name ?? item.CameraSourceId, camera is not null, camera is null ? "Unavailable" : camera.Health);
        })).ToArray();
        return new UnitDefinitionSnapshot(definition.Id, definition.DisplayName, vehicleSources, cameraSources,
            definition.CommandAuthorityConnectionId, definition.TelemetryAuthorityConnectionId,
            definition.DiagnosticsAuthorityConnectionId, definition.UpdatedAt, statuses,
            DeclaredConnectionIds(definition));
    }

    private async Task PersistAsync(IReadOnlyList<ManualUnitDefinition> definitions, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new UnitLibraryDocument("fieldconsole.units.v1", definitions), JsonOptions), cancellationToken);
        File.Move(temporary, _path, true);
    }

    private static IReadOnlyList<ManualUnitDefinition> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var text = File.ReadAllText(path);
            IReadOnlyList<ManualUnitDefinition> definitions;
            try
            {
                definitions = JsonSerializer.Deserialize<UnitLibraryDocument>(text, JsonOptions)?.Units
                    ?? throw new JsonException("Missing units document.");
            }
            catch (JsonException)
            {
                definitions = JsonSerializer.Deserialize<ManualUnitDefinition[]>(text, JsonOptions) ?? [];
            }
            return definitions
                .Where(item => DeclaredConnectionIds(item).Length >= 1)
                .Where(item => DeclaredConnectionIds(item).Contains(item.CommandAuthorityConnectionId, StringComparer.Ordinal))
                .Where(item => DeclaredConnectionIds(item).Contains(item.TelemetryAuthorityConnectionId, StringComparer.Ordinal))
                .Where(item => DeclaredConnectionIds(item).Contains(item.DiagnosticsAuthorityConnectionId, StringComparer.Ordinal))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private sealed record UnitLibraryDocument(string Schema, IReadOnlyList<ManualUnitDefinition> Units);
}
