using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public interface IMapPackageCatalog
{
    event EventHandler? Changed;

    string RootPath { get; }

    string PackagesPath { get; }

    string StagingPath { get; }

    IReadOnlyList<InstalledMapPackage> Packages { get; }

    InstalledMapPackage? ActivePackage { get; }

    bool TryGet(string key, out InstalledMapPackage? package);

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task ActivateAsync(string key, CancellationToken cancellationToken = default);

    Task ClearActiveAsync(CancellationToken cancellationToken = default);
}
