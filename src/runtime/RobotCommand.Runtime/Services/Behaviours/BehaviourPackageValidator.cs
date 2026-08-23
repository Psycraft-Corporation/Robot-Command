using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

/// <summary>
/// Performs the bounded, application-owned preflight needed before a local
/// package can be offered to Logos. This validator intentionally does not know
/// BehaviorTree.CPP node registrations, ports, plugins, blackboard contracts,
/// or runtime semantics. Logos remains authoritative for those concerns.
/// </summary>
public sealed class BehaviourPackageValidator : IBehaviourPackageValidator
{
    public async Task<BehaviourPackageValidationResult> ValidateAsync(
        BehaviourPackageValidationContext package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var findings = new List<BehaviourPackageValidationFinding>();

        if (package.Identity != package.Manifest.Identity)
        {
            findings.Add(Error(
                "BEHAVIOUR_PACKAGE_IDENTITY_MISMATCH",
                $"The package identity '{package.Identity.Key}' does not match the manifest identity '{package.Manifest.Identity.Key}'.",
                package.Layout.ManifestFile,
                "bt_id"));
        }

        if (package.Manifest.SchemaVersion <= 0)
        {
            findings.Add(Warning(
                "BEHAVIOUR_PACKAGE_SCHEMA_VERSION_UNSPECIFIED",
                "The manifest does not declare a positive schema_version. Logos will determine whether the manifest is supported.",
                package.Layout.ManifestFile,
                "schema_version"));
        }

        if (package.Identity.Version is null)
        {
            findings.Add(Information(
                "BEHAVIOUR_PACKAGE_VERSION_UNSPECIFIED",
                "The package is unversioned locally. Logos may assign or require a version during validation or installation.",
                package.Layout.ManifestFile,
                "version"));
        }

        if (!IsSha256(package.ContentSha256))
        {
            findings.Add(Error(
                "BEHAVIOUR_PACKAGE_CONTENT_HASH_INVALID",
                "The local package does not have a valid SHA-256 content hash."));
        }

        await ValidateManifestFileAsync(package, findings, cancellationToken);
        await ValidateTreeAsync(package, findings, cancellationToken);
        await ValidateGeometryAsync(package, findings, cancellationToken);

        var state = findings.Any(item => item.Severity == BehaviourPackageFindingSeverity.Error)
            ? BehaviourPackageValidationState.Invalid
            : findings.Any(item => item.Severity == BehaviourPackageFindingSeverity.Warning)
                ? BehaviourPackageValidationState.Warning
                : BehaviourPackageValidationState.Valid;
        var summary = state switch
        {
            BehaviourPackageValidationState.Invalid =>
                "The package failed Robot Command structural preflight. Logos semantic validation has not run.",
            BehaviourPackageValidationState.Warning =>
                "The package passed structural preflight with warnings. Logos semantic validation has not run.",
            _ =>
                "The package passed Robot Command structural preflight. Logos semantic validation has not run."
        };

        return new BehaviourPackageValidationResult(
            BehaviourPackageValidationAuthority.RobotCommandIntegrity,
            state,
            summary,
            findings,
            DateTimeOffset.UtcNow);
    }

    private static async Task ValidateManifestFileAsync(
        BehaviourPackageValidationContext package,
        ICollection<BehaviourPackageValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        var path = ResolveRegularFile(package, package.Layout.ManifestFile, "manifest", findings);
        if (path is null)
        {
            return;
        }

        if (new FileInfo(path).Length == 0)
        {
            findings.Add(Error(
                "BEHAVIOUR_PACKAGE_MANIFEST_EMPTY",
                "The behaviour manifest is empty.",
                package.Layout.ManifestFile));
            return;
        }

        // The manifest reader has already parsed the YAML projection. Reading
        // it again here catches a file that became inaccessible between package
        // inspection and validation without interpreting any extra fields.
        try
        {
            await using var stream = OpenRead(path);
            var buffer = new byte[1];
            _ = await stream.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            findings.Add(Error(
                "BEHAVIOUR_PACKAGE_MANIFEST_UNREADABLE",
                $"The behaviour manifest could not be read: {ex.Message}",
                package.Layout.ManifestFile));
        }
    }

    private static async Task ValidateTreeAsync(
        BehaviourPackageValidationContext package,
        ICollection<BehaviourPackageValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        var treeFile = package.Layout.TreeFile;
        if (string.IsNullOrWhiteSpace(treeFile))
        {
            findings.Add(Error(
                "BEHAVIOUR_PACKAGE_TREE_NOT_DECLARED",
                "The manifest does not declare a tree_file. Robot Command cannot submit a tree payload to Logos.",
                package.Layout.ManifestFile,
                "tree_file"));
            return;
        }

        var path = ResolveRegularFile(package, treeFile, "tree", findings);
        if (path is null)
        {
            return;
        }

        if (new FileInfo(path).Length == 0)
        {
            findings.Add(Error(
                "BEHAVIOUR_PACKAGE_TREE_EMPTY",
                "The declared behaviour tree file is empty.",
                treeFile));
            return;
        }

        try
        {
            await using var stream = OpenRead(path);
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = false,
                IgnoreWhitespace = false,
                MaxCharactersFromEntities = 0
            };
            using var reader = XmlReader.Create(stream, settings);
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
            if (document.Root is null)
            {
                findings.Add(Error(
                    "BEHAVIOUR_PACKAGE_TREE_XML_EMPTY_DOCUMENT",
                    "The declared behaviour tree XML has no document element.",
                    treeFile));
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            findings.Add(Error(
                "BEHAVIOUR_PACKAGE_TREE_XML_INVALID",
                $"The declared behaviour tree file is not well-formed XML: {ex.Message}",
                treeFile));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            findings.Add(Error(
                "BEHAVIOUR_PACKAGE_TREE_UNREADABLE",
                $"The declared behaviour tree file could not be read: {ex.Message}",
                treeFile));
        }
    }

    private static async Task ValidateGeometryAsync(
        BehaviourPackageValidationContext package,
        ICollection<BehaviourPackageValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        var geometryFile = package.Layout.GeometryFile;
        if (string.IsNullOrWhiteSpace(geometryFile))
        {
            return;
        }

        var path = ResolveRegularFile(package, geometryFile, "geometry", findings);
        if (path is null)
        {
            return;
        }

        if (new FileInfo(path).Length == 0)
        {
            findings.Add(Error(
                "BEHAVIOUR_PACKAGE_GEOMETRY_EMPTY",
                "The declared geometry metadata file is empty.",
                geometryFile));
            return;
        }

        try
        {
            await using var stream = OpenRead(path);
            using var _ = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 128
                },
                cancellationToken);
        }
        catch (JsonException ex)
        {
            findings.Add(Error(
                "BEHAVIOUR_PACKAGE_GEOMETRY_JSON_INVALID",
                $"The declared geometry metadata file is not valid JSON: {ex.Message}",
                geometryFile));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            findings.Add(Error(
                "BEHAVIOUR_PACKAGE_GEOMETRY_UNREADABLE",
                $"The declared geometry metadata file could not be read: {ex.Message}",
                geometryFile));
        }
    }

    private static string? ResolveRegularFile(
        BehaviourPackageValidationContext package,
        string relativePath,
        string label,
        ICollection<BehaviourPackageValidationFinding> findings)
    {
        try
        {
            var path = BehaviourPackageLibraryPaths.ResolvePackageFile(
                package.Layout.PackageDirectory,
                relativePath,
                label);
            if (!File.Exists(path))
            {
                findings.Add(Error(
                    $"BEHAVIOUR_PACKAGE_{label.ToUpperInvariant()}_MISSING",
                    $"The declared {label} file does not exist.",
                    relativePath));
                return null;
            }

            if (BehaviourPackageLibraryPaths.IsReparsePoint(path))
            {
                findings.Add(Error(
                    $"BEHAVIOUR_PACKAGE_{label.ToUpperInvariant()}_LINK_REJECTED",
                    $"The declared {label} file is a symbolic link or reparse point.",
                    relativePath));
                return null;
            }

            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            findings.Add(Error(
                $"BEHAVIOUR_PACKAGE_{label.ToUpperInvariant()}_PATH_INVALID",
                $"The declared {label} file path is invalid: {ex.Message}",
                relativePath));
            return null;
        }
    }

    private static FileStream OpenRead(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static BehaviourPackageValidationFinding Information(
        string code,
        string message,
        string? file = null,
        string? field = null)
        => new(code, BehaviourPackageFindingSeverity.Information, message, file, field);

    private static BehaviourPackageValidationFinding Warning(
        string code,
        string message,
        string? file = null,
        string? field = null)
        => new(code, BehaviourPackageFindingSeverity.Warning, message, file, field);

    private static BehaviourPackageValidationFinding Error(
        string code,
        string message,
        string? file = null,
        string? field = null)
        => new(code, BehaviourPackageFindingSeverity.Error, message, file, field);
}
