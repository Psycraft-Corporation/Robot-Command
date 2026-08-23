using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Evidence;

namespace RobotCommand.ViewModels;

public sealed class EvidenceLibraryViewModel : ObservableObject
{
    private readonly IEvidenceLibrary _library;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IEvidenceWorkflow? _workflow;
    private EvidenceRecord? _selectedEvidence;
    private string _statusMessage = "Loading local evidence.";
    private string _storageText = "0 B";

    public EvidenceLibraryViewModel(
        IEvidenceLibrary library,
        IUiDispatcher uiDispatcher,
        IEvidenceWorkflow? workflow = null)
    {
        _library = library;
        _uiDispatcher = uiDispatcher;
        _workflow = workflow;
        Items = [];
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedEvidence is not null);
        RevealCommand = new RelayCommand(_ => RevealRoot());
        RevealSelectedCommand = new RelayCommand(_ => RevealSelected(), _ => SelectedEvidence is not null);
        _library.Changed += OnLibraryChanged;
        if (_workflow is not null) _workflow.Changed += OnLibraryChanged;
        _ = RefreshAsync(CancellationToken.None);
    }

    public ObservableCollection<EvidenceRecord> Items { get; }

    public EvidenceRecord? SelectedEvidence
    {
        get => _selectedEvidence;
        set
        {
            if (!SetProperty(ref _selectedEvidence, value)) return;
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedSummary));
            OnPropertyChanged(nameof(SelectedContext));
            (DeleteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RevealSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string SelectedTitle => SelectedEvidence?.Title ?? "No evidence selected";

    public string SelectedSummary => SelectedEvidence is null
        ? "Select a displayed-frame capture or exported clip."
        : $"{SelectedEvidence.Kind} · {FormatBytes(SelectedEvidence.SizeBytes)} · {SelectedEvidence.CreatedAt.ToLocalTime():G}";

    public string SelectedContext
    {
        get
        {
            var item = SelectedEvidence;
            if (item is null) return "-";
            var context = item.Context;
            var values = new List<string>();
            if (!string.IsNullOrWhiteSpace(context.VehicleName ?? context.VehicleId))
                values.Add(context.VehicleName ?? context.VehicleId!);
            if (!string.IsNullOrWhiteSpace(context.CameraSourceId)) values.Add(context.CameraSourceId!);
            if (!string.IsNullOrWhiteSpace(context.MissionId)) values.Add($"mission {context.MissionId}");
            if (!string.IsNullOrWhiteSpace(context.TaskId)) values.Add($"task {context.TaskId}");
            if (context.MediaTimestamp is not null) values.Add(context.MediaTimestamp.Value.ToLocalTime().ToString("G"));
            return values.Count == 0 ? "No operational context recorded" : string.Join(" · ", values);
        }
    }

    public bool IsEmpty => Items.Count == 0;

    public int Count => Items.Count;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string StorageText
    {
        get => _storageText;
        private set => SetProperty(ref _storageText, value);
    }

    public string RootPath => _library.Snapshot.RootPath;

    public ICommand RefreshCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand RevealCommand { get; }

    public ICommand RevealSelectedCommand { get; }

    private void OnLibraryChanged(object? sender, EventArgs e)
        => _ = _uiDispatcher.InvokeAsync(ApplySnapshot);

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_workflow is not null) await _workflow.RefreshAsync(cancellationToken);
            else await _library.RefreshAsync(cancellationToken);
            ApplySnapshot();
            StatusMessage = $"Loaded {_library.Snapshot.Items.Count} evidence item(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Evidence refresh failed: {ex.Message}";
        }
    }

    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedEvidence;
        if (selected is null) return;
        try
        {
            if (_workflow is not null) await _workflow.RemoveAsync(selected.Id, cancellationToken);
            else await _library.DeleteAsync(selected.Id, cancellationToken);
            SelectedEvidence = null;
            ApplySnapshot();
            StatusMessage = $"Deleted '{selected.Title}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete failed: {ex.Message}";
        }
    }

    private void ApplySnapshot()
    {
        var selectedId = SelectedEvidence?.Id;
        Items.Clear();
        foreach (var item in _library.Snapshot.Items)
        {
            Items.Add(item);
        }
        SelectedEvidence = Items.FirstOrDefault(item => item.Id == selectedId);
        StorageText = $"{FormatBytes(_library.Snapshot.TotalBytes)} · {_library.Snapshot.RootPath}";
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(RootPath));
    }

    private void RevealRoot() => RevealPath(_library.Snapshot.RootPath);

    private void RevealSelected()
    {
        if (SelectedEvidence is null) return;
        RevealPath(Path.GetDirectoryName(SelectedEvidence.FilePath) ?? _library.Snapshot.RootPath);
    }

    private void RevealPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the evidence directory: {ex.Message}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var display = (double)Math.Max(0, bytes);
        var unit = 0;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.#} {units[unit]}";
    }
}
