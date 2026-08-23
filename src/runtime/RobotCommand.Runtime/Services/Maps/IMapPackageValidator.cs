using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public interface IMapPackageValidator
{
    MapPackageValidationResult Validate(string packageDirectory);
}
