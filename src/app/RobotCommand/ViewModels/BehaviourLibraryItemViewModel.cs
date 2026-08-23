using RobotCommand.Models;

namespace RobotCommand.ViewModels;

public sealed class BehaviourLibraryItemViewModel
{
    public BehaviourLibraryItemViewModel(BehaviourWorkspaceEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public BehaviourWorkspaceEntry Entry { get; }

    public BehaviourPackageIdentity Identity => Entry.Identity;

    public string Key => Entry.Key;

    public string DisplayName => Entry.DisplayName;

    public string Description => Entry.Description;

    public string Version => Identity.Version ?? "Unversioned";

    public string IdentityLabel => Identity.Key;

    public bool HasLocalPackage => Entry.HasLocalPackage;

    public bool HasRemotePackage => Entry.HasRemotePackage;

    public bool Active => Entry.Remote?.Active == true;

    public bool InUse => Entry.Remote?.InUse == true;

    public string SourceSummary => (HasLocalPackage, HasRemotePackage) switch
    {
        (true, true) => "Local + Logos",
        (true, false) => "Local only",
        (false, true) => "Logos only",
        _ => "Unavailable"
    };

    public string DeploymentState => Entry.Deployment.Status.ToString();

    public string DeploymentSummary => Entry.Deployment.Summary;

    public string LocalState => Entry.Local?.State.ToString() ?? "Not in local library";

    public string LocalIntegrity => Entry.Local?.Integrity.Summary ?? "No local package";

    public string LogosValidation
    {
        get
        {
            var local = Entry.Local?.LogosValidation;
            if (local is not null && local.State != BehaviourPackageValidationState.NotValidated)
            {
                return local.Summary;
            }

            return Entry.Remote?.LogosValidation?.Summary
                   ?? local?.Summary
                   ?? "Not validated by Logos";
        }
    }

    public string RemoteState => Entry.Remote is null
        ? "Not installed"
        : string.IsNullOrWhiteSpace(Entry.Remote.Status)
            ? "Installed"
            : Entry.Remote.Status;

    public string CapabilitySummary
    {
        get
        {
            var required = Entry.Local?.RequiredCapabilityKeys
                           ?? Entry.Remote?.RequiredCapabilities
                           ?? [];
            return required.Count == 0
                ? "No package-declared capability requirements"
                : $"Requires: {string.Join(", ", required)}";
        }
    }

    public string GeometrySummary
    {
        get
        {
            var slots = Entry.Local?.GeometryRequirements
                        ?? Entry.Remote?.GeometrySlots
                        ?? [];
            if (slots.Count == 0)
            {
                return "No geometry slots declared";
            }

            var required = slots.Count(item => item.RequiresResolvedGeometry);
            return $"{slots.Count} geometry slot(s), {required} required";
        }
    }

    public string HashSummary
    {
        get
        {
            var local = ShortHash(Entry.Local?.ContentSha256);
            var remote = ShortHash(Entry.Remote?.ContentSha256);
            var baseline = ShortHash(Entry.Local?.RemoteBaselineSha256);
            return $"Local {local} · Logos {remote} · Baseline {baseline}";
        }
    }

    private static string ShortHash(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().Length <= 12
                ? value.Trim()
                : value.Trim()[..12];
}
