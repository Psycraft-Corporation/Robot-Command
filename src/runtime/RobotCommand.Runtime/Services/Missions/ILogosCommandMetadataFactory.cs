using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Missions;

public interface ILogosCommandMetadataFactory
{
    V1.CommandRequestMetadata Create(
        string operation,
        string targetId,
        string? requestId = null,
        string? correlationId = null,
        string? idempotencyKey = null);

    V1.RequestId CreateRequestId(string? value = null);

    V1.CorrelationId CreateCorrelationId(string? value = null);
}
