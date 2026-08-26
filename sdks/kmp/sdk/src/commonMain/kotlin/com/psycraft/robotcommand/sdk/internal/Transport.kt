package com.psycraft.robotcommand.sdk

import kotlinx.coroutines.flow.Flow
import psycraft.logos.robotcommand.team.v1.AccessRequest
import psycraft.logos.robotcommand.team.v1.AccessStatus
import psycraft.logos.robotcommand.team.v1.ServerInfoResponse
import psycraft.logos.robotcommand.team.v1.SnapshotEnvelope

internal sealed interface CertificatePolicy {
    data object Observe : CertificatePolicy

    data class Pinned(
        val fingerprint: String,
    ) : CertificatePolicy
}

internal interface RobotCommandRpcTransport {
    val observedCertificateFingerprint: String?

    suspend fun probe(): ServerInfoResponse

    fun requestAccess(request: AccessRequest): Flow<AccessStatus>

    fun watchSnapshots(
        token: String,
        afterRevision: Long,
    ): Flow<SnapshotEnvelope>

    fun close()
}

internal interface RobotCommandTransportFactory {
    fun open(
        endpoint: RobotCommandEndpoint,
        policy: CertificatePolicy,
    ): RobotCommandRpcTransport
}

internal class UnsupportedTransportFactory : RobotCommandTransportFactory {
    override fun open(
        endpoint: RobotCommandEndpoint,
        policy: CertificatePolicy,
    ): RobotCommandRpcTransport = error("Robot Command transport is not implemented for this platform yet.")
}
