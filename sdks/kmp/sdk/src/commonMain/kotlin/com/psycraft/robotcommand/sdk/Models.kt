package com.psycraft.robotcommand.sdk

/** A validated HTTPS Team API endpoint. */
@ConsistentCopyVisibility
public data class RobotCommandEndpoint private constructor(
    public val value: String,
) {
    public val host: String
        get() = value.substringAfter("://").substringBefore('/').substringBeforeLast(':')

    public companion object {
        public fun parse(value: String): RobotCommandEndpoint {
            val normalized = value.trim().trimEnd('/')
            require(
                normalized.startsWith("https://", ignoreCase = true) &&
                    normalized.substringAfter("https://", "").isNotBlank() &&
                    !normalized.any { it.isWhitespace() },
            ) { "Robot Command connections must use an HTTPS endpoint." }
            return RobotCommandEndpoint(normalized)
        }
    }
}

@ConsistentCopyVisibility
public data class RobotCommandCertificateFingerprint private constructor(
    public val value: String,
) {
    public companion object {
        public fun parse(value: String): RobotCommandCertificateFingerprint =
            RobotCommandCertificateFingerprint(value.normalizedFingerprint())
    }
}

public data class RobotCommandServerProbe(
    val endpoint: RobotCommandEndpoint,
    val instanceId: String,
    val displayName: String,
    val apiVersion: String,
    val capabilities: List<String>,
    val minimumSdkVersion: String,
    val requiresPassphrase: Boolean,
    val certificateFingerprint: String,
)

public data class RobotCommandClientIdentity(
    val displayName: String,
    val applicationName: String,
    val applicationVersion: String,
    val clientInstanceId: String,
)

public enum class RobotCommandAccessState {
    UNSPECIFIED,
    PENDING,
    APPROVED,
    REJECTED,
    EXPIRED,
    INCOMPATIBLE,
}

public data class RobotCommandAccessStatus(
    val requestId: String,
    val state: RobotCommandAccessState,
    val message: String,
    val expiresAt: String?,
)

public sealed interface RobotCommandAccessEvent {
    public data class Status(
        val value: RobotCommandAccessStatus,
    ) : RobotCommandAccessEvent

    public data class ObserverReady(
        val session: RobotCommandObserverSession,
    ) : RobotCommandAccessEvent
}

public enum class RobotCommandObserverEventType {
    CONNECTED,
    DISCONNECTED,
    FAILED,
    CLOSED,
}

public data class RobotCommandObserverEvent(
    val type: RobotCommandObserverEventType,
    val reason: RobotCommandObserverDisconnectReason? = null,
    val message: String = "",
)

public enum class RobotCommandObserverDisconnectReason {
    UNSPECIFIED,
    OPERATOR_DISCONNECTED,
    AUTHENTICATION_REQUIRED,
    SERVER_STOPPED,
}

public data class RobotCommandPairingInvitation(
    val endpoint: RobotCommandEndpoint,
    val certificateFingerprint: String,
    val pairingId: String,
    val shortCode: String,
    val expiresAtEpochSeconds: Long,
    val passphrase: String = "",
) {
    public val isExpired: Boolean
        get() = expiresAtEpochSeconds <= currentEpochSeconds()

    public fun toUri(): String =
        buildString {
            append("logos-robot-command://pair/v1?")
            appendQuery("endpoint", endpoint.value)
            appendQuery("fingerprint", certificateFingerprint.normalizedFingerprint())
            appendQuery("pairing_id", pairingId)
            appendQuery("code", shortCode)
            appendQuery("phrase", passphrase)
            appendQuery("expires", expiresAtEpochSeconds.toString())
        }

    public companion object {
        public fun parse(value: String): RobotCommandPairingInvitation {
            val trimmed = value.trim()
            require(trimmed.startsWith("logos-robot-command://pair/v1?")) {
                "Enter a valid Robot Command pairing link."
            }
            val query = trimmed.substringAfter('?', "")
            val fields =
                query
                    .split('&')
                    .filter { it.isNotEmpty() }
                    .associate { part ->
                        val separator = part.indexOf('=')
                        require(separator > 0) { "The pairing link is incomplete or invalid." }
                        decode(part.substring(0, separator)) to decode(part.substring(separator + 1))
                    }
            val endpoint = RobotCommandEndpoint.parse(fields.required("endpoint"))
            val fingerprint = RobotCommandCertificateFingerprint.parse(fields.required("fingerprint")).value
            val pairingId = fields.required("pairing_id")
            val code = fields.required("code")
            require(code.length == 6 && code.all { it in '0'..'9' }) {
                "The pairing link contains an invalid pairing code."
            }
            val expires =
                fields.required("expires").toLongOrNull()
                    ?: error("The pairing link contains an invalid expiry.")
            require(expires > currentEpochSeconds()) {
                "The Robot Command pairing link has expired."
            }
            return RobotCommandPairingInvitation(
                endpoint = endpoint,
                certificateFingerprint = fingerprint,
                pairingId = pairingId,
                shortCode = code,
                expiresAtEpochSeconds = expires,
                passphrase = fields["phrase"].orEmpty(),
            )
        }
    }

    private fun StringBuilder.appendQuery(
        key: String,
        value: String,
    ) {
        if (length > "logos-robot-command://pair/v1?".length) append('&')
        append(encode(key)).append('=').append(encode(value))
    }

    internal fun validateAgainst(
        endpoint: RobotCommandEndpoint,
        fingerprint: String,
    ) {
        require(!isExpired) { "The Robot Command pairing link has expired." }
        require(this.endpoint == endpoint && certificateFingerprint == fingerprint) {
            "The pairing invitation does not match the selected endpoint or certificate."
        }
    }
}

public enum class RobotCommandAvailability {
    UNSPECIFIED,
    UNKNOWN,
    CONNECTING,
    RECONNECTING,
    ONLINE,
    DEGRADED,
    STALE,
    OFFLINE,
    FAULTED,
}

public data class RobotCommandTelemetrySnapshot(
    val latitudeDegrees: Double? = null,
    val longitudeDegrees: Double? = null,
    val headingDegrees: Double? = null,
    val observedAt: String? = null,
)

public data class RobotCommandUnitSnapshot(
    val id: String,
    val name: String,
    val backend: String,
    val vehicleClass: String,
    val availability: RobotCommandAvailability,
    val isGhost: Boolean,
    val telemetry: RobotCommandTelemetrySnapshot?,
)

public data class RobotCommandOperatorLocation(
    val shared: Boolean,
    val available: Boolean,
    val latitudeDegrees: Double? = null,
    val longitudeDegrees: Double? = null,
    val accuracyMetres: Double? = null,
    val observedAt: String? = null,
)

public data class RobotCommandSnapshot(
    val revision: Long,
    val capturedAt: String?,
    val units: List<RobotCommandUnitSnapshot>,
    val operatorLocation: RobotCommandOperatorLocation?,
)

public class RobotCommandAccessException(
    message: String,
    public val state: RobotCommandAccessState? = null,
) : IllegalStateException(message)

private fun Map<String, String>.required(key: String): String =
    get(key)?.takeIf { it.isNotEmpty() }
        ?: error("The pairing link is incomplete or invalid.")

private fun encode(value: String): String =
    buildString {
        value.encodeToByteArray().forEach { byte ->
            val code = byte.toInt() and 0xFF
            if (code in 0x30..0x39 || code in 0x41..0x5A || code in 0x61..0x7A || code in intArrayOf(0x2D, 0x2E, 0x5F, 0x7E)) {
                append(code.toChar())
            } else {
                code
                    .toString(16)
                    .uppercase()
                    .padStart(2, '0')
                    .let { append('%').append(it) }
            }
        }
    }

private fun decode(value: String): String {
    val bytes =
        buildList {
            var index = 0
            while (index < value.length) {
                when {
                    value[index] == '%' && index + 2 < value.length -> {
                        add(value.substring(index + 1, index + 3).toInt(16).toByte())
                        index += 3
                    }
                    value[index] == '+' -> {
                        add(' '.code.toByte())
                        index++
                    }
                    else -> {
                        add(value[index].code.toByte())
                        index++
                    }
                }
            }
        }
    return bytes.toByteArray().decodeToString()
}

private fun currentEpochSeconds(): Long =
    kotlin.time.Clock.System
        .now()
        .epochSeconds
