// This is a generated file - do not edit.
//
// Generated from team/v1/team.proto.

// @dart = 3.3

// ignore_for_file: annotate_overrides, camel_case_types, comment_references
// ignore_for_file: constant_identifier_names
// ignore_for_file: curly_braces_in_flow_control_structures
// ignore_for_file: deprecated_member_use_from_same_package, library_prefixes
// ignore_for_file: non_constant_identifier_names, prefer_relative_imports

import 'dart:async' as $async;
import 'dart:core' as $core;

import 'package:grpc/service_api.dart' as $grpc;
import 'package:protobuf/protobuf.dart' as $pb;

import 'team.pb.dart' as $0;

export 'team.pb.dart';

@$pb.GrpcServiceName('psycraft.logos.robotcommand.team.v1.ServerInfoService')
class ServerInfoServiceClient extends $grpc.Client {
  /// The hostname for this service.
  static const $core.String defaultHost = '';

  /// OAuth scopes needed for the client.
  static const $core.List<$core.String> oauthScopes = [
    '',
  ];

  ServerInfoServiceClient(super.channel, {super.options, super.interceptors});

  $grpc.ResponseFuture<$0.ServerInfoResponse> getServerInfo(
    $0.ServerInfoRequest request, {
    $grpc.CallOptions? options,
  }) {
    return $createUnaryCall(_$getServerInfo, request, options: options);
  }

  // method descriptors

  static final _$getServerInfo = $grpc.ClientMethod<$0.ServerInfoRequest,
          $0.ServerInfoResponse>(
      '/psycraft.logos.robotcommand.team.v1.ServerInfoService/GetServerInfo',
      ($0.ServerInfoRequest value) => value.writeToBuffer(),
      $0.ServerInfoResponse.fromBuffer);
}

@$pb.GrpcServiceName('psycraft.logos.robotcommand.team.v1.ServerInfoService')
abstract class ServerInfoServiceBase extends $grpc.Service {
  $core.String get $name =>
      'psycraft.logos.robotcommand.team.v1.ServerInfoService';

  ServerInfoServiceBase() {
    $addMethod($grpc.ServiceMethod<$0.ServerInfoRequest, $0.ServerInfoResponse>(
        'GetServerInfo',
        getServerInfo_Pre,
        false,
        false,
        ($core.List<$core.int> value) => $0.ServerInfoRequest.fromBuffer(value),
        ($0.ServerInfoResponse value) => value.writeToBuffer()));
  }

  $async.Future<$0.ServerInfoResponse> getServerInfo_Pre(
      $grpc.ServiceCall $call,
      $async.Future<$0.ServerInfoRequest> $request) async {
    return getServerInfo($call, await $request);
  }

  $async.Future<$0.ServerInfoResponse> getServerInfo(
      $grpc.ServiceCall call, $0.ServerInfoRequest request);
}

@$pb.GrpcServiceName('psycraft.logos.robotcommand.team.v1.AccessService')
class AccessServiceClient extends $grpc.Client {
  /// The hostname for this service.
  static const $core.String defaultHost = '';

  /// OAuth scopes needed for the client.
  static const $core.List<$core.String> oauthScopes = [
    '',
  ];

  AccessServiceClient(super.channel, {super.options, super.interceptors});

  $grpc.ResponseStream<$0.AccessStatus> requestAccess(
    $0.AccessRequest request, {
    $grpc.CallOptions? options,
  }) {
    return $createStreamingCall(
        _$requestAccess, $async.Stream.fromIterable([request]),
        options: options);
  }

  // method descriptors

  static final _$requestAccess =
      $grpc.ClientMethod<$0.AccessRequest, $0.AccessStatus>(
          '/psycraft.logos.robotcommand.team.v1.AccessService/RequestAccess',
          ($0.AccessRequest value) => value.writeToBuffer(),
          $0.AccessStatus.fromBuffer);
}

@$pb.GrpcServiceName('psycraft.logos.robotcommand.team.v1.AccessService')
abstract class AccessServiceBase extends $grpc.Service {
  $core.String get $name => 'psycraft.logos.robotcommand.team.v1.AccessService';

  AccessServiceBase() {
    $addMethod($grpc.ServiceMethod<$0.AccessRequest, $0.AccessStatus>(
        'RequestAccess',
        requestAccess_Pre,
        false,
        true,
        ($core.List<$core.int> value) => $0.AccessRequest.fromBuffer(value),
        ($0.AccessStatus value) => value.writeToBuffer()));
  }

  $async.Stream<$0.AccessStatus> requestAccess_Pre($grpc.ServiceCall $call,
      $async.Future<$0.AccessRequest> $request) async* {
    yield* requestAccess($call, await $request);
  }

  $async.Stream<$0.AccessStatus> requestAccess(
      $grpc.ServiceCall call, $0.AccessRequest request);
}

@$pb.GrpcServiceName('psycraft.logos.robotcommand.team.v1.ObserverService')
class ObserverServiceClient extends $grpc.Client {
  /// The hostname for this service.
  static const $core.String defaultHost = '';

  /// OAuth scopes needed for the client.
  static const $core.List<$core.String> oauthScopes = [
    '',
  ];

  ObserverServiceClient(super.channel, {super.options, super.interceptors});

  $grpc.ResponseFuture<$0.RobotCommandSnapshot> getSnapshot(
    $0.GetSnapshotRequest request, {
    $grpc.CallOptions? options,
  }) {
    return $createUnaryCall(_$getSnapshot, request, options: options);
  }

  $grpc.ResponseStream<$0.SnapshotEnvelope> watchSnapshots(
    $0.WatchSnapshotsRequest request, {
    $grpc.CallOptions? options,
  }) {
    return $createStreamingCall(
        _$watchSnapshots, $async.Stream.fromIterable([request]),
        options: options);
  }

  // method descriptors

  static final _$getSnapshot =
      $grpc.ClientMethod<$0.GetSnapshotRequest, $0.RobotCommandSnapshot>(
          '/psycraft.logos.robotcommand.team.v1.ObserverService/GetSnapshot',
          ($0.GetSnapshotRequest value) => value.writeToBuffer(),
          $0.RobotCommandSnapshot.fromBuffer);
  static final _$watchSnapshots =
      $grpc.ClientMethod<$0.WatchSnapshotsRequest, $0.SnapshotEnvelope>(
          '/psycraft.logos.robotcommand.team.v1.ObserverService/WatchSnapshots',
          ($0.WatchSnapshotsRequest value) => value.writeToBuffer(),
          $0.SnapshotEnvelope.fromBuffer);
}

@$pb.GrpcServiceName('psycraft.logos.robotcommand.team.v1.ObserverService')
abstract class ObserverServiceBase extends $grpc.Service {
  $core.String get $name =>
      'psycraft.logos.robotcommand.team.v1.ObserverService';

  ObserverServiceBase() {
    $addMethod(
        $grpc.ServiceMethod<$0.GetSnapshotRequest, $0.RobotCommandSnapshot>(
            'GetSnapshot',
            getSnapshot_Pre,
            false,
            false,
            ($core.List<$core.int> value) =>
                $0.GetSnapshotRequest.fromBuffer(value),
            ($0.RobotCommandSnapshot value) => value.writeToBuffer()));
    $addMethod(
        $grpc.ServiceMethod<$0.WatchSnapshotsRequest, $0.SnapshotEnvelope>(
            'WatchSnapshots',
            watchSnapshots_Pre,
            false,
            true,
            ($core.List<$core.int> value) =>
                $0.WatchSnapshotsRequest.fromBuffer(value),
            ($0.SnapshotEnvelope value) => value.writeToBuffer()));
  }

  $async.Future<$0.RobotCommandSnapshot> getSnapshot_Pre(
      $grpc.ServiceCall $call,
      $async.Future<$0.GetSnapshotRequest> $request) async {
    return getSnapshot($call, await $request);
  }

  $async.Future<$0.RobotCommandSnapshot> getSnapshot(
      $grpc.ServiceCall call, $0.GetSnapshotRequest request);

  $async.Stream<$0.SnapshotEnvelope> watchSnapshots_Pre($grpc.ServiceCall $call,
      $async.Future<$0.WatchSnapshotsRequest> $request) async* {
    yield* watchSnapshots($call, await $request);
  }

  $async.Stream<$0.SnapshotEnvelope> watchSnapshots(
      $grpc.ServiceCall call, $0.WatchSnapshotsRequest request);
}
