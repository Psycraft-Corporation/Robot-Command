using System.Windows.Input;
using RobotCommand.Infrastructure;

namespace RobotCommand.ViewModels;

/// <summary>Session-only visibility state for one saved mission on the operational map.</summary>
public sealed class MissionPreviewLayerItemViewModel : ObservableObject
{
    private readonly Action<bool> _setVisibility;
    private bool _isVisible;
    private string _name;

    public MissionPreviewLayerItemViewModel(string missionId, string name, bool isVisible, Action<bool> setVisibility)
    {
        MissionId = missionId;
        _name = name;
        _isVisible = isVisible;
        _setVisibility = setVisibility;
        ToggleCommand = new RelayCommand(_ => IsVisible = !IsVisible);
    }

    public string MissionId { get; }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (!SetProperty(ref _isVisible, value)) return;
            _setVisibility(value);
        }
    }

    public ICommand ToggleCommand { get; }

    public void UpdateName(string name)
        => Name = name;

    public void SetVisibilityFromOwner(bool value)
        => SetProperty(ref _isVisible, value);
}
