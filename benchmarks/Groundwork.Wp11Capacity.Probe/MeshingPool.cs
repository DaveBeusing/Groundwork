using System.Diagnostics;

internal sealed class MeshingPool : IDisposable
{
    internal const int WorkerCount = 3;
    internal const int QueueCapacity = 3;
    private readonly object _sync = new();
    private readonly Queue<WorkItem> _queue = new(QueueCapacity);
    private readonly Thread[] _workers;
    private Action<long, long>? _refill;
    private WorkItem? _pending;
    private int _active;
    private bool _stopping;

    internal MeshingPool()
    {
        _workers = new Thread[WorkerCount];
        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i] = new Thread(Work) { IsBackground = true, Name = $"C00 meshing {i + 1}" };
            _workers[i].Start();
        }
    }

    internal Task<Dispatch<T>> Submit<T>(Func<T> build)
    {
        var done = new TaskCompletionSource<Dispatch<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(Stopwatch.GetTimestamp(), (queued, dequeued) =>
        {
            try { done.SetResult(new(Stopwatch.GetElapsedTime(queued, dequeued).TotalMilliseconds, build())); }
            catch (Exception error) { done.SetException(error); }
        });
        lock (_sync)
        {
            if (_queue.Count < QueueCapacity)
                _queue.Enqueue(item);
            else if (_refill is not null && _pending is null)
                _pending = item;
            else
                throw new InvalidOperationException("C00 worker queue exceeded its capacity.");
            Monitor.PulseAll(_sync);
        }
        return done.Task;
    }

    internal void StartBacklog(Action<long, long> refill)
    {
        lock (_sync)
        {
            if (_refill is not null || _queue.Count != 0 || _active != 0)
                throw new InvalidOperationException("C00 backlog requires an idle pool.");
            _refill = refill;
            for (var i = 0; i < QueueCapacity; i++)
                _queue.Enqueue(new(Stopwatch.GetTimestamp(), refill));
            Monitor.PulseAll(_sync);
        }
    }

    internal bool IsFull
    {
        get { lock (_sync) return _refill is not null && _queue.Count == QueueCapacity; }
    }

    internal void StopBacklog()
    {
        lock (_sync)
        {
            if (_refill is null)
                return;
            if (_pending is not null)
                throw new InvalidOperationException("Protected work is still pending.");
            _refill = null;
            _queue.Clear();
            while (_active != 0)
                Monitor.Wait(_sync);
        }
    }

    private void Work()
    {
        while (true)
        {
            WorkItem item;
            long dequeued;
            lock (_sync)
            {
                while (_queue.Count == 0 && !_stopping)
                    Monitor.Wait(_sync);
                if (_queue.Count == 0)
                    return;
                item = _queue.Dequeue();
                dequeued = Stopwatch.GetTimestamp();
                if (_refill is { } refill)
                {
                    _queue.Enqueue(_pending ?? new(Stopwatch.GetTimestamp(), refill));
                    _pending = null;
                }
                _active++;
            }
            try { item.Run(item.Queued, dequeued); }
            finally
            {
                lock (_sync)
                {
                    _active--;
                    Monitor.PulseAll(_sync);
                }
            }
        }
    }

    public void Dispose()
    {
        StopBacklog();
        lock (_sync)
        {
            _stopping = true;
            Monitor.PulseAll(_sync);
        }
        foreach (var worker in _workers)
            worker.Join();
    }

    private readonly record struct WorkItem(long Queued, Action<long, long> Run);
    internal readonly record struct Dispatch<T>(double QueueMs, T Result);
}

internal readonly record struct GcSample(TimeSpan Pause, int Gen0, int Gen1, int Gen2)
{
    internal static GcSample Read() =>
        new(GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    internal void RecordSince(GcSample before, Dictionary<string, double> metrics, string prefix)
    {
        metrics[$"{prefix}GcPauseMs"] = (Pause - before.Pause).TotalMilliseconds;
        metrics[$"{prefix}Gen0Collections"] = Gen0 - before.Gen0;
        metrics[$"{prefix}Gen1Collections"] = Gen1 - before.Gen1;
        metrics[$"{prefix}Gen2Collections"] = Gen2 - before.Gen2;
    }
}
