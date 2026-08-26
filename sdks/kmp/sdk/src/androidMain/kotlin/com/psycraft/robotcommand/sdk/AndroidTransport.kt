package com.psycraft.robotcommand.sdk

import android.util.Base64
import com.squareup.wire.GrpcClient
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import okhttp3.CertificatePinner
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Protocol
import psycraft.logos.robotcommand.team.v1.AccessRequest
import psycraft.logos.robotcommand.team.v1.AccessStatus
import psycraft.logos.robotcommand.team.v1.GrpcAccessServiceClient
import psycraft.logos.robotcommand.team.v1.GrpcObserverServiceClient
import psycraft.logos.robotcommand.team.v1.GrpcServerInfoServiceClient
import psycraft.logos.robotcommand.team.v1.ServerInfoRequest
import psycraft.logos.robotcommand.team.v1.ServerInfoResponse
import psycraft.logos.robotcommand.team.v1.SnapshotEnvelope
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

internal class AndroidRobotCommandTransportFactory : RobotCommandTransportFactory {
    override fun open(
        endpoint: RobotCommandEndpoint,
        policy: CertificatePolicy,
    ): RobotCommandRpcTransport = AndroidRobotCommandRpcTransport(endpoint, policy)
}

internal class AndroidRobotCommandRpcTransport(
    private val endpoint: RobotCommandEndpoint,
    policy: CertificatePolicy,
) : RobotCommandRpcTransport {
    private var observedFingerprintValue: String? = null
    private val client: OkHttpClient =
        createHttpClient(endpoint, policy) { fingerprint ->
            observedFingerprintValue = fingerprint
        }
    private val grpcClient: GrpcClient =
        GrpcClient
            .Builder()
            .client(client)
            .baseUrl(endpoint.value.trimEnd('/') + "/")
            .build()

    override val observedCertificateFingerprint: String?
        get() = observedFingerprintValue

    override suspend fun probe(): ServerInfoResponse = GrpcServerInfoServiceClient(grpcClient).GetServerInfo().execute(ServerInfoRequest())

    override fun requestAccess(request: AccessRequest): Flow<AccessStatus> =
        channelFlow {
            val call = GrpcAccessServiceClient(grpcClient).RequestAccess()
            val (requests, responses) = call.executeIn(this)
            requests.send(request)
            requests.close()
            for (status in responses) send(status)
        }

    override fun watchSnapshots(
        token: String,
        afterRevision: Long,
    ): Flow<SnapshotEnvelope> =
        channelFlow {
            val call = GrpcObserverServiceClient(grpcClient).WatchSnapshots()
            call.requestMetadata = mapOf("authorization" to "Bearer $token")
            val (requests, responses) = call.executeIn(this)
            requests.send(
                psycraft.logos.robotcommand.team.v1.WatchSnapshotsRequest(
                    after_revision = afterRevision,
                ),
            )
            requests.close()
            for (envelope in responses) send(envelope)
        }

    override fun close() {
        client.dispatcher.executorService.shutdown()
        client.connectionPool.evictAll()
    }
}

private fun createHttpClient(
    endpoint: RobotCommandEndpoint,
    policy: CertificatePolicy,
    onObservedFingerprint: (String) -> Unit,
): OkHttpClient {
    val trustManager = TrustAllCertificates
    val sslContext =
        SSLContext.getInstance("TLS").apply {
            init(null, arrayOf<X509TrustManager>(trustManager), SecureRandom())
        }
    val builder =
        OkHttpClient
            .Builder()
            .protocols(listOf(Protocol.HTTP_2, Protocol.HTTP_1_1))
            .sslSocketFactory(sslContext.socketFactory, trustManager)
            // Robot Command LAN certificates may be issued to a machine name while
            // clients connect through an IP address. The certificate pin remains the
            // authoritative identity check for the approved session.
            .hostnameVerifier { _, _ -> true }
            .addNetworkInterceptor(CertificateObservationInterceptor(onObservedFingerprint))

    if (policy is CertificatePolicy.Pinned) {
        builder.certificatePinner(
            CertificatePinner
                .Builder()
                .add(endpoint.host, "sha256/${policy.fingerprint.toPin()}")
                .build(),
        )
    }
    return builder.build()
}

private object TrustAllCertificates : X509TrustManager {
    override fun checkClientTrusted(
        chain: Array<out X509Certificate>,
        authType: String,
    ) = Unit

    override fun checkServerTrusted(
        chain: Array<out X509Certificate>,
        authType: String,
    ) = Unit

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}

private class CertificateObservationInterceptor(
    private val onObservedFingerprint: (String) -> Unit,
) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): okhttp3.Response {
        val response = chain.proceed(chain.request())
        val certificate = response.handshake?.peerCertificates?.firstOrNull() as? X509Certificate
        if (certificate != null) onObservedFingerprint(certificate.sha256Fingerprint())
        return response
    }
}

private fun String.toPin(): String {
    val bytes = ByteArray(length / 2) { index -> substring(index * 2, index * 2 + 2).toInt(16).toByte() }
    return Base64.encodeToString(bytes, Base64.NO_WRAP)
}

private fun X509Certificate.sha256Fingerprint(): String =
    MessageDigest
        .getInstance("SHA-256")
        .digest(encoded)
        .joinToString("") { "%02X".format(it.toInt() and 0xFF) }
