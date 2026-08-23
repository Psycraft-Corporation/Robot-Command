using RobotCommand.Sdk;

if (args.Length == 0 || !Uri.TryCreate(args[0], UriKind.Absolute, out var endpoint))
{
    Console.WriteLine("Usage: dotnet run --project examples/RobotCommand.Observer -- https://HOST:7443");
    return;
}

var probe = await RobotCommandLanClient.ProbeAsync(endpoint);
Console.WriteLine($"Server: {probe.ServerInfo.DisplayName}");
Console.WriteLine($"API: {probe.ServerInfo.ApiVersion}");
Console.WriteLine($"TLS SHA-256: {probe.ObservedCertificateFingerprint}");
Console.Write("Trust this fingerprint and request access? [y/N] ");
if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) return;

await using var session = await RobotCommandLanClient.RequestAccessAsync(
    endpoint,
    probe.ObservedCertificateFingerprint,
    new RobotCommandClientIdentity(
        Environment.UserName,
        "Robot Command observer sample",
        "0.1.0",
        Guid.NewGuid().ToString("N")),
    statusChanged: status => Console.WriteLine($"Access: {status.State} — {status.Message}"));

session.SnapshotChanged += (_, snapshot) =>
    Console.WriteLine($"Revision {snapshot.Revision}: {snapshot.Units.Count} unit(s), " +
                      $"map {snapshot.Map.StyleName} at " +
                      $"{snapshot.Map.Viewport.LatitudeDegrees:0.00000}, " +
                      $"{snapshot.Map.Viewport.LongitudeDegrees:0.00000}");

Console.WriteLine("Connected. Press Enter to disconnect.");
Console.ReadLine();
