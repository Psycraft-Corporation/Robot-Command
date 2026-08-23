using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;

namespace RobotCommand.ViewModels;

public sealed class ThreeDWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IThreeDSceneWorkflow _scene;
    private ThreeDSceneSnapshot _snapshot;

    public ThreeDWorkspaceViewModel(IThreeDSceneWorkflow scene)
    {
        _scene = scene;
        _snapshot = scene.Current;
        _scene.Changed += OnChanged;
        ResetCameraCommand = new AsyncRelayCommand(ct => _scene.ResetCameraAsync(ct));
        FitSceneCommand = new AsyncRelayCommand(ct => _scene.FitSceneAsync(ct));
        UseSoftwareCommand = new AsyncRelayCommand(ct => _scene.SetBackendPolicyAsync(ThreeDRenderBackendPolicy.Software, ct));
        UseAutoCommand = new AsyncRelayCommand(ct => _scene.SetBackendPolicyAsync(ThreeDRenderBackendPolicy.Auto, ct));
    }

    public ThreeDSceneSnapshot Snapshot
    {
        get => _snapshot;
        private set
        {
            if (!SetProperty(ref _snapshot, value)) return;
            OnPropertyChanged(nameof(RendererText));
            OnPropertyChanged(nameof(CameraText));
            OnPropertyChanged(nameof(EntityCountText));
        }
    }

    public string RendererText => $"{Snapshot.RendererStatus.Backend} | {Snapshot.RendererStatus.FallbackReason ?? "Ready"}";
    public string CameraText => $"Camera {Snapshot.Camera.Position.X:0.#}, {Snapshot.Camera.Position.Y:0.#}, {Snapshot.Camera.Position.Z:0.#}";
    public string EntityCountText => $"{Snapshot.Primitives.Count} objects | {Snapshot.Lines.Count} lines";
    public ICommand ResetCameraCommand { get; }
    public ICommand FitSceneCommand { get; }
    public ICommand UseSoftwareCommand { get; }
    public ICommand UseAutoCommand { get; }

    public Task SetCameraAsync(ThreeDCameraSnapshot camera, CancellationToken cancellationToken = default)
        => _scene.SetCameraAsync(camera, cancellationToken);

    public Task FitSceneAsync(CancellationToken cancellationToken = default)
        => _scene.FitSceneAsync(cancellationToken);

    private void OnChanged(object? sender, EventArgs e) => Snapshot = _scene.Current;

    public void Dispose() => _scene.Changed -= OnChanged;
}
