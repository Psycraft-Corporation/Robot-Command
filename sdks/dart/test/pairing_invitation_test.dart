import 'package:robot_command_sdk/robot_command_sdk.dart';
import 'package:test/test.dart';

void main() {
  test('round trips the Robot Command QR invitation format', () {
    final source = RobotCommandPairingInvitation(
      endpoint: Uri.parse('https://robot-command.local:7443'),
      certificateFingerprint: 'AB' * 32,
      pairingId: 'pairing-id',
      shortCode: '123456',
      expiresAt: DateTime.now().toUtc().add(const Duration(minutes: 10)),
      passphrase: 'Repair-Spindle',
    );

    final parsed =
        RobotCommandPairingInvitation.parse(source.toUri().toString());

    expect(parsed.endpoint, source.endpoint);
    expect(parsed.certificateFingerprint, source.certificateFingerprint);
    expect(parsed.pairingId, source.pairingId);
    expect(parsed.shortCode, source.shortCode);
    expect(parsed.passphrase, source.passphrase);
  });

  test('rejects non-HTTPS and malformed invitations', () {
    expect(
      () => RobotCommandPairingInvitation.parse(
        'logos-robot-command://pair/v1?endpoint=http%3A%2F%2Fhost%3A7443',
      ),
      throwsFormatException,
    );
    expect(
      () => RobotCommandPairingInvitation.parse(
        'logos-robot-command://pair/v1?endpoint=https%3A%2F%2Fhost&fingerprint=AA&pairing_id=x&code=123&expires=1',
      ),
      throwsFormatException,
    );
  });

  test('normalizes and validates fingerprints', () {
    expect(
      RobotCommandLanClient.normalizeFingerprint('ab:' * 32),
      'AB' * 32,
    );
    expect(
      () => RobotCommandLanClient.normalizeFingerprint('not-a-fingerprint'),
      throwsFormatException,
    );
  });
}
