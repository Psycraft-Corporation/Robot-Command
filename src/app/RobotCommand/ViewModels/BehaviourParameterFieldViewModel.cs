using System.Globalization;
using System.Text.Json.Nodes;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;

namespace RobotCommand.ViewModels;

public sealed class BehaviourParameterFieldViewModel : ObservableObject
{
    private string _textValue = string.Empty;
    private bool? _booleanValue;
    private BehaviourParameterOption? _selectedOptionItem;
    private string _validationMessage = string.Empty;
    private string _loadedValueError = string.Empty;
    private bool _isValid = true;
    private bool _suppressChanged;

    public BehaviourParameterFieldViewModel(BehaviourParameterDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ResetToDefault(notify: false);
    }

    public event EventHandler? Changed;

    public BehaviourParameterDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Label => Definition.Label;

    public string Description => Definition.Description;

    public string TypeLabel => Definition.TypeLabel;

    public string RequirementLabel => Definition.RequirementLabel;

    public string ConstraintSummary => Definition.ConstraintSummary;

    public string Unit => Definition.Unit;

    public IReadOnlyList<BehaviourParameterOption> Options => Definition.Options;

    public bool IsText => Definition.Kind is
        BehaviourParameterKind.String or
        BehaviourParameterKind.GeometryReference;

    public bool IsMultiline => Definition.Kind == BehaviourParameterKind.Multiline;

    public bool IsNumeric => Definition.Kind is
        BehaviourParameterKind.Integer or
        BehaviourParameterKind.Decimal or
        BehaviourParameterKind.DurationSeconds or
        BehaviourParameterKind.DistanceMetres or
        BehaviourParameterKind.HeadingDegrees or
        BehaviourParameterKind.AltitudeMetres;

    public bool IsBoolean => Definition.Kind == BehaviourParameterKind.Boolean;

    public bool IsEnum => Definition.Kind == BehaviourParameterKind.Enum;

    public string TextValue
    {
        get => _textValue;
        set
        {
            var hadLoadedError = !string.IsNullOrWhiteSpace(_loadedValueError);
            var changed = SetProperty(ref _textValue, value ?? string.Empty);
            if (changed || hadLoadedError)
            {
                _loadedValueError = string.Empty;
                ValidateAndNotify();
            }
        }
    }

    public bool? BooleanValue
    {
        get => _booleanValue;
        set
        {
            var hadLoadedError = !string.IsNullOrWhiteSpace(_loadedValueError);
            var changed = SetProperty(ref _booleanValue, value);
            if (changed || hadLoadedError)
            {
                _loadedValueError = string.Empty;
                ValidateAndNotify();
            }
        }
    }

    public BehaviourParameterOption? SelectedOptionItem
    {
        get => _selectedOptionItem;
        set
        {
            var hadLoadedError = !string.IsNullOrWhiteSpace(_loadedValueError);
            var changed = SetProperty(ref _selectedOptionItem, value);
            if (changed || hadLoadedError)
            {
                _loadedValueError = string.Empty;
                ValidateAndNotify();
            }
        }
    }

    public string? SelectedOption => SelectedOptionItem?.Value;

    public bool IsValid
    {
        get => _isValid;
        private set => SetProperty(ref _isValid, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public void ResetToDefault(bool notify = true)
    {
        _suppressChanged = true;
        try
        {
            _loadedValueError = string.Empty;
            if (IsBoolean)
            {
                _booleanValue = bool.TryParse(Definition.DefaultValue, out var boolean)
                    ? boolean
                    : null;
                OnPropertyChanged(nameof(BooleanValue));
            }
            else if (IsEnum)
            {
                _selectedOptionItem = Definition.DefaultValue is not null
                    ? Options.FirstOrDefault(item => string.Equals(item.Value, Definition.DefaultValue, StringComparison.Ordinal))
                    : null;
                OnPropertyChanged(nameof(SelectedOptionItem));
                OnPropertyChanged(nameof(SelectedOption));
            }
            else
            {
                _textValue = Definition.DefaultValue ?? string.Empty;
                OnPropertyChanged(nameof(TextValue));
            }
            Validate();
        }
        finally
        {
            _suppressChanged = false;
        }
        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Load(JsonNode? node)
    {
        _suppressChanged = true;
        try
        {
            _loadedValueError = string.Empty;
            _textValue = string.Empty;
            _booleanValue = null;
            _selectedOptionItem = null;

            if (node is null)
            {
                // A missing or null property is represented by the declared default.
                ResetToDefault(notify: false);
            }
            else if (IsBoolean)
            {
                if (node is JsonValue booleanValue && booleanValue.TryGetValue<bool>(out var boolean))
                {
                    _booleanValue = boolean;
                }
                else
                {
                    _loadedValueError = $"{Label} has a non-boolean JSON value. Use raw JSON or correct the value.";
                }
            }
            else if (IsEnum)
            {
                if (node is JsonValue enumValue && enumValue.TryGetValue<string>(out var option))
                {
                    _selectedOptionItem = Options.FirstOrDefault(item =>
                        string.Equals(item.Value, option, StringComparison.Ordinal));
                    if (_selectedOptionItem is null)
                    {
                        _loadedValueError = $"{Label} is not one of the declared options.";
                    }
                }
                else
                {
                    _loadedValueError = $"{Label} has a non-string JSON value. Use raw JSON or correct the value.";
                }
            }
            else if (IsNumeric)
            {
                var numberText = node is JsonValue ? node.ToJsonString() : string.Empty;
                if (decimal.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    _textValue = number.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    _loadedValueError = $"{Label} has a non-numeric JSON value. Use raw JSON or correct the value.";
                }
            }
            else if (node is JsonValue textValue && textValue.TryGetValue<string>(out var text))
            {
                _textValue = text;
            }
            else
            {
                _loadedValueError = $"{Label} has a non-string JSON value. Use raw JSON or correct the value.";
            }

            OnPropertyChanged(nameof(TextValue));
            OnPropertyChanged(nameof(BooleanValue));
            OnPropertyChanged(nameof(SelectedOptionItem));
            OnPropertyChanged(nameof(SelectedOption));
            Validate();
        }
        finally
        {
            _suppressChanged = false;
        }
    }

    public bool TryGetValue(out JsonNode? value, out string error)
    {
        if (!string.IsNullOrWhiteSpace(_loadedValueError))
        {
            value = null;
            error = _loadedValueError;
            return false;
        }

        var text = IsBoolean
            ? BooleanValue?.ToString()
            : IsEnum
                ? SelectedOption
                : TextValue;
        return BehaviourParameterValueCodec.TryConvert(Definition, text, out value, out error);
    }

    private void ValidateAndNotify()
    {
        Validate();
        if (!_suppressChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Validate()
    {
        IsValid = TryGetValue(out _, out var error);
        ValidationMessage = error;
    }
}
