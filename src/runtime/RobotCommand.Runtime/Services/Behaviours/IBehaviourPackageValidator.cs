using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

public interface IBehaviourPackageValidator
{
    Task<BehaviourPackageValidationResult> ValidateAsync(
        BehaviourPackageValidationContext package,
        CancellationToken cancellationToken = default);
}
