using System.Collections.Specialized;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class ApplicationStatusViewModel : ObservableObject
{
    private readonly IEntityStore<string, ConnectionRecord> _connections;
    private readonly IEntityStore<string, RuntimeRecord> _runtimes;
    private readonly IEntityStore<string, TeamRecord> _teams;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, LiveStreamRecord> _streams;
    private readonly IEntityStore<string, OperationalCommandRecord> _commands;

    public ApplicationStatusViewModel(
        IEntityStore<string, ConnectionRecord> connections,
        IEntityStore<string, RuntimeRecord> runtimes,
        IEntityStore<string, TeamRecord> teams,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, LiveStreamRecord> streams,
        IEntityStore<string, OperationalCommandRecord> commands)
    {
        _connections = connections;
        _runtimes = runtimes;
        _teams = teams;
        _vehicles = vehicles;
        _telemetry = telemetry;
        _streams = streams;
        _commands = commands;

        Subscribe(_connections.Items);
        Subscribe(_runtimes.Items);
        Subscribe(_teams.Items);
        Subscribe(_vehicles.Items);
        Subscribe(_telemetry.Items);
        Subscribe(_streams.Items);
        Subscribe(_commands.Items);
    }

    public string ConnectionText
    {
        get
        {
            var online = _connections.Items.Count(item => item.State is AvailabilityState.Online or AvailabilityState.Degraded);
            return _connections.Items.Count == 0
                ? "No operational connections"
                : $"{online}/{_connections.Items.Count} connection(s) online";
        }
    }

    public string FleetText
        => $"{_runtimes.Items.Count} runtime(s) · {_teams.Items.Count} team(s) · {_vehicles.Items.Count} vehicle(s)";

    public string FrameworkText
    {
        get
        {
            var live = _streams.Items.Count(item => item.State == LiveStreamState.Live);
            var total = _streams.Items.Count;
            var commandSuffix = _commands.Items.Count == 0
                ? "no commands"
                : $"{_commands.Items.Count} command(s)";
            return total == 0
                ? $"Live subscriptions idle · {commandSuffix}"
                : $"{live}/{total} streams live · {_telemetry.Items.Count} telemetry source(s) · {commandSuffix}";
        }
    }

    private void Subscribe(System.Collections.IEnumerable collection)
        => ((INotifyCollectionChanged)collection).CollectionChanged += OnCollectionChanged;

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(FleetText));
        OnPropertyChanged(nameof(FrameworkText));
    }
}
