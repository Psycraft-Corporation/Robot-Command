package com.psycraft.robotcommand.sdk

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
import psycraft.logos.robotcommand.team.v1.AccessStatus
import psycraft.logos.robotcommand.team.v1.Availability
import psycraft.logos.robotcommand.team.v1.ObserverDisconnectReason
import psycraft.logos.robotcommand.team.v1.UnitSnapshot

/** An approved, read-only observer session. */
public class RobotCommandObserverSession internal constructor(
    private val transport: RobotCommandRpcTransport,
    private val token: String,
    public val endpoint: RobotCommandEndpoint,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    // Channels give the SDK a real end-of-stream signal. A SharedFlow cannot be closed,
    // which made a consumer wait forever after a server or transport termination.
    private val eventSink = Channel<RobotCommandObserverEvent>(capacity = Channel.BUFFERED)
    private val snapshotSink = Channel<RobotCommandSnapshot>(capacity = Channel.BUFFERED)
    private var watchJob: Job? = null
    private var lastRevision: Long = 0
    private var opened = false
    private var closed = false

    public val events: Flow<RobotCommandObserverEvent> = eventSink.receiveAsFlow()
    public val snapshots: Flow<RobotCommandSnapshot> = snapshotSink.receiveAsFlow()

    internal suspend fun open() {
        check(!closed) { "The observer session has been closed." }
        val openedSignal = kotlinx.coroutines.CompletableDeferred<Unit>()
        watchJob =
            scope.launch {
                try {
                    transport.watchSnapshots(token, lastRevision).collect { envelope ->
                        if (!opened) {
                            opened = true
                            openedSignal.complete(Unit)
                            eventSink.send(RobotCommandObserverEvent(RobotCommandObserverEventType.CONNECTED))
                        }
                        when {
                            envelope.snapshot != null -> {
                                val snapshot = envelope.snapshot.toPublicModel()
                                if (snapshot.revision >= lastRevision) {
                                    lastRevision = snapshot.revision
                                    snapshotSink.send(snapshot)
                                }
                            }
                            envelope.disconnect != null -> {
                                val notice = envelope.disconnect
                                eventSink.send(
                                    RobotCommandObserverEvent(
                                        type = RobotCommandObserverEventType.DISCONNECTED,
                                        reason = notice.reason.toPublicModel(),
                                        message = notice.message,
                                    ),
                                )
                                openedSignal.complete(Unit)
                                throw StopWatchingException
                            }
                        }
                    }
                    if (!opened) {
                        openedSignal.completeExceptionally(
                            IllegalStateException("The Robot Command observer stream ended before connecting."),
                        )
                    } else if (!closed) {
                        eventSink.send(
                            RobotCommandObserverEvent(
                                type = RobotCommandObserverEventType.FAILED,
                                message = "The Robot Command observer stream ended.",
                            ),
                        )
                    }
                } catch (_: StopWatchingException) {
                } catch (exception: Throwable) {
                    if (!opened) openedSignal.completeExceptionally(exception)
                    if (!closed) {
                        eventSink.send(
                            RobotCommandObserverEvent(
                                type = RobotCommandObserverEventType.FAILED,
                                message = exception.message ?: exception.toString(),
                            ),
                        )
                    }
                } finally {
                    if (!opened) {
                        openedSignal.completeExceptionally(
                            IllegalStateException("The Robot Command observer stream ended before connecting."),
                        )
                    }
                    if (!closed) closed = true
                    // The response collector has fully unwound before the channel is closed.
                    // This ordering avoids the HTTP/2 stream-queue shutdown race.
                    transport.close()
                    eventSink.close()
                    snapshotSink.close()
                    scope.cancel()
                }
            }
        openedSignal.await()
    }

    public suspend fun close() {
        if (closed) return
        closed = true
        eventSink.trySend(RobotCommandObserverEvent(RobotCommandObserverEventType.CLOSED))
        watchJob?.cancelAndJoin()
        transport.close()
        eventSink.close()
        snapshotSink.close()
        scope.cancel()
    }
}

private object StopWatchingException : Throwable()

internal fun AccessStatus.toPublicModel(): RobotCommandAccessStatus =
    RobotCommandAccessStatus(
        requestId = request_id,
        state = state.toPublicModel(),
        message = message,
        expiresAt = expires_at?.toString(),
    )

private fun psycraft.logos.robotcommand.team.v1.AccessState.toPublicModel(): RobotCommandAccessState =
    when (this) {
        psycraft.logos.robotcommand.team.v1.AccessState.ACCESS_STATE_PENDING -> RobotCommandAccessState.PENDING
        psycraft.logos.robotcommand.team.v1.AccessState.ACCESS_STATE_APPROVED -> RobotCommandAccessState.APPROVED
        psycraft.logos.robotcommand.team.v1.AccessState.ACCESS_STATE_REJECTED -> RobotCommandAccessState.REJECTED
        psycraft.logos.robotcommand.team.v1.AccessState.ACCESS_STATE_EXPIRED -> RobotCommandAccessState.EXPIRED
        psycraft.logos.robotcommand.team.v1.AccessState.ACCESS_STATE_INCOMPATIBLE -> RobotCommandAccessState.INCOMPATIBLE
        else -> RobotCommandAccessState.UNSPECIFIED
    }

private fun ObserverDisconnectReason.toPublicModel(): RobotCommandObserverDisconnectReason =
    when (this) {
        ObserverDisconnectReason.OBSERVER_DISCONNECT_REASON_OPERATOR_DISCONNECTED ->
            RobotCommandObserverDisconnectReason.OPERATOR_DISCONNECTED
        ObserverDisconnectReason.OBSERVER_DISCONNECT_REASON_AUTHENTICATION_REQUIRED ->
            RobotCommandObserverDisconnectReason.AUTHENTICATION_REQUIRED
        ObserverDisconnectReason.OBSERVER_DISCONNECT_REASON_SERVER_STOPPED ->
            RobotCommandObserverDisconnectReason.SERVER_STOPPED
        else -> RobotCommandObserverDisconnectReason.UNSPECIFIED
    }

private fun psycraft.logos.robotcommand.team.v1.RobotCommandSnapshot.toPublicModel(): RobotCommandSnapshot =
    RobotCommandSnapshot(
        revision = revision,
        capturedAt = captured_at?.toString(),
        units = units.map(UnitSnapshot::toPublicModel),
        operatorLocation =
            map?.operator_location?.let {
                RobotCommandOperatorLocation(
                    shared = it.shared,
                    available = it.available,
                    latitudeDegrees = it.latitude_degrees,
                    longitudeDegrees = it.longitude_degrees,
                    accuracyMetres = it.accuracy_metres,
                    observedAt = it.observed_at?.toString(),
                )
            },
    )

private fun UnitSnapshot.toPublicModel(): RobotCommandUnitSnapshot =
    RobotCommandUnitSnapshot(
        id = id,
        name = name,
        backend = backend,
        vehicleClass = vehicle_class,
        availability = state.toPublicModel(),
        isGhost = is_ghost,
        telemetry =
            telemetry?.let {
                RobotCommandTelemetrySnapshot(
                    latitudeDegrees = it.latitude_degrees,
                    longitudeDegrees = it.longitude_degrees,
                    headingDegrees = it.heading_degrees,
                    observedAt = it.observed_at?.toString(),
                )
            },
    )

private fun Availability.toPublicModel(): RobotCommandAvailability =
    when (this) {
        Availability.AVAILABILITY_UNKNOWN -> RobotCommandAvailability.UNKNOWN
        Availability.AVAILABILITY_CONNECTING -> RobotCommandAvailability.CONNECTING
        Availability.AVAILABILITY_RECONNECTING -> RobotCommandAvailability.RECONNECTING
        Availability.AVAILABILITY_ONLINE -> RobotCommandAvailability.ONLINE
        Availability.AVAILABILITY_DEGRADED -> RobotCommandAvailability.DEGRADED
        Availability.AVAILABILITY_STALE -> RobotCommandAvailability.STALE
        Availability.AVAILABILITY_OFFLINE -> RobotCommandAvailability.OFFLINE
        Availability.AVAILABILITY_FAULTED -> RobotCommandAvailability.FAULTED
        else -> RobotCommandAvailability.UNSPECIFIED
    }
