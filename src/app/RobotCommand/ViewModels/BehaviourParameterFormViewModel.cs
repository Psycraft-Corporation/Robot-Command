using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;

namespace RobotCommand.ViewModels;

public sealed class BehaviourParameterFormViewModel : ObservableObject
{
    private BehaviourParameterSchema? _schema;
    private JsonObject _unmodelled = new();
    private string _rawJson = "{}";
    private string _validationSummary = "Raw parameter JSON is valid.";
    private IReadOnlyList<string> _issues = [];
    private bool _useRawJson = true;
    private bool _suppressChanged;

    public BehaviourParameterFormViewModel()
    {
        Fields = [];
    }

    public event EventHandler? Changed;

    public ObservableCollection<BehaviourParameterFieldViewModel> Fields { get; }

    public BehaviourParameterSchema? Schema
    {
        get => _schema;
        private set => SetProperty(ref _schema, value);
    }

    public bool HasDeclaredSchema => Schema?.Declared == true;

    public bool HasUsableSchema => Schema?.Usable == true;

    public bool ShowTypedFields => HasUsableSchema && !UseRawJson;

    public bool ShowRawEditor => !ShowTypedFields;

    public string SchemaSummary => Schema?.Summary ?? "No local parameter schema is available. Use raw JSON.";

    public string RawJson
    {
        get => _rawJson;
        set
        {
            if (SetProperty(ref _rawJson, string.IsNullOrWhiteSpace(value) ? string.Empty : value))
            {
                Validate();
                NotifyChanged();
            }
        }
    }

    public bool UseRawJson
    {
        get => _useRawJson;
        set
        {
            var effective = !HasUsableSchema || value;
            if (effective == _useRawJson)
            {
                return;
            }

            if (effective)
            {
                var typed = BuildTyped();
                if (typed.Valid)
                {
                    _rawJson = typed.CanonicalJson;
                    OnPropertyChanged(nameof(RawJson));
                }
            }
            else
            {
                var raw = BehaviourParameterValueCodec.ValidateRawJson(_rawJson);
                if (!raw.Valid)
                {
                    Validate();
                    OnPropertyChanged(nameof(UseRawJson));
                    return;
                }

                _rawJson = raw.CanonicalJson;
                OnPropertyChanged(nameof(RawJson));
                LoadTypedValues(_rawJson);
            }

            _useRawJson = effective;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowTypedFields));
            OnPropertyChanged(nameof(ShowRawEditor));
            Validate();
            NotifyChanged();
        }
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetProperty(ref _validationSummary, value);
    }

    public IReadOnlyList<string> Issues
    {
        get => _issues;
        private set
        {
            if (SetProperty(ref _issues, value))
            {
                OnPropertyChanged(nameof(HasIssues));
            }
        }
    }

    public bool HasIssues => Issues.Count > 0;

    public bool IsValid => Issues.Count == 0;

    public void Load(BehaviourParameterSchema? schema, string? existingJson = null)
    {
        _suppressChanged = true;
        try
        {
            foreach (var field in Fields)
            {
                field.Changed -= OnFieldChanged;
            }
            Fields.Clear();
            Schema = schema;
            _unmodelled = new JsonObject();

            foreach (var definition in schema?.Parameters ?? [])
            {
                var field = new BehaviourParameterFieldViewModel(definition);
                field.Changed += OnFieldChanged;
                Fields.Add(field);
            }

            _rawJson = string.IsNullOrWhiteSpace(existingJson) ? "{}" : existingJson;
            OnPropertyChanged(nameof(RawJson));
            var existing = BehaviourParameterValueCodec.ValidateRawJson(_rawJson);
            _useRawJson = schema?.Usable != true || !existing.Valid;
            OnPropertyChanged(nameof(UseRawJson));
            OnPropertyChanged(nameof(HasDeclaredSchema));
            OnPropertyChanged(nameof(HasUsableSchema));
            OnPropertyChanged(nameof(ShowTypedFields));
            OnPropertyChanged(nameof(ShowRawEditor));
            OnPropertyChanged(nameof(SchemaSummary));

            if (schema?.Usable == true && existing.Valid)
            {
                _rawJson = existing.CanonicalJson;
                OnPropertyChanged(nameof(RawJson));
                LoadTypedValues(_rawJson);
            }
            Validate();
        }
        finally
        {
            _suppressChanged = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        _suppressChanged = true;
        try
        {
            _unmodelled = new JsonObject();
            foreach (var field in Fields)
            {
                field.ResetToDefault(notify: false);
            }

            if (HasUsableSchema)
            {
                var defaults = BuildTyped();
                _rawJson = defaults.Valid ? defaults.CanonicalJson : "{}";
            }
            else
            {
                _rawJson = "{}";
            }
            OnPropertyChanged(nameof(RawJson));
            Validate();
        }
        finally
        {
            _suppressChanged = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public BehaviourParameterValidationResult Build()
        => ShowTypedFields
            ? BuildTyped()
            : BehaviourParameterValueCodec.ValidateRawJson(RawJson);

    private BehaviourParameterValidationResult BuildTyped()
    {
        var result = new JsonObject();
        foreach (var pair in _unmodelled)
        {
            result[pair.Key] = pair.Value?.DeepClone();
        }

        var issues = new List<string>();
        foreach (var field in Fields)
        {
            if (!field.TryGetValue(out var value, out var error))
            {
                issues.Add(error);
                continue;
            }

            if (value is null)
            {
                result.Remove(field.Id);
            }
            else
            {
                result[field.Id] = value;
            }
        }

        if (issues.Count > 0)
        {
            return new BehaviourParameterValidationResult(
                false,
                issues[0],
                issues,
                "{}");
        }

        return new BehaviourParameterValidationResult(
            true,
            "All declared behaviour parameters are valid.",
            [],
            result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LoadTypedValues(string json)
    {
        JsonObject parsed;
        try
        {
            parsed = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            parsed = new JsonObject();
        }

        var known = Fields.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        _unmodelled = new JsonObject();
        foreach (var pair in parsed.Where(pair => !known.Contains(pair.Key)))
        {
            _unmodelled[pair.Key] = pair.Value?.DeepClone();
        }

        foreach (var field in Fields)
        {
            field.ResetToDefault(notify: false);
            if (parsed.TryGetPropertyValue(field.Id, out var value))
            {
                field.Load(value);
            }
        }
    }

    private void OnFieldChanged(object? sender, EventArgs e)
    {
        Validate();
        NotifyChanged();
    }

    private void Validate()
    {
        var result = Build();
        Issues = result.Issues;
        ValidationSummary = result.Summary;
        OnPropertyChanged(nameof(IsValid));
    }

    private void NotifyChanged()
    {
        if (!_suppressChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
