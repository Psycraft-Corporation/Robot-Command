using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using RobotCommand.Localization;
using RobotCommand.Services;
using RobotCommand.ViewModels;

namespace RobotCommand.Views;

public sealed partial class MainWindow : Window
{
    private bool _resizingTerminal;
    private bool _terminalWasOpenAtResizeStart;
    private double _terminalResizeStartY;
    private double _terminalResizeStartHeight;

    public MainWindow()
        : this(null, null)
    {
    }

    public MainWindow(IApplicationSettingsService? settings, ILocalizationService? localization = null)
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        DataContextChanged += (_, _) => UpdateResponsiveLayout();
        if (localization is not null)
        {
            Title = localization.Get("AppTitle");
            localization.PropertyChanged += OnLocalizationChanged;
            Closed += (_, _) => localization.PropertyChanged -= OnLocalizationChanged;
        }
        if (settings is not null)
        {
            ApplyBrightMode(settings.Current.BrightModeEnabled);
            settings.Changed += OnSettingsChanged;
            Closed += (_, _) => settings.Changed -= OnSettingsChanged;
        }
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is ILocalizationService localization)
        {
            Title = localization.Get("AppTitle");
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (sender is IApplicationSettingsService settings)
        {
            ApplyBrightMode(settings.Current.BrightModeEnabled);
        }
    }

    private void ApplyBrightMode(bool enabled)
    {
        Classes.Set("brightMode", enabled);
        // Fluent popups (combo-box dropdowns, menus, and flyouts) are hosted
        // outside the Window visual tree, so the bright-mode class alone does
        // not change their theme resources. Keep the actual window theme in
        // sync so those popup roots use the same light palette.
        RequestedThemeVariant = enabled ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private void UpdateResponsiveLayout()
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.UpdateResponsiveLayout(Bounds.Width);
        }
    }

    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel shell ||
            !e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) ||
            e.Key is not Avalonia.Input.Key.Oem3)
        {
            return;
        }

        shell.ToggleTerminal();
        if (shell.TerminalOpen)
        {
            Dispatcher.UIThread.Post(EmbeddedTerminal.FocusInput, DispatcherPriority.Input);
        }

        e.Handled = true;
    }

    private void OnTerminalHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        _resizingTerminal = true;
        _terminalWasOpenAtResizeStart = shell.TerminalOpen;
        _terminalResizeStartY = e.GetPosition(this).Y;
        _terminalResizeStartHeight = shell.TerminalResizeHeight;
        TerminalHandle.PointerCaptureLost += OnTerminalPointerCaptureLost;
        e.Pointer.Capture(TerminalHandle);
        e.Handled = true;
    }

    private void OnTerminalHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizingTerminal || DataContext is not ShellViewModel shell)
        {
            return;
        }

        var pointerY = e.GetPosition(this).Y;
        if (!_terminalWasOpenAtResizeStart)
        {
            // While closed, the handle sits at the bottom edge of the content
            // area. Calculate the opening height from the pointer so the new
            // boundary is placed directly under the cursor.
            var pointerInContent = e.GetPosition(ContentSurfaceGrid);
            var closedBottom = ContentSurfaceGrid.Bounds.Height;
            var openingHeight = closedBottom - pointerInContent.Y - (TerminalHandle.Bounds.Height / 2);
            if (openingHeight <= 40)
            {
                return;
            }

            shell.SetTerminalHeight(openingHeight, Bounds.Height);
            _terminalWasOpenAtResizeStart = true;
            _terminalResizeStartY = pointerInContent.Y;
            _terminalResizeStartHeight = shell.TerminalResizeHeight;
        }
        else
        {
            var delta = e.GetPosition(ContentSurfaceGrid).Y - _terminalResizeStartY;
            shell.SetTerminalHeight(_terminalResizeStartHeight - delta, Bounds.Height);
        }
        e.Handled = true;
    }

    private void OnTerminalHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizingTerminal)
        {
            return;
        }

        _resizingTerminal = false;
        e.Pointer.Capture(null);
        TerminalHandle.PointerCaptureLost -= OnTerminalPointerCaptureLost;
        e.Handled = true;
    }

    private void OnTerminalPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _resizingTerminal = false;
        TerminalHandle.PointerCaptureLost -= OnTerminalPointerCaptureLost;
    }

}
