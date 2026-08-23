using System.Collections.ObjectModel;

namespace RobotCommand.State;

public interface IEntityStore<TKey, TEntity>
    where TKey : notnull
{
    ReadOnlyObservableCollection<TEntity> Items { get; }

    bool TryGet(TKey key, out TEntity? entity);

    void Upsert(TEntity entity);

    void ReplaceAll(IEnumerable<TEntity> entities);

    bool Remove(TKey key);

    void Clear();
}
