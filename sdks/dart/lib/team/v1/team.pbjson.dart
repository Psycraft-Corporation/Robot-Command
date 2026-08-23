// This is a generated file - do not edit.
//
// Generated from team/v1/team.proto.

// @dart = 3.3

// ignore_for_file: annotate_overrides, camel_case_types, comment_references
// ignore_for_file: constant_identifier_names
// ignore_for_file: curly_braces_in_flow_control_structures
// ignore_for_file: deprecated_member_use_from_same_package, library_prefixes
// ignore_for_file: non_constant_identifier_names, prefer_relative_imports
// ignore_for_file: unused_import

import 'dart:convert' as $convert;
import 'dart:core' as $core;
import 'dart:typed_data' as $typed_data;

@$core.Deprecated('Use accessStateDescriptor instead')
const AccessState$json = {
  '1': 'AccessState',
  '2': [
    {'1': 'ACCESS_STATE_UNSPECIFIED', '2': 0},
    {'1': 'ACCESS_STATE_PENDING', '2': 1},
    {'1': 'ACCESS_STATE_APPROVED', '2': 2},
    {'1': 'ACCESS_STATE_REJECTED', '2': 3},
    {'1': 'ACCESS_STATE_EXPIRED', '2': 4},
    {'1': 'ACCESS_STATE_INCOMPATIBLE', '2': 5},
  ],
};

/// Descriptor for `AccessState`. Decode as a `google.protobuf.EnumDescriptorProto`.
final $typed_data.Uint8List accessStateDescriptor = $convert.base64Decode(
    'CgtBY2Nlc3NTdGF0ZRIcChhBQ0NFU1NfU1RBVEVfVU5TUEVDSUZJRUQQABIYChRBQ0NFU1NfU1'
    'RBVEVfUEVORElORxABEhkKFUFDQ0VTU19TVEFURV9BUFBST1ZFRBACEhkKFUFDQ0VTU19TVEFU'
    'RV9SRUpFQ1RFRBADEhgKFEFDQ0VTU19TVEFURV9FWFBJUkVEEAQSHQoZQUNDRVNTX1NUQVRFX0'
    'lOQ09NUEFUSUJMRRAF');

@$core.Deprecated('Use observerDisconnectReasonDescriptor instead')
const ObserverDisconnectReason$json = {
  '1': 'ObserverDisconnectReason',
  '2': [
    {'1': 'OBSERVER_DISCONNECT_REASON_UNSPECIFIED', '2': 0},
    {'1': 'OBSERVER_DISCONNECT_REASON_OPERATOR_DISCONNECTED', '2': 1},
    {'1': 'OBSERVER_DISCONNECT_REASON_AUTHENTICATION_REQUIRED', '2': 2},
    {'1': 'OBSERVER_DISCONNECT_REASON_SERVER_STOPPED', '2': 3},
  ],
};

/// Descriptor for `ObserverDisconnectReason`. Decode as a `google.protobuf.EnumDescriptorProto`.
final $typed_data.Uint8List observerDisconnectReasonDescriptor = $convert.base64Decode(
    'ChhPYnNlcnZlckRpc2Nvbm5lY3RSZWFzb24SKgomT0JTRVJWRVJfRElTQ09OTkVDVF9SRUFTT0'
    '5fVU5TUEVDSUZJRUQQABI0CjBPQlNFUlZFUl9ESVNDT05ORUNUX1JFQVNPTl9PUEVSQVRPUl9E'
    'SVNDT05ORUNURUQQARI2CjJPQlNFUlZFUl9ESVNDT05ORUNUX1JFQVNPTl9BVVRIRU5USUNBVE'
    'lPTl9SRVFVSVJFRBACEi0KKU9CU0VSVkVSX0RJU0NPTk5FQ1RfUkVBU09OX1NFUlZFUl9TVE9Q'
    'UEVEEAM=');

@$core.Deprecated('Use availabilityDescriptor instead')
const Availability$json = {
  '1': 'Availability',
  '2': [
    {'1': 'AVAILABILITY_UNSPECIFIED', '2': 0},
    {'1': 'AVAILABILITY_UNKNOWN', '2': 1},
    {'1': 'AVAILABILITY_CONNECTING', '2': 2},
    {'1': 'AVAILABILITY_RECONNECTING', '2': 3},
    {'1': 'AVAILABILITY_ONLINE', '2': 4},
    {'1': 'AVAILABILITY_DEGRADED', '2': 5},
    {'1': 'AVAILABILITY_STALE', '2': 6},
    {'1': 'AVAILABILITY_OFFLINE', '2': 7},
    {'1': 'AVAILABILITY_FAULTED', '2': 8},
  ],
};

/// Descriptor for `Availability`. Decode as a `google.protobuf.EnumDescriptorProto`.
final $typed_data.Uint8List availabilityDescriptor = $convert.base64Decode(
    'CgxBdmFpbGFiaWxpdHkSHAoYQVZBSUxBQklMSVRZX1VOU1BFQ0lGSUVEEAASGAoUQVZBSUxBQk'
    'lMSVRZX1VOS05PV04QARIbChdBVkFJTEFCSUxJVFlfQ09OTkVDVElORxACEh0KGUFWQUlMQUJJ'
    'TElUWV9SRUNPTk5FQ1RJTkcQAxIXChNBVkFJTEFCSUxJVFlfT05MSU5FEAQSGQoVQVZBSUxBQk'
    'lMSVRZX0RFR1JBREVEEAUSFgoSQVZBSUxBQklMSVRZX1NUQUxFEAYSGAoUQVZBSUxBQklMSVRZ'
    'X09GRkxJTkUQBxIYChRBVkFJTEFCSUxJVFlfRkFVTFRFRBAI');

@$core.Deprecated('Use diagnosticStatusDescriptor instead')
const DiagnosticStatus$json = {
  '1': 'DiagnosticStatus',
  '2': [
    {'1': 'DIAGNOSTIC_STATUS_UNSPECIFIED', '2': 0},
    {'1': 'DIAGNOSTIC_STATUS_UNKNOWN', '2': 1},
    {'1': 'DIAGNOSTIC_STATUS_READY', '2': 2},
    {'1': 'DIAGNOSTIC_STATUS_LIMITED', '2': 3},
    {'1': 'DIAGNOSTIC_STATUS_BLOCKED', '2': 4},
    {'1': 'DIAGNOSTIC_STATUS_STALE', '2': 5},
    {'1': 'DIAGNOSTIC_STATUS_OFFLINE', '2': 6},
  ],
};

/// Descriptor for `DiagnosticStatus`. Decode as a `google.protobuf.EnumDescriptorProto`.
final $typed_data.Uint8List diagnosticStatusDescriptor = $convert.base64Decode(
    'ChBEaWFnbm9zdGljU3RhdHVzEiEKHURJQUdOT1NUSUNfU1RBVFVTX1VOU1BFQ0lGSUVEEAASHQ'
    'oZRElBR05PU1RJQ19TVEFUVVNfVU5LTk9XThABEhsKF0RJQUdOT1NUSUNfU1RBVFVTX1JFQURZ'
    'EAISHQoZRElBR05PU1RJQ19TVEFUVVNfTElNSVRFRBADEh0KGURJQUdOT1NUSUNfU1RBVFVTX0'
    'JMT0NLRUQQBBIbChdESUFHTk9TVElDX1NUQVRVU19TVEFMRRAFEh0KGURJQUdOT1NUSUNfU1RB'
    'VFVTX09GRkxJTkUQBg==');

@$core.Deprecated('Use diagnosticCheckStateDescriptor instead')
const DiagnosticCheckState$json = {
  '1': 'DiagnosticCheckState',
  '2': [
    {'1': 'DIAGNOSTIC_CHECK_STATE_UNSPECIFIED', '2': 0},
    {'1': 'DIAGNOSTIC_CHECK_STATE_PASSED', '2': 1},
    {'1': 'DIAGNOSTIC_CHECK_STATE_WARNING', '2': 2},
    {'1': 'DIAGNOSTIC_CHECK_STATE_FAILED', '2': 3},
    {'1': 'DIAGNOSTIC_CHECK_STATE_UNKNOWN', '2': 4},
    {'1': 'DIAGNOSTIC_CHECK_STATE_NOT_APPLICABLE', '2': 5},
  ],
};

/// Descriptor for `DiagnosticCheckState`. Decode as a `google.protobuf.EnumDescriptorProto`.
final $typed_data.Uint8List diagnosticCheckStateDescriptor = $convert.base64Decode(
    'ChREaWFnbm9zdGljQ2hlY2tTdGF0ZRImCiJESUFHTk9TVElDX0NIRUNLX1NUQVRFX1VOU1BFQ0'
    'lGSUVEEAASIQodRElBR05PU1RJQ19DSEVDS19TVEFURV9QQVNTRUQQARIiCh5ESUFHTk9TVElD'
    'X0NIRUNLX1NUQVRFX1dBUk5JTkcQAhIhCh1ESUFHTk9TVElDX0NIRUNLX1NUQVRFX0ZBSUxFRB'
    'ADEiIKHkRJQUdOT1NUSUNfQ0hFQ0tfU1RBVEVfVU5LTk9XThAEEikKJURJQUdOT1NUSUNfQ0hF'
    'Q0tfU1RBVEVfTk9UX0FQUExJQ0FCTEUQBQ==');

@$core.Deprecated('Use serverInfoRequestDescriptor instead')
const ServerInfoRequest$json = {
  '1': 'ServerInfoRequest',
};

/// Descriptor for `ServerInfoRequest`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List serverInfoRequestDescriptor =
    $convert.base64Decode('ChFTZXJ2ZXJJbmZvUmVxdWVzdA==');

@$core.Deprecated('Use serverInfoResponseDescriptor instead')
const ServerInfoResponse$json = {
  '1': 'ServerInfoResponse',
  '2': [
    {'1': 'instance_id', '3': 1, '4': 1, '5': 9, '10': 'instanceId'},
    {'1': 'display_name', '3': 2, '4': 1, '5': 9, '10': 'displayName'},
    {'1': 'api_version', '3': 3, '4': 1, '5': 9, '10': 'apiVersion'},
    {'1': 'capabilities', '3': 4, '4': 3, '5': 9, '10': 'capabilities'},
    {
      '1': 'server_time',
      '3': 5,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'serverTime'
    },
    {
      '1': 'tls_sha256_fingerprint',
      '3': 6,
      '4': 1,
      '5': 9,
      '10': 'tlsSha256Fingerprint'
    },
    {
      '1': 'minimum_sdk_version',
      '3': 7,
      '4': 1,
      '5': 9,
      '10': 'minimumSdkVersion'
    },
    {
      '1': 'requires_passphrase',
      '3': 8,
      '4': 1,
      '5': 8,
      '10': 'requiresPassphrase'
    },
  ],
};

/// Descriptor for `ServerInfoResponse`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List serverInfoResponseDescriptor = $convert.base64Decode(
    'ChJTZXJ2ZXJJbmZvUmVzcG9uc2USHwoLaW5zdGFuY2VfaWQYASABKAlSCmluc3RhbmNlSWQSIQ'
    'oMZGlzcGxheV9uYW1lGAIgASgJUgtkaXNwbGF5TmFtZRIfCgthcGlfdmVyc2lvbhgDIAEoCVIK'
    'YXBpVmVyc2lvbhIiCgxjYXBhYmlsaXRpZXMYBCADKAlSDGNhcGFiaWxpdGllcxI7CgtzZXJ2ZX'
    'JfdGltZRgFIAEoCzIaLmdvb2dsZS5wcm90b2J1Zi5UaW1lc3RhbXBSCnNlcnZlclRpbWUSNAoW'
    'dGxzX3NoYTI1Nl9maW5nZXJwcmludBgGIAEoCVIUdGxzU2hhMjU2RmluZ2VycHJpbnQSLgoTbW'
    'luaW11bV9zZGtfdmVyc2lvbhgHIAEoCVIRbWluaW11bVNka1ZlcnNpb24SLwoTcmVxdWlyZXNf'
    'cGFzc3BocmFzZRgIIAEoCFIScmVxdWlyZXNQYXNzcGhyYXNl');

@$core.Deprecated('Use accessRequestDescriptor instead')
const AccessRequest$json = {
  '1': 'AccessRequest',
  '2': [
    {'1': 'display_name', '3': 1, '4': 1, '5': 9, '10': 'displayName'},
    {'1': 'application_name', '3': 2, '4': 1, '5': 9, '10': 'applicationName'},
    {
      '1': 'application_version',
      '3': 3,
      '4': 1,
      '5': 9,
      '10': 'applicationVersion'
    },
    {'1': 'sdk_version', '3': 4, '4': 1, '5': 9, '10': 'sdkVersion'},
    {'1': 'api_version', '3': 5, '4': 1, '5': 9, '10': 'apiVersion'},
    {
      '1': 'client_instance_id',
      '3': 6,
      '4': 1,
      '5': 9,
      '10': 'clientInstanceId'
    },
    {'1': 'request_nonce', '3': 7, '4': 1, '5': 9, '10': 'requestNonce'},
    {'1': 'pairing_id', '3': 8, '4': 1, '5': 9, '10': 'pairingId'},
    {'1': 'pairing_code', '3': 9, '4': 1, '5': 9, '10': 'pairingCode'},
    {'1': 'pairing_phrase', '3': 10, '4': 1, '5': 9, '10': 'pairingPhrase'},
  ],
};

/// Descriptor for `AccessRequest`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List accessRequestDescriptor = $convert.base64Decode(
    'Cg1BY2Nlc3NSZXF1ZXN0EiEKDGRpc3BsYXlfbmFtZRgBIAEoCVILZGlzcGxheU5hbWUSKQoQYX'
    'BwbGljYXRpb25fbmFtZRgCIAEoCVIPYXBwbGljYXRpb25OYW1lEi8KE2FwcGxpY2F0aW9uX3Zl'
    'cnNpb24YAyABKAlSEmFwcGxpY2F0aW9uVmVyc2lvbhIfCgtzZGtfdmVyc2lvbhgEIAEoCVIKc2'
    'RrVmVyc2lvbhIfCgthcGlfdmVyc2lvbhgFIAEoCVIKYXBpVmVyc2lvbhIsChJjbGllbnRfaW5z'
    'dGFuY2VfaWQYBiABKAlSEGNsaWVudEluc3RhbmNlSWQSIwoNcmVxdWVzdF9ub25jZRgHIAEoCV'
    'IMcmVxdWVzdE5vbmNlEh0KCnBhaXJpbmdfaWQYCCABKAlSCXBhaXJpbmdJZBIhCgxwYWlyaW5n'
    'X2NvZGUYCSABKAlSC3BhaXJpbmdDb2RlEiUKDnBhaXJpbmdfcGhyYXNlGAogASgJUg1wYWlyaW'
    '5nUGhyYXNl');

@$core.Deprecated('Use accessStatusDescriptor instead')
const AccessStatus$json = {
  '1': 'AccessStatus',
  '2': [
    {'1': 'request_id', '3': 1, '4': 1, '5': 9, '10': 'requestId'},
    {
      '1': 'state',
      '3': 2,
      '4': 1,
      '5': 14,
      '6': '.psycraft.logos.robotcommand.team.v1.AccessState',
      '10': 'state'
    },
    {'1': 'message', '3': 3, '4': 1, '5': 9, '10': 'message'},
    {
      '1': 'expires_at',
      '3': 4,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'expiresAt'
    },
    {'1': 'session_token', '3': 5, '4': 1, '5': 9, '10': 'sessionToken'},
  ],
};

/// Descriptor for `AccessStatus`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List accessStatusDescriptor = $convert.base64Decode(
    'CgxBY2Nlc3NTdGF0dXMSHQoKcmVxdWVzdF9pZBgBIAEoCVIJcmVxdWVzdElkEkYKBXN0YXRlGA'
    'IgASgOMjAucHN5Y3JhZnQubG9nb3MuZmllbGRjb25zb2xlLnRlYW0udjEuQWNjZXNzU3RhdGVS'
    'BXN0YXRlEhgKB21lc3NhZ2UYAyABKAlSB21lc3NhZ2USOQoKZXhwaXJlc19hdBgEIAEoCzIaLm'
    'dvb2dsZS5wcm90b2J1Zi5UaW1lc3RhbXBSCWV4cGlyZXNBdBIjCg1zZXNzaW9uX3Rva2VuGAUg'
    'ASgJUgxzZXNzaW9uVG9rZW4=');

@$core.Deprecated('Use getSnapshotRequestDescriptor instead')
const GetSnapshotRequest$json = {
  '1': 'GetSnapshotRequest',
  '2': [
    {'1': 'known_revision', '3': 1, '4': 1, '5': 4, '10': 'knownRevision'},
  ],
};

/// Descriptor for `GetSnapshotRequest`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List getSnapshotRequestDescriptor = $convert.base64Decode(
    'ChJHZXRTbmFwc2hvdFJlcXVlc3QSJQoOa25vd25fcmV2aXNpb24YASABKARSDWtub3duUmV2aX'
    'Npb24=');

@$core.Deprecated('Use watchSnapshotsRequestDescriptor instead')
const WatchSnapshotsRequest$json = {
  '1': 'WatchSnapshotsRequest',
  '2': [
    {'1': 'after_revision', '3': 1, '4': 1, '5': 4, '10': 'afterRevision'},
  ],
};

/// Descriptor for `WatchSnapshotsRequest`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List watchSnapshotsRequestDescriptor = $convert.base64Decode(
    'ChVXYXRjaFNuYXBzaG90c1JlcXVlc3QSJQoOYWZ0ZXJfcmV2aXNpb24YASABKARSDWFmdGVyUm'
    'V2aXNpb24=');

@$core.Deprecated('Use snapshotEnvelopeDescriptor instead')
const SnapshotEnvelope$json = {
  '1': 'SnapshotEnvelope',
  '2': [
    {
      '1': 'snapshot',
      '3': 1,
      '4': 1,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.RobotCommandSnapshot',
      '9': 0,
      '10': 'snapshot'
    },
    {
      '1': 'heartbeat',
      '3': 2,
      '4': 1,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.SnapshotHeartbeat',
      '9': 0,
      '10': 'heartbeat'
    },
    {
      '1': 'disconnect',
      '3': 3,
      '4': 1,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.ObserverDisconnectNotice',
      '9': 0,
      '10': 'disconnect'
    },
  ],
  '8': [
    {'1': 'payload'},
  ],
};

/// Descriptor for `SnapshotEnvelope`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List snapshotEnvelopeDescriptor = $convert.base64Decode(
    'ChBTbmFwc2hvdEVudmVsb3BlElcKCHNuYXBzaG90GAEgASgLMjkucHN5Y3JhZnQubG9nb3MuZm'
    'llbGRjb25zb2xlLnRlYW0udjEuRmllbGRDb25zb2xlU25hcHNob3RIAFIIc25hcHNob3QSVgoJ'
    'aGVhcnRiZWF0GAIgASgLMjYucHN5Y3JhZnQubG9nb3MuZmllbGRjb25zb2xlLnRlYW0udjEuU2'
    '5hcHNob3RIZWFydGJlYXRIAFIJaGVhcnRiZWF0El8KCmRpc2Nvbm5lY3QYAyABKAsyPS5wc3lj'
    'cmFmdC5sb2dvcy5maWVsZGNvbnNvbGUudGVhbS52MS5PYnNlcnZlckRpc2Nvbm5lY3ROb3RpY2'
    'VIAFIKZGlzY29ubmVjdEIJCgdwYXlsb2Fk');

@$core.Deprecated('Use observerDisconnectNoticeDescriptor instead')
const ObserverDisconnectNotice$json = {
  '1': 'ObserverDisconnectNotice',
  '2': [
    {
      '1': 'reason',
      '3': 1,
      '4': 1,
      '5': 14,
      '6': '.psycraft.logos.robotcommand.team.v1.ObserverDisconnectReason',
      '10': 'reason'
    },
    {'1': 'message', '3': 2, '4': 1, '5': 9, '10': 'message'},
    {
      '1': 'occurred_at',
      '3': 3,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'occurredAt'
    },
  ],
};

/// Descriptor for `ObserverDisconnectNotice`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List observerDisconnectNoticeDescriptor = $convert.base64Decode(
    'ChhPYnNlcnZlckRpc2Nvbm5lY3ROb3RpY2USVQoGcmVhc29uGAEgASgOMj0ucHN5Y3JhZnQubG'
    '9nb3MuZmllbGRjb25zb2xlLnRlYW0udjEuT2JzZXJ2ZXJEaXNjb25uZWN0UmVhc29uUgZyZWFz'
    'b24SGAoHbWVzc2FnZRgCIAEoCVIHbWVzc2FnZRI7CgtvY2N1cnJlZF9hdBgDIAEoCzIaLmdvb2'
    'dsZS5wcm90b2J1Zi5UaW1lc3RhbXBSCm9jY3VycmVkQXQ=');

@$core.Deprecated('Use snapshotHeartbeatDescriptor instead')
const SnapshotHeartbeat$json = {
  '1': 'SnapshotHeartbeat',
  '2': [
    {'1': 'current_revision', '3': 1, '4': 1, '5': 4, '10': 'currentRevision'},
    {
      '1': 'server_time',
      '3': 2,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'serverTime'
    },
  ],
};

/// Descriptor for `SnapshotHeartbeat`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List snapshotHeartbeatDescriptor = $convert.base64Decode(
    'ChFTbmFwc2hvdEhlYXJ0YmVhdBIpChBjdXJyZW50X3JldmlzaW9uGAEgASgEUg9jdXJyZW50Um'
    'V2aXNpb24SOwoLc2VydmVyX3RpbWUYAiABKAsyGi5nb29nbGUucHJvdG9idWYuVGltZXN0YW1w'
    'UgpzZXJ2ZXJUaW1l');

@$core.Deprecated('Use robotCommandSnapshotDescriptor instead')
const RobotCommandSnapshot$json = {
  '1': 'RobotCommandSnapshot',
  '2': [
    {'1': 'revision', '3': 1, '4': 1, '5': 4, '10': 'revision'},
    {
      '1': 'captured_at',
      '3': 2,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'capturedAt'
    },
    {
      '1': 'units',
      '3': 3,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.UnitSnapshot',
      '10': 'units'
    },
    {
      '1': 'connections',
      '3': 4,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.ConnectionSnapshot',
      '10': 'connections'
    },
    {
      '1': 'map',
      '3': 5,
      '4': 1,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.MapSnapshot',
      '10': 'map'
    },
  ],
};

/// Descriptor for `RobotCommandSnapshot`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List robotCommandSnapshotDescriptor = $convert.base64Decode(
    'ChRGaWVsZENvbnNvbGVTbmFwc2hvdBIaCghyZXZpc2lvbhgBIAEoBFIIcmV2aXNpb24SOwoLY2'
    'FwdHVyZWRfYXQYAiABKAsyGi5nb29nbGUucHJvdG9idWYuVGltZXN0YW1wUgpjYXB0dXJlZEF0'
    'EkcKBXVuaXRzGAMgAygLMjEucHN5Y3JhZnQubG9nb3MuZmllbGRjb25zb2xlLnRlYW0udjEuVW'
    '5pdFNuYXBzaG90UgV1bml0cxJZCgtjb25uZWN0aW9ucxgEIAMoCzI3LnBzeWNyYWZ0LmxvZ29z'
    'LmZpZWxkY29uc29sZS50ZWFtLnYxLkNvbm5lY3Rpb25TbmFwc2hvdFILY29ubmVjdGlvbnMSQg'
    'oDbWFwGAUgASgLMjAucHN5Y3JhZnQubG9nb3MuZmllbGRjb25zb2xlLnRlYW0udjEuTWFwU25h'
    'cHNob3RSA21hcA==');

@$core.Deprecated('Use unitSnapshotDescriptor instead')
const UnitSnapshot$json = {
  '1': 'UnitSnapshot',
  '2': [
    {'1': 'id', '3': 1, '4': 1, '5': 9, '10': 'id'},
    {'1': 'name', '3': 2, '4': 1, '5': 9, '10': 'name'},
    {'1': 'backend', '3': 3, '4': 1, '5': 9, '10': 'backend'},
    {'1': 'vehicle_class', '3': 4, '4': 1, '5': 9, '10': 'vehicleClass'},
    {'1': 'domain', '3': 5, '4': 1, '5': 9, '10': 'domain'},
    {
      '1': 'state',
      '3': 6,
      '4': 1,
      '5': 14,
      '6': '.psycraft.logos.robotcommand.team.v1.Availability',
      '10': 'state'
    },
    {'1': 'is_ghost', '3': 7, '4': 1, '5': 8, '10': 'isGhost'},
    {'1': 'logos_instance_id', '3': 8, '4': 1, '5': 9, '10': 'logosInstanceId'},
    {'1': 'capabilities', '3': 9, '4': 3, '5': 9, '10': 'capabilities'},
    {'1': 'connection_ids', '3': 10, '4': 3, '5': 9, '10': 'connectionIds'},
    {
      '1': 'telemetry_authority_connection_id',
      '3': 11,
      '4': 1,
      '5': 9,
      '10': 'telemetryAuthorityConnectionId'
    },
    {
      '1': 'diagnostics_authority_connection_id',
      '3': 12,
      '4': 1,
      '5': 9,
      '10': 'diagnosticsAuthorityConnectionId'
    },
    {
      '1': 'telemetry',
      '3': 13,
      '4': 1,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.TelemetrySnapshot',
      '10': 'telemetry'
    },
    {
      '1': 'diagnostics',
      '3': 14,
      '4': 1,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.DiagnosticsSnapshot',
      '10': 'diagnostics'
    },
    {
      '1': 'actions',
      '3': 15,
      '4': 1,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.ActionSnapshot',
      '10': 'actions'
    },
    {
      '1': 'links',
      '3': 16,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.LinkSnapshot',
      '10': 'links'
    },
  ],
};

/// Descriptor for `UnitSnapshot`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List unitSnapshotDescriptor = $convert.base64Decode(
    'CgxVbml0U25hcHNob3QSDgoCaWQYASABKAlSAmlkEhIKBG5hbWUYAiABKAlSBG5hbWUSGAoHYm'
    'Fja2VuZBgDIAEoCVIHYmFja2VuZBIjCg12ZWhpY2xlX2NsYXNzGAQgASgJUgx2ZWhpY2xlQ2xh'
    'c3MSFgoGZG9tYWluGAUgASgJUgZkb21haW4SRwoFc3RhdGUYBiABKA4yMS5wc3ljcmFmdC5sb2'
    'dvcy5maWVsZGNvbnNvbGUudGVhbS52MS5BdmFpbGFiaWxpdHlSBXN0YXRlEhkKCGlzX2dob3N0'
    'GAcgASgIUgdpc0dob3N0EioKEWxvZ29zX2luc3RhbmNlX2lkGAggASgJUg9sb2dvc0luc3Rhbm'
    'NlSWQSIgoMY2FwYWJpbGl0aWVzGAkgAygJUgxjYXBhYmlsaXRpZXMSJQoOY29ubmVjdGlvbl9p'
    'ZHMYCiADKAlSDWNvbm5lY3Rpb25JZHMSSQohdGVsZW1ldHJ5X2F1dGhvcml0eV9jb25uZWN0aW'
    '9uX2lkGAsgASgJUh50ZWxlbWV0cnlBdXRob3JpdHlDb25uZWN0aW9uSWQSTQojZGlhZ25vc3Rp'
    'Y3NfYXV0aG9yaXR5X2Nvbm5lY3Rpb25faWQYDCABKAlSIGRpYWdub3N0aWNzQXV0aG9yaXR5Q2'
    '9ubmVjdGlvbklkElQKCXRlbGVtZXRyeRgNIAEoCzI2LnBzeWNyYWZ0LmxvZ29zLmZpZWxkY29u'
    'c29sZS50ZWFtLnYxLlRlbGVtZXRyeVNuYXBzaG90Ugl0ZWxlbWV0cnkSWgoLZGlhZ25vc3RpY3'
    'MYDiABKAsyOC5wc3ljcmFmdC5sb2dvcy5maWVsZGNvbnNvbGUudGVhbS52MS5EaWFnbm9zdGlj'
    'c1NuYXBzaG90UgtkaWFnbm9zdGljcxJNCgdhY3Rpb25zGA8gASgLMjMucHN5Y3JhZnQubG9nb3'
    'MuZmllbGRjb25zb2xlLnRlYW0udjEuQWN0aW9uU25hcHNob3RSB2FjdGlvbnMSRwoFbGlua3MY'
    'ECADKAsyMS5wc3ljcmFmdC5sb2dvcy5maWVsZGNvbnNvbGUudGVhbS52MS5MaW5rU25hcHNob3'
    'RSBWxpbmtz');

@$core.Deprecated('Use connectionSnapshotDescriptor instead')
const ConnectionSnapshot$json = {
  '1': 'ConnectionSnapshot',
  '2': [
    {'1': 'id', '3': 1, '4': 1, '5': 9, '10': 'id'},
    {'1': 'name', '3': 2, '4': 1, '5': 9, '10': 'name'},
    {'1': 'target', '3': 3, '4': 1, '5': 9, '10': 'target'},
    {'1': 'mode', '3': 4, '4': 1, '5': 9, '10': 'mode'},
    {
      '1': 'state',
      '3': 5,
      '4': 1,
      '5': 14,
      '6': '.psycraft.logos.robotcommand.team.v1.Availability',
      '10': 'state'
    },
    {'1': 'auto_reconnect', '3': 6, '4': 1, '5': 8, '10': 'autoReconnect'},
    {'1': 'logos_instance_id', '3': 7, '4': 1, '5': 9, '10': 'logosInstanceId'},
    {'1': 'runtime_role', '3': 8, '4': 1, '5': 9, '10': 'runtimeRole'},
    {
      '1': 'connected_at',
      '3': 9,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'connectedAt'
    },
    {
      '1': 'last_seen',
      '3': 10,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'lastSeen'
    },
    {'1': 'last_error', '3': 11, '4': 1, '5': 9, '10': 'lastError'},
    {'1': 'is_ghost', '3': 12, '4': 1, '5': 8, '10': 'isGhost'},
  ],
};

/// Descriptor for `ConnectionSnapshot`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List connectionSnapshotDescriptor = $convert.base64Decode(
    'ChJDb25uZWN0aW9uU25hcHNob3QSDgoCaWQYASABKAlSAmlkEhIKBG5hbWUYAiABKAlSBG5hbW'
    'USFgoGdGFyZ2V0GAMgASgJUgZ0YXJnZXQSEgoEbW9kZRgEIAEoCVIEbW9kZRJHCgVzdGF0ZRgF'
    'IAEoDjIxLnBzeWNyYWZ0LmxvZ29zLmZpZWxkY29uc29sZS50ZWFtLnYxLkF2YWlsYWJpbGl0eV'
    'IFc3RhdGUSJQoOYXV0b19yZWNvbm5lY3QYBiABKAhSDWF1dG9SZWNvbm5lY3QSKgoRbG9nb3Nf'
    'aW5zdGFuY2VfaWQYByABKAlSD2xvZ29zSW5zdGFuY2VJZBIhCgxydW50aW1lX3JvbGUYCCABKA'
    'lSC3J1bnRpbWVSb2xlEj0KDGNvbm5lY3RlZF9hdBgJIAEoCzIaLmdvb2dsZS5wcm90b2J1Zi5U'
    'aW1lc3RhbXBSC2Nvbm5lY3RlZEF0EjcKCWxhc3Rfc2VlbhgKIAEoCzIaLmdvb2dsZS5wcm90b2'
    'J1Zi5UaW1lc3RhbXBSCGxhc3RTZWVuEh0KCmxhc3RfZXJyb3IYCyABKAlSCWxhc3RFcnJvchIZ'
    'Cghpc19naG9zdBgMIAEoCFIHaXNHaG9zdA==');

@$core.Deprecated('Use telemetrySnapshotDescriptor instead')
const TelemetrySnapshot$json = {
  '1': 'TelemetrySnapshot',
  '2': [
    {'1': 'reported', '3': 1, '4': 1, '5': 8, '10': 'reported'},
    {'1': 'armed', '3': 2, '4': 1, '5': 8, '10': 'armed'},
    {'1': 'landed_state', '3': 3, '4': 1, '5': 9, '10': 'landedState'},
    {'1': 'mode', '3': 4, '4': 1, '5': 9, '10': 'mode'},
    {
      '1': 'latitude_degrees',
      '3': 5,
      '4': 1,
      '5': 1,
      '9': 0,
      '10': 'latitudeDegrees',
      '17': true
    },
    {
      '1': 'longitude_degrees',
      '3': 6,
      '4': 1,
      '5': 1,
      '9': 1,
      '10': 'longitudeDegrees',
      '17': true
    },
    {
      '1': 'altitude_msl_metres',
      '3': 7,
      '4': 1,
      '5': 1,
      '9': 2,
      '10': 'altitudeMslMetres',
      '17': true
    },
    {
      '1': 'altitude_agl_metres',
      '3': 8,
      '4': 1,
      '5': 1,
      '9': 3,
      '10': 'altitudeAglMetres',
      '17': true
    },
    {
      '1': 'local_north_metres',
      '3': 9,
      '4': 1,
      '5': 1,
      '9': 4,
      '10': 'localNorthMetres',
      '17': true
    },
    {
      '1': 'local_east_metres',
      '3': 10,
      '4': 1,
      '5': 1,
      '9': 5,
      '10': 'localEastMetres',
      '17': true
    },
    {
      '1': 'local_down_metres',
      '3': 11,
      '4': 1,
      '5': 1,
      '9': 6,
      '10': 'localDownMetres',
      '17': true
    },
    {
      '1': 'velocity_north_metres_per_second',
      '3': 12,
      '4': 1,
      '5': 1,
      '9': 7,
      '10': 'velocityNorthMetresPerSecond',
      '17': true
    },
    {
      '1': 'velocity_east_metres_per_second',
      '3': 13,
      '4': 1,
      '5': 1,
      '9': 8,
      '10': 'velocityEastMetresPerSecond',
      '17': true
    },
    {
      '1': 'velocity_down_metres_per_second',
      '3': 14,
      '4': 1,
      '5': 1,
      '9': 9,
      '10': 'velocityDownMetresPerSecond',
      '17': true
    },
    {
      '1': 'heading_degrees',
      '3': 15,
      '4': 1,
      '5': 1,
      '9': 10,
      '10': 'headingDegrees',
      '17': true
    },
    {'1': 'stale', '3': 16, '4': 1, '5': 8, '10': 'stale'},
    {'1': 'code', '3': 17, '4': 1, '5': 9, '10': 'code'},
    {'1': 'message', '3': 18, '4': 1, '5': 9, '10': 'message'},
    {
      '1': 'observed_at',
      '3': 19,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'observedAt'
    },
  ],
  '8': [
    {'1': '_latitude_degrees'},
    {'1': '_longitude_degrees'},
    {'1': '_altitude_msl_metres'},
    {'1': '_altitude_agl_metres'},
    {'1': '_local_north_metres'},
    {'1': '_local_east_metres'},
    {'1': '_local_down_metres'},
    {'1': '_velocity_north_metres_per_second'},
    {'1': '_velocity_east_metres_per_second'},
    {'1': '_velocity_down_metres_per_second'},
    {'1': '_heading_degrees'},
  ],
};

/// Descriptor for `TelemetrySnapshot`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List telemetrySnapshotDescriptor = $convert.base64Decode(
    'ChFUZWxlbWV0cnlTbmFwc2hvdBIaCghyZXBvcnRlZBgBIAEoCFIIcmVwb3J0ZWQSFAoFYXJtZW'
    'QYAiABKAhSBWFybWVkEiEKDGxhbmRlZF9zdGF0ZRgDIAEoCVILbGFuZGVkU3RhdGUSEgoEbW9k'
    'ZRgEIAEoCVIEbW9kZRIuChBsYXRpdHVkZV9kZWdyZWVzGAUgASgBSABSD2xhdGl0dWRlRGVncm'
    'Vlc4gBARIwChFsb25naXR1ZGVfZGVncmVlcxgGIAEoAUgBUhBsb25naXR1ZGVEZWdyZWVziAEB'
    'EjMKE2FsdGl0dWRlX21zbF9tZXRyZXMYByABKAFIAlIRYWx0aXR1ZGVNc2xNZXRyZXOIAQESMw'
    'oTYWx0aXR1ZGVfYWdsX21ldHJlcxgIIAEoAUgDUhFhbHRpdHVkZUFnbE1ldHJlc4gBARIxChJs'
    'b2NhbF9ub3J0aF9tZXRyZXMYCSABKAFIBFIQbG9jYWxOb3J0aE1ldHJlc4gBARIvChFsb2NhbF'
    '9lYXN0X21ldHJlcxgKIAEoAUgFUg9sb2NhbEVhc3RNZXRyZXOIAQESLwoRbG9jYWxfZG93bl9t'
    'ZXRyZXMYCyABKAFIBlIPbG9jYWxEb3duTWV0cmVziAEBEksKIHZlbG9jaXR5X25vcnRoX21ldH'
    'Jlc19wZXJfc2Vjb25kGAwgASgBSAdSHHZlbG9jaXR5Tm9ydGhNZXRyZXNQZXJTZWNvbmSIAQES'
    'SQofdmVsb2NpdHlfZWFzdF9tZXRyZXNfcGVyX3NlY29uZBgNIAEoAUgIUht2ZWxvY2l0eUVhc3'
    'RNZXRyZXNQZXJTZWNvbmSIAQESSQofdmVsb2NpdHlfZG93bl9tZXRyZXNfcGVyX3NlY29uZBgO'
    'IAEoAUgJUht2ZWxvY2l0eURvd25NZXRyZXNQZXJTZWNvbmSIAQESLAoPaGVhZGluZ19kZWdyZW'
    'VzGA8gASgBSApSDmhlYWRpbmdEZWdyZWVziAEBEhQKBXN0YWxlGBAgASgIUgVzdGFsZRISCgRj'
    'b2RlGBEgASgJUgRjb2RlEhgKB21lc3NhZ2UYEiABKAlSB21lc3NhZ2USOwoLb2JzZXJ2ZWRfYX'
    'QYEyABKAsyGi5nb29nbGUucHJvdG9idWYuVGltZXN0YW1wUgpvYnNlcnZlZEF0QhMKEV9sYXRp'
    'dHVkZV9kZWdyZWVzQhQKEl9sb25naXR1ZGVfZGVncmVlc0IWChRfYWx0aXR1ZGVfbXNsX21ldH'
    'Jlc0IWChRfYWx0aXR1ZGVfYWdsX21ldHJlc0IVChNfbG9jYWxfbm9ydGhfbWV0cmVzQhQKEl9s'
    'b2NhbF9lYXN0X21ldHJlc0IUChJfbG9jYWxfZG93bl9tZXRyZXNCIwohX3ZlbG9jaXR5X25vcn'
    'RoX21ldHJlc19wZXJfc2Vjb25kQiIKIF92ZWxvY2l0eV9lYXN0X21ldHJlc19wZXJfc2Vjb25k'
    'QiIKIF92ZWxvY2l0eV9kb3duX21ldHJlc19wZXJfc2Vjb25kQhIKEF9oZWFkaW5nX2RlZ3JlZX'
    'M=');

@$core.Deprecated('Use diagnosticsSnapshotDescriptor instead')
const DiagnosticsSnapshot$json = {
  '1': 'DiagnosticsSnapshot',
  '2': [
    {'1': 'reported', '3': 1, '4': 1, '5': 8, '10': 'reported'},
    {'1': 'backend', '3': 2, '4': 1, '5': 9, '10': 'backend'},
    {
      '1': 'overall_status',
      '3': 3,
      '4': 1,
      '5': 14,
      '6': '.psycraft.logos.robotcommand.team.v1.DiagnosticStatus',
      '10': 'overallStatus'
    },
    {'1': 'summary', '3': 4, '4': 1, '5': 9, '10': 'summary'},
    {
      '1': 'arm_readiness',
      '3': 5,
      '4': 1,
      '5': 14,
      '6': '.psycraft.logos.robotcommand.team.v1.DiagnosticStatus',
      '10': 'armReadiness'
    },
    {
      '1': 'arm_readiness_detail',
      '3': 6,
      '4': 1,
      '5': 9,
      '10': 'armReadinessDetail'
    },
    {
      '1': 'navigation_readiness',
      '3': 7,
      '4': 1,
      '5': 14,
      '6': '.psycraft.logos.robotcommand.team.v1.DiagnosticStatus',
      '10': 'navigationReadiness'
    },
    {
      '1': 'navigation_readiness_detail',
      '3': 8,
      '4': 1,
      '5': 9,
      '10': 'navigationReadinessDetail'
    },
    {
      '1': 'telemetry_status',
      '3': 9,
      '4': 1,
      '5': 14,
      '6': '.psycraft.logos.robotcommand.team.v1.DiagnosticStatus',
      '10': 'telemetryStatus'
    },
    {'1': 'telemetry_detail', '3': 10, '4': 1, '5': 9, '10': 'telemetryDetail'},
    {
      '1': 'checks',
      '3': 11,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.DiagnosticCheck',
      '10': 'checks'
    },
    {
      '1': 'recent_messages',
      '3': 12,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.DiagnosticMessage',
      '10': 'recentMessages'
    },
    {
      '1': 'observed_at',
      '3': 13,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'observedAt'
    },
    {'1': 'system_id', '3': 14, '4': 1, '5': 13, '10': 'systemId'},
    {'1': 'component_id', '3': 15, '4': 1, '5': 13, '10': 'componentId'},
    {'1': 'version', '3': 16, '4': 1, '5': 9, '10': 'version'},
    {'1': 'mode', '3': 17, '4': 1, '5': 9, '10': 'mode'},
  ],
};

/// Descriptor for `DiagnosticsSnapshot`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List diagnosticsSnapshotDescriptor = $convert.base64Decode(
    'ChNEaWFnbm9zdGljc1NuYXBzaG90EhoKCHJlcG9ydGVkGAEgASgIUghyZXBvcnRlZBIYCgdiYW'
    'NrZW5kGAIgASgJUgdiYWNrZW5kElwKDm92ZXJhbGxfc3RhdHVzGAMgASgOMjUucHN5Y3JhZnQu'
    'bG9nb3MuZmllbGRjb25zb2xlLnRlYW0udjEuRGlhZ25vc3RpY1N0YXR1c1INb3ZlcmFsbFN0YX'
    'R1cxIYCgdzdW1tYXJ5GAQgASgJUgdzdW1tYXJ5EloKDWFybV9yZWFkaW5lc3MYBSABKA4yNS5w'
    'c3ljcmFmdC5sb2dvcy5maWVsZGNvbnNvbGUudGVhbS52MS5EaWFnbm9zdGljU3RhdHVzUgxhcm'
    '1SZWFkaW5lc3MSMAoUYXJtX3JlYWRpbmVzc19kZXRhaWwYBiABKAlSEmFybVJlYWRpbmVzc0Rl'
    'dGFpbBJoChRuYXZpZ2F0aW9uX3JlYWRpbmVzcxgHIAEoDjI1LnBzeWNyYWZ0LmxvZ29zLmZpZW'
    'xkY29uc29sZS50ZWFtLnYxLkRpYWdub3N0aWNTdGF0dXNSE25hdmlnYXRpb25SZWFkaW5lc3MS'
    'PgobbmF2aWdhdGlvbl9yZWFkaW5lc3NfZGV0YWlsGAggASgJUhluYXZpZ2F0aW9uUmVhZGluZX'
    'NzRGV0YWlsEmAKEHRlbGVtZXRyeV9zdGF0dXMYCSABKA4yNS5wc3ljcmFmdC5sb2dvcy5maWVs'
    'ZGNvbnNvbGUudGVhbS52MS5EaWFnbm9zdGljU3RhdHVzUg90ZWxlbWV0cnlTdGF0dXMSKQoQdG'
    'VsZW1ldHJ5X2RldGFpbBgKIAEoCVIPdGVsZW1ldHJ5RGV0YWlsEkwKBmNoZWNrcxgLIAMoCzI0'
    'LnBzeWNyYWZ0LmxvZ29zLmZpZWxkY29uc29sZS50ZWFtLnYxLkRpYWdub3N0aWNDaGVja1IGY2'
    'hlY2tzEl8KD3JlY2VudF9tZXNzYWdlcxgMIAMoCzI2LnBzeWNyYWZ0LmxvZ29zLmZpZWxkY29u'
    'c29sZS50ZWFtLnYxLkRpYWdub3N0aWNNZXNzYWdlUg5yZWNlbnRNZXNzYWdlcxI7CgtvYnNlcn'
    'ZlZF9hdBgNIAEoCzIaLmdvb2dsZS5wcm90b2J1Zi5UaW1lc3RhbXBSCm9ic2VydmVkQXQSGwoJ'
    'c3lzdGVtX2lkGA4gASgNUghzeXN0ZW1JZBIhCgxjb21wb25lbnRfaWQYDyABKA1SC2NvbXBvbm'
    'VudElkEhgKB3ZlcnNpb24YECABKAlSB3ZlcnNpb24SEgoEbW9kZRgRIAEoCVIEbW9kZQ==');

@$core.Deprecated('Use diagnosticCheckDescriptor instead')
const DiagnosticCheck$json = {
  '1': 'DiagnosticCheck',
  '2': [
    {'1': 'code', '3': 1, '4': 1, '5': 9, '10': 'code'},
    {'1': 'category', '3': 2, '4': 1, '5': 9, '10': 'category'},
    {'1': 'name', '3': 3, '4': 1, '5': 9, '10': 'name'},
    {
      '1': 'state',
      '3': 4,
      '4': 1,
      '5': 14,
      '6': '.psycraft.logos.robotcommand.team.v1.DiagnosticCheckState',
      '10': 'state'
    },
    {'1': 'detail', '3': 5, '4': 1, '5': 9, '10': 'detail'},
    {
      '1': 'affected_operations',
      '3': 6,
      '4': 3,
      '5': 9,
      '10': 'affectedOperations'
    },
  ],
};

/// Descriptor for `DiagnosticCheck`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List diagnosticCheckDescriptor = $convert.base64Decode(
    'Cg9EaWFnbm9zdGljQ2hlY2sSEgoEY29kZRgBIAEoCVIEY29kZRIaCghjYXRlZ29yeRgCIAEoCV'
    'IIY2F0ZWdvcnkSEgoEbmFtZRgDIAEoCVIEbmFtZRJPCgVzdGF0ZRgEIAEoDjI5LnBzeWNyYWZ0'
    'LmxvZ29zLmZpZWxkY29uc29sZS50ZWFtLnYxLkRpYWdub3N0aWNDaGVja1N0YXRlUgVzdGF0ZR'
    'IWCgZkZXRhaWwYBSABKAlSBmRldGFpbBIvChNhZmZlY3RlZF9vcGVyYXRpb25zGAYgAygJUhJh'
    'ZmZlY3RlZE9wZXJhdGlvbnM=');

@$core.Deprecated('Use diagnosticMessageDescriptor instead')
const DiagnosticMessage$json = {
  '1': 'DiagnosticMessage',
  '2': [
    {'1': 'id', '3': 1, '4': 1, '5': 9, '10': 'id'},
    {
      '1': 'timestamp',
      '3': 2,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'timestamp'
    },
    {'1': 'severity', '3': 3, '4': 1, '5': 9, '10': 'severity'},
    {'1': 'text', '3': 4, '4': 1, '5': 9, '10': 'text'},
    {'1': 'source', '3': 5, '4': 1, '5': 9, '10': 'source'},
  ],
};

/// Descriptor for `DiagnosticMessage`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List diagnosticMessageDescriptor = $convert.base64Decode(
    'ChFEaWFnbm9zdGljTWVzc2FnZRIOCgJpZBgBIAEoCVICaWQSOAoJdGltZXN0YW1wGAIgASgLMh'
    'ouZ29vZ2xlLnByb3RvYnVmLlRpbWVzdGFtcFIJdGltZXN0YW1wEhoKCHNldmVyaXR5GAMgASgJ'
    'UghzZXZlcml0eRISCgR0ZXh0GAQgASgJUgR0ZXh0EhYKBnNvdXJjZRgFIAEoCVIGc291cmNl');

@$core.Deprecated('Use actionSnapshotDescriptor instead')
const ActionSnapshot$json = {
  '1': 'ActionSnapshot',
  '2': [
    {'1': 'current_kind', '3': 1, '4': 1, '5': 9, '10': 'currentKind'},
    {'1': 'current_state', '3': 2, '4': 1, '5': 9, '10': 'currentState'},
    {'1': 'current_summary', '3': 3, '4': 1, '5': 9, '10': 'currentSummary'},
    {'1': 'queued_kind', '3': 4, '4': 1, '5': 9, '10': 'queuedKind'},
    {'1': 'queued_state', '3': 5, '4': 1, '5': 9, '10': 'queuedState'},
    {'1': 'queued_summary', '3': 6, '4': 1, '5': 9, '10': 'queuedSummary'},
    {
      '1': 'updated_at',
      '3': 7,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'updatedAt'
    },
  ],
};

/// Descriptor for `ActionSnapshot`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List actionSnapshotDescriptor = $convert.base64Decode(
    'Cg5BY3Rpb25TbmFwc2hvdBIhCgxjdXJyZW50X2tpbmQYASABKAlSC2N1cnJlbnRLaW5kEiMKDW'
    'N1cnJlbnRfc3RhdGUYAiABKAlSDGN1cnJlbnRTdGF0ZRInCg9jdXJyZW50X3N1bW1hcnkYAyAB'
    'KAlSDmN1cnJlbnRTdW1tYXJ5Eh8KC3F1ZXVlZF9raW5kGAQgASgJUgpxdWV1ZWRLaW5kEiEKDH'
    'F1ZXVlZF9zdGF0ZRgFIAEoCVILcXVldWVkU3RhdGUSJQoOcXVldWVkX3N1bW1hcnkYBiABKAlS'
    'DXF1ZXVlZFN1bW1hcnkSOQoKdXBkYXRlZF9hdBgHIAEoCzIaLmdvb2dsZS5wcm90b2J1Zi5UaW'
    '1lc3RhbXBSCXVwZGF0ZWRBdA==');

@$core.Deprecated('Use linkSnapshotDescriptor instead')
const LinkSnapshot$json = {
  '1': 'LinkSnapshot',
  '2': [
    {'1': 'id', '3': 1, '4': 1, '5': 9, '10': 'id'},
    {'1': 'connection_id', '3': 2, '4': 1, '5': 9, '10': 'connectionId'},
    {'1': 'name', '3': 3, '4': 1, '5': 9, '10': 'name'},
    {'1': 'kind', '3': 4, '4': 1, '5': 9, '10': 'kind'},
    {'1': 'direction', '3': 5, '4': 1, '5': 9, '10': 'direction'},
    {'1': 'state', '3': 6, '4': 1, '5': 9, '10': 'state'},
    {'1': 'health', '3': 7, '4': 1, '5': 9, '10': 'health'},
    {'1': 'connected', '3': 8, '4': 1, '5': 8, '10': 'connected'},
    {'1': 'stale', '3': 9, '4': 1, '5': 8, '10': 'stale'},
    {
      '1': 'rssi_dbm',
      '3': 10,
      '4': 1,
      '5': 1,
      '9': 0,
      '10': 'rssiDbm',
      '17': true
    },
    {'1': 'snr_db', '3': 11, '4': 1, '5': 1, '9': 1, '10': 'snrDb', '17': true},
    {
      '1': 'quality',
      '3': 12,
      '4': 1,
      '5': 1,
      '9': 2,
      '10': 'quality',
      '17': true
    },
    {
      '1': 'packet_loss',
      '3': 13,
      '4': 1,
      '5': 1,
      '9': 3,
      '10': 'packetLoss',
      '17': true
    },
    {
      '1': 'latency_milliseconds',
      '3': 14,
      '4': 1,
      '5': 1,
      '9': 4,
      '10': 'latencyMilliseconds',
      '17': true
    },
    {'1': 'code', '3': 15, '4': 1, '5': 9, '10': 'code'},
    {'1': 'message', '3': 16, '4': 1, '5': 9, '10': 'message'},
    {
      '1': 'observed_at',
      '3': 17,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'observedAt'
    },
  ],
  '8': [
    {'1': '_rssi_dbm'},
    {'1': '_snr_db'},
    {'1': '_quality'},
    {'1': '_packet_loss'},
    {'1': '_latency_milliseconds'},
  ],
};

/// Descriptor for `LinkSnapshot`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List linkSnapshotDescriptor = $convert.base64Decode(
    'CgxMaW5rU25hcHNob3QSDgoCaWQYASABKAlSAmlkEiMKDWNvbm5lY3Rpb25faWQYAiABKAlSDG'
    'Nvbm5lY3Rpb25JZBISCgRuYW1lGAMgASgJUgRuYW1lEhIKBGtpbmQYBCABKAlSBGtpbmQSHAoJ'
    'ZGlyZWN0aW9uGAUgASgJUglkaXJlY3Rpb24SFAoFc3RhdGUYBiABKAlSBXN0YXRlEhYKBmhlYW'
    'x0aBgHIAEoCVIGaGVhbHRoEhwKCWNvbm5lY3RlZBgIIAEoCFIJY29ubmVjdGVkEhQKBXN0YWxl'
    'GAkgASgIUgVzdGFsZRIeCghyc3NpX2RibRgKIAEoAUgAUgdyc3NpRGJtiAEBEhoKBnNucl9kYh'
    'gLIAEoAUgBUgVzbnJEYogBARIdCgdxdWFsaXR5GAwgASgBSAJSB3F1YWxpdHmIAQESJAoLcGFj'
    'a2V0X2xvc3MYDSABKAFIA1IKcGFja2V0TG9zc4gBARI2ChRsYXRlbmN5X21pbGxpc2Vjb25kcx'
    'gOIAEoAUgEUhNsYXRlbmN5TWlsbGlzZWNvbmRziAEBEhIKBGNvZGUYDyABKAlSBGNvZGUSGAoH'
    'bWVzc2FnZRgQIAEoCVIHbWVzc2FnZRI7CgtvYnNlcnZlZF9hdBgRIAEoCzIaLmdvb2dsZS5wcm'
    '90b2J1Zi5UaW1lc3RhbXBSCm9ic2VydmVkQXRCCwoJX3Jzc2lfZGJtQgkKB19zbnJfZGJCCgoI'
    'X3F1YWxpdHlCDgoMX3BhY2tldF9sb3NzQhcKFV9sYXRlbmN5X21pbGxpc2Vjb25kcw==');

@$core.Deprecated('Use mapSnapshotDescriptor instead')
const MapSnapshot$json = {
  '1': 'MapSnapshot',
  '2': [
    {'1': 'coordinate_frame', '3': 1, '4': 1, '5': 9, '10': 'coordinateFrame'},
    {'1': 'frame_label', '3': 2, '4': 1, '5': 9, '10': 'frameLabel'},
    {
      '1': 'viewport',
      '3': 3,
      '4': 1,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.MapViewport',
      '10': 'viewport'
    },
    {'1': 'style_id', '3': 4, '4': 1, '5': 9, '10': 'styleId'},
    {'1': 'style_name', '3': 5, '4': 1, '5': 9, '10': 'styleName'},
    {
      '1': 'style_attribution',
      '3': 6,
      '4': 1,
      '5': 9,
      '10': 'styleAttribution'
    },
    {'1': 'geometry_visible', '3': 7, '4': 1, '5': 8, '10': 'geometryVisible'},
    {'1': 'policy_visible', '3': 8, '4': 1, '5': 8, '10': 'policyVisible'},
    {'1': 'trails_visible', '3': 9, '4': 1, '5': 8, '10': 'trailsVisible'},
    {
      '1': 'destinations_visible',
      '3': 10,
      '4': 1,
      '5': 8,
      '10': 'destinationsVisible'
    },
    {'1': 'labels_visible', '3': 11, '4': 1, '5': 8, '10': 'labelsVisible'},
    {
      '1': 'selected_unit_ids',
      '3': 12,
      '4': 3,
      '5': 9,
      '10': 'selectedUnitIds'
    },
    {
      '1': 'unit_visuals',
      '3': 13,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.MapUnitVisual',
      '10': 'unitVisuals'
    },
    {
      '1': 'trails',
      '3': 14,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.MapTrail',
      '10': 'trails'
    },
    {
      '1': 'geometries',
      '3': 15,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.MapGeometry',
      '10': 'geometries'
    },
    {
      '1': 'destinations',
      '3': 16,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.MapDestination',
      '10': 'destinations'
    },
    {
      '1': 'formation_preview_destinations',
      '3': 17,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.MapDestination',
      '10': 'formationPreviewDestinations'
    },
    {
      '1': 'formation_preview_paths',
      '3': 18,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.MapPath',
      '10': 'formationPreviewPaths'
    },
    {
      '1': 'operator_location',
      '3': 19,
      '4': 1,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.OperatorLocation',
      '10': 'operatorLocation'
    },
  ],
};

/// Descriptor for `MapSnapshot`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List mapSnapshotDescriptor = $convert.base64Decode(
    'CgtNYXBTbmFwc2hvdBIpChBjb29yZGluYXRlX2ZyYW1lGAEgASgJUg9jb29yZGluYXRlRnJhbW'
    'USHwoLZnJhbWVfbGFiZWwYAiABKAlSCmZyYW1lTGFiZWwSTAoIdmlld3BvcnQYAyABKAsyMC5w'
    'c3ljcmFmdC5sb2dvcy5maWVsZGNvbnNvbGUudGVhbS52MS5NYXBWaWV3cG9ydFIIdmlld3Bvcn'
    'QSGQoIc3R5bGVfaWQYBCABKAlSB3N0eWxlSWQSHQoKc3R5bGVfbmFtZRgFIAEoCVIJc3R5bGVO'
    'YW1lEisKEXN0eWxlX2F0dHJpYnV0aW9uGAYgASgJUhBzdHlsZUF0dHJpYnV0aW9uEikKEGdlb2'
    '1ldHJ5X3Zpc2libGUYByABKAhSD2dlb21ldHJ5VmlzaWJsZRIlCg5wb2xpY3lfdmlzaWJsZRgI'
    'IAEoCFINcG9saWN5VmlzaWJsZRIlCg50cmFpbHNfdmlzaWJsZRgJIAEoCFINdHJhaWxzVmlzaW'
    'JsZRIxChRkZXN0aW5hdGlvbnNfdmlzaWJsZRgKIAEoCFITZGVzdGluYXRpb25zVmlzaWJsZRIl'
    'Cg5sYWJlbHNfdmlzaWJsZRgLIAEoCFINbGFiZWxzVmlzaWJsZRIqChFzZWxlY3RlZF91bml0X2'
    'lkcxgMIAMoCVIPc2VsZWN0ZWRVbml0SWRzElUKDHVuaXRfdmlzdWFscxgNIAMoCzIyLnBzeWNy'
    'YWZ0LmxvZ29zLmZpZWxkY29uc29sZS50ZWFtLnYxLk1hcFVuaXRWaXN1YWxSC3VuaXRWaXN1YW'
    'xzEkUKBnRyYWlscxgOIAMoCzItLnBzeWNyYWZ0LmxvZ29zLmZpZWxkY29uc29sZS50ZWFtLnYx'
    'Lk1hcFRyYWlsUgZ0cmFpbHMSUAoKZ2VvbWV0cmllcxgPIAMoCzIwLnBzeWNyYWZ0LmxvZ29zLm'
    'ZpZWxkY29uc29sZS50ZWFtLnYxLk1hcEdlb21ldHJ5UgpnZW9tZXRyaWVzElcKDGRlc3RpbmF0'
    'aW9ucxgQIAMoCzIzLnBzeWNyYWZ0LmxvZ29zLmZpZWxkY29uc29sZS50ZWFtLnYxLk1hcERlc3'
    'RpbmF0aW9uUgxkZXN0aW5hdGlvbnMSeQoeZm9ybWF0aW9uX3ByZXZpZXdfZGVzdGluYXRpb25z'
    'GBEgAygLMjMucHN5Y3JhZnQubG9nb3MuZmllbGRjb25zb2xlLnRlYW0udjEuTWFwRGVzdGluYX'
    'Rpb25SHGZvcm1hdGlvblByZXZpZXdEZXN0aW5hdGlvbnMSZAoXZm9ybWF0aW9uX3ByZXZpZXdf'
    'cGF0aHMYEiADKAsyLC5wc3ljcmFmdC5sb2dvcy5maWVsZGNvbnNvbGUudGVhbS52MS5NYXBQYX'
    'RoUhVmb3JtYXRpb25QcmV2aWV3UGF0aHMSYgoRb3BlcmF0b3JfbG9jYXRpb24YEyABKAsyNS5w'
    'c3ljcmFmdC5sb2dvcy5maWVsZGNvbnNvbGUudGVhbS52MS5PcGVyYXRvckxvY2F0aW9uUhBvcG'
    'VyYXRvckxvY2F0aW9u');

@$core.Deprecated('Use mapViewportDescriptor instead')
const MapViewport$json = {
  '1': 'MapViewport',
  '2': [
    {'1': 'reported', '3': 1, '4': 1, '5': 8, '10': 'reported'},
    {
      '1': 'longitude_degrees',
      '3': 2,
      '4': 1,
      '5': 1,
      '10': 'longitudeDegrees'
    },
    {'1': 'latitude_degrees', '3': 3, '4': 1, '5': 1, '10': 'latitudeDegrees'},
    {
      '1': 'resolution_metres_per_pixel',
      '3': 4,
      '4': 1,
      '5': 1,
      '10': 'resolutionMetresPerPixel'
    },
    {'1': 'rotation_degrees', '3': 5, '4': 1, '5': 1, '10': 'rotationDegrees'},
  ],
};

/// Descriptor for `MapViewport`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List mapViewportDescriptor = $convert.base64Decode(
    'CgtNYXBWaWV3cG9ydBIaCghyZXBvcnRlZBgBIAEoCFIIcmVwb3J0ZWQSKwoRbG9uZ2l0dWRlX2'
    'RlZ3JlZXMYAiABKAFSEGxvbmdpdHVkZURlZ3JlZXMSKQoQbGF0aXR1ZGVfZGVncmVlcxgDIAEo'
    'AVIPbGF0aXR1ZGVEZWdyZWVzEj0KG3Jlc29sdXRpb25fbWV0cmVzX3Blcl9waXhlbBgEIAEoAV'
    'IYcmVzb2x1dGlvbk1ldHJlc1BlclBpeGVsEikKEHJvdGF0aW9uX2RlZ3JlZXMYBSABKAFSD3Jv'
    'dGF0aW9uRGVncmVlcw==');

@$core.Deprecated('Use mapUnitVisualDescriptor instead')
const MapUnitVisual$json = {
  '1': 'MapUnitVisual',
  '2': [
    {'1': 'unit_id', '3': 1, '4': 1, '5': 9, '10': 'unitId'},
    {'1': 'name', '3': 2, '4': 1, '5': 9, '10': 'name'},
    {'1': 'x', '3': 3, '4': 1, '5': 1, '10': 'x'},
    {'1': 'y', '3': 4, '4': 1, '5': 1, '10': 'y'},
    {
      '1': 'heading_degrees',
      '3': 5,
      '4': 1,
      '5': 1,
      '9': 0,
      '10': 'headingDegrees',
      '17': true
    },
    {
      '1': 'state',
      '3': 6,
      '4': 1,
      '5': 14,
      '6': '.psycraft.logos.robotcommand.team.v1.Availability',
      '10': 'state'
    },
    {'1': 'selected', '3': 7, '4': 1, '5': 8, '10': 'selected'},
    {'1': 'ghost', '3': 8, '4': 1, '5': 8, '10': 'ghost'},
  ],
  '8': [
    {'1': '_heading_degrees'},
  ],
};

/// Descriptor for `MapUnitVisual`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List mapUnitVisualDescriptor = $convert.base64Decode(
    'Cg1NYXBVbml0VmlzdWFsEhcKB3VuaXRfaWQYASABKAlSBnVuaXRJZBISCgRuYW1lGAIgASgJUg'
    'RuYW1lEgwKAXgYAyABKAFSAXgSDAoBeRgEIAEoAVIBeRIsCg9oZWFkaW5nX2RlZ3JlZXMYBSAB'
    'KAFIAFIOaGVhZGluZ0RlZ3JlZXOIAQESRwoFc3RhdGUYBiABKA4yMS5wc3ljcmFmdC5sb2dvcy'
    '5maWVsZGNvbnNvbGUudGVhbS52MS5BdmFpbGFiaWxpdHlSBXN0YXRlEhoKCHNlbGVjdGVkGAcg'
    'ASgIUghzZWxlY3RlZBIUCgVnaG9zdBgIIAEoCFIFZ2hvc3RCEgoQX2hlYWRpbmdfZGVncmVlcw'
    '==');

@$core.Deprecated('Use geoPointDescriptor instead')
const GeoPoint$json = {
  '1': 'GeoPoint',
  '2': [
    {'1': 'x', '3': 1, '4': 1, '5': 1, '10': 'x'},
    {'1': 'y', '3': 2, '4': 1, '5': 1, '10': 'y'},
    {
      '1': 'altitude_metres',
      '3': 3,
      '4': 1,
      '5': 1,
      '9': 0,
      '10': 'altitudeMetres',
      '17': true
    },
  ],
  '8': [
    {'1': '_altitude_metres'},
  ],
};

/// Descriptor for `GeoPoint`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List geoPointDescriptor = $convert.base64Decode(
    'CghHZW9Qb2ludBIMCgF4GAEgASgBUgF4EgwKAXkYAiABKAFSAXkSLAoPYWx0aXR1ZGVfbWV0cm'
    'VzGAMgASgBSABSDmFsdGl0dWRlTWV0cmVziAEBQhIKEF9hbHRpdHVkZV9tZXRyZXM=');

@$core.Deprecated('Use mapTrailDescriptor instead')
const MapTrail$json = {
  '1': 'MapTrail',
  '2': [
    {'1': 'unit_id', '3': 1, '4': 1, '5': 9, '10': 'unitId'},
    {'1': 'name', '3': 2, '4': 1, '5': 9, '10': 'name'},
    {'1': 'connection_id', '3': 3, '4': 1, '5': 9, '10': 'connectionId'},
    {
      '1': 'points',
      '3': 4,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.GeoPoint',
      '10': 'points'
    },
    {
      '1': 'state',
      '3': 5,
      '4': 1,
      '5': 14,
      '6': '.psycraft.logos.robotcommand.team.v1.Availability',
      '10': 'state'
    },
    {'1': 'selected', '3': 6, '4': 1, '5': 8, '10': 'selected'},
  ],
};

/// Descriptor for `MapTrail`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List mapTrailDescriptor = $convert.base64Decode(
    'CghNYXBUcmFpbBIXCgd1bml0X2lkGAEgASgJUgZ1bml0SWQSEgoEbmFtZRgCIAEoCVIEbmFtZR'
    'IjCg1jb25uZWN0aW9uX2lkGAMgASgJUgxjb25uZWN0aW9uSWQSRQoGcG9pbnRzGAQgAygLMi0u'
    'cHN5Y3JhZnQubG9nb3MuZmllbGRjb25zb2xlLnRlYW0udjEuR2VvUG9pbnRSBnBvaW50cxJHCg'
    'VzdGF0ZRgFIAEoDjIxLnBzeWNyYWZ0LmxvZ29zLmZpZWxkY29uc29sZS50ZWFtLnYxLkF2YWls'
    'YWJpbGl0eVIFc3RhdGUSGgoIc2VsZWN0ZWQYBiABKAhSCHNlbGVjdGVk');

@$core.Deprecated('Use mapGeometryDescriptor instead')
const MapGeometry$json = {
  '1': 'MapGeometry',
  '2': [
    {'1': 'id', '3': 1, '4': 1, '5': 9, '10': 'id'},
    {'1': 'name', '3': 2, '4': 1, '5': 9, '10': 'name'},
    {'1': 'kind', '3': 3, '4': 1, '5': 9, '10': 'kind'},
    {'1': 'closed', '3': 4, '4': 1, '5': 8, '10': 'closed'},
    {
      '1': 'points',
      '3': 5,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.GeoPoint',
      '10': 'points'
    },
    {
      '1': 'rings',
      '3': 6,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.GeoRing',
      '10': 'rings'
    },
    {
      '1': 'policy_constraint',
      '3': 7,
      '4': 1,
      '5': 9,
      '10': 'policyConstraint'
    },
    {'1': 'policy_kind', '3': 8, '4': 1, '5': 9, '10': 'policyKind'},
    {'1': 'highlighted', '3': 9, '4': 1, '5': 8, '10': 'highlighted'},
  ],
};

/// Descriptor for `MapGeometry`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List mapGeometryDescriptor = $convert.base64Decode(
    'CgtNYXBHZW9tZXRyeRIOCgJpZBgBIAEoCVICaWQSEgoEbmFtZRgCIAEoCVIEbmFtZRISCgRraW'
    '5kGAMgASgJUgRraW5kEhYKBmNsb3NlZBgEIAEoCFIGY2xvc2VkEkUKBnBvaW50cxgFIAMoCzIt'
    'LnBzeWNyYWZ0LmxvZ29zLmZpZWxkY29uc29sZS50ZWFtLnYxLkdlb1BvaW50UgZwb2ludHMSQg'
    'oFcmluZ3MYBiADKAsyLC5wc3ljcmFmdC5sb2dvcy5maWVsZGNvbnNvbGUudGVhbS52MS5HZW9S'
    'aW5nUgVyaW5ncxIrChFwb2xpY3lfY29uc3RyYWludBgHIAEoCVIQcG9saWN5Q29uc3RyYWludB'
    'IfCgtwb2xpY3lfa2luZBgIIAEoCVIKcG9saWN5S2luZBIgCgtoaWdobGlnaHRlZBgJIAEoCFIL'
    'aGlnaGxpZ2h0ZWQ=');

@$core.Deprecated('Use geoRingDescriptor instead')
const GeoRing$json = {
  '1': 'GeoRing',
  '2': [
    {
      '1': 'points',
      '3': 1,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.GeoPoint',
      '10': 'points'
    },
  ],
};

/// Descriptor for `GeoRing`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List geoRingDescriptor = $convert.base64Decode(
    'CgdHZW9SaW5nEkUKBnBvaW50cxgBIAMoCzItLnBzeWNyYWZ0LmxvZ29zLmZpZWxkY29uc29sZS'
    '50ZWFtLnYxLkdlb1BvaW50UgZwb2ludHM=');

@$core.Deprecated('Use mapDestinationDescriptor instead')
const MapDestination$json = {
  '1': 'MapDestination',
  '2': [
    {'1': 'unit_id', '3': 1, '4': 1, '5': 9, '10': 'unitId'},
    {'1': 'latitude_degrees', '3': 2, '4': 1, '5': 1, '10': 'latitudeDegrees'},
    {
      '1': 'longitude_degrees',
      '3': 3,
      '4': 1,
      '5': 1,
      '10': 'longitudeDegrees'
    },
    {'1': 'selected', '3': 4, '4': 1, '5': 8, '10': 'selected'},
    {'1': 'preview', '3': 5, '4': 1, '5': 8, '10': 'preview'},
  ],
};

/// Descriptor for `MapDestination`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List mapDestinationDescriptor = $convert.base64Decode(
    'Cg5NYXBEZXN0aW5hdGlvbhIXCgd1bml0X2lkGAEgASgJUgZ1bml0SWQSKQoQbGF0aXR1ZGVfZG'
    'VncmVlcxgCIAEoAVIPbGF0aXR1ZGVEZWdyZWVzEisKEWxvbmdpdHVkZV9kZWdyZWVzGAMgASgB'
    'UhBsb25naXR1ZGVEZWdyZWVzEhoKCHNlbGVjdGVkGAQgASgIUghzZWxlY3RlZBIYCgdwcmV2aW'
    'V3GAUgASgIUgdwcmV2aWV3');

@$core.Deprecated('Use mapPathDescriptor instead')
const MapPath$json = {
  '1': 'MapPath',
  '2': [
    {
      '1': 'points',
      '3': 1,
      '4': 3,
      '5': 11,
      '6': '.psycraft.logos.robotcommand.team.v1.GeoPoint',
      '10': 'points'
    },
    {'1': 'closed', '3': 2, '4': 1, '5': 8, '10': 'closed'},
  ],
};

/// Descriptor for `MapPath`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List mapPathDescriptor = $convert.base64Decode(
    'CgdNYXBQYXRoEkUKBnBvaW50cxgBIAMoCzItLnBzeWNyYWZ0LmxvZ29zLmZpZWxkY29uc29sZS'
    '50ZWFtLnYxLkdlb1BvaW50UgZwb2ludHMSFgoGY2xvc2VkGAIgASgIUgZjbG9zZWQ=');

@$core.Deprecated('Use operatorLocationDescriptor instead')
const OperatorLocation$json = {
  '1': 'OperatorLocation',
  '2': [
    {'1': 'shared', '3': 1, '4': 1, '5': 8, '10': 'shared'},
    {'1': 'available', '3': 2, '4': 1, '5': 8, '10': 'available'},
    {
      '1': 'latitude_degrees',
      '3': 3,
      '4': 1,
      '5': 1,
      '9': 0,
      '10': 'latitudeDegrees',
      '17': true
    },
    {
      '1': 'longitude_degrees',
      '3': 4,
      '4': 1,
      '5': 1,
      '9': 1,
      '10': 'longitudeDegrees',
      '17': true
    },
    {
      '1': 'accuracy_metres',
      '3': 5,
      '4': 1,
      '5': 1,
      '9': 2,
      '10': 'accuracyMetres',
      '17': true
    },
    {
      '1': 'observed_at',
      '3': 6,
      '4': 1,
      '5': 11,
      '6': '.google.protobuf.Timestamp',
      '10': 'observedAt'
    },
  ],
  '8': [
    {'1': '_latitude_degrees'},
    {'1': '_longitude_degrees'},
    {'1': '_accuracy_metres'},
  ],
};

/// Descriptor for `OperatorLocation`. Decode as a `google.protobuf.DescriptorProto`.
final $typed_data.Uint8List operatorLocationDescriptor = $convert.base64Decode(
    'ChBPcGVyYXRvckxvY2F0aW9uEhYKBnNoYXJlZBgBIAEoCFIGc2hhcmVkEhwKCWF2YWlsYWJsZR'
    'gCIAEoCFIJYXZhaWxhYmxlEi4KEGxhdGl0dWRlX2RlZ3JlZXMYAyABKAFIAFIPbGF0aXR1ZGVE'
    'ZWdyZWVziAEBEjAKEWxvbmdpdHVkZV9kZWdyZWVzGAQgASgBSAFSEGxvbmdpdHVkZURlZ3JlZX'
    'OIAQESLAoPYWNjdXJhY3lfbWV0cmVzGAUgASgBSAJSDmFjY3VyYWN5TWV0cmVziAEBEjsKC29i'
    'c2VydmVkX2F0GAYgASgLMhouZ29vZ2xlLnByb3RvYnVmLlRpbWVzdGFtcFIKb2JzZXJ2ZWRBdE'
    'ITChFfbGF0aXR1ZGVfZGVncmVlc0IUChJfbG9uZ2l0dWRlX2RlZ3JlZXNCEgoQX2FjY3VyYWN5'
    'X21ldHJlcw==');
