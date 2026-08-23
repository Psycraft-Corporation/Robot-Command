namespace RobotCommand.Models;

public enum BehaviourParameterSchemaState
{
    NotDeclared,
    Valid,
    Invalid,
    Unavailable
}

public enum BehaviourParameterKind
{
    String,
    Multiline,
    Integer,
    Decimal,
    Boolean,
    Enum,
    DurationSeconds,
    GeometryReference,
    DistanceMetres,
    HeadingDegrees,
    AltitudeMetres
}

public sealed record BehaviourParameterOption(
    string Value,
    string Label);

public sealed record BehaviourParameterDefinition(
    string Id,
    string Label,
    string Description,
    BehaviourParameterKind Kind,
    bool Required,
    string? DefaultValue,
    decimal? Minimum,
    decimal? Maximum,
    string Unit,
    IReadOnlyList<BehaviourParameterOption> Options,
    bool Advanced = false)
{
    public string TypeLabel => Kind switch
    {
        BehaviourParameterKind.Multiline => "Multiline text",
        BehaviourParameterKind.Integer => "Integer",
        BehaviourParameterKind.Decimal => "Number",
        BehaviourParameterKind.Boolean => "Boolean",
        BehaviourParameterKind.Enum => "Choice",
        BehaviourParameterKind.DurationSeconds => "Duration",
        BehaviourParameterKind.GeometryReference => "Geometry reference",
        BehaviourParameterKind.DistanceMetres => "Distance",
        BehaviourParameterKind.HeadingDegrees => "Heading",
        BehaviourParameterKind.AltitudeMetres => "Altitude",
        _ => "Text"
    };

    public string RequirementLabel => Required ? "Required" : "Optional";

    public string DefaultSummary => DefaultValue is null ? "No default" : $"Default: {DefaultValue}";

    public string ConstraintSummary
    {
        get
        {
            var parts = new List<string>();
            if (Minimum is not null) parts.Add($">= {Minimum:0.########}");
            if (Maximum is not null) parts.Add($"<= {Maximum:0.########}");
            if (!string.IsNullOrWhiteSpace(Unit)) parts.Add(Unit);
            if (Options.Count > 0) parts.Add($"{Options.Count} option(s)");
            return parts.Count == 0 ? "No additional constraints" : string.Join(" · ", parts);
        }
    }
}

public sealed record BehaviourParameterSchemaFinding(
    string Code,
    BehaviourPackageFindingSeverity Severity,
    string Message,
    string? Field = null);

public sealed record BehaviourParameterSchema(
    BehaviourPackageIdentity Identity,
    BehaviourParameterSchemaState State,
    string Summary,
    IReadOnlyList<BehaviourParameterDefinition> Parameters,
    IReadOnlyList<BehaviourParameterSchemaFinding> Findings,
    string Source)
{
    public bool Declared => State != BehaviourParameterSchemaState.NotDeclared;

    public bool Usable => State == BehaviourParameterSchemaState.Valid;

    public static BehaviourParameterSchema NotDeclared(BehaviourPackageIdentity identity) => new(
        identity,
        BehaviourParameterSchemaState.NotDeclared,
        "The package does not declare an operator parameter schema. Use raw JSON.",
        [],
        [],
        "manifest");

    public static BehaviourParameterSchema Unavailable(
        BehaviourPackageIdentity identity,
        string summary) => new(
        identity,
        BehaviourParameterSchemaState.Unavailable,
        summary,
        [],
        [new BehaviourParameterSchemaFinding(
            "BEHAVIOUR_PARAMETER_SCHEMA_UNAVAILABLE",
            BehaviourPackageFindingSeverity.Warning,
            summary)],
        "manifest");
}

public sealed record BehaviourParameterValidationResult(
    bool Valid,
    string Summary,
    IReadOnlyList<string> Issues,
    string CanonicalJson)
{
    public static BehaviourParameterValidationResult Invalid(params string[] issues) => new(
        false,
        issues.Length == 0 ? "Parameters are invalid." : issues[0],
        issues,
        "{}");
}
