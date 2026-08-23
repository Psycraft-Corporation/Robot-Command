using System.Text.Json;
using System.Text.Json.Serialization;
using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public sealed class SavedMapViewRepository : ISavedMapViewRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly string _path;
    private SavedMapView[] _views;

    public SavedMapViewRepository(IMapPackageCatalog catalog)
    {
        _path = Path.Combine(catalog.RootPath, "saved-map-views.json");
        _views = Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<SavedMapView> Views
    {
        get
        {
            lock (_gate)
            {
                return _views.ToArray();
            }
        }
    }

    public bool TryGet(string id, out SavedMapView? view)
    {
        lock (_gate)
        {
            view = _views.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            return view is not null;
        }
    }

    public Task UpsertAsync(SavedMapView view, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(view);
        ValidateView(view);
        lock (_gate)
        {
            var list = _views.ToList();
            var index = list.FindIndex(item => string.Equals(item.Id, view.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                list[index] = view;
            }
            else
            {
                list.Add(view);
            }

            _views = Order(list);
            PersistLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var replacement = _views
                .Where(item => !string.Equals(item.Id, id, StringComparison.Ordinal))
                .ToArray();
            if (replacement.Length == _views.Length)
            {
                return Task.CompletedTask;
            }

            _views = replacement;
            PersistLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ImportAsync(
        IEnumerable<SavedMapView> views,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(views);
        var incoming = views.ToArray();
        foreach (var view in incoming)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(view);
            ValidateView(view);
        }

        lock (_gate)
        {
            var merged = _views.ToDictionary(item => item.Id, StringComparer.Ordinal);
            foreach (var view in incoming)
            {
                cancellationToken.ThrowIfCancellationRequested();
                merged[view.Id] = view;
            }

            _views = Order(merged.Values);
            PersistLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private SavedMapView[] Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return Order((JsonSerializer.Deserialize<SavedMapView[]>(stream, JsonOptions) ?? [])
                .Where(IsValid));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void PersistLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, _views, JsonOptions);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    internal static void ValidateView(SavedMapView view)
    {
        if (!IsValid(view))
        {
            throw new InvalidDataException("The saved map view contains invalid identity or viewport data.");
        }
    }

    private static bool IsValid(SavedMapView? view)
    {
        if (view?.Viewport is not { } viewport)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(view.Id) &&
               !string.IsNullOrWhiteSpace(view.Name) &&
               double.IsFinite(viewport.LongitudeDegrees) &&
               viewport.LongitudeDegrees is >= -180 and <= 180 &&
               double.IsFinite(viewport.LatitudeDegrees) &&
               viewport.LatitudeDegrees is >= -90 and <= 90 &&
               double.IsFinite(viewport.Resolution) &&
               viewport.Resolution > 0 &&
               double.IsFinite(viewport.RotationDegrees) &&
               (view.SelectionKind == SelectionKind.None || !string.IsNullOrWhiteSpace(view.SelectionId));
    }

    private static SavedMapView[] Order(IEnumerable<SavedMapView> views)
        => views
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
}
