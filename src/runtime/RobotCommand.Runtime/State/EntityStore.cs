using System.Collections.ObjectModel;

namespace RobotCommand.State;

public sealed class EntityStore<TKey, TEntity> : IEntityStore<TKey, TEntity>
    where TKey : notnull
{
    private readonly Func<TEntity, TKey> _keySelector;
    private readonly IEqualityComparer<TKey> _comparer;
    private readonly Dictionary<TKey, TEntity> _byKey;
    private readonly BatchObservableCollection<TEntity> _items = [];

    public EntityStore(Func<TEntity, TKey> keySelector, IEqualityComparer<TKey>? comparer = null)
    {
        _keySelector = keySelector;
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
        _byKey = new Dictionary<TKey, TEntity>(_comparer);
        Items = new ReadOnlyObservableCollection<TEntity>(_items);
    }

    public ReadOnlyObservableCollection<TEntity> Items { get; }

    public bool TryGet(TKey key, out TEntity? entity)
    {
        if (_byKey.TryGetValue(key, out var value))
        {
            entity = value;
            return true;
        }

        entity = default;
        return false;
    }

    public void Upsert(TEntity entity)
    {
        var key = _keySelector(entity);
        if (_byKey.TryGetValue(key, out var existing))
        {
            if (EqualityComparer<TEntity>.Default.Equals(existing, entity))
            {
                return;
            }

            var index = FindIndex(key);
            if (index >= 0)
            {
                _items[index] = entity;
            }
        }
        else
        {
            _items.Add(entity);
        }

        _byKey[key] = entity;
    }

    public void ReplaceAll(IEnumerable<TEntity> entities)
    {
        var replacement = entities.ToArray();
        var replacementKeys = new HashSet<TKey>(replacement.Select(_keySelector), _comparer);

        // ReplaceAll is the hot path for telemetry and diagnostics.  Apply the
        // complete diff privately and expose one Reset after the final order
        // is established.  Consumers still see the same final collection, but
        // no longer rebuild once per entity.
        using var batch = _items.BeginBatch();

        foreach (var existingKey in _byKey.Keys.Where(key => !replacementKeys.Contains(key)).ToArray())
        {
            Remove(existingKey);
        }

        for (var targetIndex = 0; targetIndex < replacement.Length; targetIndex++)
        {
            var entity = replacement[targetIndex];
            var key = _keySelector(entity);
            if (_byKey.TryGetValue(key, out var existing))
            {
                var currentIndex = FindIndex(key);
                if (!EqualityComparer<TEntity>.Default.Equals(existing, entity) && currentIndex >= 0)
                {
                    _items[currentIndex] = entity;
                }

                _byKey[key] = entity;
                currentIndex = FindIndex(key);
                if (currentIndex >= 0 && currentIndex != targetIndex)
                {
                    _items.Move(currentIndex, targetIndex);
                }
            }
            else
            {
                _items.Insert(Math.Min(targetIndex, _items.Count), entity);
                _byKey[key] = entity;
            }
        }
    }

    public bool Remove(TKey key)
    {
        if (!_byKey.Remove(key))
        {
            return false;
        }

        var index = FindIndex(key);
        if (index >= 0)
        {
            _items.RemoveAt(index);
        }

        return true;
    }

    public void Clear()
    {
        _byKey.Clear();
        _items.Clear();
    }

    private int FindIndex(TKey key)
    {
        for (var index = 0; index < _items.Count; index++)
        {
            if (_comparer.Equals(_keySelector(_items[index]), key))
            {
                return index;
            }
        }

        return -1;
    }
}
