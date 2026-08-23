using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RobotCommand.State;

/// <summary>
/// ObservableCollection variant that can apply a batch without exposing every
/// intermediate remove/insert/move to consumers.  The final Reset keeps
/// Avalonia bindings correct while preventing one telemetry cycle from turning
/// into one UI refresh per entity.
/// </summary>
internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    private int _batchDepth;
    private bool _changed;

    public IDisposable BeginBatch()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_batchDepth > 0)
        {
            _changed = true;
            return;
        }

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_batchDepth > 0)
        {
            _changed = true;
            return;
        }

        base.OnPropertyChanged(e);
    }

    private void EndBatch()
    {
        if (_batchDepth == 0)
            return;

        _batchDepth--;
        if (_batchDepth != 0 || !_changed)
            return;

        _changed = false;
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private sealed class BatchScope(BatchObservableCollection<T> owner) : IDisposable
    {
        private BatchObservableCollection<T>? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.EndBatch();
        }
    }
}
