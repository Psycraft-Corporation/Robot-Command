using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
using RobotCommand.Services;

namespace RobotCommand.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private WorkspaceNavigationItem _selectedWorkspace;
    private bool _leftRailOpen;
    private bool _rightRailOpen = true;
    private bool _leftRailManuallyCollapsed = true;
    private bool _leftRailAutoCollapsed;
    private bool _leftRailAutoOverrideOpen;
    private bool _terminalOpen;
    private GridLength _terminalHeight = new(260);
    private readonly ManualControlViewModel _manualControl;
    private readonly ILocalizationService _localization;

    public ShellViewModel(
        UnitsPanelViewModel units,
        ApplicationStatusViewModel status,
        OperateViewModel operate,
        ManualControlViewModel manualControl,
        EmbeddedTerminalViewModel terminal,
        MapLibraryViewModel maps,
        AutonomyWorkspaceViewModel autonomy,
        HistoryViewModel history,
        ConnectionsViewModel connections,
        SettingsViewModel settings,
        UnitsWorkspaceViewModel unitsWorkspace,
        ThreeDWorkspaceViewModel threeD,
        ILocalizationService localization,
        MyTeamViewModel? myTeam = null)
    {
        Units = units;
        Operate = operate;
        Status = status;
        _manualControl = manualControl;
        Terminal = terminal;
        MyTeam = myTeam;
        UnitsWorkspace = unitsWorkspace;
        _localization = localization;

        var workspaces = new List<WorkspaceNavigationItem>
        {
            new("operate", L("Operate"), operate),
            new("units", L("Units"), unitsWorkspace),
            new("manual-control", L("InputDevices"), manualControl),
            new("maps", L("Maps"), maps),
            new("autonomy", L("Planning"), autonomy),
            new("3d", L("ThreeD"), threeD),
        };
        workspaces.Add(new WorkspaceNavigationItem("history", L("History"), history));
        workspaces.Add(new WorkspaceNavigationItem("connections", L("Connections"), connections));
        if (myTeam is not null)
        {
            workspaces.Add(new WorkspaceNavigationItem("my-team", L("MyTeam"), myTeam));
        }
        workspaces.Add(new WorkspaceNavigationItem("settings", L("Settings"), settings));
        Workspaces = new ObservableCollection<WorkspaceNavigationItem>(workspaces);

        _selectedWorkspace = Workspaces[0];
        _localization.PropertyChanged += OnLocalizationChanged;

        ToggleLeftRailCommand = new RelayCommand(_ => ToggleLeftRail());
        ToggleRightRailCommand = new RelayCommand(_ => RightRailOpen = !RightRailOpen);
    }

    public ObservableCollection<WorkspaceNavigationItem> Workspaces { get; }

    public UnitsPanelViewModel Units { get; }

    public OperateViewModel Operate { get; }

    public ApplicationStatusViewModel Status { get; }
    public ManualControlViewModel ManualControl => _manualControl;
    public EmbeddedTerminalViewModel Terminal { get; }
    public MyTeamViewModel? MyTeam { get; }
    public UnitsWorkspaceViewModel UnitsWorkspace { get; }

    public WorkspaceNavigationItem SelectedWorkspace
    {
        get => _selectedWorkspace;
        set
        {
            if (value is null || !SetProperty(ref _selectedWorkspace, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CurrentWorkspace));
            OnPropertyChanged(nameof(CurrentWorkspaceTitle));
            OnPropertyChanged(nameof(IsOperateWorkspace));
            OnPropertyChanged(nameof(IsNotOperateWorkspace));
        }
    }

    public object CurrentWorkspace => SelectedWorkspace.Content;

    public string CurrentWorkspaceTitle => SelectedWorkspace.Title;

    public bool LeftRailOpen
    {
        get => _leftRailOpen;
        private set
        {
            if (SetProperty(ref _leftRailOpen, value))
            {
                OnPropertyChanged(nameof(LeftRailWidth));
                OnPropertyChanged(nameof(LeftRailToggleLabel));
            }
        }
    }

    public bool RightRailOpen
    {
        get => _rightRailOpen;
        private set
        {
            if (SetProperty(ref _rightRailOpen, value))
            {
                OnPropertyChanged(nameof(RightRailWidth));
                OnPropertyChanged(nameof(RightRailToggleLabel));
            }
        }
    }

    public bool SelectWorkspace(string id)
    {
        var workspace = Workspaces.FirstOrDefault(item =>
            string.Equals(item.Key, id, StringComparison.Ordinal));
        if (workspace is null)
        {
            return false;
        }

        SelectedWorkspace = workspace;
        return true;
    }

    public GridLength LeftRailWidth => LeftRailOpen ? new GridLength(200) : new GridLength(0);
    public GridLength RightRailWidth => RightRailOpen ? new GridLength(400) : new GridLength(0);
    public GridLength TerminalHeight
    {
        get => TerminalOpen ? _terminalHeight : new GridLength(0);
        set
        {
            if (value.Value <= 20)
            {
                TerminalOpen = false;
                return;
            }
            if (value.Value < 120)
                value = new GridLength(120);
            if (SetProperty(ref _terminalHeight, value) && TerminalOpen)
                OnPropertyChanged(nameof(TerminalHeight));
        }
    }

    public double TerminalResizeHeight => _terminalHeight.Value;

    public void SetTerminalHeight(double height, double windowHeight)
    {
        if (height <= 40)
        {
            TerminalOpen = false;
            return;
        }

        var maximum = Math.Max(180, windowHeight * 0.82);
        var nextHeight = new GridLength(Math.Clamp(height, 120, maximum));
        if (!TerminalOpen)
        {
            // Update the remembered size before opening so the binding exposes
            // the expanded row on the same drag gesture.
            SetProperty(ref _terminalHeight, nextHeight);
            TerminalOpen = true;
            return;
        }

        TerminalHeight = nextHeight;
    }

    public bool TerminalOpen
    {
        get => _terminalOpen;
        private set
        {
            if (!SetProperty(ref _terminalOpen, value))
                return;
            OnPropertyChanged(nameof(TerminalHeight));
            if (value)
                Terminal.WriteWelcome();
        }
    }

    public string LeftRailGlyph => "\u2039";
    public string RightRailGlyph => "\u203A";
    public string LeftRailToggleLabel => LeftRailOpen ? "<" : ">";
    public string RightRailToggleLabel => RightRailOpen ? ">" : "<";
    public bool IsOperateWorkspace => string.Equals(SelectedWorkspace.Key, "operate", StringComparison.Ordinal);
    public bool IsNotOperateWorkspace => !IsOperateWorkspace;
    public ICommand ToggleLeftRailCommand { get; }
    public ICommand ToggleRightRailCommand { get; }

    public void ToggleTerminal() => TerminalOpen = !TerminalOpen;

    public void UpdateResponsiveLayout(double availableWidth)
    {
        var shouldAutoCollapse = availableWidth < 1320;
        if (!shouldAutoCollapse)
        {
            _leftRailAutoOverrideOpen = false;
        }

        var nextAutoCollapsed = shouldAutoCollapse && !_leftRailAutoOverrideOpen;
        if (_leftRailAutoCollapsed == nextAutoCollapsed)
        {
            return;
        }

        _leftRailAutoCollapsed = nextAutoCollapsed;
        UpdateLeftRailVisibility();
    }

    private string L(string key) => _localization.Get(key);

    private void ToggleLeftRail()
    {
        if (LeftRailOpen)
        {
            _leftRailManuallyCollapsed = true;
            _leftRailAutoOverrideOpen = false;
        }
        else
        {
            _leftRailManuallyCollapsed = false;
            _leftRailAutoOverrideOpen = _leftRailAutoCollapsed;
        }

        UpdateLeftRailVisibility();
    }

    private void UpdateLeftRailVisibility()
    {
        var visible = !_leftRailManuallyCollapsed && (!_leftRailAutoCollapsed || _leftRailAutoOverrideOpen);
        LeftRailOpen = visible;
    }

    private static string WorkspaceResourceKey(string key) => key switch
    {
        "manual-control" => "InputDevices",
        "operate" => "Operate",
        "units" => "Units",
        "maps" => "Maps",
        "autonomy" => "Planning",
        "history" => "History",
        "connections" => "Connections",
        "my-team" => "MyTeam",
        "settings" => "Settings",
        _ => key
    };

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var selectedKey = _selectedWorkspace.Key;
        for (var index = 0; index < Workspaces.Count; index++)
        {
            var item = Workspaces[index];
            var resourceKey = WorkspaceResourceKey(item.Key);
            Workspaces[index] = item with
            {
                Title = L(resourceKey)
            };
        }

        _selectedWorkspace = Workspaces.First(item => item.Key == selectedKey);
        OnPropertyChanged(nameof(SelectedWorkspace));
        OnPropertyChanged(nameof(CurrentWorkspaceTitle));
    }

    public void Dispose() => Terminal.Dispose();
}
