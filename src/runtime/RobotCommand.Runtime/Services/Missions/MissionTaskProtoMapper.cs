using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using RobotCommand.Models;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Missions;

public static class MissionTaskProtoMapper
{
    private const string TeamAttribute = "robot_command.team_id";
    private const string VehicleAttribute = "robot_command.vehicle_id";

    public static V1.MissionPlan ToProto(MissionRecord mission)
    {
        ArgumentNullException.ThrowIfNull(mission);

        var plan = new V1.MissionPlan
        {
            MissionId = mission.Id,
            Metadata = new V1.ResourceMetadata
            {
                ResourceId = mission.Id,
                DisplayName = mission.Name,
                Description = mission.Objective,
                SchemaVersion = "logos.mission.v1"
            },
            FormatVersion = "logos.mission.v1",
            Priority = ParseEnum<V1.MissionPriority>(mission.Priority, "Normal"),
            PolicyId = mission.PolicyId ?? string.Empty,
            Objective = mission.Objective ?? string.Empty,
            PayloadJson = mission.PayloadJson ?? string.Empty,
            Source = new V1.MissionSource
            {
                ExternalMissionId = mission.Id,
                ExternalSystem = "logos-robot-command"
            }
        };
        plan.GeometryIds.Add(mission.GeometryIds ?? []);
        plan.RequiredCapabilities.Add(mission.RequiredCapabilities ?? []);

        if (!string.IsNullOrWhiteSpace(mission.AssignedTeamId))
        {
            plan.Attributes[TeamAttribute] = mission.AssignedTeamId;
        }

        if (!string.IsNullOrWhiteSpace(mission.AssignedVehicleId))
        {
            plan.Attributes[VehicleAttribute] = mission.AssignedVehicleId;
        }

        return plan;
    }

    public static V1.TaskPlan ToProto(OperationalTaskRecord task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var plan = new V1.TaskPlan
        {
            TaskId = task.Id,
            Metadata = new V1.ResourceMetadata
            {
                ResourceId = task.Id,
                DisplayName = task.Name,
                Description = task.Objective,
                SchemaVersion = "logos.task.v1"
            },
            MissionId = task.MissionId ?? string.Empty,
            TeamId = task.TeamId ?? string.Empty,
            TaskType = task.TaskType ?? string.Empty,
            Priority = ParseEnum<V1.TaskPriority>(task.Priority, "Normal"),
            Objective = task.Objective ?? string.Empty,
            Behaviour = new V1.TaskBehaviourBinding
            {
                BehaviourId = task.BehaviourId ?? string.Empty,
                BehaviourVersion = task.BehaviourVersion ?? string.Empty,
                PackageId = task.PackageId ?? string.Empty,
                ParametersJson = task.ParametersJson ?? string.Empty
            },
            ParametersJson = task.ParametersJson ?? string.Empty,
            CompletionCriteria = new V1.TaskCompletionCriteria
            {
                Type = ParseEnum<V1.TaskCompletionCriteriaType>("BehaviourSuccess", "BehaviourSuccess")
            }
        };
        plan.GeometryIds.Add(task.GeometryIds ?? []);

        return plan;
    }

    public static MissionRecord ToModel(
        V1.MissionPlan plan,
        string connectionId,
        V1.MissionStatus? status = null,
        V1.MissionExecution? execution = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var state = status is not null
            ? DisplayEnum(status.State)
            : execution is not null
                ? DisplayEnum(execution.State)
                : "Registered";
        var executionId = FirstNonEmpty(
            status?.MissionExecutionId,
            execution?.MissionExecutionId);
        var observedAt = ToDateTimeOffset(status?.ObservedAt) ??
                         ToDateTimeOffset(execution?.UpdatedAt) ??
                         DateTimeOffset.UtcNow;

        return new MissionRecord(
            plan.MissionId,
            FirstNonEmpty(plan.Metadata?.DisplayName, plan.MissionId) ?? plan.MissionId,
            state,
            ReadAttribute(plan.Attributes, TeamAttribute),
            ReadAttribute(plan.Attributes, VehicleAttribute),
            connectionId,
            plan.Objective,
            DisplayEnum(plan.Priority),
            plan.PolicyId,
            plan.GeometryIds.ToArray(),
            plan.RequiredCapabilities.ToArray(),
            plan.PayloadJson,
            SourcePath: null,
            ValidationState: PlanValidationState.NotValidated,
            ValidationSummary: "Remote mission",
            MissionExecutionId: executionId,
            Progress: status?.Progress,
            ObservedAt: observedAt,
            IsLocalDraft: false);
    }

    public static OperationalTaskRecord ToModel(
        V1.TaskPlan plan,
        string connectionId,
        V1.TaskStatus? status = null,
        V1.TaskExecution? execution = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var assignment = status?.Assignment ?? execution?.Assignment;
        var state = status is not null
            ? DisplayEnum(status.State)
            : execution is not null
                ? DisplayEnum(execution.State)
                : "Registered";
        var assignmentState = status is not null
            ? DisplayEnum(status.AssignmentState)
            : assignment is not null
                ? DisplayEnum(assignment.State)
                : "Unassigned";
        var executionId = FirstNonEmpty(status?.TaskExecutionId, execution?.TaskExecutionId);
        var observedAt = ToDateTimeOffset(status?.ObservedAt) ??
                         ToDateTimeOffset(execution?.UpdatedAt) ??
                         DateTimeOffset.UtcNow;

        return new OperationalTaskRecord(
            plan.TaskId,
            FirstNonEmpty(plan.Metadata?.DisplayName, plan.TaskId) ?? plan.TaskId,
            state,
            plan.MissionId,
            assignment?.AssignedVehicleId,
            connectionId,
            FirstNonEmpty(plan.TeamId, assignment?.TeamId),
            assignment?.AssignedMemberId,
            assignment?.AssignedLogosInstanceId,
            plan.Objective,
            plan.TaskType,
            DisplayEnum(plan.Priority),
            plan.Behaviour?.BehaviourId ?? string.Empty,
            plan.Behaviour?.BehaviourVersion ?? string.Empty,
            plan.Behaviour?.PackageId ?? string.Empty,
            plan.ParametersJson,
            assignmentState,
            PlanValidationState.NotValidated,
            "Remote task",
            executionId,
            status?.Progress?.Progress,
            observedAt,
            SourcePath: null,
            IsLocalDraft: false,
            GeometryIds: plan.GeometryIds.ToArray());
    }

    public static DocumentValidationResult ToValidationResult(
        V1.ValidationResult? validation,
        V1.DomainStatus? domainStatus,
        V1.AuthorizationDecision? authorization,
        string validSummary)
    {
        var issues = CollectIssues(validation?.Issues, domainStatus?.Issues, authorization?.Status?.Issues);

        if (authorization is { Allowed: false })
        {
            var denied = string.IsNullOrWhiteSpace(authorization.DeniedReasons)
                ? "Logos denied authorization for the validation request."
                : authorization.DeniedReasons;
            return new DocumentValidationResult(
                PlanValidationState.Invalid,
                denied,
                AppendIfMissing(issues, denied));
        }

        if (domainStatus is { Ok: false })
        {
            var message = DomainMessage(domainStatus);
            var unavailable = IsUnavailable(domainStatus.Code.ToString());
            return new DocumentValidationResult(
                unavailable ? PlanValidationState.Unavailable : PlanValidationState.Invalid,
                message,
                AppendIfMissing(issues, message));
        }

        var validationName = validation?.Status.ToString() ?? "Unspecified";
        var state = validationName switch
        {
            "Ok" => PlanValidationState.Valid,
            "Warning" => PlanValidationState.Warning,
            "Error" => PlanValidationState.Invalid,
            _ when domainStatus?.Ok == true => PlanValidationState.Valid,
            _ => PlanValidationState.Unavailable
        };
        var summary = state switch
        {
            PlanValidationState.Valid => validSummary,
            PlanValidationState.Warning => $"{validSummary} Logos returned warnings.",
            PlanValidationState.Invalid => "Logos rejected the plan during validation.",
            _ => "Logos did not return a usable validation result."
        };
        return new DocumentValidationResult(state, summary, issues);
    }

    public static GatewayCommandResult ToCommandResult(
        V1.CommandResult? command,
        V1.DomainStatus? domainStatus,
        V1.AuthorizationDecision? authorization,
        string successMessage,
        string? executionId = null,
        string? lifecycleState = null,
        double? progress = null)
    {
        if (authorization is { Allowed: false })
        {
            return new GatewayCommandResult(
                false,
                OperationalCommandState.Rejected,
                string.IsNullOrWhiteSpace(authorization.DeniedReasons)
                    ? "Logos denied authorization for the command."
                    : authorization.DeniedReasons,
                executionId,
                lifecycleState,
                progress);
        }

        var domainState = MapDomainState(domainStatus);
        var commandState = MapCommandState(command?.CommandStatus.ToString());
        var state = domainStatus is { Ok: false }
            ? domainState ?? OperationalCommandState.Failed
            : commandState ?? domainState ?? OperationalCommandState.Failed;
        var accepted = state is OperationalCommandState.Accepted or
            OperationalCommandState.InProgress or
            OperationalCommandState.Succeeded;
        var message = FirstNonEmpty(
            command?.Status?.Message,
            domainStatus?.Message,
            accepted ? successMessage : null,
            command?.CommandStatus.ToString()) ?? successMessage;

        return new GatewayCommandResult(
            accepted,
            state,
            message,
            executionId,
            lifecycleState,
            progress);
    }

    public static GatewayCommandResult UnsupportedCommand(string kind, string command)
        => new(
            false,
            OperationalCommandState.Rejected,
            $"Unsupported {kind} command '{command}'.");

    private static TEnum ParseEnum<TEnum>(string? value, string fallback)
        where TEnum : struct, System.Enum
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            System.Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed) &&
            Convert.ToInt32(parsed, CultureInfo.InvariantCulture) != 0)
        {
            return parsed;
        }

        return System.Enum.Parse<TEnum>(fallback, ignoreCase: true);
    }

    private static string DisplayEnum<TEnum>(TEnum value)
        where TEnum : struct, System.Enum
    {
        var text = value.ToString();
        return string.Equals(text, "Unspecified", StringComparison.OrdinalIgnoreCase)
            ? "Unknown"
            : text;
    }

    private static OperationalCommandState? MapCommandState(string? value)
        => value switch
        {
            "Accepted" => OperationalCommandState.Accepted,
            "Rejected" => OperationalCommandState.Rejected,
            "InProgress" => OperationalCommandState.InProgress,
            "Succeeded" => OperationalCommandState.Succeeded,
            "Failed" => OperationalCommandState.Failed,
            "Cancelled" => OperationalCommandState.Failed,
            _ => null
        };

    private static OperationalCommandState? MapDomainState(V1.DomainStatus? status)
    {
        if (status is null)
        {
            return null;
        }

        if (status.Ok)
        {
            return OperationalCommandState.Accepted;
        }

        return status.Code.ToString() switch
        {
            "InProgress" => OperationalCommandState.InProgress,
            "PermissionDenied" or "PolicyDenied" or "InvalidRequest" or
                "FailedPrecondition" or "NotReady" or "CapabilityUnavailable" => OperationalCommandState.Rejected,
            _ => OperationalCommandState.Failed
        };
    }

    private static bool IsUnavailable(string code)
        => code.Contains("Unavailable", StringComparison.OrdinalIgnoreCase) ||
           code.Contains("Timeout", StringComparison.OrdinalIgnoreCase);

    private static string DomainMessage(V1.DomainStatus status)
        => string.IsNullOrWhiteSpace(status.Message)
            ? status.Code.ToString()
            : $"{status.Code}: {status.Message}";

    private static string[] CollectIssues(params IEnumerable<V1.Issue>?[] sources)
        => sources
            .Where(source => source is not null)
            .SelectMany(source => source!)
            .Select(FormatIssue)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string FormatIssue(V1.Issue issue)
    {
        var prefix = string.IsNullOrWhiteSpace(issue.Code) ? string.Empty : $"{issue.Code}: ";
        var field = string.IsNullOrWhiteSpace(issue.FieldPath) ? string.Empty : $" [{issue.FieldPath}]";
        var hint = string.IsNullOrWhiteSpace(issue.Hint) ? string.Empty : $" Suggested action: {issue.Hint}";
        return $"{prefix}{issue.Message}{field}{hint}".Trim();
    }

    private static string[] AppendIfMissing(IReadOnlyList<string> issues, string message)
        => issues.Contains(message, StringComparer.Ordinal)
            ? issues.ToArray()
            : [.. issues, message];

    private static string? ReadAttribute(
        IDictionary<string, string> attributes,
        string key)
        => attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static DateTimeOffset? ToDateTimeOffset(Timestamp? timestamp)
        => timestamp is null
            ? null
            : new DateTimeOffset(timestamp.ToDateTime(), TimeSpan.Zero);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
