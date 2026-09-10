using System.Windows.Input;
using RobotCommand.Infrastructure;

namespace RobotCommand.ViewModels;

/// <summary>Session-only visibility state for one saved mission on the operational map.</summary>
public sealed class MissionPreviewLayerItemViewModel : ObservableObject
{
    private readonly Action<bool> _setVisibility;
    private bool _isVisible;
    private string _name;
    private string _captureSummary;

    public MissionPreviewLayerItemViewModel(string missionId, string name, bool isVisible, Action<bool> setVisibility, string captureSummary = "")
    {
        MissionId = missionId;
        _name = name;
        _isVisible = isVisible;
        _setVisibility = setVisibility;
        _captureSummary = captureSummary;
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

    public string CaptureSummary
    {
        get => _captureSummary;
        private set => SetProperty(ref _captureSummary, value);
    }

    public bool HasCaptureSummary => !string.IsNullOrWhiteSpace(CaptureSummary);

    public ICommand ToggleCommand { get; }

    public void UpdateName(string name)
        => Name = name;

    public void UpdateCaptureSummary(string summary)
    {
        if (string.Equals(CaptureSummary, summary, StringComparison.Ordinal)) return;
        CaptureSummary = summary;
        OnPropertyChanged(nameof(HasCaptureSummary));
    }

    public void SetVisibilityFromOwner(bool value)
        => SetProperty(ref _isVisible, value);
}
