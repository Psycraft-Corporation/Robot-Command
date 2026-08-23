using System.Collections.ObjectModel;
using System.Collections.Specialized;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class CommandsViewModel : ObservableObject
{
    private readonly ISelectionService _selection;
    private OperationalCommandRecord? _selectedCommand;

    public CommandsViewModel(
        IEntityStore<string, OperationalCommandRecord> commands,
        ISelectionService selection)
    {
        _selection = selection;
        Commands = commands.Items;
        ((INotifyCollectionChanged)Commands).CollectionChanged += OnCollectionChanged;
    }

    public ReadOnlyObservableCollection<OperationalCommandRecord> Commands { get; }

    public int Count => Commands.Count;

    public bool IsEmpty => Commands.Count == 0;

    public OperationalCommandRecord? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (SetProperty(ref _selectedCommand, value) && value is not null)
            {
                _selection.Select(SelectionFactory.From(value));
            }
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));

        if (SelectedCommand is not null)
        {
            var replacement = Commands.FirstOrDefault(item => item.Id == SelectedCommand.Id);
            if (replacement is not null && !ReferenceEquals(replacement, SelectedCommand))
            {
                _selectedCommand = replacement;
                OnPropertyChanged(nameof(SelectedCommand));
                _selection.Select(SelectionFactory.From(replacement));
            }
        }
    }
}
