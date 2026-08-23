using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Geometry;

namespace RobotCommand.ViewModels;

public sealed partial class GeometryWorkspaceViewModel
{
    private readonly string[] _policyPresets =
    [
        "None",
        "Inclusion",
        "Exclusion",
        "Avoid",
        "No land",
        "No capture"
    ];

    private readonly string[] _policyDecisions = ["None", "Deny", "Warn", "Log only"];
    private AsyncRelayCommand _saveLocalEditsCommand = null!;
    private RelayCommand _applyVertexCommand = null!;
    private RelayCommand _addVertexCommand = null!;
    private RelayCommand _insertVertexCommand = null!;
    private RelayCommand _removeVertexCommand = null!;
    private RelayCommand _moveVertexUpCommand = null!;
    private RelayCommand _moveVertexDownCommand = null!;
    private RelayCommand _clearPolicyCommand = null!;
    private GeometryDocument? _authoringDocument;
    private string? _authoringGeometryId;
    private bool _authoringDirty;
    private bool _suppressMapVertexSelection;
    private string _authoringDisplayName = string.Empty;
    private string _authoringDescription = string.Empty;
    private GeometryVertexRowViewModel? _selectedVertex;
    private string _vertexLongitudeText = string.Empty;
    private string _vertexLatitudeText = string.Empty;
    private string _vertexAltitudeText = "0";
    private string _selectedPolicyPreset = "None";
    private string _selectedPolicyDecision = "None";
    private string _policyOperationsText = string.Empty;
    private string _policyCode = string.Empty;
    private string _policyRecommendedAction = string.Empty;
    private string _policyMinimumAltitudeText = string.Empty;
    private string _policyMaximumAltitudeText = string.Empty;
    private string _policyTagsText = string.Empty;

    public ObservableCollection<GeometryVertexRowViewModel> Vertices { get; } = [];

    public IReadOnlyList<string> PolicyPresets => _policyPresets;

    public IReadOnlyList<string> PolicyDecisions => _policyDecisions;

    public GeometryDocument? AuthoringDocument => _authoringDocument;

    public GeometryDocument? AuthoringPreview
        => TryBuildAuthoringDocument(out var document, out _) ? NormalizeForValidation(document!) : null;

    public bool AuthoringAvailable => _authoringDocument is not null;

    public bool AuthoringEditable => _authoringDocument is not null && IsAuthorableShape(_authoringDocument);

    public string AuthoringReadOnlyReason
        => _authoringDocument is null
            ? "Select local geometry to author it."
            : _authoringDocument.Frame != GeometryCoordinateFrame.GlobalWgs84
                ? "Connection-scoped ENU/NED geometry remains read-only in this editor."
                : _authoringDocument.Kind == GeometryDocumentKind.Zone && _authoringDocument.Rings.Count > 1
                    ? "Zones with holes are preserved but remain read-only until hole authoring is implemented."
                    : string.Empty;

    public bool HasAuthoringReadOnlyReason => !string.IsNullOrWhiteSpace(AuthoringReadOnlyReason);

    public bool AuthoringDirty => _authoringDirty;

    public bool IsPointOfInterest => _authoringDocument?.Kind == GeometryDocumentKind.PointOfInterest;

    public bool IsWaypointSequence => _authoringDocument?.Kind == GeometryDocumentKind.WaypointSequence;

    public bool IsZone => _authoringDocument?.Kind == GeometryDocumentKind.Zone;

    public bool SupportsVertexInsertion => _authoringDocument?.Kind is
        GeometryDocumentKind.WaypointSequence or GeometryDocumentKind.Zone;

    public string AuthoringGeometryId => _authoringDocument?.GeometryId ?? string.Empty;

    public string AuthoringKind => _authoringDocument?.Kind.ToString() ?? "None";

    public string AuthoringFrame => _authoringDocument?.Frame.ToString() ?? "None";

    public string AuthoringDisplayName
    {
        get => _authoringDisplayName;
        set
        {
            if (SetProperty(ref _authoringDisplayName, value ?? string.Empty))
            {
                MarkAuthoringDirty();
            }
        }
    }

    public string AuthoringDescription
    {
        get => _authoringDescription;
        set
        {
            if (SetProperty(ref _authoringDescription, value ?? string.Empty))
            {
                MarkAuthoringDirty();
            }
        }
    }

    public GeometryVertexRowViewModel? SelectedVertex
    {
        get => _selectedVertex;
        set
        {
            if (!SetProperty(ref _selectedVertex, value))
            {
                return;
            }

            if (value is not null)
            {
                _vertexLongitudeText = FormatCoordinate(value.Point.X);
                _vertexLatitudeText = FormatCoordinate(value.Point.Y);
                _vertexAltitudeText = FormatAltitude(value.Point.Z);
                OnPropertyChanged(nameof(VertexLongitudeText));
                OnPropertyChanged(nameof(VertexLatitudeText));
                OnPropertyChanged(nameof(VertexAltitudeText));
            }

            if (!_suppressMapVertexSelection &&
                MapEdit.IsEditing &&
                string.Equals(MapEdit.Draft?.GeometryId, AuthoringGeometryId, StringComparison.Ordinal) &&
                MapEdit.SelectedVertexIndex != value?.Index)
            {
                _editSession.SelectVertex(value?.Index);
            }

            RaiseAuthoringCommandStates();
        }
    }

    public string VertexLongitudeText
    {
        get => _vertexLongitudeText;
        set
        {
            if (SetProperty(ref _vertexLongitudeText, value ?? string.Empty))
            {
                RaiseAuthoringCommandStates();
            }
        }
    }

    public string VertexLatitudeText
    {
        get => _vertexLatitudeText;
        set
        {
            if (SetProperty(ref _vertexLatitudeText, value ?? string.Empty))
            {
                RaiseAuthoringCommandStates();
            }
        }
    }

    public string VertexAltitudeText
    {
        get => _vertexAltitudeText;
        set
        {
            if (SetProperty(ref _vertexAltitudeText, value ?? string.Empty))
            {
                RaiseAuthoringCommandStates();
            }
        }
    }

    public string SelectedPolicyPreset
    {
        get => _selectedPolicyPreset;
        set
        {
            if (SetProperty(ref _selectedPolicyPreset, value ?? "None"))
            {
                MarkAuthoringDirty();
            }
        }
    }

    public string SelectedPolicyDecision
    {
        get => _selectedPolicyDecision;
        set
        {
            if (SetProperty(ref _selectedPolicyDecision, value ?? "None"))
            {
                MarkAuthoringDirty();
            }
        }
    }

    public string PolicyOperationsText
    {
        get => _policyOperationsText;
        set
        {
            if (SetProperty(ref _policyOperationsText, value ?? string.Empty))
            {
                MarkAuthoringDirty();
            }
        }
    }

    public string PolicyCode
    {
        get => _policyCode;
        set
        {
            if (SetProperty(ref _policyCode, value ?? string.Empty))
            {
                MarkAuthoringDirty();
            }
        }
    }

    public string PolicyRecommendedAction
    {
        get => _policyRecommendedAction;
        set
        {
            if (SetProperty(ref _policyRecommendedAction, value ?? string.Empty))
            {
                MarkAuthoringDirty();
            }
        }
    }

    public string PolicyMinimumAltitudeText
    {
        get => _policyMinimumAltitudeText;
        set
        {
            if (SetProperty(ref _policyMinimumAltitudeText, value ?? string.Empty))
            {
                MarkAuthoringDirty();
            }
        }
    }

    public string PolicyMaximumAltitudeText
    {
        get => _policyMaximumAltitudeText;
        set
        {
            if (SetProperty(ref _policyMaximumAltitudeText, value ?? string.Empty))
            {
                MarkAuthoringDirty();
            }
        }
    }

    public string PolicyTagsText
    {
        get => _policyTagsText;
        set
        {
            if (SetProperty(ref _policyTagsText, value ?? string.Empty))
            {
                MarkAuthoringDirty();
            }
        }
    }

    public string AuthoringValidationDetails
    {
        get
        {
            if (!TryBuildAuthoringDocument(out var document, out var inputError))
            {
                return inputError;
            }

            var candidate = NormalizeForValidation(document!);
            var validation = _validator.Validate(candidate, requireAuthorableFrame: true);
            return validation.Issues.Count == 0
                ? validation.Summary
                : $"{validation.Summary}{Environment.NewLine}{string.Join(Environment.NewLine, validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}"))}";
        }
    }

    public string VertexRequirement => _authoringDocument?.Kind switch
    {
        GeometryDocumentKind.PointOfInterest => "Exactly one point is required.",
        GeometryDocumentKind.WaypointSequence => "At least two ordered waypoints are required.",
        GeometryDocumentKind.Zone => "At least three non-intersecting vertices are required. The saved ring closes automatically.",
        _ => "Select local geometry to edit coordinates."
    };

    public ICommand SaveLocalEditsCommand { get; private set; } = null!;

    public ICommand ApplyVertexCommand { get; private set; } = null!;

    public ICommand AddVertexCommand { get; private set; } = null!;

    public ICommand InsertVertexCommand { get; private set; } = null!;

    public ICommand RemoveVertexCommand { get; private set; } = null!;

    public ICommand MoveVertexUpCommand { get; private set; } = null!;

    public ICommand MoveVertexDownCommand { get; private set; } = null!;

    public ICommand ClearPolicyCommand { get; private set; } = null!;

    private void InitializeAuthoring()
    {
        _saveLocalEditsCommand = new AsyncRelayCommand(SaveLocalEditsAsync, CanSaveLocalEdits);
        _applyVertexCommand = new RelayCommand(_ => ApplySelectedVertex(), _ => CanApplySelectedVertex());
        _addVertexCommand = new RelayCommand(_ => AddVertexFromFields(), _ => CanAddVertex());
        _insertVertexCommand = new RelayCommand(_ => InsertVertexFromFields(), _ => CanInsertVertex());
        _removeVertexCommand = new RelayCommand(_ => RemoveSelectedVertex(), _ => CanRemoveVertex());
        _moveVertexUpCommand = new RelayCommand(_ => MoveSelectedVertex(-1), _ => CanMoveVertex(-1));
        _moveVertexDownCommand = new RelayCommand(_ => MoveSelectedVertex(1), _ => CanMoveVertex(1));
        _clearPolicyCommand = new RelayCommand(_ => ClearPolicy(), _ => AuthoringEditable);
        SaveLocalEditsCommand = _saveLocalEditsCommand;
        ApplyVertexCommand = _applyVertexCommand;
        AddVertexCommand = _addVertexCommand;
        InsertVertexCommand = _insertVertexCommand;
        RemoveVertexCommand = _removeVertexCommand;
        MoveVertexUpCommand = _moveVertexUpCommand;
        MoveVertexDownCommand = _moveVertexDownCommand;
        ClearPolicyCommand = _clearPolicyCommand;
    }

    private void LoadAuthoringSelection(GeometryDocument? document, bool force = false)
    {
        if (document is null)
        {
            _authoringDocument = null;
            _authoringGeometryId = null;
            _authoringDirty = false;
            _authoringDisplayName = string.Empty;
            _authoringDescription = string.Empty;
            LoadPolicy(GeometryPolicyAnnotation.None);
            Vertices.Clear();
            _selectedVertex = null;
            RaiseAuthoringProperties();
            return;
        }

        var sameDocument = string.Equals(_authoringGeometryId, document.GeometryId, StringComparison.Ordinal);
        var mapOwnsShape = MapEdit.HasDraft &&
                           string.Equals(MapEdit.Draft?.GeometryId, document.GeometryId, StringComparison.Ordinal);
        if (!force && sameDocument && (_authoringDirty || mapOwnsShape))
        {
            return;
        }

        _authoringDocument = document with { IsDirty = false };
        _authoringGeometryId = document.GeometryId;
        _authoringDirty = false;
        _authoringDisplayName = document.DisplayName;
        _authoringDescription = document.Description;
        LoadPolicy(document.Policy);
        RebuildVertices(MapEdit.SelectedVertexIndex);
        RaiseAuthoringProperties();
    }

    private void RefreshAuthoringAfterPresentation()
    {
        LoadAuthoringSelection(SelectedLocal?.Document);
    }

    private void SynchronizeAuthoringFromMapEdit()
    {
        var mapDraft = MapEdit.Draft;
        if (mapDraft is null || _authoringDocument is null ||
            !string.Equals(mapDraft.GeometryId, _authoringDocument.GeometryId, StringComparison.Ordinal))
        {
            return;
        }

        _authoringDocument = CopyShape(_authoringDocument, mapDraft) with { IsDirty = true };
        _authoringDirty = true;
        RebuildVertices(MapEdit.SelectedVertexIndex);
        OnPropertyChanged(nameof(AuthoringValidationDetails));
        OnPropertyChanged(nameof(AuthoringPreview));
        OnPropertyChanged(nameof(AuthoringDirty));
        RaiseAuthoringCommandStates();
    }

    private async Task SaveLocalEditsAsync(CancellationToken cancellationToken)
    {
        if (!TryBuildAuthoringDocument(out var document, out var inputError))
        {
            StatusMessage = inputError;
            return;
        }

        try
        {
            var candidate = NormalizeForValidation(document!);
            var validation = _validator.Validate(candidate, requireAuthorableFrame: true);
            var saved = await _workspace.SaveLocalAsync(candidate, cancellationToken);
            _selectedLocalId = saved.GeometryId;
            _authoringDirty = false;
            _authoringDocument = saved;
            RefreshPresentation();
            StatusMessage = validation.IsValid
                ? $"Saved local geometry '{saved.DisplayName}'."
                : $"Saved local draft '{saved.DisplayName}' with {validation.Issues.Count(issue => issue.Severity == GeometryValidationSeverity.Error)} structural error(s).";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not save local geometry edits: {ex.Message}";
        }
    }

    private bool CanSaveLocalEdits()
        => AuthoringEditable && !MapEdit.HasDraft;

    private GeometryDocument MergeAuthoringWithMapDraft(GeometryDocument mapDraft)
    {
        if (!TryBuildAuthoringDocument(out var authored, out var inputError))
        {
            throw new InvalidOperationException(inputError);
        }

        return CopyShape(authored!, mapDraft);
    }

    private GeometryDocument CurrentAuthoringDocumentRequired()
    {
        if (!TryBuildAuthoringDocument(out var document, out var inputError))
        {
            throw new InvalidOperationException(inputError);
        }

        return document!;
    }

    private bool TryBuildAuthoringDocument(
        out GeometryDocument? document,
        out string error)
    {
        document = null;
        error = string.Empty;
        if (_authoringDocument is null)
        {
            error = "Select local geometry before editing it.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AuthoringDisplayName))
        {
            error = "A display name is required.";
            return false;
        }

        if (!TryParseOptionalNumber(PolicyMinimumAltitudeText, "minimum policy altitude", out var minimum, out error) ||
            !TryParseOptionalNumber(PolicyMaximumAltitudeText, "maximum policy altitude", out var maximum, out error))
        {
            return false;
        }

        var (kind, constraint) = PolicyPresetValues(SelectedPolicyPreset);
        document = _authoringDocument with
        {
            DisplayName = AuthoringDisplayName.Trim(),
            Description = AuthoringDescription.Trim(),
            Policy = new GeometryPolicyAnnotation
            {
                Kind = kind,
                Constraint = constraint,
                Operations = SplitValues(PolicyOperationsText),
                Decision = PolicyDecisionValue(SelectedPolicyDecision),
                Code = PolicyCode.Trim(),
                RecommendedAction = string.IsNullOrWhiteSpace(PolicyRecommendedAction)
                    ? "none"
                    : PolicyRecommendedAction.Trim(),
                MinimumAltitudeMetres = minimum,
                MaximumAltitudeMetres = maximum,
                Tags = SplitValues(PolicyTagsText),
                Attributes = _authoringDocument.Policy.Attributes
            },
            UpdatedAt = DateTimeOffset.UtcNow,
            ContentSha256 = string.Empty,
            IsDirty = true
        };
        return true;
    }

    private void ApplySelectedVertex()
    {
        string error = string.Empty;
        if (SelectedVertex is null || !TryReadVertex(out var point, out error))
        {
            StatusMessage = SelectedVertex is null ? "Select a vertex to update." : error;
            return;
        }

        try
        {
            MutateVertexShape(
                session => session.MoveVertex(SelectedVertex.Index, point),
                vertices => vertices[SelectedVertex.Index] = point,
                SelectedVertex.Index);
            StatusMessage = $"Updated vertex {SelectedVertex.Number}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not update the vertex: {ex.Message}";
        }
    }

    private void AddVertexFromFields()
    {
        if (!TryReadVertex(out var point, out var error))
        {
            StatusMessage = error;
            return;
        }

        try
        {
            var existing = GeometryEditShape.GetVertices(_authoringDocument).Count;
            if (IsPointOfInterest && existing == 1)
            {
                MutateVertexShape(
                    session => session.MoveVertex(0, point),
                    vertices => vertices[0] = point,
                    0);
                StatusMessage = "Moved the point of interest to the entered coordinates.";
                return;
            }

            MutateVertexShape(
                session => session.AddVertex(point),
                vertices => vertices.Add(point),
                existing);
            StatusMessage = IsPointOfInterest ? "Placed the point of interest." : "Appended a geometry vertex.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not add the vertex: {ex.Message}";
        }
    }

    private void InsertVertexFromFields()
    {
        string error = string.Empty;
        if (SelectedVertex is null || !TryReadVertex(out var point, out error))
        {
            StatusMessage = SelectedVertex is null ? "Select a vertex before inserting another point." : error;
            return;
        }

        var index = SelectedVertex.Index;
        try
        {
            MutateVertexShape(
                session => session.InsertVertex(index, point),
                vertices => vertices.Insert(index, point),
                index);
            StatusMessage = $"Inserted a vertex before position {index + 1}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not insert the vertex: {ex.Message}";
        }
    }

    private void RemoveSelectedVertex()
    {
        if (SelectedVertex is null)
        {
            return;
        }

        var index = SelectedVertex.Index;
        try
        {
            MutateVertexShape(
                session => session.RemoveVertex(index),
                vertices => vertices.RemoveAt(index),
                Math.Max(0, index - 1));
            StatusMessage = $"Removed vertex {index + 1}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not remove the vertex: {ex.Message}";
        }
    }

    private void MoveSelectedVertex(int offset)
    {
        if (SelectedVertex is null)
        {
            return;
        }

        var from = SelectedVertex.Index;
        var to = from + offset;
        try
        {
            MutateVertexShape(
                session => session.ReorderVertex(from, to),
                vertices =>
                {
                    var point = vertices[from];
                    vertices.RemoveAt(from);
                    vertices.Insert(to, point);
                },
                to);
            StatusMessage = $"Moved vertex {from + 1} to position {to + 1}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not reorder the vertex: {ex.Message}";
        }
    }

    private void MutateVertexShape(
        Action<IGeometryEditSession> sessionMutation,
        Action<List<GeometryDocumentPoint>> localMutation,
        int? selectedIndex)
    {
        if (_authoringDocument is null)
        {
            throw new InvalidOperationException("Select local geometry first.");
        }
        GeometryEditorState.EnsureAuthorable(_authoringDocument);

        if (MapEdit.IsCompleted)
        {
            throw new InvalidOperationException("Save or discard the completed map draft before changing coordinates.");
        }

        if (MapEdit.IsEditing &&
            string.Equals(MapEdit.Draft?.GeometryId, _authoringDocument.GeometryId, StringComparison.Ordinal))
        {
            sessionMutation(_editSession);
            return;
        }

        var vertices = GeometryEditShape.GetVertices(_authoringDocument).ToList();
        localMutation(vertices);
        _authoringDocument = GeometryEditShape.WithVertices(_authoringDocument, vertices);
        _authoringDirty = true;
        RebuildVertices(selectedIndex);
        OnPropertyChanged(nameof(AuthoringValidationDetails));
        OnPropertyChanged(nameof(AuthoringPreview));
        OnPropertyChanged(nameof(AuthoringDirty));
        RaiseAuthoringCommandStates();
    }

    private bool TryReadVertex(out GeometryDocumentPoint point, out string error)
    {
        point = default;
        if (!TryParseRequiredNumber(VertexLongitudeText, "longitude", out var longitude, out error) ||
            !TryParseRequiredNumber(VertexLatitudeText, "latitude", out var latitude, out error) ||
            !TryParseRequiredNumber(VertexAltitudeText, "altitude", out var altitude, out error))
        {
            return false;
        }

        if (longitude is < -180 or > 180)
        {
            error = "Longitude must be between -180 and 180 degrees.";
            return false;
        }
        if (latitude is < -90 or > 90)
        {
            error = "Latitude must be between -90 and 90 degrees.";
            return false;
        }

        point = GeometryDocumentPoint.GlobalWgs84(longitude, latitude, altitude);
        return true;
    }

    private bool CanApplySelectedVertex()
        => AuthoringEditable && SelectedVertex is not null && !MapEdit.IsCompleted;

    private bool CanAddVertex()
        => AuthoringEditable &&
           !MapEdit.IsCompleted &&
           (!IsPointOfInterest || Vertices.Count <= 1);

    private bool CanInsertVertex()
        => AuthoringEditable && SupportsVertexInsertion && SelectedVertex is not null && !MapEdit.IsCompleted;

    private bool CanRemoveVertex()
        => AuthoringEditable && SelectedVertex is not null && !MapEdit.IsCompleted;

    private bool CanMoveVertex(int offset)
        => AuthoringEditable &&
           SupportsVertexInsertion &&
           SelectedVertex is not null &&
           !MapEdit.IsCompleted &&
           SelectedVertex.Index + offset >= 0 &&
           SelectedVertex.Index + offset < Vertices.Count;

    private void ClearPolicy()
    {
        _selectedPolicyPreset = "None";
        _selectedPolicyDecision = "None";
        _policyOperationsText = string.Empty;
        _policyCode = string.Empty;
        _policyRecommendedAction = string.Empty;
        _policyMinimumAltitudeText = string.Empty;
        _policyMaximumAltitudeText = string.Empty;
        _policyTagsText = string.Empty;
        MarkAuthoringDirty();
        OnPropertyChanged(nameof(SelectedPolicyPreset));
        OnPropertyChanged(nameof(SelectedPolicyDecision));
        OnPropertyChanged(nameof(PolicyOperationsText));
        OnPropertyChanged(nameof(PolicyCode));
        OnPropertyChanged(nameof(PolicyRecommendedAction));
        OnPropertyChanged(nameof(PolicyMinimumAltitudeText));
        OnPropertyChanged(nameof(PolicyMaximumAltitudeText));
        OnPropertyChanged(nameof(PolicyTagsText));
    }

    private void LoadPolicy(GeometryPolicyAnnotation policy)
    {
        _selectedPolicyPreset = PolicyPresetName(policy.Kind, policy.Constraint);
        _selectedPolicyDecision = PolicyDecisionName(policy.Decision);
        _policyOperationsText = string.Join(", ", policy.Operations);
        _policyCode = policy.Code;
        _policyRecommendedAction = string.Equals(policy.RecommendedAction, "none", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : policy.RecommendedAction;
        _policyMinimumAltitudeText = policy.MinimumAltitudeMetres is null
            ? string.Empty
            : FormatAltitude(policy.MinimumAltitudeMetres.Value);
        _policyMaximumAltitudeText = policy.MaximumAltitudeMetres is null
            ? string.Empty
            : FormatAltitude(policy.MaximumAltitudeMetres.Value);
        _policyTagsText = string.Join(", ", policy.Tags);
    }

    private void RebuildVertices(int? preferredIndex = null)
    {
        var vertices = GeometryEditShape.GetVertices(_authoringDocument);
        Vertices.Clear();
        for (var index = 0; index < vertices.Count; index++)
        {
            Vertices.Add(new GeometryVertexRowViewModel(index, vertices[index]));
        }

        var resolvedIndex = preferredIndex;
        if (resolvedIndex is null && _selectedVertex is not null)
        {
            resolvedIndex = _selectedVertex.Index;
        }
        if (resolvedIndex is not null && Vertices.Count > 0)
        {
            resolvedIndex = Math.Clamp(resolvedIndex.Value, 0, Vertices.Count - 1);
        }

        _suppressMapVertexSelection = true;
        try
        {
            SelectedVertex = resolvedIndex is null || Vertices.Count == 0
                ? null
                : Vertices[resolvedIndex.Value];
        }
        finally
        {
            _suppressMapVertexSelection = false;
        }

        OnPropertyChanged(nameof(VertexRequirement));
    }

    private void MarkAuthoringDirty()
    {
        if (_authoringDocument is null)
        {
            return;
        }

        _authoringDirty = true;
        OnPropertyChanged(nameof(AuthoringDirty));
        OnPropertyChanged(nameof(AuthoringValidationDetails));
        OnPropertyChanged(nameof(AuthoringPreview));
        RaiseAuthoringCommandStates();
    }

    private void RaiseAuthoringProperties()
    {
        OnPropertyChanged(nameof(AuthoringDocument));
        OnPropertyChanged(nameof(AuthoringPreview));
        OnPropertyChanged(nameof(AuthoringAvailable));
        OnPropertyChanged(nameof(AuthoringEditable));
        OnPropertyChanged(nameof(AuthoringReadOnlyReason));
        OnPropertyChanged(nameof(HasAuthoringReadOnlyReason));
        OnPropertyChanged(nameof(AuthoringDirty));
        OnPropertyChanged(nameof(IsPointOfInterest));
        OnPropertyChanged(nameof(IsWaypointSequence));
        OnPropertyChanged(nameof(IsZone));
        OnPropertyChanged(nameof(SupportsVertexInsertion));
        OnPropertyChanged(nameof(AuthoringGeometryId));
        OnPropertyChanged(nameof(AuthoringKind));
        OnPropertyChanged(nameof(AuthoringFrame));
        OnPropertyChanged(nameof(AuthoringDisplayName));
        OnPropertyChanged(nameof(AuthoringDescription));
        OnPropertyChanged(nameof(SelectedPolicyPreset));
        OnPropertyChanged(nameof(SelectedPolicyDecision));
        OnPropertyChanged(nameof(PolicyOperationsText));
        OnPropertyChanged(nameof(PolicyCode));
        OnPropertyChanged(nameof(PolicyRecommendedAction));
        OnPropertyChanged(nameof(PolicyMinimumAltitudeText));
        OnPropertyChanged(nameof(PolicyMaximumAltitudeText));
        OnPropertyChanged(nameof(PolicyTagsText));
        OnPropertyChanged(nameof(AuthoringValidationDetails));
        OnPropertyChanged(nameof(VertexRequirement));
        RaiseAuthoringCommandStates();
    }

    private void RaiseAuthoringCommandStates()
    {
        _saveLocalEditsCommand?.RaiseCanExecuteChanged();
        _applyVertexCommand?.RaiseCanExecuteChanged();
        _addVertexCommand?.RaiseCanExecuteChanged();
        _insertVertexCommand?.RaiseCanExecuteChanged();
        _removeVertexCommand?.RaiseCanExecuteChanged();
        _moveVertexUpCommand?.RaiseCanExecuteChanged();
        _moveVertexDownCommand?.RaiseCanExecuteChanged();
        _clearPolicyCommand?.RaiseCanExecuteChanged();
        _createRemoteCommand?.RaiseCanExecuteChanged();
        _updateRemoteCommand?.RaiseCanExecuteChanged();
        _assessRemoteCommand?.RaiseCanExecuteChanged();
    }

    private static GeometryDocument CopyShape(GeometryDocument target, GeometryDocument source)
        => target with
        {
            Points = source.Points.ToArray(),
            Rings = source.Rings
                .Select(ring => new GeometryDocumentRing { Points = ring.Points.ToArray() })
                .ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow,
            ContentSha256 = string.Empty,
            IsDirty = true
        };

    private static GeometryDocument NormalizeForValidation(GeometryDocument document)
        => !IsAuthorableShape(document) || GeometryEditShape.GetVertices(document).Count == 0
            ? document
            : GeometryEditShape.NormalizeCompleted(document);

    private static bool IsAuthorableShape(GeometryDocument document)
        => document.Frame == GeometryCoordinateFrame.GlobalWgs84 &&
           !(document.Kind == GeometryDocumentKind.Zone && document.Rings.Count > 1);

    private static bool TryParseRequiredNumber(
        string text,
        string fieldName,
        out double value,
        out string error)
    {
        if (TryParseNumber(text, out value))
        {
            error = string.Empty;
            return true;
        }

        error = $"Enter a valid finite {fieldName}.";
        return false;
    }

    private static bool TryParseOptionalNumber(
        string text,
        string fieldName,
        out double? value,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            error = string.Empty;
            return true;
        }

        if (TryParseNumber(text, out var parsed))
        {
            value = parsed;
            error = string.Empty;
            return true;
        }

        value = null;
        error = $"Enter a valid finite {fieldName}, or leave it empty.";
        return false;
    }

    private static bool TryParseNumber(string text, out double value)
    {
        var parsed = double.TryParse(
                         text,
                         NumberStyles.Float,
                         CultureInfo.InvariantCulture,
                         out value) ||
                     double.TryParse(
                         text,
                         NumberStyles.Float,
                         CultureInfo.CurrentCulture,
                         out value);
        return parsed && double.IsFinite(value);
    }

    private static string[] SplitValues(string value)
        => value.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static (string Kind, string Constraint) PolicyPresetValues(string preset)
        => preset switch
        {
            "Inclusion" => ("operational", "inclusion"),
            "Exclusion" => ("safety", "exclusion"),
            "Avoid" => ("safety", "avoid"),
            "No land" => ("safety", "no-land"),
            "No capture" => ("privacy", "no-capture"),
            _ => ("none", "none")
        };

    private static string PolicyPresetName(string kind, string constraint)
        => constraint.ToLowerInvariant() switch
        {
            "inclusion" => "Inclusion",
            "exclusion" => "Exclusion",
            "avoid" => "Avoid",
            "no-land" => "No land",
            "no-capture" => "No capture",
            _ when string.Equals(kind, "none", StringComparison.OrdinalIgnoreCase) => "None",
            _ => "None"
        };

    private static string PolicyDecisionValue(string decision)
        => decision switch
        {
            "Deny" => "deny",
            "Warn" => "warn",
            "Log only" => "log-only",
            _ => "none"
        };

    private static string PolicyDecisionName(string decision)
        => decision.ToLowerInvariant() switch
        {
            "deny" => "Deny",
            "warn" => "Warn",
            "log-only" or "log_only" => "Log only",
            _ => "None"
        };

    private static string FormatCoordinate(double value)
        => value.ToString("0.########", CultureInfo.InvariantCulture);

    private static string FormatAltitude(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);
}
