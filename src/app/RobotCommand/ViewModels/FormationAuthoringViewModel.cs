using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Rendering;

namespace RobotCommand.ViewModels;

public sealed class FormationAuthoringViewModel : ObservableObject, IDisposable
{
    private readonly IFormationAuthoringWorkflow _workflow;
    private FormationWorkflowSnapshot? _selectedFormation;
    private FormationWorkflowSnapshot? _draft;
    private FormationMemberEditorViewModel? _selectedMember;
    private string _status = string.Empty;
    private long _sceneRevision;
    private string _formationName = string.Empty;
    private bool _draftDirty;
    private bool _loadingDraft;

    public FormationAuthoringViewModel(IFormationAuthoringWorkflow workflow)
    {
        _workflow = workflow;
        Formations = [];
        Members = [];
        NewCommand = new AsyncRelayCommand(CreateAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsEditing && IsDirty);
        CancelCommand = new AsyncRelayCommand(Cancel, () => IsEditing && IsDirty);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedFormation is not null && !IsDirty);
        AddMemberCommand = new AsyncRelayCommand(AddMemberAsync, () => IsEditing);
        RemoveMemberCommand = new AsyncRelayCommand(RemoveMemberAsync, () => IsEditing && SelectedMember is not null && Members.Count > 1);
        _workflow.Changed += OnChanged;
        Refresh();
    }

    public ObservableCollection<FormationWorkflowSnapshot> Formations { get; }
    public ObservableCollection<FormationMemberEditorViewModel> Members { get; }
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand AddMemberCommand { get; }
    public ICommand RemoveMemberCommand { get; }
    public ThreeDSceneSnapshot? Scene => SelectedFormation is null
        ? null
        : FormationAuthoringSceneBuilder.Build(_draft is null ? SelectedFormation : ToSnapshot(), SelectedMember?.Id, _camera, ++_sceneRevision);
    public bool IsEditing => _draft is not null;
    public bool IsDirty => _draftDirty;
    public bool HasSelection => SelectedFormation is not null;
    public bool HasSelectedMember => SelectedMember is not null;
    public FormationWorkflowSnapshot? SelectedFormation
    {
        get => _selectedFormation;
        set
        {
            if (value is null || value.Id == _selectedFormation?.Id) return;
            if (IsDirty)
            {
                Status = "Save or cancel the current formation before selecting another.";
                OnPropertyChanged(nameof(SelectedFormation));
                return;
            }
            if (!SetProperty(ref _selectedFormation, value)) return;
            LoadDraft(value);
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(Scene));
            RaiseCommands();
        }
    }
    public FormationMemberEditorViewModel? SelectedMember
    {
        get => _selectedMember;
        set
        {
            if (SetProperty(ref _selectedMember, value))
            {
                OnPropertyChanged(nameof(HasSelectedMember));
                OnPropertyChanged(nameof(Scene));
            }
            RaiseCommands();
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string FormationName { get => _formationName; set { if (SetProperty(ref _formationName, value)) OnDraftChanged(); } }
    private ThreeDCameraSnapshot? _camera;

    private async Task CreateAsync(CancellationToken token)
    {
        if (IsDirty)
        {
            Status = "Save or cancel the current formation before creating another.";
            return;
        }
        var created = await _workflow.CreateAsync(new($"Formation {Formations.Count + 1}"), token);
        SelectedFormation = created;
        LoadDraft(created);
        Status = string.Empty;
    }
    private async Task SaveAsync(CancellationToken token)
    {
        if (_draft is null) return;
        try
        {
            var saved = await _workflow.SaveAsync(ToSnapshot(), token);
            _selectedFormation = saved;
            OnPropertyChanged(nameof(SelectedFormation));
            LoadDraft(saved);
            Status = string.Empty;
        }
        catch (Exception exception) { Status = exception.Message; }
    }
    private Task Cancel(CancellationToken token)
    {
        if (SelectedFormation is not null) LoadDraft(SelectedFormation);
        Status = "";
        return Task.CompletedTask;
    }
    private async Task DeleteAsync(CancellationToken token)
    {
        if (SelectedFormation is null) return;
        if (IsDirty)
        {
            Status = "Save or cancel the current formation before deleting it.";
            return;
        }
        var id = SelectedFormation.Id;
        await _workflow.RemoveAsync(id, token);
        _selectedFormation = null;
        _draft = null;
        Members.Clear();
        Refresh();
        Status = string.Empty;
    }
    private Task AddMemberAsync(CancellationToken token)
    {
        if (_draft is null) return Task.CompletedTask;
        var number = Members.Count + 1;
        while (Members.Any(item => item.Name.Equals($"Unit {number}", StringComparison.OrdinalIgnoreCase))) number++;
        Members.Add(new FormationMemberEditorViewModel(new($"unit-draft-{Guid.NewGuid():N}", $"Unit {number}", 0, 0, 0), OnMemberChanged));
        SelectedMember = Members[^1];
        OnDraftChanged();
        return Task.CompletedTask;
    }
    private Task RemoveMemberAsync(CancellationToken token)
    {
        if (SelectedMember is null || Members.Count <= 1) return Task.CompletedTask;
        var index = Members.IndexOf(SelectedMember);
        Members.Remove(SelectedMember);
        SelectedMember = Members[Math.Clamp(index, 0, Members.Count - 1)];
        OnDraftChanged();
        return Task.CompletedTask;
    }
    private void LoadDraft(FormationWorkflowSnapshot snapshot)
    {
        _loadingDraft = true;
        _draft = snapshot;
        FormationName = snapshot.Name;
        Members.Clear();
        foreach (var member in snapshot.Members) Members.Add(new(member, OnMemberChanged));
        SelectedMember = Members.FirstOrDefault();
        _draftDirty = false;
        _loadingDraft = false;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Scene));
        RaiseCommands();
    }
    private FormationWorkflowSnapshot ToSnapshot() => new(_draft!.Id, FormationName, _draft.CreatedAt, _draft.UpdatedAt,
        Members.Select(item => item.ToSnapshot()).ToArray());
    private void OnMemberChanged() => OnDraftChanged();
    private void OnDraftChanged()
    {
        if (!_loadingDraft && !_draftDirty)
        {
            _draftDirty = true;
            OnPropertyChanged(nameof(IsDirty));
        }
        OnPropertyChanged(nameof(Scene));
        RaiseCommands();
    }
    private void Refresh()
    {
        var selectedId = SelectedFormation?.Id;
        Formations.Clear();
        foreach (var item in _workflow.Formations) Formations.Add(item);
        _selectedFormation = selectedId is null ? Formations.FirstOrDefault() : Formations.FirstOrDefault(item => item.Id == selectedId);
        OnPropertyChanged(nameof(SelectedFormation));
        OnPropertyChanged(nameof(HasSelection));
        if (_selectedFormation is not null && !IsDirty)
            LoadDraft(_selectedFormation);
        else if (_selectedFormation is null)
        {
            _draft = null;
            _draftDirty = false;
            Members.Clear();
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(Scene));
            RaiseCommands();
        }
    }
    private void OnChanged(object? sender, EventArgs e) => Refresh();
    private void RaiseCommands()
    {
        foreach (var command in new[] { SaveCommand, CancelCommand, DeleteCommand, AddMemberCommand, RemoveMemberCommand })
            if (command is AsyncRelayCommand async) async.RaiseCanExecuteChanged();
    }
    public Task SetCameraAsync(ThreeDCameraSnapshot camera) { _camera = camera; OnPropertyChanged(nameof(Scene)); return Task.CompletedTask; }
    public void ResetCamera() { _camera = null; OnPropertyChanged(nameof(Scene)); }
    public void Dispose() => _workflow.Changed -= OnChanged;
}

public sealed class FormationMemberEditorViewModel : ObservableObject
{
    private readonly Action _changed;
    private string _name;
    private double _east;
    private double _up;
    private double _north;
    public FormationMemberEditorViewModel(FormationMemberSnapshot snapshot, Action changed) { Id = snapshot.Id; _name = snapshot.Name; _east = snapshot.EastMetres; _up = snapshot.UpMetres; _north = snapshot.NorthMetres; _changed = changed; }
    public string Id { get; }
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) _changed(); } }
    public double EastMetres { get => _east; set { if (SetProperty(ref _east, value)) { OnPropertyChanged(nameof(EastText)); _changed(); } } }
    public double UpMetres { get => _up; set { if (SetProperty(ref _up, value)) { OnPropertyChanged(nameof(UpText)); _changed(); } } }
    public double NorthMetres { get => _north; set { if (SetProperty(ref _north, value)) { OnPropertyChanged(nameof(NorthText)); _changed(); } } }
    public string EastText { get => Format(_east); set { if (TryParse(value, out var parsed)) EastMetres = parsed; } }
    public string UpText { get => Format(_up); set { if (TryParse(value, out var parsed)) UpMetres = parsed; } }
    public string NorthText { get => Format(_north); set { if (TryParse(value, out var parsed)) NorthMetres = parsed; } }
    public FormationMemberSnapshot ToSnapshot() => new(Id, Name, EastMetres, UpMetres, NorthMetres);
    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static bool TryParse(string? value, out double parsed) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && double.IsFinite(parsed);
}
