using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RobotCommand.Models;
using RobotCommand.Services.Media;

namespace RobotCommand.Controls;

public sealed class NativeVideoSurface : Control
{
    public static readonly StyledProperty<IVideoFrameSource?> SourceProperty =
        AvaloniaProperty.Register<NativeVideoSurface, IVideoFrameSource?>(nameof(Source));

    private WriteableBitmap? _bitmap;
    private byte[] _copyBuffer = [];
    private IVideoFrameSource? _subscribedSource;
    private int _frameUpdateQueued;
    private bool _attached;
    private bool _rendering;
    private long _renderedSequence;

    static NativeVideoSurface()
    {
        AffectsRender<NativeVideoSurface>(SourceProperty);
    }

    public IVideoFrameSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            OnSourceChanged();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Subscribe(Source);
        QueueFrameUpdate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Unsubscribe();
        DisposeBitmap();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        _rendering = true;
        try
        {
            base.Render(context);
            context.FillRectangle(Brushes.Black, Bounds);

            // A source can publish its first frame before the dispatcher processes
            // the FrameAvailable notification. Pull it synchronously during the
            // render pass so a live source never presents a permanently black
            // surface while its frame counter is advancing.
            var latestSequence = Source?.LatestInfo?.Sequence ?? 0;
            if (_bitmap is null || latestSequence > _renderedSequence)
            {
                ApplyLatestFrame();
            }

            var bitmap = _bitmap;
            if (Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                return;
            }

            if (bitmap is null)
            {
                return;
            }

            var sourceWidth = bitmap.PixelSize.Width;
            var sourceHeight = bitmap.PixelSize.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return;
            }

            var scale = Math.Min(Bounds.Width / sourceWidth, Bounds.Height / sourceHeight);
            var width = sourceWidth * scale;
            var height = sourceHeight * scale;
            var destination = new Rect(
                (Bounds.Width - width) / 2,
                (Bounds.Height - height) / 2,
                width,
                height);

            context.DrawImage(
                bitmap,
                new Rect(0, 0, sourceWidth, sourceHeight),
                destination);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void OnSourceChanged()
    {
        Unsubscribe();
        if (_attached)
        {
            Subscribe(Source);
            QueueFrameUpdate();
        }
    }

    private void Subscribe(IVideoFrameSource? source)
    {
        if (source is null || ReferenceEquals(source, _subscribedSource))
        {
            return;
        }

        _subscribedSource = source;
        source.FrameAvailable += OnFrameAvailable;
    }

    private void Unsubscribe()
    {
        if (_subscribedSource is not null)
        {
            _subscribedSource.FrameAvailable -= OnFrameAvailable;
            _subscribedSource = null;
        }
    }

    private void OnFrameAvailable(object? sender, EventArgs e) => QueueFrameUpdate();

    private void QueueFrameUpdate()
    {
        if (Interlocked.Exchange(ref _frameUpdateQueued, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            ApplyLatestFrame,
            DispatcherPriority.Render);
    }

    private void ApplyLatestFrame()
    {
        Interlocked.Exchange(ref _frameUpdateQueued, 0);
        var source = Source;
        var info = source?.LatestInfo;
        if (source is null || info is null)
        {
            DisposeBitmap();
            if (!_rendering)
            {
                InvalidateVisual();
            }
            return;
        }

        if (_copyBuffer.Length < info.RequiredBytes)
        {
            _copyBuffer = new byte[info.RequiredBytes];
        }

        if (!source.TryCopyLatest(_copyBuffer, out var copiedInfo) || copiedInfo is null)
        {
            return;
        }

        var handle = GCHandle.Alloc(_copyBuffer, GCHandleType.Pinned);
        try
        {
            var nextBitmap = new WriteableBitmap(
                PixelFormats.Bgra8888,
                AlphaFormat.Opaque,
                handle.AddrOfPinnedObject(),
                new PixelSize(copiedInfo.Width, copiedInfo.Height),
                new Vector(96, 96),
                copiedInfo.Stride);
            var previousBitmap = _bitmap;
            _bitmap = nextBitmap;
            previousBitmap?.Dispose();
        }
        finally
        {
            handle.Free();
        }

        if (!_rendering)
        {
            InvalidateVisual();
        }
        _renderedSequence = copiedInfo.Sequence;

        if (source.LatestInfo is { Sequence: var latestSequence } &&
            latestSequence != copiedInfo.Sequence)
        {
            QueueFrameUpdate();
        }
    }

    private void DisposeBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _renderedSequence = 0;
    }
}
