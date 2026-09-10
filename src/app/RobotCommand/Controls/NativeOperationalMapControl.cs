using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BruTile.MbTiles;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Manipulations;
using Mapsui.Nts;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Mapsui.UI.Avalonia;
using NetTopologySuite.Geometries;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Maps;
using RobotCommand.ViewModels;
using SQLite;
using AvaloniaBrushes = Avalonia.Media.Brushes;
using AvaloniaPoints = Avalonia.Points;
using AvaloniaPolygon = Avalonia.Controls.Shapes.Polygon;
using AvaloniaPolyline = Avalonia.Controls.Shapes.Polyline;
using AvaloniaRectangle = Avalonia.Controls.Shapes.Rectangle;
using MapsuiBrush = Mapsui.Styles.Brush;
using MapsuiColor = Mapsui.Styles.Color;
using MapsuiFont = Mapsui.Styles.Font;
using MapsuiPen = Mapsui.Styles.Pen;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace RobotCommand.Controls;

public sealed class NativeOperationalMapControl : Grid, IDisposable
{
    public static readonly StyledProperty<OperationalMapPresentation?> PresentationProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, OperationalMapPresentation?>(nameof(Presentation));

    public static readonly StyledProperty<MapVehicleMotionSnapshot?> VehicleMotionProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, MapVehicleMotionSnapshot?>(nameof(VehicleMotion));

    public static readonly StyledProperty<ICommand?> SelectVehicleCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(SelectVehicleCommand));

    public static readonly StyledProperty<ICommand?> ClearConnectedClientSelectionCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(ClearConnectedClientSelectionCommand));

    public static readonly StyledProperty<ICommand?> SelectGeometryCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(SelectGeometryCommand));

    public static readonly StyledProperty<bool> GeometrySelectionEnabledProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, bool>(nameof(GeometrySelectionEnabled));

    public static readonly StyledProperty<ICommand?> ViewportChangedCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(ViewportChangedCommand));

    public static readonly StyledProperty<ICommand?> UserPannedCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(UserPannedCommand));

    public static readonly StyledProperty<MapViewportSnapshot?> CurrentViewportProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, MapViewportSnapshot?>(nameof(CurrentViewport));

    public static readonly StyledProperty<ICommand?> GeometryEditCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(GeometryEditCommand));

    public static readonly StyledProperty<ICommand?> CompleteGeometryEditCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(CompleteGeometryEditCommand));

    public static readonly StyledProperty<ICommand?> CancelGeometryEditCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(CancelGeometryEditCommand));

    public static readonly StyledProperty<ICommand?> UndoGeometryEditCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(UndoGeometryEditCommand));

    public static readonly StyledProperty<ICommand?> RedoGeometryEditCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(RedoGeometryEditCommand));

    public static readonly StyledProperty<ICommand?> MapGoToCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(MapGoToCommand));

    public static readonly StyledProperty<ICommand?> MapSetHeadingCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(MapSetHeadingCommand));

    public static readonly StyledProperty<ICommand?> MapAssembleCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(MapAssembleCommand));

    public static readonly StyledProperty<ICommand?> MapPointGimbalCommandProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, ICommand?>(nameof(MapPointGimbalCommand));

    public static readonly StyledProperty<OperatorControlsViewModel?> MapOperatorControlsProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, OperatorControlsViewModel?>(nameof(MapOperatorControls));

    public static readonly StyledProperty<UnitsPanelViewModel?> MapUnitsPanelProperty =
        AvaloniaProperty.Register<NativeOperationalMapControl, UnitsPanelViewModel?>(nameof(MapUnitsPanel));

    private readonly MapControl _mapControl;
    private readonly OperationalMapControl _schematicControl;
    private readonly Border _packageMessage;
    private readonly TextBlock _packageTitle;
    private readonly TextBlock _packageDetail;
    private readonly Border _attributionBadge;
    private readonly TextBlock _attributionText;
    private readonly Border _cursorBadge;
    private readonly TextBlock _cursorCoordinates;
    private readonly TextBlock _cursorResolution;
    private readonly AvaloniaPolygon _compassNeedle;
    private readonly StackPanel _compassPanel;
    private readonly TextBlock _scaleText;
    private readonly Border _scaleBar;
    private readonly Canvas _vehicleLabelOverlay;
    private readonly Canvas _dynamicLabelOverlay;
    private readonly Canvas _staticLabelOverlay;
    private readonly MissionPreviewArrowOverlayControl _missionPreviewArrowOverlay;
    private readonly AvaloniaRectangle _selectionBox;
    private readonly Canvas _unitMarkerOverlay;
    private readonly Canvas _unitCameraConeOverlay;
    private readonly VehicleTrailOverlayControl _trailOverlay;
    private MemoryLayer? _policyLayer;
    private MemoryLayer? _geometryLayer;
    private MemoryLayer? _draftGeometryLayer;
    private MemoryLayer? _editHandleLayer;
    private GeometryEditSnapshot _lastRenderedGeometryEdit = GeometryEditSnapshot.Empty;
    private string _lastGeometryRenderKey = string.Empty;
    private MemoryLayer? _goToTargetLayer;
    private MemoryLayer? _operatorLocationLayer;
    private MemoryLayer? _vehicleLayer;
    private readonly Dictionary<string, VehicleAnimationState> _vehicleAnimations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AvaloniaPolygon> _unitOverlayMarkers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AvaloniaPolygon> _unitCameraConeOverlays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _vehicleLabelControls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _geometryLabelControls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AvaloniaPolyline>> _geometryArrowControls = new(StringComparer.Ordinal);
    private IReadOnlyList<MapVehicleTrailVisual> _latestTrails = [];
    private string _lastTrailViewportSignature = string.Empty;
    private string _staticGeometryRenderKey = string.Empty;
    private MapFrameKind _motionFrame = MapFrameKind.Unknown;
    private Border? _operatorLabelControl;
    private Border? _teamLabelControl;
    private readonly Dictionary<string, TileLayer> _onlineTileLayers = new(StringComparer.OrdinalIgnoreCase);
    private TileLayer? _weatherLayer;
    private string? _loadedWeatherFrameId;
    private OperationalMapPresentation? _lastAppliedPresentation;
    private readonly DispatcherTimer _vehicleAnimationTimer;
    private readonly DispatcherTimer _initialNavigationTimer;
    private CancellationTokenSource? _alternateStylePrefetch;
    private string? _loadedMapSignature;
    private bool _loadedMapAvailable;
    private long _lastNavigationRevision = -1;
    private long _lastFollowRevision = -1;
    private readonly MapFollowCameraFilter _followCameraFilter = new();
    private MapViewportSnapshot? _lastRenderedViewport;
    private MapViewportSnapshot? _pendingInitialViewport;
    private bool _mapControlAttached;
    private bool _hasAppliedInitialNavigation;
    private bool _suppressInitialViewportPublication;
    private string? _loadedOnlineStyleId;
    private string? _loadedPresentationStyleId;
    private int? _dragVertexIndex;
    private ContextMenu? _mapContextMenu;
    private string? _mapContextMenuSignature;
    private MapCommandTarget? _mapContextTarget;
    private MapCommandTarget? _mapAssemblyStart;
    private MapCommandTarget? _mapAssemblyEnd;
    private string? _mapAssemblyFormationId;
    private bool _mapAssemblyCapturingStart;
    private bool _mapAssemblyCapturingEnd;
    private bool _mapAssemblyChoosingFormation;
    private bool _mapAwaitingConfirmation;
    private bool _mapConfirmationMenuPending;
    private OperatorControlsViewModel? _mapOperatorControls;
    private UnitsPanelViewModel? _mapUnitsPanel;
    private KeyModifiers _lastMapKeyModifiers;
    private bool _leftPointerDown;
    private bool _middlePanning;
    private bool _boxSelecting;
    private bool _suppressMapTap;
    private Avalonia.Point _boxStart;
    private KeyModifiers _boxModifiers;
    private Avalonia.Point _lastPanPoint;

    public NativeOperationalMapControl()
    {
        if (UnitSettingsService.Instance is { } unitSettings)
        {
            unitSettings.Changed += OnUnitSettingsChanged;
        }
        ClipToBounds = true;
        Focusable = true;
        _vehicleAnimationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _vehicleAnimationTimer.Tick += (_, _) => AnimateVehicleFeatures();
        AttachedToVisualTree += (_, _) => _vehicleAnimationTimer.Start();
        DetachedFromVisualTree += (_, _) => _vehicleAnimationTimer.Stop();
        _initialNavigationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _initialNavigationTimer.Tick += (_, _) => TryApplyInitialNavigation();
        _mapControl = new MapControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            UseContinuousMouseWheelZoom = true,
            UseFling = false
        };
        _mapControl.MapTapped += OnMapTapped;
        _mapControl.MapPointerPressed += OnMapPointerPressed;
        _mapControl.MapPointerMoved += OnMapPointerMoved;
        _mapControl.MapPointerReleased += OnMapPointerReleased;
        _mapControl.AttachedToVisualTree += (_, _) =>
        {
            _mapControlAttached = true;
            Dispatcher.UIThread.Post(() =>
            {
                TryApplyInitialNavigation();

                _mapControl.ForceUpdate();
                ScheduleAlternateStylePrefetch();
            }, DispatcherPriority.Render);
        };
        _mapControl.DetachedFromVisualTree += (_, _) =>
        {
            _mapControlAttached = false;
            CancelAlternateStylePrefetch();
        };
        // Mapsui may mark pointer events handled internally. Listen on the
        // containing control as well, and include already-handled events, so a
        // right-click can still open the operator menu.
        AddHandler(
            InputElement.PointerPressedEvent,
            OnMapControlPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            InputElement.PointerMovedEvent,
            OnMapControlPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            InputElement.PointerReleasedEvent,
            OnMapControlPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnMapControlPointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            InputElement.PointerExitedEvent,
            OnMapControlPointerExited,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        _vehicleLabelOverlay = new Canvas
        {
            IsHitTestVisible = false,
            ClipToBounds = true
        };
        _selectionBox = new AvaloniaRectangle
        {
            IsVisible = false,
            Fill = new SolidColorBrush(Avalonia.Media.Color.FromArgb(35, 80, 160, 255)),
            Stroke = new SolidColorBrush(Avalonia.Media.Color.FromArgb(220, 150, 210, 255)),
            StrokeThickness = 1
        };
        _vehicleLabelOverlay.Children.Add(_selectionBox);
        _dynamicLabelOverlay = new Canvas
        {
            IsHitTestVisible = false,
            ClipToBounds = true
        };
        _staticLabelOverlay = new Canvas
        {
            IsHitTestVisible = false,
            ClipToBounds = true
        };
        _missionPreviewArrowOverlay = new MissionPreviewArrowOverlayControl
        {
            IsHitTestVisible = false,
            ClipToBounds = true
        };
        _unitMarkerOverlay = new Canvas
        {
            IsHitTestVisible = false,
            ClipToBounds = true
        };
        _unitCameraConeOverlay = new Canvas
        {
            IsHitTestVisible = false,
            ClipToBounds = true
        };
        _trailOverlay = new VehicleTrailOverlayControl();

        _schematicControl = new OperationalMapControl();
        _packageTitle = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        _packageDetail = new TextBlock
        {
            FontSize = 12,
            Foreground = AvaloniaBrushes.LightGray,
            MaxWidth = 520,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        _packageMessage = new Border
        {
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(220, 20, 24, 29)),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(70, 78, 90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18),
            MaxWidth = 580,
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel
            {
                Spacing = 7,
                Children =
                {
                    _packageTitle,
                    _packageDetail
                }
            }
        };
        _attributionText = new TextBlock
        {
            FontSize = 10,
            Foreground = AvaloniaBrushes.LightGray,
            TextWrapping = TextWrapping.Wrap
        };
        _attributionBadge = new Border
        {
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(205, 20, 24, 29)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4),
            Margin = new Thickness(8),
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = _attributionText
        };
        _cursorCoordinates = new TextBlock
        {
            FontSize = 10,
            Foreground = AvaloniaBrushes.White,
            Text = "Move over the map for coordinates"
        };
        _cursorResolution = new TextBlock
        {
            FontSize = 9,
            Foreground = AvaloniaBrushes.LightGray,
            Text = "-"
        };
        _cursorBadge = new Border
        {
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(205, 20, 24, 29)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4),
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    _cursorCoordinates,
                    _cursorResolution
                }
            }
        };
        _compassNeedle = new AvaloniaPolygon
        {
            Width = 20,
            Height = 28,
            Points = new AvaloniaPoints { new Avalonia.Point(10, 0), new Avalonia.Point(0, 28), new Avalonia.Point(20, 28) },
            Fill = new SolidColorBrush(Avalonia.Media.Color.FromRgb(239, 91, 91)),
            Stroke = new SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 190, 190)),
            StrokeThickness = 1,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var northLabel = CompassLabel("N", 14);
        var westLabel = CompassLabel("W", 14);
        var eastLabel = CompassLabel("E", 14);
        var southLabel = CompassLabel("S", 14);
        var compassFace = new Canvas { Width = 104, Height = 104 };
        Canvas.SetLeft(northLabel, 40); Canvas.SetTop(northLabel, 8);
        Canvas.SetLeft(westLabel, 4); Canvas.SetTop(westLabel, 43);
        Canvas.SetLeft(_compassNeedle, 42); Canvas.SetTop(_compassNeedle, 38);
        Canvas.SetLeft(eastLabel, 76); Canvas.SetTop(eastLabel, 43);
        Canvas.SetLeft(southLabel, 40); Canvas.SetTop(southLabel, 78);
        compassFace.Children.Add(northLabel);
        compassFace.Children.Add(westLabel);
        compassFace.Children.Add(_compassNeedle);
        compassFace.Children.Add(eastLabel);
        compassFace.Children.Add(southLabel);
        var compassGraphic = new Border
        {
            Width = 108,
            Height = 108,
            CornerRadius = new CornerRadius(54),
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(225, 16, 21, 27)),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(126, 139, 155)),
            BorderThickness = new Thickness(2),
            Child = compassFace
        };
        var compassButton = new Button
        {
            Width = 112,
            Height = 112,
            Padding = new Thickness(0),
            Focusable = false,
            CornerRadius = new CornerRadius(56),
            ClipToBounds = true,
            Background = AvaloniaBrushes.Transparent,
            BorderBrush = AvaloniaBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = compassGraphic
        };
        compassButton.Classes.Add("mapCompassButton");
        ToolTip.SetTip(compassButton, "Click to reset north");
        compassButton.Click += (_, _) => RotateTo(0);
        var rotateLeftButton = new Button { Content = "↶", Width = 32, Height = 30, Padding = new Thickness(0), Focusable = false };
        var rotateRightButton = new Button { Content = "↷", Width = 32, Height = 30, Padding = new Thickness(0), Focusable = false };
        rotateLeftButton.Classes.Add("mapRotationButton");
        rotateRightButton.Classes.Add("mapRotationButton");
        rotateLeftButton.Width = rotateRightButton.Width = 42;
        rotateLeftButton.Height = rotateRightButton.Height = 36;
        rotateLeftButton.FontSize = rotateRightButton.FontSize = 22;
        rotateLeftButton.Background = rotateRightButton.Background =
            new SolidColorBrush(Avalonia.Media.Color.FromArgb(245, 42, 48, 58));
        rotateLeftButton.Foreground = rotateRightButton.Foreground = AvaloniaBrushes.White;
        rotateLeftButton.BorderBrush = rotateRightButton.BorderBrush =
            new SolidColorBrush(Avalonia.Media.Color.FromRgb(126, 139, 155));
        rotateLeftButton.BorderThickness = rotateRightButton.BorderThickness = new Thickness(1);
        rotateLeftButton.HorizontalContentAlignment = rotateRightButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        rotateLeftButton.VerticalContentAlignment = rotateRightButton.VerticalContentAlignment = VerticalAlignment.Center;
        ConfigureRotationButtonPointerStates(rotateLeftButton, rotateRightButton);
        ToolTip.SetTip(rotateLeftButton, "Rotate left");
        ToolTip.SetTip(rotateRightButton, "Rotate right");
        rotateLeftButton.Click += (_, _) => RotateBy(-15);
        rotateRightButton.Click += (_, _) => RotateBy(15);
        _compassPanel = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                compassButton,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { rotateLeftButton, rotateRightButton }
                }
            }
        };
        _scaleText = new TextBlock
        {
            FontSize = 10,
            Foreground = AvaloniaBrushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _scaleBar = new Border
        {
            Width = 110,
            Height = 4,
            BorderBrush = AvaloniaBrushes.White,
            BorderThickness = new Thickness(1),
            Background = AvaloniaBrushes.Transparent,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var scalePanel = new StackPanel
        {
            Children = { _scaleText, _scaleBar }
        };
        var scaleBadge = new Border
        {
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(225, 20, 24, 29)),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(110, 124, 140)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            Margin = new Thickness(150, 10, 10, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = scalePanel
        };

        Children.Add(_mapControl);
        Children.Add(_schematicControl);
        Children.Add(_trailOverlay);
        Children.Add(_vehicleLabelOverlay);
        Children.Add(_dynamicLabelOverlay);
        Children.Add(_staticLabelOverlay);
        Children.Add(_missionPreviewArrowOverlay);
        Children.Add(_unitCameraConeOverlay);
        Children.Add(_unitMarkerOverlay);
        Children.Add(_packageMessage);
        Children.Add(_cursorBadge);
        Children.Add(_attributionBadge);
        _compassPanel.ZIndex = 100;
        Children.Add(_compassPanel);
        Children.Add(scaleBadge);
        ApplyPresentation();
    }

    public OperationalMapPresentation? Presentation
    {
        get => GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    public MapVehicleMotionSnapshot? VehicleMotion
    {
        get => GetValue(VehicleMotionProperty);
        set => SetValue(VehicleMotionProperty, value);
    }

    public ICommand? SelectVehicleCommand
    {
        get => GetValue(SelectVehicleCommandProperty);
        set => SetValue(SelectVehicleCommandProperty, value);
    }

    public ICommand? ClearConnectedClientSelectionCommand
    {
        get => GetValue(ClearConnectedClientSelectionCommandProperty);
        set => SetValue(ClearConnectedClientSelectionCommandProperty, value);
    }

    public ICommand? SelectGeometryCommand
    {
        get => GetValue(SelectGeometryCommandProperty);
        set => SetValue(SelectGeometryCommandProperty, value);
    }

    public bool GeometrySelectionEnabled
    {
        get => GetValue(GeometrySelectionEnabledProperty);
        set => SetValue(GeometrySelectionEnabledProperty, value);
    }

    public ICommand? ViewportChangedCommand
    {
        get => GetValue(ViewportChangedCommandProperty);
        set => SetValue(ViewportChangedCommandProperty, value);
    }

    public ICommand? GeometryEditCommand
    {
        get => GetValue(GeometryEditCommandProperty);
        set => SetValue(GeometryEditCommandProperty, value);
    }

    public ICommand? CompleteGeometryEditCommand
    {
        get => GetValue(CompleteGeometryEditCommandProperty);
        set => SetValue(CompleteGeometryEditCommandProperty, value);
    }

    public ICommand? CancelGeometryEditCommand
    {
        get => GetValue(CancelGeometryEditCommandProperty);
        set => SetValue(CancelGeometryEditCommandProperty, value);
    }

    public ICommand? UndoGeometryEditCommand
    {
        get => GetValue(UndoGeometryEditCommandProperty);
        set => SetValue(UndoGeometryEditCommandProperty, value);
    }

    public ICommand? RedoGeometryEditCommand
    {
        get => GetValue(RedoGeometryEditCommandProperty);
        set => SetValue(RedoGeometryEditCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PresentationProperty)
        {
            if (!_hasAppliedInitialNavigation &&
                change.NewValue is OperationalMapPresentation presentation &&
                presentation.Scene.RequestedViewport is { } requestedViewport)
            {
                // The first presentation can arrive before Mapsui is attached.
                // Keep the startup request until the native map has actually
                // applied it instead of allowing Mapsui's world placeholder to
                // become the initial operator viewport.
                _pendingInitialViewport = requestedViewport;
            }
            ApplyPresentation();
        }
        else if (change.Property == VehicleMotionProperty)
        {
            ApplyMotion(change.NewValue as MapVehicleMotionSnapshot);
        }
        else if (change.Property == MapOperatorControlsProperty)
        {
            if (_mapOperatorControls is not null)
            {
                _mapOperatorControls.PropertyChanged -= OnMapOperatorControlsPropertyChanged;
                _mapOperatorControls.FormationPreviewChanged -= OnMapFormationPreviewChanged;
            }

            _mapOperatorControls = change.NewValue as OperatorControlsViewModel;
            if (_mapOperatorControls is not null)
            {
                _mapOperatorControls.PropertyChanged += OnMapOperatorControlsPropertyChanged;
                _mapOperatorControls.FormationPreviewChanged += OnMapFormationPreviewChanged;
            }
        }
        else if (change.Property == MapUnitsPanelProperty)
        {
            if (_mapUnitsPanel is not null)
                _mapUnitsPanel.PropertyChanged -= OnMapUnitsPanelPropertyChanged;

            _mapUnitsPanel = change.NewValue as UnitsPanelViewModel;
            if (_mapUnitsPanel is not null)
                _mapUnitsPanel.PropertyChanged += OnMapUnitsPanelPropertyChanged;
        }
        else if (change.Property == CurrentViewportProperty &&
                 change.NewValue is MapViewportSnapshot viewport &&
                 !_hasAppliedInitialNavigation &&
                 Presentation?.Scene.RequestedViewport is null &&
                 _pendingInitialViewport is null &&
                 _mapControlAttached &&
                 _mapControl.Bounds.Width > 0 &&
                 _mapControl.Bounds.Height > 0)
        {
            // The binding can deliver the shared snapshot after the first
            // presentation has already created Mapsui's world placeholder.
            // Restore it now rather than waiting for a telemetry or style
            // update to trigger another presentation pass.
            RestoreViewport(viewport);
        }
    }

    private void OnUnitSettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_mapControl.Map is not null)
            {
                UpdateMapWidgets();
                _mapControl.ForceUpdate();
            }
        });
    }

    private void ApplyPresentation()
    {
        var presentation = Presentation ?? OperationalMapPresentation.Empty;
        if (IsWeatherOnlyChange(presentation) &&
            _mapControl.Map is not null &&
            presentation.ShowNativeMap)
        {
            UpdateWeatherLayer(presentation);
            UpdateAttribution(presentation);
            _lastAppliedPresentation = presentation;
            return;
        }

        var scene = presentation.Scene;
        if (!_hasAppliedInitialNavigation &&
            scene.RequestedViewport is null &&
            _pendingInitialViewport is { } pendingInitialViewport)
        {
            scene = scene with { RequestedViewport = pendingInitialViewport };
        }

        if (!presentation.GeometryEdit.IsEditing)
        {
            _dragVertexIndex = null;
        }
        _schematicControl.Scene = scene;
        _schematicControl.IsVisible = presentation.ShowSchematicMap;
        _mapControl.IsVisible = presentation.ShowNativeMap;
        _trailOverlay.IsVisible = presentation.ShowNativeMap && presentation.Scene.TrailsVisible;
        _cursorBadge.IsVisible = presentation.ShowNativeMap;
        _packageMessage.IsVisible = presentation.ShowMapPackageMessage;
        _packageTitle.Text = presentation.Status;
        _packageDetail.Text = presentation.Detail;
        UpdateAttribution(presentation);

        if (!presentation.ShowNativeMap)
        {
            _lastAppliedPresentation = presentation;
            return;
        }

        // A newly created Mapsui map can publish its placeholder viewport
        // before the startup navigation is applied. Preserve the shared
        // viewport whenever there is no explicit startup/navigation request.
        // This matters when Operate is recreated after changing workspaces:
        // the native control is new, but the user's map position is not.
        var startupViewportPending = !_hasAppliedInitialNavigation &&
                                     (scene.RequestedViewport is not null ||
                                      _pendingInitialViewport is not null);
        var viewportBeforeMapRecreation = startupViewportPending
            ? null
            : CurrentViewport ??
              (_hasAppliedInitialNavigation && _vehicleLayer is not null
                  ? _lastRenderedViewport
                  : null);
        var mapRecreated = EnsureMap(presentation);
        var preserveViewport = mapRecreated && viewportBeforeMapRecreation is not null;
        var geometryEditChanged = !Equals(_lastRenderedGeometryEdit, presentation.GeometryEdit);
        var geometryRenderKey = CreateGeometryRenderKey(scene.Geometries);
        var geometryFeaturesChanged = !string.Equals(_lastGeometryRenderKey, geometryRenderKey, StringComparison.Ordinal);
        UpdateWeatherLayer(presentation);
        ReplacePolicyFeatures(scene);
        ReplaceGeometryFeatures(scene);
        ReplaceDraftGeometryFeatures(presentation.GeometryEdit);
        ReplaceEditHandleFeatures(presentation.GeometryEdit);
        UpdateTrailOverlay(scene);
        ReplaceGoToTargetFeatures(scene);
        ReplaceOperatorLocationFeatures(scene);
        var vehicleFeaturesChanged = ReplaceVehicleFeatures(scene);
        RebuildStaticLabelOverlay(scene);
        ApplyMotion(presentation.Motion);
        if (!preserveViewport &&
            (mapRecreated ||
             scene.NavigationRevision != _lastNavigationRevision))
        {
            ApplyOrientation(scene);
        }

        if (preserveViewport && viewportBeforeMapRecreation is { } preservedViewport)
        {
            RestoreViewport(preservedViewport);
        }
        else
        {
            var followRevision = scene.Follow?.Revision ?? -1;
            var followChanged = followRevision != _lastFollowRevision;
            var followStopped = scene.Follow is null && _lastFollowRevision != -1;
            if (mapRecreated ||
                scene.NavigationRevision != _lastNavigationRevision ||
                (followChanged && !followStopped))
            {
                Navigate(scene);
                _lastNavigationRevision = scene.NavigationRevision;
                _lastFollowRevision = followRevision;
            }
        }

        // Telemetry updates move the Avalonia marker overlays and do not need
        // to redraw every map tile. A full graphics refresh here, at ghost
        // simulation frequency, can tear the tile canvas while it is drawing.
        // Refresh only when the vehicle feature set itself changed (add/remove
        // or a recreated map); layer-specific FeaturesWereModified calls
        // handle their own lower-frequency updates.
        if (mapRecreated || vehicleFeaturesChanged || geometryEditChanged || geometryFeaturesChanged)
        {
            _mapControl.Map?.RefreshGraphics();
        }
        _lastRenderedGeometryEdit = presentation.GeometryEdit;
        _lastGeometryRenderKey = geometryRenderKey;
        _lastAppliedPresentation = presentation;
        UpdateMapWidgets();
    }

    private bool IsWeatherOnlyChange(OperationalMapPresentation presentation)
    {
        var previous = _lastAppliedPresentation;
        if (previous is null)
        {
            return false;
        }

        var sameMapState =
            ReferenceEquals(previous.Scene, presentation.Scene) &&
            ReferenceEquals(previous.Motion, presentation.Motion) &&
            Equals(previous.GeometryEdit, presentation.GeometryEdit) &&
            previous.Renderer == presentation.Renderer &&
            previous.OfflineMapPath == presentation.OfflineMapPath &&
            previous.OfflineMapAvailable == presentation.OfflineMapAvailable &&
            previous.OnlineMapFallback == presentation.OnlineMapFallback &&
            Equals(previous.Style, presentation.Style);

        return sameMapState &&
               (previous.WeatherVisible != presentation.WeatherVisible ||
                previous.WeatherOpacity != presentation.WeatherOpacity ||
                !Equals(previous.WeatherRadar, presentation.WeatherRadar));
    }

    private void UpdateAttribution(OperationalMapPresentation presentation)
    {
        var attributions = new[]
        {
            presentation.Attribution,
            presentation.WeatherVisible ? presentation.WeatherRadar?.Attribution : null
        };
        _attributionText.Text = string.Join(" · ", attributions.Where(item => !string.IsNullOrWhiteSpace(item)));
        _attributionBadge.IsVisible = presentation.ShowNativeMap &&
                                      ((presentation.OfflineMapAvailable || presentation.OnlineMapFallback) ||
                                       presentation.WeatherVisible) &&
                                      !string.IsNullOrWhiteSpace(_attributionText.Text);
    }

    private void ApplyMotion(MapVehicleMotionSnapshot? motion)
    {
        if (motion is null || motion.Frame == MapFrameKind.Unknown ||
            _vehicleAnimations.Count == 0 || _mapControl.Map is null)
        {
            return;
        }

        // A projected local frame and WGS84 frame use different coordinate
        // spaces. Never interpolate across that boundary.
        if (_motionFrame != motion.Frame)
        {
            foreach (var animation in _vehicleAnimations.Values)
            {
                animation.ResetSamples();
            }

            _motionFrame = motion.Frame;
        }

        foreach (var sample in motion.Vehicles)
        {
            if (!_vehicleAnimations.TryGetValue(sample.VehicleId, out var animation) ||
                !MapCoordinateProjector.TryProject(sample.X, sample.Y, out var projected))
            {
                continue;
            }

            animation.AddSample(projected.X, projected.Y, sample);
        }

        if (motion.TrailsUpdated && Presentation is { } presentation)
        {
            UpdateTrailOverlay(
                presentation.Scene with { Trails = motion.Trails },
                motion.Trails,
                trailsUpdated: true);
        }

        if (Presentation is { } targetPresentation &&
            (targetPresentation.Scene.GoToTargets.Count > 0 ||
             targetPresentation.Scene.FormationPreviewTargets.Count > 0 ||
             targetPresentation.Scene.LockedTeamPosition is not null))
        {
            ReplaceGoToTargetFeatures(WithRenderedVehiclePositions(targetPresentation.Scene));
        }

        UpdateDynamicVehiclePresentation();
    }

    private void UpdateDynamicVehiclePresentation()
    {
        foreach (var animation in _vehicleAnimations.Values)
        {
            animation.UpdateRenderedVisual();
            UpdateUnitOverlayMarker(animation);
            UpdateUnitCameraCone(animation);
        }

        if (Presentation is { } presentation)
        {
            UpdateVehicleLabelOverlay(presentation.Scene);
        }
    }

    private OperationalMapScene WithRenderedVehiclePositions(OperationalMapScene scene)
    {
        var vehicles = scene.Vehicles
            .Select(vehicle =>
            {
                if (!_vehicleAnimations.TryGetValue(vehicle.VehicleId, out var animation) ||
                    !MapCoordinateProjector.TryUnproject(animation.CurrentX, animation.CurrentY, out var longitude, out var latitude))
                {
                    return vehicle;
                }

                return vehicle with { X = longitude, Y = latitude, HeadingDegrees = animation.Visual.HeadingDegrees };
            })
            .ToArray();
        return scene with { Vehicles = vehicles };
    }

    private bool EnsureMap(OperationalMapPresentation presentation)
    {
        OfflineMapLayerDefinition[] layers = presentation.OfflineLayers.Count > 0
            ? presentation.OfflineLayers.OrderBy(item => item.Order).ToArray()
            : presentation.OfflineMapPath is null
                ? []
                : [new OfflineMapLayerDefinition(presentation.OfflineMapPath, "basemap", 0)];
        var signature = string.Join("|", layers.Select(item => $"{item.Order}:{item.Role}:{item.Path}"));
        var onlineStyleId = OnlineMapTileSources.NormalizeStyleId(presentation.Style?.StyleId);
        var usesOnlineLayers = presentation.OnlineMapFallback || !presentation.OfflineMapAvailable;
        if (_vehicleLayer is not null &&
            string.Equals(_loadedMapSignature, signature, StringComparison.Ordinal) &&
            _loadedMapAvailable == presentation.OfflineMapAvailable &&
            (usesOnlineLayers
                ? _onlineTileLayers.Count > 0
                : string.Equals(
                    _loadedPresentationStyleId,
                    presentation.Style?.StyleId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            ActivateOnlineStyle(onlineStyleId);
            return false;
        }

        CancelAlternateStylePrefetch();
        var previousMap = _mapControl.Map;
        previousMap.Navigator.ViewportChanged -= OnViewportChanged;
        var map = new Map
        {
            BackColor = MapsuiColor.FromString("#12171d")
        };
        // Left drag is reserved for box selection. Panning is implemented
        // explicitly from the middle mouse button handlers below.
        map.Navigator.PanLock = true;
        map.Navigator.ZoomLock = false;
        map.Navigator.RotationLock = false;
        map.Navigator.ViewportChanged += OnViewportChanged;
        _loadedMapSignature = signature;
        _loadedMapAvailable = presentation.OfflineMapAvailable;
        _loadedOnlineStyleId = onlineStyleId;
        _loadedPresentationStyleId = presentation.Style?.StyleId;
        _onlineTileLayers.Clear();
        _weatherLayer = null;
        _loadedWeatherFrameId = null;

        if (presentation.OfflineMapAvailable && layers.Length > 0)
        {
            try
            {
                foreach (var layer in layers)
                {
                    var source = new MbTilesTileSource(new SQLiteConnectionString(layer.Path, true));
                    map.Layers.Add(new TileLayer(source)
                    {
                        Name = layer.Order == 0
                            ? MapLayerCatalog.OfflineBaseMap
                            : $"Offline {layer.Role} {layer.Order}"
                    });
                }
            }
            catch (Exception ex)
            {
                _packageMessage.IsVisible = true;
                _attributionBadge.IsVisible = false;
                _packageTitle.Text = "Offline map presentation could not be opened";
                _packageDetail.Text = ex.Message;
            }
        }
        else if (presentation.OnlineMapFallback || !presentation.OfflineMapAvailable)
        {
            // Keep the global map usable whenever no offline basemap layer
            // was supplied. This also protects startup from stale or
            // overlay-only package metadata that would otherwise leave only
            // Mapsui's empty grid visible.
            foreach (var styleId in OnlineMapTileSources.StyleIds)
            {
                var layer = new TileLayer(
                    OnlineMapTileSources.Create(styleId),
                    minTiles: 300,
                    maxTiles: 800)
                {
                    Name = $"Online base map · {styleId}",
                    Enabled = string.Equals(styleId, onlineStyleId, StringComparison.OrdinalIgnoreCase),
                    Opacity = string.Equals(styleId, onlineStyleId, StringComparison.OrdinalIgnoreCase) ? 1 : 0
                };
                _onlineTileLayers.Add(styleId, layer);
                map.Layers.Add(layer);
            }
        }

        if (presentation.WeatherVisible && presentation.WeatherRadar?.HasFrame == true)
        {
            _weatherLayer = CreateWeatherLayer(presentation.WeatherRadar, presentation.WeatherOpacity);
            _loadedWeatherFrameId = presentation.WeatherRadar.FrameId;
            map.Layers.Add(_weatherLayer);
        }

        _policyLayer = CreateMemoryLayer(MapLayerCatalog.PolicyGeometry);
        _geometryLayer = CreateMemoryLayer(MapLayerCatalog.OperationalGeometry);
        _draftGeometryLayer = CreateMemoryLayer(MapLayerCatalog.DraftGeometry);
        _editHandleLayer = CreateMemoryLayer(MapLayerCatalog.GeometryEditHandles);
        _goToTargetLayer = CreateMemoryLayer("GoTo target");
        _operatorLocationLayer = CreateMemoryLayer("Operator location");
        _vehicleLayer = CreateMemoryLayer(MapLayerCatalog.Vehicles);
        _vehicleAnimations.Clear();
        _unitOverlayMarkers.Clear();
        _vehicleLabelControls.Clear();
        _vehicleLabelOverlay.Children.Clear();
        _vehicleLabelOverlay.Children.Add(_selectionBox);
        _dynamicLabelOverlay.Children.Clear();
        _unitMarkerOverlay.Children.Clear();
        map.Layers.Add(_policyLayer);
        map.Layers.Add(_geometryLayer);
        map.Layers.Add(_draftGeometryLayer);
        map.Layers.Add(_editHandleLayer);
        map.Layers.Add(_goToTargetLayer);
        map.Layers.Add(_operatorLocationLayer);
        map.Layers.Add(_vehicleLayer);
        // Assigning a Mapsui map synchronously emits its full-world
        // placeholder viewport. Do not publish that transient value through
        // the binding: the caller restores the shared viewport immediately
        // after this method returns.
        _suppressInitialViewportPublication = true;
        _mapControl.Map = map;
        _mapControl.RefreshData(ChangeType.Discrete);
        return true;
    }

    private static TileLayer CreateWeatherLayer(WeatherRadarSnapshot snapshot, double opacity)
        => new(
            OnlineMapTileSources.CreateWeather(snapshot),
            minTiles: 100,
            maxTiles: 400)
        {
            Name = "Weather radar · RainViewer",
            Enabled = true,
            Opacity = Math.Clamp(opacity, 0.2, 0.85)
        };

    private void UpdateWeatherLayer(OperationalMapPresentation presentation)
    {
        if (_mapControl.Map is null)
        {
            return;
        }

        var snapshot = presentation.WeatherRadar;
        var visible = presentation.ShowNativeMap &&
                      presentation.WeatherVisible &&
                      snapshot?.HasFrame == true;
        if (!visible)
        {
            if (_weatherLayer is not null)
            {
                _weatherLayer.Enabled = false;
                _weatherLayer.Opacity = 0;
                _mapControl.ForceUpdate();
            }

            return;
        }

        if (snapshot is not { HasFrame: true } weatherSnapshot)
        {
            return;
        }

        if (_weatherLayer is null ||
            !string.Equals(_loadedWeatherFrameId, weatherSnapshot.FrameId, StringComparison.Ordinal))
        {
            if (_weatherLayer is not null)
            {
                _mapControl.Map.Layers.Remove(_weatherLayer);
            }

            _weatherLayer = CreateWeatherLayer(weatherSnapshot, presentation.WeatherOpacity);
            _loadedWeatherFrameId = weatherSnapshot.FrameId;
            var layers = _mapControl.Map.Layers.GetLayers(0).ToList();
            var insertIndex = _policyLayer is null
                ? layers.Count
                : layers.IndexOf(_policyLayer);
            if (insertIndex < 0)
            {
                insertIndex = _mapControl.Map.Layers.Count;
            }

            _mapControl.Map.Layers.Insert(insertIndex, _weatherLayer, 0);
            _mapControl.RefreshData(ChangeType.Discrete);
        }
        else
        {
            _weatherLayer.Enabled = true;
            _weatherLayer.Opacity = Math.Clamp(presentation.WeatherOpacity, 0.2, 0.85);
        }

        _mapControl.ForceUpdate();
    }

    private static MemoryLayer CreateMemoryLayer(string name)
        => new(name)
        {
            Style = null,
            Features = Array.Empty<IFeature>()
        };

    private void ActivateOnlineStyle(string styleId)
    {
        if (_onlineTileLayers.Count == 0 ||
            string.Equals(_loadedOnlineStyleId, styleId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var (candidateStyleId, layer) in _onlineTileLayers)
        {
            layer.Enabled = true;
            layer.Opacity = string.Equals(candidateStyleId, styleId, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        _loadedOnlineStyleId = styleId;
        // Changing Enabled on a layer only changes its render eligibility.
        // Explicitly refresh the map data so Mapsui asks the newly enabled
        // tile source for the current viewport.
        _mapControl.RefreshData(ChangeType.Discrete);
        _mapControl.ForceUpdate();
        // A style switch retains the existing navigator rather than causing a
        // viewport event. Publish the unchanged viewport so the view model can
        // finish its explicit style-preservation request and accept subsequent
        // operator pan/zoom updates normally.
        PublishViewportSnapshot();
        ScheduleAlternateStylePrefetch();
    }

    public ICommand? UserPannedCommand
    {
        get => GetValue(UserPannedCommandProperty);
        set => SetValue(UserPannedCommandProperty, value);
    }

    private static void ConfigureRotationButtonPointerStates(params Button[] buttons)
    {
        foreach (var button in buttons)
        {
            ApplyRotationButtonState(button, highlighted: false);
            button.PointerEntered += (_, _) => ApplyRotationButtonState(button, highlighted: true);
            button.PointerPressed += (_, _) => ApplyRotationButtonState(button, highlighted: true);
            button.PointerReleased += (_, _) => ApplyRotationButtonState(button, highlighted: true);
            button.PointerExited += (_, _) => ApplyRotationButtonState(button, highlighted: false);
        }
    }

    private static void ApplyRotationButtonState(Button button, bool highlighted)
    {
        button.Background = new SolidColorBrush(
            highlighted ? Avalonia.Media.Color.FromRgb(70, 84, 101) : Avalonia.Media.Color.FromArgb(245, 42, 48, 58));
        button.Foreground = AvaloniaBrushes.White;
        button.BorderBrush = new SolidColorBrush(
            highlighted ? Avalonia.Media.Color.FromRgb(190, 204, 220) : Avalonia.Media.Color.FromRgb(126, 139, 155));
        button.BorderThickness = new Thickness(1);
        button.Opacity = 1;
    }

    private void RotateBy(double degrees)
    {
        if (_mapControl.Map is null)
        {
            return;
        }

        var rotation = _mapControl.Map.Navigator.Viewport.Rotation + degrees;
        RotateTo(rotation);
    }

    public MapViewportSnapshot? CurrentViewport
    {
        get => GetValue(CurrentViewportProperty);
        set => SetValue(CurrentViewportProperty, value);
    }

    public ICommand? MapGoToCommand
    {
        get => GetValue(MapGoToCommandProperty);
        set => SetValue(MapGoToCommandProperty, value);
    }

    public ICommand? MapSetHeadingCommand
    {
        get => GetValue(MapSetHeadingCommandProperty);
        set => SetValue(MapSetHeadingCommandProperty, value);
    }

    public ICommand? MapAssembleCommand
    {
        get => GetValue(MapAssembleCommandProperty);
        set => SetValue(MapAssembleCommandProperty, value);
    }

    public ICommand? MapPointGimbalCommand
    {
        get => GetValue(MapPointGimbalCommandProperty);
        set => SetValue(MapPointGimbalCommandProperty, value);
    }

    public OperatorControlsViewModel? MapOperatorControls
    {
        get => GetValue(MapOperatorControlsProperty);
        set => SetValue(MapOperatorControlsProperty, value);
    }

    public UnitsPanelViewModel? MapUnitsPanel
    {
        get => GetValue(MapUnitsPanelProperty);
        set => SetValue(MapUnitsPanelProperty, value);
    }

    private void RotateTo(double degrees)
    {
        if (_mapControl.Map is null)
        {
            return;
        }

        _mapControl.Map.Navigator.RotateTo(degrees, duration: 0);
        _mapControl.ForceUpdate();
        UpdateMapWidgets();
        // Keep compass rotations in the shared viewport immediately. Without
        // this, a subsequent presentation refresh can restore the old angle.
        PublishViewportSnapshot();
    }

    private void UpdateMapWidgets()
    {
        var viewport = _mapControl.Map.Navigator.Viewport;
        _compassNeedle.RenderTransform = new RotateTransform(-viewport.Rotation);
        var metres = Math.Max(1, viewport.Resolution * 110);
        _scaleText.Text = UnitFormatting.AdaptiveDistance(
            metres,
            UnitSettingsService.Instance?.Current.HorizontalDistance ?? DistanceUnit.Meters);
        foreach (var animation in _vehicleAnimations.Values)
        {
            UpdateUnitOverlayMarker(animation);
            UpdateUnitCameraCone(animation);
        }
    }

    private static TextBlock CompassLabel(string text, double fontSize)
        => new()
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = AvaloniaBrushes.White,
            Width = 24,
            Height = 18,
            TextAlignment = TextAlignment.Center,
        };

    private void ReplacePolicyFeatures(OperationalMapScene scene)
    {
        if (_policyLayer is null)
        {
            return;
        }

        _policyLayer.Enabled = scene.PolicyVisible;
        _policyLayer.Features = scene.PolicyVisible
            ? CreateGeometryFeatures(scene.Geometries.Where(item => item.IsPolicy), policy: true)
            : Array.Empty<IFeature>();
        _policyLayer.FeaturesWereModified();
    }

    private void ReplaceGeometryFeatures(OperationalMapScene scene)
    {
        if (_geometryLayer is null)
        {
            return;
        }

        var missionPreviewVisible = scene.Geometries.Any(item => !item.IsPolicy && IsFlightMissionPreview(item));
        _geometryLayer.Enabled = scene.GeometryVisible || missionPreviewVisible;
        _geometryLayer.Features = scene.GeometryVisible || missionPreviewVisible
            ? CreateGeometryFeatures(scene.Geometries.Where(item => !item.IsPolicy && (scene.GeometryVisible || IsFlightMissionPreview(item))), policy: false)
            : Array.Empty<IFeature>();
        _geometryLayer.FeaturesWereModified();
    }

    private static string CreateGeometryRenderKey(IEnumerable<MapGeometryVisual> geometries)
        => string.Join("|", geometries
            .Where(item => !item.IsPolicy)
            .OrderBy(item => item.GeometryId, StringComparer.Ordinal)
            .Select(item => $"{item.GeometryId}:{item.Name}:{item.Kind}:{item.Highlighted}:{item.Closed}:{item.Points.Count}:{item.Rings.Count}"));

    private static List<IFeature> CreateGeometryFeatures(
        IEnumerable<MapGeometryVisual> geometries,
        bool policy)
    {
        var features = new List<IFeature>();
        foreach (var geometry in geometries)
        {
            var style = CreateGeometryStyle(geometry, policy);
            if (geometry.Rings.Count > 0)
            {
                foreach (var ring in geometry.Rings)
                {
                    var polygon = CreatePolygon(ring);
                    if (polygon is null)
                    {
                        continue;
                    }

                    var feature = CreateGeometryFeature(geometry, polygon, style);
                    features.Add(feature);
                }

                continue;
            }

            if (geometry.Points.Count == 1 &&
                TryProject(geometry.Points[0], out var singlePoint))
            {
                var feature = new PointFeature(singlePoint.X, singlePoint.Y);
                feature["geometryId"] = geometry.GeometryId;
                feature["name"] = geometry.Name;
                feature.Styles.Add(CreateGeometryPointStyle(geometry, policy));
                features.Add(feature);
            }
            else if (geometry.Points.Count >= 2)
            {
                NtsGeometry? ntsGeometry = geometry.Closed
                    ? CreatePolygon(geometry.Points)
                    : CreateLineString(geometry.Points);
                if (ntsGeometry is not null)
                {
                    features.Add(CreateGeometryFeature(geometry, ntsGeometry, style));
                }
            }
        }

        return features;
    }

    private static GeometryFeature CreateGeometryFeature(
        MapGeometryVisual geometry,
        NtsGeometry ntsGeometry,
        VectorStyle style)
    {
        var feature = new GeometryFeature(ntsGeometry);
        feature["geometryId"] = geometry.GeometryId;
        feature["name"] = geometry.Name;
        feature["policy"] = geometry.IsPolicy;
        feature["highlighted"] = geometry.Highlighted;
        feature.Styles.Add(style);
        return feature;
    }

    private static VectorStyle CreateGeometryStyle(MapGeometryVisual geometry, bool policy)
    {
        var highlighted = geometry.Highlighted;
        var missionPreview = IsFlightMissionPreview(geometry);
        var fence = IsPx4Fence(geometry);
        var exclusionFence = IsPx4ExclusionFence(geometry);
        var lineColor = highlighted
            ? MapsuiColor.FromString("#F6C453")
            : missionPreview
                ? MapsuiColor.FromString(MapDrawingPrimitives.MissionPreviewAccentHex)
            : fence
                ? MapsuiColor.FromString(exclusionFence ? "#F97316" : "#22C55E")
                : policy
                ? MapsuiColor.FromString("#F87171")
                : MapsuiColor.FromString("#6FAFC9");
        var fillColor = highlighted
            ? new MapsuiColor(246, 196, 83, 44)
            : missionPreview
                ? new MapsuiColor(239, 68, 68, 44)
            : fence
                ? exclusionFence ? new MapsuiColor(249, 115, 22, 24) : new MapsuiColor(34, 197, 94, 24)
            : policy
                ? new MapsuiColor(248, 113, 113, 50)
                : new MapsuiColor(111, 175, 201, 30);
        // Mapsui widths are map-renderer units rather than Avalonia pixels;
        // the authoring preview's 3 px stroke is therefore represented by a
        // much smaller native-map width at normal zoom levels.
        var pen = new MapsuiPen(lineColor, highlighted ? 2.5 : missionPreview ? 1.25 : policy || fence ? 2 : 1.25);
        if (!policy && IsWaypointSequence(geometry))
        {
            pen.PenStyle = PenStyle.Dash;
            pen.DashArray = [5, 4];
        }

        return new VectorStyle
        {
            Line = pen,
            Outline = pen,
            Fill = new MapsuiBrush(fillColor)
        };
    }

    private static SymbolStyle CreateGeometryPointStyle(MapGeometryVisual geometry, bool policy)
    {
        var captureMarker = IsMissionCaptureMarker(geometry);
        var captureColor = geometry.Kind.EndsWith(nameof(FlightMissionCameraActionKind.StartVideo), StringComparison.OrdinalIgnoreCase) ||
                           geometry.Kind.EndsWith(nameof(FlightMissionCameraActionKind.StopVideo), StringComparison.OrdinalIgnoreCase)
            ? MapsuiColor.FromString("#60A5FA")
            : MapsuiColor.FromString("#F97316");
        var color = geometry.Highlighted
            ? MapsuiColor.FromString("#F6C453")
            : captureMarker
                ? captureColor
            : IsFlightMissionPreview(geometry)
                ? MapsuiColor.FromString(MapDrawingPrimitives.MissionPreviewAccentHex)
            : policy
                ? MapsuiColor.FromString("#F87171")
                : MapsuiColor.FromString("#6FAFC9");
        return new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse,
            SymbolScale = captureMarker ? 0.82 : geometry.Highlighted || IsFlightMissionPreview(geometry) ? 0.58 : 0.42,
            Fill = new MapsuiBrush(color),
            Outline = new MapsuiPen(MapsuiColor.FromString("#10161D"), 1)
        };
    }

    private static bool IsWaypointSequence(MapGeometryVisual geometry)
        => string.Equals(
            geometry.Kind,
            nameof(GeometryDocumentKind.WaypointSequence),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsFlightMissionPreview(MapGeometryVisual geometry)
        => geometry.Kind.StartsWith("FlightMissionPreview", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissionCaptureMarker(MapGeometryVisual geometry)
        => geometry.Kind.StartsWith("FlightMissionPreviewCaptureMarker:", StringComparison.OrdinalIgnoreCase);

    private static bool IsPx4Fence(MapGeometryVisual geometry)
        => geometry.Kind.StartsWith("Px4Fence", StringComparison.OrdinalIgnoreCase)
           || geometry.Kind.StartsWith("Fence", StringComparison.OrdinalIgnoreCase);

    private static bool IsPx4ExclusionFence(MapGeometryVisual geometry)
        => string.Equals(geometry.Kind, "Px4FenceExclusion", StringComparison.OrdinalIgnoreCase)
           || string.Equals(geometry.Kind, "FenceExclusion", StringComparison.OrdinalIgnoreCase);

    private void ReplaceDraftGeometryFeatures(GeometryEditSnapshot edit)
    {
        if (_draftGeometryLayer is null)
        {
            return;
        }

        var features = new List<IFeature>();
        var vertices = edit.Vertices;
        var document = edit.Draft;
        if (document is not null && vertices.Count > 0)
        {
            if (document.Kind == GeometryDocumentKind.PointOfInterest &&
                TryProject(vertices[0], out var point))
            {
                var feature = new PointFeature(point.X, point.Y);
                feature["draftGeometryId"] = document.GeometryId;
                feature.Styles.Add(new SymbolStyle
                {
                    SymbolType = SymbolType.Ellipse,
                    // A one-point draft has no line geometry to make its
                    // location apparent. Keep the PoI marker deliberately
                    // prominent against every supported basemap.
                    SymbolScale = 1.45,
                    Fill = new MapsuiBrush(MapsuiColor.FromString("#F6C453")),
                    Outline = new MapsuiPen(MapsuiColor.FromString("#111827"), 3)
                });
                features.Add(feature);
            }
            else
            {
                NtsGeometry? shape = document.Kind == GeometryDocumentKind.Zone && vertices.Count >= 3
                    ? CreatePolygon(vertices)
                    : CreateLineString(vertices);
                if (shape is not null)
                {
                    var feature = new GeometryFeature(shape);
                    feature["draftGeometryId"] = document.GeometryId;
                    feature.Styles.Add(new VectorStyle
                    {
                        Line = new MapsuiPen(MapsuiColor.FromString("#22D3EE"), 4),
                        Outline = new MapsuiPen(MapsuiColor.FromString("#22D3EE"), 4),
                        Fill = document.Kind == GeometryDocumentKind.Zone
                            ? new MapsuiBrush(new MapsuiColor(34, 211, 238, 48))
                            : null
                    });
                    features.Add(feature);
                }
            }
        }

        _draftGeometryLayer.Enabled = edit.HasDraft;
        _draftGeometryLayer.Features = features;
        _draftGeometryLayer.FeaturesWereModified();
    }

    private void ReplaceEditHandleFeatures(GeometryEditSnapshot edit)
    {
        if (_editHandleLayer is null)
        {
            return;
        }

        var features = new List<IFeature>();
        if (edit.IsEditing)
        {
            foreach (var handle in edit.Handles)
            {
                if (!TryProject(handle.Point, out var point))
                {
                    continue;
                }

                var feature = new PointFeature(point.X, point.Y);
                feature["vertexIndex"] = handle.VertexIndex;
                feature.Styles.Add(new SymbolStyle
                {
                    SymbolType = SymbolType.Ellipse,
                    SymbolScale = handle.Selected ? 1.0 : 0.78,
                    Fill = new MapsuiBrush(handle.Selected
                        ? MapsuiColor.FromString("#F6C453")
                        : MapsuiColor.FromString("#FFFFFF")),
                    Outline = new MapsuiPen(MapsuiColor.FromString("#083344"), handle.Selected ? 3 : 2)
                });
                features.Add(feature);
            }
        }

        _editHandleLayer.Enabled = edit.IsEditing;
        _editHandleLayer.Features = features;
        _editHandleLayer.FeaturesWereModified();
    }

    private void UpdateTrailOverlay(
        OperationalMapScene scene,
        IReadOnlyList<MapVehicleTrailVisual>? trails = null,
        bool trailsUpdated = false,
        bool forceReproject = false)
    {
        if (_mapControl.Map is null)
        {
            return;
        }

        if (!trailsUpdated && scene.Trails.Count == 0 && !scene.TrailsVisible)
        {
            _latestTrails = [];
            _trailOverlay.UpdateTrails([], scene.Frame, ProjectWorldToScreen, false);
            return;
        }

        var currentTrails = trails ?? scene.Trails;
        _latestTrails = currentTrails;

        _trailOverlay.UpdateTrails(
            currentTrails,
            scene.Frame,
            ProjectWorldToScreen,
            scene.TrailsVisible,
            forceReproject);
    }

    private Avalonia.Point ProjectWorldToScreen(double x, double y)
    {
        if (_mapControl.Map is null)
        {
            return new Avalonia.Point(double.NaN, double.NaN);
        }

        var screen = _mapControl.Map.Navigator.Viewport.WorldToScreen(x, y);
        return new Avalonia.Point(screen.X, screen.Y);
    }

    private void ReplaceGoToTargetFeatures(OperationalMapScene scene)
    {
        if (_goToTargetLayer is null)
            return;

        var features = new List<IFeature>();
        foreach (var path in scene.FormationPreviewPaths)
        {
            if (scene.Frame != MapFrameKind.GlobalWgs84 || path.Points.Count < 2)
                continue;

            var coordinates = new List<Coordinate>(path.Points.Count);
            foreach (var point in path.Points)
            {
                if (!MapCoordinateProjector.TryProject(
                        point.LongitudeDegrees,
                        point.LatitudeDegrees,
                        out var projected))
                {
                    coordinates.Clear();
                    break;
                }

                coordinates.Add(new Coordinate(projected.X, projected.Y));
            }

            if (coordinates.Count < 2)
                continue;

            var outline = new GeometryFeature(new LineString(coordinates.ToArray()));
            var pen = new MapsuiPen(new MapsuiColor(255, 255, 255, 190), 1)
            {
                PenStyle = PenStyle.Dash,
                DashArray = [3, 3]
            };
            outline.Styles.Add(new VectorStyle { Line = pen });
            features.Add(outline);
        }

        var targets = scene.FormationPreviewTargets
            .Concat(scene.GoToTargets)
            .ToArray();
        if (targets.Length == 0 && scene.GoToTarget is not null)
            targets = [scene.GoToTarget];
        foreach (var target in targets)
        {
            var vehicle = scene.Vehicles.FirstOrDefault(item => item.VehicleId == target.VehicleId);
            if (scene.Frame != MapFrameKind.GlobalWgs84 || vehicle is null ||
                !MapCoordinateProjector.TryProject(target.LongitudeDegrees, target.LatitudeDegrees, out var targetPoint) ||
                !MapCoordinateProjector.TryProject(vehicle.X, vehicle.Y, out var vehiclePoint))
                continue;

            var line = new LineString([
                new Coordinate(vehiclePoint.X, vehiclePoint.Y),
                new Coordinate(targetPoint.X, targetPoint.Y)]);
            var connector = new GeometryFeature(line);
            var lineAlpha = target.PreviewOnly
                ? 45
                : target.Selected ? 255 : 80;
            var pen = new MapsuiPen(new MapsuiColor(255, 255, 255, lineAlpha), 1)
            {
                PenStyle = PenStyle.Dash,
                DashArray = [4, 4]
            };
            connector.Styles.Add(new VectorStyle { Line = pen });
            features.Add(connector);

            var circle = new PointFeature(targetPoint.X, targetPoint.Y);
            var alpha = target.PreviewOnly
                ? 55
                : target.Selected ? 255 : 80;
            circle.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse,
                SymbolScale = 0.7,
                Fill = new MapsuiBrush(new MapsuiColor(255, 255, 255, target.Selected ? 18 : 6)),
                Outline = new MapsuiPen(new MapsuiColor(255, 255, 255, alpha), 2)
            });
            features.Add(circle);
        }

        // A locked Team has a virtual, stable Team position. It is intentionally
        // rendered separately from a vehicle target because it is not an
        // individual aircraft and does not move when membership changes.
        if (scene.LockedTeamPosition is { } formation && scene.Frame == MapFrameKind.GlobalWgs84 &&
            MapCoordinateProjector.TryProject(formation.LongitudeDegrees, formation.LatitudeDegrees, out var teamPoint))
        {
            if (formation.MemberTargets is { Count: > 1 } memberTargets)
            {
                var outlineCoordinates = new List<Coordinate>();
                foreach (var target in memberTargets)
                {
                    if (MapCoordinateProjector.TryProject(target.LongitudeDegrees, target.LatitudeDegrees, out var point))
                        outlineCoordinates.Add(new Coordinate(point.X, point.Y));
                }
                if (outlineCoordinates.Count > 1)
                {
                    var outline = new GeometryFeature(new LineString(outlineCoordinates.Concat([outlineCoordinates[0]]).ToArray()));
                    outline.Styles.Add(new VectorStyle
                    {
                        Line = new MapsuiPen(new MapsuiColor(246, 196, 83, formation.Transforming ? 230 : 150), 1.5)
                        {
                            PenStyle = PenStyle.Dash,
                            DashArray = [3, 3]
                        }
                    });
                    features.Add(outline);
                }
            }

            if (formation.Moving && formation.TargetLatitudeDegrees is { } targetLatitude && formation.TargetLongitudeDegrees is { } targetLongitude &&
                MapCoordinateProjector.TryProject(targetLongitude, targetLatitude, out var targetPoint))
            {
                var line = new GeometryFeature(new LineString([
                    new Coordinate(teamPoint.X, teamPoint.Y), new Coordinate(targetPoint.X, targetPoint.Y)]));
                line.Styles.Add(new VectorStyle
                {
                    Line = new MapsuiPen(new MapsuiColor(246, 196, 83, 220), 2) { PenStyle = PenStyle.Dash, DashArray = [4, 3] }
                });
                features.Add(line);
            }

            var marker = new PointFeature(teamPoint.X, teamPoint.Y);
            marker.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse,
                SymbolScale = 0.5,
                Fill = new MapsuiBrush(new MapsuiColor(246, 196, 83, 220)),
                Outline = new MapsuiPen(new MapsuiColor(255, 255, 255, 240), 1.5)
            });
            features.Add(marker);
        }

        _goToTargetLayer.Enabled = features.Count > 0;
        _goToTargetLayer.Features = features;
        _goToTargetLayer.FeaturesWereModified();
    }

    private void ReplaceOperatorLocationFeatures(OperationalMapScene scene)
    {
        if (_operatorLocationLayer is null)
        {
            return;
        }

        if (scene.Frame != MapFrameKind.GlobalWgs84 ||
            !scene.OperatorLocation.IsAvailable ||
            scene.OperatorLocation.LongitudeDegrees is not { } longitude ||
            scene.OperatorLocation.LatitudeDegrees is not { } latitude ||
            !MapCoordinateProjector.TryProject(longitude, latitude, out var projected))
        {
            _operatorLocationLayer.Enabled = false;
            _operatorLocationLayer.Features = Array.Empty<IFeature>();
            _operatorLocationLayer.FeaturesWereModified();
            return;
        }

        var feature = new PointFeature(projected.X, projected.Y);
        feature["operatorLocation"] = true;
        feature.Styles.Add(new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse,
            SymbolScale = 0.8,
            Fill = new MapsuiBrush(MapsuiColor.FromString("#60A5FA")),
            Outline = new MapsuiPen(MapsuiColor.FromString("#FFFFFF"), 2)
        });
        _operatorLocationLayer.Enabled = true;
        _operatorLocationLayer.Features = [feature];
        _operatorLocationLayer.FeaturesWereModified();
    }

    private bool ReplaceVehicleFeatures(OperationalMapScene scene)
    {
        if (_vehicleLayer is null)
        {
            return false;
        }

        var features = new List<IFeature>();
        var liveVehicleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vehicle in scene.Vehicles)
        {
            if (!MapCoordinateProjector.TryProject(vehicle.X, vehicle.Y, out var projected))
            {
                continue;
            }

            liveVehicleIds.Add(vehicle.VehicleId);
            if (_vehicleAnimations.TryGetValue(vehicle.VehicleId, out var animation))
            {
                animation.SetTarget(projected.X, projected.Y, vehicle);
            }
            else
            {
                var feature = new PointFeature(projected.X, projected.Y);
                feature["vehicleId"] = vehicle.VehicleId;
                feature["name"] = vehicle.Name;
                feature.Styles.Add(CreateVehicleHitTestStyle());
                animation = new VehicleAnimationState(
                    feature, projected.X, projected.Y, projected.X, projected.Y, vehicle);
                _vehicleAnimations[vehicle.VehicleId] = animation;
            }

            // Keep an invisible Mapsui feature for hit testing. The visible
            // marker is always the shared Avalonia arrow overlay, so every
            // backend uses the same geometry.
            features.Add(animation.Feature);
            EnsureUnitOverlayMarker(vehicle.VehicleId);
            UpdateUnitOverlayMarker(animation);
            EnsureUnitCameraCone(vehicle.VehicleId);
            UpdateUnitCameraCone(animation);
        }

        foreach (var staleVehicleId in _vehicleAnimations.Keys.Where(id => !liveVehicleIds.Contains(id)).ToArray())
        {
            _vehicleAnimations.Remove(staleVehicleId);
            if (_unitOverlayMarkers.Remove(staleVehicleId, out var marker))
            {
                _unitMarkerOverlay.Children.Remove(marker);
            }
            if (_unitCameraConeOverlays.Remove(staleVehicleId, out var cone))
            {
                _unitCameraConeOverlay.Children.Remove(cone);
            }
        }

        // Keep the existing feature collection when the set of vehicles has not
        // changed. Replacing it for every telemetry sample causes Mapsui to
        // rebuild its spatial index while a ghost is moving, which presents as
        // marker jitter even when the simulated coordinates are smooth.
        var featureSetChanged = _vehicleLayer.Features.Count() != features.Count ||
                                !_vehicleLayer.Features.SequenceEqual(features);
        if (featureSetChanged)
        {
            _vehicleLayer.Features = features;
            _vehicleLayer.FeaturesWereModified();
        }

        return featureSetChanged;
    }

    private void AnimateVehicleFeatures()
    {
        if (!IsVisible)
        {
            return;
        }

        if (_vehicleLayer is not null && _vehicleAnimations.Count > 0)
        {
            foreach (var animation in _vehicleAnimations.Values)
            {
                animation.UpdateRenderedVisual();
                UpdateUnitOverlayMarker(animation);
                UpdateUnitCameraCone(animation);
            }

            UpdateFollowCamera();
        }

        // Label controls are screen-space overlays.  Refreshing them only from
        // Mapsui viewport notifications makes them visibly lag behind during
        // a drag/zoom, especially when the viewport is being updated in small
        // bursts.  The existing 60 Hz visual timer keeps labels, geometry names,
        // and the operator label attached to the current map viewport.
        if (Presentation is { } presentation)
        {
            UpdateVehicleLabelOverlay(presentation.Scene);
        }

        // Trail geometry is screen-space, while its cached data is world-space.
        // Reproject on the same visual cadence as interpolated markers so a
        // low-rate MAVLink source cannot leave a trail behind during a pan.
        ReprojectTrailOverlayIfViewportChanged();
    }

    private void ReprojectTrailOverlayIfViewportChanged()
    {
        if (!_mapControl.IsVisible || _mapControl.Map is null ||
            !HasUsableNativeViewport() || _latestTrails.Count == 0)
        {
            return;
        }

        var viewport = _mapControl.Map.Navigator.Viewport;
        var signature = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{viewport.CenterX:R}|{viewport.CenterY:R}|{viewport.Resolution:R}|{viewport.Rotation:R}");
        if (string.Equals(signature, _lastTrailViewportSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastTrailViewportSignature = signature;
        _trailOverlay.Reproject(ProjectWorldToScreen);
    }

    private void EnsureUnitOverlayMarker(string vehicleId)
    {
        if (_unitOverlayMarkers.ContainsKey(vehicleId)) return;
        var marker = new AvaloniaPolygon
        {
            Width = 26,
            Height = 34,
            Points = new AvaloniaPoints(UnitMarkerGeometry.Create()),
            Fill = AvaloniaBrushes.Transparent,
            Stroke = AvaloniaBrushes.Black,
            StrokeThickness = 1,
            IsHitTestVisible = false,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        };
        _unitOverlayMarkers[vehicleId] = marker;
        _unitMarkerOverlay.Children.Add(marker);
    }

    private void UpdateUnitOverlayMarker(VehicleAnimationState animation)
    {
        if (!_unitOverlayMarkers.TryGetValue(animation.VehicleId, out var marker) ||
            !_mapControl.IsVisible ||
            _mapControl.Map is null ||
            !HasUsableNativeViewport())
        {
            if (marker is not null)
            {
                marker.IsVisible = false;
            }
            return;
        }

        var screen = _mapControl.Map.Navigator.Viewport.WorldToScreen(
            animation.CurrentX,
            animation.CurrentY);
        if (!TryPositionOnCanvas(marker, screen.X - (marker.Width / 2), screen.Y - (marker.Height / 2)))
        {
            marker.IsVisible = false;
            return;
        }

        marker.IsVisible = true;
        var baseColor = UnitMarkerColor(animation.Visual);
        var alpha = animation.Visual.IsGhost
            ? animation.Visual.Selected || animation.Visual.TeamSelected ? (byte)220 : (byte)135
            : (byte)255;
        marker.Opacity = 1;
        marker.Fill = new SolidColorBrush(Avalonia.Media.Color.FromArgb(
            alpha, baseColor.R, baseColor.G, baseColor.B));
        marker.Stroke = new SolidColorBrush(animation.Visual.Selected
            ? Avalonia.Media.Color.FromRgb(246, 196, 83)
            : animation.Visual.TeamSelected
            ? Avalonia.Media.Color.FromRgb(96, 196, 232)
            : Avalonia.Media.Color.FromRgb(10, 14, 18));
        marker.StrokeThickness = animation.Visual.Selected || animation.Visual.TeamSelected ? 1.5 : 1;
        marker.RenderTransform = new RotateTransform(animation.Visual.HeadingDegrees ?? 0);
    }

    private void EnsureUnitCameraCone(string vehicleId)
    {
        if (_unitCameraConeOverlays.ContainsKey(vehicleId)) return;
        var cone = new AvaloniaPolygon
        {
            Fill = new SolidColorBrush(Avalonia.Media.Color.FromArgb(55, 96, 165, 250)),
            Stroke = new SolidColorBrush(Avalonia.Media.Color.FromArgb(190, 96, 165, 250)),
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
            IsVisible = false
        };
        _unitCameraConeOverlays[vehicleId] = cone;
        _unitCameraConeOverlay.Children.Add(cone);
    }

    private void UpdateUnitCameraCone(VehicleAnimationState animation)
    {
        if (!_unitCameraConeOverlays.TryGetValue(animation.VehicleId, out var cone) ||
            animation.Visual.CameraCone is not { } camera ||
            animation.Visual.HeadingDegrees is not { } heading ||
            !_mapControl.IsVisible ||
            _mapControl.Map is null ||
            !HasUsableNativeViewport())
        {
            if (cone is not null) cone.IsVisible = false;
            return;
        }

        var viewport = _mapControl.Map.Navigator.Viewport;
        var screenCenter = viewport.WorldToScreen(animation.CurrentX, animation.CurrentY);
        var center = new Avalonia.Point(screenCenter.X, screenCenter.Y);
        var rangePixels = Math.Clamp(camera.RangeMetres / viewport.Resolution, 18, 2400);
        var halfFov = Math.Clamp(camera.HorizontalFieldOfViewDegrees, 10, 120) * Math.PI / 360d;
        var gimbalYaw = animation.Visual.GimbalYawDegrees ?? 0;
        var cameraHeading = animation.Visual.GimbalYawInEarthFrame == true
            ? gimbalYaw
            : heading + gimbalYaw;
        var headingRadians = (cameraHeading - viewport.Rotation) * Math.PI / 180d;
        var points = new List<Avalonia.Point> { center };
        const int arcPoints = 12;
        for (var index = 0; index <= arcPoints; index++)
        {
            var angle = headingRadians - halfFov + (2 * halfFov * index / arcPoints);
            points.Add(new Avalonia.Point(
                center.X + Math.Sin(angle) * rangePixels,
                center.Y - Math.Cos(angle) * rangePixels));
        }

        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        cone.Width = Math.Max(1, maxX - minX);
        cone.Height = Math.Max(1, maxY - minY);
        cone.Points = new AvaloniaPoints(points.Select(point => new Avalonia.Point(point.X - minX, point.Y - minY)));
        cone.IsVisible = TryPositionOnCanvas(cone, minX, minY);
    }

    private sealed class VehicleAnimationState
    {
        private const double RenderDelaySeconds = 0.1;
        private const double MaxExtrapolationSeconds = 0.1;
        private const double TeleportThresholdMetres = 50;
        private const int MaxSamples = 12;
        private readonly List<RenderSample> _samples = new();
        private MapVehicleVisual _visual;

        public VehicleAnimationState(
            PointFeature feature,
            double x,
            double y,
            double targetX,
            double targetY,
            MapVehicleVisual visual)
        {
            Feature = feature;
            CurrentX = x;
            CurrentY = y;
            _visual = visual;
        }

        public PointFeature Feature { get; }
        public string VehicleId => _visual.VehicleId;
        public MapVehicleVisual Visual => _visual;
        public double CurrentX { get; private set; }
        public double CurrentY { get; private set; }

        public void SetTarget(double x, double y, MapVehicleVisual visual)
        {
            _visual = visual;
            if (_samples.Count == 0)
            {
                CurrentX = x;
                CurrentY = y;
            }
        }

        public void ResetSamples()
        {
            _samples.Clear();
        }

        public void AddSample(double x, double y, MapVehicleMotionSample sample)
        {
            var now = DateTimeOffset.UtcNow;
            var timestamp = sample.SourceTimestamp == DateTimeOffset.MinValue ? now : sample.SourceTimestamp;
            _visual = _visual with
            {
                HeadingDegrees = sample.HeadingDegrees,
                State = sample.State,
                GimbalPitchDegrees = sample.GimbalPitchDegrees,
                GimbalYawDegrees = sample.GimbalYawDegrees,
                GimbalYawInEarthFrame = sample.GimbalYawInEarthFrame
            };
            var snap = _samples.Count == 0 ||
                       sample.IsStale ||
                       IsLandedState(sample.LandedState) ||
                       (_samples.Count > 0 &&
                        (sample.AirframeMode != _samples[^1].AirframeMode ||
                         IsLandedState(sample.LandedState) != IsLandedState(_samples[^1].LandedState) ||
                         sample.PositionResetRevision != _samples[^1].PositionResetRevision ||
                         Distance(x, y, _samples[^1].X, _samples[^1].Y) > TeleportThresholdMetres));
            if (snap)
            {
                _samples.Clear();
                CurrentX = x;
                CurrentY = y;
            }

            _samples.Add(new RenderSample(
                timestamp,
                x,
                y,
                sample.HeadingDegrees,
                sample.VelocityXPerSecond,
                sample.VelocityYPerSecond,
                sample.AirframeMode,
                sample.LandedState,
                sample.PositionResetRevision));
            _samples.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
            if (_samples.Count > MaxSamples)
                _samples.RemoveRange(0, _samples.Count - MaxSamples);
        }

        private static bool IsLandedState(string state)
            => state.Contains("land", StringComparison.OrdinalIgnoreCase) ||
               state.Contains("ground", StringComparison.OrdinalIgnoreCase);

        public void UpdateRenderedVisual()
        {
            if (_samples.Count == 0)
                return;

            var renderAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(RenderDelaySeconds);
            RenderSample rendered;
            if (renderAt <= _samples[0].Timestamp)
            {
                rendered = _samples[0];
            }
            else if (renderAt >= _samples[^1].Timestamp)
            {
                var latest = _samples[^1];
                var seconds = Math.Clamp((renderAt - latest.Timestamp).TotalSeconds, 0, MaxExtrapolationSeconds);
                rendered = latest with
                {
                    X = latest.X + ((latest.VelocityX ?? 0) * seconds),
                    Y = latest.Y + ((latest.VelocityY ?? 0) * seconds)
                };
            }
            else
            {
                var upperIndex = _samples.FindIndex(item => item.Timestamp >= renderAt);
                var upper = _samples[upperIndex];
                var lower = _samples[upperIndex - 1];
                var total = (upper.Timestamp - lower.Timestamp).TotalSeconds;
                var fraction = total <= 0 ? 1 : Math.Clamp((renderAt - lower.Timestamp).TotalSeconds / total, 0, 1);
                rendered = lower with
                {
                    X = lower.X + ((upper.X - lower.X) * fraction),
                    Y = lower.Y + ((upper.Y - lower.Y) * fraction),
                    HeadingDegrees = InterpolateHeading(lower.HeadingDegrees, upper.HeadingDegrees, fraction)
                };
            }

            CurrentX = rendered.X;
            CurrentY = rendered.Y;
            _visual = _visual with { HeadingDegrees = rendered.HeadingDegrees };
        }

        private static double Distance(double x1, double y1, double x2, double y2)
            => Math.Sqrt(Math.Pow(x1 - x2, 2) + Math.Pow(y1 - y2, 2));

        private static double? InterpolateHeading(double? first, double? second, double amount)
        {
            if (first is not { } a || second is not { } b)
                return second ?? first;

            var delta = ((b - a + 540) % 360) - 180;
            return (a + (delta * amount) + 360) % 360;
        }

        private readonly record struct RenderSample(
            DateTimeOffset Timestamp,
            double X,
            double Y,
            double? HeadingDegrees,
            double? VelocityX,
            double? VelocityY,
            string AirframeMode,
            string LandedState,
            long PositionResetRevision);
    }

    private static SymbolStyle CreateVehicleHitTestStyle()
    {
        return new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse,
            SymbolScale = 1.2,
            Fill = new MapsuiBrush(new MapsuiColor(0, 0, 0, 0)),
            Outline = new MapsuiPen(new MapsuiColor(0, 0, 0, 0), 0)
        };
    }

    private static Avalonia.Media.Color UnitMarkerColor(MapVehicleVisual vehicle)
        => vehicle.Selected
            ? Avalonia.Media.Color.FromRgb(246, 196, 83)
            : vehicle.TeamSelected
            ? Avalonia.Media.Color.FromRgb(96, 196, 232)
            : vehicle.State switch
            {
                AvailabilityState.Online => Avalonia.Media.Color.FromRgb(74, 222, 128),
                AvailabilityState.Degraded => Avalonia.Media.Color.FromRgb(251, 146, 60),
                AvailabilityState.Stale => Avalonia.Media.Color.FromRgb(250, 204, 21),
                AvailabilityState.Offline => Avalonia.Media.Color.FromRgb(156, 163, 175),
                AvailabilityState.Faulted => Avalonia.Media.Color.FromRgb(248, 113, 113),
                _ => Avalonia.Media.Color.FromRgb(209, 213, 219)
            };

    private void UpdateVehicleLabelOverlay(OperationalMapScene scene)
    {
        _dynamicLabelOverlay.Children.Clear();
        if (_teamLabelControl is not null)
            _teamLabelControl.IsVisible = false;
        if (!_mapControl.IsVisible || _mapControl.Map is null || !HasUsableNativeViewport())
        {
            foreach (var label in _vehicleLabelControls.Values) label.IsVisible = false;
            return;
        }

        var viewport = _mapControl.Map.Navigator.Viewport;
        if (scene.OperatorLocation.IsAvailable &&
            scene.OperatorLocation.LongitudeDegrees is { } operatorLongitude &&
            scene.OperatorLocation.LatitudeDegrees is { } operatorLatitude &&
            MapCoordinateProjector.TryProject(operatorLongitude, operatorLatitude, out var operatorPoint))
        {
            var operatorScreen = viewport.WorldToScreen(operatorPoint.X, operatorPoint.Y);
            _operatorLabelControl ??= CreateLabel("Operator", 10, FontWeight.Normal);
            var operatorLabel = _operatorLabelControl;
            operatorLabel.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(96, 165, 250));
            if (TryPositionOnCanvas(operatorLabel, operatorScreen.X - 28, operatorScreen.Y + 10))
            {
                _dynamicLabelOverlay.Children.Add(operatorLabel);
            }
        }

        if (scene.LockedTeamPosition is { } teamPosition &&
            scene.Frame == MapFrameKind.GlobalWgs84 &&
            MapCoordinateProjector.TryProject(teamPosition.LongitudeDegrees, teamPosition.LatitudeDegrees, out var teamProjected))
        {
            var teamScreen = viewport.WorldToScreen(teamProjected.X, teamProjected.Y);
            _teamLabelControl ??= CreateLabel(string.Empty, 10, FontWeight.SemiBold);
            _teamLabelControl.IsVisible = false;
            _teamLabelControl.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(246, 196, 83));
            if (_teamLabelControl.Child is TextBlock teamText)
            {
                teamText.Text = teamPosition.TeamName;
                teamText.FontWeight = FontWeight.SemiBold;
                teamText.Foreground = AvaloniaBrushes.White;
            }

            if (TryPositionOnCanvas(_teamLabelControl, teamScreen.X - 32, teamScreen.Y + 8))
            {
                _teamLabelControl.IsVisible = true;
                _dynamicLabelOverlay.Children.Add(_teamLabelControl);
            }
        }

        if (!scene.VehicleLabelsVisible)
        {
            foreach (var label in _vehicleLabelControls.Values) label.IsVisible = false;
            return;
        }

        var liveVehicleIds = scene.Vehicles.Select(item => item.VehicleId).ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _vehicleLabelControls.Keys.Where(id => !liveVehicleIds.Contains(id)).ToArray())
        {
            _vehicleLabelOverlay.Children.Remove(_vehicleLabelControls[staleId]);
            _vehicleLabelControls.Remove(staleId);
        }

        foreach (var vehicle in scene.Vehicles)
        {
            var label = GetVehicleLabel(vehicle.VehicleId);
            label.IsVisible = false;
            if (!_vehicleAnimations.TryGetValue(vehicle.VehicleId, out var animation))
            {
                continue;
            }

            var screen = viewport.WorldToScreen(animation.CurrentX, animation.CurrentY);
            label.BorderBrush = new SolidColorBrush(vehicle.Selected
                ? Avalonia.Media.Color.FromRgb(246, 196, 83)
                : vehicle.TeamSelected
                ? Avalonia.Media.Color.FromRgb(96, 196, 232)
                : Avalonia.Media.Color.FromRgb(70, 80, 93));
            if (label.Child is TextBlock text)
            {
                text.Text = vehicle.Name;
                text.FontWeight = vehicle.Selected || vehicle.TeamSelected ? FontWeight.Bold : FontWeight.Normal;
            }

            if (TryPositionOnCanvas(label, screen.X - 32, screen.Y + 22))
            {
                label.IsVisible = true;
            }
        }
    }

    private void RebuildStaticLabelOverlay(OperationalMapScene scene)
    {
        var renderKey = CreateGeometryRenderKey(scene.Geometries) + ":" + scene.GeometryVisible;
        if (!string.Equals(_staticGeometryRenderKey, renderKey, StringComparison.Ordinal))
        {
            _staticLabelOverlay.Children.Clear();
            _geometryLabelControls.Clear();
            _geometryArrowControls.Clear();
            _staticGeometryRenderKey = renderKey;
            if (scene.GeometryVisible || scene.Geometries.Any(item => !item.IsPolicy && IsFlightMissionPreview(item)))
            {
                BuildStaticGeometryControls(scene);
            }
        }

        if (_mapControl.IsVisible && _mapControl.Map is not null && HasUsableNativeViewport())
        {
            UpdateStaticGeometryControlPositions(scene, _mapControl.Map.Navigator.Viewport);
        }
    }

    private Border GetVehicleLabel(string vehicleId)
    {
        if (_vehicleLabelControls.TryGetValue(vehicleId, out var existing)) return existing;
        var label = CreateLabel(string.Empty, 11, FontWeight.Normal);
        _vehicleLabelControls[vehicleId] = label;
        _vehicleLabelOverlay.Children.Add(label);
        return label;
    }

    private static Border CreateLabel(string text, double fontSize, FontWeight weight)
        => new()
        {
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(210, 20, 24, 29)),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(70, 80, 93)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2),
            Child = new TextBlock { Text = text, FontSize = fontSize, FontWeight = weight, Foreground = AvaloniaBrushes.White }
        };

    private void BuildStaticGeometryControls(OperationalMapScene scene)
    {
        if (!scene.GeometryVisible && !scene.Geometries.Any(item => !item.IsPolicy && IsFlightMissionPreview(item)))
        {
            return;
        }

        foreach (var geometry in scene.Geometries.Where(item => !item.IsPolicy && (scene.GeometryVisible || IsFlightMissionPreview(item))))
        {
            if (IsWaypointSequence(geometry))
            {
                var arrows = new List<AvaloniaPolyline>();
                // Keep a bounded reusable pool. Viewport changes only toggle
                // and reposition these controls; they never recreate the map
                // overlay on every pan, zoom, or telemetry tick.
                for (var index = 0; index < 256; index++)
                {
                    var chevron = new AvaloniaPolyline
                    {
                        Width = MapDrawingPrimitives.DirectionArrowLength + 2,
                        Height = (MapDrawingPrimitives.DirectionArrowHalfWidth * 2) + 2,
                        Points = new AvaloniaPoints([
                            new Avalonia.Point(1, 1),
                            new Avalonia.Point(MapDrawingPrimitives.DirectionArrowLength + 1, MapDrawingPrimitives.DirectionArrowHalfWidth + 1),
                            new Avalonia.Point(1, (MapDrawingPrimitives.DirectionArrowHalfWidth * 2) + 1)
                        ]),
                        StrokeThickness = MapDrawingPrimitives.DirectionArrowThickness,
                        IsHitTestVisible = false,
                        IsVisible = false,
                        RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
                    };
                    arrows.Add(chevron);
                    _staticLabelOverlay.Children.Add(chevron);
                }

                _geometryArrowControls[geometry.GeometryId] = arrows;
            }

            if (!string.IsNullOrWhiteSpace(geometry.Name))
            {
                var label = new Border
                {
                    Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(218, 16, 22, 29)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 2),
                    IsVisible = false,
                    Child = new TextBlock
                    {
                        Text = geometry.Name,
                        FontSize = 10,
                        Foreground = AvaloniaBrushes.White
                    }
                };
                _geometryLabelControls[geometry.GeometryId] = label;
                _staticLabelOverlay.Children.Add(label);
            }
        }
    }

    private void UpdateStaticGeometryControlPositions(OperationalMapScene scene, Viewport viewport)
    {
        var missionArrows = new List<MissionPreviewArrow>();
        foreach (var label in _geometryLabelControls.Values)
        {
            label.IsVisible = false;
        }

        foreach (var arrows in _geometryArrowControls.Values)
        {
            foreach (var arrow in arrows)
            {
                arrow.IsVisible = false;
            }
        }

        if (!scene.GeometryVisible && !scene.Geometries.Any(item => !item.IsPolicy && IsFlightMissionPreview(item)))
        {
            _missionPreviewArrowOverlay.Arrows = [];
            return;
        }

        foreach (var geometry in scene.Geometries.Where(item => !item.IsPolicy && (scene.GeometryVisible || IsFlightMissionPreview(item))))
        {
            if (_geometryLabelControls.TryGetValue(geometry.GeometryId, out var label) &&
                !string.IsNullOrWhiteSpace(geometry.Name) &&
                TryGetGeometryLabelAnchor(geometry, out var anchor) &&
                TryProject(anchor, out var projected))
            {
                var screen = viewport.WorldToScreen(projected.X, projected.Y);
                var accent = IsFlightMissionPreview(geometry)
                    ? Avalonia.Media.Color.Parse(MapDrawingPrimitives.MissionPreviewAccentHex)
                    : geometry.Highlighted
                        ? Avalonia.Media.Color.FromRgb(245, 196, 81)
                        : Avalonia.Media.Color.FromRgb(111, 175, 201);
                if (IsPx4Fence(geometry))
                {
                    accent = IsPx4ExclusionFence(geometry)
                        ? Avalonia.Media.Color.FromRgb(249, 115, 22)
                        : Avalonia.Media.Color.FromRgb(34, 197, 94);
                }

                label.BorderBrush = new SolidColorBrush(accent);
                if (label.Child is TextBlock text)
                {
                    text.Text = geometry.Name;
                    text.FontWeight = geometry.Highlighted ? FontWeight.SemiBold : FontWeight.Normal;
                }

                label.IsVisible = TryPositionOnCanvas(label, screen.X - 36, screen.Y + 12);
            }

            if (IsFlightMissionPreview(geometry))
            {
                missionArrows.AddRange(BuildMissionPreviewArrows(geometry, viewport));
            }
            else if (_geometryArrowControls.TryGetValue(geometry.GeometryId, out var arrows))
            {
                UpdateWaypointDirectionArrows(geometry, viewport, arrows);
            }
        }

        _missionPreviewArrowOverlay.Arrows = missionArrows;
    }

    private static IEnumerable<MissionPreviewArrow> BuildMissionPreviewArrows(
        MapGeometryVisual geometry,
        Viewport viewport)
    {
        const double arrowSpacing = MapDrawingPrimitives.DirectionArrowSpacing;
        for (var index = 0; index < geometry.Points.Count - 1; index++)
        {
            if (!TryProject(geometry.Points[index], out var startProjected) ||
                !TryProject(geometry.Points[index + 1], out var endProjected))
            {
                continue;
            }

            var start = viewport.WorldToScreen(startProjected.X, startProjected.Y);
            var end = viewport.WorldToScreen(endProjected.X, endProjected.Y);
            if (!IsFinite(start.X) || !IsFinite(start.Y) || !IsFinite(end.X) || !IsFinite(end.Y))
            {
                continue;
            }

            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            if (length < 42)
            {
                continue;
            }

            var arrowCount = Math.Max(1, (int)(length / arrowSpacing));
            var ux = deltaX / length;
            var uy = deltaY / length;
            var px = -uy;
            var py = ux;
            for (var arrowIndex = 1; arrowIndex <= arrowCount; arrowIndex++)
            {
                var distance = length * arrowIndex / (arrowCount + 1d);
                var tip = new Avalonia.Point(start.X + ux * distance, start.Y + uy * distance);
                var back = new Avalonia.Point(
                    tip.X - ux * MapDrawingPrimitives.DirectionArrowLength,
                    tip.Y - uy * MapDrawingPrimitives.DirectionArrowLength);
                yield return new MissionPreviewArrow(
                    tip,
                    new Avalonia.Point(
                        back.X + px * MapDrawingPrimitives.DirectionArrowHalfWidth,
                        back.Y + py * MapDrawingPrimitives.DirectionArrowHalfWidth),
                    new Avalonia.Point(
                        back.X - px * MapDrawingPrimitives.DirectionArrowHalfWidth,
                        back.Y - py * MapDrawingPrimitives.DirectionArrowHalfWidth));
            }
        }
    }

    private static void UpdateWaypointDirectionArrows(
        MapGeometryVisual geometry,
        Viewport viewport,
        List<AvaloniaPolyline> arrows)
    {
        foreach (var arrow in arrows)
        {
            arrow.IsVisible = false;
        }

        var arrowOffset = 0;
        for (var index = 0; index < geometry.Points.Count - 1; index++)
        {
            if (!TryProject(geometry.Points[index], out var startProjected) ||
                !TryProject(geometry.Points[index + 1], out var endProjected))
            {
                continue;
            }

            var start = viewport.WorldToScreen(startProjected.X, startProjected.Y);
            var end = viewport.WorldToScreen(endProjected.X, endProjected.Y);
            if (!IsFinite(start.X) || !IsFinite(start.Y) || !IsFinite(end.X) || !IsFinite(end.Y))
            {
                continue;
            }

            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            if (length < 42)
            {
                continue;
            }

            // Route direction is shown as small, open chevrons along the dashed
            // stroke. They deliberately avoid the solid triangular silhouette
            // used by vehicle markers.
            var arrowSpacing = MapDrawingPrimitives.DirectionArrowSpacing;
            if (arrowOffset >= arrows.Count)
            {
                break;
            }

            var arrowCount = Math.Min(arrows.Count - arrowOffset, Math.Max(1, (int)(length / arrowSpacing)));
            var rotation = Math.Atan2(deltaY, deltaX) * (180 / Math.PI);
            for (var arrowIndex = 1; arrowIndex <= arrowCount; arrowIndex++)
            {
                var progress = arrowIndex / (double)(arrowCount + 1);
                var chevron = arrows[arrowOffset + arrowIndex - 1];
                chevron.Stroke = new SolidColorBrush(IsFlightMissionPreview(geometry)
                    ? Avalonia.Media.Color.Parse(MapDrawingPrimitives.MissionPreviewAccentHex)
                    : geometry.Highlighted
                        ? Avalonia.Media.Color.FromRgb(245, 196, 81)
                        : Avalonia.Media.Color.FromRgb(111, 175, 201));
                chevron.RenderTransform = new RotateTransform(rotation);

                var centerX = start.X + (deltaX * progress);
                var centerY = start.Y + (deltaY * progress);
                chevron.IsVisible = TryPositionOnCanvas(
                    chevron,
                    centerX - (chevron.Width / 2),
                    centerY - (chevron.Height / 2));
            }

            arrowOffset += arrowCount;
        }
    }

    // Kept as the named geometry-direction hook for navigation tooling and
    // source-level regression checks; the live path uses the pooled controls
    // above so viewport changes only reposition existing arrows.
    private void AddWaypointDirectionArrows(MapGeometryVisual geometry, Viewport viewport)
    {
        if (_geometryArrowControls.TryGetValue(geometry.GeometryId, out var arrows))
        {
            UpdateWaypointDirectionArrows(geometry, viewport, arrows);
        }
    }

    private static bool TryGetGeometryLabelAnchor(MapGeometryVisual geometry, out OperationalPoint anchor)
    {
        var points = geometry.Rings.Count > 0
            ? geometry.Rings.SelectMany(ring => ring).ToArray()
            : geometry.Points.ToArray();
        if (points.Length == 0)
        {
            anchor = default;
            return false;
        }

        if (points.Length == 1 || IsWaypointSequence(geometry))
        {
            anchor = points[0];
            return true;
        }

        anchor = new OperationalPoint(
            points.Average(point => point.X),
            points.Average(point => point.Y),
            points.Average(point => point.Z));
        return true;
    }

    // Mapsui can publish an intermediate viewport while its native control is
    // being detached or attached.  WorldToScreen is allowed to return an
    // infinite coordinate in that short interval.  Avalonia's Canvas rejects
    // such a value during Arrange and terminates the desktop process, so map
    // overlays must wait for a real, laid-out viewport.
    private bool HasUsableNativeViewport()
    {
        if (_mapControl.Map is null ||
            _mapControl.Bounds.Width <= 0 ||
            _mapControl.Bounds.Height <= 0)
        {
            return false;
        }

        var viewport = _mapControl.Map.Navigator.Viewport;
        return IsFinite(viewport.CenterX) &&
               IsFinite(viewport.CenterY) &&
               IsFinite(viewport.Resolution) &&
               viewport.Resolution > 0;
    }

    private static bool TryPositionOnCanvas(Control control, double left, double top)
    {
        if (!IsFinite(left) || !IsFinite(top))
        {
            return false;
        }

        Canvas.SetLeft(control, left);
        Canvas.SetTop(control, top);
        return true;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static MapsuiColor StateColor(AvailabilityState state)
        => state switch
        {
            AvailabilityState.Online => MapsuiColor.FromString("#4ADE80"),
            AvailabilityState.Degraded => MapsuiColor.FromString("#FB923C"),
            AvailabilityState.Stale => MapsuiColor.FromString("#FACC15"),
            AvailabilityState.Offline => MapsuiColor.FromString("#9CA3AF"),
            AvailabilityState.Faulted => MapsuiColor.FromString("#F87171"),
            _ => MapsuiColor.FromString("#D1D5DB")
        };

    private static LineString? CreateLineString(IReadOnlyList<GeometryDocumentPoint> points)
    {
        var coordinates = ProjectCoordinates(points);
        return coordinates.Length >= 2 ? new LineString(coordinates) : null;
    }

    private static Polygon? CreatePolygon(IReadOnlyList<GeometryDocumentPoint> points)
    {
        var coordinates = ProjectCoordinates(points);
        if (coordinates.Length < 3)
        {
            return null;
        }

        if (!coordinates[0].Equals2D(coordinates[^1]))
        {
            coordinates = [.. coordinates, new Coordinate(coordinates[0].X, coordinates[0].Y)];
        }

        return coordinates.Length >= 4
            ? new Polygon(new LinearRing(coordinates))
            : null;
    }

    private static LineString? CreateLineString(IReadOnlyList<OperationalPoint> points)
    {
        var coordinates = ProjectCoordinates(points);
        return coordinates.Length >= 2 ? new LineString(coordinates) : null;
    }

    private static Polygon? CreatePolygon(IReadOnlyList<OperationalPoint> points)
    {
        var coordinates = ProjectCoordinates(points);
        if (coordinates.Length < 3)
        {
            return null;
        }

        if (!coordinates[0].Equals2D(coordinates[^1]))
        {
            coordinates = [.. coordinates, new Coordinate(coordinates[0].X, coordinates[0].Y)];
        }

        return coordinates.Length >= 4
            ? new Polygon(new LinearRing(coordinates))
            : null;
    }

    private static Coordinate[] ProjectCoordinates(IEnumerable<GeometryDocumentPoint> points)
        => points
            .Select(point => TryProject(point, out var projected)
                ? new Coordinate(projected.X, projected.Y)
                : null)
            .Where(point => point is not null)
            .Cast<Coordinate>()
            .ToArray();

    private static Coordinate[] ProjectCoordinates(IEnumerable<OperationalPoint> points)
        => points
            .Select(point => TryProject(point, out var projected)
                ? new Coordinate(projected.X, projected.Y)
                : null)
            .Where(point => point is not null)
            .Cast<Coordinate>()
            .ToArray();

    private static bool TryProject(GeometryDocumentPoint point, out ProjectedMapPoint projected)
        => MapCoordinateProjector.TryProject(point.X, point.Y, out projected);

    private static bool TryProject(OperationalPoint point, out ProjectedMapPoint projected)
        => MapCoordinateProjector.TryProject(point.X, point.Y, out projected);

    private void ApplyOrientation(OperationalMapScene scene)
    {
        var rotation = scene.RequestedViewport?.RotationDegrees
                       ?? MapOrientationResolver.ResolveRotation(scene);
        _mapControl.Map.Navigator.RotateTo(rotation, duration: 0);
    }

    private void TryApplyInitialNavigation()
    {
        if (_hasAppliedInitialNavigation)
        {
            _initialNavigationTimer.Stop();
            return;
        }

        if (Presentation is not { } presentation)
        {
            return;
        }

        // Mapsui publishes its world placeholder as soon as a new map is
        // assigned. That snapshot can already be bound to CurrentViewport by
        // the time the native control attaches, so the startup request must
        // take precedence over it.
        var requestedInitialViewport = presentation.Scene.RequestedViewport
                                       ?? _pendingInitialViewport;
        if (requestedInitialViewport is { } initialViewport)
        {
            Navigate(presentation.Scene with
            {
                RequestedViewport = initialViewport
            });
            _lastNavigationRevision = presentation.Scene.NavigationRevision;
            if (!_hasAppliedInitialNavigation)
            {
                _initialNavigationTimer.Start();
            }
            return;
        }

        if (CurrentViewport is { } currentViewport &&
            _mapControlAttached &&
            _mapControl.Bounds.Width > 0 &&
            _mapControl.Bounds.Height > 0)
        {
            RestoreViewport(currentViewport);
        }
    }

    private void ScheduleInitialNavigationRetry()
    {
        if (!_hasAppliedInitialNavigation)
        {
            _initialNavigationTimer.Start();
        }
    }

    private void Navigate(OperationalMapScene scene)
    {
        // Explicit operator requests must win over a startup or saved-view
        // viewport that may still be present while the native map is settling.
        if (scene.NavigationRequest is { } navigationRequest &&
            ApplyNavigationRequest(scene, navigationRequest))
        {
            return;
        }

        if (scene.Follow is { } follow &&
            ApplyFollowStart(scene, follow))
        {
            return;
        }

        if (scene.RequestedViewport is { } requested &&
            MapCoordinateProjector.TryProject(
                requested.LongitudeDegrees,
                requested.LatitudeDegrees,
                out var requestedCenter))
        {
            // A presentation may arrive before the native map has a usable
            // viewport.  Do not mark the startup request as applied in that
            // state; the map's initial world extent would otherwise win after
            // layout and the Toronto request would be lost.
            if (!_mapControlAttached ||
                _mapControl.Map is null ||
                _mapControl.Bounds.Width <= 0 ||
                _mapControl.Bounds.Height <= 0)
            {
                _pendingInitialViewport = requested;
                ScheduleInitialNavigationRetry();
                return;
            }

            _mapControl.Map.Navigator.CenterOnAndZoomTo(
                new MPoint(requestedCenter.X, requestedCenter.Y),
                requested.Resolution,
                duration: 0);
            _mapControl.Map.Navigator.RotateTo(requested.RotationDegrees, duration: 0);
            _pendingInitialViewport = null;
            _hasAppliedInitialNavigation = true;
            _suppressInitialViewportPublication = false;
            PublishViewportSnapshot();
            return;
        }

        var projected = scene.Vehicles
            .Select(vehicle => MapCoordinateProjector.TryProject(vehicle.X, vehicle.Y, out var point)
                ? (Vehicle: vehicle, Point: (ProjectedMapPoint?)point)
                : (Vehicle: vehicle, Point: null))
            .Where(item => item.Point is not null)
            .Select(item => (item.Vehicle, Point: item.Point!.Value))
            .ToArray();

        if (projected.Length == 0)
        {
            return;
        }

        if (MapCoordinateProjector.TryBuildExtent(projected.Select(item => item.Point), out var extent))
        {
            _mapControl.Map.Navigator.ZoomToBox(
                new MRect(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY),
                duration: 0);
            _hasAppliedInitialNavigation = true;
            PublishViewportSnapshot();
        }
    }

    private void OnMapTapped(object? sender, MapEventArgs e)
    {
        if (_suppressMapTap)
        {
            e.Handled = true;
            return;
        }

        if ((_mapAssemblyCapturingStart || _mapAssemblyCapturingEnd) &&
            TryGetMapCommandTarget(e.WorldPosition, out var assemblyPoint))
        {
            var screen = _mapControl.Map.Navigator.Viewport.WorldToScreen(
                e.WorldPosition.X,
                e.WorldPosition.Y);
            if (_mapAssemblyCapturingStart)
            {
                _mapAssemblyStart = assemblyPoint;
                _mapAssemblyCapturingStart = false;
                _mapAssemblyCapturingEnd = true;
            }
            else if (_mapAssemblyStart is { } start && _mapAssemblyFormationId is not null)
            {
                _mapAssemblyEnd = assemblyPoint;
                _mapAssemblyCapturingEnd = false;
                var request = new MapAssemblyRequest(
                    _mapAssemblyFormationId,
                    start,
                    assemblyPoint);
                MapOperatorControls?.UpdateFormationPreview(request);
                _mapAwaitingConfirmation = true;
                MapAssembleCommand?.Execute(request);
                OpenMapContextMenu(
                    new Avalonia.Point(screen.X, screen.Y),
                    assemblyPoint,
                    awaitingConfirmation: true);
            }

            e.Handled = true;
            return;
        }

        var edit = Presentation?.GeometryEdit ?? GeometryEditSnapshot.Empty;
        if (edit.IsEditing)
        {
            var handle = _editHandleLayer is null
                ? null
                : e.GetMapInfo([_editHandleLayer]).Feature;
            if (TryGetVertexIndex(handle, out var vertexIndex))
            {
                ExecuteGeometryEdit(new GeometryMapEditRequest(
                    GeometryMapEditAction.SelectVertex,
                    VertexIndex: vertexIndex));
                e.Handled = true;
                return;
            }

            if (TryGetDocumentPoint(e.WorldPosition.X, e.WorldPosition.Y, out var point))
            {
                if (edit.Draft?.Kind == GeometryDocumentKind.PointOfInterest && edit.Vertices.Count == 1)
                {
                    ExecuteGeometryEdit(new GeometryMapEditRequest(
                        GeometryMapEditAction.SelectVertex,
                        VertexIndex: 0));
                    ExecuteGeometryEdit(new GeometryMapEditRequest(
                        GeometryMapEditAction.MoveVertex,
                        point,
                        0));
                }
                else
                {
                    ExecuteGeometryEdit(new GeometryMapEditRequest(
                        GeometryMapEditAction.AddVertex,
                        point));
                }
                e.Handled = true;
            }

            return;
        }

        if (GeometrySelectionEnabled)
        {
            var geometryFeature = _geometryLayer is null
                ? null
                : e.GetMapInfo([_geometryLayer]).Feature;
            var geometryId = TryGetGeometryAt(e.WorldPosition, out var selectedGeometryId)
                ? selectedGeometryId
                : geometryFeature?["geometryId"] as string;
            if (!string.IsNullOrWhiteSpace(geometryId))
            {
                var geometrySelection = new MapGeometrySelectionRequest(
                    geometryId,
                    _lastMapKeyModifiers.HasFlag(KeyModifiers.Control),
                    _lastMapKeyModifiers.HasFlag(KeyModifiers.Shift));
                if (SelectGeometryCommand?.CanExecute(geometrySelection) == true)
                {
                    SelectGeometryCommand.Execute(geometrySelection);
                }
            }

            else if (SelectGeometryCommand?.CanExecute(null) == true)
            {
                SelectGeometryCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (_vehicleLayer is null)
        {
            SelectVehicleCommand?.Execute(null);
            return;
        }

        if (TryGetVehicleAt(e.WorldPosition, out var vehicleAtPointId))
        {
            var vehicleSelection = new MapVehicleSelectionRequest(
                vehicleAtPointId,
                _lastMapKeyModifiers.HasFlag(KeyModifiers.Control),
                _lastMapKeyModifiers.HasFlag(KeyModifiers.Shift));
            if (SelectVehicleCommand?.CanExecute(vehicleSelection) == true)
            {
                SelectVehicleCommand.Execute(vehicleSelection);
                e.Handled = true;
                return;
            }
        }

        var feature = e.GetMapInfo([_vehicleLayer]).Feature;
        if (feature?["vehicleId"] is not string vehicleId)
        {
            // A normal tap on empty map space clears unit selection. Box
            // selection suppresses this callback, and unit taps return above.
            SelectVehicleCommand?.Execute(null);
            return;
        }

        var command = SelectVehicleCommand;
        var selection = new MapVehicleSelectionRequest(
            vehicleId,
            _lastMapKeyModifiers.HasFlag(KeyModifiers.Control),
            _lastMapKeyModifiers.HasFlag(KeyModifiers.Shift));
        if (command?.CanExecute(selection) == true)
        {
            command.Execute(selection);
            e.Handled = true;
        }
    }

    private bool ApplyNavigationRequest(OperationalMapScene scene, MapNavigationRequest request)
    {
        if (request.Kind == MapNavigationRequestKind.CenterOn &&
            request.Viewport is { } viewport &&
            MapCoordinateProjector.TryProject(
                viewport.LongitudeDegrees,
                viewport.LatitudeDegrees,
                out var center))
        {
            _mapControl.Map.Navigator.CenterOnAndZoomTo(
                new MPoint(center.X, center.Y),
                viewport.Resolution,
                duration: 0);
            _mapControl.Map.Navigator.RotateTo(viewport.RotationDegrees, duration: 0);
            _hasAppliedInitialNavigation = true;
            PublishViewportSnapshot();
            return true;
        }

        if (request.Kind != MapNavigationRequestKind.FitVehicles ||
            request.VehicleIds is not { Count: > 0 })
        {
            return false;
        }

        var ids = request.VehicleIds.ToHashSet(StringComparer.Ordinal);
        var projected = scene.Vehicles
            .Where(vehicle => ids.Contains(vehicle.VehicleId))
            .Select(vehicle => MapCoordinateProjector.TryProject(vehicle.X, vehicle.Y, out var point)
                ? (Vehicle: vehicle, Point: (ProjectedMapPoint?)point)
                : (Vehicle: vehicle, Point: null))
            .Where(item => item.Point is not null)
            .Select(item => item.Point!.Value)
            .ToArray();
        if (!MapCoordinateProjector.TryBuildExtent(projected, out var extent))
        {
            return false;
        }

        _mapControl.Map.Navigator.ZoomToBox(
            PadExtent(extent, 1.2),
            duration: 0);
        CenterNavigationRequest(scene, request);
        _hasAppliedInitialNavigation = true;
        PublishViewportSnapshot();
        return true;
    }

    private bool ApplyFollowStart(OperationalMapScene scene, MapFollowState follow)
    {
        var ids = follow.VehicleIds.ToHashSet(StringComparer.Ordinal);
        var projected = scene.Vehicles
            .Where(vehicle => ids.Contains(vehicle.VehicleId))
            .Select(vehicle => MapCoordinateProjector.TryProject(vehicle.X, vehicle.Y, out var point)
                ? (Vehicle: vehicle, Point: (ProjectedMapPoint?)point)
                : (Vehicle: vehicle, Point: null))
            .Where(item => item.Point is not null)
            .Select(item => item.Point!.Value)
            .ToArray();
        if (projected.Length == 0)
        {
            return false;
        }

        if (projected.Length == 1)
        {
            _mapControl.Map.Navigator.CenterOnAndZoomTo(
                new MPoint(projected[0].X, projected[0].Y),
                MapNavigationMath.SingleTargetResolution,
                duration: 0);
        }
        else if (MapCoordinateProjector.TryBuildExtent(projected, out var extent))
        {
            _mapControl.Map.Navigator.ZoomToBox(PadExtent(extent, 1.2), duration: 0);
            var meanX = projected.Average(point => point.X);
            var meanY = projected.Average(point => point.Y);
            _mapControl.Map.Navigator.CenterOn(meanX, meanY, duration: 0);
        }

        _hasAppliedInitialNavigation = true;
        PublishViewportSnapshot();
        return true;
    }

    private void CenterNavigationRequest(OperationalMapScene scene, MapNavigationRequest request)
    {
        if (request.Viewport is not { } viewport ||
            !MapCoordinateProjector.TryProject(
                viewport.LongitudeDegrees,
                viewport.LatitudeDegrees,
                out var center))
        {
            return;
        }

        _mapControl.Map.Navigator.CenterOn(center.X, center.Y, duration: 0);
    }

    private static MRect PadExtent(ProjectedMapExtent extent, double factor)
    {
        var centerX = (extent.MinX + extent.MaxX) / 2;
        var centerY = (extent.MinY + extent.MaxY) / 2;
        var halfWidth = Math.Max((extent.MaxX - extent.MinX) * factor / 2, 10);
        var halfHeight = Math.Max((extent.MaxY - extent.MinY) * factor / 2, 10);
        return new MRect(centerX - halfWidth, centerY - halfHeight, centerX + halfWidth, centerY + halfHeight);
    }

    private void UpdateFollowCamera()
    {
        if (Presentation?.Scene.Follow is not { } follow ||
            _mapControl.Map is null)
        {
            _followCameraFilter.Reset();
            return;
        }

        var ids = follow.VehicleIds.ToHashSet(StringComparer.Ordinal);
        var targets = _vehicleAnimations.Values
            .Where(animation => ids.Contains(animation.VehicleId))
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        var targetX = targets.Average(animation => animation.CurrentX);
        var targetY = targets.Average(animation => animation.CurrentY);
        var resolution = Math.Max(1, Math.Abs(_mapControl.Map.Navigator.Viewport.Resolution));
        if (_followCameraFilter.TryUpdate(
                follow.Revision,
                targetX,
                targetY,
                resolution,
                DateTimeOffset.UtcNow,
                out var update))
        {
            _mapControl.Map.Navigator.CenterOn(update.CenterX, update.CenterY, duration: 0);
        }
    }

    private void RestoreViewport(MapViewportSnapshot snapshot)
    {
        if (!MapCoordinateProjector.TryProject(
                snapshot.LongitudeDegrees,
                snapshot.LatitudeDegrees,
                out var center))
        {
            return;
        }

        _mapControl.Map.Navigator.CenterOnAndZoomTo(
            new MPoint(center.X, center.Y),
            snapshot.Resolution,
            duration: 0);
        _mapControl.Map.Navigator.RotateTo(snapshot.RotationDegrees, duration: 0);
        _pendingInitialViewport = null;
        _hasAppliedInitialNavigation = true;
        _suppressInitialViewportPublication = false;
        PublishViewportSnapshot();
    }

    private bool TryGetVehicleAt(MPoint worldPosition, out string vehicleId)
    {
        vehicleId = string.Empty;
        if (_mapControl.Map is null || _vehicleAnimations.Count == 0)
        {
            return false;
        }

        var threshold = Math.Max(12d, _mapControl.Map.Navigator.Viewport.Resolution * 18d);
        var thresholdSquared = threshold * threshold;
        var closestDistance = double.MaxValue;
        foreach (var animation in _vehicleAnimations.Values)
        {
            var dx = animation.CurrentX - worldPosition.X;
            var dy = animation.CurrentY - worldPosition.Y;
            var distanceSquared = (dx * dx) + (dy * dy);
            if (distanceSquared <= thresholdSquared && distanceSquared < closestDistance)
            {
                closestDistance = distanceSquared;
                vehicleId = animation.VehicleId;
            }
        }

        return vehicleId.Length > 0;
    }

    private bool TryGetGeometryAt(MPoint worldPosition, out string geometryId)
    {
        geometryId = string.Empty;
        var scene = Presentation?.Scene;
        if (scene is null || _mapControl.Map is null)
        {
            return false;
        }

        var tolerance = Math.Max(10d, _mapControl.Map.Navigator.Viewport.Resolution * 14d);
        var toleranceSquared = tolerance * tolerance;
        var closestDistance = double.MaxValue;
        foreach (var geometry in scene.Geometries.Where(item => !item.IsPolicy))
        {
            var projected = geometry.Rings.Count > 0
                ? geometry.Rings.SelectMany(item => item).Select(ProjectPoint).ToArray()
                : geometry.Points.Select(ProjectPoint).ToArray();
            if (projected.Length == 0)
            {
                continue;
            }

            var distance = DistanceSquaredToGeometry(worldPosition, projected);
            var isHit = (geometry.Closed && projected.Length >= 3 &&
                         IsPointInPolygon(worldPosition, projected)) ||
                        distance <= toleranceSquared;
            if (!isHit)
            {
                continue;
            }

            if (distance < closestDistance)
            {
                closestDistance = distance;
                geometryId = geometry.GeometryId;
            }
        }

        return geometryId.Length > 0;
    }

    private static MPoint? ProjectPoint(OperationalPoint point)
        => MapCoordinateProjector.TryProject(point.X, point.Y, out var projected)
            ? new MPoint(projected.X, projected.Y)
            : null;

    private static double DistanceSquaredToGeometry(MPoint point, IEnumerable<MPoint?> points)
    {
        var materialized = points.Where(item => item is not null).Cast<MPoint>().ToArray();
        if (materialized.Length == 0)
        {
            return double.MaxValue;
        }

        if (materialized.Length == 1)
        {
            var dx = point.X - materialized[0].X;
            var dy = point.Y - materialized[0].Y;
            return (dx * dx) + (dy * dy);
        }

        var closest = double.MaxValue;
        for (var index = 1; index < materialized.Length; index++)
        {
            var start = materialized[index - 1];
            var end = materialized[index];
            var segmentX = end.X - start.X;
            var segmentY = end.Y - start.Y;
            var lengthSquared = (segmentX * segmentX) + (segmentY * segmentY);
            var progress = lengthSquared <= double.Epsilon
                ? 0
                : Math.Clamp((((point.X - start.X) * segmentX) + ((point.Y - start.Y) * segmentY)) / lengthSquared, 0, 1);
            var nearestX = start.X + (segmentX * progress);
            var nearestY = start.Y + (segmentY * progress);
            var dx = point.X - nearestX;
            var dy = point.Y - nearestY;
            closest = Math.Min(closest, (dx * dx) + (dy * dy));
        }

        return closest;
    }

    private static bool IsPointInPolygon(MPoint point, IEnumerable<MPoint?> points)
    {
        var polygon = points.Where(item => item is not null).Cast<MPoint>().ToArray();
        if (polygon.Length < 3)
        {
            return false;
        }
        var inside = false;
        for (int current = 0, previous = polygon.Length - 1; current < polygon.Length; previous = current++)
        {
            var currentPoint = polygon[current];
            var previousPoint = polygon[previous];
            if (((currentPoint.Y > point.Y) != (previousPoint.Y > point.Y)) &&
                point.X < ((previousPoint.X - currentPoint.X) * (point.Y - currentPoint.Y) /
                    ((previousPoint.Y - currentPoint.Y) == 0 ? double.Epsilon : previousPoint.Y - currentPoint.Y)) + currentPoint.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private void OnMapPointerPressed(object? sender, MapEventArgs e)
    {
        Focus();
        var edit = Presentation?.GeometryEdit ?? GeometryEditSnapshot.Empty;
        if (edit.IsEditing && _editHandleLayer is not null)
        {
            var feature = e.GetMapInfo([_editHandleLayer]).Feature;
            if (TryGetVertexIndex(feature, out var vertexIndex))
            {
                _dragVertexIndex = vertexIndex;
                ExecuteGeometryEdit(new GeometryMapEditRequest(
                    GeometryMapEditAction.SelectVertex,
                    VertexIndex: vertexIndex));
                e.Handled = true;
                return;
            }
        }

        // Mapsui keeps gesture state internally. Clearing a stale state before a
        // new gesture prevents a rapid pan/rotate sequence from leaving the map
        // captured while the overlay compass continues to update.
        _mapControl.ClearTouchState();
    }

    private void OnMapControlPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Compass buttons are siblings layered above the Mapsui control. The
        // parent tunnel handler also receives their pointer events; leave
        // those events alone so the Button can raise its Click event instead
        // of treating the press as a map gesture.
        if (IsCompassSource(e.Source))
        {
            return;
        }

        _lastMapKeyModifiers = e.KeyModifiers;
        var point = e.GetCurrentPoint(_mapControl);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            ClearConnectedClientSelectionCommand?.Execute(null);
            if (_mapAssemblyCapturingStart || _mapAssemblyCapturingEnd)
            {
                _leftPointerDown = false;
                _suppressMapTap = true;
                e.Pointer.Capture(_mapControl);
                e.Handled = true;
                return;
            }
            _leftPointerDown = true;
            _boxStart = point.Position;
            _boxModifiers = e.KeyModifiers;
            _boxSelecting = false;
            _suppressMapTap = false;
            _mapContextMenu?.Close();
            return;
        }

        if (point.Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
        {
            UserPannedCommand?.Execute(null);
            _middlePanning = true;
            _lastPanPoint = point.Position;
            e.Pointer.Capture(_mapControl);
            e.Handled = true;
            return;
        }

        if (point.Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed ||
            Presentation?.Scene.Frame != MapFrameKind.GlobalWgs84 ||
            !TryGetMapCommandTarget(point.Position, out var target))
        {
            return;
        }

        var canPrepareTeamGoTo = _mapUnitsPanel?.PrepareMapTeamGoToCommand?.CanExecute(target) == true;
        if (!canPrepareTeamGoTo &&
            (string.IsNullOrWhiteSpace(Presentation?.Scene.SelectedVehicleId) ||
             MapOperatorControls?.HasCommandSelection != true))
        {
            return;
        }

        if (_mapAssemblyCapturingStart || _mapAssemblyCapturingEnd)
        {
            e.Handled = true;
            return;
        }

        OpenMapContextMenu(point.Position, target, awaitingConfirmation: false);
        e.Handled = true;
    }

    private bool IsCompassSource(object? source)
    {
        if (source is not Avalonia.Visual visual)
        {
            return false;
        }

        for (Avalonia.Visual? current = visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, _compassPanel))
            {
                return true;
            }
        }

        return false;
    }

    private void OnMapControlPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(_mapControl);
        if (_mapAssemblyCapturingStart &&
            TryGetMapCommandTarget(point.Position, out var previewStart))
        {
            MapOperatorControls?.UpdateFormationStartPreview(previewStart);
            return;
        }

        if (_mapAssemblyCapturingStart)
        {
            MapOperatorControls?.ClearFormationPreview();
            return;
        }

        if (_mapAssemblyCapturingEnd &&
            _mapAssemblyStart is { } assemblyStart &&
            _mapAssemblyFormationId is not null &&
            TryGetMapCommandTarget(point.Position, out var previewEnd))
        {
            MapOperatorControls?.UpdateFormationPreview(new MapAssemblyRequest(
                _mapAssemblyFormationId,
                assemblyStart,
                previewEnd));
            return;
        }

        if (_mapAssemblyCapturingEnd)
        {
            MapOperatorControls?.ClearFormationPreview();
            return;
        }

        if (_middlePanning && point.Properties.IsMiddleButtonPressed && _mapControl.Map is not null)
        {
            var before = _mapControl.Map.Navigator.Viewport.ScreenToWorld(_lastPanPoint.X, _lastPanPoint.Y);
            var after = _mapControl.Map.Navigator.Viewport.ScreenToWorld(point.Position.X, point.Position.Y);
            var viewport = _mapControl.Map.Navigator.Viewport;
            _mapControl.Map.Navigator.CenterOn(
                viewport.CenterX + before.X - after.X,
                viewport.CenterY + before.Y - after.Y,
                duration: 0);
            _lastPanPoint = point.Position;
            _mapControl.ForceUpdate();
            e.Handled = true;
            return;
        }

        if (!_leftPointerDown || !point.Properties.IsLeftButtonPressed)
            return;

        var dx = point.Position.X - _boxStart.X;
        var dy = point.Position.Y - _boxStart.Y;
        if (!_boxSelecting && (Math.Abs(dx) < 4 && Math.Abs(dy) < 4))
            return;

        _boxSelecting = true;
        _suppressMapTap = true;
        e.Pointer.Capture(_mapControl);
        var left = Math.Min(_boxStart.X, point.Position.X);
        var top = Math.Min(_boxStart.Y, point.Position.Y);
        _selectionBox.Width = Math.Abs(dx);
        _selectionBox.Height = Math.Abs(dy);
        Canvas.SetLeft(_selectionBox, left);
        Canvas.SetTop(_selectionBox, top);
        _selectionBox.IsVisible = true;
        e.Handled = true;
    }

    private void OnMapControlPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_mapControl.Map is null || e.Delta.Y == 0)
        {
            return;
        }

        // Mapsui's default wheel handler zooms around the viewport center. Use
        // its anchored navigator overload instead so the world point beneath
        // the cursor stays fixed, as users expect from an interactive map.
        var point = e.GetCurrentPoint(_mapControl).Position;
        var zoomPoint = new ScreenPosition(point.X, point.Y);
        const double zoomStep = 0.85;
        var currentResolution = _mapControl.Map.Navigator.Viewport.Resolution;
        var targetResolution = e.Delta.Y > 0
            ? currentResolution * zoomStep
            : currentResolution / zoomStep;
        _mapControl.Map.Navigator.ZoomTo(targetResolution, zoomPoint, duration: 0);

        e.Handled = true;
        _mapControl.ForceUpdate();
        PublishViewportSnapshot();
    }

    private void OnMapControlPointerExited(object? sender, PointerEventArgs e)
    {
        // A pointer can leave the native map without producing another valid
        // world coordinate. Do not leave the last formation preview frozen at
        // the last point under the cursor; it is transient interaction state.
        if (_mapAssemblyCapturingStart || _mapAssemblyCapturingEnd)
        {
            MapOperatorControls?.ClearFormationPreview();
        }
    }

    private void OnMapControlPointerReleased(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(_mapControl);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased &&
            (_mapAssemblyCapturingStart || _mapAssemblyCapturingEnd) &&
            TryGetMapCommandTarget(point.Position, out var formationPoint))
        {
            e.Pointer.Capture(null);
            HandleFormationLeftClick(point.Position, formationPoint);
            e.Handled = true;
            return;
        }

        if (_middlePanning && point.Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
        {
            _middlePanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            PublishViewportSnapshot();
            return;
        }

        if (!_leftPointerDown || point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased)
            return;

        _leftPointerDown = false;
        e.Pointer.Capture(null);
        if (_boxSelecting)
        {
            SelectVehiclesInBox(point.Position);
            _selectionBox.IsVisible = false;
            _boxSelecting = false;
            e.Handled = true;
        }
    }

    private void HandleFormationLeftClick(Avalonia.Point screenPoint, MapCommandTarget point)
    {
        if (_mapAssemblyCapturingStart)
        {
            _mapAssemblyStart = point;
            _mapAssemblyCapturingStart = false;
            _mapAssemblyCapturingEnd = true;
            MapOperatorControls?.UpdateFormationStartPreview(point);
            _suppressMapTap = true;
            return;
        }

        if (_mapAssemblyStart is not { } start || _mapAssemblyFormationId is null)
            return;

        _mapAssemblyEnd = point;
        _mapAssemblyCapturingEnd = false;
        var request = new MapAssemblyRequest(
            _mapAssemblyFormationId,
            start,
            point);
        MapOperatorControls?.UpdateFormationPreview(request);
        _mapAwaitingConfirmation = true;
        MapAssembleCommand?.Execute(request);
        OpenMapContextMenu(screenPoint, point, awaitingConfirmation: true);
        _suppressMapTap = true;
    }

    private void SelectVehiclesInBox(Avalonia.Point end)
    {
        if (Presentation?.Scene is not { } scene || _mapControl.Map is null)
            return;

        var left = Math.Min(_boxStart.X, end.X);
        var right = Math.Max(_boxStart.X, end.X);
        var top = Math.Min(_boxStart.Y, end.Y);
        var bottom = Math.Max(_boxStart.Y, end.Y);
        var ids = scene.Vehicles
            .Where(vehicle => TryGetVehicleScreenPoint(vehicle.VehicleId, vehicle, out var point) &&
                              point.X >= left && point.X <= right &&
                              point.Y >= top && point.Y <= bottom)
            .Select(vehicle => vehicle.VehicleId)
            .ToArray();
        if (ids.Length == 0)
            return;

        var modifiers = _boxModifiers;
        var request = new MapVehicleBoxSelectionRequest(
            ids,
            modifiers.HasFlag(KeyModifiers.Shift) || modifiers.HasFlag(KeyModifiers.Control),
            false);
        if (SelectVehicleCommand?.CanExecute(request) == true)
            SelectVehicleCommand.Execute(request);
    }

    private bool TryGetVehicleScreenPoint(
        string vehicleId,
        MapVehicleVisual vehicle,
        out Avalonia.Point point)
    {
        point = default;
        if (_mapControl.Map is null)
            return false;

        // Box selection must match the marker the operator sees. Raw scene
        // positions can lag behind the interpolated marker during telemetry
        // updates, especially for PX4 and ArduPilot links.
        if (_vehicleAnimations.TryGetValue(vehicleId, out var animation))
        {
            var interpolatedScreen = _mapControl.Map.Navigator.Viewport.WorldToScreen(
                animation.CurrentX,
                animation.CurrentY);
            point = new Avalonia.Point(interpolatedScreen.X, interpolatedScreen.Y);
            return double.IsFinite(point.X) && double.IsFinite(point.Y);
        }

        MPoint world;
        if (Presentation?.Scene.Frame == MapFrameKind.GlobalWgs84)
        {
            if (!MapCoordinateProjector.TryProject(vehicle.X, vehicle.Y, out var projected))
                return false;
            world = new MPoint(projected.X, projected.Y);
        }
        else
        {
            world = new MPoint(vehicle.X, vehicle.Y);
        }

        var screen = _mapControl.Map.Navigator.Viewport.WorldToScreen(world.X, world.Y);
        point = new Avalonia.Point(screen.X, screen.Y);
        return true;
    }

    private void RefreshMapContextMenu()
    {
        if (_mapContextMenu is null || _mapContextTarget is not { } target)
        {
            return;
        }

        var pending = _mapOperatorControls?.PendingPlan;
        var teamPending = _mapUnitsPanel?.PendingFormationOperation;
        var pendingTitle = teamPending is { } teamOperation
            ? $"{teamOperation.Title} - Team"
            : pending is { } operatorOperation
                ? $"{operatorOperation.DisplayName} - {operatorOperation.TargetName}"
                : null;
        var teamGoToCommand = _mapUnitsPanel?.PrepareMapTeamGoToCommand;
        var canPrepareTeamGoTo = teamGoToCommand?.CanExecute(target) == true;
        var canPrepareGoTo = MapGoToCommand?.CanExecute(target) == true;
        var canPrepareSetHeading = MapSetHeadingCommand?.CanExecute(target) == true;
        var canPreparePointGimbal = MapPointGimbalCommand?.CanExecute(target) == true;
        var findingSignature = pending is null
            ? string.Empty
            : string.Join('|', _mapOperatorControls?.Findings.Select(item => $"{item.Severity}:{item.Message}") ?? []);
        var teamFindingSignature = teamPending is null
            ? string.Empty
            : string.Join('|', teamPending.Findings.Select(item => $"{item.Severity}:{item.Message}"));
        var signature = string.Join('|',
            _mapAwaitingConfirmation,
            target.LatitudeDegrees.ToString("R", CultureInfo.InvariantCulture),
            target.LongitudeDegrees.ToString("R", CultureInfo.InvariantCulture),
            pendingTitle,
            findingSignature,
            teamFindingSignature,
            canPrepareTeamGoTo,
            canPrepareGoTo,
            canPrepareSetHeading,
            canPreparePointGimbal,
            _mapOperatorControls?.HasMultiUnitSelection == true,
            _mapAssemblyChoosingFormation,
            _mapAssemblyStart is not null,
            _mapAssemblyEnd is not null,
            _mapAssemblyFormationId);
        // Telemetry and command-history updates can arrive several times per
        // second while the menu is open. Rebuilding Items on every update
        // destroys the current MenuItem and steals the pointer target, which
        // makes the menu flicker and prevents a click from reaching the
        // command. Only rebuild when the menu's actual structure/state changes.
        if (string.Equals(signature, _mapContextMenuSignature, StringComparison.Ordinal))
        {
            return;
        }

        _mapContextMenuSignature = signature;
        _mapContextMenu.Items.Clear();
        if (!_mapAwaitingConfirmation)
        {
            if (canPrepareTeamGoTo)
            {
                _mapContextMenu.Items.Add(new MenuItem
                {
                    Header = "Queue Team Go to here",
                    Command = new RelayCommand(
                        parameter => ExecuteMapContextCommand(teamGoToCommand, parameter),
                        parameter => teamGoToCommand?.CanExecute(parameter) == true),
                    CommandParameter = target
                });
            }
            else if (_mapOperatorControls?.HasMultiUnitSelection == true)
            {
                if (_mapAssemblyChoosingFormation)
                {
                    _mapContextMenu.Items.Add(new MenuItem { Header = "Choose formation", IsEnabled = false });
                    foreach (var formation in _mapOperatorControls.FormationProviders)
                    {
                        _mapContextMenu.Items.Add(new MenuItem
                        {
                            Header = formation.DisplayName,
                            Command = new RelayCommand(_ => BeginFormationCapture(formation.Id))
                        });
                    }
                }
                else if (_mapAssemblyEnd is { } assemblyEnd &&
                         _mapAssemblyStart is { } assemblyStart &&
                         _mapAssemblyFormationId is not null)
                {
                    var request = new MapAssemblyRequest(
                        _mapAssemblyFormationId,
                        assemblyStart,
                        assemblyEnd);
                    _mapContextMenu.Items.Add(new MenuItem
                    {
                        Header = $"Queue Assemble {_mapOperatorControls.SelectedUnitCount} units",
                        Command = new RelayCommand(
                            parameter => ExecuteMapContextCommand(MapAssembleCommand, parameter),
                            parameter => MapAssembleCommand?.CanExecute(parameter) == true),
                        CommandParameter = request
                    });
                    _mapContextMenu.Items.Add(new MenuItem
                    {
                        Header = "Cancel",
                        Command = new RelayCommand(_ => CancelMapInteraction())
                    });
                }
                else
                {
                    _mapContextMenu.Items.Add(new MenuItem
                    {
                        Header = $"Queue Assemble {_mapOperatorControls.SelectedUnitCount} units",
                        Command = new RelayCommand(_ => BeginAssemblyFormationSelection())
                    });
                }
            }
            else
            {
                var goTo = new MenuItem
                {
                    Header = "Queue Go to here",
                    Command = new RelayCommand(
                        parameter => ExecuteMapContextCommand(MapGoToCommand, parameter),
                        parameter => canPrepareGoTo),
                    CommandParameter = target
                };
                _mapContextMenu.Items.Add(goTo);
            }
            var setHeading = new MenuItem
            {
                Header = "Queue Set heading here",
                Command = new RelayCommand(
                    parameter => ExecuteMapContextCommand(MapSetHeadingCommand, parameter),
                    parameter => canPrepareSetHeading),
                CommandParameter = target
            };
            if (canPrepareSetHeading)
                _mapContextMenu.Items.Add(setHeading);
            if (canPreparePointGimbal)
            {
                _mapContextMenu.Items.Add(new MenuItem
                {
                    Header = "Queue point gimbal here",
                    Command = new RelayCommand(
                        parameter => ExecuteMapContextCommand(MapPointGimbalCommand, parameter),
                        parameter => MapPointGimbalCommand?.CanExecute(parameter) == true),
                    CommandParameter = target
                });
            }
            return;
        }

        _mapContextMenu.Items.Add(new MenuItem
        {
            Header = pendingTitle ?? "Preparing operator command...",
            IsEnabled = false
        });
        _mapContextMenu.Items.Add(new Separator());
        _mapContextMenu.Items.Add(new MenuItem
        {
            Header = "Execute command",
            Command = teamPending is not null
                ? _mapUnitsPanel?.ExecuteFormationOperationCommand
                : _mapOperatorControls?.ExecutePendingCommand
        });
        _mapContextMenu.Items.Add(new MenuItem
        {
            Header = "Clear command queue",
            Command = teamPending is not null
                ? _mapUnitsPanel?.CancelFormationOperationCommand
                : _mapOperatorControls?.CancelPendingCommand
        });

        if (teamPending is { } formationOperation)
        {
            foreach (var finding in formationOperation.Findings)
            {
                _mapContextMenu.Items.Add(new MenuItem
                {
                    Header = $"{finding.Severity}: {finding.Message}",
                    IsEnabled = false
                });
            }
        }
        else if (pending is not null)
        {
            foreach (var finding in _mapOperatorControls?.Findings ?? [])
            {
                _mapContextMenu.Items.Add(new MenuItem
                {
                    Header = $"{finding.Severity}: {finding.Message}",
                    IsEnabled = false
                });
            }
        }
    }

    private void OpenMapContextMenu(
        Avalonia.Point screenPoint,
        MapCommandTarget target,
        bool awaitingConfirmation)
    {
        if (!awaitingConfirmation)
        {
            _mapConfirmationMenuPending = false;
            _mapAssemblyStart = null;
            _mapAssemblyEnd = null;
            _mapAssemblyFormationId = null;
            _mapAssemblyCapturingStart = false;
            _mapAssemblyCapturingEnd = false;
            _mapAssemblyChoosingFormation = false;
            MapOperatorControls?.ClearFormationPreview();
        }
        _mapContextTarget = target;
        _mapAwaitingConfirmation = awaitingConfirmation;
        _mapContextMenu?.Close();
        _mapContextMenuSignature = null;
        _mapContextMenu = new ContextMenu
        {
            PlacementTarget = _mapControl,
            Placement = PlacementMode.Pointer
        };
        RefreshMapContextMenu();
        _mapContextMenu.Open(_mapControl);
    }

    private void BeginAssemblyFormationSelection()
    {
        _mapAssemblyStart = null;
        _mapAssemblyEnd = null;
        _mapAssemblyFormationId = null;
        _mapAssemblyChoosingFormation = true;
        _mapAssemblyCapturingStart = false;
        _mapAssemblyCapturingEnd = false;
        RefreshMapContextMenu();
    }

    private void BeginFormationCapture(string formationId)
    {
        _mapAssemblyFormationId = formationId;
        _mapAssemblyChoosingFormation = false;
        _mapAssemblyCapturingStart = true;
        _mapAssemblyCapturingEnd = false;
        _mapAssemblyStart = null;
        _mapAssemblyEnd = null;
        MapOperatorControls?.ClearFormationPreview();
        _mapContextMenu?.Close();
    }

    private void CancelMapInteraction()
    {
        if (_mapAwaitingConfirmation)
        {
            if (_mapUnitsPanel?.PendingFormationOperation is not null)
                _mapUnitsPanel.CancelFormationOperationCommand.Execute(null);
            else if (MapOperatorControls?.PendingPlan is not null)
                MapOperatorControls.CancelPendingCommand.Execute(null);
        }
        _mapContextMenu?.Close();
        _mapContextMenu = null;
        _mapContextMenuSignature = null;
        _mapContextTarget = null;
        _mapAwaitingConfirmation = false;
        _mapConfirmationMenuPending = false;
        _mapAssemblyStart = null;
        _mapAssemblyEnd = null;
        _mapAssemblyFormationId = null;
        _mapAssemblyCapturingStart = false;
        _mapAssemblyCapturingEnd = false;
        _mapAssemblyChoosingFormation = false;
        MapOperatorControls?.ClearFormationPreview();
    }

    private void ExecuteMapContextCommand(ICommand? command, object? parameter)
    {
        if (_mapContextTarget is not { } target)
        {
            return;
        }

        _mapAwaitingConfirmation = true;
        _mapConfirmationMenuPending = true;

        // Avalonia closes a ContextMenu after a MenuItem command has handled
        // the pointer event. Wait for that close notification before opening
        // the confirmation menu; opening it from inside the original click
        // causes the new popup to be closed by the original menu's close pass.
        var sourceMenu = _mapContextMenu;
        EventHandler<RoutedEventArgs>? reopenConfirmationMenu = null;
        reopenConfirmationMenu = (_, _) =>
        {
            if (sourceMenu is not null)
                sourceMenu.Closed -= reopenConfirmationMenu;

            Dispatcher.UIThread.Post(() =>
            {
                if (!_mapConfirmationMenuPending || !_mapAwaitingConfirmation)
                {
                    return;
                }

                _mapConfirmationMenuPending = false;
                OpenMapContextMenu(default, target, awaitingConfirmation: true);
            }, DispatcherPriority.Background);
        };

        if (sourceMenu is not null)
            sourceMenu.Closed += reopenConfirmationMenu;

        command?.Execute(parameter);
        if (ReferenceEquals(command, MapAssembleCommand))
            MapOperatorControls?.ClearFormationPreview();

        // A command can be invoked programmatically without a source popup.
        // Preserve the same confirmation flow in that case.
        if (sourceMenu is null)
        {
            reopenConfirmationMenu(null, new RoutedEventArgs());
        }
    }

    private void OnMapOperatorControlsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(OperatorControlsViewModel.PendingPlan) and
            not nameof(OperatorControlsViewModel.Findings) and
            not nameof(OperatorControlsViewModel.StatusMessage) and
            not nameof(OperatorControlsViewModel.IsPreparingMapCommand) and
            not nameof(OperatorControlsViewModel.HasGimbalCameraSelection) and
            not nameof(OperatorControlsViewModel.GimbalCameraSupportedCount))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_mapConfirmationMenuPending)
            {
                return;
            }

            if (_mapAwaitingConfirmation &&
                _mapOperatorControls?.PendingPlan is null &&
                _mapOperatorControls?.IsPreparingMapCommand != true)
            {
                _ = VerifyMapContextPreparationAsync();
                return;
            }

            RefreshMapContextMenu();
        });
    }

    private async Task VerifyMapContextPreparationAsync()
    {
        await Task.Delay(75);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_mapConfirmationMenuPending)
            {
                return;
            }

            if (_mapAwaitingConfirmation &&
                _mapOperatorControls?.PendingPlan is null &&
                _mapOperatorControls?.IsPreparingMapCommand != true)
            {
                CancelMapInteraction();
                return;
            }

            RefreshMapContextMenu();
        });
    }

    private void OnMapUnitsPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(UnitsPanelViewModel.PendingFormationOperation) and
            not nameof(UnitsPanelViewModel.Formation) and
            not nameof(UnitsPanelViewModel.IsTeamFormationLocked) and
            not nameof(UnitsPanelViewModel.TeamStatus))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_mapConfirmationMenuPending)
            {
                return;
            }

            if (_mapAwaitingConfirmation &&
                _mapUnitsPanel?.PendingFormationOperation is null &&
                _mapOperatorControls?.PendingPlan is null)
            {
                CancelMapInteraction();
                return;
            }

            RefreshMapContextMenu();
        });
    }

    private void OnMapFormationPreviewChanged(object? sender, EventArgs e)
    {
        // The preview is transient state and can change many times without a
        // vehicle/store update. Refresh after the VM has rebuilt the scene so
        // Mapsui paints the changed memory-layer features immediately.
        Dispatcher.UIThread.Post(() =>
        {
            if (_mapControl.Map is null)
            {
                return;
            }

            _mapControl.Map.RefreshGraphics();
            _mapControl.ForceUpdate();
        }, DispatcherPriority.Render);
    }

    private bool TryGetMapCommandTarget(Avalonia.Point screenPoint, out MapCommandTarget target)
    {
        target = default!;
        var world = _mapControl.Map.Navigator.Viewport.ScreenToWorld(screenPoint.X, screenPoint.Y);
        return TryGetMapCommandTarget(world, out target);
    }

    private static bool TryGetMapCommandTarget(MPoint world, out MapCommandTarget target)
    {
        target = default!;
        if (!MapCoordinateProjector.TryUnproject(world.X, world.Y, out var longitude, out var latitude))
        {
            return false;
        }

        target = new MapCommandTarget(latitude, longitude);
        return true;
    }

    private void OnMapPointerReleased(object? sender, MapEventArgs e)
    {
        if (_dragVertexIndex is not null)
        {
            _dragVertexIndex = null;
            e.Handled = true;
        }

        // Release can arrive after a cancelled or very fast gesture. Resetting
        // here makes the next pan/zoom/rotation start from a clean state.
        Dispatcher.UIThread.Post(_mapControl.ClearTouchState);
    }

    private void OnMapPointerMoved(object? sender, MapEventArgs e)
    {
        if (_dragVertexIndex is { } vertexIndex &&
            TryGetDocumentPoint(e.WorldPosition.X, e.WorldPosition.Y, out var movedPoint))
        {
            ExecuteGeometryEdit(new GeometryMapEditRequest(
                GeometryMapEditAction.MoveVertex,
                movedPoint,
                vertexIndex));
            e.Handled = true;
        }

        if (!MapCoordinateProjector.TryUnproject(
                e.WorldPosition.X,
                e.WorldPosition.Y,
                out var longitude,
                out var latitude))
        {
            return;
        }

        var location = new MapCursorLocation(
            longitude,
            latitude,
            e.Map.Navigator.Viewport.Resolution);
        _cursorCoordinates.Text = location.CoordinateText;
        _cursorResolution.Text = $"{UnitFormatting.AdaptiveDistance(
            location.ResolutionMetresPerPixel,
            UnitSettingsService.Instance?.Current.HorizontalDistance ?? DistanceUnit.Meters)}/px";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            (_mapAssemblyStart is not null || _mapContextMenu is not null))
        {
            CancelMapInteraction();
            e.Handled = true;
            return;
        }

        var edit = Presentation?.GeometryEdit ?? GeometryEditSnapshot.Empty;
        if (!edit.IsEditing)
        {
            base.OnKeyDown(e);
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Z)
        {
            ExecuteCommand(UndoGeometryEditCommand);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Y)
        {
            ExecuteCommand(RedoGeometryEditCommand);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                ExecuteCommand(CancelGeometryEditCommand);
                e.Handled = true;
                break;
            case Key.Delete:
            case Key.Back:
                ExecuteGeometryEdit(new GeometryMapEditRequest(GeometryMapEditAction.RemoveVertex));
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteCommand(CompleteGeometryEditCommand);
                e.Handled = true;
                break;
            default:
                base.OnKeyDown(e);
                break;
        }
    }

    private void ExecuteGeometryEdit(GeometryMapEditRequest request)
    {
        var command = GeometryEditCommand;
        if (command?.CanExecute(request) == true)
        {
            command.Execute(request);
        }
    }

    private static void ExecuteCommand(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private static bool TryGetVertexIndex(IFeature? feature, out int vertexIndex)
    {
        switch (feature?["vertexIndex"])
        {
            case int value:
                vertexIndex = value;
                return true;
            case long value when value is >= int.MinValue and <= int.MaxValue:
                vertexIndex = (int)value;
                return true;
            default:
                vertexIndex = -1;
                return false;
        }
    }

    private static bool TryGetDocumentPoint(
        double projectedX,
        double projectedY,
        out GeometryDocumentPoint point)
    {
        if (MapCoordinateProjector.TryUnproject(
                projectedX,
                projectedY,
                out var longitude,
                out var latitude))
        {
            point = GeometryDocumentPoint.GlobalWgs84(longitude, latitude);
            return true;
        }

        point = default;
        return false;
    }

    private void OnViewportChanged(object sender, ViewportChangedEventArgs e)
    {
        if (_suppressInitialViewportPublication)
        {
            UpdateMapWidgets();
            return;
        }

        PublishViewportSnapshot();
        UpdateMapWidgets();
        ScheduleAlternateStylePrefetch();
        if (Presentation is { } presentation)
        {
            RebuildStaticLabelOverlay(presentation.Scene);
            UpdateVehicleLabelOverlay(presentation.Scene);
            UpdateTrailOverlay(
                presentation.Scene,
                _latestTrails.Count > 0 ? _latestTrails : presentation.Scene.Trails,
                trailsUpdated: true,
                forceReproject: true);
        }
    }

    private void PublishViewportSnapshot()
    {
        var viewport = _mapControl.Map.Navigator.Viewport;
        if (!MapCoordinateProjector.TryUnproject(
                viewport.CenterX,
                viewport.CenterY,
                out var longitude,
                out var latitude))
        {
            return;
        }

        var snapshot = new MapViewportSnapshot(
            longitude,
            latitude,
            viewport.Resolution,
            viewport.Rotation);
        _lastRenderedViewport = snapshot;
        var command = ViewportChangedCommand;
        if (command?.CanExecute(snapshot) == true)
        {
            command.Execute(snapshot);
        }
    }

    private void ScheduleAlternateStylePrefetch()
    {
        CancelAlternateStylePrefetch();
        if (_onlineTileLayers.Count == 0 ||
            _lastRenderedViewport is not { } viewport ||
            Bounds.Width <= 0 ||
            Bounds.Height <= 0)
        {
            return;
        }

        var activeStyleId = _loadedOnlineStyleId;
        var width = Bounds.Width;
        var height = Bounds.Height;
        var cancellation = new CancellationTokenSource();
        _alternateStylePrefetch = cancellation;
        _ = PrefetchAlternateStylesAfterForegroundAsync(
            activeStyleId,
            viewport,
            width,
            height,
            cancellation.Token);
    }

    private async Task PrefetchAlternateStylesAfterForegroundAsync(
        string? activeStyleId,
        MapViewportSnapshot viewport,
        double width,
        double height,
        CancellationToken cancellationToken)
    {
        try
        {
            // Wait until map interaction has settled and foreground requests
            // have drained. A later viewport event cancels this work.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            for (var attempt = 0; attempt < 16; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var foregroundBusy = await Dispatcher.UIThread.InvokeAsync(
                    () => activeStyleId is not null &&
                          _onlineTileLayers.TryGetValue(activeStyleId, out var layer) &&
                          layer.Busy,
                    DispatcherPriority.Background);
                if (!foregroundBusy)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }

            await WarmAlternateStyleLayersAsync(activeStyleId, cancellationToken);
            await OnlineMapTileSources.PrefetchAlternatesAsync(
                activeStyleId,
                viewport,
                width,
                height,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Viewport movement, style changes, and control teardown supersede
            // speculative tile warming.
        }
        catch (Exception)
        {
            // Prefetch is optional and must never affect foreground map use.
        }
    }

    private async Task WarmAlternateStyleLayersAsync(
        string? activeStyleId,
        CancellationToken cancellationToken)
    {
        var alternateStyleIds = await Dispatcher.UIThread.InvokeAsync(
            () => _onlineTileLayers.Keys
                .Where(styleId => !string.Equals(styleId, activeStyleId, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            DispatcherPriority.Background);

        foreach (var styleId in alternateStyleIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_onlineTileLayers.TryGetValue(styleId, out var layer))
                {
                    return;
                }

                // Keep the layer in Mapsui's fetch pipeline while making it
                // invisible. This warms its in-memory raster cache as well as
                // the shared persistent byte cache used by later map sessions.
                layer.Enabled = true;
                layer.Opacity = 0;
                _mapControl.RefreshData(ChangeType.Discrete);
                _mapControl.ForceUpdate();
            }, DispatcherPriority.Background, cancellationToken);

            for (var attempt = 0; attempt < 120; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var busy = await Dispatcher.UIThread.InvokeAsync(
                    () => _onlineTileLayers.TryGetValue(styleId, out var layer) && layer.Busy,
                    DispatcherPriority.Background);
                if (!busy)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }

    private void CancelAlternateStylePrefetch()
    {
        var previous = Interlocked.Exchange(ref _alternateStylePrefetch, null);
        if (previous is null)
        {
            return;
        }

        previous.Cancel();
        previous.Dispose();
    }

    private readonly record struct MissionPreviewArrow(Avalonia.Point Tip, Avalonia.Point Left, Avalonia.Point Right);

    private sealed class MissionPreviewArrowOverlayControl : Control
    {
        private IReadOnlyList<MissionPreviewArrow> _arrows = [];

        public IReadOnlyList<MissionPreviewArrow> Arrows
        {
            get => _arrows;
            set
            {
                _arrows = value;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_arrows.Count == 0)
            {
                return;
            }

            var pen = new Avalonia.Media.Pen(
                new SolidColorBrush(Avalonia.Media.Color.Parse(MapDrawingPrimitives.MissionPreviewAccentHex)),
                MapDrawingPrimitives.DirectionArrowThickness);
            foreach (var arrow in _arrows)
            {
                context.DrawLine(pen, arrow.Tip, arrow.Left);
                context.DrawLine(pen, arrow.Tip, arrow.Right);
            }
        }
    }

    public void Dispose()
    {
        CancelAlternateStylePrefetch();
        _vehicleAnimationTimer.Stop();
        _mapControl.Dispose();
        GC.SuppressFinalize(this);
    }

}
