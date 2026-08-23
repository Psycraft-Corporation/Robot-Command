using RobotCommand.Core;
using Veldrid;

namespace RobotCommand.Rendering.Veldrid;

/// <summary>
/// Veldrid device probe and lifecycle boundary. Surface-specific command
/// encoding is intentionally kept behind this type so Avalonia integration can
/// evolve without leaking Veldrid types into Runtime or Core.
/// </summary>
public sealed class VeldridRenderer : IThreeDRenderer
{
    private GraphicsDevice? _device;
    private ThreeDRendererStatus _status = ThreeDRendererStatus.Uninitialized;
    private DateTimeOffset _lastFrame = DateTimeOffset.UtcNow;
    private int _frameCount;

    public ThreeDRendererStatus Status => _status;

    public Task InitializeAsync(ThreeDRenderBackendPolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (policy == ThreeDRenderBackendPolicy.Software)
        {
            _status = new("Veldrid", false, false, false, "Software rendering was requested.", 0, 0);
            return Task.CompletedTask;
        }

        try
        {
            // A no-swapchain device probe is safe before an Avalonia surface is
            // attached. The control creates the real surface-specific renderer.
            _device = GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions(false));
            _status = new($"Veldrid/{_device.BackendType}", true, true, false, null, 0, 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _device?.Dispose();
            _device = null;
            _status = new("Veldrid", false, false, false, exception.Message, 0, 0);
        }

        return Task.CompletedTask;
    }

    public Task ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task RenderAsync(ThreeDSceneSnapshot scene, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_device is null) return Task.CompletedTask;
        var now = DateTimeOffset.UtcNow;
        _frameCount++;
        var elapsed = (now - _lastFrame).TotalSeconds;
        if (elapsed >= 1)
        {
            _status = _status with
            {
                FramesPerSecond = _frameCount / elapsed,
                FrameTimeMilliseconds = elapsed * 1000d / Math.Max(1, _frameCount)
            };
            _frameCount = 0;
            _lastFrame = now;
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _device?.Dispose();
        _device = null;
        _status = ThreeDRendererStatus.Uninitialized;
        return ValueTask.CompletedTask;
    }
}
