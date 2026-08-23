using System.Collections.Concurrent;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Simulation;
using RobotCommand.Services.Workflows;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class FormationLockWorkflowTests
{
    [Fact]
    public async Task AuthoredFormationAssignment_AutoAssignsAndSwapsStableSlots()
    {
        var root = Path.Combine(Path.GetTempPath(), "robotcommand-assignment-" + Guid.NewGuid().ToString("N"));
        try
        {
            var units = new MutableUnits(
                Ghost("ghost-1", 43.70000, -79.40000, 20),
                Ghost("ghost-2", 43.70010, -79.39990, 20));
            var teams = new UnitTeamWorkflow(root, units);
            var team = await teams.CreateAsync(["ghost-1", "ghost-2"]);
            var library = new FormationLibraryStore(root);
            var authoring = new FormationAuthoringWorkflow(library);
            var formation = await authoring.CreateAsync(new FormationCreateRequest("Line"));
            formation = await authoring.AddMemberAsync(formation.Id, new FormationMemberRequest("Left", -5, 0, 0));
            formation = await authoring.AddMemberAsync(formation.Id, new FormationMemberRequest("Right", 5, 0, 0));
            var reviewed = new ReviewedOperationWorkflow();
            var lockWorkflow = new FormationLockWorkflow(teams, units, new RecordingGhostService(), reviewed, [new GhostFormationLockExecutor()]);
            using var assignment = new FormationAssignmentWorkflow(teams, authoring, units, lockWorkflow, reviewed);

            var assigned = await assignment.AssignAsync(new(team.Id, formation.Id, 90));
            Assert.Equal(FormationAssignmentState.Assigned, assigned.State);
            Assert.Equal(["Left", "Right"], assigned.Assignments.Select(item => item.SlotName));

            var firstUnit = assigned.Assignments[0].UnitId;
            var secondSlot = assigned.Assignments[1].SlotId;
            var swapped = await assignment.SwapSlotAsync(team.Id, firstUnit, secondSlot);
            Assert.Equal(secondSlot, swapped.Assignments.Single(item => item.UnitId == firstUnit).SlotId);
            Assert.Equal("Right", swapped.Assignments.Single(item => item.UnitId == firstUnit).SlotName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Lock_CapturesStableTeamPosition_AndMembershipChangesDoNotMoveIt()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 20),
            Ghost("ghost-2", 43.70010, -79.39990, 24),
            Ghost("ghost-3", 43.70020, -79.39980, 28));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(["ghost-1", "ghost-2"]);
        var ghosts = new RecordingGhostService();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);

        var locked = await workflow.LockAsync(team.Id);
        var latitude = locked.TeamLatitudeDegrees;
        var longitude = locked.TeamLongitudeDegrees;
        var altitude = locked.TeamAltitudeAglMetres;

        await teams.AssignAsync(team.Id, ["ghost-3"]);
        await EventuallyAsync(() => workflow.Current.Members.Count == 3);

        Assert.Equal(latitude, workflow.Current.TeamLatitudeDegrees);
        Assert.Equal(longitude, workflow.Current.TeamLongitudeDegrees);
        Assert.Equal(altitude, workflow.Current.TeamAltitudeAglMetres);
        Assert.Equal(3, workflow.Current.Members.Count);
        Assert.Equal(3, ghosts.Targets.Count);
    }

    [Fact]
    public async Task UnitDropout_HoldsRemainingMembersAndUnlocks()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 20),
            Ghost("ghost-2", 43.70010, -79.39990, 20));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(["ghost-1", "ghost-2"]);
        var ghosts = new RecordingGhostService();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);
        await workflow.LockAsync(team.Id);

        await workflow.HandleUnitDeletedAsync("ghost-1");

        Assert.False(workflow.Current.IsLocked);
        Assert.Contains("deleted", workflow.Current.InterruptionReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ghosts.Cleared, item => item.VehicleId == "ghost-2" && item.Hold);
    }

    [Fact]
    public async Task TeamTranslation_IsReviewedAndRevalidatedBeforeExecution()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 20),
            Ghost("ghost-2", 43.70010, -79.39990, 20));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(["ghost-1", "ghost-2"]);
        var ghosts = new RecordingGhostService();
        var reviewed = new ReviewedOperationWorkflow();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, reviewed, [new GhostFormationLockExecutor()]);
        await workflow.StartAsync(CancellationToken.None);
        await workflow.LockAsync(team.Id);

        var plan = await workflow.PlanMoveToAsync(team.Id, 43.701, -79.401);

        Assert.True(plan.CanExecute);
        Assert.Equal(43.70005, workflow.Current.TargetLatitudeDegrees!.Value, 5);
        var execution = await reviewed.ExecuteAsync(plan.Id);
        Assert.True(execution.Succeeded);
        Assert.Equal(43.701, workflow.Current.TargetLatitudeDegrees!.Value, 5);
    }

    [Fact]
    public async Task TeamAltitude_IsPushedToEveryMemberWhenReviewedOperationExecutes()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 20),
            Ghost("ghost-2", 43.70010, -79.39990, 20));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(["ghost-1", "ghost-2"]);
        var ghosts = new RecordingGhostService();
        var reviewed = new ReviewedOperationWorkflow();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, reviewed, [new GhostFormationLockExecutor()]);
        await workflow.StartAsync(CancellationToken.None);
        await workflow.LockAsync(team.Id);

        var plan = await workflow.PlanChangeAltitudeAsync(team.Id, 22);
        var result = await reviewed.ExecuteAsync(plan.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(22, workflow.Current.TargetAltitudeAglMetres);
        // The reviewed operation changes the virtual Team target. The 60 Hz
        // controller then streams the intermediate targets; Ghosts must not
        // be commanded to jump directly to the final formation altitude.
        await EventuallyAsync(() => ghosts.Targets.Count == 2 && ghosts.Targets.ToArray().All(target =>
            target.Target.AltitudeAglMetres > 20d && target.Target.AltitudeAglMetres <= 22d), 3000);
        await EventuallyAsync(() => workflow.Current.State == FormationLockState.Locked &&
                                  Math.Abs(workflow.Current.TeamAltitudeAglMetres!.Value - 22d) < 0.001d &&
                                  ghosts.Targets.All(target => Math.Abs(target.Target.AltitudeAglMetres - 22d) < 0.001d &&
                                      Math.Abs(target.Target.VelocityUpMetresPerSecond) < 0.0001d), 30000);
    }

    [Fact]
    public async Task MixedGhostAndPx4Lock_UsesPerMemberExecutors_AndReleasesPx4ToHold()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 20),
            Ghost("ghost-2", 43.70012, -79.39988, 22),
            Px4("px4-1", 43.70024, -79.39976, 24));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(["ghost-1", "ghost-2", "px4-1"]);
        var ghosts = new RecordingGhostService();
        var px4 = new RecordingPx4FormationExecutor();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor(), px4]);

        var locked = await workflow.LockAsync(team.Id);

        Assert.True(locked.IsLocked);
        Assert.Equal(3, locked.Members.Count);
        Assert.Equal(FormationMemberControlState.Active, locked.Members.Single(member => member.UnitId == "px4-1").ControlState);
        Assert.Single(px4.Begun);
        Assert.NotEqual(locked.TeamLatitudeDegrees, units.Units.Single(unit => unit.Id == "px4-1").Telemetry!.LatitudeDegrees);

        await workflow.MoveToAsync(team.Id, 43.701, -79.401);
        await EventuallyAsync(() => !px4.Updated.IsEmpty, 5000);
        await workflow.RotateAsync(team.Id, 45);
        await workflow.ScaleAsync(team.Id, 150);
        await EventuallyAsync(() => px4.Updated.Select(target => target.TargetRevision).Distinct().Count() >= 2, 5000);

        await workflow.UnlockAsync(team.Id);
        Assert.Single(px4.Released);
        Assert.False(workflow.Current.IsLocked);
    }

    [Fact]
    public async Task ArduPilotBrakeHold_IsEligibleForFormationActivation()
    {
        var unit = Px4("ardupilot-1", 43.70000, -79.40000, 20) with
        {
            ProfileKey = "ardupilot",
            Name = "ArduPilot System 1",
            ConnectionIds = ["ardupilot-connection"],
            Telemetry = Px4("ardupilot-1", 43.70000, -79.40000, 20).Telemetry! with
            {
                Mode = "Brake"
            }
        };

        var executor = new ArduPilotFormationLockExecutor(new MavlinkConnectionRegistry());
        var findings = await executor.ValidateAsync([unit]);

        Assert.DoesNotContain(findings, finding =>
            finding.Code == "FORMATION_MOTION_OWNED");
    }

    [Fact]
    public async Task MixedManeuver_StopsTheWholeFormationWhenOneBackendDoesNotConverge()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 20),
            Ghost("ghost-2", 43.70010, -79.39990, 20),
            Px4("px4-1", 43.70020, -79.39980, 20));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(units.Units.Select(unit => unit.Id).ToArray());
        var ghosts = new RecordingGhostService();
        var px4 = new RecordingPx4FormationExecutor { Converges = false };
        await using var workflow = new FormationLockWorkflow(
            teams, units, ghosts, new ReviewedOperationWorkflow(),
            [new GhostFormationLockExecutor(), px4]);
        await workflow.StartAsync(CancellationToken.None);
        await workflow.LockAsync(team.Id);

        await workflow.RotateAsync(team.Id, 90d);
        await EventuallyAsync(
            () => !workflow.Current.IsLocked &&
                  workflow.Current.InterruptionCode == "FORMATION_MEMBER_NOT_CONVERGING",
            25000);

        Assert.Contains(workflow.Current.Findings,
            finding => finding.Code == "FORMATION_MEMBER_NOT_CONVERGING");
        Assert.Contains("PX4", workflow.Current.InterruptionReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, ghosts.Cleared.Count);
        Assert.All(ghosts.Cleared, item => Assert.True(item.Hold));
    }

    [Fact]
    public async Task ReviewedAltitudeChange_DrivesLiveGhostFormationVelocityPath()
    {
        var stores = new LiveGhostStores();
        await using var ghosts = stores.CreateGhostService();
        await ghosts.StartAsync(CancellationToken.None);
        var ghostUnits = new[]
        {
            await ghosts.CreateAsync(),
            await ghosts.CreateAsync(),
            await ghosts.CreateAsync(),
            await ghosts.CreateAsync(),
            await ghosts.CreateAsync()
        };

        for (var index = 0; index < ghostUnits.Length; index++)
        {
            await ArmAsync(ghosts, ghostUnits[index].Id, $"arm-{index}");
            await TakeoffAsync(ghosts, ghostUnits[index].Id, $"takeoff-{index}");
        }
        await EventuallyAsync(() =>
        {
            var telemetry = SnapshotTelemetry(stores, ghostUnits.Length);
            return telemetry.Length == ghostUnits.Length &&
                   telemetry.All(item => item.AltitudeAglMetres >= 4.9 && string.Equals(item.AdapterState, "Simulated", StringComparison.Ordinal));
        }, 4000);

        var units = new LiveUnits(stores);
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(ghostUnits.Select(unit => unit.Id).ToArray());
        var reviewed = new ReviewedOperationWorkflow();
        await using var formation = new FormationLockWorkflow(teams, units, ghosts, reviewed, [new GhostFormationLockExecutor()]);
        await formation.StartAsync(CancellationToken.None);
        await formation.LockAsync(team.Id);

        var plan = await formation.PlanChangeAltitudeAsync(team.Id, 30);
        Assert.True(plan.CanExecute);
        var execution = await reviewed.ExecuteAsync(plan.Id);
        Assert.True(execution.Succeeded);

        // The formation workflow refreshes absolute member targets at the
        // physics cadence. Reapplying a target must not reset each member's
        // velocity, otherwise the formation only creeps upward.
        for (var index = 0; index < 20; index++)
        {
            units.RaiseChanged();
            await Task.Delay(100);
        }
        await EventuallyAsync(
            () => SnapshotTelemetry(stores, ghostUnits.Length).All(item => item.IsGhost && item.AltitudeAglMetres > 15),
            10000);
        Assert.Equal(30, formation.Current.TargetAltitudeAglMetres);
    }

    [Fact]
    public async Task LiveGhosts_RepeatedCombinedGoToRotateScale_SettleAtTheCurrentFormationPose()
    {
        const double earthRadiusMetres = 6378137d;
        var stores = new LiveGhostStores();
        await using var ghosts = stores.CreateGhostService();
        await ghosts.StartAsync(CancellationToken.None);
        var ghostUnits = new[]
        {
            await ghosts.CreateAsync(),
            await ghosts.CreateAsync(),
            await ghosts.CreateAsync()
        };

        for (var index = 0; index < ghostUnits.Length; index++)
        {
            await ArmAsync(ghosts, ghostUnits[index].Id, $"arm-combined-{index}");
            await TakeoffAsync(ghosts, ghostUnits[index].Id, $"takeoff-combined-{index}");
        }
        await EventuallyAsync(() =>
        {
            var telemetry = SnapshotTelemetry(stores, ghostUnits.Length);
            return telemetry.Length == ghostUnits.Length &&
                   telemetry.All(item => item.AltitudeAglMetres >= 4.9 && string.Equals(item.AdapterState, "Simulated", StringComparison.Ordinal));
        }, 10000);

        var initial = SnapshotTelemetry(stores, ghostUnits.Length).Single(item => item.VehicleId == ghostUnits[0].Id);
        var baseLatitude = initial.LatitudeDegrees!.Value;
        var baseLongitude = initial.LongitudeDegrees!.Value;
        var offsets = new[] { (-4d, -3d), (0d, 4d), (5d, -1d) };
        for (var index = 0; index < ghostUnits.Length; index++)
        {
            var (north, east) = offsets[index];
            var latitude = baseLatitude + north / earthRadiusMetres * 180d / Math.PI;
            var longitude = baseLongitude + east / (earthRadiusMetres * Math.Cos(baseLatitude * Math.PI / 180d)) * 180d / Math.PI;
            await ghosts.SetFormationTargetAsync(ghostUnits[index].Id, new GhostFormationTarget("setup", latitude, longitude, 5d, 0d, 0d, 0d));
        }
        await EventuallyAsync(() => SnapshotTelemetry(stores, ghostUnits.Length).All(item => DistanceMetres(item.LatitudeDegrees!.Value, item.LongitudeDegrees!.Value, baseLatitude, baseLongitude) <= 7d), 15000);
        foreach (var ghost in ghostUnits)
            await ghosts.ClearFormationTargetAsync(ghost.Id, "setup", hold: true);

        var units = new LiveUnits(stores);
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(ghostUnits.Select(unit => unit.Id).ToArray());
        await using var formation = new FormationLockWorkflow(teams, units, ghosts, new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);
        await formation.StartAsync(CancellationToken.None);
        await formation.LockAsync(team.Id);

        var first = formation.Current;
        await formation.MoveToAsync(team.Id, first.TeamLatitudeDegrees!.Value + 12d / earthRadiusMetres * 180d / Math.PI, first.TeamLongitudeDegrees!.Value);
        await formation.RotateAsync(team.Id, 60d);
        await formation.ScaleAsync(team.Id, 150d);
        await EventuallyAsync(() => formation.Current.State == FormationLockState.Locked, 30000);

        var second = formation.Current;
        await formation.MoveToAsync(team.Id, second.TeamLatitudeDegrees!.Value, second.TeamLongitudeDegrees!.Value + 10d / (earthRadiusMetres * Math.Cos(second.TeamLatitudeDegrees.Value * Math.PI / 180d)) * 180d / Math.PI);
        await formation.ScaleAsync(team.Id, 75d);
        await EventuallyAsync(() => formation.Current.State == FormationLockState.Locked, 30000);

        // FormationLockState.Locked is based on the workflow's convergence
        // window and the latest observation. The Ghost simulator publishes
        // telemetry independently, so a fixed delay can sample the one
        // intermediate frame just before its final braking tick. Wait for
        // the observable telemetry contract instead of coupling this test to
        // scheduler timing.
        await EventuallyAsync(() =>
        {
            var telemetry = SnapshotTelemetry(stores, ghostUnits.Length);
            return telemetry.Length == ghostUnits.Length && telemetry.All(sample =>
                Math.Abs(sample.VelocityNorthMetresPerSecond ?? double.PositiveInfinity) <= 0.35d &&
                Math.Abs(sample.VelocityEastMetresPerSecond ?? double.PositiveInfinity) <= 0.35d);
        }, 5000);

        var settled = formation.Current;
        var samples = SnapshotTelemetry(stores, ghostUnits.Length).ToDictionary(item => item.VehicleId);
        foreach (var member in settled.Members)
        {
            var expectedLatitude = settled.TeamLatitudeDegrees!.Value + member.TargetNorthOffsetMetres!.Value / earthRadiusMetres * 180d / Math.PI;
            var expectedLongitude = settled.TeamLongitudeDegrees!.Value + member.TargetEastOffsetMetres!.Value / (earthRadiusMetres * Math.Cos(settled.TeamLatitudeDegrees.Value * Math.PI / 180d)) * 180d / Math.PI;
            var sample = samples[member.UnitId];
            Assert.InRange(DistanceMetres(sample.LatitudeDegrees!.Value, sample.LongitudeDegrees!.Value, expectedLatitude, expectedLongitude), 0d, 0.8d);
            Assert.InRange(Math.Abs(sample.VelocityNorthMetresPerSecond ?? 0d), 0d, 0.35d);
            Assert.InRange(Math.Abs(sample.VelocityEastMetresPerSecond ?? 0d), 0d, 0.35d);
        }

        var beforeHold = samples.ToDictionary(pair => pair.Key, pair => (pair.Value.LatitudeDegrees!.Value, pair.Value.LongitudeDegrees!.Value));
        await formation.HoldAsync(team.Id);
        await Task.Delay(750);
        foreach (var sample in SnapshotTelemetry(stores, ghostUnits.Length))
        {
            var previous = beforeHold[sample.VehicleId];
            Assert.InRange(DistanceMetres(sample.LatitudeDegrees!.Value, sample.LongitudeDegrees!.Value, previous.Item1, previous.Item2), 0d, 0.3d);
            Assert.InRange(Math.Abs(sample.VelocityNorthMetresPerSecond ?? 0d), 0d, 0.2d);
            Assert.InRange(Math.Abs(sample.VelocityEastMetresPerSecond ?? 0d), 0d, 0.2d);
        }
    }

    [Fact]
    public async Task GhostRotation_IsClockwiseAroundStableTeamPosition_AndPreservesShape()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 18),
            Ghost("ghost-2", 43.70010, -79.39990, 22),
            Ghost("ghost-3", 43.70020, -79.40000, 26));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(units.Units.Select(unit => unit.Id).ToArray());
        var ghosts = new RecordingGhostService();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);
        await workflow.StartAsync(CancellationToken.None);
        var locked = await workflow.LockAsync(team.Id);
        var fixedLatitude = Assert.IsType<double>(locked.TeamLatitudeDegrees);
        var fixedLongitude = Assert.IsType<double>(locked.TeamLongitudeDegrees);
        var original = locked.Members.ToDictionary(member => member.UnitId);

        await workflow.RotateAsync(team.Id, 90);
        await Task.Delay(150);
        Assert.Contains(ghosts.Targets, target => Math.Abs(target.Target.VelocityNorthMetresPerSecond) + Math.Abs(target.Target.VelocityEastMetresPerSecond) > 0.01d);
        var first = original["ghost-1"];
        var arcSample = ghosts.TargetHistory.ToArray()
            .Where(entry => entry.VehicleId == "ghost-1" && Math.Abs(entry.Target.VelocityNorthMetresPerSecond) + Math.Abs(entry.Target.VelocityEastMetresPerSecond) > 0.01d)
            .Select(entry =>
            {
                var north = (entry.Target.LatitudeDegrees - fixedLatitude) * Math.PI / 180d * 6378137d;
                var east = (entry.Target.LongitudeDegrees - fixedLongitude) * Math.PI / 180d * 6378137d * Math.Cos(fixedLatitude * Math.PI / 180d);
                var degrees = Math.Atan2(first.NorthOffsetMetres * east - first.EastOffsetMetres * north,
                    first.NorthOffsetMetres * north + first.EastOffsetMetres * east) * 180d / Math.PI;
                return (North: north, East: east, Degrees: degrees);
            })
            .First(sample => sample.Degrees > 0.01d && sample.Degrees < 89.99d);
        Assert.Equal(Math.Sqrt(first.NorthOffsetMetres * first.NorthOffsetMetres + first.EastOffsetMetres * first.EastOffsetMetres),
            Math.Sqrt(arcSample.North * arcSample.North + arcSample.East * arcSample.East), 2);
        Assert.True(workflow.Current.CurrentRotationDegrees > 0d, $"Rotation did not advance: {workflow.Current.CurrentRotationDegrees:F3} / {workflow.Current.TargetRotationDegrees:F3}");
        await Task.Delay(1000);
        Assert.True(workflow.Current.IsLocked, workflow.Current.InterruptionReason ?? "Formation unexpectedly unlocked.");
        Assert.True(workflow.Current.CurrentRotationDegrees > 5d,
            $"Rotation stalled: {workflow.Current.CurrentRotationDegrees:F3} / {workflow.Current.TargetRotationDegrees:F3}; {workflow.Current.InterruptionReason}");
        await EventuallyAsync(
            () => Math.Abs(workflow.Current.CurrentRotationDegrees - 90d) < 0.0001d,
            12000,
            () => $"Rotation stalled at {workflow.Current.CurrentRotationDegrees:F3} / {workflow.Current.TargetRotationDegrees:F3}; state={workflow.Current.State}; converged={workflow.Current.IsConverged}; reason={workflow.Current.InterruptionReason}; code={workflow.Current.InterruptionCode}");

        var rotated = workflow.Current;
        Assert.Equal(fixedLatitude, rotated.TeamLatitudeDegrees);
        Assert.Equal(fixedLongitude, rotated.TeamLongitudeDegrees);
        foreach (var member in rotated.Members)
        {
            var baseline = original[member.UnitId];
            Assert.Equal(-baseline.EastOffsetMetres, member.TargetNorthOffsetMetres!.Value, 3);
            Assert.Equal(baseline.NorthOffsetMetres, member.TargetEastOffsetMetres!.Value, 3);
            Assert.Equal(baseline.UpOffsetMetres, member.TargetUpOffsetMetres!.Value, 4);
        }
    }

    [Fact]
    public async Task GhostScale_ChangesHorizontalAndVerticalOffsets_WithoutMovingTeamPosition()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 10),
            Ghost("ghost-2", 43.70012, -79.39988, 20),
            Ghost("ghost-3", 43.69994, -79.39984, 30));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(units.Units.Select(unit => unit.Id).ToArray());
        var ghosts = new RecordingGhostService();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);
        await workflow.StartAsync(CancellationToken.None);
        var locked = await workflow.LockAsync(team.Id);
        var baseline = locked.Members.ToDictionary(member => member.UnitId);

        await workflow.ScaleAsync(team.Id, 150);
        await Task.Delay(1000);
        Assert.True(workflow.Current.CurrentScalePercent > 105d,
            $"Scale stalled: {workflow.Current.CurrentScalePercent:F3} / {workflow.Current.TargetScalePercent:F3}; {workflow.Current.InterruptionReason}");
        // The formation controller intentionally runs on a background timer.
        // Allow enough wall-clock time when the complete xUnit suite is
        // saturating the shared thread pool; the motion rate itself is
        // asserted above and must not depend on test-run parallelism.
        await EventuallyAsync(() => Math.Abs(workflow.Current.CurrentScalePercent - 150d) < 0.0001d, 30000);

        var expanded = workflow.Current;
        Assert.Equal(locked.TeamLatitudeDegrees, expanded.TeamLatitudeDegrees);
        Assert.Equal(locked.TeamLongitudeDegrees, expanded.TeamLongitudeDegrees);
        Assert.Equal(locked.TeamAltitudeAglMetres, expanded.TeamAltitudeAglMetres);
        foreach (var member in expanded.Members)
        {
            var original = baseline[member.UnitId];
            Assert.Equal(original.NorthOffsetMetres * 1.5d, member.TargetNorthOffsetMetres!.Value, 3);
            Assert.Equal(original.EastOffsetMetres * 1.5d, member.TargetEastOffsetMetres!.Value, 3);
            Assert.Equal(original.UpOffsetMetres * 1.5d, member.TargetUpOffsetMetres!.Value, 3);
        }

        await workflow.ScaleAsync(team.Id, 50);
        await EventuallyAsync(() => Math.Abs(workflow.Current.CurrentScalePercent - 50d) < 0.0001d, 30000);
        Assert.All(workflow.Current.Members, member => Assert.Equal(baseline[member.UnitId].UpOffsetMetres * .5d, member.TargetUpOffsetMetres!.Value, 3));
    }

    [Fact]
    public async Task ConcurrentTranslationAndRotation_KeepEveryMemberWithinTheCombinedMotionBudget()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 20),
            Ghost("ghost-2", 43.70014, -79.39990, 20),
            Ghost("ghost-3", 43.70024, -79.40005, 20));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(units.Units.Select(unit => unit.Id).ToArray());
        var ghosts = new RecordingGhostService();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);
        await workflow.StartAsync(CancellationToken.None);
        await workflow.LockAsync(team.Id);

        await workflow.MoveToAsync(team.Id, 43.70200, -79.40200);
        await workflow.RotateAsync(team.Id, 90d);
        await EventuallyAsync(
            () => ghosts.TargetHistory.Any(target => Math.Sqrt(
                target.Target.VelocityNorthMetresPerSecond * target.Target.VelocityNorthMetresPerSecond +
                target.Target.VelocityEastMetresPerSecond * target.Target.VelocityEastMetresPerSecond) > 0.01d),
            5000);

        var movingTargets = ghosts.TargetHistory.ToArray()
            .Where(target => Math.Sqrt(
                target.Target.VelocityNorthMetresPerSecond * target.Target.VelocityNorthMetresPerSecond +
                target.Target.VelocityEastMetresPerSecond * target.Target.VelocityEastMetresPerSecond) > 0.01d)
            .ToArray();
        Assert.NotEmpty(movingTargets);
        Assert.All(movingTargets, target => Assert.InRange(
            Math.Sqrt(target.Target.VelocityNorthMetresPerSecond * target.Target.VelocityNorthMetresPerSecond +
                target.Target.VelocityEastMetresPerSecond * target.Target.VelocityEastMetresPerSecond),
            0d, 7.001d));
    }

    [Fact]
    public async Task ConcurrentTranslationAndRotation_PublishesAZeroVelocityHoldAtTheFinalTarget()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 20),
            Ghost("ghost-2", 43.70006, -79.39994, 20),
            Ghost("ghost-3", 43.70012, -79.40000, 20));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(units.Units.Select(unit => unit.Id).ToArray());
        var ghosts = new RecordingGhostService();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);
        await workflow.StartAsync(CancellationToken.None);
        await workflow.LockAsync(team.Id);

        // Keep this translation short: the test verifies the terminal hold
        // contract, while the separate regression below covers a transform
        // completing during a long Go To. A short concurrent motion avoids
        // making the terminal assertion depend on a heavily loaded test host.
        await workflow.MoveToAsync(team.Id, 43.700015, -79.399985);
        await workflow.RotateAsync(team.Id, 18d);

        await EventuallyAsync(() => workflow.Current.State == FormationLockState.Locked &&
                                  Math.Abs(workflow.Current.CurrentRotationDegrees - 18d) < 0.0001d,
            20000);
        await EventuallyAsync(() => workflow.Current.TeamLatitudeDegrees is { } latitude &&
                                  workflow.Current.TeamLongitudeDegrees is { } longitude &&
                                  workflow.Current.TargetLatitudeDegrees is { } targetLatitude &&
                                  workflow.Current.TargetLongitudeDegrees is { } targetLongitude &&
                                  Math.Abs(latitude - targetLatitude) < 0.0000001d &&
                                  Math.Abs(longitude - targetLongitude) < 0.0000001d &&
                                  ghosts.Targets.Count == 3 && ghosts.Targets.All(target =>
                                      Math.Abs(target.Target.VelocityNorthMetresPerSecond) < 0.0001d &&
                                      Math.Abs(target.Target.VelocityEastMetresPerSecond) < 0.0001d &&
                                      Math.Abs(target.Target.VelocityUpMetresPerSecond) < 0.0001d),
            20000);

        Assert.All(ghosts.Targets, target =>
        {
            Assert.Equal(0d, target.Target.VelocityNorthMetresPerSecond, 6);
            Assert.Equal(0d, target.Target.VelocityEastMetresPerSecond, 6);
            Assert.Equal(0d, target.Target.VelocityUpMetresPerSecond, 6);
        });
    }

    [Fact]
    public async Task CompletedRotation_DuringAnOngoingGoTo_ContinuesToPublishCurrentTeamTargets()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 20),
            Ghost("ghost-2", 43.70008, -79.39992, 20),
            Ghost("ghost-3", 43.69992, -79.39988, 20));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(units.Units.Select(unit => unit.Id).ToArray());
        var ghosts = new RecordingGhostService();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);
        await workflow.StartAsync(CancellationToken.None);
        await workflow.LockAsync(team.Id);

        // The rotation completes well before this long translation. The
        // controller must keep publishing the current moving Team position,
        // not swap back to a fixed final position with a non-zero velocity.
        await workflow.MoveToAsync(team.Id, 43.70500, -79.39500);
        await workflow.RotateAsync(team.Id, 18d);
        await EventuallyAsync(() => Math.Abs(workflow.Current.CurrentRotationDegrees - 18d) < 0.0001d, 6000);
        Assert.Equal(FormationLockState.Moving, workflow.Current.State);

        var snapshot = workflow.Current;
        var member = Assert.Single(snapshot.Members, item => item.UnitId == "ghost-1");
        var actual = Assert.Single(ghosts.Targets, item => item.VehicleId == "ghost-1").Target;
        var expectedLatitude = snapshot.TeamLatitudeDegrees!.Value + member.TargetNorthOffsetMetres!.Value / 6378137d * 180d / Math.PI;
        var expectedLongitude = snapshot.TeamLongitudeDegrees!.Value + member.TargetEastOffsetMetres!.Value /
            (6378137d * Math.Cos(snapshot.TeamLatitudeDegrees.Value * Math.PI / 180d)) * 180d / Math.PI;
        var finalLatitude = snapshot.TargetLatitudeDegrees!.Value + member.TargetNorthOffsetMetres.Value / 6378137d * 180d / Math.PI;

        // The loop may advance one 60 Hz tick between reading Current and the
        // recorded target. Allow that bounded movement while still proving the
        // controller is publishing the live Team position rather than the
        // distant final destination.
        Assert.InRange(Math.Abs(expectedLatitude - actual.LatitudeDegrees), 0d, 0.00001d);
        Assert.InRange(Math.Abs(expectedLongitude - actual.LongitudeDegrees), 0d, 0.00001d);
        Assert.True(Math.Abs(actual.LatitudeDegrees - finalLatitude) > 0.00001d,
            "A moving formation must not publish the final position before the Team position arrives there.");
    }

    [Fact]
    public async Task RepeatedGoToAndResize_OnlyPublishCurrentTeamTargets_AndHoldFreezesTheFormation()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 20),
            Ghost("ghost-2", 43.70010, -79.39990, 24),
            Ghost("ghost-3", 43.69990, -79.39985, 16));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(units.Units.Select(unit => unit.Id).ToArray());
        var ghosts = new RecordingGhostService();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);
        await workflow.StartAsync(CancellationToken.None);
        await workflow.LockAsync(team.Id);

        await workflow.MoveToAsync(team.Id, 43.70400, -79.39600);
        await workflow.ScaleAsync(team.Id, 150d);
        await Task.Delay(150);
        await workflow.MoveToAsync(team.Id, 43.70600, -79.39400);
        await workflow.ScaleAsync(team.Id, 75d);
        await Task.Delay(150);

        var inFlight = workflow.Current;
        Assert.Equal(FormationLockState.Moving, inFlight.State);
        var firstTarget = Assert.Single(ghosts.Targets, item => item.VehicleId == "ghost-1").Target;
        var firstMember = Assert.Single(inFlight.Members, item => item.UnitId == "ghost-1");
        var expectedLatitude = inFlight.TeamLatitudeDegrees!.Value + firstMember.TargetNorthOffsetMetres!.Value / 6378137d * 180d / Math.PI;
        var finalLatitude = inFlight.TargetLatitudeDegrees!.Value + firstMember.TargetNorthOffsetMetres.Value / 6378137d * 180d / Math.PI;
        Assert.InRange(Math.Abs(firstTarget.LatitudeDegrees - expectedLatitude), 0d, 0.00001d);
        Assert.True(Math.Abs(firstTarget.LatitudeDegrees - finalLatitude) > 0.00001d,
            "Repeated formation actions must never publish a future final position while the Team is moving.");

        var holdTargetCount = ghosts.TargetHistory.Count;
        await workflow.HoldAsync(team.Id);
        var held = workflow.Current;
        Assert.Equal(FormationLockState.Locked, held.State);
        Assert.Equal(held.TeamLatitudeDegrees, held.TargetLatitudeDegrees);
        Assert.Equal(held.TeamLongitudeDegrees, held.TargetLongitudeDegrees);
        Assert.Equal(held.TeamAltitudeAglMetres, held.TargetAltitudeAglMetres);
        Assert.Equal(held.CurrentRotationDegrees, held.TargetRotationDegrees, 8);
        Assert.Equal(held.CurrentScalePercent, held.TargetScalePercent, 8);
        Assert.All(ghosts.Targets, target =>
        {
            Assert.Equal(0d, target.Target.VelocityNorthMetresPerSecond, 6);
            Assert.Equal(0d, target.Target.VelocityEastMetresPerSecond, 6);
            Assert.Equal(0d, target.Target.VelocityUpMetresPerSecond, 6);
        });

        await Task.Delay(300);
        var targetsAfterHold = ghosts.TargetHistory.ToArray().Skip(holdTargetCount).ToArray();
        Assert.NotEmpty(targetsAfterHold);
        Assert.All(targetsAfterHold, target =>
        {
            Assert.Equal(0d, target.Target.VelocityNorthMetresPerSecond, 6);
            Assert.Equal(0d, target.Target.VelocityEastMetresPerSecond, 6);
            Assert.Equal(0d, target.Target.VelocityUpMetresPerSecond, 6);
        });
    }

    [Fact]
    public async Task RotationAndScale_AcceptMixedFormationWhenEveryExecutorSupportsTransforms()
    {
        var units = new MutableUnits(Ghost("ghost-1", 43.7, -79.4, 20), Px4("px4-1", 43.7001, -79.3999, 22));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(units.Units.Select(unit => unit.Id).ToArray());
        await using var workflow = new FormationLockWorkflow(teams, units, new RecordingGhostService(), new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor(), new RecordingPx4FormationExecutor()]);
        await workflow.LockAsync(team.Id);

        var rotation = await workflow.PlanRotateAsync(team.Id, 90);
        var scale = await workflow.PlanScaleAsync(team.Id, 150);

        Assert.True(rotation.CanExecute, string.Join(" ", rotation.Findings.Select(finding => finding.Message)));
        Assert.True(scale.CanExecute, string.Join(" ", scale.Findings.Select(finding => finding.Message)));

        await workflow.RotateAsync(team.Id, 90);
        await workflow.ScaleAsync(team.Id, 150);

        Assert.Equal(90d, workflow.Current.TargetRotationDegrees, 6);
        Assert.Equal(150d, workflow.Current.TargetScalePercent, 6);
    }

    [Fact]
    public async Task Hold_RebasesToTheObservedFormationPose_InsteadOfReturningToAnOldVirtualPoint()
    {
        const double earthRadiusMetres = 6378137d;
        const double northDriftMetres = 80d;
        var units = new MutableUnits(
            Ghost("ghost-1", 43.70000, -79.40000, 20),
            Ghost("ghost-2", 43.70010, -79.39990, 22),
            Ghost("ghost-3", 43.69995, -79.39980, 24));
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(units.Units.Select(unit => unit.Id).ToArray());
        var ghosts = new RecordingGhostService();
        await using var workflow = new FormationLockWorkflow(teams, units, ghosts, new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);
        var locked = await workflow.LockAsync(team.Id);

        foreach (var unit in units.Units.ToArray())
        {
            var telemetry = unit.Telemetry!;
            units.Update(unit with
            {
                Telemetry = telemetry with
                {
                    LatitudeDegrees = telemetry.LatitudeDegrees!.Value + northDriftMetres / earthRadiusMetres * 180d / Math.PI
                }
            });
        }

        var held = await workflow.HoldAsync(team.Id);

        Assert.Equal(FormationLockState.Locked, held.State);
        Assert.InRange((held.TeamLatitudeDegrees!.Value - locked.TeamLatitudeDegrees!.Value) * Math.PI / 180d * earthRadiusMetres,
            northDriftMetres - 0.25d, northDriftMetres + 0.25d);
        Assert.Equal(held.TeamLatitudeDegrees, held.TargetLatitudeDegrees);
        Assert.Equal(held.TeamLongitudeDegrees, held.TargetLongitudeDegrees);
        Assert.All(ghosts.Targets, item =>
        {
            Assert.Equal(0d, item.Target.VelocityNorthMetresPerSecond, 6);
            Assert.Equal(0d, item.Target.VelocityEastMetresPerSecond, 6);
            Assert.Equal(0d, item.Target.VelocityUpMetresPerSecond, 6);
        });
    }

    private static async Task ArmAsync(IGhostUnitService ghosts, string vehicleId, string commandId)
    {
        var target = new OperatorCommandTarget($"ghost-connection-{vehicleId[6..]}", vehicleId, null, DateTimeOffset.UtcNow);
        var result = await ghosts.ExecuteAsync(new OperatorCommandRequest(commandId, commandId, commandId,
            OperatorCommandKind.Arm, target, "test", false, DateTimeOffset.UtcNow));
        Assert.True(result.Accepted);
    }

    private static async Task TakeoffAsync(IGhostUnitService ghosts, string vehicleId, string commandId)
    {
        var target = new OperatorCommandTarget($"ghost-connection-{vehicleId[6..]}", vehicleId, null, DateTimeOffset.UtcNow);
        var result = await ghosts.ExecuteAsync(new OperatorCommandRequest(commandId, commandId, commandId,
            OperatorCommandKind.Takeoff, target, "test", false, DateTimeOffset.UtcNow,
            Parameters: new OperatorCommandParameters(TakeoffAltitudeAglMetres: 5)));
        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task Lock_RejectsLandedOrUnarmedMembers()
    {
        var units = new MutableUnits(
            Ghost("ghost-1", 43.7, -79.4, 20),
            Ghost("ghost-2", 43.7, -79.39, 20) with { Telemetry = Ghost("x", 43.7, -79.39, 20).Telemetry! with { Armed = false, LandedState = "Landed" } });
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(["ghost-1", "ghost-2"]);
        await using var workflow = new FormationLockWorkflow(teams, units, new RecordingGhostService(), new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.LockAsync(team.Id));

        Assert.Contains("armed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("airborne", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lock_RejectsArmedGroundMemberWithoutConfirmedAirborneTelemetry()
    {
        var groundTelemetry = Ghost("ground", 43.7, -79.39, 0.2).Telemetry! with
        {
            LandedState = "Unknown",
            AltitudeAglMetres = 0.2
        };
        var units = new MutableUnits(
            Ghost("ghost-1", 43.7, -79.4, 20),
            Ghost("ghost-2", 43.7, -79.39, 0.2) with { Telemetry = groundTelemetry });
        var teams = new UnitTeamWorkflow(Path.GetTempPath(), units);
        var team = await teams.CreateAsync(["ghost-1", "ghost-2"]);
        await using var workflow = new FormationLockWorkflow(teams, units, new RecordingGhostService(), new ReviewedOperationWorkflow(), [new GhostFormationLockExecutor()]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.LockAsync(team.Id));

        Assert.Contains("airborne telemetry", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.5", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static UnitObservationSnapshot Ghost(string id, double latitude, double longitude, double altitude)
        => new(
            id, id, [], null, null, "Multicopter", "Air", "Ghost", ManagedConnectionState.Online,
            "Ready", "Ready", "Armed", "Ready", [], DateTimeOffset.UtcNow, true,
            id, id, id,
            new UnitTelemetryObservation(ManagedConnectionState.Online, true, "Flying", "Simulated", latitude, longitude,
                100 + altitude, altitude, 0, 0, 0, 90, false, DateTimeOffset.UtcNow),
            null, [], new UnitActionObservation(null, null));

    private static double DistanceMetres(double fromLatitude, double fromLongitude, double toLatitude, double toLongitude)
    {
        const double earthRadiusMetres = 6378137d;
        var north = (toLatitude - fromLatitude) * Math.PI / 180d * earthRadiusMetres;
        var east = (toLongitude - fromLongitude) * Math.PI / 180d * earthRadiusMetres * Math.Cos(fromLatitude * Math.PI / 180d);
        return Math.Sqrt(north * north + east * east);
    }

    private static UnitObservationSnapshot Px4(string id, double latitude, double longitude, double altitude)
        => Ghost(id, latitude, longitude, altitude) with
        {
            IsGhost = false,
            ProfileKey = "px4",
            Name = "PX4 System 1",
            Telemetry = Ghost(id, latitude, longitude, altitude).Telemetry! with { Mode = "Hold" }
        };

    private static async Task EventuallyAsync(Func<bool> condition, int timeoutMilliseconds = 250, Func<string>? failureDetail = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), failureDetail?.Invoke() ?? "Expected condition was not reached.");
    }

    private static VehicleTelemetryRecord[] SnapshotTelemetry(LiveGhostStores stores, int expectedCount)
    {
        var telemetry = new List<VehicleTelemetryRecord>(expectedCount);
        for (var number = 1; number <= expectedCount; number++)
        {
            if (stores.Telemetry.TryGet($"ghost-telemetry-{number}", out var item) && item is not null)
                telemetry.Add(item);
        }

        return telemetry.ToArray();
    }

    private sealed class MutableUnits(params UnitObservationSnapshot[] units) : IUnitObservationWorkflow
    {
        private readonly List<UnitObservationSnapshot> _units = [.. units];
        public event EventHandler? Changed;
        public IReadOnlyList<UnitObservationSnapshot> Units => _units;
        public bool TryGet(string unitId, out UnitObservationSnapshot? unit)
        {
            unit = _units.FirstOrDefault(item => item.Id == unitId);
            return unit is not null;
        }
        public void Update(UnitObservationSnapshot unit)
        {
            var index = _units.FindIndex(item => item.Id == unit.Id);
            _units[index] = unit;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class RecordingGhostService : IGhostUnitService
    {
        private readonly object _targetGate = new();
        private readonly List<(string VehicleId, GhostFormationTarget Target)> _targets = [];
        public IReadOnlyList<(string VehicleId, GhostFormationTarget Target)> Targets
        {
            get { lock (_targetGate) return _targets.ToArray(); }
        }
        public ConcurrentQueue<(string VehicleId, GhostFormationTarget Target)> TargetHistory { get; } = new();
        public List<(string VehicleId, bool Hold)> Cleared { get; } = [];
        public IReadOnlyList<string> GhostVehicleIds => [];
        public bool IsGhostConnection(string connectionId) => true;
        public bool IsGhostVehicle(string vehicleId) => true;
        public Task<VehicleRecord> CreateAsync(MapViewportSnapshot? viewport = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VehicleRecord> CreateAsync(MapViewportSnapshot? viewport, double headingDegrees, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string vehicleId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteByConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public OperatorPolicyEvaluation EvaluatePolicy(OperatorPolicyRequest request) => throw new NotSupportedException();
        public Task<OperatorCommandPreparationResult> PrepareAsync(OperatorCommandRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorCommandResult> ExecuteAsync(OperatorCommandRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> BeginManualControlAsync(string vehicleId, string sessionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void UpdateManualControl(string vehicleId, string sessionId, ManualControlSetpoint setpoint) { }
        public Task EndManualControlAsync(string vehicleId, string sessionId, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFormationTargetAsync(string vehicleId, GhostFormationTarget target, CancellationToken cancellationToken = default)
        {
            lock (_targetGate)
            {
                _targets.RemoveAll(item => item.VehicleId == vehicleId);
                _targets.Add((vehicleId, target));
            }
            TargetHistory.Enqueue((vehicleId, target));
            return Task.CompletedTask;
        }
        public Task ClearFormationTargetAsync(string vehicleId, string lockId, bool hold, CancellationToken cancellationToken = default)
        {
            Cleared.Add((vehicleId, hold));
            return Task.CompletedTask;
        }
        public Task<FlightMissionExecutorResult> PrepareMissionAsync(string vehicleId, FlightMissionExecutionArtifact artifact, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlightMissionExecutorResult> StartMissionAsync(string vehicleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlightMissionExecutorResult> PauseMissionAsync(string vehicleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlightMissionExecutorResult> ResumeMissionAsync(string vehicleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlightMissionExecutorResult> PrepareMissionForResumeAsync(string vehicleId, FlightMissionExecutionArtifact artifact, int resumeItemIndex, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlightMissionExecutorResult> ClearMissionAsync(string vehicleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool TryGetMissionProgress(string vehicleId, out FlightMissionExecutorProgress progress) { progress = default!; return false; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingPx4FormationExecutor : IFormationLockExecutor
    {
        public bool Converges { get; init; } = true;
        public ConcurrentQueue<FormationMemberTarget> Begun { get; } = new();
        public ConcurrentQueue<FormationMemberTarget> Updated { get; } = new();
        public ConcurrentQueue<string> Released { get; } = new();
        public string Backend => "PX4 Offboard";
        public FormationControlCapabilities Capabilities =>
            FormationControlCapabilities.Translation |
            FormationControlCapabilities.Altitude |
            FormationControlCapabilities.Rotation |
            FormationControlCapabilities.Scale;
        public bool CanHandle(UnitObservationSnapshot unit) => !unit.IsGhost && string.Equals(unit.ProfileKey, "px4", StringComparison.OrdinalIgnoreCase);
        public bool IsTargetConverged(UnitObservationSnapshot unit, FormationMemberTarget target) => Converges;
        public Task<IReadOnlyList<FormationLockFinding>> ValidateAsync(IReadOnlyList<UnitObservationSnapshot> units, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FormationLockFinding>>([]);
        public Task<FormationExecutorResult> BeginAsync(UnitObservationSnapshot unit, FormationMemberTarget target, CancellationToken cancellationToken = default)
        {
            Begun.Enqueue(target);
            return Task.FromResult(FormationExecutorResult.AcceptedActive("PX4 Offboard active."));
        }
        public Task<FormationExecutorResult> UpdateAsync(UnitObservationSnapshot unit, FormationMemberTarget target, CancellationToken cancellationToken = default)
        {
            Updated.Enqueue(target);
            return Task.FromResult(FormationExecutorResult.AcceptedActive("PX4 Offboard target updated."));
        }
        public Task<FormationExecutorResult> HoldAndReleaseAsync(UnitObservationSnapshot unit, string lockId, CancellationToken cancellationToken = default)
        {
            Released.Enqueue(unit.Id);
            return Task.FromResult(new FormationExecutorResult(true, FormationMemberControlState.Holding, "PX4 Hold confirmed."));
        }
    }

    private sealed class LiveGhostStores
    {
        public EntityStore<string, ConnectionRecord> Connections { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, RuntimeRecord> Runtimes { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, VehicleRecord> Vehicles { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, VehicleTelemetryRecord> Telemetry { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, LinkRecord> Links { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, OperationalCommandRecord> Commands { get; } = new(item => item.Id, StringComparer.Ordinal);
        public SelectionService Selection { get; } = new();

        public GhostUnitService CreateGhostService()
            => new(Connections, Runtimes, Vehicles, Telemetry, Commands, Selection,
                new ImmediateDispatcher(), new AppConfiguration(), links: Links);
    }

    private sealed class LiveUnits(LiveGhostStores stores) : IUnitObservationWorkflow
    {
        public event EventHandler? Changed;
        public IReadOnlyList<UnitObservationSnapshot> Units => stores.Vehicles.Items.Select(Project).ToArray();

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public bool TryGet(string unitId, out UnitObservationSnapshot? unit)
        {
            unit = Units.FirstOrDefault(item => item.Id == unitId);
            return unit is not null;
        }

        private UnitObservationSnapshot Project(VehicleRecord vehicle)
        {
            // EntityStore publishes telemetry replacements asynchronously. Use
            // its keyed read instead of enumerating the live collection while
            // the 60 Hz Ghost service is publishing a batch.
            var number = vehicle.Id.StartsWith("ghost-", StringComparison.Ordinal)
                ? vehicle.Id["ghost-".Length..]
                : vehicle.Id;
            if (!stores.Telemetry.TryGet($"ghost-telemetry-{number}", out var telemetry) || telemetry is not { } currentTelemetry)
                throw new InvalidOperationException($"Telemetry for '{vehicle.Id}' was not available.");
            return Ghost(vehicle.Id, currentTelemetry.LatitudeDegrees!.Value, currentTelemetry.LongitudeDegrees!.Value, currentTelemetry.AltitudeAglMetres!.Value)
                with
            {
                Telemetry = Ghost(vehicle.Id, currentTelemetry.LatitudeDegrees!.Value, currentTelemetry.LongitudeDegrees!.Value, currentTelemetry.AltitudeAglMetres!.Value).Telemetry with
                {
                    Armed = currentTelemetry.Armed,
                    LandedState = currentTelemetry.LandedState,
                    Mode = currentTelemetry.AdapterState,
                    AltitudeAglMetres = currentTelemetry.AltitudeAglMetres,
                    ObservedAt = currentTelemetry.ObservedAt
                }
            };
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
