using System.Reflection;

namespace RobotCommand.Services;

internal static class ThisAssembly
{
    public static string Version { get; } =
        typeof(ThisAssembly).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ThisAssembly).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
