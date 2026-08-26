package com.psycraft.robotcommand.sdk

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.test.runTest
import psycraft.logos.robotcommand.team.v1.AccessRequest
import psycraft.logos.robotcommand.team.v1.AccessState
import psycraft.logos.robotcommand.team.v1.AccessStatus
import psycraft.logos.robotcommand.team.v1.MapSnapshot
import psycraft.logos.robotcommand.team.v1.OperatorLocation
import psycraft.logos.robotcommand.team.v1.RobotCommandSnapshot
import psycraft.logos.robotcommand.team.v1.ServerInfoResponse
import psycraft.logos.robotcommand.team.v1.SnapshotEnvelope
import psycraft.logos.robotcommand.team.v1.SnapshotHeartbeat
import psycraft.logos.robotcommand.team.v1.TelemetrySnapshot
import psycraft.logos.robotcommand.team.v1.UnitSnapshot
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

private val fingerprint = "AA".repeat(32)

class RobotCommandSdkTest {
    @Test
    fun pairingInvitationRoundTripsWithCanonicalFields() {
        val invitation =
            RobotCommandPairingInvitation(
                endpoint = RobotCommandEndpoint.parse("https://robot-command.local:7443"),
                certificateFingerprint = fingerprint,
                pairingId = "pairing-123",
                shortCode = "123456",
                expiresAtEpochSeconds =
                    kotlin.time.Clock.System
                        .now()
                        .epochSeconds + 300,
                passphrase = "Meadow-Wolf",
            )

        val parsed = RobotCommandPairingInvitation.parse(invitation.toUri())

        assertEquals(invitation, parsed)
    }

    @Test
    fun pairingInvitationRejectsInvalidInputs() {
        assertFailsWith<IllegalArgumentException> {
            RobotCommandPairingInvitation.parse("http://pair/v1?endpoint=https%3A%2F%2Fhost")
        }
        assertFailsWith<IllegalArgumentException> {
            RobotCommandPairingInvitation.parse(
                "logos-robot-command://pair/v1?endpoint=https%3A%2F%2Fhost&fingerprint=AA&pairing_id=id&code=123&expires=9999999999",
            )
        }
        assertFailsWith<IllegalArgumentException> {
            RobotCommandCertificateFingerprint.parse("not-a-fingerprint")
        }
    }

    @Test
    fun clientProbesAndMapsServerInfo() =
        runTest {
            val factory = FakeTransportFactory()
            val client = RobotCommandLanClient.forTesting(factory)

            val probe = client.probe("https://robot-command.local:7443")

            assertEquals("Robot Command", probe.displayName)
            assertEquals("v1", probe.apiVersion)
            assertEquals(fingerprint, probe.certificateFingerprint)
            assertTrue(factory.openedPolicies.first() is CertificatePolicy.Observe)
        }

    @Test
    fun generatedProtocolTypesRoundTrip() {
        val original = snapshot(revision = 7)
        val encoded = RobotCommandSnapshot.ADAPTER.encode(original)
        val decoded = RobotCommandSnapshot.ADAPTER.decode(encoded)

        assertEquals(original, decoded)
    }

    @Test
    fun accessOpensObserverBeforeReturningSession() =
        runTest {
            val factory = FakeTransportFactory()
            val client = RobotCommandLanClient.forTesting(factory)
            val statuses = mutableListOf<RobotCommandAccessState>()

            val session =
                client.requestAccess(
                    endpoint = "https://robot-command.local:7443",
                    expectedFingerprint = fingerprint,
                    identity =
                        RobotCommandClientIdentity(
                            displayName = "Robot Command Mobile",
                            applicationName = "Robot Command Mobile",
                            applicationVersion = "0.1.0",
                            clientInstanceId = "test-client",
                        ),
                    passphrase = "Meadow-Wolf",
                    onAccessStatus = { statuses += it.state },
                )

            assertEquals(listOf(RobotCommandAccessState.PENDING, RobotCommandAccessState.APPROVED), statuses)
            assertEquals("Meadow-Wolf", factory.lastAccessRequest?.pairing_phrase)
            assertEquals("test-token", factory.lastWatchedToken)
            assertEquals(
                RobotCommandObserverEventType.CONNECTED,
                session.events.first { it.type == RobotCommandObserverEventType.CONNECTED }.type,
            )
            assertEquals(7L, session.snapshots.first().revision)

            session.close()
        }

    @Test
    fun olderSnapshotsAreIgnored() =
        runTest {
            val factory = FakeTransportFactory()
            factory.snapshot = snapshot(revision = 7)
            val client = RobotCommandLanClient.forTesting(factory)
            val session =
                client.requestAccess(
                    endpoint = "https://robot-command.local:7443",
                    expectedFingerprint = fingerprint,
                    identity = testIdentity(),
                )

            factory.snapshot = snapshot(revision = 3)
            assertEquals(7L, session.snapshots.first().revision)
            session.close()
        }

    @Test
    fun heartbeatsDoNotBecomeSnapshots() =
        runTest {
            val factory = FakeTransportFactory().apply { includeHeartbeat = true }
            val session =
                RobotCommandLanClient.forTesting(factory).requestAccess(
                    endpoint = "https://robot-command.local:7443",
                    expectedFingerprint = fingerprint,
                    identity = testIdentity(),
                )

            assertEquals(7L, session.snapshots.first().revision)
            session.close()
        }

    @Test
    fun endpointRequiresHttps() {
        assertFailsWith<IllegalArgumentException> { RobotCommandEndpoint.parse("http://host:7443") }
        assertFailsWith<IllegalArgumentException> { RobotCommandEndpoint.parse("https://") }
    }

    private fun testIdentity() = RobotCommandClientIdentity("Mobile", "Mobile", "0.1.0", "client")

    private fun snapshot(revision: Long) =
        RobotCommandSnapshot(
            revision = revision,
            map =
                MapSnapshot(
                    operator_location =
                        OperatorLocation(
                            shared = true,
                            available = true,
                            latitude_degrees = 43.67,
                            longitude_degrees = -79.40,
                        ),
                ),
            units =
                listOf(
                    UnitSnapshot(
                        id = "unit-1",
                        name = "Ghost One",
                        backend = "ghost",
                        vehicle_class = "simulated",
                        telemetry =
                            TelemetrySnapshot(
                                latitude_degrees = 43.68,
                                longitude_degrees = -79.41,
                            ),
                    ),
                ),
        )
}

private class FakeTransportFactory : RobotCommandTransportFactory {
    val openedPolicies = mutableListOf<CertificatePolicy>()
    var lastAccessRequest: AccessRequest? = null
    var lastWatchedToken: String? = null
    var includeHeartbeat: Boolean = false
    var snapshot: RobotCommandSnapshot = snapshotForFactory()

    override fun open(
        endpoint: RobotCommandEndpoint,
        policy: CertificatePolicy,
    ): RobotCommandRpcTransport {
        openedPolicies += policy
        return FakeTransport(this)
    }
}

private class FakeTransport(
    private val factory: FakeTransportFactory,
) : RobotCommandRpcTransport {
    override val observedCertificateFingerprint: String = fingerprint

    override suspend fun probe(): ServerInfoResponse =
        ServerInfoResponse(
            instance_id = "instance-1",
            display_name = "Robot Command",
            api_version = "v1",
            capabilities = listOf("observer.units", "observer.map"),
            tls_sha256_fingerprint = fingerprint,
            minimum_sdk_version = RobotCommandSdkInfo.sdkVersion,
            requires_passphrase = true,
        )

    override fun requestAccess(request: AccessRequest): Flow<AccessStatus> {
        factory.lastAccessRequest = request
        return flowOf(
            AccessStatus(request_id = "request-1", state = AccessState.ACCESS_STATE_PENDING),
            AccessStatus(
                request_id = "request-1",
                state = AccessState.ACCESS_STATE_APPROVED,
                session_token = "test-token",
            ),
        )
    }

    override fun watchSnapshots(
        token: String,
        afterRevision: Long,
    ): Flow<SnapshotEnvelope> {
        factory.lastWatchedToken = token
        return flowOf(
            *buildList {
                if (factory.includeHeartbeat) add(SnapshotEnvelope(heartbeat = SnapshotHeartbeat(current_revision = 7)))
                add(SnapshotEnvelope(snapshot = factory.snapshot))
            }.toTypedArray(),
        )
    }

    override fun close() = Unit
}

private fun snapshotForFactory() = RobotCommandSnapshot(revision = 7)
