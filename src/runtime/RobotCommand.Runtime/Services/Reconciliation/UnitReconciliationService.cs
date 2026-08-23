using System.Text.Json;
using RobotCommand.Models;

namespace RobotCommand.Services.Reconciliation;

public sealed class UnitDefinitionService : IUnitDefinitionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private IReadOnlyList<ManualUnitDefinition> _definitions;

    public UnitDefinitionService(string baseDirectory)
    {
        _path = Path.Combine(baseDirectory, "data", "units.json");
        _definitions = Load(_path);
    }

    public IReadOnlyList<ManualUnitDefinition> Definitions
    {
        get
        {
            lock (_gate) return _definitions.ToArray();
        }
    }

    public event EventHandler? Changed;

    public ManualUnitDefinition? FindBySource(string sourceVehicleId)
    {
        if (string.IsNullOrWhiteSpace(sourceVehicleId)) return null;
        lock (_gate)
            return _definitions.FirstOrDefault(item =>
                item.Connections.Any(binding =>
                    string.Equals(binding.VehicleId, sourceVehicleId, StringComparison.Ordinal)));
    }

    public ManualUnitDefinition? FindByBinding(string connectionId, string vehicleId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(vehicleId)) return null;
        lock (_gate)
            return _definitions.FirstOrDefault(item => item.Connections.Any(binding =>
                string.Equals(binding.ConnectionId, connectionId, StringComparison.Ordinal) &&
                string.Equals(binding.VehicleId, vehicleId, StringComparison.Ordinal)));
    }

    public string ResolveCommandSource(string sourceVehicleId)
        => Resolve(sourceVehicleId, item => VehicleForConnection(item, item.CommandAuthorityConnectionId));

    public string ResolveTelemetrySource(string sourceVehicleId)
        => Resolve(sourceVehicleId, item => VehicleForConnection(item, item.TelemetryAuthorityConnectionId));

    public string ResolveDiagnosticsSource(string sourceVehicleId)
        => Resolve(sourceVehicleId, item => VehicleForConnection(item, item.DiagnosticsAuthorityConnectionId));

    public string DisplayNameFor(string sourceVehicleId, string fallback)
        => FindBySource(sourceVehicleId)?.DisplayName is { Length: > 0 } name ? name : fallback;

    public IReadOnlyList<VehicleRecord> ProjectVehicles(IReadOnlyList<VehicleRecord> vehicles)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        var byId = vehicles.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var definitions = Definitions;
        var associatedIds = definitions.SelectMany(item => item.Connections.Select(binding => binding.VehicleId))
            .ToHashSet(StringComparer.Ordinal);
        var result = vehicles.Where(item => !associatedIds.Contains(item.Id)).ToList();

        foreach (var definition in definitions)
        {
            var sources = definition.Connections.Select(binding => binding.VehicleId)
                .Select(id => byId.GetValueOrDefault(id))
                .Where(item => item is not null)
                .Cast<VehicleRecord>()
                .ToArray();
            if (sources.Length == 0) continue;

            var representativeId = VehicleForConnection(definition, definition.CommandAuthorityConnectionId);
            var representative = byId.GetValueOrDefault(representativeId) ?? sources[0];
            result.Add(representative with
            {
                Name = definition.DisplayName,
                ConnectionIds = definition.Connections.Select(item => item.ConnectionId)
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
        var associatedIds = definitions.SelectMany(item => item.Connections.Select(binding => binding.VehicleId))
            .ToHashSet(StringComparer.Ordinal);
        var result = runtimes.Where(item => item.VehicleId is null || !associatedIds.Contains(item.VehicleId)).ToList();

        foreach (var definition in definitions)
        {
            var sourceRuntimes = runtimes.Where(item =>
                    item.VehicleId is not null &&
                    definition.Connections.Any(binding => binding.VehicleId == item.VehicleId))
                .ToArray();
            if (sourceRuntimes.Length == 0) continue;

            var representative = sourceRuntimes.FirstOrDefault(item =>
                                     string.Equals(item.VehicleId, VehicleForConnection(definition, definition.CommandAuthorityConnectionId),
                                         StringComparison.Ordinal))
                                 ?? sourceRuntimes[0];
            result.Add(representative with
            {
                Name = definition.DisplayName,
                VehicleName = definition.DisplayName,
                VehicleId = VehicleForConnection(definition, definition.CommandAuthorityConnectionId),
                ConnectionIds = definition.Connections.Select(item => item.ConnectionId)
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
        if (bindings.Select(item => item.ConnectionId).Distinct(StringComparer.Ordinal).Count() < 2)
            throw new InvalidOperationException("A manually defined unit requires at least two associated connections.");
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

    private static string VehicleForConnection(ManualUnitDefinition definition, string connectionId)
        => definition.Connections.First(item => item.ConnectionId == connectionId).VehicleId;

    private static void ValidateConnection(
        string kind,
        string connectionId,
        IReadOnlyList<UnitConnectionBinding> available)
    {
        if (!available.Any(item => item.ConnectionId == connectionId))
            throw new InvalidOperationException($"The selected {kind} connection is not part of this unit.");
    }

    private async Task PersistAsync(IReadOnlyList<ManualUnitDefinition> definitions, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(definitions, JsonOptions), cancellationToken);
        File.Move(temporary, _path, true);
    }

    private static IReadOnlyList<ManualUnitDefinition> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return (JsonSerializer.Deserialize<ManualUnitDefinition[]>(File.ReadAllText(path), JsonOptions) ?? [])
                .Where(item => item.Connections.Count >= 2)
                .Where(item => item.Connections.Any(binding => binding.ConnectionId == item.CommandAuthorityConnectionId))
                .Where(item => item.Connections.Any(binding => binding.ConnectionId == item.TelemetryAuthorityConnectionId))
                .Where(item => item.Connections.Any(binding => binding.ConnectionId == item.DiagnosticsAuthorityConnectionId))
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
}
