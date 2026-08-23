using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

public static class BehaviourParameterValueCodec
{
    public static string? ValidateDefault(BehaviourParameterDefinition definition)
    {
        if (definition.DefaultValue is null) return null;
        return TryConvert(definition, definition.DefaultValue, out _, out var error)
            ? null
            : $"Default value for parameter '{definition.Id}' is invalid: {error}";
    }

    public static bool TryConvert(
        BehaviourParameterDefinition definition,
        string? text,
        out JsonNode? value,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(definition);
        text ??= string.Empty;
        error = string.Empty;
        value = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            if (definition.Required)
            {
                error = $"{definition.Label} is required.";
                return false;
            }
            return true;
        }

        switch (definition.Kind)
        {
            case BehaviourParameterKind.Boolean:
                if (!bool.TryParse(text, out var boolean))
                {
                    error = $"{definition.Label} must be true or false.";
                    return false;
                }
                value = JsonValue.Create(boolean);
                return true;

            case BehaviourParameterKind.Integer:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    error = $"{definition.Label} must be an integer.";
                    return false;
                }
                if (!InRange(definition, integer, out error)) return false;
                value = JsonValue.Create(integer);
                return true;

            case BehaviourParameterKind.Decimal:
            case BehaviourParameterKind.DurationSeconds:
            case BehaviourParameterKind.DistanceMetres:
            case BehaviourParameterKind.HeadingDegrees:
            case BehaviourParameterKind.AltitudeMetres:
                if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    error = $"{definition.Label} must be a number.";
                    return false;
                }
                if (!InRange(definition, number, out error)) return false;
                value = JsonValue.Create(number);
                return true;

            case BehaviourParameterKind.Enum:
                if (!definition.Options.Any(item => string.Equals(item.Value, text, StringComparison.Ordinal)))
                {
                    error = $"{definition.Label} must be one of the declared options.";
                    return false;
                }
                value = JsonValue.Create(text);
                return true;

            default:
                value = JsonValue.Create(text);
                return true;
        }
    }

    public static BehaviourParameterValidationResult ValidateRawJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return BehaviourParameterValidationResult.Invalid("Enter a JSON object, for example {}.");
        }

        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject objectNode)
            {
                return BehaviourParameterValidationResult.Invalid("Behaviour parameters must be a JSON object.");
            }

            return new BehaviourParameterValidationResult(
                true,
                "Raw parameter JSON is valid.",
                [],
                objectNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException ex)
        {
            return BehaviourParameterValidationResult.Invalid(ex.Message);
        }
    }

    private static bool InRange(
        BehaviourParameterDefinition definition,
        decimal value,
        out string error)
    {
        error = string.Empty;
        if (definition.Minimum is not null && value < definition.Minimum)
        {
            error = $"{definition.Label} must be at least {definition.Minimum:0.########}.";
            return false;
        }
        if (definition.Maximum is not null && value > definition.Maximum)
        {
            error = $"{definition.Label} must be at most {definition.Maximum:0.########}.";
            return false;
        }
        return true;
    }
}
