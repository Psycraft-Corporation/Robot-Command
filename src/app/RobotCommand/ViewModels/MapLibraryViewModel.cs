using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Maps;

namespace RobotCommand.ViewModels;

public sealed class MapLibraryViewModel : ObservableObject
{
    private readonly IMapPackageCatalog _catalog;
    private readonly IMapPackageInstaller _installer;
    private readonly ISavedMapViewRepository _savedViews;
    private readonly IMapDeploymentBundleService _deploymentBundles;
    private readonly IMapLibraryWorkflow? _workflow;
    private readonly AsyncRelayCommand _importCommand;
    private readonly AsyncRelayCommand _activateCommand;
    private readonly AsyncRelayCommand _removeCommand;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _exportDeploymentCommand;
    private readonly AsyncRelayCommand _importDeploymentCommand;
    private readonly RelayCommand _revealCommand;
    private InstalledMapPackage? _selectedPackage;
    private string _importPath = string.Empty;
    private string _deploymentPath = string.Empty;
    private string _deploymentName = "Field deployment";
    private string _deploymentDocuments = string.Empty;
    private string _statusMessage = "Import a local Logos map package directory or ZIP archive.";

    public MapLibraryViewModel(
        IMapPackageCatalog catalog,
        IMapPackageInstaller installer,
        ISavedMapViewRepository savedViews,
        IMapDeploymentBundleService deploymentBundles,
        IMapLibraryWorkflow? workflow = null)
    {
        _catalog = catalog;
        _installer = installer;
        _savedViews = savedViews;
        _deploymentBundles = deploymentBundles;
        _workflow = workflow;
        Packages = [];
        _catalog.Changed += OnCatalogChanged;
        if (_workflow is not null) _workflow.Changed += OnCatalogChanged;
        _savedViews.Changed += OnSavedViewsChanged;

        _importCommand = new AsyncRelayCommand(ImportAsync, () => !string.IsNullOrWhiteSpace(ImportPath));
        _activateCommand = new AsyncRelayCommand(ActivateAsync, CanActivate);
        _removeCommand = new AsyncRelayCommand(RemoveAsync, () => SelectedPackage is not null);
        _refreshCommand = new AsyncRelayCommand(RefreshAsync);
        _exportDeploymentCommand = new AsyncRelayCommand(ExportDeploymentAsync, CanExportDeployment);
        _importDeploymentCommand = new AsyncRelayCommand(ImportDeploymentAsync, CanImportDeployment);
        _revealCommand = new RelayCommand(_ => RevealSelected(), _ => SelectedPackage is not null);

        ImportCommand = _importCommand;
        ActivateCommand = _activateCommand;
        RemoveCommand = _removeCommand;
        RefreshCommand = _refreshCommand;
        ExportDeploymentCommand = _exportDeploymentCommand;
        ImportDeploymentCommand = _importDeploymentCommand;
        RevealCommand = _revealCommand;
        RefreshPresentation();
    }

    public ObservableCollection<InstalledMapPackage> Packages { get; }

    public string LibraryPath => _catalog.RootPath;

    public int Count => Packages.Count;

    public bool IsEmpty => Packages.Count == 0;

    public int SavedViewCount => _savedViews.Views.Count;

    public string TotalStorageText => FormatBytes(Packages.Sum(item => item.InstalledSizeBytes));

    public string ActivePackageText => _catalog.ActivePackage is { } active
        ? $"{active.DisplayName} {active.Version}"
        : "No active package";

    public InstalledMapPackage? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (!SetProperty(ref _selectedPackage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedPackageTitle));
            OnPropertyChanged(nameof(SelectedPackageSubtitle));
            OnPropertyChanged(nameof(SelectedPackageDetails));
            OnPropertyChanged(nameof(SelectedValidationDetails));
            RaiseCommandStates();
        }
    }

    public bool HasSelection => SelectedPackage is not null;

    public string SelectedPackageTitle => SelectedPackage?.DisplayName ?? "No package selected";

    public string SelectedPackageSubtitle => SelectedPackage is null
        ? "Select an installed package to inspect or activate it."
        : $"{SelectedPackage.Kind} · {SelectedPackage.StateLabel} · {SelectedPackage.Version}";

    public string SelectedPackageDetails
    {
        get
        {
            var package = SelectedPackage;
            if (package is null)
            {
                return "-";
            }

            return string.Join(Environment.NewLine,
                $"Package: {package.PackageId}",
                $"Version: {package.Version}",
                $"Coverage: {package.CoverageSummary}",
                $"Zoom: {package.ZoomSummary}",
                $"Size: {FormatBytes(package.InstalledSizeBytes)}",
                $"Dataset: {package.DatasetDate?.ToLocalTime().ToString("d") ?? "Not declared"}",
                $"Build: {package.BuildDate?.ToLocalTime().ToString("g") ?? "Not declared"}",
                $"Presentations: {package.StylesSummary}",
                $"Default presentation: {package.DefaultStyleId ?? "Operational"}",
                $"Attribution: {package.Attribution}",
                $"Licence: {package.License}",
                $"Directory: {package.DirectoryPath}");
        }
    }

    public string SelectedValidationDetails => SelectedPackage is null
        ? "No package selected."
        : SelectedPackage.ValidationIssues.Count == 0
            ? SelectedPackage.ValidationSummary
            : $"{SelectedPackage.ValidationSummary}{Environment.NewLine}{string.Join(Environment.NewLine, SelectedPackage.ValidationIssues)}";

    public string ImportPath
    {
        get => _importPath;
        set
        {
            if (SetProperty(ref _importPath, value))
            {
                _importCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DeploymentPath
    {
        get => _deploymentPath;
        set
        {
            if (SetProperty(ref _deploymentPath, value))
            {
                _exportDeploymentCommand.RaiseCanExecuteChanged();
                _importDeploymentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DeploymentName
    {
        get => _deploymentName;
        set => SetProperty(ref _deploymentName, value);
    }

    public string DeploymentDocuments
    {
        get => _deploymentDocuments;
        set => SetProperty(ref _deploymentDocuments, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ICommand ImportCommand { get; }

    public ICommand ActivateCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand ExportDeploymentCommand { get; }

    public ICommand ImportDeploymentCommand { get; }

    public ICommand RevealCommand { get; }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_workflow is not null)
            {
                var result = await _workflow.ImportAsync(ImportPath, cancellationToken);
                RefreshPresentation(result.Key);
                StatusMessage = $"Imported {result.DisplayName}.";
            }
            else
            {
                var result = await _installer.ImportAsync(ImportPath, cancellationToken);
                RefreshPresentation(result.Package.Key);
                StatusMessage = result.Message;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Import failed: {ex.Message}";
        }
    }

    private async Task ActivateAsync(CancellationToken cancellationToken)
    {
        if (SelectedPackage is null)
        {
            return;
        }

        try
        {
            var key = SelectedPackage.Key;
            if (_workflow is not null) await _workflow.ActivateAsync(key, cancellationToken);
            else await _catalog.ActivateAsync(key, cancellationToken);
            RefreshPresentation(key);
            StatusMessage = $"Activated {SelectedPackage?.DisplayName ?? key}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Activation failed: {ex.Message}";
        }
    }

    private async Task RemoveAsync(CancellationToken cancellationToken)
    {
        if (SelectedPackage is null)
        {
            return;
        }

        var key = SelectedPackage.Key;
        var name = SelectedPackage.DisplayName;
        try
        {
            if (_workflow is not null) await _workflow.RemoveAsync(key, cancellationToken);
            else await _installer.RemoveAsync(key, cancellationToken);
            RefreshPresentation();
            StatusMessage = $"Removed {name}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Removal failed: {ex.Message}";
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var selectedKey = SelectedPackage?.Key;
        if (_workflow is not null) await _workflow.RefreshAsync(cancellationToken);
        else await _catalog.RefreshAsync(cancellationToken);
        RefreshPresentation(selectedKey);
        StatusMessage = "Map package catalog refreshed.";
    }

    private async Task ExportDeploymentAsync(CancellationToken cancellationToken)
    {
        if (SelectedPackage is null)
        {
            return;
        }

        try
        {
            var result = await _deploymentBundles.ExportAsync(
                new MapDeploymentExportRequest(
                    DeploymentPath,
                    DeploymentName,
                    [SelectedPackage.Key],
                    _savedViews.Views
                        .Where(item => string.IsNullOrWhiteSpace(item.PackageKey) ||
                                       string.Equals(item.PackageKey, SelectedPackage.Key, StringComparison.Ordinal))
                        .Select(item => item.Id)
                        .ToArray(),
                    ParseDocumentPaths(DeploymentDocuments)),
                cancellationToken);
            DeploymentPath = result.BundlePath;
            StatusMessage = $"Exported {result.PackageCount} package(s), {result.SavedViewCount} saved view(s), and {result.DocumentCount} document(s) to {result.BundlePath}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Deployment export failed: {ex.Message}";
        }
    }

    private async Task ImportDeploymentAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _deploymentBundles.ImportAsync(DeploymentPath, cancellationToken);
            RefreshPresentation(result.Packages.FirstOrDefault()?.Key);
            OnPropertyChanged(nameof(SavedViewCount));
            StatusMessage = $"Imported deployment '{result.DisplayName}': {result.Packages.Count} package(s), {result.SavedViewCount} saved view(s)." +
                            (result.DocumentsDirectory is null ? string.Empty : $" Documents: {result.DocumentsDirectory}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Deployment import failed: {ex.Message}";
        }
    }

    private void RevealSelected()
    {
        if (SelectedPackage is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SelectedPackage.DirectoryPath,
                UseShellExecute = true
            });
            StatusMessage = $"Opened {SelectedPackage.DirectoryPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the package directory: {ex.Message}";
        }
    }

    private bool CanActivate()
        => SelectedPackage is { Valid: true, Active: false, PrimaryMbTilesPath: not null };

    private bool CanExportDeployment()
        => SelectedPackage is not null && !string.IsNullOrWhiteSpace(DeploymentPath);

    private bool CanImportDeployment()
        => !string.IsNullOrWhiteSpace(DeploymentPath);

    private void OnCatalogChanged(object? sender, EventArgs e)
        => RefreshPresentation(SelectedPackage?.Key);

    private void OnSavedViewsChanged(object? sender, EventArgs e)
        => OnPropertyChanged(nameof(SavedViewCount));

    private void RefreshPresentation(string? selectedKey = null)
    {
        selectedKey ??= SelectedPackage?.Key;
        Packages.Clear();
        foreach (var package in _catalog.Packages)
        {
            Packages.Add(package);
        }

        SelectedPackage = selectedKey is null
            ? Packages.FirstOrDefault(item => item.Active) ?? Packages.FirstOrDefault()
            : Packages.FirstOrDefault(item => item.Key == selectedKey)
              ?? Packages.FirstOrDefault(item => item.Active)
              ?? Packages.FirstOrDefault();

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(TotalStorageText));
        OnPropertyChanged(nameof(ActivePackageText));
        OnPropertyChanged(nameof(SavedViewCount));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        _activateCommand.RaiseCanExecuteChanged();
        _removeCommand.RaiseCanExecuteChanged();
        _revealCommand.RaiseCanExecuteChanged();
        _exportDeploymentCommand.RaiseCanExecuteChanged();
        _importDeploymentCommand.RaiseCanExecuteChanged();
    }

    private static string[] ParseDocumentPaths(string value)
        => value.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return $"{display:0.##} {units[unit]}";
    }
}
