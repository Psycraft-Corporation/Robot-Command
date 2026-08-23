// This is a generated file - do not edit.
//
// Generated from team/v1/team.proto.

// @dart = 3.3

// ignore_for_file: annotate_overrides, camel_case_types, comment_references
// ignore_for_file: constant_identifier_names
// ignore_for_file: curly_braces_in_flow_control_structures
// ignore_for_file: deprecated_member_use_from_same_package, library_prefixes
// ignore_for_file: non_constant_identifier_names, prefer_relative_imports

import 'dart:core' as $core;

import 'package:fixnum/fixnum.dart' as $fixnum;
import 'package:protobuf/protobuf.dart' as $pb;
import 'package:protobuf/well_known_types/google/protobuf/timestamp.pb.dart'
    as $1;

import 'team.pbenum.dart';

export 'package:protobuf/protobuf.dart' show GeneratedMessageGenericExtensions;

export 'team.pbenum.dart';

class ServerInfoRequest extends $pb.GeneratedMessage {
  factory ServerInfoRequest() => create();

  ServerInfoRequest._();

  factory ServerInfoRequest.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory ServerInfoRequest.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'ServerInfoRequest',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  ServerInfoRequest clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  ServerInfoRequest copyWith(void Function(ServerInfoRequest) updates) =>
      super.copyWith((message) => updates(message as ServerInfoRequest))
          as ServerInfoRequest;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static ServerInfoRequest create() => ServerInfoRequest._();
  @$core.override
  ServerInfoRequest createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static ServerInfoRequest getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<ServerInfoRequest>(create);
  static ServerInfoRequest? _defaultInstance;
}

class ServerInfoResponse extends $pb.GeneratedMessage {
  factory ServerInfoResponse({
    $core.String? instanceId,
    $core.String? displayName,
    $core.String? apiVersion,
    $core.Iterable<$core.String>? capabilities,
    $1.Timestamp? serverTime,
    $core.String? tlsSha256Fingerprint,
    $core.String? minimumSdkVersion,
    $core.bool? requiresPassphrase,
  }) {
    final result = create();
    if (instanceId != null) result.instanceId = instanceId;
    if (displayName != null) result.displayName = displayName;
    if (apiVersion != null) result.apiVersion = apiVersion;
    if (capabilities != null) result.capabilities.addAll(capabilities);
    if (serverTime != null) result.serverTime = serverTime;
    if (tlsSha256Fingerprint != null)
      result.tlsSha256Fingerprint = tlsSha256Fingerprint;
    if (minimumSdkVersion != null) result.minimumSdkVersion = minimumSdkVersion;
    if (requiresPassphrase != null)
      result.requiresPassphrase = requiresPassphrase;
    return result;
  }

  ServerInfoResponse._();

  factory ServerInfoResponse.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory ServerInfoResponse.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'ServerInfoResponse',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'instanceId')
    ..aOS(2, _omitFieldNames ? '' : 'displayName')
    ..aOS(3, _omitFieldNames ? '' : 'apiVersion')
    ..pPS(4, _omitFieldNames ? '' : 'capabilities')
    ..aOM<$1.Timestamp>(5, _omitFieldNames ? '' : 'serverTime',
        subBuilder: $1.Timestamp.create)
    ..aOS(6, _omitFieldNames ? '' : 'tlsSha256Fingerprint')
    ..aOS(7, _omitFieldNames ? '' : 'minimumSdkVersion')
    ..aOB(8, _omitFieldNames ? '' : 'requiresPassphrase')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  ServerInfoResponse clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  ServerInfoResponse copyWith(void Function(ServerInfoResponse) updates) =>
      super.copyWith((message) => updates(message as ServerInfoResponse))
          as ServerInfoResponse;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static ServerInfoResponse create() => ServerInfoResponse._();
  @$core.override
  ServerInfoResponse createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static ServerInfoResponse getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<ServerInfoResponse>(create);
  static ServerInfoResponse? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get instanceId => $_getSZ(0);
  @$pb.TagNumber(1)
  set instanceId($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasInstanceId() => $_has(0);
  @$pb.TagNumber(1)
  void clearInstanceId() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get displayName => $_getSZ(1);
  @$pb.TagNumber(2)
  set displayName($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasDisplayName() => $_has(1);
  @$pb.TagNumber(2)
  void clearDisplayName() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.String get apiVersion => $_getSZ(2);
  @$pb.TagNumber(3)
  set apiVersion($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasApiVersion() => $_has(2);
  @$pb.TagNumber(3)
  void clearApiVersion() => $_clearField(3);

  @$pb.TagNumber(4)
  $pb.PbList<$core.String> get capabilities => $_getList(3);

  @$pb.TagNumber(5)
  $1.Timestamp get serverTime => $_getN(4);
  @$pb.TagNumber(5)
  set serverTime($1.Timestamp value) => $_setField(5, value);
  @$pb.TagNumber(5)
  $core.bool hasServerTime() => $_has(4);
  @$pb.TagNumber(5)
  void clearServerTime() => $_clearField(5);
  @$pb.TagNumber(5)
  $1.Timestamp ensureServerTime() => $_ensure(4);

  @$pb.TagNumber(6)
  $core.String get tlsSha256Fingerprint => $_getSZ(5);
  @$pb.TagNumber(6)
  set tlsSha256Fingerprint($core.String value) => $_setString(5, value);
  @$pb.TagNumber(6)
  $core.bool hasTlsSha256Fingerprint() => $_has(5);
  @$pb.TagNumber(6)
  void clearTlsSha256Fingerprint() => $_clearField(6);

  @$pb.TagNumber(7)
  $core.String get minimumSdkVersion => $_getSZ(6);
  @$pb.TagNumber(7)
  set minimumSdkVersion($core.String value) => $_setString(6, value);
  @$pb.TagNumber(7)
  $core.bool hasMinimumSdkVersion() => $_has(6);
  @$pb.TagNumber(7)
  void clearMinimumSdkVersion() => $_clearField(7);

  @$pb.TagNumber(8)
  $core.bool get requiresPassphrase => $_getBF(7);
  @$pb.TagNumber(8)
  set requiresPassphrase($core.bool value) => $_setBool(7, value);
  @$pb.TagNumber(8)
  $core.bool hasRequiresPassphrase() => $_has(7);
  @$pb.TagNumber(8)
  void clearRequiresPassphrase() => $_clearField(8);
}

class AccessRequest extends $pb.GeneratedMessage {
  factory AccessRequest({
    $core.String? displayName,
    $core.String? applicationName,
    $core.String? applicationVersion,
    $core.String? sdkVersion,
    $core.String? apiVersion,
    $core.String? clientInstanceId,
    $core.String? requestNonce,
    $core.String? pairingId,
    $core.String? pairingCode,
    $core.String? pairingPhrase,
  }) {
    final result = create();
    if (displayName != null) result.displayName = displayName;
    if (applicationName != null) result.applicationName = applicationName;
    if (applicationVersion != null)
      result.applicationVersion = applicationVersion;
    if (sdkVersion != null) result.sdkVersion = sdkVersion;
    if (apiVersion != null) result.apiVersion = apiVersion;
    if (clientInstanceId != null) result.clientInstanceId = clientInstanceId;
    if (requestNonce != null) result.requestNonce = requestNonce;
    if (pairingId != null) result.pairingId = pairingId;
    if (pairingCode != null) result.pairingCode = pairingCode;
    if (pairingPhrase != null) result.pairingPhrase = pairingPhrase;
    return result;
  }

  AccessRequest._();

  factory AccessRequest.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory AccessRequest.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'AccessRequest',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'displayName')
    ..aOS(2, _omitFieldNames ? '' : 'applicationName')
    ..aOS(3, _omitFieldNames ? '' : 'applicationVersion')
    ..aOS(4, _omitFieldNames ? '' : 'sdkVersion')
    ..aOS(5, _omitFieldNames ? '' : 'apiVersion')
    ..aOS(6, _omitFieldNames ? '' : 'clientInstanceId')
    ..aOS(7, _omitFieldNames ? '' : 'requestNonce')
    ..aOS(8, _omitFieldNames ? '' : 'pairingId')
    ..aOS(9, _omitFieldNames ? '' : 'pairingCode')
    ..aOS(10, _omitFieldNames ? '' : 'pairingPhrase')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  AccessRequest clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  AccessRequest copyWith(void Function(AccessRequest) updates) =>
      super.copyWith((message) => updates(message as AccessRequest))
          as AccessRequest;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static AccessRequest create() => AccessRequest._();
  @$core.override
  AccessRequest createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static AccessRequest getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<AccessRequest>(create);
  static AccessRequest? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get displayName => $_getSZ(0);
  @$pb.TagNumber(1)
  set displayName($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasDisplayName() => $_has(0);
  @$pb.TagNumber(1)
  void clearDisplayName() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get applicationName => $_getSZ(1);
  @$pb.TagNumber(2)
  set applicationName($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasApplicationName() => $_has(1);
  @$pb.TagNumber(2)
  void clearApplicationName() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.String get applicationVersion => $_getSZ(2);
  @$pb.TagNumber(3)
  set applicationVersion($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasApplicationVersion() => $_has(2);
  @$pb.TagNumber(3)
  void clearApplicationVersion() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.String get sdkVersion => $_getSZ(3);
  @$pb.TagNumber(4)
  set sdkVersion($core.String value) => $_setString(3, value);
  @$pb.TagNumber(4)
  $core.bool hasSdkVersion() => $_has(3);
  @$pb.TagNumber(4)
  void clearSdkVersion() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.String get apiVersion => $_getSZ(4);
  @$pb.TagNumber(5)
  set apiVersion($core.String value) => $_setString(4, value);
  @$pb.TagNumber(5)
  $core.bool hasApiVersion() => $_has(4);
  @$pb.TagNumber(5)
  void clearApiVersion() => $_clearField(5);

  @$pb.TagNumber(6)
  $core.String get clientInstanceId => $_getSZ(5);
  @$pb.TagNumber(6)
  set clientInstanceId($core.String value) => $_setString(5, value);
  @$pb.TagNumber(6)
  $core.bool hasClientInstanceId() => $_has(5);
  @$pb.TagNumber(6)
  void clearClientInstanceId() => $_clearField(6);

  @$pb.TagNumber(7)
  $core.String get requestNonce => $_getSZ(6);
  @$pb.TagNumber(7)
  set requestNonce($core.String value) => $_setString(6, value);
  @$pb.TagNumber(7)
  $core.bool hasRequestNonce() => $_has(6);
  @$pb.TagNumber(7)
  void clearRequestNonce() => $_clearField(7);

  @$pb.TagNumber(8)
  $core.String get pairingId => $_getSZ(7);
  @$pb.TagNumber(8)
  set pairingId($core.String value) => $_setString(7, value);
  @$pb.TagNumber(8)
  $core.bool hasPairingId() => $_has(7);
  @$pb.TagNumber(8)
  void clearPairingId() => $_clearField(8);

  @$pb.TagNumber(9)
  $core.String get pairingCode => $_getSZ(8);
  @$pb.TagNumber(9)
  set pairingCode($core.String value) => $_setString(8, value);
  @$pb.TagNumber(9)
  $core.bool hasPairingCode() => $_has(8);
  @$pb.TagNumber(9)
  void clearPairingCode() => $_clearField(9);

  /// Session-only human pairing phrase. It is sent only over the TLS channel.
  @$pb.TagNumber(10)
  $core.String get pairingPhrase => $_getSZ(9);
  @$pb.TagNumber(10)
  set pairingPhrase($core.String value) => $_setString(9, value);
  @$pb.TagNumber(10)
  $core.bool hasPairingPhrase() => $_has(9);
  @$pb.TagNumber(10)
  void clearPairingPhrase() => $_clearField(10);
}

class AccessStatus extends $pb.GeneratedMessage {
  factory AccessStatus({
    $core.String? requestId,
    AccessState? state,
    $core.String? message,
    $1.Timestamp? expiresAt,
    $core.String? sessionToken,
  }) {
    final result = create();
    if (requestId != null) result.requestId = requestId;
    if (state != null) result.state = state;
    if (message != null) result.message = message;
    if (expiresAt != null) result.expiresAt = expiresAt;
    if (sessionToken != null) result.sessionToken = sessionToken;
    return result;
  }

  AccessStatus._();

  factory AccessStatus.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory AccessStatus.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'AccessStatus',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'requestId')
    ..aE<AccessState>(2, _omitFieldNames ? '' : 'state',
        enumValues: AccessState.values)
    ..aOS(3, _omitFieldNames ? '' : 'message')
    ..aOM<$1.Timestamp>(4, _omitFieldNames ? '' : 'expiresAt',
        subBuilder: $1.Timestamp.create)
    ..aOS(5, _omitFieldNames ? '' : 'sessionToken')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  AccessStatus clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  AccessStatus copyWith(void Function(AccessStatus) updates) =>
      super.copyWith((message) => updates(message as AccessStatus))
          as AccessStatus;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static AccessStatus create() => AccessStatus._();
  @$core.override
  AccessStatus createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static AccessStatus getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<AccessStatus>(create);
  static AccessStatus? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get requestId => $_getSZ(0);
  @$pb.TagNumber(1)
  set requestId($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasRequestId() => $_has(0);
  @$pb.TagNumber(1)
  void clearRequestId() => $_clearField(1);

  @$pb.TagNumber(2)
  AccessState get state => $_getN(1);
  @$pb.TagNumber(2)
  set state(AccessState value) => $_setField(2, value);
  @$pb.TagNumber(2)
  $core.bool hasState() => $_has(1);
  @$pb.TagNumber(2)
  void clearState() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.String get message => $_getSZ(2);
  @$pb.TagNumber(3)
  set message($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasMessage() => $_has(2);
  @$pb.TagNumber(3)
  void clearMessage() => $_clearField(3);

  @$pb.TagNumber(4)
  $1.Timestamp get expiresAt => $_getN(3);
  @$pb.TagNumber(4)
  set expiresAt($1.Timestamp value) => $_setField(4, value);
  @$pb.TagNumber(4)
  $core.bool hasExpiresAt() => $_has(3);
  @$pb.TagNumber(4)
  void clearExpiresAt() => $_clearField(4);
  @$pb.TagNumber(4)
  $1.Timestamp ensureExpiresAt() => $_ensure(3);

  @$pb.TagNumber(5)
  $core.String get sessionToken => $_getSZ(4);
  @$pb.TagNumber(5)
  set sessionToken($core.String value) => $_setString(4, value);
  @$pb.TagNumber(5)
  $core.bool hasSessionToken() => $_has(4);
  @$pb.TagNumber(5)
  void clearSessionToken() => $_clearField(5);
}

class GetSnapshotRequest extends $pb.GeneratedMessage {
  factory GetSnapshotRequest({
    $fixnum.Int64? knownRevision,
  }) {
    final result = create();
    if (knownRevision != null) result.knownRevision = knownRevision;
    return result;
  }

  GetSnapshotRequest._();

  factory GetSnapshotRequest.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory GetSnapshotRequest.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'GetSnapshotRequest',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..a<$fixnum.Int64>(
        1, _omitFieldNames ? '' : 'knownRevision', $pb.PbFieldType.OU6,
        defaultOrMaker: $fixnum.Int64.ZERO)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  GetSnapshotRequest clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  GetSnapshotRequest copyWith(void Function(GetSnapshotRequest) updates) =>
      super.copyWith((message) => updates(message as GetSnapshotRequest))
          as GetSnapshotRequest;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static GetSnapshotRequest create() => GetSnapshotRequest._();
  @$core.override
  GetSnapshotRequest createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static GetSnapshotRequest getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<GetSnapshotRequest>(create);
  static GetSnapshotRequest? _defaultInstance;

  @$pb.TagNumber(1)
  $fixnum.Int64 get knownRevision => $_getI64(0);
  @$pb.TagNumber(1)
  set knownRevision($fixnum.Int64 value) => $_setInt64(0, value);
  @$pb.TagNumber(1)
  $core.bool hasKnownRevision() => $_has(0);
  @$pb.TagNumber(1)
  void clearKnownRevision() => $_clearField(1);
}

class WatchSnapshotsRequest extends $pb.GeneratedMessage {
  factory WatchSnapshotsRequest({
    $fixnum.Int64? afterRevision,
  }) {
    final result = create();
    if (afterRevision != null) result.afterRevision = afterRevision;
    return result;
  }

  WatchSnapshotsRequest._();

  factory WatchSnapshotsRequest.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory WatchSnapshotsRequest.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'WatchSnapshotsRequest',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..a<$fixnum.Int64>(
        1, _omitFieldNames ? '' : 'afterRevision', $pb.PbFieldType.OU6,
        defaultOrMaker: $fixnum.Int64.ZERO)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  WatchSnapshotsRequest clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  WatchSnapshotsRequest copyWith(
          void Function(WatchSnapshotsRequest) updates) =>
      super.copyWith((message) => updates(message as WatchSnapshotsRequest))
          as WatchSnapshotsRequest;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static WatchSnapshotsRequest create() => WatchSnapshotsRequest._();
  @$core.override
  WatchSnapshotsRequest createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static WatchSnapshotsRequest getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<WatchSnapshotsRequest>(create);
  static WatchSnapshotsRequest? _defaultInstance;

  @$pb.TagNumber(1)
  $fixnum.Int64 get afterRevision => $_getI64(0);
  @$pb.TagNumber(1)
  set afterRevision($fixnum.Int64 value) => $_setInt64(0, value);
  @$pb.TagNumber(1)
  $core.bool hasAfterRevision() => $_has(0);
  @$pb.TagNumber(1)
  void clearAfterRevision() => $_clearField(1);
}

enum SnapshotEnvelope_Payload { snapshot, heartbeat, disconnect, notSet }

class SnapshotEnvelope extends $pb.GeneratedMessage {
  factory SnapshotEnvelope({
    RobotCommandSnapshot? snapshot,
    SnapshotHeartbeat? heartbeat,
    ObserverDisconnectNotice? disconnect,
  }) {
    final result = create();
    if (snapshot != null) result.snapshot = snapshot;
    if (heartbeat != null) result.heartbeat = heartbeat;
    if (disconnect != null) result.disconnect = disconnect;
    return result;
  }

  SnapshotEnvelope._();

  factory SnapshotEnvelope.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory SnapshotEnvelope.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static const $core.Map<$core.int, SnapshotEnvelope_Payload>
      _SnapshotEnvelope_PayloadByTag = {
    1: SnapshotEnvelope_Payload.snapshot,
    2: SnapshotEnvelope_Payload.heartbeat,
    3: SnapshotEnvelope_Payload.disconnect,
    0: SnapshotEnvelope_Payload.notSet
  };
  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'SnapshotEnvelope',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..oo(0, [1, 2, 3])
    ..aOM<RobotCommandSnapshot>(1, _omitFieldNames ? '' : 'snapshot',
        subBuilder: RobotCommandSnapshot.create)
    ..aOM<SnapshotHeartbeat>(2, _omitFieldNames ? '' : 'heartbeat',
        subBuilder: SnapshotHeartbeat.create)
    ..aOM<ObserverDisconnectNotice>(3, _omitFieldNames ? '' : 'disconnect',
        subBuilder: ObserverDisconnectNotice.create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  SnapshotEnvelope clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  SnapshotEnvelope copyWith(void Function(SnapshotEnvelope) updates) =>
      super.copyWith((message) => updates(message as SnapshotEnvelope))
          as SnapshotEnvelope;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static SnapshotEnvelope create() => SnapshotEnvelope._();
  @$core.override
  SnapshotEnvelope createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static SnapshotEnvelope getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<SnapshotEnvelope>(create);
  static SnapshotEnvelope? _defaultInstance;

  @$pb.TagNumber(1)
  @$pb.TagNumber(2)
  @$pb.TagNumber(3)
  SnapshotEnvelope_Payload whichPayload() =>
      _SnapshotEnvelope_PayloadByTag[$_whichOneof(0)]!;
  @$pb.TagNumber(1)
  @$pb.TagNumber(2)
  @$pb.TagNumber(3)
  void clearPayload() => $_clearField($_whichOneof(0));

  @$pb.TagNumber(1)
  RobotCommandSnapshot get snapshot => $_getN(0);
  @$pb.TagNumber(1)
  set snapshot(RobotCommandSnapshot value) => $_setField(1, value);
  @$pb.TagNumber(1)
  $core.bool hasSnapshot() => $_has(0);
  @$pb.TagNumber(1)
  void clearSnapshot() => $_clearField(1);
  @$pb.TagNumber(1)
  RobotCommandSnapshot ensureSnapshot() => $_ensure(0);

  @$pb.TagNumber(2)
  SnapshotHeartbeat get heartbeat => $_getN(1);
  @$pb.TagNumber(2)
  set heartbeat(SnapshotHeartbeat value) => $_setField(2, value);
  @$pb.TagNumber(2)
  $core.bool hasHeartbeat() => $_has(1);
  @$pb.TagNumber(2)
  void clearHeartbeat() => $_clearField(2);
  @$pb.TagNumber(2)
  SnapshotHeartbeat ensureHeartbeat() => $_ensure(1);

  @$pb.TagNumber(3)
  ObserverDisconnectNotice get disconnect => $_getN(2);
  @$pb.TagNumber(3)
  set disconnect(ObserverDisconnectNotice value) => $_setField(3, value);
  @$pb.TagNumber(3)
  $core.bool hasDisconnect() => $_has(2);
  @$pb.TagNumber(3)
  void clearDisconnect() => $_clearField(3);
  @$pb.TagNumber(3)
  ObserverDisconnectNotice ensureDisconnect() => $_ensure(2);
}

class ObserverDisconnectNotice extends $pb.GeneratedMessage {
  factory ObserverDisconnectNotice({
    ObserverDisconnectReason? reason,
    $core.String? message,
    $1.Timestamp? occurredAt,
  }) {
    final result = create();
    if (reason != null) result.reason = reason;
    if (message != null) result.message = message;
    if (occurredAt != null) result.occurredAt = occurredAt;
    return result;
  }

  ObserverDisconnectNotice._();

  factory ObserverDisconnectNotice.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory ObserverDisconnectNotice.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'ObserverDisconnectNotice',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aE<ObserverDisconnectReason>(1, _omitFieldNames ? '' : 'reason',
        enumValues: ObserverDisconnectReason.values)
    ..aOS(2, _omitFieldNames ? '' : 'message')
    ..aOM<$1.Timestamp>(3, _omitFieldNames ? '' : 'occurredAt',
        subBuilder: $1.Timestamp.create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  ObserverDisconnectNotice clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  ObserverDisconnectNotice copyWith(
          void Function(ObserverDisconnectNotice) updates) =>
      super.copyWith((message) => updates(message as ObserverDisconnectNotice))
          as ObserverDisconnectNotice;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static ObserverDisconnectNotice create() => ObserverDisconnectNotice._();
  @$core.override
  ObserverDisconnectNotice createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static ObserverDisconnectNotice getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<ObserverDisconnectNotice>(create);
  static ObserverDisconnectNotice? _defaultInstance;

  @$pb.TagNumber(1)
  ObserverDisconnectReason get reason => $_getN(0);
  @$pb.TagNumber(1)
  set reason(ObserverDisconnectReason value) => $_setField(1, value);
  @$pb.TagNumber(1)
  $core.bool hasReason() => $_has(0);
  @$pb.TagNumber(1)
  void clearReason() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get message => $_getSZ(1);
  @$pb.TagNumber(2)
  set message($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasMessage() => $_has(1);
  @$pb.TagNumber(2)
  void clearMessage() => $_clearField(2);

  @$pb.TagNumber(3)
  $1.Timestamp get occurredAt => $_getN(2);
  @$pb.TagNumber(3)
  set occurredAt($1.Timestamp value) => $_setField(3, value);
  @$pb.TagNumber(3)
  $core.bool hasOccurredAt() => $_has(2);
  @$pb.TagNumber(3)
  void clearOccurredAt() => $_clearField(3);
  @$pb.TagNumber(3)
  $1.Timestamp ensureOccurredAt() => $_ensure(2);
}

class SnapshotHeartbeat extends $pb.GeneratedMessage {
  factory SnapshotHeartbeat({
    $fixnum.Int64? currentRevision,
    $1.Timestamp? serverTime,
  }) {
    final result = create();
    if (currentRevision != null) result.currentRevision = currentRevision;
    if (serverTime != null) result.serverTime = serverTime;
    return result;
  }

  SnapshotHeartbeat._();

  factory SnapshotHeartbeat.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory SnapshotHeartbeat.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'SnapshotHeartbeat',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..a<$fixnum.Int64>(
        1, _omitFieldNames ? '' : 'currentRevision', $pb.PbFieldType.OU6,
        defaultOrMaker: $fixnum.Int64.ZERO)
    ..aOM<$1.Timestamp>(2, _omitFieldNames ? '' : 'serverTime',
        subBuilder: $1.Timestamp.create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  SnapshotHeartbeat clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  SnapshotHeartbeat copyWith(void Function(SnapshotHeartbeat) updates) =>
      super.copyWith((message) => updates(message as SnapshotHeartbeat))
          as SnapshotHeartbeat;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static SnapshotHeartbeat create() => SnapshotHeartbeat._();
  @$core.override
  SnapshotHeartbeat createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static SnapshotHeartbeat getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<SnapshotHeartbeat>(create);
  static SnapshotHeartbeat? _defaultInstance;

  @$pb.TagNumber(1)
  $fixnum.Int64 get currentRevision => $_getI64(0);
  @$pb.TagNumber(1)
  set currentRevision($fixnum.Int64 value) => $_setInt64(0, value);
  @$pb.TagNumber(1)
  $core.bool hasCurrentRevision() => $_has(0);
  @$pb.TagNumber(1)
  void clearCurrentRevision() => $_clearField(1);

  @$pb.TagNumber(2)
  $1.Timestamp get serverTime => $_getN(1);
  @$pb.TagNumber(2)
  set serverTime($1.Timestamp value) => $_setField(2, value);
  @$pb.TagNumber(2)
  $core.bool hasServerTime() => $_has(1);
  @$pb.TagNumber(2)
  void clearServerTime() => $_clearField(2);
  @$pb.TagNumber(2)
  $1.Timestamp ensureServerTime() => $_ensure(1);
}

class RobotCommandSnapshot extends $pb.GeneratedMessage {
  factory RobotCommandSnapshot({
    $fixnum.Int64? revision,
    $1.Timestamp? capturedAt,
    $core.Iterable<UnitSnapshot>? units,
    $core.Iterable<ConnectionSnapshot>? connections,
    MapSnapshot? map,
  }) {
    final result = create();
    if (revision != null) result.revision = revision;
    if (capturedAt != null) result.capturedAt = capturedAt;
    if (units != null) result.units.addAll(units);
    if (connections != null) result.connections.addAll(connections);
    if (map != null) result.map = map;
    return result;
  }

  RobotCommandSnapshot._();

  factory RobotCommandSnapshot.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory RobotCommandSnapshot.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'RobotCommandSnapshot',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..a<$fixnum.Int64>(
        1, _omitFieldNames ? '' : 'revision', $pb.PbFieldType.OU6,
        defaultOrMaker: $fixnum.Int64.ZERO)
    ..aOM<$1.Timestamp>(2, _omitFieldNames ? '' : 'capturedAt',
        subBuilder: $1.Timestamp.create)
    ..pPM<UnitSnapshot>(3, _omitFieldNames ? '' : 'units',
        subBuilder: UnitSnapshot.create)
    ..pPM<ConnectionSnapshot>(4, _omitFieldNames ? '' : 'connections',
        subBuilder: ConnectionSnapshot.create)
    ..aOM<MapSnapshot>(5, _omitFieldNames ? '' : 'map',
        subBuilder: MapSnapshot.create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  RobotCommandSnapshot clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  RobotCommandSnapshot copyWith(void Function(RobotCommandSnapshot) updates) =>
      super.copyWith((message) => updates(message as RobotCommandSnapshot))
          as RobotCommandSnapshot;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static RobotCommandSnapshot create() => RobotCommandSnapshot._();
  @$core.override
  RobotCommandSnapshot createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static RobotCommandSnapshot getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<RobotCommandSnapshot>(create);
  static RobotCommandSnapshot? _defaultInstance;

  @$pb.TagNumber(1)
  $fixnum.Int64 get revision => $_getI64(0);
  @$pb.TagNumber(1)
  set revision($fixnum.Int64 value) => $_setInt64(0, value);
  @$pb.TagNumber(1)
  $core.bool hasRevision() => $_has(0);
  @$pb.TagNumber(1)
  void clearRevision() => $_clearField(1);

  @$pb.TagNumber(2)
  $1.Timestamp get capturedAt => $_getN(1);
  @$pb.TagNumber(2)
  set capturedAt($1.Timestamp value) => $_setField(2, value);
  @$pb.TagNumber(2)
  $core.bool hasCapturedAt() => $_has(1);
  @$pb.TagNumber(2)
  void clearCapturedAt() => $_clearField(2);
  @$pb.TagNumber(2)
  $1.Timestamp ensureCapturedAt() => $_ensure(1);

  @$pb.TagNumber(3)
  $pb.PbList<UnitSnapshot> get units => $_getList(2);

  @$pb.TagNumber(4)
  $pb.PbList<ConnectionSnapshot> get connections => $_getList(3);

  @$pb.TagNumber(5)
  MapSnapshot get map => $_getN(4);
  @$pb.TagNumber(5)
  set map(MapSnapshot value) => $_setField(5, value);
  @$pb.TagNumber(5)
  $core.bool hasMap() => $_has(4);
  @$pb.TagNumber(5)
  void clearMap() => $_clearField(5);
  @$pb.TagNumber(5)
  MapSnapshot ensureMap() => $_ensure(4);
}

class UnitSnapshot extends $pb.GeneratedMessage {
  factory UnitSnapshot({
    $core.String? id,
    $core.String? name,
    $core.String? backend,
    $core.String? vehicleClass,
    $core.String? domain,
    Availability? state,
    $core.bool? isGhost,
    $core.String? logosInstanceId,
    $core.Iterable<$core.String>? capabilities,
    $core.Iterable<$core.String>? connectionIds,
    $core.String? telemetryAuthorityConnectionId,
    $core.String? diagnosticsAuthorityConnectionId,
    TelemetrySnapshot? telemetry,
    DiagnosticsSnapshot? diagnostics,
    ActionSnapshot? actions,
    $core.Iterable<LinkSnapshot>? links,
  }) {
    final result = create();
    if (id != null) result.id = id;
    if (name != null) result.name = name;
    if (backend != null) result.backend = backend;
    if (vehicleClass != null) result.vehicleClass = vehicleClass;
    if (domain != null) result.domain = domain;
    if (state != null) result.state = state;
    if (isGhost != null) result.isGhost = isGhost;
    if (logosInstanceId != null) result.logosInstanceId = logosInstanceId;
    if (capabilities != null) result.capabilities.addAll(capabilities);
    if (connectionIds != null) result.connectionIds.addAll(connectionIds);
    if (telemetryAuthorityConnectionId != null)
      result.telemetryAuthorityConnectionId = telemetryAuthorityConnectionId;
    if (diagnosticsAuthorityConnectionId != null)
      result.diagnosticsAuthorityConnectionId =
          diagnosticsAuthorityConnectionId;
    if (telemetry != null) result.telemetry = telemetry;
    if (diagnostics != null) result.diagnostics = diagnostics;
    if (actions != null) result.actions = actions;
    if (links != null) result.links.addAll(links);
    return result;
  }

  UnitSnapshot._();

  factory UnitSnapshot.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory UnitSnapshot.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'UnitSnapshot',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'id')
    ..aOS(2, _omitFieldNames ? '' : 'name')
    ..aOS(3, _omitFieldNames ? '' : 'backend')
    ..aOS(4, _omitFieldNames ? '' : 'vehicleClass')
    ..aOS(5, _omitFieldNames ? '' : 'domain')
    ..aE<Availability>(6, _omitFieldNames ? '' : 'state',
        enumValues: Availability.values)
    ..aOB(7, _omitFieldNames ? '' : 'isGhost')
    ..aOS(8, _omitFieldNames ? '' : 'logosInstanceId')
    ..pPS(9, _omitFieldNames ? '' : 'capabilities')
    ..pPS(10, _omitFieldNames ? '' : 'connectionIds')
    ..aOS(11, _omitFieldNames ? '' : 'telemetryAuthorityConnectionId')
    ..aOS(12, _omitFieldNames ? '' : 'diagnosticsAuthorityConnectionId')
    ..aOM<TelemetrySnapshot>(13, _omitFieldNames ? '' : 'telemetry',
        subBuilder: TelemetrySnapshot.create)
    ..aOM<DiagnosticsSnapshot>(14, _omitFieldNames ? '' : 'diagnostics',
        subBuilder: DiagnosticsSnapshot.create)
    ..aOM<ActionSnapshot>(15, _omitFieldNames ? '' : 'actions',
        subBuilder: ActionSnapshot.create)
    ..pPM<LinkSnapshot>(16, _omitFieldNames ? '' : 'links',
        subBuilder: LinkSnapshot.create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  UnitSnapshot clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  UnitSnapshot copyWith(void Function(UnitSnapshot) updates) =>
      super.copyWith((message) => updates(message as UnitSnapshot))
          as UnitSnapshot;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static UnitSnapshot create() => UnitSnapshot._();
  @$core.override
  UnitSnapshot createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static UnitSnapshot getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<UnitSnapshot>(create);
  static UnitSnapshot? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get id => $_getSZ(0);
  @$pb.TagNumber(1)
  set id($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasId() => $_has(0);
  @$pb.TagNumber(1)
  void clearId() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get name => $_getSZ(1);
  @$pb.TagNumber(2)
  set name($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasName() => $_has(1);
  @$pb.TagNumber(2)
  void clearName() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.String get backend => $_getSZ(2);
  @$pb.TagNumber(3)
  set backend($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasBackend() => $_has(2);
  @$pb.TagNumber(3)
  void clearBackend() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.String get vehicleClass => $_getSZ(3);
  @$pb.TagNumber(4)
  set vehicleClass($core.String value) => $_setString(3, value);
  @$pb.TagNumber(4)
  $core.bool hasVehicleClass() => $_has(3);
  @$pb.TagNumber(4)
  void clearVehicleClass() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.String get domain => $_getSZ(4);
  @$pb.TagNumber(5)
  set domain($core.String value) => $_setString(4, value);
  @$pb.TagNumber(5)
  $core.bool hasDomain() => $_has(4);
  @$pb.TagNumber(5)
  void clearDomain() => $_clearField(5);

  @$pb.TagNumber(6)
  Availability get state => $_getN(5);
  @$pb.TagNumber(6)
  set state(Availability value) => $_setField(6, value);
  @$pb.TagNumber(6)
  $core.bool hasState() => $_has(5);
  @$pb.TagNumber(6)
  void clearState() => $_clearField(6);

  @$pb.TagNumber(7)
  $core.bool get isGhost => $_getBF(6);
  @$pb.TagNumber(7)
  set isGhost($core.bool value) => $_setBool(6, value);
  @$pb.TagNumber(7)
  $core.bool hasIsGhost() => $_has(6);
  @$pb.TagNumber(7)
  void clearIsGhost() => $_clearField(7);

  @$pb.TagNumber(8)
  $core.String get logosInstanceId => $_getSZ(7);
  @$pb.TagNumber(8)
  set logosInstanceId($core.String value) => $_setString(7, value);
  @$pb.TagNumber(8)
  $core.bool hasLogosInstanceId() => $_has(7);
  @$pb.TagNumber(8)
  void clearLogosInstanceId() => $_clearField(8);

  @$pb.TagNumber(9)
  $pb.PbList<$core.String> get capabilities => $_getList(8);

  @$pb.TagNumber(10)
  $pb.PbList<$core.String> get connectionIds => $_getList(9);

  @$pb.TagNumber(11)
  $core.String get telemetryAuthorityConnectionId => $_getSZ(10);
  @$pb.TagNumber(11)
  set telemetryAuthorityConnectionId($core.String value) =>
      $_setString(10, value);
  @$pb.TagNumber(11)
  $core.bool hasTelemetryAuthorityConnectionId() => $_has(10);
  @$pb.TagNumber(11)
  void clearTelemetryAuthorityConnectionId() => $_clearField(11);

  @$pb.TagNumber(12)
  $core.String get diagnosticsAuthorityConnectionId => $_getSZ(11);
  @$pb.TagNumber(12)
  set diagnosticsAuthorityConnectionId($core.String value) =>
      $_setString(11, value);
  @$pb.TagNumber(12)
  $core.bool hasDiagnosticsAuthorityConnectionId() => $_has(11);
  @$pb.TagNumber(12)
  void clearDiagnosticsAuthorityConnectionId() => $_clearField(12);

  @$pb.TagNumber(13)
  TelemetrySnapshot get telemetry => $_getN(12);
  @$pb.TagNumber(13)
  set telemetry(TelemetrySnapshot value) => $_setField(13, value);
  @$pb.TagNumber(13)
  $core.bool hasTelemetry() => $_has(12);
  @$pb.TagNumber(13)
  void clearTelemetry() => $_clearField(13);
  @$pb.TagNumber(13)
  TelemetrySnapshot ensureTelemetry() => $_ensure(12);

  @$pb.TagNumber(14)
  DiagnosticsSnapshot get diagnostics => $_getN(13);
  @$pb.TagNumber(14)
  set diagnostics(DiagnosticsSnapshot value) => $_setField(14, value);
  @$pb.TagNumber(14)
  $core.bool hasDiagnostics() => $_has(13);
  @$pb.TagNumber(14)
  void clearDiagnostics() => $_clearField(14);
  @$pb.TagNumber(14)
  DiagnosticsSnapshot ensureDiagnostics() => $_ensure(13);

  @$pb.TagNumber(15)
  ActionSnapshot get actions => $_getN(14);
  @$pb.TagNumber(15)
  set actions(ActionSnapshot value) => $_setField(15, value);
  @$pb.TagNumber(15)
  $core.bool hasActions() => $_has(14);
  @$pb.TagNumber(15)
  void clearActions() => $_clearField(15);
  @$pb.TagNumber(15)
  ActionSnapshot ensureActions() => $_ensure(14);

  @$pb.TagNumber(16)
  $pb.PbList<LinkSnapshot> get links => $_getList(15);
}

class ConnectionSnapshot extends $pb.GeneratedMessage {
  factory ConnectionSnapshot({
    $core.String? id,
    $core.String? name,
    $core.String? target,
    $core.String? mode,
    Availability? state,
    $core.bool? autoReconnect,
    $core.String? logosInstanceId,
    $core.String? runtimeRole,
    $1.Timestamp? connectedAt,
    $1.Timestamp? lastSeen,
    $core.String? lastError,
    $core.bool? isGhost,
  }) {
    final result = create();
    if (id != null) result.id = id;
    if (name != null) result.name = name;
    if (target != null) result.target = target;
    if (mode != null) result.mode = mode;
    if (state != null) result.state = state;
    if (autoReconnect != null) result.autoReconnect = autoReconnect;
    if (logosInstanceId != null) result.logosInstanceId = logosInstanceId;
    if (runtimeRole != null) result.runtimeRole = runtimeRole;
    if (connectedAt != null) result.connectedAt = connectedAt;
    if (lastSeen != null) result.lastSeen = lastSeen;
    if (lastError != null) result.lastError = lastError;
    if (isGhost != null) result.isGhost = isGhost;
    return result;
  }

  ConnectionSnapshot._();

  factory ConnectionSnapshot.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory ConnectionSnapshot.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'ConnectionSnapshot',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'id')
    ..aOS(2, _omitFieldNames ? '' : 'name')
    ..aOS(3, _omitFieldNames ? '' : 'target')
    ..aOS(4, _omitFieldNames ? '' : 'mode')
    ..aE<Availability>(5, _omitFieldNames ? '' : 'state',
        enumValues: Availability.values)
    ..aOB(6, _omitFieldNames ? '' : 'autoReconnect')
    ..aOS(7, _omitFieldNames ? '' : 'logosInstanceId')
    ..aOS(8, _omitFieldNames ? '' : 'runtimeRole')
    ..aOM<$1.Timestamp>(9, _omitFieldNames ? '' : 'connectedAt',
        subBuilder: $1.Timestamp.create)
    ..aOM<$1.Timestamp>(10, _omitFieldNames ? '' : 'lastSeen',
        subBuilder: $1.Timestamp.create)
    ..aOS(11, _omitFieldNames ? '' : 'lastError')
    ..aOB(12, _omitFieldNames ? '' : 'isGhost')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  ConnectionSnapshot clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  ConnectionSnapshot copyWith(void Function(ConnectionSnapshot) updates) =>
      super.copyWith((message) => updates(message as ConnectionSnapshot))
          as ConnectionSnapshot;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static ConnectionSnapshot create() => ConnectionSnapshot._();
  @$core.override
  ConnectionSnapshot createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static ConnectionSnapshot getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<ConnectionSnapshot>(create);
  static ConnectionSnapshot? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get id => $_getSZ(0);
  @$pb.TagNumber(1)
  set id($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasId() => $_has(0);
  @$pb.TagNumber(1)
  void clearId() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get name => $_getSZ(1);
  @$pb.TagNumber(2)
  set name($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasName() => $_has(1);
  @$pb.TagNumber(2)
  void clearName() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.String get target => $_getSZ(2);
  @$pb.TagNumber(3)
  set target($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasTarget() => $_has(2);
  @$pb.TagNumber(3)
  void clearTarget() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.String get mode => $_getSZ(3);
  @$pb.TagNumber(4)
  set mode($core.String value) => $_setString(3, value);
  @$pb.TagNumber(4)
  $core.bool hasMode() => $_has(3);
  @$pb.TagNumber(4)
  void clearMode() => $_clearField(4);

  @$pb.TagNumber(5)
  Availability get state => $_getN(4);
  @$pb.TagNumber(5)
  set state(Availability value) => $_setField(5, value);
  @$pb.TagNumber(5)
  $core.bool hasState() => $_has(4);
  @$pb.TagNumber(5)
  void clearState() => $_clearField(5);

  @$pb.TagNumber(6)
  $core.bool get autoReconnect => $_getBF(5);
  @$pb.TagNumber(6)
  set autoReconnect($core.bool value) => $_setBool(5, value);
  @$pb.TagNumber(6)
  $core.bool hasAutoReconnect() => $_has(5);
  @$pb.TagNumber(6)
  void clearAutoReconnect() => $_clearField(6);

  @$pb.TagNumber(7)
  $core.String get logosInstanceId => $_getSZ(6);
  @$pb.TagNumber(7)
  set logosInstanceId($core.String value) => $_setString(6, value);
  @$pb.TagNumber(7)
  $core.bool hasLogosInstanceId() => $_has(6);
  @$pb.TagNumber(7)
  void clearLogosInstanceId() => $_clearField(7);

  @$pb.TagNumber(8)
  $core.String get runtimeRole => $_getSZ(7);
  @$pb.TagNumber(8)
  set runtimeRole($core.String value) => $_setString(7, value);
  @$pb.TagNumber(8)
  $core.bool hasRuntimeRole() => $_has(7);
  @$pb.TagNumber(8)
  void clearRuntimeRole() => $_clearField(8);

  @$pb.TagNumber(9)
  $1.Timestamp get connectedAt => $_getN(8);
  @$pb.TagNumber(9)
  set connectedAt($1.Timestamp value) => $_setField(9, value);
  @$pb.TagNumber(9)
  $core.bool hasConnectedAt() => $_has(8);
  @$pb.TagNumber(9)
  void clearConnectedAt() => $_clearField(9);
  @$pb.TagNumber(9)
  $1.Timestamp ensureConnectedAt() => $_ensure(8);

  @$pb.TagNumber(10)
  $1.Timestamp get lastSeen => $_getN(9);
  @$pb.TagNumber(10)
  set lastSeen($1.Timestamp value) => $_setField(10, value);
  @$pb.TagNumber(10)
  $core.bool hasLastSeen() => $_has(9);
  @$pb.TagNumber(10)
  void clearLastSeen() => $_clearField(10);
  @$pb.TagNumber(10)
  $1.Timestamp ensureLastSeen() => $_ensure(9);

  @$pb.TagNumber(11)
  $core.String get lastError => $_getSZ(10);
  @$pb.TagNumber(11)
  set lastError($core.String value) => $_setString(10, value);
  @$pb.TagNumber(11)
  $core.bool hasLastError() => $_has(10);
  @$pb.TagNumber(11)
  void clearLastError() => $_clearField(11);

  @$pb.TagNumber(12)
  $core.bool get isGhost => $_getBF(11);
  @$pb.TagNumber(12)
  set isGhost($core.bool value) => $_setBool(11, value);
  @$pb.TagNumber(12)
  $core.bool hasIsGhost() => $_has(11);
  @$pb.TagNumber(12)
  void clearIsGhost() => $_clearField(12);
}

class TelemetrySnapshot extends $pb.GeneratedMessage {
  factory TelemetrySnapshot({
    $core.bool? reported,
    $core.bool? armed,
    $core.String? landedState,
    $core.String? mode,
    $core.double? latitudeDegrees,
    $core.double? longitudeDegrees,
    $core.double? altitudeMslMetres,
    $core.double? altitudeAglMetres,
    $core.double? localNorthMetres,
    $core.double? localEastMetres,
    $core.double? localDownMetres,
    $core.double? velocityNorthMetresPerSecond,
    $core.double? velocityEastMetresPerSecond,
    $core.double? velocityDownMetresPerSecond,
    $core.double? headingDegrees,
    $core.bool? stale,
    $core.String? code,
    $core.String? message,
    $1.Timestamp? observedAt,
  }) {
    final result = create();
    if (reported != null) result.reported = reported;
    if (armed != null) result.armed = armed;
    if (landedState != null) result.landedState = landedState;
    if (mode != null) result.mode = mode;
    if (latitudeDegrees != null) result.latitudeDegrees = latitudeDegrees;
    if (longitudeDegrees != null) result.longitudeDegrees = longitudeDegrees;
    if (altitudeMslMetres != null) result.altitudeMslMetres = altitudeMslMetres;
    if (altitudeAglMetres != null) result.altitudeAglMetres = altitudeAglMetres;
    if (localNorthMetres != null) result.localNorthMetres = localNorthMetres;
    if (localEastMetres != null) result.localEastMetres = localEastMetres;
    if (localDownMetres != null) result.localDownMetres = localDownMetres;
    if (velocityNorthMetresPerSecond != null)
      result.velocityNorthMetresPerSecond = velocityNorthMetresPerSecond;
    if (velocityEastMetresPerSecond != null)
      result.velocityEastMetresPerSecond = velocityEastMetresPerSecond;
    if (velocityDownMetresPerSecond != null)
      result.velocityDownMetresPerSecond = velocityDownMetresPerSecond;
    if (headingDegrees != null) result.headingDegrees = headingDegrees;
    if (stale != null) result.stale = stale;
    if (code != null) result.code = code;
    if (message != null) result.message = message;
    if (observedAt != null) result.observedAt = observedAt;
    return result;
  }

  TelemetrySnapshot._();

  factory TelemetrySnapshot.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory TelemetrySnapshot.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'TelemetrySnapshot',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOB(1, _omitFieldNames ? '' : 'reported')
    ..aOB(2, _omitFieldNames ? '' : 'armed')
    ..aOS(3, _omitFieldNames ? '' : 'landedState')
    ..aOS(4, _omitFieldNames ? '' : 'mode')
    ..aD(5, _omitFieldNames ? '' : 'latitudeDegrees')
    ..aD(6, _omitFieldNames ? '' : 'longitudeDegrees')
    ..aD(7, _omitFieldNames ? '' : 'altitudeMslMetres')
    ..aD(8, _omitFieldNames ? '' : 'altitudeAglMetres')
    ..aD(9, _omitFieldNames ? '' : 'localNorthMetres')
    ..aD(10, _omitFieldNames ? '' : 'localEastMetres')
    ..aD(11, _omitFieldNames ? '' : 'localDownMetres')
    ..aD(12, _omitFieldNames ? '' : 'velocityNorthMetresPerSecond')
    ..aD(13, _omitFieldNames ? '' : 'velocityEastMetresPerSecond')
    ..aD(14, _omitFieldNames ? '' : 'velocityDownMetresPerSecond')
    ..aD(15, _omitFieldNames ? '' : 'headingDegrees')
    ..aOB(16, _omitFieldNames ? '' : 'stale')
    ..aOS(17, _omitFieldNames ? '' : 'code')
    ..aOS(18, _omitFieldNames ? '' : 'message')
    ..aOM<$1.Timestamp>(19, _omitFieldNames ? '' : 'observedAt',
        subBuilder: $1.Timestamp.create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  TelemetrySnapshot clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  TelemetrySnapshot copyWith(void Function(TelemetrySnapshot) updates) =>
      super.copyWith((message) => updates(message as TelemetrySnapshot))
          as TelemetrySnapshot;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static TelemetrySnapshot create() => TelemetrySnapshot._();
  @$core.override
  TelemetrySnapshot createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static TelemetrySnapshot getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<TelemetrySnapshot>(create);
  static TelemetrySnapshot? _defaultInstance;

  @$pb.TagNumber(1)
  $core.bool get reported => $_getBF(0);
  @$pb.TagNumber(1)
  set reported($core.bool value) => $_setBool(0, value);
  @$pb.TagNumber(1)
  $core.bool hasReported() => $_has(0);
  @$pb.TagNumber(1)
  void clearReported() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.bool get armed => $_getBF(1);
  @$pb.TagNumber(2)
  set armed($core.bool value) => $_setBool(1, value);
  @$pb.TagNumber(2)
  $core.bool hasArmed() => $_has(1);
  @$pb.TagNumber(2)
  void clearArmed() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.String get landedState => $_getSZ(2);
  @$pb.TagNumber(3)
  set landedState($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasLandedState() => $_has(2);
  @$pb.TagNumber(3)
  void clearLandedState() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.String get mode => $_getSZ(3);
  @$pb.TagNumber(4)
  set mode($core.String value) => $_setString(3, value);
  @$pb.TagNumber(4)
  $core.bool hasMode() => $_has(3);
  @$pb.TagNumber(4)
  void clearMode() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.double get latitudeDegrees => $_getN(4);
  @$pb.TagNumber(5)
  set latitudeDegrees($core.double value) => $_setDouble(4, value);
  @$pb.TagNumber(5)
  $core.bool hasLatitudeDegrees() => $_has(4);
  @$pb.TagNumber(5)
  void clearLatitudeDegrees() => $_clearField(5);

  @$pb.TagNumber(6)
  $core.double get longitudeDegrees => $_getN(5);
  @$pb.TagNumber(6)
  set longitudeDegrees($core.double value) => $_setDouble(5, value);
  @$pb.TagNumber(6)
  $core.bool hasLongitudeDegrees() => $_has(5);
  @$pb.TagNumber(6)
  void clearLongitudeDegrees() => $_clearField(6);

  @$pb.TagNumber(7)
  $core.double get altitudeMslMetres => $_getN(6);
  @$pb.TagNumber(7)
  set altitudeMslMetres($core.double value) => $_setDouble(6, value);
  @$pb.TagNumber(7)
  $core.bool hasAltitudeMslMetres() => $_has(6);
  @$pb.TagNumber(7)
  void clearAltitudeMslMetres() => $_clearField(7);

  @$pb.TagNumber(8)
  $core.double get altitudeAglMetres => $_getN(7);
  @$pb.TagNumber(8)
  set altitudeAglMetres($core.double value) => $_setDouble(7, value);
  @$pb.TagNumber(8)
  $core.bool hasAltitudeAglMetres() => $_has(7);
  @$pb.TagNumber(8)
  void clearAltitudeAglMetres() => $_clearField(8);

  @$pb.TagNumber(9)
  $core.double get localNorthMetres => $_getN(8);
  @$pb.TagNumber(9)
  set localNorthMetres($core.double value) => $_setDouble(8, value);
  @$pb.TagNumber(9)
  $core.bool hasLocalNorthMetres() => $_has(8);
  @$pb.TagNumber(9)
  void clearLocalNorthMetres() => $_clearField(9);

  @$pb.TagNumber(10)
  $core.double get localEastMetres => $_getN(9);
  @$pb.TagNumber(10)
  set localEastMetres($core.double value) => $_setDouble(9, value);
  @$pb.TagNumber(10)
  $core.bool hasLocalEastMetres() => $_has(9);
  @$pb.TagNumber(10)
  void clearLocalEastMetres() => $_clearField(10);

  @$pb.TagNumber(11)
  $core.double get localDownMetres => $_getN(10);
  @$pb.TagNumber(11)
  set localDownMetres($core.double value) => $_setDouble(10, value);
  @$pb.TagNumber(11)
  $core.bool hasLocalDownMetres() => $_has(10);
  @$pb.TagNumber(11)
  void clearLocalDownMetres() => $_clearField(11);

  @$pb.TagNumber(12)
  $core.double get velocityNorthMetresPerSecond => $_getN(11);
  @$pb.TagNumber(12)
  set velocityNorthMetresPerSecond($core.double value) =>
      $_setDouble(11, value);
  @$pb.TagNumber(12)
  $core.bool hasVelocityNorthMetresPerSecond() => $_has(11);
  @$pb.TagNumber(12)
  void clearVelocityNorthMetresPerSecond() => $_clearField(12);

  @$pb.TagNumber(13)
  $core.double get velocityEastMetresPerSecond => $_getN(12);
  @$pb.TagNumber(13)
  set velocityEastMetresPerSecond($core.double value) => $_setDouble(12, value);
  @$pb.TagNumber(13)
  $core.bool hasVelocityEastMetresPerSecond() => $_has(12);
  @$pb.TagNumber(13)
  void clearVelocityEastMetresPerSecond() => $_clearField(13);

  @$pb.TagNumber(14)
  $core.double get velocityDownMetresPerSecond => $_getN(13);
  @$pb.TagNumber(14)
  set velocityDownMetresPerSecond($core.double value) => $_setDouble(13, value);
  @$pb.TagNumber(14)
  $core.bool hasVelocityDownMetresPerSecond() => $_has(13);
  @$pb.TagNumber(14)
  void clearVelocityDownMetresPerSecond() => $_clearField(14);

  @$pb.TagNumber(15)
  $core.double get headingDegrees => $_getN(14);
  @$pb.TagNumber(15)
  set headingDegrees($core.double value) => $_setDouble(14, value);
  @$pb.TagNumber(15)
  $core.bool hasHeadingDegrees() => $_has(14);
  @$pb.TagNumber(15)
  void clearHeadingDegrees() => $_clearField(15);

  @$pb.TagNumber(16)
  $core.bool get stale => $_getBF(15);
  @$pb.TagNumber(16)
  set stale($core.bool value) => $_setBool(15, value);
  @$pb.TagNumber(16)
  $core.bool hasStale() => $_has(15);
  @$pb.TagNumber(16)
  void clearStale() => $_clearField(16);

  @$pb.TagNumber(17)
  $core.String get code => $_getSZ(16);
  @$pb.TagNumber(17)
  set code($core.String value) => $_setString(16, value);
  @$pb.TagNumber(17)
  $core.bool hasCode() => $_has(16);
  @$pb.TagNumber(17)
  void clearCode() => $_clearField(17);

  @$pb.TagNumber(18)
  $core.String get message => $_getSZ(17);
  @$pb.TagNumber(18)
  set message($core.String value) => $_setString(17, value);
  @$pb.TagNumber(18)
  $core.bool hasMessage() => $_has(17);
  @$pb.TagNumber(18)
  void clearMessage() => $_clearField(18);

  @$pb.TagNumber(19)
  $1.Timestamp get observedAt => $_getN(18);
  @$pb.TagNumber(19)
  set observedAt($1.Timestamp value) => $_setField(19, value);
  @$pb.TagNumber(19)
  $core.bool hasObservedAt() => $_has(18);
  @$pb.TagNumber(19)
  void clearObservedAt() => $_clearField(19);
  @$pb.TagNumber(19)
  $1.Timestamp ensureObservedAt() => $_ensure(18);
}

class DiagnosticsSnapshot extends $pb.GeneratedMessage {
  factory DiagnosticsSnapshot({
    $core.bool? reported,
    $core.String? backend,
    DiagnosticStatus? overallStatus,
    $core.String? summary,
    DiagnosticStatus? armReadiness,
    $core.String? armReadinessDetail,
    DiagnosticStatus? navigationReadiness,
    $core.String? navigationReadinessDetail,
    DiagnosticStatus? telemetryStatus,
    $core.String? telemetryDetail,
    $core.Iterable<DiagnosticCheck>? checks,
    $core.Iterable<DiagnosticMessage>? recentMessages,
    $1.Timestamp? observedAt,
    $core.int? systemId,
    $core.int? componentId,
    $core.String? version,
    $core.String? mode,
  }) {
    final result = create();
    if (reported != null) result.reported = reported;
    if (backend != null) result.backend = backend;
    if (overallStatus != null) result.overallStatus = overallStatus;
    if (summary != null) result.summary = summary;
    if (armReadiness != null) result.armReadiness = armReadiness;
    if (armReadinessDetail != null)
      result.armReadinessDetail = armReadinessDetail;
    if (navigationReadiness != null)
      result.navigationReadiness = navigationReadiness;
    if (navigationReadinessDetail != null)
      result.navigationReadinessDetail = navigationReadinessDetail;
    if (telemetryStatus != null) result.telemetryStatus = telemetryStatus;
    if (telemetryDetail != null) result.telemetryDetail = telemetryDetail;
    if (checks != null) result.checks.addAll(checks);
    if (recentMessages != null) result.recentMessages.addAll(recentMessages);
    if (observedAt != null) result.observedAt = observedAt;
    if (systemId != null) result.systemId = systemId;
    if (componentId != null) result.componentId = componentId;
    if (version != null) result.version = version;
    if (mode != null) result.mode = mode;
    return result;
  }

  DiagnosticsSnapshot._();

  factory DiagnosticsSnapshot.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory DiagnosticsSnapshot.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'DiagnosticsSnapshot',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOB(1, _omitFieldNames ? '' : 'reported')
    ..aOS(2, _omitFieldNames ? '' : 'backend')
    ..aE<DiagnosticStatus>(3, _omitFieldNames ? '' : 'overallStatus',
        enumValues: DiagnosticStatus.values)
    ..aOS(4, _omitFieldNames ? '' : 'summary')
    ..aE<DiagnosticStatus>(5, _omitFieldNames ? '' : 'armReadiness',
        enumValues: DiagnosticStatus.values)
    ..aOS(6, _omitFieldNames ? '' : 'armReadinessDetail')
    ..aE<DiagnosticStatus>(7, _omitFieldNames ? '' : 'navigationReadiness',
        enumValues: DiagnosticStatus.values)
    ..aOS(8, _omitFieldNames ? '' : 'navigationReadinessDetail')
    ..aE<DiagnosticStatus>(9, _omitFieldNames ? '' : 'telemetryStatus',
        enumValues: DiagnosticStatus.values)
    ..aOS(10, _omitFieldNames ? '' : 'telemetryDetail')
    ..pPM<DiagnosticCheck>(11, _omitFieldNames ? '' : 'checks',
        subBuilder: DiagnosticCheck.create)
    ..pPM<DiagnosticMessage>(12, _omitFieldNames ? '' : 'recentMessages',
        subBuilder: DiagnosticMessage.create)
    ..aOM<$1.Timestamp>(13, _omitFieldNames ? '' : 'observedAt',
        subBuilder: $1.Timestamp.create)
    ..aI(14, _omitFieldNames ? '' : 'systemId', fieldType: $pb.PbFieldType.OU3)
    ..aI(15, _omitFieldNames ? '' : 'componentId',
        fieldType: $pb.PbFieldType.OU3)
    ..aOS(16, _omitFieldNames ? '' : 'version')
    ..aOS(17, _omitFieldNames ? '' : 'mode')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  DiagnosticsSnapshot clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  DiagnosticsSnapshot copyWith(void Function(DiagnosticsSnapshot) updates) =>
      super.copyWith((message) => updates(message as DiagnosticsSnapshot))
          as DiagnosticsSnapshot;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static DiagnosticsSnapshot create() => DiagnosticsSnapshot._();
  @$core.override
  DiagnosticsSnapshot createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static DiagnosticsSnapshot getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<DiagnosticsSnapshot>(create);
  static DiagnosticsSnapshot? _defaultInstance;

  @$pb.TagNumber(1)
  $core.bool get reported => $_getBF(0);
  @$pb.TagNumber(1)
  set reported($core.bool value) => $_setBool(0, value);
  @$pb.TagNumber(1)
  $core.bool hasReported() => $_has(0);
  @$pb.TagNumber(1)
  void clearReported() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get backend => $_getSZ(1);
  @$pb.TagNumber(2)
  set backend($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasBackend() => $_has(1);
  @$pb.TagNumber(2)
  void clearBackend() => $_clearField(2);

  @$pb.TagNumber(3)
  DiagnosticStatus get overallStatus => $_getN(2);
  @$pb.TagNumber(3)
  set overallStatus(DiagnosticStatus value) => $_setField(3, value);
  @$pb.TagNumber(3)
  $core.bool hasOverallStatus() => $_has(2);
  @$pb.TagNumber(3)
  void clearOverallStatus() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.String get summary => $_getSZ(3);
  @$pb.TagNumber(4)
  set summary($core.String value) => $_setString(3, value);
  @$pb.TagNumber(4)
  $core.bool hasSummary() => $_has(3);
  @$pb.TagNumber(4)
  void clearSummary() => $_clearField(4);

  @$pb.TagNumber(5)
  DiagnosticStatus get armReadiness => $_getN(4);
  @$pb.TagNumber(5)
  set armReadiness(DiagnosticStatus value) => $_setField(5, value);
  @$pb.TagNumber(5)
  $core.bool hasArmReadiness() => $_has(4);
  @$pb.TagNumber(5)
  void clearArmReadiness() => $_clearField(5);

  @$pb.TagNumber(6)
  $core.String get armReadinessDetail => $_getSZ(5);
  @$pb.TagNumber(6)
  set armReadinessDetail($core.String value) => $_setString(5, value);
  @$pb.TagNumber(6)
  $core.bool hasArmReadinessDetail() => $_has(5);
  @$pb.TagNumber(6)
  void clearArmReadinessDetail() => $_clearField(6);

  @$pb.TagNumber(7)
  DiagnosticStatus get navigationReadiness => $_getN(6);
  @$pb.TagNumber(7)
  set navigationReadiness(DiagnosticStatus value) => $_setField(7, value);
  @$pb.TagNumber(7)
  $core.bool hasNavigationReadiness() => $_has(6);
  @$pb.TagNumber(7)
  void clearNavigationReadiness() => $_clearField(7);

  @$pb.TagNumber(8)
  $core.String get navigationReadinessDetail => $_getSZ(7);
  @$pb.TagNumber(8)
  set navigationReadinessDetail($core.String value) => $_setString(7, value);
  @$pb.TagNumber(8)
  $core.bool hasNavigationReadinessDetail() => $_has(7);
  @$pb.TagNumber(8)
  void clearNavigationReadinessDetail() => $_clearField(8);

  @$pb.TagNumber(9)
  DiagnosticStatus get telemetryStatus => $_getN(8);
  @$pb.TagNumber(9)
  set telemetryStatus(DiagnosticStatus value) => $_setField(9, value);
  @$pb.TagNumber(9)
  $core.bool hasTelemetryStatus() => $_has(8);
  @$pb.TagNumber(9)
  void clearTelemetryStatus() => $_clearField(9);

  @$pb.TagNumber(10)
  $core.String get telemetryDetail => $_getSZ(9);
  @$pb.TagNumber(10)
  set telemetryDetail($core.String value) => $_setString(9, value);
  @$pb.TagNumber(10)
  $core.bool hasTelemetryDetail() => $_has(9);
  @$pb.TagNumber(10)
  void clearTelemetryDetail() => $_clearField(10);

  @$pb.TagNumber(11)
  $pb.PbList<DiagnosticCheck> get checks => $_getList(10);

  @$pb.TagNumber(12)
  $pb.PbList<DiagnosticMessage> get recentMessages => $_getList(11);

  @$pb.TagNumber(13)
  $1.Timestamp get observedAt => $_getN(12);
  @$pb.TagNumber(13)
  set observedAt($1.Timestamp value) => $_setField(13, value);
  @$pb.TagNumber(13)
  $core.bool hasObservedAt() => $_has(12);
  @$pb.TagNumber(13)
  void clearObservedAt() => $_clearField(13);
  @$pb.TagNumber(13)
  $1.Timestamp ensureObservedAt() => $_ensure(12);

  @$pb.TagNumber(14)
  $core.int get systemId => $_getIZ(13);
  @$pb.TagNumber(14)
  set systemId($core.int value) => $_setUnsignedInt32(13, value);
  @$pb.TagNumber(14)
  $core.bool hasSystemId() => $_has(13);
  @$pb.TagNumber(14)
  void clearSystemId() => $_clearField(14);

  @$pb.TagNumber(15)
  $core.int get componentId => $_getIZ(14);
  @$pb.TagNumber(15)
  set componentId($core.int value) => $_setUnsignedInt32(14, value);
  @$pb.TagNumber(15)
  $core.bool hasComponentId() => $_has(14);
  @$pb.TagNumber(15)
  void clearComponentId() => $_clearField(15);

  @$pb.TagNumber(16)
  $core.String get version => $_getSZ(15);
  @$pb.TagNumber(16)
  set version($core.String value) => $_setString(15, value);
  @$pb.TagNumber(16)
  $core.bool hasVersion() => $_has(15);
  @$pb.TagNumber(16)
  void clearVersion() => $_clearField(16);

  @$pb.TagNumber(17)
  $core.String get mode => $_getSZ(16);
  @$pb.TagNumber(17)
  set mode($core.String value) => $_setString(16, value);
  @$pb.TagNumber(17)
  $core.bool hasMode() => $_has(16);
  @$pb.TagNumber(17)
  void clearMode() => $_clearField(17);
}

class DiagnosticCheck extends $pb.GeneratedMessage {
  factory DiagnosticCheck({
    $core.String? code,
    $core.String? category,
    $core.String? name,
    DiagnosticCheckState? state,
    $core.String? detail,
    $core.Iterable<$core.String>? affectedOperations,
  }) {
    final result = create();
    if (code != null) result.code = code;
    if (category != null) result.category = category;
    if (name != null) result.name = name;
    if (state != null) result.state = state;
    if (detail != null) result.detail = detail;
    if (affectedOperations != null)
      result.affectedOperations.addAll(affectedOperations);
    return result;
  }

  DiagnosticCheck._();

  factory DiagnosticCheck.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory DiagnosticCheck.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'DiagnosticCheck',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'code')
    ..aOS(2, _omitFieldNames ? '' : 'category')
    ..aOS(3, _omitFieldNames ? '' : 'name')
    ..aE<DiagnosticCheckState>(4, _omitFieldNames ? '' : 'state',
        enumValues: DiagnosticCheckState.values)
    ..aOS(5, _omitFieldNames ? '' : 'detail')
    ..pPS(6, _omitFieldNames ? '' : 'affectedOperations')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  DiagnosticCheck clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  DiagnosticCheck copyWith(void Function(DiagnosticCheck) updates) =>
      super.copyWith((message) => updates(message as DiagnosticCheck))
          as DiagnosticCheck;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static DiagnosticCheck create() => DiagnosticCheck._();
  @$core.override
  DiagnosticCheck createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static DiagnosticCheck getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<DiagnosticCheck>(create);
  static DiagnosticCheck? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get code => $_getSZ(0);
  @$pb.TagNumber(1)
  set code($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasCode() => $_has(0);
  @$pb.TagNumber(1)
  void clearCode() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get category => $_getSZ(1);
  @$pb.TagNumber(2)
  set category($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasCategory() => $_has(1);
  @$pb.TagNumber(2)
  void clearCategory() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.String get name => $_getSZ(2);
  @$pb.TagNumber(3)
  set name($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasName() => $_has(2);
  @$pb.TagNumber(3)
  void clearName() => $_clearField(3);

  @$pb.TagNumber(4)
  DiagnosticCheckState get state => $_getN(3);
  @$pb.TagNumber(4)
  set state(DiagnosticCheckState value) => $_setField(4, value);
  @$pb.TagNumber(4)
  $core.bool hasState() => $_has(3);
  @$pb.TagNumber(4)
  void clearState() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.String get detail => $_getSZ(4);
  @$pb.TagNumber(5)
  set detail($core.String value) => $_setString(4, value);
  @$pb.TagNumber(5)
  $core.bool hasDetail() => $_has(4);
  @$pb.TagNumber(5)
  void clearDetail() => $_clearField(5);

  @$pb.TagNumber(6)
  $pb.PbList<$core.String> get affectedOperations => $_getList(5);
}

class DiagnosticMessage extends $pb.GeneratedMessage {
  factory DiagnosticMessage({
    $core.String? id,
    $1.Timestamp? timestamp,
    $core.String? severity,
    $core.String? text,
    $core.String? source,
  }) {
    final result = create();
    if (id != null) result.id = id;
    if (timestamp != null) result.timestamp = timestamp;
    if (severity != null) result.severity = severity;
    if (text != null) result.text = text;
    if (source != null) result.source = source;
    return result;
  }

  DiagnosticMessage._();

  factory DiagnosticMessage.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory DiagnosticMessage.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'DiagnosticMessage',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'id')
    ..aOM<$1.Timestamp>(2, _omitFieldNames ? '' : 'timestamp',
        subBuilder: $1.Timestamp.create)
    ..aOS(3, _omitFieldNames ? '' : 'severity')
    ..aOS(4, _omitFieldNames ? '' : 'text')
    ..aOS(5, _omitFieldNames ? '' : 'source')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  DiagnosticMessage clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  DiagnosticMessage copyWith(void Function(DiagnosticMessage) updates) =>
      super.copyWith((message) => updates(message as DiagnosticMessage))
          as DiagnosticMessage;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static DiagnosticMessage create() => DiagnosticMessage._();
  @$core.override
  DiagnosticMessage createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static DiagnosticMessage getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<DiagnosticMessage>(create);
  static DiagnosticMessage? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get id => $_getSZ(0);
  @$pb.TagNumber(1)
  set id($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasId() => $_has(0);
  @$pb.TagNumber(1)
  void clearId() => $_clearField(1);

  @$pb.TagNumber(2)
  $1.Timestamp get timestamp => $_getN(1);
  @$pb.TagNumber(2)
  set timestamp($1.Timestamp value) => $_setField(2, value);
  @$pb.TagNumber(2)
  $core.bool hasTimestamp() => $_has(1);
  @$pb.TagNumber(2)
  void clearTimestamp() => $_clearField(2);
  @$pb.TagNumber(2)
  $1.Timestamp ensureTimestamp() => $_ensure(1);

  @$pb.TagNumber(3)
  $core.String get severity => $_getSZ(2);
  @$pb.TagNumber(3)
  set severity($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasSeverity() => $_has(2);
  @$pb.TagNumber(3)
  void clearSeverity() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.String get text => $_getSZ(3);
  @$pb.TagNumber(4)
  set text($core.String value) => $_setString(3, value);
  @$pb.TagNumber(4)
  $core.bool hasText() => $_has(3);
  @$pb.TagNumber(4)
  void clearText() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.String get source => $_getSZ(4);
  @$pb.TagNumber(5)
  set source($core.String value) => $_setString(4, value);
  @$pb.TagNumber(5)
  $core.bool hasSource() => $_has(4);
  @$pb.TagNumber(5)
  void clearSource() => $_clearField(5);
}

class ActionSnapshot extends $pb.GeneratedMessage {
  factory ActionSnapshot({
    $core.String? currentKind,
    $core.String? currentState,
    $core.String? currentSummary,
    $core.String? queuedKind,
    $core.String? queuedState,
    $core.String? queuedSummary,
    $1.Timestamp? updatedAt,
  }) {
    final result = create();
    if (currentKind != null) result.currentKind = currentKind;
    if (currentState != null) result.currentState = currentState;
    if (currentSummary != null) result.currentSummary = currentSummary;
    if (queuedKind != null) result.queuedKind = queuedKind;
    if (queuedState != null) result.queuedState = queuedState;
    if (queuedSummary != null) result.queuedSummary = queuedSummary;
    if (updatedAt != null) result.updatedAt = updatedAt;
    return result;
  }

  ActionSnapshot._();

  factory ActionSnapshot.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory ActionSnapshot.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'ActionSnapshot',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'currentKind')
    ..aOS(2, _omitFieldNames ? '' : 'currentState')
    ..aOS(3, _omitFieldNames ? '' : 'currentSummary')
    ..aOS(4, _omitFieldNames ? '' : 'queuedKind')
    ..aOS(5, _omitFieldNames ? '' : 'queuedState')
    ..aOS(6, _omitFieldNames ? '' : 'queuedSummary')
    ..aOM<$1.Timestamp>(7, _omitFieldNames ? '' : 'updatedAt',
        subBuilder: $1.Timestamp.create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  ActionSnapshot clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  ActionSnapshot copyWith(void Function(ActionSnapshot) updates) =>
      super.copyWith((message) => updates(message as ActionSnapshot))
          as ActionSnapshot;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static ActionSnapshot create() => ActionSnapshot._();
  @$core.override
  ActionSnapshot createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static ActionSnapshot getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<ActionSnapshot>(create);
  static ActionSnapshot? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get currentKind => $_getSZ(0);
  @$pb.TagNumber(1)
  set currentKind($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasCurrentKind() => $_has(0);
  @$pb.TagNumber(1)
  void clearCurrentKind() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get currentState => $_getSZ(1);
  @$pb.TagNumber(2)
  set currentState($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasCurrentState() => $_has(1);
  @$pb.TagNumber(2)
  void clearCurrentState() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.String get currentSummary => $_getSZ(2);
  @$pb.TagNumber(3)
  set currentSummary($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasCurrentSummary() => $_has(2);
  @$pb.TagNumber(3)
  void clearCurrentSummary() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.String get queuedKind => $_getSZ(3);
  @$pb.TagNumber(4)
  set queuedKind($core.String value) => $_setString(3, value);
  @$pb.TagNumber(4)
  $core.bool hasQueuedKind() => $_has(3);
  @$pb.TagNumber(4)
  void clearQueuedKind() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.String get queuedState => $_getSZ(4);
  @$pb.TagNumber(5)
  set queuedState($core.String value) => $_setString(4, value);
  @$pb.TagNumber(5)
  $core.bool hasQueuedState() => $_has(4);
  @$pb.TagNumber(5)
  void clearQueuedState() => $_clearField(5);

  @$pb.TagNumber(6)
  $core.String get queuedSummary => $_getSZ(5);
  @$pb.TagNumber(6)
  set queuedSummary($core.String value) => $_setString(5, value);
  @$pb.TagNumber(6)
  $core.bool hasQueuedSummary() => $_has(5);
  @$pb.TagNumber(6)
  void clearQueuedSummary() => $_clearField(6);

  @$pb.TagNumber(7)
  $1.Timestamp get updatedAt => $_getN(6);
  @$pb.TagNumber(7)
  set updatedAt($1.Timestamp value) => $_setField(7, value);
  @$pb.TagNumber(7)
  $core.bool hasUpdatedAt() => $_has(6);
  @$pb.TagNumber(7)
  void clearUpdatedAt() => $_clearField(7);
  @$pb.TagNumber(7)
  $1.Timestamp ensureUpdatedAt() => $_ensure(6);
}

class LinkSnapshot extends $pb.GeneratedMessage {
  factory LinkSnapshot({
    $core.String? id,
    $core.String? connectionId,
    $core.String? name,
    $core.String? kind,
    $core.String? direction,
    $core.String? state,
    $core.String? health,
    $core.bool? connected,
    $core.bool? stale,
    $core.double? rssiDbm,
    $core.double? snrDb,
    $core.double? quality,
    $core.double? packetLoss,
    $core.double? latencyMilliseconds,
    $core.String? code,
    $core.String? message,
    $1.Timestamp? observedAt,
  }) {
    final result = create();
    if (id != null) result.id = id;
    if (connectionId != null) result.connectionId = connectionId;
    if (name != null) result.name = name;
    if (kind != null) result.kind = kind;
    if (direction != null) result.direction = direction;
    if (state != null) result.state = state;
    if (health != null) result.health = health;
    if (connected != null) result.connected = connected;
    if (stale != null) result.stale = stale;
    if (rssiDbm != null) result.rssiDbm = rssiDbm;
    if (snrDb != null) result.snrDb = snrDb;
    if (quality != null) result.quality = quality;
    if (packetLoss != null) result.packetLoss = packetLoss;
    if (latencyMilliseconds != null)
      result.latencyMilliseconds = latencyMilliseconds;
    if (code != null) result.code = code;
    if (message != null) result.message = message;
    if (observedAt != null) result.observedAt = observedAt;
    return result;
  }

  LinkSnapshot._();

  factory LinkSnapshot.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory LinkSnapshot.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'LinkSnapshot',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'id')
    ..aOS(2, _omitFieldNames ? '' : 'connectionId')
    ..aOS(3, _omitFieldNames ? '' : 'name')
    ..aOS(4, _omitFieldNames ? '' : 'kind')
    ..aOS(5, _omitFieldNames ? '' : 'direction')
    ..aOS(6, _omitFieldNames ? '' : 'state')
    ..aOS(7, _omitFieldNames ? '' : 'health')
    ..aOB(8, _omitFieldNames ? '' : 'connected')
    ..aOB(9, _omitFieldNames ? '' : 'stale')
    ..aD(10, _omitFieldNames ? '' : 'rssiDbm')
    ..aD(11, _omitFieldNames ? '' : 'snrDb')
    ..aD(12, _omitFieldNames ? '' : 'quality')
    ..aD(13, _omitFieldNames ? '' : 'packetLoss')
    ..aD(14, _omitFieldNames ? '' : 'latencyMilliseconds')
    ..aOS(15, _omitFieldNames ? '' : 'code')
    ..aOS(16, _omitFieldNames ? '' : 'message')
    ..aOM<$1.Timestamp>(17, _omitFieldNames ? '' : 'observedAt',
        subBuilder: $1.Timestamp.create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  LinkSnapshot clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  LinkSnapshot copyWith(void Function(LinkSnapshot) updates) =>
      super.copyWith((message) => updates(message as LinkSnapshot))
          as LinkSnapshot;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static LinkSnapshot create() => LinkSnapshot._();
  @$core.override
  LinkSnapshot createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static LinkSnapshot getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<LinkSnapshot>(create);
  static LinkSnapshot? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get id => $_getSZ(0);
  @$pb.TagNumber(1)
  set id($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasId() => $_has(0);
  @$pb.TagNumber(1)
  void clearId() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get connectionId => $_getSZ(1);
  @$pb.TagNumber(2)
  set connectionId($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasConnectionId() => $_has(1);
  @$pb.TagNumber(2)
  void clearConnectionId() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.String get name => $_getSZ(2);
  @$pb.TagNumber(3)
  set name($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasName() => $_has(2);
  @$pb.TagNumber(3)
  void clearName() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.String get kind => $_getSZ(3);
  @$pb.TagNumber(4)
  set kind($core.String value) => $_setString(3, value);
  @$pb.TagNumber(4)
  $core.bool hasKind() => $_has(3);
  @$pb.TagNumber(4)
  void clearKind() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.String get direction => $_getSZ(4);
  @$pb.TagNumber(5)
  set direction($core.String value) => $_setString(4, value);
  @$pb.TagNumber(5)
  $core.bool hasDirection() => $_has(4);
  @$pb.TagNumber(5)
  void clearDirection() => $_clearField(5);

  @$pb.TagNumber(6)
  $core.String get state => $_getSZ(5);
  @$pb.TagNumber(6)
  set state($core.String value) => $_setString(5, value);
  @$pb.TagNumber(6)
  $core.bool hasState() => $_has(5);
  @$pb.TagNumber(6)
  void clearState() => $_clearField(6);

  @$pb.TagNumber(7)
  $core.String get health => $_getSZ(6);
  @$pb.TagNumber(7)
  set health($core.String value) => $_setString(6, value);
  @$pb.TagNumber(7)
  $core.bool hasHealth() => $_has(6);
  @$pb.TagNumber(7)
  void clearHealth() => $_clearField(7);

  @$pb.TagNumber(8)
  $core.bool get connected => $_getBF(7);
  @$pb.TagNumber(8)
  set connected($core.bool value) => $_setBool(7, value);
  @$pb.TagNumber(8)
  $core.bool hasConnected() => $_has(7);
  @$pb.TagNumber(8)
  void clearConnected() => $_clearField(8);

  @$pb.TagNumber(9)
  $core.bool get stale => $_getBF(8);
  @$pb.TagNumber(9)
  set stale($core.bool value) => $_setBool(8, value);
  @$pb.TagNumber(9)
  $core.bool hasStale() => $_has(8);
  @$pb.TagNumber(9)
  void clearStale() => $_clearField(9);

  @$pb.TagNumber(10)
  $core.double get rssiDbm => $_getN(9);
  @$pb.TagNumber(10)
  set rssiDbm($core.double value) => $_setDouble(9, value);
  @$pb.TagNumber(10)
  $core.bool hasRssiDbm() => $_has(9);
  @$pb.TagNumber(10)
  void clearRssiDbm() => $_clearField(10);

  @$pb.TagNumber(11)
  $core.double get snrDb => $_getN(10);
  @$pb.TagNumber(11)
  set snrDb($core.double value) => $_setDouble(10, value);
  @$pb.TagNumber(11)
  $core.bool hasSnrDb() => $_has(10);
  @$pb.TagNumber(11)
  void clearSnrDb() => $_clearField(11);

  @$pb.TagNumber(12)
  $core.double get quality => $_getN(11);
  @$pb.TagNumber(12)
  set quality($core.double value) => $_setDouble(11, value);
  @$pb.TagNumber(12)
  $core.bool hasQuality() => $_has(11);
  @$pb.TagNumber(12)
  void clearQuality() => $_clearField(12);

  @$pb.TagNumber(13)
  $core.double get packetLoss => $_getN(12);
  @$pb.TagNumber(13)
  set packetLoss($core.double value) => $_setDouble(12, value);
  @$pb.TagNumber(13)
  $core.bool hasPacketLoss() => $_has(12);
  @$pb.TagNumber(13)
  void clearPacketLoss() => $_clearField(13);

  @$pb.TagNumber(14)
  $core.double get latencyMilliseconds => $_getN(13);
  @$pb.TagNumber(14)
  set latencyMilliseconds($core.double value) => $_setDouble(13, value);
  @$pb.TagNumber(14)
  $core.bool hasLatencyMilliseconds() => $_has(13);
  @$pb.TagNumber(14)
  void clearLatencyMilliseconds() => $_clearField(14);

  @$pb.TagNumber(15)
  $core.String get code => $_getSZ(14);
  @$pb.TagNumber(15)
  set code($core.String value) => $_setString(14, value);
  @$pb.TagNumber(15)
  $core.bool hasCode() => $_has(14);
  @$pb.TagNumber(15)
  void clearCode() => $_clearField(15);

  @$pb.TagNumber(16)
  $core.String get message => $_getSZ(15);
  @$pb.TagNumber(16)
  set message($core.String value) => $_setString(15, value);
  @$pb.TagNumber(16)
  $core.bool hasMessage() => $_has(15);
  @$pb.TagNumber(16)
  void clearMessage() => $_clearField(16);

  @$pb.TagNumber(17)
  $1.Timestamp get observedAt => $_getN(16);
  @$pb.TagNumber(17)
  set observedAt($1.Timestamp value) => $_setField(17, value);
  @$pb.TagNumber(17)
  $core.bool hasObservedAt() => $_has(16);
  @$pb.TagNumber(17)
  void clearObservedAt() => $_clearField(17);
  @$pb.TagNumber(17)
  $1.Timestamp ensureObservedAt() => $_ensure(16);
}

class MapSnapshot extends $pb.GeneratedMessage {
  factory MapSnapshot({
    $core.String? coordinateFrame,
    $core.String? frameLabel,
    MapViewport? viewport,
    $core.String? styleId,
    $core.String? styleName,
    $core.String? styleAttribution,
    $core.bool? geometryVisible,
    $core.bool? policyVisible,
    $core.bool? trailsVisible,
    $core.bool? destinationsVisible,
    $core.bool? labelsVisible,
    $core.Iterable<$core.String>? selectedUnitIds,
    $core.Iterable<MapUnitVisual>? unitVisuals,
    $core.Iterable<MapTrail>? trails,
    $core.Iterable<MapGeometry>? geometries,
    $core.Iterable<MapDestination>? destinations,
    $core.Iterable<MapDestination>? formationPreviewDestinations,
    $core.Iterable<MapPath>? formationPreviewPaths,
    OperatorLocation? operatorLocation,
  }) {
    final result = create();
    if (coordinateFrame != null) result.coordinateFrame = coordinateFrame;
    if (frameLabel != null) result.frameLabel = frameLabel;
    if (viewport != null) result.viewport = viewport;
    if (styleId != null) result.styleId = styleId;
    if (styleName != null) result.styleName = styleName;
    if (styleAttribution != null) result.styleAttribution = styleAttribution;
    if (geometryVisible != null) result.geometryVisible = geometryVisible;
    if (policyVisible != null) result.policyVisible = policyVisible;
    if (trailsVisible != null) result.trailsVisible = trailsVisible;
    if (destinationsVisible != null)
      result.destinationsVisible = destinationsVisible;
    if (labelsVisible != null) result.labelsVisible = labelsVisible;
    if (selectedUnitIds != null) result.selectedUnitIds.addAll(selectedUnitIds);
    if (unitVisuals != null) result.unitVisuals.addAll(unitVisuals);
    if (trails != null) result.trails.addAll(trails);
    if (geometries != null) result.geometries.addAll(geometries);
    if (destinations != null) result.destinations.addAll(destinations);
    if (formationPreviewDestinations != null)
      result.formationPreviewDestinations.addAll(formationPreviewDestinations);
    if (formationPreviewPaths != null)
      result.formationPreviewPaths.addAll(formationPreviewPaths);
    if (operatorLocation != null) result.operatorLocation = operatorLocation;
    return result;
  }

  MapSnapshot._();

  factory MapSnapshot.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory MapSnapshot.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'MapSnapshot',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'coordinateFrame')
    ..aOS(2, _omitFieldNames ? '' : 'frameLabel')
    ..aOM<MapViewport>(3, _omitFieldNames ? '' : 'viewport',
        subBuilder: MapViewport.create)
    ..aOS(4, _omitFieldNames ? '' : 'styleId')
    ..aOS(5, _omitFieldNames ? '' : 'styleName')
    ..aOS(6, _omitFieldNames ? '' : 'styleAttribution')
    ..aOB(7, _omitFieldNames ? '' : 'geometryVisible')
    ..aOB(8, _omitFieldNames ? '' : 'policyVisible')
    ..aOB(9, _omitFieldNames ? '' : 'trailsVisible')
    ..aOB(10, _omitFieldNames ? '' : 'destinationsVisible')
    ..aOB(11, _omitFieldNames ? '' : 'labelsVisible')
    ..pPS(12, _omitFieldNames ? '' : 'selectedUnitIds')
    ..pPM<MapUnitVisual>(13, _omitFieldNames ? '' : 'unitVisuals',
        subBuilder: MapUnitVisual.create)
    ..pPM<MapTrail>(14, _omitFieldNames ? '' : 'trails',
        subBuilder: MapTrail.create)
    ..pPM<MapGeometry>(15, _omitFieldNames ? '' : 'geometries',
        subBuilder: MapGeometry.create)
    ..pPM<MapDestination>(16, _omitFieldNames ? '' : 'destinations',
        subBuilder: MapDestination.create)
    ..pPM<MapDestination>(
        17, _omitFieldNames ? '' : 'formationPreviewDestinations',
        subBuilder: MapDestination.create)
    ..pPM<MapPath>(18, _omitFieldNames ? '' : 'formationPreviewPaths',
        subBuilder: MapPath.create)
    ..aOM<OperatorLocation>(19, _omitFieldNames ? '' : 'operatorLocation',
        subBuilder: OperatorLocation.create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapSnapshot clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapSnapshot copyWith(void Function(MapSnapshot) updates) =>
      super.copyWith((message) => updates(message as MapSnapshot))
          as MapSnapshot;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static MapSnapshot create() => MapSnapshot._();
  @$core.override
  MapSnapshot createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static MapSnapshot getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<MapSnapshot>(create);
  static MapSnapshot? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get coordinateFrame => $_getSZ(0);
  @$pb.TagNumber(1)
  set coordinateFrame($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasCoordinateFrame() => $_has(0);
  @$pb.TagNumber(1)
  void clearCoordinateFrame() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get frameLabel => $_getSZ(1);
  @$pb.TagNumber(2)
  set frameLabel($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasFrameLabel() => $_has(1);
  @$pb.TagNumber(2)
  void clearFrameLabel() => $_clearField(2);

  @$pb.TagNumber(3)
  MapViewport get viewport => $_getN(2);
  @$pb.TagNumber(3)
  set viewport(MapViewport value) => $_setField(3, value);
  @$pb.TagNumber(3)
  $core.bool hasViewport() => $_has(2);
  @$pb.TagNumber(3)
  void clearViewport() => $_clearField(3);
  @$pb.TagNumber(3)
  MapViewport ensureViewport() => $_ensure(2);

  @$pb.TagNumber(4)
  $core.String get styleId => $_getSZ(3);
  @$pb.TagNumber(4)
  set styleId($core.String value) => $_setString(3, value);
  @$pb.TagNumber(4)
  $core.bool hasStyleId() => $_has(3);
  @$pb.TagNumber(4)
  void clearStyleId() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.String get styleName => $_getSZ(4);
  @$pb.TagNumber(5)
  set styleName($core.String value) => $_setString(4, value);
  @$pb.TagNumber(5)
  $core.bool hasStyleName() => $_has(4);
  @$pb.TagNumber(5)
  void clearStyleName() => $_clearField(5);

  @$pb.TagNumber(6)
  $core.String get styleAttribution => $_getSZ(5);
  @$pb.TagNumber(6)
  set styleAttribution($core.String value) => $_setString(5, value);
  @$pb.TagNumber(6)
  $core.bool hasStyleAttribution() => $_has(5);
  @$pb.TagNumber(6)
  void clearStyleAttribution() => $_clearField(6);

  @$pb.TagNumber(7)
  $core.bool get geometryVisible => $_getBF(6);
  @$pb.TagNumber(7)
  set geometryVisible($core.bool value) => $_setBool(6, value);
  @$pb.TagNumber(7)
  $core.bool hasGeometryVisible() => $_has(6);
  @$pb.TagNumber(7)
  void clearGeometryVisible() => $_clearField(7);

  @$pb.TagNumber(8)
  $core.bool get policyVisible => $_getBF(7);
  @$pb.TagNumber(8)
  set policyVisible($core.bool value) => $_setBool(7, value);
  @$pb.TagNumber(8)
  $core.bool hasPolicyVisible() => $_has(7);
  @$pb.TagNumber(8)
  void clearPolicyVisible() => $_clearField(8);

  @$pb.TagNumber(9)
  $core.bool get trailsVisible => $_getBF(8);
  @$pb.TagNumber(9)
  set trailsVisible($core.bool value) => $_setBool(8, value);
  @$pb.TagNumber(9)
  $core.bool hasTrailsVisible() => $_has(8);
  @$pb.TagNumber(9)
  void clearTrailsVisible() => $_clearField(9);

  @$pb.TagNumber(10)
  $core.bool get destinationsVisible => $_getBF(9);
  @$pb.TagNumber(10)
  set destinationsVisible($core.bool value) => $_setBool(9, value);
  @$pb.TagNumber(10)
  $core.bool hasDestinationsVisible() => $_has(9);
  @$pb.TagNumber(10)
  void clearDestinationsVisible() => $_clearField(10);

  @$pb.TagNumber(11)
  $core.bool get labelsVisible => $_getBF(10);
  @$pb.TagNumber(11)
  set labelsVisible($core.bool value) => $_setBool(10, value);
  @$pb.TagNumber(11)
  $core.bool hasLabelsVisible() => $_has(10);
  @$pb.TagNumber(11)
  void clearLabelsVisible() => $_clearField(11);

  @$pb.TagNumber(12)
  $pb.PbList<$core.String> get selectedUnitIds => $_getList(11);

  @$pb.TagNumber(13)
  $pb.PbList<MapUnitVisual> get unitVisuals => $_getList(12);

  @$pb.TagNumber(14)
  $pb.PbList<MapTrail> get trails => $_getList(13);

  @$pb.TagNumber(15)
  $pb.PbList<MapGeometry> get geometries => $_getList(14);

  @$pb.TagNumber(16)
  $pb.PbList<MapDestination> get destinations => $_getList(15);

  @$pb.TagNumber(17)
  $pb.PbList<MapDestination> get formationPreviewDestinations => $_getList(16);

  @$pb.TagNumber(18)
  $pb.PbList<MapPath> get formationPreviewPaths => $_getList(17);

  @$pb.TagNumber(19)
  OperatorLocation get operatorLocation => $_getN(18);
  @$pb.TagNumber(19)
  set operatorLocation(OperatorLocation value) => $_setField(19, value);
  @$pb.TagNumber(19)
  $core.bool hasOperatorLocation() => $_has(18);
  @$pb.TagNumber(19)
  void clearOperatorLocation() => $_clearField(19);
  @$pb.TagNumber(19)
  OperatorLocation ensureOperatorLocation() => $_ensure(18);
}

class MapViewport extends $pb.GeneratedMessage {
  factory MapViewport({
    $core.bool? reported,
    $core.double? longitudeDegrees,
    $core.double? latitudeDegrees,
    $core.double? resolutionMetresPerPixel,
    $core.double? rotationDegrees,
  }) {
    final result = create();
    if (reported != null) result.reported = reported;
    if (longitudeDegrees != null) result.longitudeDegrees = longitudeDegrees;
    if (latitudeDegrees != null) result.latitudeDegrees = latitudeDegrees;
    if (resolutionMetresPerPixel != null)
      result.resolutionMetresPerPixel = resolutionMetresPerPixel;
    if (rotationDegrees != null) result.rotationDegrees = rotationDegrees;
    return result;
  }

  MapViewport._();

  factory MapViewport.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory MapViewport.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'MapViewport',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOB(1, _omitFieldNames ? '' : 'reported')
    ..aD(2, _omitFieldNames ? '' : 'longitudeDegrees')
    ..aD(3, _omitFieldNames ? '' : 'latitudeDegrees')
    ..aD(4, _omitFieldNames ? '' : 'resolutionMetresPerPixel')
    ..aD(5, _omitFieldNames ? '' : 'rotationDegrees')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapViewport clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapViewport copyWith(void Function(MapViewport) updates) =>
      super.copyWith((message) => updates(message as MapViewport))
          as MapViewport;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static MapViewport create() => MapViewport._();
  @$core.override
  MapViewport createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static MapViewport getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<MapViewport>(create);
  static MapViewport? _defaultInstance;

  @$pb.TagNumber(1)
  $core.bool get reported => $_getBF(0);
  @$pb.TagNumber(1)
  set reported($core.bool value) => $_setBool(0, value);
  @$pb.TagNumber(1)
  $core.bool hasReported() => $_has(0);
  @$pb.TagNumber(1)
  void clearReported() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.double get longitudeDegrees => $_getN(1);
  @$pb.TagNumber(2)
  set longitudeDegrees($core.double value) => $_setDouble(1, value);
  @$pb.TagNumber(2)
  $core.bool hasLongitudeDegrees() => $_has(1);
  @$pb.TagNumber(2)
  void clearLongitudeDegrees() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.double get latitudeDegrees => $_getN(2);
  @$pb.TagNumber(3)
  set latitudeDegrees($core.double value) => $_setDouble(2, value);
  @$pb.TagNumber(3)
  $core.bool hasLatitudeDegrees() => $_has(2);
  @$pb.TagNumber(3)
  void clearLatitudeDegrees() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.double get resolutionMetresPerPixel => $_getN(3);
  @$pb.TagNumber(4)
  set resolutionMetresPerPixel($core.double value) => $_setDouble(3, value);
  @$pb.TagNumber(4)
  $core.bool hasResolutionMetresPerPixel() => $_has(3);
  @$pb.TagNumber(4)
  void clearResolutionMetresPerPixel() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.double get rotationDegrees => $_getN(4);
  @$pb.TagNumber(5)
  set rotationDegrees($core.double value) => $_setDouble(4, value);
  @$pb.TagNumber(5)
  $core.bool hasRotationDegrees() => $_has(4);
  @$pb.TagNumber(5)
  void clearRotationDegrees() => $_clearField(5);
}

class MapUnitVisual extends $pb.GeneratedMessage {
  factory MapUnitVisual({
    $core.String? unitId,
    $core.String? name,
    $core.double? x,
    $core.double? y,
    $core.double? headingDegrees,
    Availability? state,
    $core.bool? selected,
    $core.bool? ghost,
  }) {
    final result = create();
    if (unitId != null) result.unitId = unitId;
    if (name != null) result.name = name;
    if (x != null) result.x = x;
    if (y != null) result.y = y;
    if (headingDegrees != null) result.headingDegrees = headingDegrees;
    if (state != null) result.state = state;
    if (selected != null) result.selected = selected;
    if (ghost != null) result.ghost = ghost;
    return result;
  }

  MapUnitVisual._();

  factory MapUnitVisual.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory MapUnitVisual.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'MapUnitVisual',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'unitId')
    ..aOS(2, _omitFieldNames ? '' : 'name')
    ..aD(3, _omitFieldNames ? '' : 'x')
    ..aD(4, _omitFieldNames ? '' : 'y')
    ..aD(5, _omitFieldNames ? '' : 'headingDegrees')
    ..aE<Availability>(6, _omitFieldNames ? '' : 'state',
        enumValues: Availability.values)
    ..aOB(7, _omitFieldNames ? '' : 'selected')
    ..aOB(8, _omitFieldNames ? '' : 'ghost')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapUnitVisual clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapUnitVisual copyWith(void Function(MapUnitVisual) updates) =>
      super.copyWith((message) => updates(message as MapUnitVisual))
          as MapUnitVisual;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static MapUnitVisual create() => MapUnitVisual._();
  @$core.override
  MapUnitVisual createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static MapUnitVisual getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<MapUnitVisual>(create);
  static MapUnitVisual? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get unitId => $_getSZ(0);
  @$pb.TagNumber(1)
  set unitId($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasUnitId() => $_has(0);
  @$pb.TagNumber(1)
  void clearUnitId() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get name => $_getSZ(1);
  @$pb.TagNumber(2)
  set name($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasName() => $_has(1);
  @$pb.TagNumber(2)
  void clearName() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.double get x => $_getN(2);
  @$pb.TagNumber(3)
  set x($core.double value) => $_setDouble(2, value);
  @$pb.TagNumber(3)
  $core.bool hasX() => $_has(2);
  @$pb.TagNumber(3)
  void clearX() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.double get y => $_getN(3);
  @$pb.TagNumber(4)
  set y($core.double value) => $_setDouble(3, value);
  @$pb.TagNumber(4)
  $core.bool hasY() => $_has(3);
  @$pb.TagNumber(4)
  void clearY() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.double get headingDegrees => $_getN(4);
  @$pb.TagNumber(5)
  set headingDegrees($core.double value) => $_setDouble(4, value);
  @$pb.TagNumber(5)
  $core.bool hasHeadingDegrees() => $_has(4);
  @$pb.TagNumber(5)
  void clearHeadingDegrees() => $_clearField(5);

  @$pb.TagNumber(6)
  Availability get state => $_getN(5);
  @$pb.TagNumber(6)
  set state(Availability value) => $_setField(6, value);
  @$pb.TagNumber(6)
  $core.bool hasState() => $_has(5);
  @$pb.TagNumber(6)
  void clearState() => $_clearField(6);

  @$pb.TagNumber(7)
  $core.bool get selected => $_getBF(6);
  @$pb.TagNumber(7)
  set selected($core.bool value) => $_setBool(6, value);
  @$pb.TagNumber(7)
  $core.bool hasSelected() => $_has(6);
  @$pb.TagNumber(7)
  void clearSelected() => $_clearField(7);

  @$pb.TagNumber(8)
  $core.bool get ghost => $_getBF(7);
  @$pb.TagNumber(8)
  set ghost($core.bool value) => $_setBool(7, value);
  @$pb.TagNumber(8)
  $core.bool hasGhost() => $_has(7);
  @$pb.TagNumber(8)
  void clearGhost() => $_clearField(8);
}

class GeoPoint extends $pb.GeneratedMessage {
  factory GeoPoint({
    $core.double? x,
    $core.double? y,
    $core.double? altitudeMetres,
  }) {
    final result = create();
    if (x != null) result.x = x;
    if (y != null) result.y = y;
    if (altitudeMetres != null) result.altitudeMetres = altitudeMetres;
    return result;
  }

  GeoPoint._();

  factory GeoPoint.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory GeoPoint.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'GeoPoint',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aD(1, _omitFieldNames ? '' : 'x')
    ..aD(2, _omitFieldNames ? '' : 'y')
    ..aD(3, _omitFieldNames ? '' : 'altitudeMetres')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  GeoPoint clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  GeoPoint copyWith(void Function(GeoPoint) updates) =>
      super.copyWith((message) => updates(message as GeoPoint)) as GeoPoint;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static GeoPoint create() => GeoPoint._();
  @$core.override
  GeoPoint createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static GeoPoint getDefault() =>
      _defaultInstance ??= $pb.GeneratedMessage.$_defaultFor<GeoPoint>(create);
  static GeoPoint? _defaultInstance;

  @$pb.TagNumber(1)
  $core.double get x => $_getN(0);
  @$pb.TagNumber(1)
  set x($core.double value) => $_setDouble(0, value);
  @$pb.TagNumber(1)
  $core.bool hasX() => $_has(0);
  @$pb.TagNumber(1)
  void clearX() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.double get y => $_getN(1);
  @$pb.TagNumber(2)
  set y($core.double value) => $_setDouble(1, value);
  @$pb.TagNumber(2)
  $core.bool hasY() => $_has(1);
  @$pb.TagNumber(2)
  void clearY() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.double get altitudeMetres => $_getN(2);
  @$pb.TagNumber(3)
  set altitudeMetres($core.double value) => $_setDouble(2, value);
  @$pb.TagNumber(3)
  $core.bool hasAltitudeMetres() => $_has(2);
  @$pb.TagNumber(3)
  void clearAltitudeMetres() => $_clearField(3);
}

class MapTrail extends $pb.GeneratedMessage {
  factory MapTrail({
    $core.String? unitId,
    $core.String? name,
    $core.String? connectionId,
    $core.Iterable<GeoPoint>? points,
    Availability? state,
    $core.bool? selected,
  }) {
    final result = create();
    if (unitId != null) result.unitId = unitId;
    if (name != null) result.name = name;
    if (connectionId != null) result.connectionId = connectionId;
    if (points != null) result.points.addAll(points);
    if (state != null) result.state = state;
    if (selected != null) result.selected = selected;
    return result;
  }

  MapTrail._();

  factory MapTrail.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory MapTrail.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'MapTrail',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'unitId')
    ..aOS(2, _omitFieldNames ? '' : 'name')
    ..aOS(3, _omitFieldNames ? '' : 'connectionId')
    ..pPM<GeoPoint>(4, _omitFieldNames ? '' : 'points',
        subBuilder: GeoPoint.create)
    ..aE<Availability>(5, _omitFieldNames ? '' : 'state',
        enumValues: Availability.values)
    ..aOB(6, _omitFieldNames ? '' : 'selected')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapTrail clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapTrail copyWith(void Function(MapTrail) updates) =>
      super.copyWith((message) => updates(message as MapTrail)) as MapTrail;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static MapTrail create() => MapTrail._();
  @$core.override
  MapTrail createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static MapTrail getDefault() =>
      _defaultInstance ??= $pb.GeneratedMessage.$_defaultFor<MapTrail>(create);
  static MapTrail? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get unitId => $_getSZ(0);
  @$pb.TagNumber(1)
  set unitId($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasUnitId() => $_has(0);
  @$pb.TagNumber(1)
  void clearUnitId() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get name => $_getSZ(1);
  @$pb.TagNumber(2)
  set name($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasName() => $_has(1);
  @$pb.TagNumber(2)
  void clearName() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.String get connectionId => $_getSZ(2);
  @$pb.TagNumber(3)
  set connectionId($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasConnectionId() => $_has(2);
  @$pb.TagNumber(3)
  void clearConnectionId() => $_clearField(3);

  @$pb.TagNumber(4)
  $pb.PbList<GeoPoint> get points => $_getList(3);

  @$pb.TagNumber(5)
  Availability get state => $_getN(4);
  @$pb.TagNumber(5)
  set state(Availability value) => $_setField(5, value);
  @$pb.TagNumber(5)
  $core.bool hasState() => $_has(4);
  @$pb.TagNumber(5)
  void clearState() => $_clearField(5);

  @$pb.TagNumber(6)
  $core.bool get selected => $_getBF(5);
  @$pb.TagNumber(6)
  set selected($core.bool value) => $_setBool(5, value);
  @$pb.TagNumber(6)
  $core.bool hasSelected() => $_has(5);
  @$pb.TagNumber(6)
  void clearSelected() => $_clearField(6);
}

class MapGeometry extends $pb.GeneratedMessage {
  factory MapGeometry({
    $core.String? id,
    $core.String? name,
    $core.String? kind,
    $core.bool? closed,
    $core.Iterable<GeoPoint>? points,
    $core.Iterable<GeoRing>? rings,
    $core.String? policyConstraint,
    $core.String? policyKind,
    $core.bool? highlighted,
  }) {
    final result = create();
    if (id != null) result.id = id;
    if (name != null) result.name = name;
    if (kind != null) result.kind = kind;
    if (closed != null) result.closed = closed;
    if (points != null) result.points.addAll(points);
    if (rings != null) result.rings.addAll(rings);
    if (policyConstraint != null) result.policyConstraint = policyConstraint;
    if (policyKind != null) result.policyKind = policyKind;
    if (highlighted != null) result.highlighted = highlighted;
    return result;
  }

  MapGeometry._();

  factory MapGeometry.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory MapGeometry.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'MapGeometry',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'id')
    ..aOS(2, _omitFieldNames ? '' : 'name')
    ..aOS(3, _omitFieldNames ? '' : 'kind')
    ..aOB(4, _omitFieldNames ? '' : 'closed')
    ..pPM<GeoPoint>(5, _omitFieldNames ? '' : 'points',
        subBuilder: GeoPoint.create)
    ..pPM<GeoRing>(6, _omitFieldNames ? '' : 'rings',
        subBuilder: GeoRing.create)
    ..aOS(7, _omitFieldNames ? '' : 'policyConstraint')
    ..aOS(8, _omitFieldNames ? '' : 'policyKind')
    ..aOB(9, _omitFieldNames ? '' : 'highlighted')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapGeometry clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapGeometry copyWith(void Function(MapGeometry) updates) =>
      super.copyWith((message) => updates(message as MapGeometry))
          as MapGeometry;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static MapGeometry create() => MapGeometry._();
  @$core.override
  MapGeometry createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static MapGeometry getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<MapGeometry>(create);
  static MapGeometry? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get id => $_getSZ(0);
  @$pb.TagNumber(1)
  set id($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasId() => $_has(0);
  @$pb.TagNumber(1)
  void clearId() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.String get name => $_getSZ(1);
  @$pb.TagNumber(2)
  set name($core.String value) => $_setString(1, value);
  @$pb.TagNumber(2)
  $core.bool hasName() => $_has(1);
  @$pb.TagNumber(2)
  void clearName() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.String get kind => $_getSZ(2);
  @$pb.TagNumber(3)
  set kind($core.String value) => $_setString(2, value);
  @$pb.TagNumber(3)
  $core.bool hasKind() => $_has(2);
  @$pb.TagNumber(3)
  void clearKind() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.bool get closed => $_getBF(3);
  @$pb.TagNumber(4)
  set closed($core.bool value) => $_setBool(3, value);
  @$pb.TagNumber(4)
  $core.bool hasClosed() => $_has(3);
  @$pb.TagNumber(4)
  void clearClosed() => $_clearField(4);

  @$pb.TagNumber(5)
  $pb.PbList<GeoPoint> get points => $_getList(4);

  @$pb.TagNumber(6)
  $pb.PbList<GeoRing> get rings => $_getList(5);

  @$pb.TagNumber(7)
  $core.String get policyConstraint => $_getSZ(6);
  @$pb.TagNumber(7)
  set policyConstraint($core.String value) => $_setString(6, value);
  @$pb.TagNumber(7)
  $core.bool hasPolicyConstraint() => $_has(6);
  @$pb.TagNumber(7)
  void clearPolicyConstraint() => $_clearField(7);

  @$pb.TagNumber(8)
  $core.String get policyKind => $_getSZ(7);
  @$pb.TagNumber(8)
  set policyKind($core.String value) => $_setString(7, value);
  @$pb.TagNumber(8)
  $core.bool hasPolicyKind() => $_has(7);
  @$pb.TagNumber(8)
  void clearPolicyKind() => $_clearField(8);

  @$pb.TagNumber(9)
  $core.bool get highlighted => $_getBF(8);
  @$pb.TagNumber(9)
  set highlighted($core.bool value) => $_setBool(8, value);
  @$pb.TagNumber(9)
  $core.bool hasHighlighted() => $_has(8);
  @$pb.TagNumber(9)
  void clearHighlighted() => $_clearField(9);
}

class GeoRing extends $pb.GeneratedMessage {
  factory GeoRing({
    $core.Iterable<GeoPoint>? points,
  }) {
    final result = create();
    if (points != null) result.points.addAll(points);
    return result;
  }

  GeoRing._();

  factory GeoRing.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory GeoRing.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'GeoRing',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..pPM<GeoPoint>(1, _omitFieldNames ? '' : 'points',
        subBuilder: GeoPoint.create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  GeoRing clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  GeoRing copyWith(void Function(GeoRing) updates) =>
      super.copyWith((message) => updates(message as GeoRing)) as GeoRing;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static GeoRing create() => GeoRing._();
  @$core.override
  GeoRing createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static GeoRing getDefault() =>
      _defaultInstance ??= $pb.GeneratedMessage.$_defaultFor<GeoRing>(create);
  static GeoRing? _defaultInstance;

  @$pb.TagNumber(1)
  $pb.PbList<GeoPoint> get points => $_getList(0);
}

class MapDestination extends $pb.GeneratedMessage {
  factory MapDestination({
    $core.String? unitId,
    $core.double? latitudeDegrees,
    $core.double? longitudeDegrees,
    $core.bool? selected,
    $core.bool? preview,
  }) {
    final result = create();
    if (unitId != null) result.unitId = unitId;
    if (latitudeDegrees != null) result.latitudeDegrees = latitudeDegrees;
    if (longitudeDegrees != null) result.longitudeDegrees = longitudeDegrees;
    if (selected != null) result.selected = selected;
    if (preview != null) result.preview = preview;
    return result;
  }

  MapDestination._();

  factory MapDestination.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory MapDestination.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'MapDestination',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOS(1, _omitFieldNames ? '' : 'unitId')
    ..aD(2, _omitFieldNames ? '' : 'latitudeDegrees')
    ..aD(3, _omitFieldNames ? '' : 'longitudeDegrees')
    ..aOB(4, _omitFieldNames ? '' : 'selected')
    ..aOB(5, _omitFieldNames ? '' : 'preview')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapDestination clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapDestination copyWith(void Function(MapDestination) updates) =>
      super.copyWith((message) => updates(message as MapDestination))
          as MapDestination;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static MapDestination create() => MapDestination._();
  @$core.override
  MapDestination createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static MapDestination getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<MapDestination>(create);
  static MapDestination? _defaultInstance;

  @$pb.TagNumber(1)
  $core.String get unitId => $_getSZ(0);
  @$pb.TagNumber(1)
  set unitId($core.String value) => $_setString(0, value);
  @$pb.TagNumber(1)
  $core.bool hasUnitId() => $_has(0);
  @$pb.TagNumber(1)
  void clearUnitId() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.double get latitudeDegrees => $_getN(1);
  @$pb.TagNumber(2)
  set latitudeDegrees($core.double value) => $_setDouble(1, value);
  @$pb.TagNumber(2)
  $core.bool hasLatitudeDegrees() => $_has(1);
  @$pb.TagNumber(2)
  void clearLatitudeDegrees() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.double get longitudeDegrees => $_getN(2);
  @$pb.TagNumber(3)
  set longitudeDegrees($core.double value) => $_setDouble(2, value);
  @$pb.TagNumber(3)
  $core.bool hasLongitudeDegrees() => $_has(2);
  @$pb.TagNumber(3)
  void clearLongitudeDegrees() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.bool get selected => $_getBF(3);
  @$pb.TagNumber(4)
  set selected($core.bool value) => $_setBool(3, value);
  @$pb.TagNumber(4)
  $core.bool hasSelected() => $_has(3);
  @$pb.TagNumber(4)
  void clearSelected() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.bool get preview => $_getBF(4);
  @$pb.TagNumber(5)
  set preview($core.bool value) => $_setBool(4, value);
  @$pb.TagNumber(5)
  $core.bool hasPreview() => $_has(4);
  @$pb.TagNumber(5)
  void clearPreview() => $_clearField(5);
}

class MapPath extends $pb.GeneratedMessage {
  factory MapPath({
    $core.Iterable<GeoPoint>? points,
    $core.bool? closed,
  }) {
    final result = create();
    if (points != null) result.points.addAll(points);
    if (closed != null) result.closed = closed;
    return result;
  }

  MapPath._();

  factory MapPath.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory MapPath.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'MapPath',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..pPM<GeoPoint>(1, _omitFieldNames ? '' : 'points',
        subBuilder: GeoPoint.create)
    ..aOB(2, _omitFieldNames ? '' : 'closed')
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapPath clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  MapPath copyWith(void Function(MapPath) updates) =>
      super.copyWith((message) => updates(message as MapPath)) as MapPath;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static MapPath create() => MapPath._();
  @$core.override
  MapPath createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static MapPath getDefault() =>
      _defaultInstance ??= $pb.GeneratedMessage.$_defaultFor<MapPath>(create);
  static MapPath? _defaultInstance;

  @$pb.TagNumber(1)
  $pb.PbList<GeoPoint> get points => $_getList(0);

  @$pb.TagNumber(2)
  $core.bool get closed => $_getBF(1);
  @$pb.TagNumber(2)
  set closed($core.bool value) => $_setBool(1, value);
  @$pb.TagNumber(2)
  $core.bool hasClosed() => $_has(1);
  @$pb.TagNumber(2)
  void clearClosed() => $_clearField(2);
}

class OperatorLocation extends $pb.GeneratedMessage {
  factory OperatorLocation({
    $core.bool? shared,
    $core.bool? available,
    $core.double? latitudeDegrees,
    $core.double? longitudeDegrees,
    $core.double? accuracyMetres,
    $1.Timestamp? observedAt,
  }) {
    final result = create();
    if (shared != null) result.shared = shared;
    if (available != null) result.available = available;
    if (latitudeDegrees != null) result.latitudeDegrees = latitudeDegrees;
    if (longitudeDegrees != null) result.longitudeDegrees = longitudeDegrees;
    if (accuracyMetres != null) result.accuracyMetres = accuracyMetres;
    if (observedAt != null) result.observedAt = observedAt;
    return result;
  }

  OperatorLocation._();

  factory OperatorLocation.fromBuffer($core.List<$core.int> data,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromBuffer(data, registry);
  factory OperatorLocation.fromJson($core.String json,
          [$pb.ExtensionRegistry registry = $pb.ExtensionRegistry.EMPTY]) =>
      create()..mergeFromJson(json, registry);

  static final $pb.BuilderInfo _i = $pb.BuilderInfo(
      _omitMessageNames ? '' : 'OperatorLocation',
      package: const $pb.PackageName(
          _omitMessageNames ? '' : 'psycraft.logos.robotcommand.team.v1'),
      createEmptyInstance: create)
    ..aOB(1, _omitFieldNames ? '' : 'shared')
    ..aOB(2, _omitFieldNames ? '' : 'available')
    ..aD(3, _omitFieldNames ? '' : 'latitudeDegrees')
    ..aD(4, _omitFieldNames ? '' : 'longitudeDegrees')
    ..aD(5, _omitFieldNames ? '' : 'accuracyMetres')
    ..aOM<$1.Timestamp>(6, _omitFieldNames ? '' : 'observedAt',
        subBuilder: $1.Timestamp.create)
    ..hasRequiredFields = false;

  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  OperatorLocation clone() => deepCopy();
  @$core.Deprecated('See https://github.com/google/protobuf.dart/issues/998.')
  OperatorLocation copyWith(void Function(OperatorLocation) updates) =>
      super.copyWith((message) => updates(message as OperatorLocation))
          as OperatorLocation;

  @$core.override
  $pb.BuilderInfo get info_ => _i;

  @$core.pragma('dart2js:noInline')
  static OperatorLocation create() => OperatorLocation._();
  @$core.override
  OperatorLocation createEmptyInstance() => create();
  @$core.pragma('dart2js:noInline')
  static OperatorLocation getDefault() => _defaultInstance ??=
      $pb.GeneratedMessage.$_defaultFor<OperatorLocation>(create);
  static OperatorLocation? _defaultInstance;

  @$pb.TagNumber(1)
  $core.bool get shared => $_getBF(0);
  @$pb.TagNumber(1)
  set shared($core.bool value) => $_setBool(0, value);
  @$pb.TagNumber(1)
  $core.bool hasShared() => $_has(0);
  @$pb.TagNumber(1)
  void clearShared() => $_clearField(1);

  @$pb.TagNumber(2)
  $core.bool get available => $_getBF(1);
  @$pb.TagNumber(2)
  set available($core.bool value) => $_setBool(1, value);
  @$pb.TagNumber(2)
  $core.bool hasAvailable() => $_has(1);
  @$pb.TagNumber(2)
  void clearAvailable() => $_clearField(2);

  @$pb.TagNumber(3)
  $core.double get latitudeDegrees => $_getN(2);
  @$pb.TagNumber(3)
  set latitudeDegrees($core.double value) => $_setDouble(2, value);
  @$pb.TagNumber(3)
  $core.bool hasLatitudeDegrees() => $_has(2);
  @$pb.TagNumber(3)
  void clearLatitudeDegrees() => $_clearField(3);

  @$pb.TagNumber(4)
  $core.double get longitudeDegrees => $_getN(3);
  @$pb.TagNumber(4)
  set longitudeDegrees($core.double value) => $_setDouble(3, value);
  @$pb.TagNumber(4)
  $core.bool hasLongitudeDegrees() => $_has(3);
  @$pb.TagNumber(4)
  void clearLongitudeDegrees() => $_clearField(4);

  @$pb.TagNumber(5)
  $core.double get accuracyMetres => $_getN(4);
  @$pb.TagNumber(5)
  set accuracyMetres($core.double value) => $_setDouble(4, value);
  @$pb.TagNumber(5)
  $core.bool hasAccuracyMetres() => $_has(4);
  @$pb.TagNumber(5)
  void clearAccuracyMetres() => $_clearField(5);

  @$pb.TagNumber(6)
  $1.Timestamp get observedAt => $_getN(5);
  @$pb.TagNumber(6)
  set observedAt($1.Timestamp value) => $_setField(6, value);
  @$pb.TagNumber(6)
  $core.bool hasObservedAt() => $_has(5);
  @$pb.TagNumber(6)
  void clearObservedAt() => $_clearField(6);
  @$pb.TagNumber(6)
  $1.Timestamp ensureObservedAt() => $_ensure(5);
}

const $core.bool _omitFieldNames =
    $core.bool.fromEnvironment('protobuf.omit_field_names');
const $core.bool _omitMessageNames =
    $core.bool.fromEnvironment('protobuf.omit_message_names');
