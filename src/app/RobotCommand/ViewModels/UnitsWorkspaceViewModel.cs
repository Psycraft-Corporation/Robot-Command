using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.Rendering.Meshes;
using RobotCommand.Services.Mavlink;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed record UnitsWorkspaceSection(string Id, string Title, object Content);

public sealed class UnitsWorkspaceViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private UnitsWorkspaceSection _selectedSection;

    public UnitsWorkspaceViewModel(UnitsLibraryViewModel units, GhostProfilesViewModel ghosts, ILocalizationService localization)
    {
        _localization = localization;
        Units = units;
        Ghosts = ghosts;
        Sections = new ObservableCollection<UnitsWorkspaceSection>
        {
            new("units", localization.Get("Units"), units),
            new("ghosts", localization.Get("Ghosts"), ghosts)
        };
        _selectedSection = Sections[1];
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    public GhostProfilesViewModel Ghosts { get; }
    public UnitsLibraryViewModel Units { get; }
    public ObservableCollection<UnitsWorkspaceSection> Sections { get; }
    public UnitsWorkspaceSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null || !SetProperty(ref _selectedSection, value)) return;
            OnPropertyChanged(nameof(CurrentSection));
        }
    }

    public object CurrentSection => SelectedSection.Content;

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var selectedId = _selectedSection.Id;
        for (var index = 0; index < Sections.Count; index++)
        {
            var section = Sections[index];
            var key = section.Id switch
            {
                "units" => "Units",
                "ghosts" => "Ghosts",
                _ => section.Id
            };
            Sections[index] = section with { Title = _localization.Get(key) };
        }

        _selectedSection = Sections.First(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal));
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(SelectedSection));
        OnPropertyChanged(nameof(CurrentSection));
    }
}

public sealed class UnitsLibraryViewModel : ObservableObject
{
    private readonly IUnitAssociationWorkflow _workflow;
    private readonly IEntityStore<string, ConnectionRecord> _connections;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, CameraSourceRecord> _cameras;
    private readonly ILocalizationService _localization;
    private readonly IConnectionManagementWorkflow? _connectionWorkflow;
    private readonly IPx4ParameterService? _parameterService;
    private readonly IPx4ParameterProfileStore? _parameterProfiles;
    private UnitDefinitionSnapshot? _selectedUnit;
    private string _unitName = string.Empty;
    private string _status = string.Empty;
    private bool _isEditing;
    private string? _editingId;
    private UnitParameterTarget? _selectedParameterTarget;
    private Px4ParameterProfile? _selectedParameterProfile;
    private string _parameterStatus = "Select a MAVLink unit to manage parameters.";
    private bool _parameterBusy;

    public UnitsLibraryViewModel(
        IUnitAssociationWorkflow workflow,
        IEntityStore<string, ConnectionRecord> connections,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, CameraSourceRecord> cameras,
        ILocalizationService localization,
        IConnectionManagementWorkflow? connectionWorkflow = null,
        IPx4ParameterService? parameterService = null,
        IPx4ParameterProfileStore? parameterProfiles = null)
    {
        _workflow = workflow;
        _connections = connections;
        _vehicles = vehicles;
        _cameras = cameras;
        _localization = localization;
        _connectionWorkflow = connectionWorkflow;
        _parameterService = parameterService;
        _parameterProfiles = parameterProfiles;
        Units = new ObservableCollection<UnitDefinitionSnapshot>(_workflow.Units);
        AvailableConnections = [];
        AvailableVehicles = [];
        AvailableCameras = [];
        ParameterTargets = [];
        ParameterProfiles = [];
        ParameterDiffs = [];
        NewUnitCommand = new RelayCommand(_ => BeginNew());
        EditUnitCommand = new RelayCommand(_ => BeginEdit(), _ => CanEdit);
        SaveUnitCommand = new AsyncRelayCommand(SaveAsync, () => IsEditing);
        CancelUnitCommand = new RelayCommand(_ => CancelEdit(), _ => IsEditing);
        DeleteUnitCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedUnit is not null && !IsEditing);
        DownloadParametersCommand = new AsyncRelayCommand(DownloadParametersAsync, CanManageParameters);
        CompareParametersCommand = new AsyncRelayCommand(CompareParametersAsync, CanCompareParameters);
        ApplyParametersCommand = new AsyncRelayCommand(ApplyParametersAsync, CanApplyParameters);
        _workflow.Changed += OnChanged;
        _localization.PropertyChanged += OnLocalizationChanged;
        if (_connectionWorkflow is not null)
            _connectionWorkflow.Changed += OnConnectionWorkflowChanged;
        ((INotifyCollectionChanged)_connections.Items).CollectionChanged += OnSourcesChanged;
        ((INotifyCollectionChanged)_vehicles.Items).CollectionChanged += OnSourcesChanged;
        ((INotifyCollectionChanged)_cameras.Items).CollectionChanged += OnSourcesChanged;
        SelectedUnit = Units.FirstOrDefault();
        _ = RefreshParameterProfilesAsync();
    }

    public ObservableCollection<UnitDefinitionSnapshot> Units { get; }
    public ObservableCollection<UnitConnectionOption> AvailableConnections { get; }
    public ObservableCollection<UnitSourceOption> AvailableVehicles { get; }
    public ObservableCollection<UnitSourceOption> AvailableCameras { get; }
    public ObservableCollection<UnitParameterTarget> ParameterTargets { get; }
    public ObservableCollection<Px4ParameterProfile> ParameterProfiles { get; }
    public ObservableCollection<Px4ParameterDiff> ParameterDiffs { get; }
    public UnitDefinitionSnapshot? SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (!SetProperty(ref _selectedUnit, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(SelectedConnectionSummary));
            RefreshParameterTargets();
            (EditUnitCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteUnitCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            if (!IsEditing) LoadDraft(value);
        }
    }
    public string UnitName { get => _unitName; set => SetProperty(ref _unitName, value); }
    public string SelectedConnectionSummary
    {
        get
        {
            if (SelectedUnit is null) return string.Empty;
            var connectionIds = (SelectedUnit.ConnectionIds ?? SelectedUnit.VehicleSources.Select(source => source.ConnectionId).Distinct(StringComparer.Ordinal).ToArray())
                .ToHashSet(StringComparer.Ordinal);
            var names = SavedConnections()
                .Where(connection => connectionIds.Contains(connection.ConnectionId))
                .Select(connection => connection.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return names.Length == 0 ? string.Empty : string.Join(", ", names);
        }
    }
    public bool IsEditing { get => _isEditing; private set { if (SetProperty(ref _isEditing, value)) { OnPropertyChanged(nameof(IsViewing)); OnPropertyChanged(nameof(IsMavlinkParameterPanel)); OnPropertyChanged(nameof(CanEdit)); (EditUnitCommand as RelayCommand)?.RaiseCanExecuteChanged(); (SaveUnitCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (CancelUnitCommand as RelayCommand)?.RaiseCanExecuteChanged(); (DeleteUnitCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } } }
    public bool IsViewing => !IsEditing && SelectedUnit is not null;
    public bool CanEdit => SelectedUnit is not null && !IsEditing;
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public UnitParameterTarget? SelectedParameterTarget
    {
        get => _selectedParameterTarget;
        set
        {
            if (!SetProperty(ref _selectedParameterTarget, value)) return;
            ParameterDiffs.Clear();
            OnPropertyChanged(nameof(ParameterTitle));
            RaiseParameterCommandStates();
        }
    }

    public Px4ParameterProfile? SelectedParameterProfile
    {
        get => _selectedParameterProfile;
        set
        {
            if (!SetProperty(ref _selectedParameterProfile, value)) return;
            ParameterDiffs.Clear();
            RaiseParameterCommandStates();
        }
    }

    public string ParameterStatus { get => _parameterStatus; private set => SetProperty(ref _parameterStatus, value); }
    public bool IsParameterBusy { get => _parameterBusy; private set { if (SetProperty(ref _parameterBusy, value)) RaiseParameterCommandStates(); } }
    public string ParameterTitle => SelectedParameterTarget?.Backend == ManagedMavlinkAutopilot.ArduPilot
        ? _localization.Get("ArduPilotParametersTitle")
        : _localization.Get("Px4ParametersTitle");
    public bool IsMavlinkParameterPanel => !IsEditing && SelectedUnit is not null && ParameterTargets.Count > 0;

    public ICommand NewUnitCommand { get; }
    public ICommand EditUnitCommand { get; }
    public ICommand SaveUnitCommand { get; }
    public ICommand CancelUnitCommand { get; }
    public ICommand DeleteUnitCommand { get; }
    public ICommand DownloadParametersCommand { get; }
    public ICommand CompareParametersCommand { get; }
    public ICommand ApplyParametersCommand { get; }
    public event EventHandler? OpenRequested;

    public void OpenExisting(string? unitId)
    {
        var unit = Units.FirstOrDefault(item => string.Equals(item.Id, unitId, StringComparison.Ordinal));
        if (unit is null) BeginNew();
        else BeginEdit(unit);
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public void OpenForVehicle(string connectionId, string vehicleId)
    {
        BeginNew();
        var source = AvailableVehicles.FirstOrDefault(item =>
            string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal) &&
            string.Equals(item.SourceId, vehicleId, StringComparison.Ordinal));
        if (source is not null)
        {
            source.IsSelected = true;
            var connection = AvailableConnections.FirstOrDefault(item =>
                string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal));
            if (connection is not null) connection.IsSelected = true;
            UnitName = source.Label;
        }
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BeginNew()
    {
        _editingId = null;
        SelectedUnit = null;
        LoadDraft(null);
        IsEditing = true;
        Status = string.Empty;
    }

    public void BeginEdit(UnitDefinitionSnapshot? unit = null)
    {
        if (unit is not null) SelectedUnit = unit;
        if (SelectedUnit is null) return;
        _editingId = SelectedUnit.Id;
        LoadDraft(SelectedUnit);
        IsEditing = true;
        Status = string.Empty;
    }

    private void LoadDraft(UnitDefinitionSnapshot? unit)
    {
        UnitName = unit?.DisplayName ?? string.Empty;
        RebuildOptions(unit);
    }

    private void RebuildOptions(UnitDefinitionSnapshot? unit)
    {
        AvailableVehicles.Clear();
        AvailableCameras.Clear();
        AvailableConnections.Clear();
        var selectedVehicles = unit?.VehicleSources.ToHashSet() ?? [];
        var selectedCameras = unit?.CameraSources.ToHashSet() ?? [];
        var selectedConnections = (unit?.ConnectionIds ?? selectedVehicles.Select(item => item.ConnectionId).Distinct(StringComparer.Ordinal).ToArray())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var connection in SavedConnections())
        {
            AddConnectionOption(new UnitConnectionOption(
                connection.ConnectionId,
                connection.Name,
                connection.Mode,
                connection.State,
                _vehicles.Items.Any(vehicle => vehicle.ConnectionIds.Contains(connection.ConnectionId, StringComparer.Ordinal)),
                selectedConnections.Contains(connection.ConnectionId)));
        }
        foreach (var vehicle in _vehicles.Items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            foreach (var connectionId in vehicle.ConnectionIds.Distinct(StringComparer.Ordinal))
                AddVehicleOption(new UnitSourceOption(UnitSourceKind.Vehicle, connectionId, vehicle.Id, vehicle.Name, vehicle.State != AvailabilityState.Offline, selectedVehicles.Contains(new UnitVehicleSourceBinding(connectionId, vehicle.Id))));
        foreach (var binding in selectedVehicles.Where(item => AvailableVehicles.All(option => option.SourceId != item.VehicleId || option.ConnectionId != item.ConnectionId)))
            AddVehicleOption(new UnitSourceOption(UnitSourceKind.Vehicle, binding.ConnectionId, binding.VehicleId, binding.VehicleId, false, true));
        foreach (var camera in _cameras.Items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            AddCameraOption(new UnitSourceOption(UnitSourceKind.Camera, camera.ConnectionId, camera.CameraSourceId, camera.Name, camera.State != AvailabilityState.Offline, selectedCameras.Contains(new UnitCameraSourceBinding(camera.ConnectionId, camera.CameraSourceId))));
        foreach (var binding in selectedCameras.Where(item => AvailableCameras.All(option => option.SourceId != item.CameraSourceId || option.ConnectionId != item.ConnectionId)))
            AddCameraOption(new UnitSourceOption(UnitSourceKind.Camera, binding.ConnectionId, binding.CameraSourceId, binding.CameraSourceId, false, true));
        RefreshParameterTargets();
    }

    private void AddConnectionOption(UnitConnectionOption option)
    {
        option.SelectionChanged += OnConnectionSelectionChanged;
        AvailableConnections.Add(option);
    }

    private UnitConnectionOption[] SavedConnections()
    {
        if (_connectionWorkflow is not null)
        {
            return _connectionWorkflow.Connections
                .Select(connection => new UnitConnectionOption(
                    connection.Id,
                    connection.Name,
                    ToConnectionMode(connection.Mode),
                    (AvailabilityState)(int)connection.State,
                    HasVehicleObservation(connection.Id),
                    false))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return _connections.Items
            .Where(item => item.Mode is not ConnectionMode.Ghost and not ConnectionMode.TeamObserver)
            .Select(connection => new UnitConnectionOption(
                connection.Id,
                connection.Name,
                connection.Mode,
                connection.State,
                HasVehicleObservation(connection.Id),
                false))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool HasVehicleObservation(string connectionId)
        => _vehicles.Items.Any(vehicle => vehicle.ConnectionIds.Contains(connectionId, StringComparer.Ordinal));

    private static ConnectionMode ToConnectionMode(ManagedConnectionMode mode)
        => mode switch
        {
            ManagedConnectionMode.FieldLink => ConnectionMode.FieldLink,
            ManagedConnectionMode.Mavlink => ConnectionMode.Mavlink,
            _ => ConnectionMode.Direct
        };

    private void AddVehicleOption(UnitSourceOption option)
    {
        AvailableVehicles.Add(option);
    }

    private void AddCameraOption(UnitSourceOption option)
    {
        AvailableCameras.Add(option);
    }

    private void OnConnectionSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is not UnitConnectionOption connection) return;
        foreach (var vehicle in AvailableVehicles.Where(item => item.ConnectionId == connection.ConnectionId))
            vehicle.IsSelected = connection.IsSelected;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var vehicles = AvailableVehicles.Where(item => item.IsSelected).Select(item => new UnitVehicleSourceBinding(item.ConnectionId, item.SourceId)).ToArray();
            var cameras = AvailableCameras.Where(item => item.IsSelected).Select(item => new UnitCameraSourceBinding(item.ConnectionId, item.SourceId)).ToArray();
            var connectionIds = AvailableConnections.Where(item => item.IsSelected).Select(item => item.ConnectionId).ToArray();
            var retainedAuthorities = SelectedUnit;
            var saved = await _workflow.SaveAsync(_editingId, new UnitDefinitionRequest(UnitName, vehicles, cameras,
                retainedAuthorities?.CommandAuthorityConnectionId,
                retainedAuthorities?.TelemetryAuthorityConnectionId,
                retainedAuthorities?.DiagnosticsAuthorityConnectionId,
                connectionIds), cancellationToken);
            Refresh(saved.Id);
            IsEditing = false;
            _editingId = null;
            Status = _localization.Get("UnitSaved");
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (SelectedUnit is null) return;
        try
        {
            var id = SelectedUnit.Id;
            await _workflow.DeleteAsync(id, cancellationToken);
            Refresh(null);
            Status = _localization.Get("UnitDeleted");
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    private void CancelEdit()
    {
        IsEditing = false;
        _editingId = null;
        LoadDraft(SelectedUnit);
        Status = string.Empty;
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => Refresh(SelectedUnit?.Id));
    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ParameterTitle)));
    private void OnConnectionWorkflowChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (!IsEditing) RebuildOptions(SelectedUnit); else RefreshParameterTargets(); });
    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.UIThread.Post(() => { if (!IsEditing) RebuildOptions(SelectedUnit); else RefreshParameterTargets(); });

    private void Refresh(string? selectedId)
    {
        Units.Clear();
        foreach (var unit in _workflow.Units) Units.Add(unit);
        SelectedUnit = Units.FirstOrDefault(item => item.Id == selectedId) ?? Units.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedConnectionSummary));
        RefreshParameterTargets();
    }

    private void RefreshParameterTargets()
    {
        var previous = SelectedParameterTarget?.Key;
        ParameterTargets.Clear();
        if (SelectedUnit is not null)
        {
            foreach (var binding in SelectedUnit.VehicleSources)
            {
                var connection = _connectionWorkflow?.Connections.FirstOrDefault(item => item.Id == binding.ConnectionId);
                if (connection is null || connection.Mode != ManagedConnectionMode.Mavlink || connection.Mavlink is null) continue;
                var vehicle = _vehicles.Items.FirstOrDefault(item => item.Id == binding.VehicleId && item.ConnectionIds.Contains(binding.ConnectionId, StringComparer.Ordinal));
                var name = vehicle?.Name ?? binding.VehicleId;
                ParameterTargets.Add(new UnitParameterTarget(SelectedUnit.Id, binding.ConnectionId, binding.VehicleId, name, connection.Mavlink.Autopilot));
            }
        }
        SelectedParameterTarget = ParameterTargets.FirstOrDefault(item => item.Key == previous) ?? ParameterTargets.FirstOrDefault();
        ParameterStatus = ParameterTargets.Count == 0 ? "Select a unit with a PX4 or ArduPilot connection." : ParameterStatus;
        OnPropertyChanged(nameof(IsMavlinkParameterPanel));
        RaiseParameterCommandStates();
    }

    private async Task RefreshParameterProfilesAsync()
    {
        if (_parameterProfiles is null) return;
        try
        {
            await _parameterProfiles.RefreshAsync();
            ReplaceParameterProfiles();
        }
        catch (Exception exception) { ParameterStatus = $"Could not load parameter profiles: {exception.Message}"; }
    }

    private void ReplaceParameterProfiles()
    {
        if (_parameterProfiles is null) return;
        var selectedId = SelectedParameterProfile?.Id;
        ParameterProfiles.Clear();
        foreach (var profile in _parameterProfiles.Profiles.OrderByDescending(item => item.CreatedAt)) ParameterProfiles.Add(profile);
        SelectedParameterProfile = ParameterProfiles.FirstOrDefault(item => item.Id == selectedId) ?? ParameterProfiles.FirstOrDefault();
    }

    private bool CanManageParameters() => !IsParameterBusy && SelectedParameterTarget is not null && _parameterService is not null && _parameterProfiles is not null;
    private bool CanCompareParameters() => CanManageParameters() && SelectedParameterProfile is not null;
    private bool CanApplyParameters() => CanCompareParameters() && ParameterDiffs.Any(item => item.Kind == Px4ParameterChangeKind.Changed);

    private async Task DownloadParametersAsync(CancellationToken cancellationToken)
    {
        if (!CanManageParameters() || SelectedParameterTarget is null || _parameterService is null || _parameterProfiles is null) return;
        IsParameterBusy = true;
        try
        {
            var target = SelectedParameterTarget;
            var document = await _parameterService.DownloadAsync(target.ConnectionId, target.VehicleId, cancellationToken);
            var profile = await _parameterProfiles.SaveAsync($"{target.Name}-download", document, cancellationToken: cancellationToken);
            await _parameterProfiles.RefreshAsync(cancellationToken);
            ReplaceParameterProfiles();
            SelectedParameterProfile = ParameterProfiles.FirstOrDefault(item => item.Id == profile.Id) ?? profile;
            ParameterStatus = $"Downloaded {document.Parameters.Count} parameters.";
            await CompareParametersCoreAsync(cancellationToken);
        }
        catch (Exception exception) { ParameterStatus = $"Parameter download failed: {exception.Message}"; }
        finally { IsParameterBusy = false; }
    }

    private async Task CompareParametersAsync(CancellationToken cancellationToken)
    {
        if (!CanCompareParameters()) return;
        IsParameterBusy = true;
        try { await CompareParametersCoreAsync(cancellationToken); }
        catch (Exception exception) { ParameterStatus = $"Parameter comparison failed: {exception.Message}"; }
        finally { IsParameterBusy = false; }
    }

    private async Task CompareParametersCoreAsync(CancellationToken cancellationToken)
    {
        if (SelectedParameterTarget is null || SelectedParameterProfile is null || _parameterService is null) return;
        var diffs = await _parameterService.CompareAsync(SelectedParameterTarget.ConnectionId, SelectedParameterTarget.VehicleId, SelectedParameterProfile, cancellationToken);
        ParameterDiffs.Clear();
        foreach (var diff in diffs) ParameterDiffs.Add(diff);
        ParameterStatus = $"{diffs.Count(item => item.Kind == Px4ParameterChangeKind.Changed)} changes.";
    }

    private async Task ApplyParametersAsync(CancellationToken cancellationToken)
    {
        if (!CanApplyParameters() || SelectedParameterTarget is null || SelectedParameterProfile is null || _parameterService is null) return;
        IsParameterBusy = true;
        try
        {
            var result = await _parameterService.ApplyAsync(SelectedParameterTarget.ConnectionId, SelectedParameterTarget.VehicleId, SelectedParameterProfile, cancellationToken);
            ParameterStatus = result.BackupProfile is { } backup ? $"Applied {result.Applied}; backup {backup.Name}." : string.Join(" ", result.Messages);
            await CompareParametersCoreAsync(cancellationToken);
        }
        catch (Exception exception) { ParameterStatus = $"Parameter apply failed: {exception.Message}"; }
        finally { IsParameterBusy = false; }
    }

    public async Task ImportParameterProfileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_parameterProfiles is null) return;
        try { var profile = await _parameterProfiles.ImportAsync(path, cancellationToken: cancellationToken); ReplaceParameterProfiles(); SelectedParameterProfile = ParameterProfiles.FirstOrDefault(item => item.Id == profile.Id) ?? profile; ParameterStatus = "Profile imported."; }
        catch (Exception exception) { ParameterStatus = $"Parameter import failed: {exception.Message}"; }
    }

    public async Task ExportSelectedParameterProfileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_parameterProfiles is null || SelectedParameterProfile is null) return;
        try { await _parameterProfiles.ExportAsync(SelectedParameterProfile.Id, path, cancellationToken); ParameterStatus = "Profile exported."; }
        catch (Exception exception) { ParameterStatus = $"Parameter export failed: {exception.Message}"; }
    }

    private void RaiseParameterCommandStates()
    {
        foreach (var command in new[] { DownloadParametersCommand, CompareParametersCommand, ApplyParametersCommand })
            if (command is AsyncRelayCommand async) async.RaiseCanExecuteChanged();
    }
}

public sealed record UnitParameterTarget(string UnitId, string ConnectionId, string VehicleId, string Name, ManagedMavlinkAutopilot Backend)
{
    public string Key => $"{ConnectionId}:{VehicleId}";
    public string DisplayName => $"{Name} ({Backend})";
}

public sealed class UnitSourceOption : ObservableObject
{
    private bool _isSelected;
    public UnitSourceOption(UnitSourceKind kind, string connectionId, string sourceId, string label, bool isAvailable, bool isSelected)
    { Kind = kind; ConnectionId = connectionId; SourceId = sourceId; Label = label; IsAvailable = isAvailable; _isSelected = isSelected; }
    public event EventHandler? SelectionChanged;
    public UnitSourceKind Kind { get; }
    public string ConnectionId { get; }
    public string SourceId { get; }
    public string Label { get; }
    public bool IsAvailable { get; }
    public string Detail => $"{ConnectionId} · {SourceId}" + (IsAvailable ? string.Empty : " · unavailable");
    public bool IsSelected { get => _isSelected; set { if (SetProperty(ref _isSelected, value)) SelectionChanged?.Invoke(this, EventArgs.Empty); } }
}

public sealed class UnitConnectionOption : ObservableObject
{
    private bool _isSelected;

    public UnitConnectionOption(
        string connectionId,
        string name,
        ConnectionMode mode,
        AvailabilityState state,
        bool hasVehicleObservation,
        bool isSelected)
    {
        ConnectionId = connectionId;
        Name = name;
        Mode = mode;
        State = state;
        HasVehicleObservation = hasVehicleObservation;
        _isSelected = isSelected;
    }

    public event EventHandler? SelectionChanged;
    public string ConnectionId { get; }
    public string Name { get; }
    public ConnectionMode Mode { get; }
    public AvailabilityState State { get; }
    public bool HasVehicleObservation { get; }
    public string Detail => $"{Mode} · {State}" +
        (HasVehicleObservation ? string.Empty : " · no vehicle discovered");
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class GhostProfilesViewModel : ObservableObject, IDisposable
{
    private readonly IGhostProfileWorkflow _profiles;
    private readonly IGhostProfileAssetWorkflow _assets;
    private readonly ILocalizationService _localization;
    private GhostProfileSnapshot? _selectedProfile;
    private string _profileName = string.Empty;
    private double _maximumSpeed;
    private double _climbRate;
    private double _descentRate;
    private double _horizontalAcceleration;
    private double _verticalAcceleration;
    private double _maximumYawRate;
    private double _altitudeLimit;
    private double _endurance;
    private string _status = string.Empty;
    private bool _isEditing;
    private string? _editingProfileId;
    private Bitmap? _previewImage;
    private MeshAssetSnapshot? _previewMesh;
    private string _previewError = string.Empty;
    private CancellationTokenSource? _previewCancellation;

    public GhostProfilesViewModel(IGhostProfileWorkflow profiles, IGhostProfileAssetWorkflow assets, ILocalizationService localization)
    {
        _profiles = profiles;
        _assets = assets;
        _localization = localization;
        Profiles = new ObservableCollection<GhostProfileSnapshot>(_profiles.Profiles);
        _selectedProfile = Profiles.FirstOrDefault();
        NewProfileCommand = new RelayCommand(_ => BeginNewProfile());
        EditProfileCommand = new RelayCommand(_ => BeginEdit(), _ => CanEditProfile);
        SaveProfileCommand = new AsyncRelayCommand(SaveAsync, () => IsEditing);
        CancelEditCommand = new RelayCommand(_ => CancelEdit());
        DeleteProfileCommand = new AsyncRelayCommand(DeleteAsync, () => CanDeleteProfile);
        RemoveVisualCommand = new AsyncRelayCommand(RemoveVisualAsync, () => CanRemoveVisual);
        _profiles.Changed += OnProfilesChanged;
        _assets.Changed += OnAssetsChanged;
        _localization.PropertyChanged += OnLocalizationChanged;
        LoadEditor(_selectedProfile);
        _ = LoadPreviewAsync(_selectedProfile);
    }

    public ObservableCollection<GhostProfileSnapshot> Profiles { get; }
    public GhostProfileSnapshot? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            OnPropertyChanged(nameof(SelectedSimulation));
            OnPropertyChanged(nameof(VisualAssetSummary));
            OnPropertyChanged(nameof(HasVisualAsset));
            OnPropertyChanged(nameof(CanEditProfile));
            OnPropertyChanged(nameof(CanDeleteProfile));
            (EditProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RemoveVisualCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            if (!IsEditing) LoadEditor(value);
            _ = LoadPreviewAsync(value);
        }
    }

    public GhostSimulationStats? SelectedSimulation => SelectedProfile?.Simulation;
    public string ProfileName { get => _profileName; set => SetProperty(ref _profileName, value); }
    public double MaximumSpeed { get => _maximumSpeed; set => SetProperty(ref _maximumSpeed, value); }
    public double ClimbRate { get => _climbRate; set => SetProperty(ref _climbRate, value); }
    public double DescentRate { get => _descentRate; set => SetProperty(ref _descentRate, value); }
    public double HorizontalAcceleration { get => _horizontalAcceleration; set => SetProperty(ref _horizontalAcceleration, value); }
    public double VerticalAcceleration { get => _verticalAcceleration; set => SetProperty(ref _verticalAcceleration, value); }
    public double MaximumYawRate { get => _maximumYawRate; set => SetProperty(ref _maximumYawRate, value); }
    public double AltitudeLimit { get => _altitudeLimit; set => SetProperty(ref _altitudeLimit, value); }
    public double Endurance { get => _endurance; set => SetProperty(ref _endurance, value); }
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool IsEditing { get => _isEditing; private set { if (SetProperty(ref _isEditing, value)) { OnPropertyChanged(nameof(IsViewingProfile)); OnPropertyChanged(nameof(CanUploadVisual)); OnPropertyChanged(nameof(CanRemoveVisual)); (RemoveVisualCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } } }
    public bool IsViewingProfile => !IsEditing;
    public bool IsEditingExisting => IsEditing && _editingProfileId is not null;
    public bool IsBuiltIn => SelectedProfile?.IsBuiltIn == true;
    public bool CanEditProfile => SelectedProfile is { IsBuiltIn: false, IsEditable: true };
    public bool CanDeleteProfile => CanEditProfile;
    public bool CanUploadVisual => SelectedProfile is { IsBuiltIn: false, IsEditable: true } && !IsEditing;
    public bool CanRemoveVisual => CanUploadVisual && SelectedProfile?.Asset is not null;
    public bool HasVisualAsset => SelectedProfile?.Asset is not null;
    public string VisualAssetSummary => SelectedProfile?.Asset is { } asset
        ? asset.Kind == GhostProfileAssetKind.Mesh
            ? $"{asset.FileName} · {asset.Kind} · {asset.VertexCount ?? 0} vertices · {asset.TriangleCount ?? 0} triangles"
            : $"{asset.FileName} · {asset.Kind} · {asset.Width}×{asset.Height} · {asset.ByteLength / 1024d:0.#} KB"
        : _localization.Get("VisualAssetNone");
    public Bitmap? PreviewImage { get => _previewImage; private set { _previewImage?.Dispose(); if (SetProperty(ref _previewImage, value)) OnPropertyChanged(nameof(HasImagePreview)); } }
    public MeshAssetSnapshot? PreviewMesh { get => _previewMesh; private set { if (SetProperty(ref _previewMesh, value)) OnPropertyChanged(nameof(HasMeshPreview)); } }
    public bool HasImagePreview => PreviewImage is not null;
    public bool HasMeshPreview => PreviewMesh is not null;
    public string PreviewError { get => _previewError; private set { if (SetProperty(ref _previewError, value)) OnPropertyChanged(nameof(HasPreviewError)); } }
    public bool HasPreviewError => !string.IsNullOrWhiteSpace(PreviewError);
    public static string VehicleType => "Multicopter";

    public ICommand NewProfileCommand { get; }
    public ICommand EditProfileCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand CancelEditCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand RemoveVisualCommand { get; }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(VisualAssetSummary));

    private void BeginNewProfile()
    {
        _editingProfileId = null;
        ProfileName = $"Ghost profile {Profiles.Count}";
        LoadEditor(GhostProfileDefaults.Dracula);
        IsEditing = true;
        Status = string.Empty;
        OnPropertyChanged(nameof(IsEditingExisting));
        (SaveProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void BeginEdit()
    {
        if (!CanEditProfile || SelectedProfile is null) return;
        _editingProfileId = SelectedProfile.Id;
        LoadEditor(SelectedProfile);
        IsEditing = true;
        Status = string.Empty;
        OnPropertyChanged(nameof(IsEditingExisting));
        (SaveProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var simulation = new GhostSimulationStats(MaximumSpeed, ClimbRate, DescentRate, HorizontalAcceleration, VerticalAcceleration, MaximumYawRate, AltitudeLimit, Endurance);
            GhostProfileSnapshot saved;
            if (_editingProfileId is null)
                saved = await _profiles.CreateAsync(new GhostProfileCreateRequest(ProfileName, simulation), cancellationToken);
            else
                saved = await _profiles.UpdateAsync(_editingProfileId, new GhostProfileUpdateRequest(ProfileName, simulation), cancellationToken);
            RefreshProfiles(saved.Id);
            IsEditing = false;
            _editingProfileId = null;
            Status = _localization.Get("ProfileSaved");
            OnPropertyChanged(nameof(IsEditingExisting));
            (SaveProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (!CanDeleteProfile || SelectedProfile is null) return;
        try
        {
            var deletedId = SelectedProfile.Id;
            await _profiles.DeleteAsync(deletedId, cancellationToken);
            RefreshProfiles(GhostProfileDefaults.Dracula.Id);
            Status = _localization.Get("ProfileDeleted");
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    private void CancelEdit()
    {
        IsEditing = false;
        _editingProfileId = null;
        LoadEditor(SelectedProfile);
        Status = string.Empty;
        OnPropertyChanged(nameof(IsEditingExisting));
        (SaveProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var selectedId = SelectedProfile?.Id ?? GhostProfileDefaults.Dracula.Id;
            RefreshProfiles(selectedId);
        });
    }

    private void OnAssetsChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var selectedId = SelectedProfile?.Id ?? GhostProfileDefaults.Dracula.Id;
            RefreshProfiles(selectedId);
            OnPropertyChanged(nameof(VisualAssetSummary));
            OnPropertyChanged(nameof(HasVisualAsset));
            (RemoveVisualCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            _ = LoadPreviewAsync(SelectedProfile);
        });
    }

    private void RefreshProfiles(string selectedId)
    {
        Profiles.Clear();
        foreach (var profile in _profiles.Profiles) Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(profile => string.Equals(profile.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(IsBuiltIn));
        OnPropertyChanged(nameof(CanUploadVisual));
        OnPropertyChanged(nameof(CanRemoveVisual));
        OnPropertyChanged(nameof(VisualAssetSummary));
    }

    private void LoadEditor(GhostProfileSnapshot? profile)
    {
        var source = profile ?? GhostProfileDefaults.Dracula;
        ProfileName = source.Name;
        MaximumSpeed = source.Simulation.MaximumHorizontalSpeedMetresPerSecond;
        ClimbRate = source.Simulation.MaximumClimbRateMetresPerSecond;
        DescentRate = source.Simulation.MaximumDescentRateMetresPerSecond;
        HorizontalAcceleration = source.Simulation.HorizontalAccelerationMetresPerSecondSquared;
        VerticalAcceleration = source.Simulation.VerticalAccelerationMetresPerSecondSquared;
        MaximumYawRate = source.Simulation.MaximumYawRateDegreesPerSecond;
        AltitudeLimit = source.Simulation.MaximumAltitudeAglMetres;
        Endurance = source.Simulation.NominalEnduranceMinutes;
    }

    public async Task UploadVisualAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!CanUploadVisual || SelectedProfile is null) return;
        try
        {
            var profileId = SelectedProfile.Id;
            await _assets.ImportAsync(profileId, sourcePath, cancellationToken);
            // Refresh explicitly as well as through the workflow event. The
            // event is intentionally dispatched asynchronously, so relying on
            // it alone can leave the preview bound to the previous snapshot.
            RefreshProfiles(profileId);
            _ = LoadPreviewAsync(SelectedProfile);
            Status = _localization.Get("VisualImported");
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    private async Task RemoveVisualAsync(CancellationToken cancellationToken)
    {
        if (!CanRemoveVisual || SelectedProfile is null) return;
        try
        {
            var profileId = SelectedProfile.Id;
            await _assets.RemoveAsync(profileId, cancellationToken);
            RefreshProfiles(profileId);
            _ = LoadPreviewAsync(SelectedProfile);
            Status = _localization.Get("VisualRemoved");
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    private async Task LoadPreviewAsync(GhostProfileSnapshot? profile)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var token = _previewCancellation.Token;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            PreviewImage = null;
            PreviewMesh = null;
            PreviewError = string.Empty;
        });
        if (profile?.Asset is not { } asset) return;
        try
        {
            var handle = await _assets.OpenReadAsync(profile.Id, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (handle is null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => PreviewError = _localization.Get("VisualUnavailable"));
                return;
            }
            if (asset.Kind == GhostProfileAssetKind.Image)
            {
                // Avalonia image objects are thread-affine. Read the bytes off the
                // UI thread, then create and publish the Bitmap on the UI thread.
                var bytes = await File.ReadAllBytesAsync(handle.ManagedFilePath, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var bitmap = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    var width = Math.Clamp(asset.Width ?? 1024, 1, 2048);
                    return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
                });
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                        bitmap.Dispose();
                    else
                        PreviewImage = bitmap;
                });
            }
            else
            {
                var result = await MeshAssetLoader.LoadAsync(handle.ManagedFilePath, cancellationToken: token).ConfigureAwait(false);
                if (result.Success)
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                            PreviewMesh = result.Asset;
                    });
                else
                    await Dispatcher.UIThread.InvokeAsync(() => PreviewError = _localization.Get("VisualPreviewFailed"));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => PreviewError = exception.Message);
        }
    }

    public void Dispose()
    {
        _profiles.Changed -= OnProfilesChanged;
        _assets.Changed -= OnAssetsChanged;
        _localization.PropertyChanged -= OnLocalizationChanged;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        _previewImage?.Dispose();
        _previewImage = null;
        GC.SuppressFinalize(this);
    }
}
