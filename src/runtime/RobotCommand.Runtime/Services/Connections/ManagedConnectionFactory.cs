using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public sealed class ManagedConnectionFactory : ILogosConnectionFactory
{
    private readonly IReadOnlyList<IConnectionProvider> _providers;

    public ManagedConnectionFactory(IEnumerable<IConnectionProvider> providers)
        => _providers = providers.ToArray();

    public ILogosConnection Create(ConnectionDefinition definition)
    {
        var provider = _providers.FirstOrDefault(item => item.Supports(definition.Mode));
        return provider?.Create(definition) ??
               new UnsupportedLogosConnection(
                   definition,
                   $"No connection provider is registered for {definition.Mode}.");
    }
}
