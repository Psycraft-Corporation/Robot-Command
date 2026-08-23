using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Controls;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Reconciliation;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class OperateViewModel : ObservableObject
{
    private readonly ISelectionService _selection;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, LinkRecord> _links;
    private readonly IEntityStore<string, VehicleDiagnosticsSnapshot>? _diagnostics;
    private readonly IUnitDefinitionService? _reconciliation;
    private readonly IUnitSettingsService? _unitSettings;
    private readonly MyTeamViewModel? _myTeam;
    private readonly ILocalizationService? _localization;
    private string _selectionTitle = "No vehicle selected";
    private string _selectionSubtitle = "Select a discovered vehicle from the fleet panel.";
    private string _connectionText = "-";
    private string _stateText = "-";
    private string _readinessText = "-";
    private string _lifecycleText = "-";
    private string _armStateText = "-";
    private string _telemetryHealthText = "-";
    private string _flightModeText = "-";
    private string _landedStateText = "-";
    private string _positionText = "-";
    private string _localPositionText = "-";
    private string _altitudeText = "-";
    private string _velocityText = "-";
    private string _headingText = "-";
    private string _linkText = "-";
    private string _lastTelemetryText = "-";
    private string _dataStatus = "NO VEHICLE SELECTED";
    private string _overallStatusText = "Unknown";
    private string _overallStatusBrush = "#8A96A8";
    private string _armReadinessText = "Unknown";
    private string _navigationReadinessText = "Unknown";
    private string _telemetryStatusText = "Unknown";
    private string _armReadinessDetail = "Not assessed";
    private string _navigationReadinessDetail = "Not assessed";
    private string _telemetryStatusDetail = "Not assessed";
    private string _armReadinessBrush = "#8A96A8";
    private string _navigationReadinessBrush = "#8A96A8";
    private string _telemetryStatusBrush = "#8A96A8";
    private string _backendIdentityText = "Not reported";
    private IReadOnlyList<VehicleDiagnosticCheck> _diagnosticChecks = [];
    private IReadOnlyList<VehicleDiagnosticCheck> _diagnosticBlockers = [];
    private IReadOnlyList<VehicleDiagnosticMessage> _recentDiagnosticMessages = [];
    private bool _hasDiagnostics;
    private bool _hasMultipleSelectedUnits;
    private OperateLayoutMode _layoutMode = OperateLayoutMode.MapFocus;
    private GridLength _mapPaneWidth = new(1, GridUnitType.Star);
    private GridLength _videoPaneWidth = new(1, GridUnitType.Star);
    private bool _mapVisible = true;
    private bool _videoVisible = true;
    private bool _unitVisible;
    private bool _splitVisible = true;

    public OperateViewModel(
        ISelectionService selection,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, LinkRecord> links,
        OperationalMapViewModel map,
        CameraPanelViewModel camera,
        OperatorControlsViewModel controls,
        UnitsPanelViewModel units,
        IEntityStore<string, VehicleDiagnosticsSnapshot>? diagnostics = null,
        IUnitDefinitionService? reconciliation = null,
        IUnitSettingsService? unitSettings = null,
        MyTeamViewModel? myTeam = null,
        ILocalizationService? localization = null)
    {
        _selection = selection;
        _vehicles = vehicles;
        _telemetry = telemetry;
        _links = links;
        _diagnostics = diagnostics;
        _reconciliation = reconciliation;
        _unitSettings = unitSettings;
        _myTeam = myTeam;
        _localization = localization;
        Map = map;
        Camera = camera;
        Controls = controls;
        Units = units;
        _selection.Changed += OnSelectionChanged;
        Subscribe(_vehicles.Items);
        Subscribe(_telemetry.Items);
        Subscribe(_links.Items);
        if (_diagnostics is not null) Subscribe(_diagnostics.Items);
        if (_reconciliation is not null) _reconciliation.Changed += OnSelectionChanged;
        if (_unitSettings is not null) _unitSettings.Changed += OnUnitSettingsChanged;
        if (_localization is not null) _localization.PropertyChanged += OnLocalizationChanged;

        ShowMapCommand = new RelayCommand(_ => SetLayout(OperateLayoutMode.MapFocus));
        ShowSplitCommand = new RelayCommand(_ => SetLayout(OperateLayoutMode.Split));
        ShowVideoCommand = new RelayCommand(_ => SetLayout(OperateLayoutMode.VideoFocus));
        ShowUnitCommand = new RelayCommand(_ => SetLayout(OperateLayoutMode.UnitFocus));
        ApplySelection();
        ApplyLayout();
    }

    public OperationalMapViewModel Map { get; }

    public CameraPanelViewModel Camera { get; }

    public OperatorControlsViewModel Controls { get; }

    public UnitsPanelViewModel Units { get; }

    public string UnitsHeaderText => _localization?.Get("UnitsHeader") ?? "Units";
    public string OperationsHeaderText => _localization?.Get("InspectorOperations") ?? "Operations";

    public ICommand? ClearConnectedClientSelectionCommand => _myTeam?.ClearSelectedConnectedClientCommand;

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(UnitsHeaderText));
        OnPropertyChanged(nameof(OperationsHeaderText));
    }

    public string SelectionTitle { get => _selectionTitle; private set => SetProperty(ref _selectionTitle, value); }
    public string SelectionSubtitle { get => _selectionSubtitle; private set => SetProperty(ref _selectionSubtitle, value); }
    public string ConnectionText { get => _connectionText; private set => SetProperty(ref _connectionText, value); }
    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }
    public string ReadinessText { get => _readinessText; private set => SetProperty(ref _readinessText, value); }
    public string LifecycleText { get => _lifecycleText; private set => SetProperty(ref _lifecycleText, value); }
    public string ArmStateText { get => _armStateText; private set => SetProperty(ref _armStateText, value); }
    public string TelemetryHealthText { get => _telemetryHealthText; private set => SetProperty(ref _telemetryHealthText, value); }
    public string FlightModeText { get => _flightModeText; private set => SetProperty(ref _flightModeText, value); }
    public string LandedStateText { get => _landedStateText; private set => SetProperty(ref _landedStateText, value); }
    public string PositionText { get => _positionText; private set => SetProperty(ref _positionText, value); }
    public string LocalPositionText { get => _localPositionText; private set => SetProperty(ref _localPositionText, value); }
    public string AltitudeText { get => _altitudeText; private set => SetProperty(ref _altitudeText, value); }
    public string VelocityText { get => _velocityText; private set => SetProperty(ref _velocityText, value); }
    public string HeadingText { get => _headingText; private set => SetProperty(ref _headingText, value); }
    public string LinkText { get => _linkText; private set => SetProperty(ref _linkText, value); }
    public string LastTelemetryText { get => _lastTelemetryText; private set => SetProperty(ref _lastTelemetryText, value); }
    public string DataStatus { get => _dataStatus; private set => SetProperty(ref _dataStatus, value); }
    public string OverallStatusText { get => _overallStatusText; private set => SetProperty(ref _overallStatusText, value); }
    public string OverallStatusBrush { get => _overallStatusBrush; private set => SetProperty(ref _overallStatusBrush, value); }
    public string ArmReadinessText { get => _armReadinessText; private set => SetProperty(ref _armReadinessText, value); }
    public string NavigationReadinessText { get => _navigationReadinessText; private set => SetProperty(ref _navigationReadinessText, value); }
    public string TelemetryStatusText { get => _telemetryStatusText; private set => SetProperty(ref _telemetryStatusText, value); }
    public string ArmReadinessDetail { get => _armReadinessDetail; private set => SetProperty(ref _armReadinessDetail, value); }
    public string NavigationReadinessDetail { get => _navigationReadinessDetail; private set => SetProperty(ref _navigationReadinessDetail, value); }
    public string TelemetryStatusDetail { get => _telemetryStatusDetail; private set => SetProperty(ref _telemetryStatusDetail, value); }
    public string ArmReadinessBrush { get => _armReadinessBrush; private set => SetProperty(ref _armReadinessBrush, value); }
    public string NavigationReadinessBrush { get => _navigationReadinessBrush; private set => SetProperty(ref _navigationReadinessBrush, value); }
    public string TelemetryStatusBrush { get => _telemetryStatusBrush; private set => SetProperty(ref _telemetryStatusBrush, value); }
    public string BackendIdentityText { get => _backendIdentityText; private set => SetProperty(ref _backendIdentityText, value); }
    public IReadOnlyList<VehicleDiagnosticCheck> DiagnosticChecks { get => _diagnosticChecks; private set => SetProperty(ref _diagnosticChecks, value); }
    public IReadOnlyList<VehicleDiagnosticCheck> DiagnosticBlockers { get => _diagnosticBlockers; private set => SetProperty(ref _diagnosticBlockers, value); }
    public IReadOnlyList<VehicleDiagnosticMessage> RecentDiagnosticMessages { get => _recentDiagnosticMessages; private set => SetProperty(ref _recentDiagnosticMessages, value); }
    public bool HasDiagnostics { get => _hasDiagnostics; private set => SetProperty(ref _hasDiagnostics, value); }
    public bool HasDiagnosticBlockers => DiagnosticBlockers.Count > 0;
    public bool HasRecentDiagnosticMessages => RecentDiagnosticMessages.Count > 0;
    public bool HasMultipleSelectedUnits { get => _hasMultipleSelectedUnits; private set => SetProperty(ref _hasMultipleSelectedUnits, value); }

    public OperateLayoutMode LayoutMode
    {
        get => _layoutMode;
        private set => SetProperty(ref _layoutMode, value);
    }

    public GridLength MapPaneWidth
    {
        get => _mapPaneWidth;
        private set => SetProperty(ref _mapPaneWidth, value);
    }

    public GridLength VideoPaneWidth
    {
        get => _videoPaneWidth;
        private set => SetProperty(ref _videoPaneWidth, value);
    }

    public bool MapVisible
    {
        get => _mapVisible;
        private set => SetProperty(ref _mapVisible, value);
    }

    public bool VideoVisible
    {
        get => _videoVisible;
        private set => SetProperty(ref _videoVisible, value);
    }

    public bool UnitVisible
    {
        get => _unitVisible;
        private set => SetProperty(ref _unitVisible, value);
    }

    public bool SplitVisible
    {
        get => _splitVisible;
        private set => SetProperty(ref _splitVisible, value);
    }

    public string LayoutLabel => LayoutMode switch
    {
        OperateLayoutMode.MapFocus => "MAP",
        OperateLayoutMode.VideoFocus => "VIDEO",
        OperateLayoutMode.UnitFocus => "UNIT",
        _ => "SPLIT"
    };

    public ICommand ShowMapCommand { get; }

    public ICommand ShowSplitCommand { get; }

    public ICommand ShowVideoCommand { get; }

    public ICommand ShowUnitCommand { get; }

    private void Subscribe(System.Collections.IEnumerable collection)
        => ((INotifyCollectionChanged)collection).CollectionChanged += OnDataChanged;

    private void OnSelectionChanged(object? sender, EventArgs e) => ApplySelection();

    private void OnDataChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplySelection();

    private void SetLayout(OperateLayoutMode mode)
    {
        LayoutMode = mode;
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        switch (LayoutMode)
        {
            case OperateLayoutMode.MapFocus:
                MapVisible = true;
                VideoVisible = false;
                UnitVisible = false;
                MapPaneWidth = new GridLength(1, GridUnitType.Star);
                VideoPaneWidth = new GridLength(0);
                SplitVisible = false;
                break;
            case OperateLayoutMode.VideoFocus:
                MapVisible = false;
                VideoVisible = true;
                UnitVisible = false;
                MapPaneWidth = new GridLength(0);
                VideoPaneWidth = new GridLength(1, GridUnitType.Star);
                SplitVisible = false;
                break;
            default:
                MapVisible = true;
                VideoVisible = true;
                UnitVisible = false;
                MapPaneWidth = new GridLength(1, GridUnitType.Star);
                VideoPaneWidth = new GridLength(1, GridUnitType.Star);
                SplitVisible = true;
                break;
            case OperateLayoutMode.UnitFocus:
                MapVisible = false;
                VideoVisible = false;
                UnitVisible = true;
                MapPaneWidth = new GridLength(1, GridUnitType.Star);
                VideoPaneWidth = new GridLength(0);
                SplitVisible = false;
                break;
        }

        Map.SetMapVisible(MapVisible);
        OnPropertyChanged(nameof(LayoutLabel));
    }

    private void ApplySelection()
    {
        var selectedIds = _selection.SelectedUnitIds;
        if (selectedIds.Count > 1)
        {
            ApplyMultipleSelection(selectedIds);
            return;
        }
        HasMultipleSelectedUnits = false;
        var current = _selection.Current;
        VehicleRecord? vehicle = null;
        if (current.Kind == SelectionKind.Vehicle && !string.IsNullOrWhiteSpace(current.Id))
        {
            _vehicles.TryGet(current.Id, out vehicle);
        }
        else if (current.Kind == SelectionKind.Runtime && !string.IsNullOrWhiteSpace(current.Id))
        {
            vehicle = _vehicles.Items.FirstOrDefault(item =>
                string.Equals(item.LogosInstanceId, current.Id, StringComparison.Ordinal) ||
                item.ConnectionIds.Contains(current.Id, StringComparer.Ordinal));
        }

        if (vehicle is null)
        {
            ClearSelection();
            return;
        }

        vehicle = (_reconciliation?.ProjectVehicles(_vehicles.Items) ?? _vehicles.Items)
                      .FirstOrDefault(item => string.Equals(item.Id, vehicle.Id, StringComparison.Ordinal))
                  ?? vehicle;

        var telemetrySourceId = _reconciliation?.ResolveTelemetrySource(vehicle.Id) ?? vehicle.Id;
        var diagnosticsSourceId = _reconciliation?.ResolveDiagnosticsSource(vehicle.Id) ?? vehicle.Id;

        var telemetry = _telemetry.Items
            .Where(item => item.VehicleId == telemetrySourceId)
            .OrderByDescending(item => StateRank(item.State))
            .ThenByDescending(item => item.ObservedAt)
            .FirstOrDefault();
        var links = _links.Items
            .Where(item => vehicle.ConnectionIds.Contains(item.ConnectionId, StringComparer.Ordinal))
            .ToArray();
        var diagnostics = _diagnostics?.Items
            .Where(item => item.VehicleId == diagnosticsSourceId)
            .OrderByDescending(item => item.ObservedAt)
            .FirstOrDefault();

        SelectionTitle = vehicle.Name;
        SelectionSubtitle = $"{vehicle.Domain} · {vehicle.VehicleClass}";
        ConnectionText = vehicle.ConnectionIds.Count == 0
            ? "None"
            : string.Join(", ", vehicle.ConnectionIds);
        StateText = vehicle.State.ToString();
        ReadinessText = telemetry?.Readiness ?? vehicle.Readiness;
        LifecycleText = vehicle.Lifecycle;
        ArmStateText = telemetry is null
            ? vehicle.ArmState
            : telemetry.Armed ? "Armed" : "Disarmed";

        if (telemetry is null)
        {
            TelemetryHealthText = "No telemetry received";
            FlightModeText = "-";
            LandedStateText = "-";
            PositionText = "-";
            LocalPositionText = "-";
            AltitudeText = "-";
            VelocityText = "-";
            HeadingText = "-";
            LastTelemetryText = "-";
            DataStatus = vehicle.State switch
            {
                AvailabilityState.Online => "WAITING FOR TELEMETRY",
                AvailabilityState.Degraded => "DEGRADED",
                AvailabilityState.Stale => "STALE",
                _ => vehicle.State.ToString().ToUpperInvariant()
            };
        }
        else
        {
            TelemetryHealthText = telemetry.IsStale
                ? $"{telemetry.Health} · stale"
                : telemetry.Health;
            FlightModeText = telemetry.AirframeMode;
            LandedStateText = telemetry.LandedState;
            PositionText = FormatGlobalPosition(telemetry);
            LocalPositionText = FormatLocalPosition(telemetry);
            AltitudeText = FormatAltitude(telemetry);
            VelocityText = FormatVelocity(telemetry);
            HeadingText = telemetry.HeadingDegrees is null
                ? "-"
                : $"{telemetry.HeadingDegrees.Value:0.0}°";
            LastTelemetryText = telemetry.ObservedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            DataStatus = telemetry.State switch
            {
                AvailabilityState.Online => "LIVE TELEMETRY",
                AvailabilityState.Degraded => "DEGRADED TELEMETRY",
                AvailabilityState.Stale => "STALE TELEMETRY",
                _ => telemetry.State.ToString().ToUpperInvariant()
            };
        }

        var connectedLinks = links.Count(item => item.Connected && !item.IsStale);
        LinkText = links.Length == 0
            ? "No links reported"
            : $"{connectedLinks}/{links.Length} connected";
        ApplyDiagnostics(diagnostics);
    }

    private void ApplyMultipleSelection(IReadOnlyList<string> selectedIds)
    {
        HasMultipleSelectedUnits = true;
        var diagnosticSourceIds = selectedIds
            .Select(id => _reconciliation?.ResolveDiagnosticsSource(id) ?? id)
            .ToHashSet(StringComparer.Ordinal);
        var snapshots = (_diagnostics?.Items.AsEnumerable() ?? Enumerable.Empty<VehicleDiagnosticsSnapshot>())
            .Where(item => diagnosticSourceIds.Contains(item.VehicleId))
            .GroupBy(item => item.VehicleId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.ObservedAt).First())
            .ToArray();
        SelectionTitle = $"{selectedIds.Count} units selected";
        SelectionSubtitle = "Select one unit to inspect detailed vehicle diagnostics.";
        OverallStatusText = snapshots.Any(item => item.OverallStatus == VehicleDiagnosticStatus.Blocked) ? "Blocked" :
            snapshots.Length == selectedIds.Count && snapshots.All(item => item.OverallStatus == VehicleDiagnosticStatus.Ready) ? "Ready" : "Mixed";
        OverallStatusBrush = OverallStatusText == "Blocked" ? "#F05252" : OverallStatusText == "Ready" ? "#32D583" : "#F5C451";
        ArmReadinessText = "? See unit";
        NavigationReadinessText = "? See unit";
        TelemetryStatusText = snapshots.Length == selectedIds.Count ? "✓ Current" : "! Partial";
        ArmReadinessDetail = "Review each selected unit for arm readiness.";
        NavigationReadinessDetail = "Review each selected unit for navigation readiness.";
        TelemetryStatusDetail = $"{snapshots.Length} of {selectedIds.Count} selected units are reporting.";
        ArmReadinessBrush = NavigationReadinessBrush = TelemetryStatusBrush = "#F5C451";
        BackendIdentityText = "Multiple units";
        DiagnosticChecks = [];
        DiagnosticBlockers = snapshots.SelectMany(item => item.Blockers).ToArray();
        RecentDiagnosticMessages = [];
        HasDiagnostics = snapshots.Length > 0;
        OnPropertyChanged(nameof(HasDiagnosticBlockers));
        OnPropertyChanged(nameof(HasRecentDiagnosticMessages));
    }

    private void ApplyDiagnostics(VehicleDiagnosticsSnapshot? diagnostics)
    {
        HasDiagnostics = diagnostics is not null;
        if (diagnostics is null)
        {
            OverallStatusText = "Unknown";
            OverallStatusBrush = "#8A96A8";
            ArmReadinessText = "? Unknown";
            NavigationReadinessText = "? Unknown";
            TelemetryStatusText = "? Unknown";
            ArmReadinessDetail = NavigationReadinessDetail = TelemetryStatusDetail = "No diagnostic snapshot is available.";
            ArmReadinessBrush = NavigationReadinessBrush = TelemetryStatusBrush = "#8A96A8";
            BackendIdentityText = "Not reported";
            DiagnosticChecks = [];
            DiagnosticBlockers = [];
            RecentDiagnosticMessages = [];
        }
        else
        {
            OverallStatusText = diagnostics.OverallStatus.ToString();
            OverallStatusBrush = StatusBrush(diagnostics.OverallStatus);
            ArmReadinessText = StatusDisplay(diagnostics.ArmReadiness);
            NavigationReadinessText = StatusDisplay(diagnostics.NavigationReadiness);
            TelemetryStatusText = StatusDisplay(diagnostics.TelemetryStatus);
            ArmReadinessDetail = diagnostics.ArmReadinessDetail;
            NavigationReadinessDetail = diagnostics.NavigationReadinessDetail;
            TelemetryStatusDetail = diagnostics.TelemetryDetail;
            ArmReadinessBrush = StatusBrush(diagnostics.ArmReadiness);
            NavigationReadinessBrush = StatusBrush(diagnostics.NavigationReadiness);
            TelemetryStatusBrush = StatusBrush(diagnostics.TelemetryStatus);
            BackendIdentityText = diagnostics.SystemId is byte systemId
                ? $"{diagnostics.Backend} · MAVLink system {systemId}, component {diagnostics.ComponentId} · {diagnostics.Version} · {diagnostics.Mode}"
                : diagnostics.Backend;
            DiagnosticChecks = diagnostics.Checks
                .OrderBy(item => item.Category, StringComparer.Ordinal)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToArray();
            DiagnosticBlockers = diagnostics.Checks
                .Where(item => item.State is VehicleDiagnosticCheckState.Failed or VehicleDiagnosticCheckState.Warning)
                .OrderBy(item => item.State == VehicleDiagnosticCheckState.Failed ? 0 : 1)
                .ToArray();
            RecentDiagnosticMessages = diagnostics.RecentMessages;
        }
        OnPropertyChanged(nameof(HasDiagnosticBlockers));
        OnPropertyChanged(nameof(HasRecentDiagnosticMessages));
    }

    private static string StatusBrush(VehicleDiagnosticStatus status) => status switch
    {
        VehicleDiagnosticStatus.Ready => "#32D583",
        VehicleDiagnosticStatus.Limited => "#F5C451",
        VehicleDiagnosticStatus.Blocked => "#F05252",
        VehicleDiagnosticStatus.Stale => "#FFFFFF",
        _ => "#8A96A8"
    };

    private static string StatusDisplay(VehicleDiagnosticStatus status) =>
        $"{StatusSymbol(status)} {status}";

    private static string StatusSymbol(VehicleDiagnosticStatus status) => status switch
    {
        VehicleDiagnosticStatus.Ready => "\u2713",
        VehicleDiagnosticStatus.Limited => "!",
        VehicleDiagnosticStatus.Blocked => "\u2715",
        VehicleDiagnosticStatus.Stale => "!",
        VehicleDiagnosticStatus.Offline => "\u2715",
        _ => "?"
    };

    private void ClearSelection()
    {
        HasMultipleSelectedUnits = false;
        SelectionTitle = "No vehicle selected";
        SelectionSubtitle = "Select a discovered vehicle from the fleet panel.";
        ConnectionText = "-";
        StateText = "-";
        ReadinessText = "-";
        LifecycleText = "-";
        ArmStateText = "-";
        TelemetryHealthText = "-";
        FlightModeText = "-";
        LandedStateText = "-";
        PositionText = "-";
        LocalPositionText = "-";
        AltitudeText = "-";
        VelocityText = "-";
        HeadingText = "-";
        LinkText = "-";
        LastTelemetryText = "-";
        DataStatus = "NO VEHICLE SELECTED";
        ApplyDiagnostics(null);
    }

    private static string FormatGlobalPosition(VehicleTelemetryRecord telemetry)
        => telemetry.LatitudeDegrees is null || telemetry.LongitudeDegrees is null
            ? "-"
            : $"{telemetry.LatitudeDegrees.Value:0.000000}, {telemetry.LongitudeDegrees.Value:0.000000}";

    private string FormatLocalPosition(VehicleTelemetryRecord telemetry)
        => telemetry.LocalNorthMetres is null || telemetry.LocalEastMetres is null
            ? "-"
            : $"N {UnitFormatting.Distance(telemetry.LocalNorthMetres.Value, _unitSettings?.Current.HorizontalDistance ?? DistanceUnit.Meters)} · " +
              $"E {UnitFormatting.Distance(telemetry.LocalEastMetres.Value, _unitSettings?.Current.HorizontalDistance ?? DistanceUnit.Meters)} · " +
              $"D {UnitFormatting.Distance(telemetry.LocalDownMetres ?? 0, _unitSettings?.Current.VerticalDistance ?? DistanceUnit.Meters)}";

    private string FormatAltitude(VehicleTelemetryRecord telemetry)
    {
        var values = new List<string>();
        if (telemetry.AltitudeAglMetres is not null)
        {
            values.Add($"AGL {UnitFormatting.Distance(telemetry.AltitudeAglMetres.Value, _unitSettings?.Current.VerticalDistance ?? DistanceUnit.Meters)}");
        }

        if (telemetry.AltitudeMslMetres is not null)
        {
            values.Add($"MSL {UnitFormatting.Distance(telemetry.AltitudeMslMetres.Value, _unitSettings?.Current.VerticalDistance ?? DistanceUnit.Meters)}");
        }

        return values.Count == 0 ? "-" : string.Join(" · ", values);
    }

    private string FormatVelocity(VehicleTelemetryRecord telemetry)
    {
        if (telemetry.VelocityNorthMetresPerSecond is null ||
            telemetry.VelocityEastMetresPerSecond is null)
        {
            return "-";
        }

        var north = telemetry.VelocityNorthMetresPerSecond.Value;
        var east = telemetry.VelocityEastMetresPerSecond.Value;
        var down = telemetry.VelocityDownMetresPerSecond ?? 0;
        var groundSpeed = Math.Sqrt((north * north) + (east * east));
        var unit = _unitSettings?.Current.Speed ?? SpeedUnit.MetersPerSecond;
        return $"{UnitFormatting.Speed(groundSpeed, unit)} ground · {UnitFormatting.Speed(down, unit)} down";
    }

    private void OnUnitSettingsChanged(object? sender, EventArgs e)
        => ApplySelection();

    private static int StateRank(AvailabilityState state)
        => state switch
        {
            AvailabilityState.Online => 4,
            AvailabilityState.Degraded => 3,
            AvailabilityState.Stale => 2,
            AvailabilityState.Offline => 1,
            _ => 0
        };
}
