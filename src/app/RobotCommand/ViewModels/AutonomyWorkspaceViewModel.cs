using System.Collections.ObjectModel;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;

namespace RobotCommand.ViewModels;

public sealed record AutonomyWorkspaceSection(
    string Id,
    string Title,
    string Description,
    object Content);

public sealed class AutonomyWorkspaceViewModel : ObservableObject
{
    private AutonomyWorkspaceSection _selectedSection;

    public AutonomyWorkspaceViewModel(GeometryLibraryViewModel geometry, FlightMissionViewModel missions, FormationAuthoringViewModel formations, ILocalizationService localization)
    {
        Sections = new ObservableCollection<AutonomyWorkspaceSection>
        {
            new("mission", localization.Get("Mission"), localization.Get("FlightMissionDescription"), missions),
            new("geometry", localization.Get("Geometry"), localization.Get("GeometryDescription"), geometry),
            new("formations", localization.Get("Formations"), localization.Get("FormationsDescription"), formations)
        };
        _selectedSection = Sections[0];
    }

    public ObservableCollection<AutonomyWorkspaceSection> Sections { get; }

    public AutonomyWorkspaceSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null || !SetProperty(ref _selectedSection, value))
            {
                return;
            }
            OnPropertyChanged(nameof(CurrentSection));
            OnPropertyChanged(nameof(CurrentSectionTitle));
            OnPropertyChanged(nameof(CurrentSectionDescription));
        }
    }

    public object CurrentSection => SelectedSection.Content;

    public string CurrentSectionTitle => SelectedSection.Title;

    public string CurrentSectionDescription => SelectedSection.Description;

    public bool SelectSection(string id)
    {
        var section = Sections.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (section is null)
        {
            return false;
        }
        SelectedSection = section;
        return true;
    }
}

/// <summary>Intentional scope marker: mission planning follows the local geometry library.</summary>
public sealed class AutonomyMissionPlaceholderViewModel
{
    public AutonomyMissionPlaceholderViewModel(ILocalizationService localization)
    {
        Title = localization.Get("Mission");
        Message = localization.Get("MissionDeferredDescription");
    }

    public string Title { get; }
    public string Message { get; }
}
