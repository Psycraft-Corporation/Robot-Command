using Google.Protobuf.WellKnownTypes;
using RobotCommand.Models;
using RobotCommand.Services.Operations;
using Xunit;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Tests.Operations;

public sealed class VehicleOperationProtoMapperTests
{
    [Fact]
    public void Recover_MapsToReturnHome()
    {
        Assert.Equal(
            V1.VehicleOperationKind.ReturnHome,
            VehicleOperationProtoMapper.ToProto(OperatorCommandKind.Recover));
    }

    [Fact]
    public void Takeoff_MapsKindAndAltitudeParameters()
    {
        Assert.Equal(
            V1.VehicleOperationKind.Takeoff,
            VehicleOperationProtoMapper.ToProto(OperatorCommandKind.Takeoff));

        var parameters = VehicleOperationProtoMapper.ToProtoParameters(
            OperatorCommandKind.Takeoff,
            new OperatorCommandParameters(7.5));

        Assert.NotNull(parameters);
        Assert.NotNull(parameters!.Takeoff);
        Assert.Equal(7.5, parameters.Takeoff.AltitudeAglM, 6);
    }

    [Fact]
    public void GoTo_MapsGlobalAndLocalTargets()
    {
        Assert.Equal(
            V1.VehicleOperationKind.GoTo,
            VehicleOperationProtoMapper.ToProto(OperatorCommandKind.GoTo));

        var global = VehicleOperationProtoMapper.ToProtoParameters(
            OperatorCommandKind.GoTo,
            OperatorCommandParameters.GlobalGoTo(43.65, -79.38, 125, 3));
        Assert.NotNull(global?.GoTo?.GlobalWgs84);
        Assert.Equal(43.65, global!.GoTo.GlobalWgs84.LatitudeDeg, 6);
        Assert.Equal(-79.38, global.GoTo.GlobalWgs84.LongitudeDeg, 6);
        Assert.Equal(125, global.GoTo.GlobalWgs84.AltitudeAmslM, 6);
        Assert.Equal(3, global.GoTo.GlobalWgs84.AcceptanceRadiusM, 6);

        var local = VehicleOperationProtoMapper.ToProtoParameters(
            OperatorCommandKind.GoTo,
            OperatorCommandParameters.LocalGoTo(25, -10, -5, 90, 2));
        Assert.NotNull(local?.GoTo?.LocalNed);
        Assert.Equal(25, local!.GoTo.LocalNed.NorthM, 6);
        Assert.Equal(-10, local.GoTo.LocalNed.EastM, 6);
        Assert.Equal(-5, local.GoTo.LocalNed.DownM, 6);
        Assert.Equal(Math.PI / 2, local.GoTo.LocalNed.YawRad, 6);
        Assert.Equal(2, local.GoTo.LocalNed.AcceptanceRadiusM, 6);
    }

    [Fact]
    public void ChangeAltitude_MapsEveryTargetVariant()
    {
        Assert.Equal(
            V1.VehicleOperationKind.ChangeAltitude,
            VehicleOperationProtoMapper.ToProto(OperatorCommandKind.ChangeAltitude));

        var amsl = VehicleOperationProtoMapper.ToProtoParameters(
            OperatorCommandKind.ChangeAltitude,
            OperatorCommandParameters.ChangeAltitudeAmsl(125));
        Assert.Equal(125, amsl!.ChangeAltitude.AltitudeAmslM, 6);
        Assert.Equal(
            V1.ChangeAltitudeParameters.TargetOneofCase.AltitudeAmslM,
            amsl.ChangeAltitude.TargetCase);

        var agl = VehicleOperationProtoMapper.ToProtoParameters(
            OperatorCommandKind.ChangeAltitude,
            OperatorCommandParameters.ChangeAltitudeAgl(30));
        Assert.Equal(30, agl!.ChangeAltitude.AltitudeAglM, 6);
        Assert.Equal(
            V1.ChangeAltitudeParameters.TargetOneofCase.AltitudeAglM,
            agl.ChangeAltitude.TargetCase);

        var relative = VehicleOperationProtoMapper.ToProtoParameters(
            OperatorCommandKind.ChangeAltitude,
            OperatorCommandParameters.ChangeAltitudeRelative(-5));
        Assert.Equal(-5, relative!.ChangeAltitude.RelativeDeltaM, 6);
        Assert.Equal(
            V1.ChangeAltitudeParameters.TargetOneofCase.RelativeDeltaM,
            relative.ChangeAltitude.TargetCase);
    }

    [Fact]
    public void SetHeading_MapsAbsoluteAndRelativeDegreesToRadians()
    {
        Assert.Equal(
            V1.VehicleOperationKind.SetHeading,
            VehicleOperationProtoMapper.ToProto(OperatorCommandKind.SetHeading));

        var absolute = VehicleOperationProtoMapper.ToProtoParameters(
            OperatorCommandKind.SetHeading,
            OperatorCommandParameters.AbsoluteHeading(90));
        Assert.Equal(Math.PI / 2, absolute!.SetHeading.HeadingRad, 6);
        Assert.Equal(
            V1.SetHeadingParameters.TargetOneofCase.HeadingRad,
            absolute.SetHeading.TargetCase);

        var relative = VehicleOperationProtoMapper.ToProtoParameters(
            OperatorCommandKind.SetHeading,
            OperatorCommandParameters.RelativeYaw(-45));
        Assert.Equal(-Math.PI / 4, relative!.SetHeading.RelativeYawRad, 6);
        Assert.Equal(
            V1.SetHeadingParameters.TargetOneofCase.RelativeYawRad,
            relative.SetHeading.TargetCase);
    }

    [Fact]
    public void ArmDisarmAndLand_RemainExplicitMappings()
    {
        Assert.Equal(V1.VehicleOperationKind.Arm, VehicleOperationProtoMapper.ToProto(OperatorCommandKind.Arm));
        Assert.Equal(V1.VehicleOperationKind.Disarm, VehicleOperationProtoMapper.ToProto(OperatorCommandKind.Disarm));
        Assert.Equal(V1.VehicleOperationKind.Land, VehicleOperationProtoMapper.ToProto(OperatorCommandKind.Land));
    }

    [Fact]
    public void Preparation_PreservesImmutableTargetAndToken()
    {
        var now = DateTimeOffset.UtcNow;
        var response = new V1.PrepareOperationResponse
        {
            Status = new V1.DomainStatus { Ok = true, Message = "Prepared" },
            Preparation = new V1.PreparedOperation
            {
                PreparationId = "prepare-1",
                ConfirmationToken = "secret-token",
                OperationType = "vehicle.hold",
                PreparedAt = Timestamp.FromDateTime(now.UtcDateTime),
                ExpiresAt = Timestamp.FromDateTime(now.AddMinutes(1).UtcDateTime),
                Authorization = new V1.AuthorizationDecision { Allowed = true },
                Readiness = new V1.HealthStatus { Readiness = V1.ReadinessLevel.Ready },
                Target = new V1.PreparedOperationTarget
                {
                    LogosInstanceId = "logos-1",
                    VehicleId = "vehicle-1",
                    VehicleBindingGeneration = "binding-7",
                    ControlStateVersion = 42
                }
            },
            Readiness = new V1.OperationalReadiness
            {
                Readiness = new V1.HealthStatus { Readiness = V1.ReadinessLevel.Ready },
                Authorization = new V1.AuthorizationDecision { Allowed = true }
            }
        };

        var result = VehicleOperationProtoMapper.ToPreparationResult(response);

        Assert.True(result.Accepted);
        Assert.NotNull(result.Preparation);
        Assert.Equal("prepare-1", result.Preparation!.Reference.PreparationId);
        Assert.Equal("secret-token", result.Preparation.Reference.ConfirmationToken);
        Assert.Equal("vehicle-1", result.Preparation.Target.VehicleId);
        Assert.Equal("binding-7", result.Preparation.Target.VehicleBindingGeneration);
        Assert.Equal((ulong)42, result.Preparation.Target.ControlStateVersion);
    }

    [Fact]
    public void Execution_MapsAcceptedOperation()
    {
        var response = new V1.ExecuteOperationResponse
        {
            Authorization = new V1.AuthorizationDecision { Allowed = true },
            Result = new V1.CommandResult
            {
                CommandStatus = V1.CommandStatus.Accepted,
                Status = new V1.DomainStatus { Ok = true, Message = "Accepted" },
                Operation = new V1.OperationRef { OperationId = "operation-1" }
            },
            Operation = new V1.VehicleOperation
            {
                OperationId = "operation-1",
                State = V1.OperationState.Running,
                Status = new V1.DomainStatus { Ok = true, Message = "Running" }
            }
        };

        var result = VehicleOperationProtoMapper.ToCommandResult(response);

        Assert.True(result.Accepted);
        Assert.Equal(OperationalCommandState.InProgress, result.State);
        Assert.Equal("operation-1", result.OperationId);
    }

    [Fact]
    public void Execution_MapsCancelledOperationSeparatelyFromFailure()
    {
        var response = new V1.ExecuteOperationResponse
        {
            Authorization = new V1.AuthorizationDecision { Allowed = true },
            Operation = new V1.VehicleOperation
            {
                OperationId = "operation-2",
                State = V1.OperationState.Cancelled,
                Status = new V1.DomainStatus { Ok = false, Message = "Cancelled" }
            }
        };

        var result = VehicleOperationProtoMapper.ToCommandResult(response);

        Assert.False(result.Accepted);
        Assert.Equal(OperationalCommandState.Cancelled, result.State);
    }
}
