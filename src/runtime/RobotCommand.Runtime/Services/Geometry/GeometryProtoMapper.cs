using Google.Protobuf.WellKnownTypes;
using RobotCommand.Models;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Geometry;

public sealed class GeometryProtoMapper
{
    private const string MinimumAltitudePresentAttribute = "robot_command.minimum_altitude_present";
    private const string MaximumAltitudePresentAttribute = "robot_command.maximum_altitude_present";
    private const string SourceApplication = "logos-robot-command";

    private readonly GeometryDocumentCodec _codec;

    public GeometryProtoMapper(GeometryDocumentCodec codec)
    {
        _codec = codec;
    }

    public V1.GeometryObject ToProto(GeometryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = GeometryDocumentCodec.Normalize(document);
        var geometry = new V1.GeometryObject
        {
            Metadata = new V1.ResourceMetadata
            {
                ResourceId = normalized.GeometryId,
                DisplayName = normalized.DisplayName,
                Description = normalized.Description,
                SchemaVersion = normalized.SchemaVersion,
                Revision = normalized.SourceRevision ?? string.Empty
            },
            GeometryId = normalized.GeometryId,
            Kind = ToProto(normalized.Kind),
            DisplayName = normalized.DisplayName,
            Description = normalized.Description,
            FrameId = FrameId(normalized.Frame),
            FrameScope = ToProto(normalized.Frame),
            Closed = normalized.Kind == GeometryDocumentKind.Zone,
            Policy = ToProto(normalized.Policy),
            Source = Source(normalized.Origin),
            SourceSummary = SourceApplication,
            UpdatedAt = ToTimestamp(normalized.UpdatedAt)
        };

        geometry.Metadata.CreatedAt = ToTimestamp(normalized.CreatedAt);
        geometry.Metadata.UpdatedAt = ToTimestamp(normalized.UpdatedAt);
        CopyAttributes(normalized.Attributes, geometry.Attributes);

        if (normalized.Kind == GeometryDocumentKind.Zone)
        {
            foreach (var ring in normalized.Rings)
            {
                var protoRing = new V1.GeometryRing();
                protoRing.Points.Add(ring.Points.Select(ToProto));
                geometry.Rings.Add(protoRing);
            }

            if (normalized.Rings.Count > 0)
            {
                geometry.Points.Add(normalized.Rings[0].Points.Select(ToProto));
            }
        }
        else
        {
            geometry.Points.Add(normalized.Points.Select(ToProto));
        }

        return geometry;
    }

    public RemoteGeometryObject ToRemoteObject(
        V1.GeometryObject geometry,
        V1.GeometryRecord? record,
        string connectionId)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var document = ToDocument(geometry, connectionId);
        var mappedRecord = record is null
            ? CreateRecordFromObject(document, connectionId)
            : ToRecord(record, connectionId, document.SourceRevision);
        document = document with
        {
            SourceRevision = mappedRecord.Revision ?? document.SourceRevision,
            SourceSha256 = string.IsNullOrWhiteSpace(mappedRecord.Sha256)
                ? document.ContentSha256
                : mappedRecord.Sha256,
            IsDirty = false
        };
        return new RemoteGeometryObject(document, mappedRecord);
    }

    public GeometryDocument ToDocument(V1.GeometryObject geometry, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var frame = ToModel(geometry.FrameScope, geometry.FrameId);
        var kind = ToModel(geometry.Kind);
        var rings = geometry.Rings
            .Select(item => new GeometryDocumentRing
            {
                Points = item.Points.Select(ToModel).ToArray()
            })
            .ToArray();

        if (kind == GeometryDocumentKind.Zone && rings.Length == 0 && geometry.Points.Count > 0)
        {
            rings =
            [
                new GeometryDocumentRing
                {
                    Points = geometry.Points.Select(ToModel).ToArray()
                }
            ];
        }

        var metadata = geometry.Metadata;
        var updatedAt = ToDateTimeOffset(metadata?.UpdatedAt)
                        ?? ToDateTimeOffset(geometry.UpdatedAt)
                        ?? DateTimeOffset.UtcNow;
        var createdAt = ToDateTimeOffset(metadata?.CreatedAt) ?? updatedAt;
        var document = new GeometryDocument
        {
            SchemaVersion = FirstNonEmpty(metadata?.SchemaVersion, GeometryDocument.CurrentSchemaVersion),
            GeometryId = FirstNonEmpty(geometry.GeometryId, metadata?.ResourceId),
            DisplayName = FirstNonEmpty(geometry.DisplayName, metadata?.DisplayName, geometry.GeometryId),
            Description = FirstNonEmpty(geometry.Description, metadata?.Description),
            Kind = kind,
            Frame = frame,
            Points = kind == GeometryDocumentKind.Zone
                ? []
                : geometry.Points.Select(ToModel).ToArray(),
            Rings = kind == GeometryDocumentKind.Zone ? rings : [],
            Policy = ToModel(geometry.Policy),
            Attributes = CopyAttributes(geometry.Attributes),
            Origin = GeometryDocumentOrigin.PulledFromLogos,
            SourceConnectionId = string.IsNullOrWhiteSpace(connectionId) ? null : connectionId.Trim(),
            SourceRevision = EmptyToNull(metadata?.Revision),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            IsDirty = false
        };
        var normalized = GeometryDocumentCodec.Normalize(document);
        return normalized with
        {
            ContentSha256 = GeometryDocumentCodec.ComputeContentSha256(normalized),
            IsDirty = false
        };
    }

    public static RemoteGeometryRecord ToRecord(
        V1.GeometryRecord record,
        string connectionId,
        string? revision = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new RemoteGeometryRecord(
            connectionId,
            record.GeometryId,
            ToModel(record.Kind),
            FirstNonEmpty(record.DisplayName, record.GeometryId),
            ToModel(record.FrameScope, record.FrameId),
            record.Closed,
            ToInt32(record.PointCount),
            ToInt32(record.RingCount),
            record.SizeBytes,
            record.Sha256,
            EmptyToNull(revision),
            ToDateTimeOffset(record.UpdatedAt),
            CopyAttributes(record.Attributes));
    }

    public static GeometryValidationResult ToValidationResult(
        V1.ValidationResult? validation,
        V1.DomainStatus? domainStatus,
        V1.AuthorizationDecision? authorization,
        string validSummary = "Logos accepted the geometry validation request.")
    {
        var issues = CollectIssues(
            validation?.Issues,
            domainStatus?.Issues,
            authorization?.Status?.Issues);

        if (authorization is { Allowed: false })
        {
            var message = FirstNonEmpty(
                authorization.DeniedReasons,
                authorization.Status?.Message,
                "Logos denied authorization for geometry validation.");
            return new GeometryValidationResult(
                GeometryValidationState.Invalid,
                message,
                AppendIfMissing(issues, new GeometryValidationIssue(
                    "GEOMETRY_AUTHORIZATION_DENIED",
                    GeometryValidationSeverity.Error,
                    message,
                    Source: "Logos GeometryService")));
        }

        if (domainStatus is { Ok: false })
        {
            var message = DomainMessage(domainStatus);
            var unavailable = IsUnavailable(domainStatus.Code);
            return new GeometryValidationResult(
                unavailable ? GeometryValidationState.Unavailable : GeometryValidationState.Invalid,
                message,
                AppendIfMissing(issues, new GeometryValidationIssue(
                    unavailable ? "GEOMETRY_VALIDATION_UNAVAILABLE" : "GEOMETRY_VALIDATION_REJECTED",
                    unavailable ? GeometryValidationSeverity.Warning : GeometryValidationSeverity.Error,
                    message,
                    Source: "Logos GeometryService")));
        }

        var state = validation?.Status switch
        {
            V1.ValidationStatus.Ok => GeometryValidationState.Valid,
            V1.ValidationStatus.Warning => GeometryValidationState.Warning,
            V1.ValidationStatus.Error => GeometryValidationState.Invalid,
            _ when domainStatus?.Ok == true => GeometryValidationState.Valid,
            _ => GeometryValidationState.Unavailable
        };
        var summary = state switch
        {
            GeometryValidationState.Valid => validSummary,
            GeometryValidationState.Warning => $"{validSummary} Logos returned warnings.",
            GeometryValidationState.Invalid => "Logos rejected the geometry during validation.",
            _ => "Logos did not return a usable geometry validation result."
        };
        return new GeometryValidationResult(state, summary, issues);
    }

    public GeometryCommandResult ToCreateResult(
        V1.CreateGeometryObjectResponse response,
        string connectionId)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ToMutationResult(
            response.Authorization,
            response.Status,
            response.Validation,
            response.Object,
            response.Record,
            connectionId,
            "Logos created the geometry object.");
    }

    public GeometryCommandResult ToUpdateResult(
        V1.UpdateGeometryObjectResponse response,
        string connectionId)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ToMutationResult(
            response.Authorization,
            response.Status,
            response.Validation,
            response.Object,
            response.Record,
            connectionId,
            "Logos updated the geometry object.");
    }

    public static GeometryCommandResult ToDeleteResult(V1.DeleteGeometryObjectResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var authorization = response.Authorization;
        if (authorization is { Allowed: false })
        {
            return new GeometryCommandResult(
                false,
                GeometryCommandState.Rejected,
                FirstNonEmpty(
                    authorization.DeniedReasons,
                    authorization.Status?.Message,
                    "Logos denied authorization to delete the geometry object."));
        }

        var command = response.Result;
        var status = command?.Status;
        if (status is { Ok: false })
        {
            return new GeometryCommandResult(
                false,
                IsConflict(status) ? GeometryCommandState.Conflict : GeometryCommandState.Rejected,
                DomainMessage(status),
                OperationId: EmptyToNull(command?.Operation?.OperationId));
        }

        var accepted = command?.CommandStatus is
            V1.CommandStatus.Accepted or
            V1.CommandStatus.InProgress or
            V1.CommandStatus.Succeeded;
        var state = accepted
            ? GeometryCommandState.Accepted
            : command?.CommandStatus == V1.CommandStatus.Rejected
                ? GeometryCommandState.Rejected
                : GeometryCommandState.Failed;
        return new GeometryCommandResult(
            accepted,
            state,
            FirstNonEmpty(
                status?.Message,
                accepted
                    ? "Logos accepted the geometry deletion."
                    : "Logos did not accept the geometry deletion."),
            OperationId: EmptyToNull(command?.Operation?.OperationId));
    }

    public static GeometryRegistrySnapshot ToRegistrySnapshot(
        V1.GeometryRegistryStatus? status,
        string connectionId)
    {
        if (status is null)
        {
            return GeometryRegistrySnapshot.Unknown(connectionId);
        }

        return new GeometryRegistrySnapshot(
            connectionId,
            ToModel(status.State),
            Display(status.Health),
            Display(status.Readiness),
            status.Code,
            status.Message,
            ToInt32(status.ObjectCount),
            status.RegistrySignature,
            ToDateTimeOffset(status.LoadedAt),
            ToDateTimeOffset(status.CheckedAt) ?? DateTimeOffset.UtcNow,
            CollectIssues(status.Issues));
    }

    public GeometryRegistryEvent ToRegistryEvent(
        V1.WatchGeometryRegistryResponse response,
        string connectionId)
    {
        ArgumentNullException.ThrowIfNull(response);
        var remoteObject = response.Object is null
            ? null
            : ToRemoteObject(response.Object, response.Record, connectionId);
        var record = remoteObject?.Record
                     ?? (response.Record is null ? null : ToRecord(response.Record, connectionId));
        var observedAt = ToDateTimeOffset(response.Event?.ObservedAt)
                         ?? ToDateTimeOffset(response.Response?.ServerTime)
                         ?? DateTimeOffset.UtcNow;
        return new GeometryRegistryEvent(
            connectionId,
            ToModel(response.EventType),
            response.RegistryStatus is null
                ? null
                : ToRegistrySnapshot(response.RegistryStatus, connectionId),
            record,
            remoteObject,
            observedAt);
    }

    public static V1.GeometryKind ToProto(GeometryDocumentKind kind)
        => kind switch
        {
            GeometryDocumentKind.PointOfInterest => V1.GeometryKind.Poi,
            GeometryDocumentKind.WaypointSequence => V1.GeometryKind.WaypointSequence,
            GeometryDocumentKind.Zone => V1.GeometryKind.Zone,
            _ => V1.GeometryKind.Unspecified
        };

    public static V1.GeometryFrameScope ToProto(GeometryCoordinateFrame frame)
        => frame switch
        {
            GeometryCoordinateFrame.GlobalWgs84 => V1.GeometryFrameScope.GlobalWgs84,
            GeometryCoordinateFrame.LocalEnu => V1.GeometryFrameScope.LocalEnu,
            GeometryCoordinateFrame.LocalNed => V1.GeometryFrameScope.LocalNed,
            _ => V1.GeometryFrameScope.Unknown
        };

    public static GeometryDocumentKind ToModel(V1.GeometryKind kind)
        => kind switch
        {
            V1.GeometryKind.Poi => GeometryDocumentKind.PointOfInterest,
            V1.GeometryKind.WaypointSequence => GeometryDocumentKind.WaypointSequence,
            V1.GeometryKind.Zone => GeometryDocumentKind.Zone,
            _ => GeometryDocumentKind.Unknown
        };

    public static GeometryCoordinateFrame ToModel(
        V1.GeometryFrameScope frame,
        string? frameId = null)
    {
        var mapped = frame switch
        {
            V1.GeometryFrameScope.GlobalWgs84 => GeometryCoordinateFrame.GlobalWgs84,
            V1.GeometryFrameScope.LocalEnu => GeometryCoordinateFrame.LocalEnu,
            V1.GeometryFrameScope.LocalNed => GeometryCoordinateFrame.LocalNed,
            _ => GeometryCoordinateFrame.Unknown
        };
        if (mapped != GeometryCoordinateFrame.Unknown)
        {
            return mapped;
        }

        return frameId?.Trim().ToLowerInvariant() switch
        {
            "wgs84" or "global_wgs84" => GeometryCoordinateFrame.GlobalWgs84,
            "local_enu" or "enu" => GeometryCoordinateFrame.LocalEnu,
            "local_ned" or "ned" => GeometryCoordinateFrame.LocalNed,
            _ => GeometryCoordinateFrame.Unknown
        };
    }

    private GeometryCommandResult ToMutationResult(
        V1.AuthorizationDecision? authorization,
        V1.DomainStatus? domainStatus,
        V1.ValidationResult? validation,
        V1.GeometryObject? geometry,
        V1.GeometryRecord? record,
        string connectionId,
        string successMessage)
    {
        var validationResult = ToValidationResult(validation, domainStatus, authorization, successMessage);
        var remote = geometry is null ? null : ToRemoteObject(geometry, record, connectionId);

        if (authorization is { Allowed: false })
        {
            return new GeometryCommandResult(
                false,
                GeometryCommandState.Rejected,
                validationResult.Summary,
                remote,
                validationResult);
        }

        if (domainStatus is { Ok: false })
        {
            return new GeometryCommandResult(
                false,
                IsConflict(domainStatus)
                    ? GeometryCommandState.Conflict
                    : IsUnavailable(domainStatus.Code)
                        ? GeometryCommandState.Failed
                        : GeometryCommandState.Rejected,
                validationResult.Summary,
                remote,
                validationResult);
        }

        if (!validationResult.IsValid)
        {
            return new GeometryCommandResult(
                false,
                validationResult.State == GeometryValidationState.Unavailable
                    ? GeometryCommandState.Failed
                    : GeometryCommandState.Rejected,
                validationResult.Summary,
                remote,
                validationResult);
        }

        return new GeometryCommandResult(
            true,
            GeometryCommandState.Accepted,
            successMessage,
            remote,
            validationResult);
    }

    private static RemoteGeometryRecord CreateRecordFromObject(
        GeometryDocument document,
        string connectionId)
        => new(
            connectionId,
            document.GeometryId,
            document.Kind,
            document.DisplayName,
            document.Frame,
            document.IsClosed,
            document.Kind == GeometryDocumentKind.Zone
                ? document.Rings.Sum(ring => ring.Points.Count)
                : document.Points.Count,
            document.Rings.Count,
            0,
            string.Empty,
            document.SourceRevision,
            document.UpdatedAt,
            document.Attributes);

    private static V1.GeometryPoint ToProto(GeometryDocumentPoint point)
        => new()
        {
            X = point.X,
            Y = point.Y,
            Z = point.Z
        };

    private static GeometryDocumentPoint ToModel(V1.GeometryPoint point)
        => new(point.X, point.Y, point.Z);

    private static V1.GeometryPolicyMetadata ToProto(GeometryPolicyAnnotation policy)
    {
        var result = new V1.GeometryPolicyMetadata
        {
            Kind = policy.Kind,
            Constraint = policy.Constraint,
            Decision = policy.Decision,
            Code = policy.Code,
            RecommendedAction = policy.RecommendedAction
        };
        result.Operations.Add(policy.Operations);
        result.Tags.Add(policy.Tags);
        CopyAttributes(policy.Attributes, result.Attributes);
        if (policy.MinimumAltitudeMetres is { } minimum)
        {
            result.MinAltM = minimum;
            result.Attributes[MinimumAltitudePresentAttribute] = "true";
        }
        if (policy.MaximumAltitudeMetres is { } maximum)
        {
            result.MaxAltM = maximum;
            result.Attributes[MaximumAltitudePresentAttribute] = "true";
        }
        return result;
    }

    private static GeometryPolicyAnnotation ToModel(V1.GeometryPolicyMetadata? policy)
    {
        if (policy is null)
        {
            return GeometryPolicyAnnotation.None;
        }

        var attributes = CopyAttributes(policy.Attributes);
        var minimumPresent = IsTrue(attributes, MinimumAltitudePresentAttribute) || policy.MinAltM != 0;
        var maximumPresent = IsTrue(attributes, MaximumAltitudePresentAttribute) || policy.MaxAltM != 0;
        attributes.Remove(MinimumAltitudePresentAttribute);
        attributes.Remove(MaximumAltitudePresentAttribute);
        return new GeometryPolicyAnnotation
        {
            Kind = policy.Kind,
            Constraint = policy.Constraint,
            Operations = policy.Operations.ToArray(),
            Decision = policy.Decision,
            Code = policy.Code,
            RecommendedAction = policy.RecommendedAction,
            MinimumAltitudeMetres = minimumPresent ? policy.MinAltM : null,
            MaximumAltitudeMetres = maximumPresent ? policy.MaxAltM : null,
            Tags = policy.Tags.ToArray(),
            Attributes = attributes
        };
    }

    private static GeometryRegistryState ToModel(V1.GeometryRegistryState state)
        => state switch
        {
            V1.GeometryRegistryState.Ready => GeometryRegistryState.Ready,
            V1.GeometryRegistryState.Reloading => GeometryRegistryState.Reloading,
            V1.GeometryRegistryState.Degraded => GeometryRegistryState.Degraded,
            V1.GeometryRegistryState.Failed => GeometryRegistryState.Failed,
            _ => GeometryRegistryState.Unknown
        };

    private static GeometryRegistryEventKind ToModel(V1.WatchGeometryRegistryEventType kind)
        => kind switch
        {
            V1.WatchGeometryRegistryEventType.Snapshot => GeometryRegistryEventKind.Snapshot,
            V1.WatchGeometryRegistryEventType.Created => GeometryRegistryEventKind.Created,
            V1.WatchGeometryRegistryEventType.Updated => GeometryRegistryEventKind.Updated,
            V1.WatchGeometryRegistryEventType.Deleted => GeometryRegistryEventKind.Deleted,
            V1.WatchGeometryRegistryEventType.Reloaded => GeometryRegistryEventKind.Reloaded,
            V1.WatchGeometryRegistryEventType.StatusUpdate => GeometryRegistryEventKind.StatusUpdate,
            V1.WatchGeometryRegistryEventType.Heartbeat => GeometryRegistryEventKind.Heartbeat,
            _ => GeometryRegistryEventKind.Unknown
        };

    private static GeometryValidationIssue[] CollectIssues(
        params IEnumerable<V1.Issue>?[] sources)
        => sources
            .Where(source => source is not null)
            .SelectMany(source => source!)
            .Select(ToModel)
            .DistinctBy(item => (item.Code, item.Message, item.FieldPath, item.Source))
            .ToArray();

    private static GeometryValidationIssue ToModel(V1.Issue issue)
        => new(
            string.IsNullOrWhiteSpace(issue.Code) ? "LOGOS_GEOMETRY_ISSUE" : issue.Code,
            issue.Severity switch
            {
                V1.Severity.Warning => GeometryValidationSeverity.Warning,
                V1.Severity.Error or V1.Severity.Critical => GeometryValidationSeverity.Error,
                _ => GeometryValidationSeverity.Information
            },
            FirstNonEmpty(issue.Message, issue.Hint, issue.Code),
            issue.FieldPath,
            "Logos GeometryService");

    private static GeometryValidationIssue[] AppendIfMissing(
        IReadOnlyList<GeometryValidationIssue> issues,
        GeometryValidationIssue addition)
        => issues.Any(item =>
                string.Equals(item.Code, addition.Code, StringComparison.Ordinal) &&
                string.Equals(item.Message, addition.Message, StringComparison.Ordinal))
            ? issues.ToArray()
            : [.. issues, addition];

    private static bool IsConflict(V1.DomainStatus status)
        => status.Code == V1.DomainCode.AlreadyExists ||
           status.Issues.Any(issue =>
               issue.Code.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) ||
               issue.Code.Contains("REVISION", StringComparison.OrdinalIgnoreCase) ||
               issue.Code.Contains("STALE", StringComparison.OrdinalIgnoreCase));

    private static bool IsUnavailable(V1.DomainCode code)
        => code is
            V1.DomainCode.DependencyUnavailable or
            V1.DomainCode.DependencyTimeout or
            V1.DomainCode.NotReady;

    private static string DomainMessage(V1.DomainStatus status)
        => string.IsNullOrWhiteSpace(status.Message)
            ? status.Code.ToString()
            : $"{status.Code}: {status.Message}";

    private static string Source(GeometryDocumentOrigin origin)
        => origin switch
        {
            GeometryDocumentOrigin.Imported => "json",
            GeometryDocumentOrigin.PulledFromLogos => "api",
            GeometryDocumentOrigin.Generated => "generated",
            _ => SourceApplication
        };

    private static string FrameId(GeometryCoordinateFrame frame)
        => frame switch
        {
            GeometryCoordinateFrame.GlobalWgs84 => "wgs84",
            GeometryCoordinateFrame.LocalEnu => "local_enu",
            GeometryCoordinateFrame.LocalNed => "local_ned",
            _ => string.Empty
        };

    private static void CopyAttributes(
        IReadOnlyDictionary<string, string> source,
        IDictionary<string, string> destination)
    {
        foreach (var pair in source)
        {
            destination[pair.Key] = pair.Value ?? string.Empty;
        }
    }

    private static Dictionary<string, string> CopyAttributes(
        IEnumerable<KeyValuePair<string, string>> source)
        => source.ToDictionary(
            item => item.Key,
            item => item.Value ?? string.Empty,
            StringComparer.Ordinal);

    private static bool IsTrue(IReadOnlyDictionary<string, string> attributes, string key)
        => attributes.TryGetValue(key, out var value) &&
           bool.TryParse(value, out var parsed) &&
           parsed;

    private static Timestamp ToTimestamp(DateTimeOffset value)
        => Timestamp.FromDateTime(value.UtcDateTime);

    private static DateTimeOffset? ToDateTimeOffset(Timestamp? timestamp)
    {
        if (timestamp is null || timestamp.Seconds == 0 && timestamp.Nanos == 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(timestamp.ToDateTime(), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int ToInt32(uint value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Display(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ||
               string.Equals(text, "Unspecified", StringComparison.OrdinalIgnoreCase)
            ? "Unknown"
            : text;
    }
}
