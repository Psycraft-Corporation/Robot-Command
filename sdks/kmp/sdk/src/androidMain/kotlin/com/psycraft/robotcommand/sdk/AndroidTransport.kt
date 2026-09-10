package com.psycraft.robotcommand.sdk

import com.squareup.wire.GrpcClient
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import okhttp3.HttpUrl
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
import java.net.URI
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
        createHttpClient(policy) { fingerprint ->
            observedFingerprintValue = fingerprint
        }
    private val grpcClient: GrpcClient =
        GrpcClient
            .Builder()
            .client(client)
            // Build the URL from parsed components. Passing the raw endpoint string
            // through Wire's String overload caused valid LAN ports such as 7443 to
            // be rejected by the Android URL parser.
            .baseUrl(endpoint.toAndroidHttpUrl())
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

private fun RobotCommandEndpoint.toAndroidHttpUrl(): HttpUrl {
    val uri = URI(value)
    val host = uri.host ?: error("Robot Command endpoint has no valid host.")
    val port = if (uri.port == -1) 443 else uri.port
    return HttpUrl
        .Builder()
        .scheme("https")
        .host(host)
        .port(port)
        .build()
}

private fun createHttpClient(
    policy: CertificatePolicy,
    onObservedFingerprint: (String) -> Unit,
): OkHttpClient {
    val trustManager =
        when (policy) {
            CertificatePolicy.Observe -> TrustAllCertificates
            is CertificatePolicy.Pinned -> PinnedCertificateTrustManager(policy.fingerprint)
        }
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

private class PinnedCertificateTrustManager(
    private val expectedFingerprint: String,
) : X509TrustManager {
    override fun checkClientTrusted(
        chain: Array<out X509Certificate>,
        authType: String,
    ) = Unit

    override fun checkServerTrusted(
        chain: Array<out X509Certificate>,
        authType: String,
    ) {
        val certificate =
            chain.firstOrNull()
                ?: throw java.security.cert.CertificateException("Robot Command server did not present a certificate.")
        val actualFingerprint = certificate.sha256Fingerprint()
        if (actualFingerprint != expectedFingerprint) {
            throw java.security.cert.CertificateException(
                "Robot Command certificate fingerprint did not match the pinned server fingerprint.",
            )
        }
    }

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

private fun X509Certificate.sha256Fingerprint(): String =
    MessageDigest
        .getInstance("SHA-256")
        .digest(encoded)
        .joinToString("") { "%02X".format(it.toInt() and 0xFF) }
