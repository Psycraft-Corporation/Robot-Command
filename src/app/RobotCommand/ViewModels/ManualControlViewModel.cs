using System.Collections.ObjectModel;
using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class ManualControlViewModel : ObservableObject
{
    private readonly IManualControlWorkflow _manual;
    private readonly ISelectionService _selection;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly ILocalizationService _localization;
    private ManualControlProfileSnapshot _profile;
    private string _profileName = string.Empty;
    private string? _editingProfileId;
    private string? _selectedProfileId;
    private ManualControlProfileEntrySnapshot? _selectedProfile;
    private string? _selectedDeviceId;
    private bool _isEditingProfile;
    private bool _advancedOpen;
    private bool _diagnosticsOpen;

    public ManualControlViewModel(IManualControlWorkflow manual, ISelectionService selection, IEntityStore<string, VehicleRecord> vehicles, ILocalizationService localization)
    {
        _manual = manual;
        _selection = selection;
        _vehicles = vehicles;
        _localization = localization;
        _profile = manual.Profile;
        _profileName = manual.Profiles.FirstOrDefault(item => item.IsActive)?.Name ?? "Default";
        _selectedProfileId = manual.ActiveProfileId;
        _selectedProfile = manual.Profiles.FirstOrDefault(item => item.IsActive);
        _selectedDeviceId = manual.Devices.FirstOrDefault(item => item.Selected)?.Id;
        Profiles = new ObservableCollection<ManualControlProfileEntrySnapshot>(manual.Profiles);
        Devices = new ObservableCollection<ManualInputDeviceSnapshot>(manual.Devices);
        ToggleControlCommand = new AsyncRelayCommand(ToggleControlAsync, CanToggleControl);
        NewProfileCommand = new AsyncRelayCommand(NewProfileAsync, () => !HasSession);
        EditProfileCommand = new RelayCommand(_ => BeginEdit(), _ => !HasSession && !IsEditingProfile);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => !HasSession && IsEditingProfile);
        CancelProfileCommand = new RelayCommand(_ => CancelEdit(), _ => IsEditingProfile);
        DeleteProfileCommand = new AsyncRelayCommand(DeleteProfileAsync, () => !HasSession && Profiles.Count > 1);
        _manual.Changed += (_, _) => Refresh();
        _selection.Changed += (_, _) => Refresh();
        _localization.PropertyChanged += (_, _) => Refresh();
    }

    public ICommand ToggleControlCommand { get; }
    public ICommand NewProfileCommand { get; }
    public ICommand EditProfileCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand CancelProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }

    public ObservableCollection<ManualInputDeviceSnapshot> Devices { get; }
    public ObservableCollection<ManualControlProfileEntrySnapshot> Profiles { get; }
    public bool HasDevices => Devices.Count > 0;
    public ManualControlSessionWorkflowSnapshot Session => _manual.Session;
    public ManualControlReadingSnapshot? Reading => _manual.LatestReading;
    public bool HasSession => Session.IsActive;
    public bool HasControlStatus => HasSession;
    public bool IsEditingProfile
    {
        get => _isEditingProfile;
        private set => SetProperty(ref _isEditingProfile, value);
    }
    public bool HasSavedProfile => _editingProfileId is not null;
    public bool IsAdvancedOpen
    {
        get => _advancedOpen;
        set => SetProperty(ref _advancedOpen, value);
    }
    public bool IsDiagnosticsOpen
    {
        get => _diagnosticsOpen;
        set => SetProperty(ref _diagnosticsOpen, value);
    }
    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }
    public string? SelectedProfileId
    {
        get => _selectedProfileId;
        set
        {
            if (IsEditingProfile || value is null || !SetProperty(ref _selectedProfileId, value))
                return;
            if (!string.Equals(value, _manual.ActiveProfileId, StringComparison.Ordinal))
                _ = SelectProfileAsync(value);
        }
    }
    public ManualControlProfileEntrySnapshot? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (IsEditingProfile || value is null || !SetProperty(ref _selectedProfile, value))
                return;
            _selectedProfileId = value.Id;
            if (!string.Equals(value.Id, _manual.ActiveProfileId, StringComparison.Ordinal))
                _ = SelectProfileAsync(value.Id);
        }
    }
    public string? SelectedDeviceId
    {
        get => _selectedDeviceId;
        set
        {
            if (SetProperty(ref _selectedDeviceId, value))
                _ = _manual.SelectDeviceAsync(value);
        }
    }
    public string SelectedDeviceDescription
    {
        get
        {
            var device = Devices.FirstOrDefault(item => item.Selected);
            return device?.Kind ?? _localization.Get("ManualNoController");
        }
    }
    public string SelectedDeviceStatus => Devices.FirstOrDefault(item => item.Selected) is { IsConnected: true }
        ? _localization.Get("ManualConnected")
        : _localization.Get("ManualDisconnected");
    public string SelectedManualUnitText => SelectedManualUnit?.Name ?? _localization.Get("ManualNoUnit");
    public bool HasSelectedDevice => Devices.Any(item => item.Selected);
    public bool IsControlTransitioning => Session.State is nameof(ManualControlSessionState.Acquiring) or nameof(ManualControlSessionState.Releasing);
    public string ControlButtonText => Session.State switch
    {
        nameof(ManualControlSessionState.Acquiring) => _localization.Get("ManualTakingControl"),
        nameof(ManualControlSessionState.Releasing) => _localization.Get("ManualReleasingControl"),
        nameof(ManualControlSessionState.Active) or nameof(ManualControlSessionState.Hold) or nameof(ManualControlSessionState.InputStale) => _localization.Get("ManualRelease"),
        _ => _localization.Get("ManualTakeControl")
    };
    public string SessionStatus => string.Equals(Session.Status, "No manual-control session.", StringComparison.Ordinal)
        ? _localization.Get("ManualNoSession")
        : Session.Status;
    public string SessionMode => string.Equals(Session.AutopilotMode ?? Session.Backend, "None", StringComparison.Ordinal)
        ? _localization.Get("ManualNone")
        : Session.AutopilotMode ?? Session.Backend;
    public string DeadmanStatus => Session.DeadmanPressed ? _localization.Get("ManualSafetyActive") : _localization.Get("ManualSafetyReleased");
    public string InputAge => Session.LastInputAt is { } time ? $"{Math.Max(0, (DateTimeOffset.UtcNow - time).TotalMilliseconds):F0} ms" : "-";
    public string RawAxesText => Reading?.RawAxes is { Count: > 0 } axes
        ? string.Join(" · ", axes.Select((value, index) => $"A{index}: {value:F2}"))
        : _localization.Get("ManualNoLiveInput");

    public ManualControlProfileSnapshot Profile
    {
        get => _profile;
        set => SetProperty(ref _profile, value);
    }
    public double DeadZone { get => Profile.DeadZone; set => Profile = Profile with { DeadZone = value }; }
    public double Expo { get => Profile.Expo; set => Profile = Profile with { Expo = value }; }
    public double MaximumHorizontalSpeed { get => Profile.MaximumHorizontalSpeedMetresPerSecond; set => Profile = Profile with { MaximumHorizontalSpeedMetresPerSecond = value }; }
    public double MaximumVerticalSpeed { get => Profile.MaximumVerticalSpeedMetresPerSecond; set => Profile = Profile with { MaximumVerticalSpeedMetresPerSecond = value }; }
    public double MaximumYawRate { get => Profile.MaximumYawRateDegreesPerSecond; set => Profile = Profile with { MaximumYawRateDegreesPerSecond = value }; }
    public double HorizontalAcceleration { get => Profile.HorizontalAccelerationMetresPerSecondSquared; set => Profile = Profile with { HorizontalAccelerationMetresPerSecondSquared = value }; }
    public double VerticalAcceleration { get => Profile.VerticalAccelerationMetresPerSecondSquared; set => Profile = Profile with { VerticalAccelerationMetresPerSecondSquared = value }; }
    public double YawAcceleration { get => Profile.YawAccelerationDegreesPerSecondSquared; set => Profile = Profile with { YawAccelerationDegreesPerSecondSquared = value }; }
    public double TakeoffAltitude { get => Profile.TakeoffAltitudeAglMetres; set => Profile = Profile with { TakeoffAltitudeAglMetres = value }; }
    public double LeftX => Reading?.LeftX ?? 0;
    public double LeftY => Reading?.LeftY ?? 0;
    public double RightX => Reading?.RightX ?? 0;
    public double RightY => Reading?.RightY ?? 0;
    public double ProcessedLeftX => Process(LeftX, Profile.MaximumYawRateDegreesPerSecond);
    public double ProcessedLeftY => Process(LeftY, Profile.MaximumVerticalSpeedMetresPerSecond);
    public double ProcessedRightX => Process(RightX, Profile.MaximumHorizontalSpeedMetresPerSecond);
    public double ProcessedRightY => Process(RightY, Profile.MaximumHorizontalSpeedMetresPerSecond);
    public ManualJoystickMappingSnapshot JoystickMapping => GetJoystickMapping();
    public int ForwardAxis { get => JoystickMapping.ForwardAxis; set => UpdateJoystickMapping(JoystickMapping with { ForwardAxis = value }); }
    public bool ForwardInverted { get => JoystickMapping.ForwardInverted; set => UpdateJoystickMapping(JoystickMapping with { ForwardInverted = value }); }
    public int RightAxis { get => JoystickMapping.RightAxis; set => UpdateJoystickMapping(JoystickMapping with { RightAxis = value }); }
    public bool RightInverted { get => JoystickMapping.RightInverted; set => UpdateJoystickMapping(JoystickMapping with { RightInverted = value }); }
    public int VerticalAxis { get => JoystickMapping.VerticalAxis; set => UpdateJoystickMapping(JoystickMapping with { VerticalAxis = value }); }
    public bool VerticalInverted { get => JoystickMapping.VerticalInverted; set => UpdateJoystickMapping(JoystickMapping with { VerticalInverted = value }); }
    public int YawAxis { get => JoystickMapping.YawAxis; set => UpdateJoystickMapping(JoystickMapping with { YawAxis = value }); }
    public bool YawInverted { get => JoystickMapping.YawInverted; set => UpdateJoystickMapping(JoystickMapping with { YawInverted = value }); }
    public int DeadmanButton { get => JoystickMapping.DeadmanButton; set => UpdateJoystickMapping(JoystickMapping with { DeadmanButton = value }); }
    public int ArmButton { get => JoystickMapping.ArmButton; set => UpdateJoystickMapping(JoystickMapping with { ArmButton = value }); }
    public int TakeoffButton { get => JoystickMapping.TakeoffButton; set => UpdateJoystickMapping(JoystickMapping with { TakeoffButton = value }); }
    public int ExecuteButton { get => JoystickMapping.ExecuteButton; set => UpdateJoystickMapping(JoystickMapping with { ExecuteButton = value }); }
    public int CancelButton { get => JoystickMapping.CancelButton; set => UpdateJoystickMapping(JoystickMapping with { CancelButton = value }); }
    public int ReleaseButton { get => JoystickMapping.ReleaseButton; set => UpdateJoystickMapping(JoystickMapping with { ReleaseButton = value }); }

    private VehicleRecord? SelectedManualUnit
        => _selection.SelectedUnitIds.Count == 1 && _vehicles.TryGet(_selection.SelectedUnitIds[0], out var vehicle) &&
           vehicle is { State: AvailabilityState.Online or AvailabilityState.Degraded } &&
           (vehicle.IsGhost || vehicle.ConnectionIds.Count > 0) ? vehicle : null;

    private bool CanToggleControl() => !IsControlTransitioning && (HasSession || (SelectedManualUnit is not null && HasSelectedDevice));

    private async Task ToggleControlAsync(CancellationToken cancellationToken)
    {
        if (HasSession)
            await _manual.ReleaseAsync(ManualControlOwnerKind.Gui, "desktop-gui", cancellationToken: cancellationToken);
        else if (SelectedManualUnit is { } unit)
            await _manual.TakeControlAsync(unit.Id, ManualControlOwnerKind.Gui, "desktop-gui", cancellationToken);
        Refresh();
    }

    private Task NewProfileAsync(CancellationToken cancellationToken)
    {
        var index = 1;
        var name = $"Profile {index}";
        while (Profiles.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))) name = $"Profile {++index}";
        _editingProfileId = null;
        ProfileName = name;
        Profile = _manual.Profile;
        IsEditingProfile = true;
        OnPropertyChanged(nameof(HasSavedProfile));
        RefreshCommands();
        return Task.CompletedTask;
    }

    private void BeginEdit()
    {
        if (HasSession) return;
        _editingProfileId = _manual.ActiveProfileId;
        ProfileName = Profiles.FirstOrDefault(item => item.IsActive)?.Name ?? "Default";
        Profile = _manual.Profile;
        IsEditingProfile = true;
        OnPropertyChanged(nameof(HasSavedProfile));
        RefreshCommands();
    }

    private void CancelEdit()
    {
        _editingProfileId = null;
        ProfileName = Profiles.FirstOrDefault(item => item.IsActive)?.Name ?? "Default";
        Profile = _manual.Profile;
        IsEditingProfile = false;
        OnPropertyChanged(nameof(HasSavedProfile));
        Refresh();
    }

    private async Task SaveProfileAsync(CancellationToken cancellationToken)
    {
        if (_editingProfileId is null)
        {
            var created = await _manual.CreateProfileAsync(ProfileName, Profile, cancellationToken);
            await _manual.SelectProfileAsync(created.Id, cancellationToken);
        }
        else
        {
            await _manual.UpdateProfileAsync(_editingProfileId, ProfileName, Profile, cancellationToken);
        }
        _editingProfileId = null;
        IsEditingProfile = false;
        OnPropertyChanged(nameof(HasSavedProfile));
        Refresh();
    }

    private async Task DeleteProfileAsync(CancellationToken cancellationToken)
    {
        if (_editingProfileId is null)
        {
            CancelEdit();
            return;
        }
        await _manual.DeleteProfileAsync(_editingProfileId, cancellationToken);
        _editingProfileId = null;
        IsEditingProfile = false;
        OnPropertyChanged(nameof(HasSavedProfile));
        Refresh();
    }

    private async Task SelectProfileAsync(string profileId)
    {
        await _manual.SelectProfileAsync(profileId);
        Refresh();
    }

    private ManualJoystickMappingSnapshot GetJoystickMapping()
    {
        var deviceId = SelectedDeviceId;
        if (deviceId is not null && Profile.JoystickMappings is not null && Profile.JoystickMappings.TryGetValue(deviceId, out var mapping)) return mapping;
        return new ManualJoystickMappingSnapshot(1, true, 0, false, 3, false, 2, false, 0, 1, 2, 3, 4, 5);
    }

    private void UpdateJoystickMapping(ManualJoystickMappingSnapshot mapping)
    {
        var deviceId = SelectedDeviceId;
        if (deviceId is null) return;
        var mappings = (Profile.JoystickMappings ?? new Dictionary<string, ManualJoystickMappingSnapshot>()).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        mappings[deviceId] = mapping;
        Profile = Profile with { JoystickMappings = mappings };
        Refresh();
    }

    private double Process(double input, double maximum)
    {
        var magnitude = Math.Abs(input);
        if (magnitude <= Profile.DeadZone) return 0;
        var normalized = (magnitude - Profile.DeadZone) / (1 - Profile.DeadZone);
        return Math.Sign(input) * (((1 - Profile.Expo) * normalized) + (Profile.Expo * normalized * normalized * normalized)) * maximum;
    }

    private void Refresh()
    {
        var previousProfileId = _selectedProfileId;
        var previousProfile = _selectedProfile;
        var previousDeviceId = _selectedDeviceId;
        SyncProfiles();
        SyncDevices();
        if (!IsEditingProfile)
        {
            _profile = _manual.Profile;
            _profileName = Profiles.FirstOrDefault(item => item.IsActive)?.Name ?? "Default";
            _selectedProfileId = _manual.ActiveProfileId;
            _selectedProfile = Profiles.FirstOrDefault(item => item.IsActive);
        }
        var selectedDevice = Devices.FirstOrDefault(item => item.Selected)?.Id;
        if (selectedDevice is not null || _selectedDeviceId is null)
            _selectedDeviceId = selectedDevice;
        if (!string.Equals(previousProfileId, _selectedProfileId, StringComparison.Ordinal))
            OnPropertyChanged(nameof(SelectedProfileId));
        if (!Equals(previousProfile, _selectedProfile))
            OnPropertyChanged(nameof(SelectedProfile));
        if (!string.Equals(previousDeviceId, _selectedDeviceId, StringComparison.Ordinal))
            OnPropertyChanged(nameof(SelectedDeviceId));

        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(HasControlStatus));
        OnPropertyChanged(nameof(ControlButtonText));
        OnPropertyChanged(nameof(SessionStatus));
        OnPropertyChanged(nameof(SessionMode));
        OnPropertyChanged(nameof(DeadmanStatus));
        OnPropertyChanged(nameof(InputAge));
        OnPropertyChanged(nameof(SelectedDeviceDescription));
        OnPropertyChanged(nameof(SelectedDeviceStatus));
        OnPropertyChanged(nameof(SelectedManualUnitText));
        OnPropertyChanged(nameof(HasSelectedDevice));
        OnPropertyChanged(nameof(LeftX));
        OnPropertyChanged(nameof(LeftY));
        OnPropertyChanged(nameof(RightX));
        OnPropertyChanged(nameof(RightY));
        OnPropertyChanged(nameof(RawAxesText));
        OnPropertyChanged(nameof(JoystickMapping));
        OnPropertyChanged(nameof(ForwardAxis));
        OnPropertyChanged(nameof(ForwardInverted));
        OnPropertyChanged(nameof(RightAxis));
        OnPropertyChanged(nameof(RightInverted));
        OnPropertyChanged(nameof(VerticalAxis));
        OnPropertyChanged(nameof(VerticalInverted));
        OnPropertyChanged(nameof(YawAxis));
        OnPropertyChanged(nameof(YawInverted));
        OnPropertyChanged(nameof(DeadmanButton));
        OnPropertyChanged(nameof(ArmButton));
        OnPropertyChanged(nameof(TakeoffButton));
        OnPropertyChanged(nameof(ExecuteButton));
        OnPropertyChanged(nameof(CancelButton));
        OnPropertyChanged(nameof(ReleaseButton));
        RefreshCommands();
    }

    private void SyncProfiles()
    {
        var current = _manual.Profiles;
        if (Profiles.SequenceEqual(current))
            return;
        Profiles.Clear();
        foreach (var profile in current)
            Profiles.Add(profile);
    }

    private void SyncDevices()
    {
        var current = _manual.Devices;
        if (Devices.SequenceEqual(current))
            return;
        Devices.Clear();
        foreach (var device in current)
            Devices.Add(device);
        OnPropertyChanged(nameof(HasDevices));
    }

    private void RefreshCommands()
    {
        ((AsyncRelayCommand)ToggleControlCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)NewProfileCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)SaveProfileCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)DeleteProfileCommand).RaiseCanExecuteChanged();
        ((RelayCommand)EditProfileCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelProfileCommand).RaiseCanExecuteChanged();
    }
}
