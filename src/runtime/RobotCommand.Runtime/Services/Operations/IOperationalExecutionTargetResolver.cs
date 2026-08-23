using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public interface IOperationalExecutionTargetResolver
{
    OperationalInspectionResolution Resolve(OperationalSelection selection);
}
