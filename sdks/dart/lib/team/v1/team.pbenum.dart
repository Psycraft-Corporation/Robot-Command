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

import 'package:protobuf/protobuf.dart' as $pb;

class AccessState extends $pb.ProtobufEnum {
  static const AccessState ACCESS_STATE_UNSPECIFIED =
      AccessState._(0, _omitEnumNames ? '' : 'ACCESS_STATE_UNSPECIFIED');
  static const AccessState ACCESS_STATE_PENDING =
      AccessState._(1, _omitEnumNames ? '' : 'ACCESS_STATE_PENDING');
  static const AccessState ACCESS_STATE_APPROVED =
      AccessState._(2, _omitEnumNames ? '' : 'ACCESS_STATE_APPROVED');
  static const AccessState ACCESS_STATE_REJECTED =
      AccessState._(3, _omitEnumNames ? '' : 'ACCESS_STATE_REJECTED');
  static const AccessState ACCESS_STATE_EXPIRED =
      AccessState._(4, _omitEnumNames ? '' : 'ACCESS_STATE_EXPIRED');
  static const AccessState ACCESS_STATE_INCOMPATIBLE =
      AccessState._(5, _omitEnumNames ? '' : 'ACCESS_STATE_INCOMPATIBLE');

  static const $core.List<AccessState> values = <AccessState>[
    ACCESS_STATE_UNSPECIFIED,
    ACCESS_STATE_PENDING,
    ACCESS_STATE_APPROVED,
    ACCESS_STATE_REJECTED,
    ACCESS_STATE_EXPIRED,
    ACCESS_STATE_INCOMPATIBLE,
  ];

  static final $core.List<AccessState?> _byValue =
      $pb.ProtobufEnum.$_initByValueList(values, 5);
  static AccessState? valueOf($core.int value) =>
      value < 0 || value >= _byValue.length ? null : _byValue[value];

  const AccessState._(super.value, super.name);
}

class ObserverDisconnectReason extends $pb.ProtobufEnum {
  static const ObserverDisconnectReason OBSERVER_DISCONNECT_REASON_UNSPECIFIED =
      ObserverDisconnectReason._(
          0, _omitEnumNames ? '' : 'OBSERVER_DISCONNECT_REASON_UNSPECIFIED');
  static const ObserverDisconnectReason
      OBSERVER_DISCONNECT_REASON_OPERATOR_DISCONNECTED =
      ObserverDisconnectReason._(
          1,
          _omitEnumNames
              ? ''
              : 'OBSERVER_DISCONNECT_REASON_OPERATOR_DISCONNECTED');
  static const ObserverDisconnectReason
      OBSERVER_DISCONNECT_REASON_AUTHENTICATION_REQUIRED =
      ObserverDisconnectReason._(
          2,
          _omitEnumNames
              ? ''
              : 'OBSERVER_DISCONNECT_REASON_AUTHENTICATION_REQUIRED');
  static const ObserverDisconnectReason
      OBSERVER_DISCONNECT_REASON_SERVER_STOPPED = ObserverDisconnectReason._(
          3, _omitEnumNames ? '' : 'OBSERVER_DISCONNECT_REASON_SERVER_STOPPED');

  static const $core.List<ObserverDisconnectReason> values =
      <ObserverDisconnectReason>[
    OBSERVER_DISCONNECT_REASON_UNSPECIFIED,
    OBSERVER_DISCONNECT_REASON_OPERATOR_DISCONNECTED,
    OBSERVER_DISCONNECT_REASON_AUTHENTICATION_REQUIRED,
    OBSERVER_DISCONNECT_REASON_SERVER_STOPPED,
  ];

  static final $core.List<ObserverDisconnectReason?> _byValue =
      $pb.ProtobufEnum.$_initByValueList(values, 3);
  static ObserverDisconnectReason? valueOf($core.int value) =>
      value < 0 || value >= _byValue.length ? null : _byValue[value];

  const ObserverDisconnectReason._(super.value, super.name);
}

class Availability extends $pb.ProtobufEnum {
  static const Availability AVAILABILITY_UNSPECIFIED =
      Availability._(0, _omitEnumNames ? '' : 'AVAILABILITY_UNSPECIFIED');
  static const Availability AVAILABILITY_UNKNOWN =
      Availability._(1, _omitEnumNames ? '' : 'AVAILABILITY_UNKNOWN');
  static const Availability AVAILABILITY_CONNECTING =
      Availability._(2, _omitEnumNames ? '' : 'AVAILABILITY_CONNECTING');
  static const Availability AVAILABILITY_RECONNECTING =
      Availability._(3, _omitEnumNames ? '' : 'AVAILABILITY_RECONNECTING');
  static const Availability AVAILABILITY_ONLINE =
      Availability._(4, _omitEnumNames ? '' : 'AVAILABILITY_ONLINE');
  static const Availability AVAILABILITY_DEGRADED =
      Availability._(5, _omitEnumNames ? '' : 'AVAILABILITY_DEGRADED');
  static const Availability AVAILABILITY_STALE =
      Availability._(6, _omitEnumNames ? '' : 'AVAILABILITY_STALE');
  static const Availability AVAILABILITY_OFFLINE =
      Availability._(7, _omitEnumNames ? '' : 'AVAILABILITY_OFFLINE');
  static const Availability AVAILABILITY_FAULTED =
      Availability._(8, _omitEnumNames ? '' : 'AVAILABILITY_FAULTED');

  static const $core.List<Availability> values = <Availability>[
    AVAILABILITY_UNSPECIFIED,
    AVAILABILITY_UNKNOWN,
    AVAILABILITY_CONNECTING,
    AVAILABILITY_RECONNECTING,
    AVAILABILITY_ONLINE,
    AVAILABILITY_DEGRADED,
    AVAILABILITY_STALE,
    AVAILABILITY_OFFLINE,
    AVAILABILITY_FAULTED,
  ];

  static final $core.List<Availability?> _byValue =
      $pb.ProtobufEnum.$_initByValueList(values, 8);
  static Availability? valueOf($core.int value) =>
      value < 0 || value >= _byValue.length ? null : _byValue[value];

  const Availability._(super.value, super.name);
}

class DiagnosticStatus extends $pb.ProtobufEnum {
  static const DiagnosticStatus DIAGNOSTIC_STATUS_UNSPECIFIED =
      DiagnosticStatus._(
          0, _omitEnumNames ? '' : 'DIAGNOSTIC_STATUS_UNSPECIFIED');
  static const DiagnosticStatus DIAGNOSTIC_STATUS_UNKNOWN =
      DiagnosticStatus._(1, _omitEnumNames ? '' : 'DIAGNOSTIC_STATUS_UNKNOWN');
  static const DiagnosticStatus DIAGNOSTIC_STATUS_READY =
      DiagnosticStatus._(2, _omitEnumNames ? '' : 'DIAGNOSTIC_STATUS_READY');
  static const DiagnosticStatus DIAGNOSTIC_STATUS_LIMITED =
      DiagnosticStatus._(3, _omitEnumNames ? '' : 'DIAGNOSTIC_STATUS_LIMITED');
  static const DiagnosticStatus DIAGNOSTIC_STATUS_BLOCKED =
      DiagnosticStatus._(4, _omitEnumNames ? '' : 'DIAGNOSTIC_STATUS_BLOCKED');
  static const DiagnosticStatus DIAGNOSTIC_STATUS_STALE =
      DiagnosticStatus._(5, _omitEnumNames ? '' : 'DIAGNOSTIC_STATUS_STALE');
  static const DiagnosticStatus DIAGNOSTIC_STATUS_OFFLINE =
      DiagnosticStatus._(6, _omitEnumNames ? '' : 'DIAGNOSTIC_STATUS_OFFLINE');

  static const $core.List<DiagnosticStatus> values = <DiagnosticStatus>[
    DIAGNOSTIC_STATUS_UNSPECIFIED,
    DIAGNOSTIC_STATUS_UNKNOWN,
    DIAGNOSTIC_STATUS_READY,
    DIAGNOSTIC_STATUS_LIMITED,
    DIAGNOSTIC_STATUS_BLOCKED,
    DIAGNOSTIC_STATUS_STALE,
    DIAGNOSTIC_STATUS_OFFLINE,
  ];

  static final $core.List<DiagnosticStatus?> _byValue =
      $pb.ProtobufEnum.$_initByValueList(values, 6);
  static DiagnosticStatus? valueOf($core.int value) =>
      value < 0 || value >= _byValue.length ? null : _byValue[value];

  const DiagnosticStatus._(super.value, super.name);
}

class DiagnosticCheckState extends $pb.ProtobufEnum {
  static const DiagnosticCheckState DIAGNOSTIC_CHECK_STATE_UNSPECIFIED =
      DiagnosticCheckState._(
          0, _omitEnumNames ? '' : 'DIAGNOSTIC_CHECK_STATE_UNSPECIFIED');
  static const DiagnosticCheckState DIAGNOSTIC_CHECK_STATE_PASSED =
      DiagnosticCheckState._(
          1, _omitEnumNames ? '' : 'DIAGNOSTIC_CHECK_STATE_PASSED');
  static const DiagnosticCheckState DIAGNOSTIC_CHECK_STATE_WARNING =
      DiagnosticCheckState._(
          2, _omitEnumNames ? '' : 'DIAGNOSTIC_CHECK_STATE_WARNING');
  static const DiagnosticCheckState DIAGNOSTIC_CHECK_STATE_FAILED =
      DiagnosticCheckState._(
          3, _omitEnumNames ? '' : 'DIAGNOSTIC_CHECK_STATE_FAILED');
  static const DiagnosticCheckState DIAGNOSTIC_CHECK_STATE_UNKNOWN =
      DiagnosticCheckState._(
          4, _omitEnumNames ? '' : 'DIAGNOSTIC_CHECK_STATE_UNKNOWN');
  static const DiagnosticCheckState DIAGNOSTIC_CHECK_STATE_NOT_APPLICABLE =
      DiagnosticCheckState._(
          5, _omitEnumNames ? '' : 'DIAGNOSTIC_CHECK_STATE_NOT_APPLICABLE');

  static const $core.List<DiagnosticCheckState> values = <DiagnosticCheckState>[
    DIAGNOSTIC_CHECK_STATE_UNSPECIFIED,
    DIAGNOSTIC_CHECK_STATE_PASSED,
    DIAGNOSTIC_CHECK_STATE_WARNING,
    DIAGNOSTIC_CHECK_STATE_FAILED,
    DIAGNOSTIC_CHECK_STATE_UNKNOWN,
    DIAGNOSTIC_CHECK_STATE_NOT_APPLICABLE,
  ];

  static final $core.List<DiagnosticCheckState?> _byValue =
      $pb.ProtobufEnum.$_initByValueList(values, 5);
  static DiagnosticCheckState? valueOf($core.int value) =>
      value < 0 || value >= _byValue.length ? null : _byValue[value];

  const DiagnosticCheckState._(super.value, super.name);
}

const $core.bool _omitEnumNames =
    $core.bool.fromEnvironment('protobuf.omit_enum_names');
