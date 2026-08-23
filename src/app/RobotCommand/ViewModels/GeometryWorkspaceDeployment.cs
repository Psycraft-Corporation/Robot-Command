using RobotCommand.Models;

namespace RobotCommand.ViewModels;

public sealed partial class GeometryWorkspaceViewModel
{
    private GeometryOperationAssessment? _remoteAssessment;
    private string _lastRemoteOperationDetails = "No remote geometry operation has been attempted in this session.";
    private string _conflictCopyId = string.Empty;

    public GeometryOperationAssessment? RemoteAssessment
    {
        get => _remoteAssessment;
        private set
        {
            if (!SetProperty(ref _remoteAssessment, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasRemoteAssessment));
            OnPropertyChanged(nameof(AssessmentSummary));
            OnPropertyChanged(nameof(AssessmentDetails));
            OnPropertyChanged(nameof(SelectedRemoteOperation));
            OnPropertyChanged(nameof(SelectedRemoteOperationSummary));
        }
    }

    public bool HasRemoteAssessment => RemoteAssessment is not null;

    public string SelectedRemoteOperation => ResolveSelectedRemoteOperation()?.ToString() ?? "Unavailable";

    public string SelectedRemoteOperationSummary => $"Proposed operation: {SelectedRemoteOperation}";

    public string AssessmentSummary => RemoteAssessment?.Summary
                                       ?? "Choose a local or remote geometry object, then validate the proposed Logos operation.";

    public string ConflictCopyId
    {
        get => _conflictCopyId;
        set
        {
            if (SetProperty(ref _conflictCopyId, value ?? string.Empty))
            {
                _duplicateLocalCommand.RaiseCanExecuteChanged();
                _pullRemoteCopyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasConflictResolutionContext =>
        SelectedLocal?.Deployment?.Status is GeometryDeploymentStatus.Conflict or GeometryDeploymentStatus.RemoteModified ||
        SelectedRemote?.Deployment?.Status is GeometryDeploymentStatus.Conflict or GeometryDeploymentStatus.RemoteModified;

    public string LastRemoteOperationDetails
    {
        get => _lastRemoteOperationDetails;
        private set => SetProperty(ref _lastRemoteOperationDetails, value);
    }

    public string AssessmentDetails
    {
        get
        {
            if (RemoteAssessment is null)
            {
                return "Local structural validation, Logos validation, deployment state, and the optimistic-concurrency revision will be reviewed separately.";
            }

            var assessment = RemoteAssessment;
            var lines = new List<string>
            {
                $"Operation: {assessment.Operation}",
                $"State: {assessment.State}",
                $"Expected revision: {assessment.ExpectedRevision ?? "Not applicable"}",
                $"Local validation: {assessment.LocalValidation?.State.ToString() ?? "Not applicable"}",
                $"Logos validation: {assessment.LogosValidation?.State.ToString() ?? "Not applicable"}",
                $"Deployment: {assessment.Deployment?.Status.ToString() ?? "Unknown"}",
                string.Empty,
                assessment.Summary
            };
            if (assessment.Findings.Count > 0)
            {
                lines.Add(string.Empty);
                lines.AddRange(assessment.Findings.Select(item =>
                    $"[{item.Severity}] {item.Code} · {item.Source}: {item.Message}"));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    private async Task AssessRemoteAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null)
        {
            return;
        }

        var operation = ResolveSelectedRemoteOperation();
        var geometryId = SelectedLocal?.GeometryId ?? SelectedRemote?.GeometryId;
        if (operation is null || string.IsNullOrWhiteSpace(geometryId))
        {
            return;
        }

        try
        {
            RemoteAssessment = await _workspace.AssessRemoteOperationAsync(
                SelectedConnection.Id,
                geometryId,
                operation.Value,
                cancellationToken);
            StatusMessage = RemoteAssessment.Summary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RemoteAssessment = null;
            StatusMessage = $"Could not validate the Logos geometry operation: {ex.Message}";
        }
    }

    private bool CanAssessRemote()
        => SelectedConnection is not null &&
           !AuthoringDirty &&
           !MapEdit.HasDraft &&
           ResolveSelectedRemoteOperation() is not null &&
           (SelectedLocal is not null || SelectedRemote is not null);

    private GeometryRemoteOperationKind? ResolveSelectedRemoteOperation()
    {
        if (SelectedLocal is not null)
        {
            return SelectedLocal.Deployment?.Status switch
            {
                GeometryDeploymentStatus.LocalOnly or GeometryDeploymentStatus.MissingRemote
                    => GeometryRemoteOperationKind.Create,
                GeometryDeploymentStatus.RemoteOnly => GeometryRemoteOperationKind.Delete,
                _ => GeometryRemoteOperationKind.Update
            };
        }

        return SelectedRemote is not null ? GeometryRemoteOperationKind.Delete : null;
    }

    private bool AssessmentMatchesCurrentSelection()
    {
        if (RemoteAssessment is null)
        {
            return true;
        }

        var geometryId = SelectedLocal?.GeometryId ?? SelectedRemote?.GeometryId;
        return SelectedConnection is not null &&
               string.Equals(RemoteAssessment.ConnectionId, SelectedConnection.Id, StringComparison.Ordinal) &&
               string.Equals(RemoteAssessment.GeometryId, geometryId, StringComparison.Ordinal) &&
               RemoteAssessment.Operation == ResolveSelectedRemoteOperation();
    }

    private async Task DuplicateLocalForConflictAsync(CancellationToken cancellationToken)
    {
        if (SelectedLocal is null)
        {
            return;
        }

        try
        {
            var copy = await _workspace.DuplicateLocalAsync(
                SelectedLocal.GeometryId,
                ConflictCopyId,
                cancellationToken);
            _selectedLocalId = copy.GeometryId;
            RefreshPresentation();
            StatusMessage = $"Preserved the local geometry as '{copy.GeometryId}'. The original deployment was not changed.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not preserve the local geometry as a copy: {ex.Message}";
        }
    }

    private bool CanDuplicateLocalForConflict()
        => SelectedLocal is not null &&
           !string.IsNullOrWhiteSpace(ConflictCopyId) &&
           !_workspace.LocalDocuments.Any(item =>
               string.Equals(item.GeometryId, ConflictCopyId.Trim(), StringComparison.Ordinal));

    private async Task PullRemoteCopyAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null || SelectedRemote is null)
        {
            return;
        }

        try
        {
            var copy = await _workspace.PullAsLocalCopyAsync(
                SelectedConnection.Id,
                SelectedRemote.GeometryId,
                ConflictCopyId,
                cancellationToken);
            _selectedLocalId = copy.GeometryId;
            RefreshPresentation();
            StatusMessage = $"Pulled the Logos geometry as independent local copy '{copy.GeometryId}'.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not pull the remote geometry as a copy: {ex.Message}";
        }
    }

    private bool CanPullRemoteCopy()
        => SelectedConnection is not null &&
           SelectedRemote is not null &&
           !string.IsNullOrWhiteSpace(ConflictCopyId) &&
           !_workspace.LocalDocuments.Any(item =>
               string.Equals(item.GeometryId, ConflictCopyId.Trim(), StringComparison.Ordinal));

    private void RecordRemoteOperationResult(
        GeometryRemoteOperationKind operation,
        string geometryId,
        GeometryCommandResult result)
    {
        var lines = new List<string>
        {
            $"Operation: {operation}",
            $"Geometry: {geometryId}",
            $"State: {result.State}",
            $"Accepted: {result.Accepted}",
            $"Operation ID: {result.OperationId ?? "Not reported"}",
            result.Message
        };
        if (result.Validation is { } validation)
        {
            lines.Add(string.Empty);
            lines.Add($"Validation: {validation.State} · {validation.Summary}");
            lines.AddRange(validation.Issues.Select(item =>
                $"[{item.Severity}] {item.Code} · {item.Source}: {item.Message}"));
        }

        LastRemoteOperationDetails = string.Join(Environment.NewLine, lines);
    }

    private void ClearRemoteAssessment()
    {
        RemoteAssessment = null;
        _assessRemoteCommand.RaiseCanExecuteChanged();
    }
}
