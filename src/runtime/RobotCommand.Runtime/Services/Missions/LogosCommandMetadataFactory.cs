using Google.Protobuf.WellKnownTypes;
using RobotCommand.Services;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Missions;

public sealed class LogosCommandMetadataFactory : ILogosCommandMetadataFactory
{
    private const string ClientName = "Robot Command";

    public V1.CommandRequestMetadata Create(
        string operation,
        string targetId,
        string? requestId = null,
        string? correlationId = null,
        string? idempotencyKey = null)
    {
        var normalizedOperation = NormalizeSegment(operation, "operation");
        var normalizedTarget = NormalizeSegment(targetId, "resource");
        var effectiveRequestId = NormalizeIdentifier(requestId, $"robot-command-{Guid.NewGuid():N}");
        var effectiveCorrelationId = NormalizeIdentifier(correlationId, $"robot-command-{Guid.NewGuid():N}");
        var effectiveIdempotencyKey = NormalizeIdentifier(
            idempotencyKey,
            $"robot-command:{normalizedOperation}:{normalizedTarget}:{Guid.NewGuid():N}");

        return new V1.CommandRequestMetadata
        {
            RequestId = CreateRequestId(effectiveRequestId),
            CorrelationId = CreateCorrelationId(effectiveCorrelationId),
            RequestedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            IdempotencyKey = effectiveIdempotencyKey,
            ClientName = ClientName,
            ClientVersion = ThisAssembly.Version
        };
    }

    public V1.RequestId CreateRequestId(string? value = null)
        => new()
        {
            RequestId_ = NormalizeIdentifier(value, $"robot-command-{Guid.NewGuid():N}")
        };

    public V1.CorrelationId CreateCorrelationId(string? value = null)
        => new()
        {
            CorrelationId_ = NormalizeIdentifier(value, $"robot-command-{Guid.NewGuid():N}")
        };

    private static string NormalizeIdentifier(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeSegment(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray();
        var normalized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}
