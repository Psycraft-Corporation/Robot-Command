using System.Collections.ObjectModel;
using System.Windows.Input;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Autonomy;
using RobotCommand.Services.Behaviours;
using RobotCommand.Services.Geometry;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class AutonomyDeploymentBundleViewModel : ObservableObject, IDisposable
{
    private readonly IAutonomyDeploymentBundleService _bundles;
    private readonly IBehaviourPackageStore _behaviours;
    private readonly IGeometryDocumentStore _geometry;
    private readonly IBehaviourBindingWorkspaceService _bindings;
    private readonly IUiDispatcher _dispatcher;
    private readonly AsyncRelayCommand _exportCommand;
    private readonly AsyncRelayCommand _inspectCommand;
    private readonly AsyncRelayCommand _importCommand;

    private ConnectionRecord? _selectedConnection;
    private MissionRecord? _selectedMission;
    private OperationalTaskRecord? _selectedTask;
    private string _bundleName = "Field autonomy deployment";
    private string _description = "Portable Logos autonomy assets and plan templates.";
    private string _exportPath = "autonomy-deployment.logos-autonomy.zip";
    private string _importPath = string.Empty;
    private string _policyPaths = string.Empty;
    private bool _includeLocalBehaviours = true;
    private bool _includeLocalGeometry = true;
    private bool _includeSelectedMission = true;
    private bool _includeSelectedTask = true;
    private bool _captureBindings = true;
    private bool _allowReplaceArchive;
    private bool _allowReplaceImportedBundle;
    private bool _allowReplaceImportedAssets;
    private bool _importMissions = true;
    private bool _importTasks = true;
    private bool _busy;
    private int _disposed;
    private string _status = "Export a portable deployment bundle or inspect one before importing it.";
    private string _manifestSummary = "No bundle has been inspected in this session.";

    public AutonomyDeploymentBundleViewModel(
        IAutonomyDeploymentBundleService bundles,
        IBehaviourPackageStore behaviours,
        IGeometryDocumentStore geometry,
        IBehaviourBindingWorkspaceService bindings,
        IEntityStore<string, ConnectionRecord> connections,
        IEntityStore<string, MissionRecord> missions,
        IEntityStore<string, OperationalTaskRecord> tasks,
        IUiDispatcher dispatcher)
    {
        _bundles = bundles;
        _behaviours = behaviours;
        _geometry = geometry;
        _bindings = bindings;
        _dispatcher = dispatcher;
        _behaviours.Changed += OnLocalAssetsChanged;
        _geometry.Changed += OnLocalAssetsChanged;

        Connections = connections.Items;
        Missions = missions.Items;
        Tasks = tasks.Items;
        Plan = [];
        Issues = [];
        _selectedConnection = Connections.FirstOrDefault();
        _selectedMission = Missions.FirstOrDefault(item => item.IsLocalDraft) ?? Missions.FirstOrDefault();
        _selectedTask = Tasks.FirstOrDefault(item => item.IsLocalDraft) ?? Tasks.FirstOrDefault();

        _exportCommand = new AsyncRelayCommand(ExportAsync, CanExport);
        _inspectCommand = new AsyncRelayCommand(InspectAsync, CanInspect);
        _importCommand = new AsyncRelayCommand(ImportAsync, CanImport);
    }

    public ReadOnlyObservableCollection<ConnectionRecord> Connections { get; }

    public ReadOnlyObservableCollection<MissionRecord> Missions { get; }

    public ReadOnlyObservableCollection<OperationalTaskRecord> Tasks { get; }

    public ObservableCollection<AutonomyDeploymentStep> Plan { get; }

    public ObservableCollection<string> Issues { get; }

    public string BundleRootPath => _bundles.RootPath;

    public int LocalBehaviourCount => _behaviours.Packages.Count;

    public int LocalGeometryCount => _geometry.Documents.Count;

    public ConnectionRecord? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (SetProperty(ref _selectedConnection, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public MissionRecord? SelectedMission
    {
        get => _selectedMission;
        set
        {
            if (SetProperty(ref _selectedMission, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public OperationalTaskRecord? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string BundleName
    {
        get => _bundleName;
        set
        {
            if (SetProperty(ref _bundleName, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string ExportPath
    {
        get => _exportPath;
        set
        {
            if (SetProperty(ref _exportPath, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string ImportPath
    {
        get => _importPath;
        set
        {
            if (SetProperty(ref _importPath, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string PolicyPaths
    {
        get => _policyPaths;
        set
        {
            if (SetProperty(ref _policyPaths, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IncludeLocalBehaviours
    {
        get => _includeLocalBehaviours;
        set
        {
            if (SetProperty(ref _includeLocalBehaviours, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IncludeLocalGeometry
    {
        get => _includeLocalGeometry;
        set
        {
            if (SetProperty(ref _includeLocalGeometry, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IncludeSelectedMission
    {
        get => _includeSelectedMission;
        set
        {
            if (SetProperty(ref _includeSelectedMission, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IncludeSelectedTask
    {
        get => _includeSelectedTask;
        set
        {
            if (SetProperty(ref _includeSelectedTask, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool CaptureBindings
    {
        get => _captureBindings;
        set
        {
            if (SetProperty(ref _captureBindings, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool AllowReplaceArchive
    {
        get => _allowReplaceArchive;
        set => SetProperty(ref _allowReplaceArchive, value);
    }

    public bool AllowReplaceImportedBundle
    {
        get => _allowReplaceImportedBundle;
        set => SetProperty(ref _allowReplaceImportedBundle, value);
    }

    public bool AllowReplaceImportedAssets
    {
        get => _allowReplaceImportedAssets;
        set => SetProperty(ref _allowReplaceImportedAssets, value);
    }

    public bool ImportMissions
    {
        get => _importMissions;
        set => SetProperty(ref _importMissions, value);
    }

    public bool ImportTasks
    {
        get => _importTasks;
        set => SetProperty(ref _importTasks, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ManifestSummary
    {
        get => _manifestSummary;
        private set => SetProperty(ref _manifestSummary, value);
    }

    public bool HasPlan => Plan.Count > 0;

    public bool HasIssues => Issues.Count > 0;

    public ICommand ExportCommand => _exportCommand;

    public ICommand InspectCommand => _inspectCommand;

    public ICommand ImportCommand => _importCommand;

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        Busy = true;
        try
        {
            var identities = IncludeLocalBehaviours
                ? _behaviours.Packages
                    .Where(item => item.Integrity.Accepted)
                    .Select(item => item.Identity)
                    .Distinct()
                    .ToArray()
                : [];
            var geometryIds = IncludeLocalGeometry
                ? _geometry.Documents.Select(item => item.GeometryId).Distinct(StringComparer.Ordinal).ToArray()
                : [];
            var bindings = await CaptureBindingIntentsAsync(identities, geometryIds, cancellationToken);
            var request = new AutonomyBundleExportRequest(
                ExportPath.Trim(),
                BundleName.Trim(),
                Description.Trim(),
                identities,
                geometryIds,
                IncludeSelectedMission && SelectedMission is not null ? [SelectedMission.Id] : [],
                IncludeSelectedTask && SelectedTask is not null ? [SelectedTask.Id] : [],
                ParsePolicyPaths(),
                bindings,
                AllowReplaceArchive,
                Metadata: new Dictionary<string, string>
                {
                    ["producer"] = "Robot Command",
                    ["bindingSourceConnection"] = SelectedConnection?.Id ?? string.Empty
                });
            var manifest = await _bundles.ExportAsync(request, cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                ManifestSummary = $"{manifest.DisplayName} · {manifest.AssetCount} asset reference(s) · {manifest.Files.Count} hashed file(s)";
                ApplyPlan(AutonomyDeploymentBundleRules.BuildPlan(manifest), []);
                Status = $"Exported autonomy deployment bundle to '{Path.GetFullPath(ExportPath.Trim())}'.";
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"Bundle export failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task InspectAsync(CancellationToken cancellationToken)
    {
        Busy = true;
        try
        {
            var result = await _bundles.InspectAsync(ImportPath.Trim(), cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                ManifestSummary = result.Manifest is null
                    ? "No usable deployment manifest was found."
                    : $"{result.Manifest.DisplayName} · {result.Manifest.AssetCount} asset reference(s) · created {result.Manifest.CreatedAt.ToLocalTime():g}";
                ApplyPlan(result.Plan, result.Issues);
                Status = result.Summary;
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"Bundle inspection failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        Busy = true;
        try
        {
            var result = await _bundles.ImportAsync(
                ImportPath.Trim(),
                new AutonomyBundleImportOptions(
                    AllowReplaceImportedBundle,
                    AllowReplaceImportedAssets,
                    AllowReplaceImportedAssets,
                    ImportMissions,
                    ImportTasks),
                cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                ApplyPlan(result.Steps, result.Issues);
                ManifestSummary = string.IsNullOrWhiteSpace(result.ImportedBundlePath)
                    ? "The bundle was not imported."
                    : $"Retained at {result.ImportedBundlePath}";
                Status = result.Summary;
                OnPropertyChanged(nameof(LocalBehaviourCount));
                OnPropertyChanged(nameof(LocalGeometryCount));
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"Bundle import failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<IReadOnlyList<AutonomyBundleBindingIntent>> CaptureBindingIntentsAsync(
        BehaviourPackageIdentity[] identities,
        string[] geometryIds,
        CancellationToken cancellationToken)
    {
        if (!CaptureBindings ||
            SelectedConnection is null ||
            identities.Length == 0 ||
            geometryIds.Length == 0)
        {
            return [];
        }

        var includedGeometry = geometryIds.ToHashSet(StringComparer.Ordinal);
        var result = new List<AutonomyBundleBindingIntent>();
        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await _bindings.InspectAsync(
                    SelectedConnection.Id,
                    identity,
                    refreshPackages: false,
                    refreshGeometry: false,
                    cancellationToken: cancellationToken);
                result.AddRange(snapshot.Slots
                    .Where(item =>
                        item.Bound &&
                        !string.IsNullOrWhiteSpace(item.GeometryId) &&
                        includedGeometry.Contains(item.GeometryId!))
                    .Select(item => new AutonomyBundleBindingIntent(
                        identity.BehaviourId,
                        identity.Version,
                        item.SlotId,
                        item.GeometryId!,
                        item.Required)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _ = exception;
                // A portable bundle remains useful without a runtime binding snapshot.
                // The dependency plan will simply omit bindings that could not be read.
            }
        }
        return result
            .DistinctBy(item => (item.BehaviourId, item.Version, item.SlotId))
            .ToArray();
    }

    private string[] ParsePolicyPaths()
        => PolicyPaths
            .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private bool CanExport()
        => !Busy &&
           !string.IsNullOrWhiteSpace(BundleName) &&
           !string.IsNullOrWhiteSpace(ExportPath) &&
           (IncludeLocalBehaviours && LocalBehaviourCount > 0 ||
            IncludeLocalGeometry && LocalGeometryCount > 0 ||
            IncludeSelectedMission && SelectedMission is not null ||
            IncludeSelectedTask && SelectedTask is not null ||
             ParsePolicyPaths().Length > 0);

    private bool CanInspect()
        => !Busy && !string.IsNullOrWhiteSpace(ImportPath);

    private bool CanImport()
        => CanInspect();

    private void ApplyPlan(
        IEnumerable<AutonomyDeploymentStep> steps,
        IEnumerable<string> issues)
    {
        Plan.Clear();
        foreach (var step in steps)
        {
            Plan.Add(step);
        }
        Issues.Clear();
        foreach (var issue in issues)
        {
            Issues.Add(issue);
        }
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasIssues));
    }

    private void RaiseCommandStates()
    {
        _exportCommand.RaiseCanExecuteChanged();
        _inspectCommand.RaiseCanExecuteChanged();
        _importCommand.RaiseCanExecuteChanged();
    }
    private void OnLocalAssetsChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(LocalBehaviourCount));
            OnPropertyChanged(nameof(LocalGeometryCount));
            RaiseCommandStates();
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _behaviours.Changed -= OnLocalAssetsChanged;
        _geometry.Changed -= OnLocalAssetsChanged;
    }

}
