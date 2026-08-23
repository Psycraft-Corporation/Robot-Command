import 'dart:async';
import 'dart:io';
import 'dart:math';

import 'package:crypto/crypto.dart';
import 'package:grpc/grpc.dart';

import '../team/v1/team.pbgrpc.dart';
import 'pairing_invitation.dart';

/// Information obtained from a Robot Command before its certificate is pinned.
final class RobotCommandServerProbe {
  const RobotCommandServerProbe({
    required this.endpoint,
    required this.serverInfo,
    required this.observedCertificateFingerprint,
  });

  final Uri endpoint;
  final ServerInfoResponse serverInfo;
  final String observedCertificateFingerprint;
}

/// Identity sent to Robot Command during observer access negotiation.
final class RobotCommandClientIdentity {
  const RobotCommandClientIdentity({
    required this.displayName,
    required this.applicationName,
    required this.applicationVersion,
    required this.clientInstanceId,
  });

  final String displayName;
  final String applicationName;
  final String applicationVersion;
  final String clientInstanceId;
}

enum RobotCommandObserverEventType { connected, disconnected, failed }

/// A lifecycle event from an approved observer session.
final class RobotCommandObserverEvent {
  const RobotCommandObserverEvent({
    required this.type,
    this.reason,
    this.message = '',
    this.error,
  });

  final RobotCommandObserverEventType type;
  final ObserverDisconnectReason? reason;
  final String message;
  final Object? error;
}

/// An approved, read-only Robot Command observer session.
final class RobotCommandObserverSession {
  RobotCommandObserverSession._(this._channel, this._token);

  final ClientChannel _channel;
  final String _token;
  final _events = StreamController<RobotCommandObserverEvent>.broadcast();
  final _snapshots = StreamController<RobotCommandSnapshot>.broadcast();
  StreamSubscription<SnapshotEnvelope>? _subscription;
  Completer<void>? _ready;
  final _streamCompleted = Completer<void>();
  Future<void>? _closeFuture;
  bool _closed = false;
  bool _closeRequested = false;

  Stream<RobotCommandObserverEvent> get events => _events.stream;

  /// Snapshots received from the approved observer stream.
  ///
  /// Heartbeats are intentionally not exposed here. Consumers that need
  /// connection lifecycle information should subscribe to [events].
  Stream<RobotCommandSnapshot> get snapshots => _snapshots.stream;

  /// The most recent snapshot, including the initial snapshot received while
  /// the session was being established.
  RobotCommandSnapshot? get currentSnapshot => _currentSnapshot?.clone();

  RobotCommandSnapshot? _currentSnapshot;

  Future<void> start({Duration timeout = const Duration(seconds: 15)}) async {
    if (_ready != null)
      throw StateError('The observer session has already started.');
    final ready = _ready = Completer<void>();
    final client = ObserverServiceClient(_channel);
    final stream = client.watchSnapshots(
      WatchSnapshotsRequest(),
      options: CallOptions(metadata: {'authorization': 'Bearer $_token'}),
    );
    void markReady() {
      if (!ready.isCompleted) {
        ready.complete();
        _events.add(const RobotCommandObserverEvent(
          type: RobotCommandObserverEventType.connected,
        ));
      }
    }

    _subscription = stream.listen(
      (envelope) {
        if (_closed) return;
        // Keep this as a fallback for transports that do not expose response
        // headers before the first message.
        markReady();
        if (envelope.whichPayload() == SnapshotEnvelope_Payload.snapshot) {
          _currentSnapshot = envelope.snapshot.clone();
          _snapshots.add(_currentSnapshot!);
        } else if (envelope.whichPayload() ==
            SnapshotEnvelope_Payload.disconnect) {
          _events.add(RobotCommandObserverEvent(
            type: RobotCommandObserverEventType.disconnected,
            reason: envelope.disconnect.reason,
            message: envelope.disconnect.message,
          ));
          _closeRequested = true;
        }
      },
      onError: (Object error, StackTrace stackTrace) {
        if (!_streamCompleted.isCompleted) _streamCompleted.complete();
        _closeRequested = true;
        if (!ready.isCompleted) {
          ready.completeError(error, stackTrace);
        } else if (!_closed) {
          _events.add(RobotCommandObserverEvent(
            type: RobotCommandObserverEventType.failed,
            message: error.toString(),
            error: error,
          ));
        }
      },
      onDone: () {
        if (!_streamCompleted.isCompleted) _streamCompleted.complete();
        _closeRequested = true;
        if (!ready.isCompleted) {
          ready.completeError(
            const GrpcError.unavailable(
                'The observer stream ended before connecting.'),
          );
        } else if (!_closed) {
          _events.add(const RobotCommandObserverEvent(
            type: RobotCommandObserverEventType.failed,
            message: 'The observer stream ended.',
          ));
        }
        if (_closeRequested) {
          // Wait until the response subscription has unwound before touching
          // the channel. Closing from the response callback can race http2's
          // stream-queue cleanup and trigger its termination assertion.
          unawaited(
            Future<void>.delayed(const Duration(milliseconds: 50), close),
          );
        }
      },
      cancelOnError: false,
    );
    try {
      // The server sends gRPC response headers when WatchSnapshots has been
      // accepted and the observer lease has been established. Do not make
      // connection success wait for the complete first snapshot payload;
      // large snapshots can take longer to decode on an Android emulator.
      await stream.headers.timeout(
        timeout,
        onTimeout: () => throw TimeoutException(
          'Timed out waiting for the Robot Command observer stream to open.',
          timeout,
        ),
      );
      markReady();
    } catch (error, stackTrace) {
      if (!ready.isCompleted) ready.completeError(error, stackTrace);
      rethrow;
    }
    await ready.future;
  }

  Future<void> close() => _closeFuture ??= _close();

  Future<void> _close() async {
    _closeRequested = true;
    _closed = true;
    await _subscription?.cancel();
    // A cancelled response stream can take a short turn to finish its
    // transport callback. Give it time to remove its HTTP/2 stream queue
    // before shutting down the owning connection.
    try {
      await _streamCompleted.future.timeout(const Duration(seconds: 1));
    } on TimeoutException {
      // The stream may already be gone at the transport layer even if its
      // Dart completion callback was not delivered.
    }
    await Future<void>.delayed(const Duration(milliseconds: 50));
    await _channel.shutdown();
    await _events.close();
    await _snapshots.close();
  }
}

/// Client for the Robot Command Team Observer API.
final class RobotCommandLanClient {
  const RobotCommandLanClient();

  static const apiVersion = 'v1';
  static const sdkVersion = '0.1.0-alpha.1';

  Future<RobotCommandServerProbe> probe(
    Uri endpoint, {
    Duration timeout = const Duration(seconds: 10),
  }) async {
    final normalizedEndpoint = _validateEndpoint(endpoint);
    String? fingerprint;
    final channel = _createChannel(
      normalizedEndpoint,
      onBadCertificate: (certificate, _) {
        fingerprint = _fingerprint(certificate);
        return true;
      },
    );
    try {
      final info = await ServerInfoServiceClient(channel)
          .getServerInfo(ServerInfoRequest())
          .timeout(timeout);
      if (fingerprint == null) {
        throw StateError(
          'Robot Command did not present a certificate that could be pinned.',
        );
      }
      return RobotCommandServerProbe(
        endpoint: normalizedEndpoint,
        serverInfo: info,
        observedCertificateFingerprint: fingerprint!,
      );
    } finally {
      await channel.shutdown();
    }
  }

  Future<RobotCommandObserverSession> requestAccess({
    required Uri endpoint,
    required String expectedFingerprint,
    required RobotCommandClientIdentity identity,
    String passphrase = '',
    RobotCommandPairingInvitation? pairing,
    void Function(AccessStatus status)? onStatus,
    Duration timeout = const Duration(minutes: 3),
  }) async {
    final normalizedEndpoint = _validateEndpoint(endpoint);
    final fingerprint = normalizeFingerprint(expectedFingerprint);
    if (pairing != null) {
      if (pairing.isExpired) {
        throw const FormatException(
            'The Robot Command pairing link has expired.');
      }
      if (!_sameEndpoint(pairing.endpoint, normalizedEndpoint) ||
          pairing.certificateFingerprint != fingerprint) {
        throw ArgumentError(
          'The pairing invitation does not match the selected endpoint or certificate.',
        );
      }
    }

    // Keep the approval negotiation channel separate from the observer
    // channel. The approval RPC is a server stream and cancelling it while
    // immediately opening WatchSnapshots on the same Dart HTTP/2 channel can
    // race in the transport and tear down the connection. A separate channel
    // also gives each long-lived session an explicit owner and lifecycle.
    final accessChannel = _createChannel(
      normalizedEndpoint,
      onBadCertificate: (certificate, _) =>
          _fingerprint(certificate) == fingerprint,
    );
    var sessionCreated = false;
    StreamIterator<AccessStatus>? statusIterator;
    try {
      final request = AccessRequest()
        ..displayName = identity.displayName
        ..applicationName = identity.applicationName
        ..applicationVersion = identity.applicationVersion
        ..sdkVersion = sdkVersion
        ..apiVersion = apiVersion
        ..clientInstanceId = identity.clientInstanceId
        ..requestNonce = _nonce();
      if (pairing != null) {
        pairing.applyTo(request);
      }
      if (passphrase.trim().isNotEmpty)
        request.pairingPhrase = passphrase.trim();

      final accessStatuses =
          AccessServiceClient(accessChannel).requestAccess(request);
      final iterator = StreamIterator(accessStatuses.timeout(timeout));
      statusIterator = iterator;
      while (await iterator.moveNext()) {
        final status = iterator.current;
        onStatus?.call(status);
        if (status.state == AccessState.ACCESS_STATE_APPROVED &&
            status.sessionToken.isNotEmpty) {
          final observerChannel = _createChannel(
            normalizedEndpoint,
            onBadCertificate: (certificate, _) =>
                _fingerprint(certificate) == fingerprint,
          );
          final session = RobotCommandObserverSession._(
            observerChannel,
            status.sessionToken,
          );
          try {
            // Establish the authenticated observer stream before closing the
            // approval stream. The server only considers the token active
            // once WatchSnapshots has successfully begun.
            await session.start();
            sessionCreated = true;
            // The server completes RequestAccess immediately after sending
            // the approval status. Drain that normal completion instead of
            // cancelling the response stream while it still has a queued
            // message; this avoids an http2 queue-termination race.
            try {
              while (await iterator
                  .moveNext()
                  .timeout(const Duration(seconds: 2))) {}
            } on TimeoutException {
              await iterator.cancel();
            }
            await Future<void>.delayed(const Duration(milliseconds: 50));
            await accessChannel.shutdown();
            return session;
          } catch (_) {
            await session.close();
            rethrow;
          }
        }
        if (status.state == AccessState.ACCESS_STATE_REJECTED ||
            status.state == AccessState.ACCESS_STATE_EXPIRED ||
            status.state == AccessState.ACCESS_STATE_INCOMPATIBLE) {
          throw GrpcError.permissionDenied(status.message);
        }
      }
      throw GrpcError.permissionDenied(
        'The access request ended without host approval.',
      );
    } finally {
      if (!sessionCreated) {
        await statusIterator?.cancel();
        await Future<void>.delayed(Duration.zero);
        await accessChannel.shutdown();
      }
    }
  }

  static Uri _validateEndpoint(Uri endpoint) {
    if (endpoint.scheme.toLowerCase() != 'https' || endpoint.host.isEmpty) {
      throw const FormatException(
        'Robot Command connections must use an HTTPS endpoint.',
      );
    }
    return endpoint.replace(path: endpoint.path.isEmpty ? '/' : endpoint.path);
  }

  static bool _sameEndpoint(Uri left, Uri right) =>
      _validateEndpoint(left).toString().toLowerCase() ==
      _validateEndpoint(right).toString().toLowerCase();

  static ClientChannel _createChannel(
    Uri endpoint, {
    required BadCertificateHandler onBadCertificate,
  }) {
    return ClientChannel(
      endpoint.host,
      port: endpoint.hasPort ? endpoint.port : 443,
      options: ChannelOptions(
        credentials: ChannelCredentials.secure(
          authority: endpoint.host,
          onBadCertificate: onBadCertificate,
        ),
      ),
    );
  }

  static String normalizeFingerprint(String value) {
    final normalized = value.replaceAll(':', '').trim().toUpperCase();
    if (normalized.length != 64 ||
        !RegExp(r'^[0-9A-F]{64}$').hasMatch(normalized)) {
      throw const FormatException(
        'A SHA-256 certificate fingerprint must contain 64 hexadecimal characters.',
      );
    }
    return normalized;
  }

  static String _fingerprint(X509Certificate certificate) => sha256
      .convert(certificate.der)
      .bytes
      .map((byte) => byte.toRadixString(16).padLeft(2, '0'))
      .join()
      .toUpperCase();

  static String _nonce() =>
      '${DateTime.now().microsecondsSinceEpoch.toRadixString(16)}-${_randomPart()}';

  static String _randomPart() {
    final random = Random.secure();
    return List.generate(16, (_) => random.nextInt(16).toRadixString(16))
        .join();
  }
}
