namespace RobotCommand.Bootstrap;

/// <summary>Resolves the shared per-user location for writable Robot Command data.</summary>
public static class RobotCommandDataDirectory
{
    public static string GetDefaultPath()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localApplicationData)
            ? AppContext.BaseDirectory
            : Path.Combine(localApplicationData, "Psycraft", "Robot Command");
    }
}
