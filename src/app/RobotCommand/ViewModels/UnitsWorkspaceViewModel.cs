using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
using RobotCommand.Rendering.Meshes;

namespace RobotCommand.ViewModels;

public sealed record UnitsWorkspaceSection(string Id, string Title, object Content);

public sealed class UnitsWorkspaceViewModel : ObservableObject
{
    private UnitsWorkspaceSection _selectedSection;

    public UnitsWorkspaceViewModel(GhostProfilesViewModel ghosts, ILocalizationService localization)
    {
        Ghosts = ghosts;
        Sections = new ObservableCollection<UnitsWorkspaceSection>
        {
            new("units", localization.Get("Units"), new UnitsStubViewModel(localization.Get("Units"))),
            new("ghosts", localization.Get("Ghosts"), ghosts)
        };
        _selectedSection = Sections[1];
    }

    public GhostProfilesViewModel Ghosts { get; }
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
}

public sealed class UnitsStubViewModel
{
    public UnitsStubViewModel(string title) => Title = title;
    public string Title { get; }
}

public sealed class GhostProfilesViewModel : ObservableObject
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
    public string VehicleType => "Multicopter";

    public ICommand NewProfileCommand { get; }
    public ICommand EditProfileCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand CancelEditCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand RemoveVisualCommand { get; }

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
}
