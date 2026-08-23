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

    public ManualControlViewModel(IManualControlWorkflow manual, ISelectionService selection, IEntityStore<string, VehicleRecord> vehicles, ILocalizationService localization)
    {
        _manual = manual;
        _selection = selection;
        _vehicles = vehicles;
        _localization = localization;
        _profile = manual.Profile;
        ToggleControlCommand = new AsyncRelayCommand(ToggleControlAsync, CanToggleControl);
        SaveProfileCommand = new AsyncRelayCommand(_ => _manual.SaveProfileAsync(Profile));
        _manual.Changed += (_, _) => Refresh();
        _selection.Changed += (_, _) => Refresh();
        _localization.PropertyChanged += (_, _) => Refresh();
    }

    public ICommand ToggleControlCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public IReadOnlyList<ManualInputDeviceSnapshot> Devices => _manual.Devices;
    public ManualControlSessionWorkflowSnapshot Session => _manual.Session;
    public ManualControlReadingSnapshot? Reading => _manual.LatestReading;
    public string? SelectedDeviceId
    {
        get => _manual.Devices.FirstOrDefault(item => item.Selected)?.Id;
        set => _ = _manual.SelectDeviceAsync(value);
    }
    public string SelectedDeviceDescription
    {
        get
        {
            var device = Devices.FirstOrDefault(item => item.Selected);
            return device is null
                ? "No controller selected."
                : device.Kind == "Joystick"
                    ? $"USB joystick · {device.AxisCount} axes · {device.ButtonCount} buttons"
                    : "Xbox-compatible controller";
        }
    }
    public bool IsSelectedJoystick => string.Equals(Devices.FirstOrDefault(item => item.Selected)?.Kind, "Joystick", StringComparison.Ordinal);
    public string SelectedManualUnitText => SelectedManualUnit?.Name ?? _localization.Get("ManualControlSelectUnit");
    public bool HasSession => Session.IsActive;
    public bool IsControlTransitioning => Session.State is nameof(ManualControlSessionState.Acquiring) or nameof(ManualControlSessionState.Releasing);
    public bool HasControlStatus => HasSession || IsControlTransitioning;
    public string ControlButtonText => Session.State switch
    {
        nameof(ManualControlSessionState.Acquiring) => "Taking control...",
        nameof(ManualControlSessionState.Releasing) => "Releasing control...",
        nameof(ManualControlSessionState.Active) or nameof(ManualControlSessionState.Hold) or nameof(ManualControlSessionState.InputStale) => "Release control",
        _ => "Take control"
    };
    public string SessionStatus => Session.Status;
    public string DeadmanStatus => Session.DeadmanPressed ? "LB active" : "LB released";
    public string PendingButtonAction => Session.PendingButtonAction ?? "No pending controller action.";
    public bool HasPendingButtonAction => Session.PendingButtonExpiresAt is not null && !string.IsNullOrWhiteSpace(Session.PendingButtonAction);
    public bool HasNoPendingButtonAction => !HasPendingButtonAction;
    public string PendingButtonExpiry
    {
        get
        {
            if (Session.PendingButtonExpiresAt is not { } expiresAt) return "";
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero
                ? $"Timeout in {Math.Ceiling(remaining.TotalSeconds):0}s"
                : "Confirmation timed out";
        }
    }
    public string InputAge => Session.LastInputAt is { } time ? $"{Math.Max(0, (DateTimeOffset.UtcNow - time).TotalMilliseconds):F0} ms" : "-";
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
    public double TakeoffAltitude { get => Profile.TakeoffAltitudeAglMetres; set => Profile = Profile with { TakeoffAltitudeAglMetres = value }; }
    public double LeftX => Reading?.LeftX ?? 0;
    public double LeftY => Reading?.LeftY ?? 0;
    public double RightX => Reading?.RightX ?? 0;
    public double RightY => Reading?.RightY ?? 0;
    public string RawAxesText => Reading?.RawAxes is { Count: > 0 } axes
        ? string.Join(" · ", axes.Select((value, index) => $"A{index}: {value:F2}"))
        : "Raw axes appear here when a USB joystick is selected.";
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
           (vehicle.IsGhost || vehicle.ConnectionIds.Count > 0)
            ? vehicle : null;

    private bool CanToggleControl()
        => !IsControlTransitioning &&
           (HasSession || (SelectedManualUnit is not null && Devices.Count > 0));

    private async Task ToggleControlAsync(CancellationToken cancellationToken)
    {
        if (HasSession)
        {
            await _manual.ReleaseAsync(ManualControlOwnerKind.Gui, "desktop-gui", cancellationToken: cancellationToken);
        }
        else if (SelectedManualUnit is { } unit)
        {
            await _manual.TakeControlAsync(unit.Id, ManualControlOwnerKind.Gui, "desktop-gui", cancellationToken);
        }
        Refresh();
    }

    private void Refresh()
    {
        _profile = _manual.Profile;
        OnPropertyChanged(nameof(Devices)); OnPropertyChanged(nameof(SelectedDeviceId)); OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(SelectedDeviceDescription)); OnPropertyChanged(nameof(IsSelectedJoystick));
        OnPropertyChanged(nameof(Reading)); OnPropertyChanged(nameof(SelectedManualUnitText)); OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(HasControlStatus));
        OnPropertyChanged(nameof(IsControlTransitioning)); OnPropertyChanged(nameof(ControlButtonText));
        OnPropertyChanged(nameof(SessionStatus)); OnPropertyChanged(nameof(DeadmanStatus)); OnPropertyChanged(nameof(PendingButtonAction));
        OnPropertyChanged(nameof(HasPendingButtonAction)); OnPropertyChanged(nameof(HasNoPendingButtonAction)); OnPropertyChanged(nameof(PendingButtonExpiry));
        OnPropertyChanged(nameof(InputAge)); OnPropertyChanged(nameof(LeftX)); OnPropertyChanged(nameof(LeftY));
        OnPropertyChanged(nameof(RightX)); OnPropertyChanged(nameof(RightY)); OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(RawAxesText)); OnPropertyChanged(nameof(JoystickMapping));
        OnPropertyChanged(nameof(ForwardAxis)); OnPropertyChanged(nameof(ForwardInverted));
        OnPropertyChanged(nameof(RightAxis)); OnPropertyChanged(nameof(RightInverted));
        OnPropertyChanged(nameof(VerticalAxis)); OnPropertyChanged(nameof(VerticalInverted));
        OnPropertyChanged(nameof(YawAxis)); OnPropertyChanged(nameof(YawInverted));
        OnPropertyChanged(nameof(DeadmanButton)); OnPropertyChanged(nameof(ArmButton)); OnPropertyChanged(nameof(TakeoffButton)); OnPropertyChanged(nameof(ExecuteButton)); OnPropertyChanged(nameof(CancelButton)); OnPropertyChanged(nameof(ReleaseButton));
        OnPropertyChanged(nameof(ProcessedLeftX)); OnPropertyChanged(nameof(ProcessedLeftY)); OnPropertyChanged(nameof(ProcessedRightX)); OnPropertyChanged(nameof(ProcessedRightY));
        OnPropertyChanged(nameof(DeadZone)); OnPropertyChanged(nameof(Expo)); OnPropertyChanged(nameof(MaximumHorizontalSpeed));
        OnPropertyChanged(nameof(MaximumVerticalSpeed)); OnPropertyChanged(nameof(MaximumYawRate)); OnPropertyChanged(nameof(TakeoffAltitude));
        ((AsyncRelayCommand)ToggleControlCommand).RaiseCanExecuteChanged();
    }

    private double Process(double input, double maximum)
    {
        var magnitude = Math.Abs(input);
        if (magnitude <= Profile.DeadZone) return 0;
        var normalized = (magnitude - Profile.DeadZone) / (1 - Profile.DeadZone);
        return Math.Sign(input) * (((1 - Profile.Expo) * normalized) + (Profile.Expo * normalized * normalized * normalized)) * maximum;
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
        var mappings = (Profile.JoystickMappings ?? new Dictionary<string, ManualJoystickMappingSnapshot>())
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        mappings[deviceId] = mapping;
        Profile = Profile with { JoystickMappings = mappings };
        Refresh();
    }
}
