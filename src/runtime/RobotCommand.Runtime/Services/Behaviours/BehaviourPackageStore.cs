using System.Text.Json;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

public sealed class BehaviourPackageStore : IBehaviourPackageStore, IDisposable
{
    private const string MetadataSchema = "logos.robot-command.behaviour-library.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<BehaviourPackageStore> _logger;
    private readonly BehaviourPackageIntegrityInspector _inspector;
    private readonly IBehaviourPackageValidator _validator;
    private LocalBehaviourPackageRecord[] _packages = [];
    private BehaviourPackageLibraryIssue[] _issues = [];

    public BehaviourPackageStore(
        AppConfiguration configuration,
        ILogger<BehaviourPackageStore> logger,
        IBehaviourPackageValidator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger;
        RootPath = Path.GetFullPath(configuration.BehaviourLibraryPath);
        _inspector = new BehaviourPackageIntegrityInspector(
            new BehaviourPackageManifestReader(),
            configuration.MaximumBehaviourPackageFiles,
            configuration.MaximumBehaviourPackageBytes);
        _validator = validator ?? new BehaviourPackageValidator();
        EnsureLibraryDirectories();
        RefreshCoreAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public event EventHandler? Changed;

    public string RootPath { get; }

    public IReadOnlyList<LocalBehaviourPackageRecord> Packages => _packages;

    public IReadOnlyList<BehaviourPackageLibraryIssue> Issues => _issues;

    public bool TryGet(BehaviourPackageIdentity identity, out LocalBehaviourPackageRecord? package)
    {
        ArgumentNullException.ThrowIfNull(identity);
        package = _packages.FirstOrDefault(item => item.Identity == identity);
        return package is not null;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<BehaviourPackageImportResult> ImportDirectoryAsync(
        string sourceDirectory,
        bool allowReplace = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("A source behaviour package directory is required.", nameof(sourceDirectory));
        }

        var source = Path.GetFullPath(sourceDirectory);
        if (BehaviourPackageLibraryPaths.IsWithin(RootPath, source) ||
            BehaviourPackageLibraryPaths.IsWithin(source, RootPath))
        {
            throw new InvalidOperationException(
                "The source package directory cannot overlap the managed behaviour library.");
        }

        await _gate.WaitAsync(cancellationToken);
        BehaviourPackageImportResult result;
        try
        {
            EnsureLibraryDirectories();
            var sourceInspection = await _inspector.InspectAsync(source, cancellationToken);
            var identity = sourceInspection.Manifest.Identity;
            var destination = BehaviourPackageLibraryPaths.PackagePath(RootPath, identity);
            var metadataPath = BehaviourPackageLibraryPaths.MetadataFilePath(RootPath, identity);
            var replacing = Directory.Exists(destination);
            if (replacing && !allowReplace)
            {
                throw new InvalidOperationException(
                    $"Behaviour package '{identity.Key}' already exists in the local library. Explicit replacement is required.");
            }

            var operationRoot = Path.Combine(
                BehaviourPackageLibraryPaths.StagingPath(RootPath),
                $"import-{Guid.NewGuid():N}");
            var candidate = Path.Combine(operationRoot, "candidate");
            var backup = Path.Combine(operationRoot, "backup-package");
            var backupMetadata = Path.Combine(operationRoot, "backup-metadata.json");
            var stagedMetadata = Path.Combine(operationRoot, "metadata.json");
            Directory.CreateDirectory(operationRoot);
            var preserveOperationRoot = false;

            try
            {
                await _inspector.CopyDirectoryAsync(source, candidate, cancellationToken);
                var candidateInspection = await _inspector.InspectAsync(candidate, cancellationToken);
                if (!string.Equals(
                        sourceInspection.ContentSha256,
                        candidateInspection.ContentSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The behaviour package changed while it was being imported. Import it again.");
                }

                var existingMetadata = replacing
                    ? await ReadMetadataAsync(metadataPath, cancellationToken)
                    : null;
                var now = DateTimeOffset.UtcNow;
                var metadata = new PackageMetadata(
                    MetadataSchema,
                    identity.BehaviourId,
                    identity.Version,
                    candidateInspection.ContentSha256,
                    existingMetadata?.ImportedAt ?? now,
                    now,
                    existingMetadata?.RemoteBaselineSha256,
                    existingMetadata is not null &&
                    string.Equals(
                        existingMetadata.ImportedContentSha256,
                        candidateInspection.ContentSha256,
                        StringComparison.OrdinalIgnoreCase)
                        ? existingMetadata.LogosValidation
                        : null);
                await WriteMetadataAsync(stagedMetadata, metadata, cancellationToken);

                try
                {
                    if (replacing)
                    {
                        Directory.Move(destination, backup);
                    }
                    if (File.Exists(metadataPath))
                    {
                        File.Move(metadataPath, backupMetadata);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    Directory.Move(candidate, destination);
                    Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
                    File.Move(stagedMetadata, metadataPath);
                    await RefreshCoreAsync(cancellationToken);
                    var installed = _packages.FirstOrDefault(item => item.Identity == identity);
                    if (installed is null ||
                        !string.Equals(installed.ContentSha256, candidateInspection.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("The imported behaviour package could not be verified in the local library.");
                    }

                    var actionMessage = replacing
                        ? $"Replaced local behaviour package '{identity.Key}'."
                        : $"Imported local behaviour package '{identity.Key}'.";
                    result = new BehaviourPackageImportResult(
                        installed,
                        replacing,
                        installed.Integrity.Accepted
                            ? actionMessage
                            : $"{actionMessage} Structural preflight found blocking issues; the package cannot be submitted to Logos yet.");
                }
                catch (Exception importError)
                {
                    try
                    {
                        TryDeleteDirectory(destination);
                        TryDeleteFile(metadataPath);
                        if (Directory.Exists(backup)) Directory.Move(backup, destination);
                        if (File.Exists(backupMetadata)) File.Move(backupMetadata, metadataPath);
                        await RefreshCoreAsync(CancellationToken.None);
                    }
                    catch (Exception rollbackError)
                    {
                        preserveOperationRoot = true;
                        throw new AggregateException(
                            "Behaviour package import failed and its previous library copy could not be restored. " +
                            $"Recovery files remain at '{operationRoot}'.",
                            importError,
                            rollbackError);
                    }

                    throw;
                }
            }
            finally
            {
                if (!preserveOperationRoot)
                {
                    TryDeleteDirectory(operationRoot);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async Task ExportDirectoryAsync(
        BehaviourPackageIdentity identity,
        string destinationDirectory,
        bool allowReplace = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("A destination directory is required.", nameof(destinationDirectory));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var package = GetRequired(identity);
            var source = package.Layout.PackageDirectory;
            var destination = Path.GetFullPath(destinationDirectory);
            if (BehaviourPackageLibraryPaths.IsWithin(source, destination) ||
                BehaviourPackageLibraryPaths.IsWithin(destination, source))
            {
                throw new InvalidOperationException("The export destination cannot overlap the installed package directory.");
            }

            if ((Directory.Exists(destination) || File.Exists(destination)) && !allowReplace)
            {
                throw new InvalidOperationException($"Export destination '{destination}' already exists.");
            }

            var parent = Path.GetDirectoryName(destination)
                         ?? throw new InvalidOperationException("The export destination has no parent directory.");
            Directory.CreateDirectory(parent);
            var operationRoot = Path.Combine(parent, $".{Path.GetFileName(destination)}.robot-command-{Guid.NewGuid():N}");
            var candidate = Path.Combine(operationRoot, "candidate");
            var backup = Path.Combine(operationRoot, "backup");
            Directory.CreateDirectory(operationRoot);
            var preserveOperationRoot = false;
            try
            {
                await _inspector.CopyDirectoryAsync(source, candidate, cancellationToken);
                var sourceInspection = await _inspector.InspectAsync(source, cancellationToken);
                var exportedInspection = await _inspector.InspectAsync(candidate, cancellationToken);
                if (!string.Equals(sourceInspection.ContentSha256, exportedInspection.ContentSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The exported behaviour package did not match the local library copy.");
                }

                if (Directory.Exists(destination)) Directory.Move(destination, backup);
                else if (File.Exists(destination)) throw new InvalidOperationException("The export destination is an existing file.");
                Directory.Move(candidate, destination);
                TryDeleteDirectory(backup);
            }
            catch (Exception exportError)
            {
                try
                {
                    TryDeleteDirectory(destination);
                    if (Directory.Exists(backup)) Directory.Move(backup, destination);
                }
                catch (Exception rollbackError)
                {
                    preserveOperationRoot = true;
                    throw new AggregateException(
                        "Behaviour package export failed and the previous destination could not be restored. " +
                        $"Recovery files remain at '{operationRoot}'.",
                        exportError,
                        rollbackError);
                }

                throw;
            }
            finally
            {
                if (!preserveOperationRoot)
                {
                    TryDeleteDirectory(operationRoot);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(
        BehaviourPackageIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var package = GetRequired(identity);
            var packagePath = package.Layout.PackageDirectory;
            var metadataPath = BehaviourPackageLibraryPaths.MetadataFilePath(RootPath, identity);
            var operationRoot = Path.Combine(
                BehaviourPackageLibraryPaths.StagingPath(RootPath),
                $"remove-{Guid.NewGuid():N}");
            var backup = Path.Combine(operationRoot, "package");
            var backupMetadata = Path.Combine(operationRoot, "metadata.json");
            Directory.CreateDirectory(operationRoot);
            try
            {
                Directory.Move(packagePath, backup);
                if (File.Exists(metadataPath)) File.Move(metadataPath, backupMetadata);
                await RefreshCoreAsync(cancellationToken);
                if (_packages.Any(item => item.Identity == identity))
                {
                    throw new IOException($"Behaviour package '{identity.Key}' remained in the local catalogue after removal.");
                }

                TryDeleteDirectory(operationRoot);
            }
            catch
            {
                if (Directory.Exists(backup)) Directory.Move(backup, packagePath);
                if (File.Exists(backupMetadata)) File.Move(backupMetadata, metadataPath);
                await RefreshCoreAsync(CancellationToken.None);
                TryDeleteDirectory(operationRoot);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetRemoteBaselineAsync(
        BehaviourPackageIdentity identity,
        string? remoteSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var package = GetRequired(identity);
            var metadataPath = BehaviourPackageLibraryPaths.MetadataFilePath(RootPath, identity);
            var metadata = await ReadMetadataAsync(metadataPath, cancellationToken)
                           ?? new PackageMetadata(
                               MetadataSchema,
                               identity.BehaviourId,
                               identity.Version,
                               package.ContentSha256 ?? string.Empty,
                               package.ImportedAt ?? DateTimeOffset.UtcNow,
                               DateTimeOffset.UtcNow,
                               null);
            metadata = metadata with
            {
                RemoteBaselineSha256 = string.IsNullOrWhiteSpace(remoteSha256)
                    ? null
                    : remoteSha256.Trim().ToLowerInvariant(),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await WriteMetadataAtomicallyAsync(metadataPath, metadata, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetLogosValidationAsync(
        BehaviourPackageIdentity identity,
        BehaviourPackageValidationResult validation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(validation);
        if (validation.Authority != BehaviourPackageValidationAuthority.Logos)
        {
            throw new ArgumentException(
                "Only Logos-authoritative validation results can be stored as Logos validation.",
                nameof(validation));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var package = GetRequired(identity);
            var metadataPath = BehaviourPackageLibraryPaths.MetadataFilePath(RootPath, identity);
            var metadata = await ReadMetadataAsync(metadataPath, cancellationToken)
                           ?? new PackageMetadata(
                               MetadataSchema,
                               identity.BehaviourId,
                               identity.Version,
                               package.ContentSha256 ?? string.Empty,
                               package.ImportedAt ?? DateTimeOffset.UtcNow,
                               DateTimeOffset.UtcNow,
                               package.RemoteBaselineSha256);
            metadata = metadata with
            {
                LogosValidation = validation,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await WriteMetadataAtomicallyAsync(metadataPath, metadata, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        EnsureLibraryDirectories();
        var packages = new List<LocalBehaviourPackageRecord>();
        var issues = new List<BehaviourPackageLibraryIssue>();
        foreach (var packageDirectory in Directory.EnumerateDirectories(BehaviourPackageLibraryPaths.PackagesPath(RootPath))
                     .OrderBy(item => item, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storageKey = Path.GetFileName(packageDirectory);
            var metadataPath = Path.Combine(BehaviourPackageLibraryPaths.MetadataPath(RootPath), $"{storageKey}.json");
            PackageMetadata? metadata = null;
            BehaviourPackageValidationFinding? metadataFinding = null;
            try
            {
                metadata = await ReadMetadataAsync(metadataPath, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not load behaviour package metadata {MetadataPath}", metadataPath);
                issues.Add(new BehaviourPackageLibraryIssue(
                    "BEHAVIOUR_LIBRARY_METADATA_LOAD_FAILED",
                    BehaviourPackageFindingSeverity.Warning,
                    ex.Message,
                    metadataPath));
                metadataFinding = new BehaviourPackageValidationFinding(
                    "BEHAVIOUR_LIBRARY_METADATA_LOAD_FAILED",
                    BehaviourPackageFindingSeverity.Warning,
                    "Robot Command metadata could not be read; the package files are still available for inspection.",
                    metadataPath);
            }

            try
            {
                var inspection = await _inspector.InspectAsync(packageDirectory, cancellationToken);
                var manifestIdentity = inspection.Manifest.Identity;
                var identity = metadata?.Identity ?? manifestIdentity;
                var preflight = await _validator.ValidateAsync(
                    new BehaviourPackageValidationContext(
                        identity,
                        new BehaviourPackageLayout(
                            packageDirectory,
                            inspection.ManifestFile,
                            inspection.TreeFile,
                            inspection.GeometryFile),
                        inspection.Manifest,
                        inspection.ContentSha256),
                    cancellationToken);
                var findings = preflight.Findings.ToList();
                if (metadataFinding is not null)
                {
                    findings.Add(metadataFinding);
                }
                var validationState = preflight.State;
                if (metadata is not null && metadata.Identity != manifestIdentity)
                {
                    validationState = BehaviourPackageValidationState.Invalid;
                    findings.Add(new BehaviourPackageValidationFinding(
                        "BEHAVIOUR_LIBRARY_IDENTITY_CHANGED",
                        BehaviourPackageFindingSeverity.Error,
                        $"The installed manifest identity '{manifestIdentity.Key}' differs from the imported identity '{metadata.Identity.Key}'.",
                        inspection.ManifestFile,
                        "bt_id"));
                }

                var expectedStorageKey = BehaviourPackageLibraryPaths.StorageKey(identity);
                if (!string.Equals(storageKey, expectedStorageKey, StringComparison.Ordinal))
                {
                    validationState = PromoteValidationState(
                        validationState,
                        BehaviourPackageFindingSeverity.Warning);
                    findings.Add(new BehaviourPackageValidationFinding(
                        "BEHAVIOUR_LIBRARY_DIRECTORY_RENAMED",
                        BehaviourPackageFindingSeverity.Warning,
                        "The managed package directory was renamed outside Robot Command.",
                        packageDirectory));
                }

                var contentModified = metadata is not null &&
                                      !string.Equals(
                                          metadata.ImportedContentSha256,
                                          inspection.ContentSha256,
                                          StringComparison.OrdinalIgnoreCase);
                if (contentModified)
                {
                    validationState = PromoteValidationState(
                        validationState,
                        BehaviourPackageFindingSeverity.Warning);
                    findings.Add(new BehaviourPackageValidationFinding(
                        "BEHAVIOUR_LIBRARY_CONTENT_MODIFIED",
                        BehaviourPackageFindingSeverity.Warning,
                        "Package files changed after import. Re-import the folder before deploying it.",
                        packageDirectory));
                }

                validationState = StateFromFindings(validationState, findings);
                var integrity = new BehaviourPackageValidationResult(
                    BehaviourPackageValidationAuthority.RobotCommandIntegrity,
                    validationState,
                    validationState switch
                    {
                        BehaviourPackageValidationState.Valid =>
                            "The package folder is readable and passed Robot Command structural preflight.",
                        BehaviourPackageValidationState.Warning =>
                            "The package folder is readable and passed structural preflight with warnings.",
                        _ =>
                            "The package folder is readable, but Robot Command structural preflight found blocking issues."
                    },
                    findings,
                    preflight.ValidatedAt ?? DateTimeOffset.UtcNow);
                var state = validationState == BehaviourPackageValidationState.Invalid
                    ? BehaviourPackageLocalState.Invalid
                    : metadataFinding is not null
                        ? BehaviourPackageLocalState.Discovered
                        : contentModified
                            ? BehaviourPackageLocalState.Modified
                            : BehaviourPackageLocalState.Imported;
                var logosValidation = metadata?.LogosValidation is not null && !contentModified
                    ? metadata.LogosValidation
                    : BehaviourPackageValidationResult.NotValidated(
                        BehaviourPackageValidationAuthority.Logos,
                        contentModified
                            ? "The local package changed after Logos validation. Submit the current copy for validation again."
                            : "Logos has not validated this local package copy.");
                packages.Add(new LocalBehaviourPackageRecord(
                    identity,
                    new BehaviourPackageLayout(
                        packageDirectory,
                        inspection.ManifestFile,
                        inspection.TreeFile,
                        inspection.GeometryFile),
                    inspection.Manifest,
                    inspection.Manifest.DisplayName,
                    inspection.Manifest.Description,
                    inspection.ContentSha256,
                    state,
                    integrity,
                    logosValidation,
                    metadata?.ImportedAt ?? new DateTimeOffset(Directory.GetCreationTimeUtc(packageDirectory)),
                    metadata?.ImportedAt,
                    metadata?.UpdatedAt,
                    metadata?.RemoteBaselineSha256,
                    Attributes: inspection.Manifest.Attributes));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not load local behaviour package {PackageDirectory}", packageDirectory);
                var identity = metadata?.Identity;
                issues.Add(new BehaviourPackageLibraryIssue(
                    "BEHAVIOUR_LIBRARY_LOAD_FAILED",
                    BehaviourPackageFindingSeverity.Error,
                    ex.Message,
                    packageDirectory,
                    identity));
                if (identity is not null)
                {
                    packages.Add(new LocalBehaviourPackageRecord(
                        identity,
                        new BehaviourPackageLayout(packageDirectory, BehaviourPackageLibraryPaths.ManifestYaml, null, null),
                        null,
                        identity.BehaviourId,
                        string.Empty,
                        null,
                        BehaviourPackageLocalState.Unreadable,
                        new BehaviourPackageValidationResult(
                            BehaviourPackageValidationAuthority.RobotCommandIntegrity,
                            BehaviourPackageValidationState.Invalid,
                            "The package could not be read from the local library.",
                            [new BehaviourPackageValidationFinding(
                                "BEHAVIOUR_LIBRARY_LOAD_FAILED",
                                BehaviourPackageFindingSeverity.Error,
                                ex.Message,
                                packageDirectory)],
                            DateTimeOffset.UtcNow),
                        BehaviourPackageValidationResult.NotValidated(
                            BehaviourPackageValidationAuthority.Logos,
                            "The local package is unreadable and must be validated again after it is repaired."),
                        metadata!.ImportedAt,
                        metadata.ImportedAt,
                        metadata.UpdatedAt,
                        metadata.RemoteBaselineSha256));
                }
            }
        }

        foreach (var metadataFile in Directory.EnumerateFiles(
                     BehaviourPackageLibraryPaths.MetadataPath(RootPath),
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            var storageKey = Path.GetFileNameWithoutExtension(metadataFile);
            var packagePath = Path.Combine(BehaviourPackageLibraryPaths.PackagesPath(RootPath), storageKey);
            if (!Directory.Exists(packagePath))
            {
                issues.Add(new BehaviourPackageLibraryIssue(
                    "BEHAVIOUR_LIBRARY_ORPHAN_METADATA",
                    BehaviourPackageFindingSeverity.Warning,
                    "Package metadata exists without a package directory.",
                    metadataFile));
            }
        }

        _packages = packages
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identity.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _issues = issues.ToArray();
    }

    private static BehaviourPackageValidationState PromoteValidationState(
        BehaviourPackageValidationState current,
        BehaviourPackageFindingSeverity severity)
        => severity switch
        {
            BehaviourPackageFindingSeverity.Error => BehaviourPackageValidationState.Invalid,
            BehaviourPackageFindingSeverity.Warning when current == BehaviourPackageValidationState.Valid =>
                BehaviourPackageValidationState.Warning,
            _ => current
        };

    private static BehaviourPackageValidationState StateFromFindings(
        BehaviourPackageValidationState fallback,
        IReadOnlyCollection<BehaviourPackageValidationFinding> findings)
        => findings.Any(item => item.Severity == BehaviourPackageFindingSeverity.Error)
            ? BehaviourPackageValidationState.Invalid
            : findings.Any(item => item.Severity == BehaviourPackageFindingSeverity.Warning)
                ? BehaviourPackageValidationState.Warning
                : fallback;

    private LocalBehaviourPackageRecord GetRequired(BehaviourPackageIdentity identity)
        => _packages.FirstOrDefault(item => item.Identity == identity)
           ?? throw new KeyNotFoundException($"Local behaviour package '{identity.Key}' was not found.");

    private void EnsureLibraryDirectories()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(BehaviourPackageLibraryPaths.PackagesPath(RootPath));
        Directory.CreateDirectory(BehaviourPackageLibraryPaths.MetadataPath(RootPath));
        Directory.CreateDirectory(BehaviourPackageLibraryPaths.StagingPath(RootPath));
    }

    private static async Task<PackageMetadata?> ReadMetadataAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var metadata = await JsonSerializer.DeserializeAsync<PackageMetadata>(stream, JsonOptions, cancellationToken);
        if (metadata is null || !string.Equals(metadata.Schema, MetadataSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Behaviour package metadata '{path}' has an unsupported schema.");
        }

        return metadata;
    }

    private static async Task WriteMetadataAsync(
        string path,
        PackageMetadata metadata,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteMetadataAtomicallyAsync(
        string path,
        PackageMetadata metadata,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteMetadataAsync(temporaryPath, metadata, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record PackageMetadata(
        string Schema,
        string BehaviourId,
        string? Version,
        string ImportedContentSha256,
        DateTimeOffset ImportedAt,
        DateTimeOffset UpdatedAt,
        string? RemoteBaselineSha256,
        BehaviourPackageValidationResult? LogosValidation = null)
    {
        public BehaviourPackageIdentity Identity => new(BehaviourId, Version);
    }
}
