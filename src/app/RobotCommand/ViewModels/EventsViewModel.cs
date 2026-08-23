using System.Collections.ObjectModel;
using System.Collections.Specialized;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class EventsViewModel : ObservableObject
{
    private readonly ISelectionService _selection;
    private ConsoleEventRecord? _selectedEvent;

    public EventsViewModel(
        IEntityStore<string, ConsoleEventRecord> events,
        ISelectionService selection)
    {
        _selection = selection;
        Events = events.Items;
        ((INotifyCollectionChanged)Events).CollectionChanged += OnCollectionChanged;
    }

    public ReadOnlyObservableCollection<ConsoleEventRecord> Events { get; }

    public bool IsEmpty => Events.Count == 0;

    public int Count => Events.Count;

    public ConsoleEventRecord? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (SetProperty(ref _selectedEvent, value) && value is not null)
            {
                _selection.Select(SelectionFactory.From(value));
            }
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Count));
    }
}
