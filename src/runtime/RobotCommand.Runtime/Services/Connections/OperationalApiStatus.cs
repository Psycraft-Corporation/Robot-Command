namespace RobotCommand.Services.Connections;

public enum OperationalApiDomain
{
    System,
    Mission,
    Task,
    Autonomy,
    Geometry,
    Policy,
    Sensors,
    VehicleOperations
}

public enum OperationalApiAvailability
{
    Unknown,
    Inspecting,
    Available,
    Unavailable,
    Unimplemented,
    Unreachable,
    Faulted
}

public sealed record OperationalApiDomainStatus(
    OperationalApiDomain Domain,
    OperationalApiAvailability Availability,
    string Summary,
    string Detail,
    IReadOnlyList<string> CapabilityKeys)
{
    public bool Available => Availability == OperationalApiAvailability.Available;
}

public sealed record OperationalApiSnapshot(
    string ConnectionId,
    DateTimeOffset InspectedAt,
    IReadOnlyList<OperationalApiDomainStatus> Domains,
    IReadOnlyList<string> AvailableCapabilityKeys,
    IReadOnlyList<string> UnavailableCapabilityKeys)
{
    public static OperationalApiSnapshot Unknown(string connectionId) => new(
        connectionId,
        DateTimeOffset.MinValue,
        Enum.GetValues<OperationalApiDomain>()
            .Select(domain => new OperationalApiDomainStatus(
                domain,
                OperationalApiAvailability.Unknown,
                $"{domain} API not inspected",
                "Connect to Logos and inspect the generated operational clients.",
                []))
            .ToArray(),
        [],
        []);

    public bool IsAvailable(OperationalApiDomain domain)
        => Domains.FirstOrDefault(item => item.Domain == domain)?.Available == true;

    public OperationalApiDomainStatus Get(OperationalApiDomain domain)
        => Domains.First(item => item.Domain == domain);

    public string Summary
    {
        get
        {
            var operational = Domains.Where(item => item.Domain != OperationalApiDomain.System).ToArray();
            var available = operational.Count(item => item.Available);
            return $"{available}/{operational.Length} operational API domains available";
        }
    }
}
