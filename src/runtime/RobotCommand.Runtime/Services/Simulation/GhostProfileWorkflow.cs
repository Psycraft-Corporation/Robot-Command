using System.Text.Json;
using System.Text.Json.Serialization;
using RobotCommand.Core;

namespace RobotCommand.Services.Simulation;

/// <summary>Persistent local Ghost profile catalog with an immutable Dracula template.</summary>
public sealed class GhostProfileWorkflow : IGhostProfileWorkflow
{
    private const string SchemaVersion = "robotcommand.ghost-profiles.v1";
    private const int MaximumNameLength = 64;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly List<GhostProfileSnapshot> _customProfiles = [];
    private readonly List<string> _libraryIssues = [];
    private IReadOnlyList<GhostProfileSnapshot> _profiles = [GhostProfileDefaults.Dracula];

    public GhostProfileWorkflow(string? baseDirectory = null)
    {
        var root = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "ghost-profiles.json");
        Load();
    }

    public event EventHandler? Changed;
    public IReadOnlyList<GhostProfileSnapshot> Profiles => _profiles;
    public IReadOnlyList<string> LibraryIssues => _libraryIssues;

    public GhostProfileSnapshot? Find(string profileId)
        => Profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));

    public async Task<GhostProfileSnapshot> CreateAsync(GhostProfileCreateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request.Name, request.VehicleType, request.Simulation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_profiles.Any(profile => string.Equals(profile.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A Ghost profile named '{request.Name.Trim()}' already exists.");

            var id = CreateUniqueId(request.Name);
            var profile = CreateCustomSnapshot(id, request.Name, request.VehicleType, request.Simulation);
            _customProfiles.Add(profile);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            RebuildProfiles();
            Changed?.Invoke(this, EventArgs.Empty);
            return profile;
        }
        finally { _gate.Release(); }
    }

    public async Task<GhostProfileSnapshot> UpdateAsync(string profileId, GhostProfileUpdateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request.Name, request.VehicleType, request.Simulation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _customProfiles.FindIndex(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                if (string.Equals(profileId, GhostProfileDefaults.Dracula.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The built-in Dracula profile is read-only.");
                throw new KeyNotFoundException($"Ghost profile '{profileId}' was not found.");
            }

            var name = request.Name.Trim();
            if (_customProfiles.Where((_, current) => current != index).Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(name, GhostProfileDefaults.Dracula.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"A Ghost profile named '{name}' already exists.");

            var updated = CreateCustomSnapshot(_customProfiles[index].Id, name, request.VehicleType, request.Simulation, _customProfiles[index].Asset);
            _customProfiles[index] = updated;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            RebuildProfiles();
            Changed?.Invoke(this, EventArgs.Empty);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(profileId, GhostProfileDefaults.Dracula.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The built-in Dracula profile cannot be deleted.");

            var removed = _customProfiles.RemoveAll(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                throw new KeyNotFoundException($"Ghost profile '{profileId}' was not found.");

            await SaveAsync(cancellationToken).ConfigureAwait(false);
            RebuildProfiles();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public async Task<GhostProfileSnapshot> SetAssetAsync(string profileId, GhostProfileAssetSnapshot asset, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _customProfiles.FindIndex(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                if (string.Equals(profileId, GhostProfileDefaults.Dracula.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The built-in Dracula profile is read-only and cannot have a visual asset.");
                throw new KeyNotFoundException($"Ghost profile '{profileId}' was not found.");
            }

            var current = _customProfiles[index];
            var updated = current with { Asset = asset };
            _customProfiles[index] = updated;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            RebuildProfiles();
            Changed?.Invoke(this, EventArgs.Empty);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<GhostProfileSnapshot> ClearAssetAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _customProfiles.FindIndex(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                if (string.Equals(profileId, GhostProfileDefaults.Dracula.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The built-in Dracula profile is read-only and cannot have a visual asset.");
                throw new KeyNotFoundException($"Ghost profile '{profileId}' was not found.");
            }

            var updated = _customProfiles[index] with { Asset = null };
            _customProfiles[index] = updated;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            RebuildProfiles();
            Changed?.Invoke(this, EventArgs.Empty);
            return updated;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var document = new ProfileDocument(
            SchemaVersion,
            _customProfiles.Select(profile => new PersistedProfile(
                profile.Id,
                profile.Name,
                profile.VehicleType,
                profile.Simulation,
                profile.Asset)).ToArray());
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, Json), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* preserve the original write error */ }
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var document = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(_path), Json);
            if (document?.SchemaVersion != SchemaVersion)
            {
                _libraryIssues.Add("The Ghost profile library has an unsupported version and was not loaded.");
                return;
            }

            foreach (var persisted in document.Profiles ?? [])
            {
                try
                {
                    Validate(persisted.Name, persisted.VehicleType, persisted.Simulation);
                    if (!IsSafeId(persisted.Id) ||
                        _customProfiles.Any(profile => string.Equals(profile.Id, persisted.Id, StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(persisted.Id, GhostProfileDefaults.Dracula.Id, StringComparison.OrdinalIgnoreCase) ||
                        _customProfiles.Any(profile => string.Equals(profile.Name, persisted.Name.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(persisted.Name.Trim(), GhostProfileDefaults.Dracula.Name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The profile identity is duplicated or reserved.");
                    _customProfiles.Add(CreateCustomSnapshot(persisted.Id, persisted.Name, persisted.VehicleType, persisted.Simulation, persisted.Asset));
                }
                catch (Exception exception)
                {
                    _libraryIssues.Add($"Ignored Ghost profile '{persisted.Name}': {exception.Message}");
                }
            }
            RebuildProfiles();
        }
        catch (Exception exception)
        {
            _libraryIssues.Add($"The Ghost profile library could not be read: {exception.Message}");
        }
    }

    private void RebuildProfiles()
        => _profiles = new[] { GhostProfileDefaults.Dracula }
            .Concat(_customProfiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

    private string CreateUniqueId(string name)
    {
        var stem = new string(name.Trim().ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
        stem = string.Join('-', stem.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(stem)) stem = "ghost-profile";
        var id = stem;
        var suffix = 2;
        while (Find(id) is not null) id = $"{stem}-{suffix++}";
        return id;
    }

    private static GhostProfileSnapshot CreateCustomSnapshot(string id, string name, string vehicleType, GhostSimulationStats simulation, GhostProfileAssetSnapshot? asset = null)
        => new(id, name.Trim(), vehicleType, simulation, false, true, asset);

    private static void Validate(string name, string vehicleType, GhostSimulationStats simulation)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaximumNameLength)
            throw new ArgumentException($"Profile name is required and must be {MaximumNameLength} characters or fewer.");
        if (!string.Equals(vehicleType, "Multicopter", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only Multicopter Ghost profiles are supported.");
        ValidateValue(simulation.MaximumHorizontalSpeedMetresPerSecond, 100, "maximum speed");
        ValidateValue(simulation.MaximumClimbRateMetresPerSecond, 100, "climb rate");
        ValidateValue(simulation.MaximumDescentRateMetresPerSecond, 100, "descent rate");
        ValidateValue(simulation.HorizontalAccelerationMetresPerSecondSquared, 100, "horizontal acceleration");
        ValidateValue(simulation.VerticalAccelerationMetresPerSecondSquared, 100, "vertical acceleration");
        ValidateValue(simulation.MaximumYawRateDegreesPerSecond, 3600, "maximum yaw rate");
        ValidateValue(simulation.MaximumAltitudeAglMetres, 10_000, "altitude limit");
        ValidateValue(simulation.NominalEnduranceMinutes, 10_080, "endurance");
    }

    private static void ValidateValue(double value, double maximum, string label)
    {
        if (!double.IsFinite(value) || value <= 0 || value > maximum)
            throw new ArgumentException($"Ghost {label} must be greater than zero and no more than {maximum.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}.");
    }

    private static bool IsSafeId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id == Path.GetFileName(id) && id.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private sealed record ProfileDocument(string SchemaVersion, IReadOnlyList<PersistedProfile>? Profiles);
    private sealed record PersistedProfile(string Id, string Name, string VehicleType, GhostSimulationStats Simulation, GhostProfileAssetSnapshot? Asset = null);
}
