package com.psycraft.robotcommand.sdk

/** Creates the platform transport-backed Robot Command client. */
public expect fun createRobotCommandLanClient(): RobotCommandLanClient

/** Version of the mobile SDK sent during access negotiation. */
@Suppress("ktlint:standard:property-naming")
public object RobotCommandSdkInfo {
    public const val apiVersion: String = "v1"
    public const val sdkVersion: String = "0.1.0-alpha.1"
}

/** Read-only client for a Robot Command Team observer server. */
public class RobotCommandLanClient internal constructor(
    private val transportFactory: RobotCommandTransportFactory,
) {
    /** Obtains server identity and the certificate fingerprint before trust is pinned. */
    public suspend fun probe(endpoint: String): RobotCommandServerProbe {
        val parsedEndpoint = RobotCommandEndpoint.parse(endpoint)
        val transport = transportFactory.open(parsedEndpoint, CertificatePolicy.Observe)
        return try {
            val response = transport.probe()
            val fingerprint =
                transport.observedCertificateFingerprint
                    ?: response.tls_sha256_fingerprint.takeIf { it.isNotBlank() }
                    ?: error("Robot Command did not present a certificate that could be pinned.")
            RobotCommandServerProbe(
                endpoint = parsedEndpoint,
                instanceId = response.instance_id,
                displayName = response.display_name,
                apiVersion = response.api_version,
                capabilities = response.capabilities.toList(),
                minimumSdkVersion = response.minimum_sdk_version,
                requiresPassphrase = response.requires_passphrase,
                certificateFingerprint = normalizeFingerprint(fingerprint),
            )
        } finally {
            transport.close()
        }
    }

    /** Requests host-approved read-only observation using an explicitly pinned certificate. */
    public suspend fun requestAccess(
        endpoint: String,
        expectedFingerprint: String,
        identity: RobotCommandClientIdentity,
        passphrase: String = "",
        pairingInvitation: RobotCommandPairingInvitation? = null,
        onAccessStatus: (RobotCommandAccessStatus) -> Unit = {},
    ): RobotCommandObserverSession {
        val parsedEndpoint = RobotCommandEndpoint.parse(endpoint)
        val fingerprint = normalizeFingerprint(expectedFingerprint)
        pairingInvitation?.validateAgainst(parsedEndpoint, fingerprint)

        val accessTransport = transportFactory.open(parsedEndpoint, CertificatePolicy.Pinned(fingerprint))
        var session: RobotCommandObserverSession? = null
        try {
            val request =
                psycraft.logos.robotcommand.team.v1.AccessRequest(
                    display_name = identity.displayName,
                    application_name = identity.applicationName,
                    application_version = identity.applicationVersion,
                    sdk_version = RobotCommandSdkInfo.sdkVersion,
                    api_version = RobotCommandSdkInfo.apiVersion,
                    client_instance_id = identity.clientInstanceId,
                    request_nonce = createRequestNonce(),
                    pairing_id = pairingInvitation?.pairingId.orEmpty(),
                    pairing_code = pairingInvitation?.shortCode.orEmpty(),
                    pairing_phrase = passphrase.trim().ifEmpty { pairingInvitation?.passphrase.orEmpty() },
                )

            try {
                accessTransport.requestAccess(request).collect { status ->
                    val mappedStatus = status.toPublicModel()
                    onAccessStatus(mappedStatus)
                    when (status.state) {
                        psycraft.logos.robotcommand.team.v1.AccessState.ACCESS_STATE_APPROVED -> {
                            val token = status.session_token
                            if (token.isBlank()) {
                                throw RobotCommandAccessException("Robot Command approved access without a session token.")
                            }
                            val observerTransport =
                                transportFactory.open(
                                    parsedEndpoint,
                                    CertificatePolicy.Pinned(fingerprint),
                                )
                            val candidate =
                                RobotCommandObserverSession(
                                    transport = observerTransport,
                                    token = token,
                                    endpoint = parsedEndpoint,
                                )
                            try {
                                candidate.open()
                                session = candidate
                                throw AccessSessionOpened
                            } catch (exception: Throwable) {
                                if (exception !== AccessSessionOpened) candidate.close()
                                throw exception
                            }
                        }

                        psycraft.logos.robotcommand.team.v1.AccessState.ACCESS_STATE_REJECTED,
                        psycraft.logos.robotcommand.team.v1.AccessState.ACCESS_STATE_EXPIRED,
                        psycraft.logos.robotcommand.team.v1.AccessState.ACCESS_STATE_INCOMPATIBLE,
                        -> {
                            throw RobotCommandAccessException(
                                status.message.ifBlank { "Robot Command access was not approved." },
                                mappedStatus.state,
                            )
                        }

                        else -> Unit
                    }
                }
            } catch (opened: AccessSessionOpenedException) {
                return session ?: error("Observer session was not created.")
            }
            throw RobotCommandAccessException("Robot Command access ended before host approval.")
        } finally {
            accessTransport.close()
        }
    }

    internal companion object {
        fun forTesting(factory: RobotCommandTransportFactory): RobotCommandLanClient = RobotCommandLanClient(factory)
    }
}

private class AccessSessionOpenedException : RuntimeException()

private val AccessSessionOpened = AccessSessionOpenedException()

private fun createRequestNonce(): String = (1..32).map { "0123456789abcdef"[kotlin.random.Random.nextInt(16)] }.joinToString("")

private fun normalizeFingerprint(value: String): String {
    val normalized = value.replace(":", "").trim().uppercase()
    require(normalized.length == 64 && normalized.all { it in "0123456789ABCDEF" }) {
        "A SHA-256 certificate fingerprint must contain 64 hexadecimal characters."
    }
    return normalized
}

internal fun String.normalizedFingerprint(): String = normalizeFingerprint(this)
