using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using RobotCommand.Services.Geometry;
using RobotCommand.Services.Missions;

namespace RobotCommand.Services.Autonomy;

public sealed class AutonomyDeploymentBundleService : IAutonomyDeploymentBundleService
{
    private const string ManifestFileName = "deployment-manifest.json";
    private const int MaximumArchiveEntries = 10_000;
    private const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumManifestBytes = 4L * 1024 * 1024;

    private static StringComparer ArchivePathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IBehaviourPackageStore _behaviours;
    private readonly IGeometryDocumentStore? _geometry;
    private readonly IMissionTaskWorkspaceService? _plans;
    private readonly ILogger<AutonomyDeploymentBundleService> _logger;

    public AutonomyDeploymentBundleService(
        IBehaviourPackageStore behaviours,
        IGeometryDocumentStore? geometry = null,
        IMissionTaskWorkspaceService? plans = null,
        ILogger<AutonomyDeploymentBundleService>? logger = null)
    {
        _behaviours = behaviours ?? throw new ArgumentNullException(nameof(behaviours));
        _geometry = geometry;
        _plans = plans;
        _logger = logger ?? NullLogger<AutonomyDeploymentBundleService>.Instance;

        var autonomyRoot = Directory.GetParent(Path.GetFullPath(_behaviours.RootPath))?.FullName
                           ?? Path.GetFullPath(_behaviours.RootPath);
        RootPath = Path.Combine(autonomyRoot, "Deployments");
        Directory.CreateDirectory(RootPath);
        EnsureNotReparsePoint(RootPath);
        Directory.CreateDirectory(StagingPath);
        Directory.CreateDirectory(ImportsPath);
    }

    public string RootPath { get; }

    private string StagingPath => Path.Combine(RootPath, "staging");

    private string ImportsPath => Path.Combine(RootPath, "imports");

    public async Task<AutonomyDeploymentBundleManifest> ExportAsync(
        AutonomyBundleExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateExportRequest(request);

        await _gate.WaitAsync(cancellationToken);
        var stage = Path.Combine(StagingPath, $"export-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stage);
            EnsureNotReparsePoint(stage);
            var behaviours = new List<AutonomyBundleBehaviourAsset>();
            var geometry = new List<AutonomyBundleDocumentAsset>();
            var policies = new List<AutonomyBundleDocumentAsset>();
            var missions = new List<AutonomyBundleDocumentAsset>();
            var tasks = new List<AutonomyBundleDocumentAsset>();

            foreach (var identity in request.BehaviourPackages.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_behaviours.TryGet(identity, out var package) || package is null)
                {
                    throw new KeyNotFoundException($"Local behaviour package '{identity.Key}' was not found.");
                }
                if (!package.Integrity.Accepted)
                {
                    throw new InvalidOperationException(
                        $"Behaviour package '{identity.Key}' has blocking local integrity findings and cannot be bundled.");
                }

                var directory = $"assets/behaviours/{BehaviourDirectoryName(identity)}";
                var target = AutonomyDeploymentBundleRules.ResolveContainedPath(stage, directory);
                await CopyDirectoryAsync(package.Layout.PackageDirectory, target, cancellationToken);
                behaviours.Add(new AutonomyBundleBehaviourAsset(
                    identity.BehaviourId,
                    identity.Version,
                    directory,
                    package.ContentSha256));
            }

            foreach (var geometryId in NormalizeIds(request.GeometryIds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_geometry is null)
                {
                    throw new InvalidOperationException("The local geometry document store is unavailable.");
                }
                var path = $"assets/geometry/{AutonomyDeploymentBundleRules.SafeSegment(geometryId)}.logos-geometry.json";
                await _geometry.ExportAsync(
                    geometryId,
                    AutonomyDeploymentBundleRules.ResolveContainedPath(stage, path),
                    cancellationToken);
                geometry.Add(new AutonomyBundleDocumentAsset(geometryId, path));
            }

            foreach (var policyPath in NormalizeExternalFiles(request.PolicyFiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = UniqueDocumentPath(
                    "assets/policies",
                    Path.GetFileName(policyPath),
                    policies.Select(item => item.RelativePath));
                var assetId = Path.GetFileNameWithoutExtension(relative);
                await CopyFileAsync(
                    policyPath,
                    AutonomyDeploymentBundleRules.ResolveContainedPath(stage, relative),
                    cancellationToken);
                policies.Add(new AutonomyBundleDocumentAsset(assetId, relative));
            }

            foreach (var missionId in NormalizeIds(request.MissionIds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_plans is null)
                {
                    throw new InvalidOperationException("The mission and task document workspace is unavailable.");
                }
                var path = $"plans/missions/{AutonomyDeploymentBundleRules.SafeSegment(missionId)}.logos-mission.json";
                await _plans.ExportMissionAsync(
                    missionId,
                    AutonomyDeploymentBundleRules.ResolveContainedPath(stage, path),
                    cancellationToken);
                missions.Add(new AutonomyBundleDocumentAsset(missionId, path));
            }

            foreach (var taskId in NormalizeIds(request.TaskIds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_plans is null)
                {
                    throw new InvalidOperationException("The mission and task document workspace is unavailable.");
                }
                var path = $"plans/tasks/{AutonomyDeploymentBundleRules.SafeSegment(taskId)}.logos-task.json";
                await _plans.ExportTaskAsync(
                    taskId,
                    AutonomyDeploymentBundleRules.ResolveContainedPath(stage, path),
                    cancellationToken);
                tasks.Add(new AutonomyBundleDocumentAsset(taskId, path));
            }

            var files = await DescribeFilesAsync(stage, cancellationToken);
            var bundleId = string.IsNullOrWhiteSpace(request.BundleId)
                ? ($"autonomy-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}")[..42]
                : AutonomyDeploymentBundleRules.SafeSegment(request.BundleId, "autonomy-bundle");
            var manifest = new AutonomyDeploymentBundleManifest(
                AutonomyDeploymentBundleManifest.CurrentSchemaVersion,
                bundleId,
                request.DisplayName.Trim(),
                request.Description?.Trim() ?? string.Empty,
                DateTimeOffset.UtcNow,
                behaviours,
                geometry,
                policies,
                missions,
                tasks,
                request.BindingIntents
                    .DistinctBy(item => (item.BehaviourId, item.Version, item.SlotId))
                    .OrderBy(item => item.BehaviourId, StringComparer.Ordinal)
                    .ThenBy(item => item.Version, StringComparer.Ordinal)
                    .ThenBy(item => item.SlotId, StringComparer.Ordinal)
                    .ToArray(),
                files,
                request.Metadata);
            var manifestIssues = AutonomyDeploymentBundleRules.ValidateManifest(manifest);
            if (manifestIssues.Count > 0)
            {
                throw new InvalidDataException(string.Join(" ", manifestIssues));
            }

            await File.WriteAllTextAsync(
                Path.Combine(stage, ManifestFileName),
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken);
            await CreateArchiveAtomicallyAsync(stage, request.DestinationPath, request.AllowReplace, cancellationToken);
            return manifest;
        }
        finally
        {
            TryDeleteDirectory(stage);
            _gate.Release();
        }
    }

    public async Task<AutonomyBundleInspectionResult> InspectAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = ValidateArchivePath(archivePath);
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                131072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            return await InspectArchiveAsync(fullPath, archive, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AutonomyBundleInspectionResult.Invalid(
                archivePath,
                "The autonomy deployment bundle is invalid.",
                [ex.Message]);
        }
    }

    public async Task<AutonomyBundleImportResult> ImportAsync(
        string archivePath,
        AutonomyBundleImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken);
        var staging = Path.Combine(StagingPath, $"import-{Guid.NewGuid():N}");
        string importedPath = string.Empty;
        try
        {
            var fullPath = ValidateArchivePath(archivePath);
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                131072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var inspection = await InspectArchiveAsync(fullPath, archive, cancellationToken);
            if (!inspection.Valid || inspection.Manifest is null)
            {
                return new AutonomyBundleImportResult(
                    fullPath,
                    string.Empty,
                    false,
                    inspection.Summary,
                    inspection.Issues,
                    inspection.Plan,
                    0,
                    0,
                    0,
                    0);
            }

            Directory.CreateDirectory(staging);
            await ExtractArchiveAsync(archive, staging, cancellationToken);
            importedPath = await CommitImportedBundleAsync(
                staging,
                inspection.Manifest.BundleId,
                options.AllowReplaceBundle,
                cancellationToken);
            staging = string.Empty;

            var steps = inspection.Plan.ToList();
            var issues = new List<string>();
            var geometryCount = 0;
            var behaviourCount = 0;
            var missionCount = 0;
            var taskCount = 0;

            for (var index = 0; index < steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = steps[index];
                try
                {
                    switch (step.Kind)
                    {
                        case AutonomyBundleAssetKind.Geometry:
                            {
                                var asset = inspection.Manifest.Geometry.Single(item => item.AssetId == step.AssetId);
                                if (_geometry is null)
                                {
                                    steps[index] = step with
                                    {
                                        State = AutonomyDeploymentStepState.Deferred,
                                        Summary = "The geometry store is unavailable; the validated file remains staged in the imported bundle."
                                    };
                                    break;
                                }
                                await _geometry.ImportAsync(
                                    AutonomyDeploymentBundleRules.ResolveContainedPath(importedPath, asset.RelativePath),
                                    options.AllowReplaceGeometry,
                                    cancellationToken);
                                geometryCount++;
                                steps[index] = step with
                                {
                                    State = AutonomyDeploymentStepState.Completed,
                                    Summary = "Imported through the local geometry document store."
                                };
                                break;
                            }
                        case AutonomyBundleAssetKind.BehaviourPackage:
                            {
                                var asset = inspection.Manifest.Behaviours.Single(item => item.Identity.Key == step.AssetId);
                                await _behaviours.ImportDirectoryAsync(
                                    AutonomyDeploymentBundleRules.ResolveContainedPath(importedPath, asset.RelativeDirectory),
                                    options.AllowReplaceBehaviours,
                                    cancellationToken);
                                behaviourCount++;
                                steps[index] = step with
                                {
                                    State = AutonomyDeploymentStepState.Completed,
                                    Summary = "Imported through the managed local behaviour package library."
                                };
                                break;
                            }
                        case AutonomyBundleAssetKind.MissionTemplate:
                            {
                                if (!options.ImportMissions)
                                {
                                    steps[index] = step with
                                    {
                                        State = AutonomyDeploymentStepState.Skipped,
                                        Summary = "Mission import was disabled; the document remains staged."
                                    };
                                    break;
                                }
                                var asset = inspection.Manifest.Missions.Single(item => item.AssetId == step.AssetId);
                                if (_plans is null)
                                {
                                    steps[index] = step with
                                    {
                                        State = AutonomyDeploymentStepState.Deferred,
                                        Summary = "The mission workspace is unavailable; the document remains staged."
                                    };
                                    break;
                                }
                                await _plans.ImportMissionAsync(
                                    AutonomyDeploymentBundleRules.ResolveContainedPath(importedPath, asset.RelativePath),
                                    cancellationToken);
                                missionCount++;
                                steps[index] = step with
                                {
                                    State = AutonomyDeploymentStepState.Completed,
                                    Summary = "Imported as a local mission draft."
                                };
                                break;
                            }
                        case AutonomyBundleAssetKind.TaskTemplate:
                            {
                                if (!options.ImportTasks)
                                {
                                    steps[index] = step with
                                    {
                                        State = AutonomyDeploymentStepState.Skipped,
                                        Summary = "Task import was disabled; the document remains staged."
                                    };
                                    break;
                                }
                                var asset = inspection.Manifest.Tasks.Single(item => item.AssetId == step.AssetId);
                                if (_plans is null)
                                {
                                    steps[index] = step with
                                    {
                                        State = AutonomyDeploymentStepState.Deferred,
                                        Summary = "The task workspace is unavailable; the document remains staged."
                                    };
                                    break;
                                }
                                await _plans.ImportTaskAsync(
                                    AutonomyDeploymentBundleRules.ResolveContainedPath(importedPath, asset.RelativePath),
                                    cancellationToken);
                                taskCount++;
                                steps[index] = step with
                                {
                                    State = AutonomyDeploymentStepState.Completed,
                                    Summary = "Imported as a local task draft."
                                };
                                break;
                            }
                        case AutonomyBundleAssetKind.BehaviourBinding:
                        case AutonomyBundleAssetKind.PolicyProfile:
                            // These steps intentionally remain deferred. Binding is a
                            // connection-scoped Logos mutation and policy activation has
                            // its own validation and confirmation workflow.
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    issues.Add($"{step.Action} '{step.AssetId}' failed: {ex.Message}");
                    steps[index] = step with
                    {
                        State = AutonomyDeploymentStepState.Failed,
                        Summary = ex.Message
                    };
                    _logger.LogWarning(ex, "Autonomy bundle step {Step} failed for {AssetId}", step.Action, step.AssetId);
                }
            }

            var succeeded = steps.All(item => item.State != AutonomyDeploymentStepState.Failed);
            var summary = succeeded
                ? "The autonomy deployment bundle was imported. Deferred bindings and policies remain explicit follow-up operations."
                : "The bundle was retained locally, but one or more asset imports failed. Review the dependency plan before continuing.";
            return new AutonomyBundleImportResult(
                fullPath,
                importedPath,
                succeeded,
                summary,
                issues,
                steps,
                geometryCount,
                behaviourCount,
                missionCount,
                taskCount);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(staging))
            {
                TryDeleteDirectory(staging);
            }
            _gate.Release();
        }
    }

    private async Task<AutonomyBundleInspectionResult> InspectArchiveAsync(
        string fullPath,
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            return AutonomyBundleInspectionResult.Invalid(
                fullPath,
                "The autonomy deployment bundle is too large.",
                [$"The archive contains {archive.Entries.Count} entries; the limit is {MaximumArchiveEntries}."]);
        }

        var issues = new List<string>();
        var entries = new Dictionary<string, ZipArchiveEntry>(ArchivePathComparer);
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path;
            try
            {
                path = AutonomyDeploymentBundleRules.NormalizeRelativePath(entry.FullName);
            }
            catch (Exception ex)
            {
                issues.Add(ex.Message);
                continue;
            }

            if (IsSymbolicLink(entry))
            {
                issues.Add($"Archive entry '{path}' is a symbolic link.");
                continue;
            }
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }
            if (!entries.TryAdd(path, entry))
            {
                issues.Add($"Archive entry '{path}' is duplicated.");
                continue;
            }
            if (entry.Length < 0 || entry.Length > MaximumArchiveBytes)
            {
                issues.Add($"Archive entry '{path}' has an invalid size.");
                continue;
            }
            totalBytes += entry.Length;
            if (totalBytes > MaximumArchiveBytes)
            {
                issues.Add($"The uncompressed archive exceeds {MaximumArchiveBytes} bytes.");
                break;
            }
        }

        if (!entries.TryGetValue(ManifestFileName, out var manifestEntry))
        {
            issues.Add($"The archive does not contain '{ManifestFileName}'.");
            return AutonomyBundleInspectionResult.Invalid(
                fullPath,
                "The autonomy deployment bundle is invalid.",
                issues);
        }
        if (manifestEntry.Length > MaximumManifestBytes)
        {
            issues.Add("The deployment manifest exceeds the maximum supported size.");
            return AutonomyBundleInspectionResult.Invalid(fullPath, "The autonomy deployment bundle is invalid.", issues);
        }

        AutonomyDeploymentBundleManifest? manifest;
        try
        {
            await using var manifestStream = manifestEntry.Open();
            manifest = await JsonSerializer.DeserializeAsync<AutonomyDeploymentBundleManifest>(
                manifestStream,
                JsonOptions,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            issues.Add($"The deployment manifest is invalid JSON: {ex.Message}");
            return AutonomyBundleInspectionResult.Invalid(fullPath, "The autonomy deployment bundle is invalid.", issues);
        }
        if (manifest is null)
        {
            issues.Add("The deployment manifest is empty.");
            return AutonomyBundleInspectionResult.Invalid(fullPath, "The autonomy deployment bundle is invalid.", issues);
        }

        issues.AddRange(AutonomyDeploymentBundleRules.ValidateManifest(manifest));
        if (issues.Count > 0)
        {
            return new AutonomyBundleInspectionResult(
                fullPath,
                false,
                "The autonomy deployment bundle failed manifest validation.",
                issues,
                manifest,
                []);
        }

        var declared = manifest.Files
            .Select(item => AutonomyDeploymentBundleRules.NormalizeRelativePath(item.Path))
            .ToHashSet(ArchivePathComparer);
        var undeclared = entries.Keys
            .Where(path => path != ManifestFileName && !declared.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        foreach (var path in undeclared)
        {
            issues.Add($"Archive entry '{path}' is not declared by the manifest.");
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = AutonomyDeploymentBundleRules.NormalizeRelativePath(file.Path);
            if (!entries.TryGetValue(path, out var entry))
            {
                issues.Add($"Manifest file '{path}' is missing from the archive.");
                continue;
            }
            if (entry.Length != file.SizeBytes)
            {
                issues.Add($"Manifest file '{path}' has size {entry.Length}, expected {file.SizeBytes}.");
                continue;
            }
            await using var content = entry.Open();
            var sha256 = await ComputeSha256Async(content, cancellationToken);
            if (!string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"Manifest file '{path}' failed SHA-256 verification.");
            }
        }

        var plan = AutonomyDeploymentBundleRules.BuildPlan(manifest);
        return new AutonomyBundleInspectionResult(
            fullPath,
            issues.Count == 0,
            issues.Count == 0
                ? $"Bundle '{manifest.DisplayName}' contains {manifest.AssetCount} asset reference(s) and passed integrity verification."
                : "The autonomy deployment bundle failed integrity verification.",
            issues,
            manifest,
            plan);
    }

    private static async Task ExtractArchiveAsync(
        ZipArchive archive,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = AutonomyDeploymentBundleRules.NormalizeRelativePath(entry.FullName);
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(AutonomyDeploymentBundleRules.ResolveContainedPath(destinationRoot, relative));
                continue;
            }
            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException($"Archive entry '{relative}' is a symbolic link.");
            }

            var destination = AutonomyDeploymentBundleRules.ResolveContainedPath(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                131072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken);
        }
    }

    private async Task<string> CommitImportedBundleAsync(
        string staging,
        string bundleId,
        bool allowReplace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.Combine(ImportsPath, AutonomyDeploymentBundleRules.SafeSegment(bundleId, "autonomy-bundle"));
        var backup = destination + $".backup-{Guid.NewGuid():N}";
        if (Directory.Exists(destination) && !allowReplace)
        {
            throw new IOException($"Imported bundle '{bundleId}' already exists locally.");
        }

        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
            }
            Directory.Move(staging, destination);
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
            await Task.CompletedTask;
            return destination;
        }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(backup))
            {
                Directory.Move(backup, destination);
            }
            throw;
        }
    }

    private static async Task<IReadOnlyList<AutonomyBundleFileEntry>> DescribeFilesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var files = new List<AutonomyBundleFileEntry>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(item => item, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotReparsePoint(path);
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var kind = KindFromPath(relative);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                131072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            files.Add(new AutonomyBundleFileEntry(
                relative,
                kind,
                stream.Length,
                await ComputeSha256Async(stream, cancellationToken)));
        }
        return files;
    }

    private static AutonomyBundleAssetKind KindFromPath(string relativePath)
        => relativePath switch
        {
            var path when path.StartsWith("assets/geometry/", StringComparison.Ordinal) => AutonomyBundleAssetKind.Geometry,
            var path when path.StartsWith("assets/behaviours/", StringComparison.Ordinal) => AutonomyBundleAssetKind.BehaviourPackage,
            var path when path.StartsWith("assets/policies/", StringComparison.Ordinal) => AutonomyBundleAssetKind.PolicyProfile,
            var path when path.StartsWith("plans/missions/", StringComparison.Ordinal) => AutonomyBundleAssetKind.MissionTemplate,
            var path when path.StartsWith("plans/tasks/", StringComparison.Ordinal) => AutonomyBundleAssetKind.TaskTemplate,
            _ => throw new InvalidDataException($"Bundle file '{relativePath}' is outside a supported asset directory.")
        };

    private static async Task CreateArchiveAtomicallyAsync(
        string sourceDirectory,
        string destinationPath,
        bool allowReplace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(destinationPath.Trim());
        if (Directory.Exists(destination))
        {
            throw new IOException($"Bundle destination '{destination}' is a directory.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        EnsureNotReparsePoint(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && !allowReplace)
        {
            throw new IOException($"Bundle destination '{destination}' already exists.");
        }

        var temporary = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            ZipFile.CreateFromDirectory(sourceDirectory, temporary, CompressionLevel.Optimal, includeBaseDirectory: false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: allowReplace);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourceDirectory);
        EnsureNotReparsePoint(source);
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotReparsePoint(directory);
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotReparsePoint(file);
            var relative = Path.GetRelativePath(source, file);
            await CopyFileAsync(file, Path.Combine(destinationDirectory, relative), cancellationToken);
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        EnsureNotReparsePoint(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ValidateArchivePath(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("An autonomy bundle archive path is required.", nameof(archivePath));
        }
        var fullPath = Path.GetFullPath(archivePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The autonomy deployment bundle was not found.", fullPath);
        }
        EnsureNotReparsePoint(fullPath);
        return fullPath;
    }

    private static void ValidateExportRequest(AutonomyBundleExportRequest request)
    {
        if (request.BehaviourPackages is null ||
            request.GeometryIds is null ||
            request.MissionIds is null ||
            request.TaskIds is null ||
            request.PolicyFiles is null ||
            request.BindingIntents is null)
        {
            throw new ArgumentException("Autonomy bundle asset collections cannot be null.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            throw new ArgumentException("A destination ZIP path is required.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ArgumentException("A bundle display name is required.", nameof(request));
        }
        if (request.BehaviourPackages.Count == 0 &&
            request.GeometryIds.Count == 0 &&
            request.MissionIds.Count == 0 &&
            request.TaskIds.Count == 0 &&
            request.PolicyFiles.Count == 0 &&
            request.BindingIntents.Count == 0)
        {
            throw new ArgumentException("Select at least one autonomy asset for the bundle.", nameof(request));
        }
    }

    private static IReadOnlyList<string> NormalizeIds(IEnumerable<string> values)
        => values
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> NormalizeExternalFiles(IEnumerable<string> values)
    {
        var result = new List<string>();
        foreach (var value in values.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var path = Path.GetFullPath(value.Trim());
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("A policy profile file was not found.", path);
            }
            EnsureNotReparsePoint(path);
            result.Add(path);
        }
        return result.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    private static string UniqueDocumentPath(
        string directory,
        string fileName,
        IEnumerable<string> existing)
    {
        var safeName = AutonomyDeploymentBundleRules.SafeSegment(Path.GetFileNameWithoutExtension(fileName));
        var extension = Path.GetExtension(fileName);
        var candidate = $"{directory}/{safeName}{extension}";
        var reserved = existing.ToHashSet(StringComparer.Ordinal);
        var suffix = 2;
        while (reserved.Contains(candidate))
        {
            candidate = $"{directory}/{safeName}-{suffix++}{extension}";
        }
        return candidate;
    }

    private static string BehaviourDirectoryName(BehaviourPackageIdentity identity)
    {
        var id = AutonomyDeploymentBundleRules.SafeSegment(identity.BehaviourId, "behaviour");
        var version = AutonomyDeploymentBundleRules.SafeSegment(identity.Version ?? "unversioned", "unversioned");
        var keyHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity.Key)))
            .ToLowerInvariant()[..12];
        return $"{id}--{version}--{keyHash}";
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixFileType == 0xA000;
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Symbolic links and reparse points are not allowed: '{path}'.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Staging cleanup is best effort. The next import/export can safely
            // use a new GUID directory and operators can remove abandoned files.
        }
    }
}
