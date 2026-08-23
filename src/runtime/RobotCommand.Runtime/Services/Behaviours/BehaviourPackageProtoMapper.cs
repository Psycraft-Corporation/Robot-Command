using RobotCommand.Models;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Behaviours;

public sealed class BehaviourPackageProtoMapper
{
    public async Task<V1.BehaviourPackage> ToProtoAsync(
        LocalBehaviourPackageRecord package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.CanSubmitToLogos)
        {
            throw new InvalidOperationException(
                $"Local behaviour package '{package.Identity.Key}' is not ready to submit to Logos.");
        }

        var manifestPath = Resolve(package, package.Layout.ManifestFile, "manifest");
        var treePath = ResolveRequired(package, package.Layout.TreeFile, "tree");
        var geometryPath = ResolveRequired(package, package.Layout.GeometryFile, "geometry metadata");

        var proto = new V1.BehaviourPackage
        {
            Package = new V1.AutonomyPackageMetadata
            {
                Version = package.Identity.Version ?? string.Empty,
                DisplayName = package.DisplayName,
                Description = package.Description
            },
            BehaviourId = package.Identity.BehaviourId,
            ManifestYaml = await File.ReadAllTextAsync(manifestPath, cancellationToken),
            TreeXml = await File.ReadAllTextAsync(treePath, cancellationToken),
            GeometryJson = await File.ReadAllTextAsync(geometryPath, cancellationToken),
            PackageSha256 = package.ContentSha256 ?? string.Empty
        };
        proto.Package.RequiredCapabilities.Add(package.RequiredCapabilityKeys);
        proto.Package.ProvidedCapabilities.Add(package.ProvidedCapabilityKeys);
        proto.Package.CompatibleVehicleProfileKeys.Add(package.CompatibleProfiles);
        return proto;
    }

    public BehaviourPackageValidationResult ToValidationResult(
        V1.ValidationResult? validation,
        V1.DomainStatus? status,
        V1.AuthorizationDecision? authorization,
        string acceptedSummary = "Logos accepted the behaviour package validation request.")
    {
        var findings = CollectFindings(
            validation?.Issues,
            status?.Issues,
            authorization?.Status?.Issues);

        if (authorization is { Allowed: false })
        {
            var message = FirstNonEmpty(
                authorization.DeniedReasons,
                authorization.Status?.Message,
                "Logos denied authorization to validate the behaviour package.");
            return new BehaviourPackageValidationResult(
                BehaviourPackageValidationAuthority.Logos,
                BehaviourPackageValidationState.Invalid,
                message,
                AppendIfMissing(findings, new BehaviourPackageValidationFinding(
                    "BEHAVIOUR_PACKAGE_AUTHORIZATION_DENIED",
                    BehaviourPackageFindingSeverity.Error,
                    message)),
                DateTimeOffset.UtcNow);
        }

        if (status is { Ok: false })
        {
            var unavailable = IsUnavailable(status.Code);
            var message = DomainMessage(status);
            return new BehaviourPackageValidationResult(
                BehaviourPackageValidationAuthority.Logos,
                unavailable
                    ? BehaviourPackageValidationState.Unavailable
                    : BehaviourPackageValidationState.Invalid,
                message,
                AppendIfMissing(findings, new BehaviourPackageValidationFinding(
                    unavailable
                        ? "BEHAVIOUR_PACKAGE_VALIDATION_UNAVAILABLE"
                        : "BEHAVIOUR_PACKAGE_VALIDATION_REJECTED",
                    unavailable
                        ? BehaviourPackageFindingSeverity.Warning
                        : BehaviourPackageFindingSeverity.Error,
                    message)),
                ToDateTimeOffset(validation?.ValidatedAt) ?? DateTimeOffset.UtcNow);
        }

        var state = validation?.Status switch
        {
            V1.ValidationStatus.Ok => BehaviourPackageValidationState.Valid,
            V1.ValidationStatus.Warning => BehaviourPackageValidationState.Warning,
            V1.ValidationStatus.Error => BehaviourPackageValidationState.Invalid,
            _ when status?.Ok == true => BehaviourPackageValidationState.Valid,
            _ => BehaviourPackageValidationState.Unavailable
        };
        var summary = state switch
        {
            BehaviourPackageValidationState.Valid => acceptedSummary,
            BehaviourPackageValidationState.Warning => $"{acceptedSummary} Logos returned warnings.",
            BehaviourPackageValidationState.Invalid => "Logos rejected the behaviour package during validation.",
            _ => "Logos did not return a usable behaviour package validation result."
        };
        return new BehaviourPackageValidationResult(
            BehaviourPackageValidationAuthority.Logos,
            state,
            summary,
            findings,
            ToDateTimeOffset(validation?.ValidatedAt) ?? DateTimeOffset.UtcNow);
    }

    public BehaviourPackageGatewayMutationResult ToCreateResult(
        V1.CreateBehaviourPackageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ToMutationResult(
            response.Authorization,
            response.Status,
            response.Validation,
            "Logos installed the behaviour package.");
    }

    public BehaviourPackageGatewayMutationResult ToUpdateResult(
        V1.UpdateBehaviourPackageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ToMutationResult(
            response.Authorization,
            response.Status,
            response.Validation,
            "Logos updated the behaviour package.");
    }

    public BehaviourPackageGatewayMutationResult ToDeleteResult(
        V1.DeleteBehaviourPackageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Authorization is { Allowed: false } authorization)
        {
            return new BehaviourPackageGatewayMutationResult(
                false,
                BehaviourPackageCommandState.Rejected,
                FirstNonEmpty(
                    authorization.DeniedReasons,
                    authorization.Status?.Message,
                    "Logos denied authorization to remove the behaviour package."));
        }

        var command = response.Result;
        var status = command?.Status;
        if (status is { Ok: false })
        {
            return new BehaviourPackageGatewayMutationResult(
                false,
                IsConflict(status)
                    ? BehaviourPackageCommandState.Conflict
                    : IsUnavailable(status.Code)
                        ? BehaviourPackageCommandState.Failed
                        : BehaviourPackageCommandState.Rejected,
                DomainMessage(status),
                OperationId: EmptyToNull(command?.Operation?.OperationId));
        }

        var accepted = command?.CommandStatus is
            V1.CommandStatus.Accepted or
            V1.CommandStatus.InProgress or
            V1.CommandStatus.Succeeded;
        return new BehaviourPackageGatewayMutationResult(
            accepted,
            accepted
                ? BehaviourPackageCommandState.Accepted
                : command?.CommandStatus == V1.CommandStatus.Rejected
                    ? BehaviourPackageCommandState.Rejected
                    : BehaviourPackageCommandState.Failed,
            FirstNonEmpty(
                status?.Message,
                accepted
                    ? "Logos accepted the behaviour package removal."
                    : "Logos did not accept the behaviour package removal."),
            OperationId: EmptyToNull(command?.Operation?.OperationId));
    }

    public static BehaviourPackageValidationResult UnavailableValidation(string message)
        => new(
            BehaviourPackageValidationAuthority.Logos,
            BehaviourPackageValidationState.Unavailable,
            message,
            [new BehaviourPackageValidationFinding(
                "BEHAVIOUR_PACKAGE_GATEWAY_UNAVAILABLE",
                BehaviourPackageFindingSeverity.Warning,
                message)],
            DateTimeOffset.UtcNow);

    private BehaviourPackageGatewayMutationResult ToMutationResult(
        V1.AuthorizationDecision? authorization,
        V1.DomainStatus? status,
        V1.ValidationResult? validation,
        string successMessage)
    {
        var validationResult = ToValidationResult(validation, status, authorization, successMessage);
        if (authorization is { Allowed: false })
        {
            return new BehaviourPackageGatewayMutationResult(
                false,
                BehaviourPackageCommandState.Rejected,
                validationResult.Summary,
                validationResult);
        }

        if (status is { Ok: false })
        {
            return new BehaviourPackageGatewayMutationResult(
                false,
                IsConflict(status)
                    ? BehaviourPackageCommandState.Conflict
                    : IsUnavailable(status.Code)
                        ? BehaviourPackageCommandState.Failed
                        : BehaviourPackageCommandState.Rejected,
                validationResult.Summary,
                validationResult);
        }

        if (!validationResult.Accepted)
        {
            return new BehaviourPackageGatewayMutationResult(
                false,
                validationResult.State == BehaviourPackageValidationState.Unavailable
                    ? BehaviourPackageCommandState.Failed
                    : BehaviourPackageCommandState.Rejected,
                validationResult.Summary,
                validationResult);
        }

        return new BehaviourPackageGatewayMutationResult(
            true,
            BehaviourPackageCommandState.Accepted,
            successMessage,
            validationResult);
    }

    private static string ResolveRequired(
        LocalBehaviourPackageRecord package,
        string? relativePath,
        string label)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException(
                $"Behaviour package '{package.Identity.Key}' does not declare a {label} file required by the current Logos package API.");
        }

        return Resolve(package, relativePath, label);
    }

    private static string Resolve(
        LocalBehaviourPackageRecord package,
        string relativePath,
        string label)
    {
        var path = BehaviourPackageLibraryPaths.ResolvePackageFile(
            package.Layout.PackageDirectory,
            relativePath,
            label);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The declared behaviour package {label} file does not exist.",
                path);
        }

        if (BehaviourPackageLibraryPaths.IsReparsePoint(path))
        {
            throw new InvalidDataException(
                $"The declared behaviour package {label} file cannot be a symbolic link or reparse point.");
        }

        return path;
    }

    private static IReadOnlyList<BehaviourPackageValidationFinding> CollectFindings(
        params IEnumerable<V1.Issue>?[] issueSets)
        => issueSets
            .Where(items => items is not null)
            .SelectMany(items => items!)
            .Select(issue => new BehaviourPackageValidationFinding(
                string.IsNullOrWhiteSpace(issue.Code) ? "LOGOS_BEHAVIOUR_PACKAGE_ISSUE" : issue.Code,
                issue.Severity switch
                {
                    V1.Severity.Error => BehaviourPackageFindingSeverity.Error,
                    V1.Severity.Warning => BehaviourPackageFindingSeverity.Warning,
                    _ => BehaviourPackageFindingSeverity.Information
                },
                issue.Message,
                Field: EmptyToNull(issue.FieldPath)))
            .Distinct()
            .ToArray();

    private static IReadOnlyList<BehaviourPackageValidationFinding> AppendIfMissing(
        IReadOnlyList<BehaviourPackageValidationFinding> findings,
        BehaviourPackageValidationFinding addition)
        => findings.Any(item =>
                string.Equals(item.Code, addition.Code, StringComparison.Ordinal) &&
                string.Equals(item.Message, addition.Message, StringComparison.Ordinal))
            ? findings
            : [.. findings, addition];

    private static bool IsConflict(V1.DomainStatus status)
        => status.Code is V1.DomainCode.AlreadyExists or V1.DomainCode.FailedPrecondition ||
           status.Issues.Any(issue =>
               issue.Code.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) ||
               issue.Code.Contains("STALE", StringComparison.OrdinalIgnoreCase) ||
               issue.Code.Contains("REVISION", StringComparison.OrdinalIgnoreCase));

    private static bool IsUnavailable(V1.DomainCode code)
        => code is
            V1.DomainCode.CapabilityUnavailable or
            V1.DomainCode.DependencyUnavailable or
            V1.DomainCode.DependencyTimeout or
            V1.DomainCode.NotReady;

    private static string DomainMessage(V1.DomainStatus status)
        => string.IsNullOrWhiteSpace(status.Message)
            ? status.Code.ToString()
            : $"{status.Code}: {status.Message}";

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
           ?? string.Empty;

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset? ToDateTimeOffset(Google.Protobuf.WellKnownTypes.Timestamp? timestamp)
        => timestamp is null ? null : new DateTimeOffset(timestamp.ToDateTime(), TimeSpan.Zero);
}
