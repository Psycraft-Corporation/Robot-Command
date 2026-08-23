using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Mavlink;

public sealed record Px4ParameterEntry(
    byte VehicleId,
    byte ComponentId,
    string Name,
    string Value,
    byte Type);

public sealed record Px4ParameterFile(
    IReadOnlyList<Px4ParameterEntry> Parameters,
    IReadOnlyList<string> Comments)
{
    public static Px4ParameterFile Empty { get; } = new([], []);
}

public sealed record Px4ParameterProfile(
    string Id,
    string Name,
    string FileName,
    string Path,
    DateTimeOffset CreatedAt,
    string Sha256,
    Px4ParameterFile Document,
    string? FirmwareVersion = null,
    string? SourceFileName = null);

public enum Px4ParameterChangeKind
{
    Unchanged,
    Changed,
    MissingOnVehicle,
    Invalid,
    Unsupported
}

public sealed record Px4ParameterDiff(
    Px4ParameterEntry Profile,
    string? LiveValue,
    Px4ParameterChangeKind Kind,
    string Detail);

public sealed record MavlinkParameterValue(
    byte SystemId,
    byte ComponentId,
    string Name,
    float Value,
    byte Type,
    short Index,
    ushort Count,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Backend-neutral parameter transport contract shared by MAVLink autopilots.
/// </summary>
public interface IMavlinkParameterClient
{
    Task<Px4ParameterFile> DownloadParametersAsync(
        string vehicleId,
        CancellationToken cancellationToken = default);

    Task<bool> SetParameterAsync(
        string vehicleId,
        Px4ParameterEntry entry,
        float value,
        CancellationToken cancellationToken = default);
}

public interface IPx4ParameterFileCodec
{
    Px4ParameterFile Parse(string content);
    string Serialize(Px4ParameterFile document);
}

public sealed class Px4ParameterFileCodec : IPx4ParameterFileCodec
{
    public Px4ParameterFile Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var parameters = new List<Px4ParameterEntry>();
        var comments = new List<string>();
        var seen = new HashSet<(byte Vehicle, byte Component, string Name)>();

        foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                comments.Add(rawLine.TrimEnd());
                continue;
            }

            // ArduPilot Mission Planner exports NAME,VALUE rows while
            // QGroundControl/PX4 exports five MAVLink columns. Both formats
            // map to the same internal document and reviewed apply path.
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 1 && line.Contains(','))
            {
                fields = line.Split(',', StringSplitOptions.TrimEntries);
                if (fields.Length != 2 || string.IsNullOrWhiteSpace(fields[0]) ||
                    !float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var compactValue) ||
                    !float.IsFinite(compactValue))
                {
                    throw new FormatException($"Invalid ArduPilot parameter row: '{rawLine}'.");
                }

                var compactKey = (Vehicle: (byte)1, Component: (byte)1, Name: fields[0]);
                if (!seen.Add(compactKey))
                {
                    throw new FormatException($"Duplicate parameter '{fields[0]}'.");
                }

                parameters.Add(new Px4ParameterEntry(1, 1, fields[0], fields[1], 9));
                continue;
            }

            if (fields.Length != 5)
            {
                throw new FormatException($"Parameter row must contain either two comma-separated columns or five MAVLink columns: '{rawLine}'.");
            }

            if (!byte.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vehicleId) ||
                !byte.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var componentId) ||
                !byte.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var type) ||
                string.IsNullOrWhiteSpace(fields[2]) ||
                !float.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue) ||
                !float.IsFinite(numericValue) ||
                type is < 1 or > 10)
            {
                throw new FormatException($"Invalid MAVLink parameter row: '{rawLine}'.");
            }

            var key = (vehicleId, componentId, fields[2]);
            if (!seen.Add(key))
            {
                throw new FormatException($"Duplicate parameter '{fields[2]}' for vehicle {vehicleId}, component {componentId}.");
            }

            parameters.Add(new Px4ParameterEntry(vehicleId, componentId, fields[2], fields[3], type));
        }

        return new Px4ParameterFile(parameters, comments);
    }

    public string Serialize(Px4ParameterFile document)
    {
        var builder = new StringBuilder();
        foreach (var comment in document.Comments ?? [])
        {
            builder.AppendLine(comment.StartsWith('#') ? comment : $"# {comment}");
        }

        if (document.Comments is null || document.Comments.Count == 0)
        {
            builder.AppendLine("# Onboard parameters for PX4");
            builder.AppendLine("# # Vehicle-Id Component-Id Name Value Type");
        }

        foreach (var parameter in document.Parameters.OrderBy(item => item.VehicleId).ThenBy(item => item.ComponentId).ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            builder.Append(parameter.VehicleId).Append('\t')
                .Append(parameter.ComponentId).Append('\t')
                .Append(parameter.Name).Append('\t')
                .Append(parameter.Value).Append('\t')
                .Append(parameter.Type).AppendLine();
        }

        return builder.ToString();
    }
}

public interface IPx4ParameterProfileStore
{
    string LibraryPath { get; }
    IReadOnlyList<Px4ParameterProfile> Profiles { get; }
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task<Px4ParameterProfile> ImportAsync(string sourcePath, string? name = null, CancellationToken cancellationToken = default);
    Task<Px4ParameterProfile> SaveAsync(string name, Px4ParameterFile document, string? firmwareVersion = null, CancellationToken cancellationToken = default);
    Task ExportAsync(string profileId, string destinationPath, CancellationToken cancellationToken = default);
    bool TryGet(string profileId, out Px4ParameterProfile? profile);
}

public sealed class Px4ParameterProfileStore : IPx4ParameterProfileStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IPx4ParameterFileCodec _codec;
    private readonly ILogger<Px4ParameterProfileStore> _logger;
    private readonly List<Px4ParameterProfile> _profiles = [];
    private readonly object _gate = new();

    public Px4ParameterProfileStore(string baseDirectory, IPx4ParameterFileCodec codec, ILogger<Px4ParameterProfileStore> logger)
    {
        LibraryPath = Path.Combine(baseDirectory, "data", "px4-parameters");
        _codec = codec;
        _logger = logger;
        Directory.CreateDirectory(LibraryPath);
    }

    public string LibraryPath { get; }
    public IReadOnlyList<Px4ParameterProfile> Profiles { get { lock (_gate) return _profiles.ToArray(); } }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(LibraryPath);
        var loaded = new List<Px4ParameterProfile>();
        foreach (var path in Directory.EnumerateFiles(LibraryPath, "*.params", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var content = await File.ReadAllTextAsync(path, cancellationToken);
                var document = _codec.Parse(content);
                var metadata = await ReadMetadataAsync(MetadataPath(path), cancellationToken);
                loaded.Add(CreateProfile(
                    metadata?.Name ?? Path.GetFileNameWithoutExtension(path),
                    path,
                    document,
                    content,
                    metadata?.CreatedAt ?? File.GetCreationTimeUtc(path),
                    metadata?.FirmwareVersion,
                    metadata?.SourceFileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                _logger.LogWarning(ex, "Ignoring invalid PX4 parameter profile {Path}", path);
            }
        }
        lock (_gate) { _profiles.Clear(); _profiles.AddRange(loaded.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)); }
    }

    public async Task<Px4ParameterProfile> ImportAsync(string sourcePath, string? name = null, CancellationToken cancellationToken = default)
    {
        var fullSource = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSource)) throw new FileNotFoundException("The PX4 parameter file was not found.", sourcePath);
        var content = await File.ReadAllTextAsync(fullSource, cancellationToken);
        var document = _codec.Parse(content);
        var safeName = SanitizeName(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fullSource) : name);
        var destination = UniquePath(safeName);
        await WriteAtomicAsync(destination, content, cancellationToken);
        var profile = CreateProfile(safeName, destination, document, content, DateTimeOffset.UtcNow, sourceFileName: Path.GetFileName(fullSource));
        await WriteMetadataAsync(destination, profile, cancellationToken);
        lock (_gate) { _profiles.RemoveAll(item => item.Path.Equals(destination, StringComparison.OrdinalIgnoreCase)); _profiles.Add(profile); }
        return profile;
    }

    public async Task<Px4ParameterProfile> SaveAsync(string name, Px4ParameterFile document, string? firmwareVersion = null, CancellationToken cancellationToken = default)
    {
        var safeName = SanitizeName(name);
        var destination = UniquePath(safeName);
        var content = _codec.Serialize(document);
        await WriteAtomicAsync(destination, content, cancellationToken);
        var profile = CreateProfile(safeName, destination, document, content, DateTimeOffset.UtcNow, firmwareVersion);
        await WriteMetadataAsync(destination, profile, cancellationToken);
        lock (_gate) { _profiles.Add(profile); }
        return profile;
    }

    public async Task ExportAsync(string profileId, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!TryGet(profileId, out var profile) || profile is null) throw new KeyNotFoundException("The PX4 parameter profile was not found.");
        var destination = Path.GetFullPath(destinationPath);
        await WriteAtomicAsync(destination, _codec.Serialize(profile.Document), cancellationToken);
    }

    public bool TryGet(string profileId, out Px4ParameterProfile? profile)
    {
        lock (_gate) { profile = _profiles.FirstOrDefault(item => item.Id.Equals(profileId, StringComparison.Ordinal)); }
        return profile is not null;
    }

    private static Px4ParameterProfile CreateProfile(string name, string path, Px4ParameterFile document, string content, DateTimeOffset createdAt, string? firmwareVersion = null, string? sourceFileName = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return new Px4ParameterProfile(hash[..16], name, Path.GetFileName(path), path, createdAt, hash, document, firmwareVersion, sourceFileName);
    }

    private static string MetadataPath(string parameterPath) => Path.ChangeExtension(parameterPath, ".json");

    private static async Task WriteMetadataAsync(string parameterPath, Px4ParameterProfile profile, CancellationToken cancellationToken)
    {
        var metadata = new Px4ParameterProfileMetadata(
            profile.Name,
            profile.SourceFileName,
            profile.CreatedAt,
            profile.FirmwareVersion,
            profile.Sha256);
        await WriteAtomicAsync(
            MetadataPath(parameterPath),
            JsonSerializer.Serialize(metadata, Json),
            cancellationToken);
    }

    private static async Task<Px4ParameterProfileMetadata?> ReadMetadataAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Px4ParameterProfileMetadata>(
                await File.ReadAllTextAsync(path, cancellationToken), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string UniquePath(string name)
    {
        Directory.CreateDirectory(LibraryPath);
        var path = Path.Combine(LibraryPath, $"{name}.params");
        var index = 2;
        while (File.Exists(path)) path = Path.Combine(LibraryPath, $"{name}-{index++}.params");
        return path;
    }

    private static string SanitizeName(string name)
    {
        var invalid = new string(Path.GetInvalidFileNameChars());
        var safe = new string(name.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "px4-parameters" : safe;
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, content, Encoding.UTF8, cancellationToken);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }
}

internal sealed record Px4ParameterProfileMetadata(
    string Name,
    string? SourceFileName,
    DateTimeOffset CreatedAt,
    string? FirmwareVersion,
    string Sha256);

public sealed record Px4ParameterApplyResult(
    bool Accepted,
    int Applied,
    int Failed,
    IReadOnlyList<string> Messages,
    Px4ParameterProfile? BackupProfile = null)
{
    public static Px4ParameterApplyResult Rejected(string message) => new(false, 0, 0, [message]);
}

public interface IPx4ParameterService
{
    Task<Px4ParameterFile> DownloadAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Px4ParameterDiff>> CompareAsync(string connectionId, string vehicleId, Px4ParameterProfile profile, CancellationToken cancellationToken = default);
    Task<Px4ParameterApplyResult> ApplyAsync(string connectionId, string vehicleId, Px4ParameterProfile profile, CancellationToken cancellationToken = default);
}

public sealed class Px4ParameterService(
    IMavlinkConnectionRegistry registry,
    IPx4ParameterProfileStore profiles,
    ILogger<Px4ParameterService> logger) : IPx4ParameterService
{
    public async Task<Px4ParameterFile> DownloadAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
    {
        var connection = GetConnection(connectionId);
        return await connection.DownloadParametersAsync(vehicleId, cancellationToken);
    }

    public async Task<IReadOnlyList<Px4ParameterDiff>> CompareAsync(string connectionId, string vehicleId, Px4ParameterProfile profile, CancellationToken cancellationToken = default)
    {
        var live = await DownloadAsync(connectionId, vehicleId, cancellationToken);
        return Compare(profile.Document, live);
    }

    public async Task<Px4ParameterApplyResult> ApplyAsync(string connectionId, string vehicleId, Px4ParameterProfile profile, CancellationToken cancellationToken = default)
    {
        var connection = GetConnection(connectionId);
        if (!connection.TryGetVehicle(vehicleId, out _, out _, out var telemetry, out var adapter) ||
            adapter?.Profile is not (MavlinkAutopilotProfile.Px4 or MavlinkAutopilotProfile.ArduPilot) || telemetry is null)
        {
            return Px4ParameterApplyResult.Rejected("Only an available PX4 or ArduPilot MAVLink vehicle can receive parameters.");
        }
        var backendName = adapter.Profile == MavlinkAutopilotProfile.ArduPilot ? "ArduPilot" : "PX4";
        if (telemetry.Armed) return Px4ParameterApplyResult.Rejected($"{backendName} parameters can only be applied while the vehicle is disarmed.");
        if (telemetry.IsStale || telemetry.State is not AvailabilityState.Online)
        {
            return Px4ParameterApplyResult.Rejected($"{backendName} telemetry is stale or offline.");
        }

        var live = await connection.DownloadParametersAsync(vehicleId, cancellationToken);
        var backup = await profiles.SaveAsync($"backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}", live, cancellationToken: cancellationToken);
        var allDiffs = Compare(profile.Document, live);
        var unsupported = allDiffs.Where(item => item.Kind is Px4ParameterChangeKind.MissingOnVehicle or Px4ParameterChangeKind.Unsupported or Px4ParameterChangeKind.Invalid).ToArray();
        var messages = new List<string>();
        var applied = 0;
        var failed = unsupported.Length;
        foreach (var diff in unsupported) messages.Add($"{diff.Profile.Name}: {diff.Detail}");
        var diffs = allDiffs.Where(item => item.Kind == Px4ParameterChangeKind.Changed).ToArray();
        foreach (var diff in diffs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!float.TryParse(diff.Profile.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                failed++;
                messages.Add($"{diff.Profile.Name}: invalid value.");
                continue;
            }

            var verified = await connection.SetParameterAsync(vehicleId, diff.Profile, value, cancellationToken);
            if (verified) { applied++; messages.Add($"{diff.Profile.Name}: applied and verified."); }
            else { failed++; messages.Add($"{diff.Profile.Name}: no matching PARAM_VALUE acknowledgement was received."); }
        }

        logger.LogInformation("Applied {Backend} parameter profile {Profile} to {Vehicle}: {Applied} applied, {Failed} failed", backendName, profile.Name, vehicleId, applied, failed);
        return new Px4ParameterApplyResult(failed == 0, applied, failed, messages, backup);
    }

    private static IReadOnlyList<Px4ParameterDiff> Compare(Px4ParameterFile profile, Px4ParameterFile live)
    {
        var liveByKey = live.Parameters.ToDictionary(item => (item.ComponentId, item.Name));
        return profile.Parameters.Select(item =>
        {
            if (!liveByKey.TryGetValue((item.ComponentId, item.Name), out var current))
                return new Px4ParameterDiff(item, null, Px4ParameterChangeKind.MissingOnVehicle, "Parameter is not reported by the vehicle.");
            if (item.Type != current.Type)
                return new Px4ParameterDiff(item, current.Value, Px4ParameterChangeKind.Unsupported, $"MAVLink type differs: profile {item.Type}, vehicle {current.Type}.");
            var unchanged = string.Equals(item.Value, current.Value, StringComparison.Ordinal) ||
                            (float.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var profileValue) &&
                             float.TryParse(current.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var liveValue) &&
                             profileValue == liveValue);
            return new Px4ParameterDiff(item, current.Value,
                unchanged ? Px4ParameterChangeKind.Unchanged : Px4ParameterChangeKind.Changed,
                unchanged ? "Unchanged." : "Value differs from the vehicle.");
        }).ToArray();
    }

    private MavlinkConnection GetConnection(string connectionId)
        => registry.TryGet(connectionId, out var connection) && connection is not null
            ? connection
            : throw new InvalidOperationException("The MAVLink connection is not active.");
}
