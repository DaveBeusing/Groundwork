using System.Collections.Concurrent;
using System.Diagnostics;

internal sealed class MeshingPool : IDisposable
{
    internal const int WorkerCount = 3;
    private readonly BlockingCollection<Action> _queue = new(WorkerCount);
    private readonly Thread[] _workers;

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
        var queued = Stopwatch.GetTimestamp();
        if (!_queue.TryAdd(() =>
            {
                var dequeued = Stopwatch.GetTimestamp();
                try { done.SetResult(new(Stopwatch.GetElapsedTime(queued, dequeued).TotalMilliseconds, build())); }
                catch (Exception error) { done.SetException(error); }
            }))
            throw new InvalidOperationException("C00 worker queue exceeded its capacity.");
        return done.Task;
    }

    private void Work()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
            action();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        foreach (var worker in _workers)
            worker.Join();
        _queue.Dispose();
    }

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
