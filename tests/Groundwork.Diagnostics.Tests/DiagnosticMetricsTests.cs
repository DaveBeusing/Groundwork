namespace Groundwork.Diagnostics.Tests;

[TestClass]
public sealed class DiagnosticMetricsTests
{
    [TestMethod]
    public void RecordsCountsAndElapsedValuesInFixedUnits()
    {
        var metrics = new DiagnosticMetrics();
        Assert.IsTrue(metrics.TryRecord(DiagnosticMetric.CompletedTicks));
        Assert.IsTrue(metrics.TryRecord(DiagnosticMetric.CompletedTicks, 5));
        Assert.IsTrue(metrics.TryRecordElapsed(DiagnosticMetric.TickWorkNanoseconds, 1, 3));
        Assert.IsTrue(metrics.TryRecordElapsed(DiagnosticMetric.TickWorkNanoseconds, 250, 1_000_000));
        Assert.IsTrue(metrics.TryRecord(DiagnosticMetric.TickBacklogNanoseconds, 0));

        Assert.AreEqual(new DiagnosticMetricReading(2, 6, 5, false), metrics.Read(DiagnosticMetric.CompletedTicks));
        Assert.AreEqual(new DiagnosticMetricReading(2, 333_583_333, 250_000, false),
            metrics.Read(DiagnosticMetric.TickWorkNanoseconds));
        Assert.AreEqual(new DiagnosticMetricReading(1, 0, 0, false), metrics.Read(DiagnosticMetric.TickBacklogNanoseconds));
        Assert.AreEqual(default, metrics.Read(DiagnosticMetric.JobWorkerNanoseconds));
    }

    [TestMethod]
    public void DisabledAndInvalidSamplesLeaveStateUntouched()
    {
        var disabled = DiagnosticMetrics.Disabled;
        Assert.IsFalse(disabled.TryRecord(DiagnosticMetric.FatalStops));
        Assert.IsFalse(disabled.TryRecordElapsed(DiagnosticMetric.FrameWorkNanoseconds, long.MaxValue, 1));
        Assert.AreEqual(default, disabled.Read(DiagnosticMetric.FatalStops));
        Assert.AreEqual(0L, disabled.DroppedMeasurements);

        var enabled = new DiagnosticMetrics();
        Assert.IsFalse(enabled.TryRecord(default));
        Assert.IsFalse(enabled.TryRecord(DiagnosticMetric.CompletedTicks, -1));
        Assert.IsFalse(enabled.TryRecordElapsed(DiagnosticMetric.CompletedTicks, 1, 1));
        Assert.IsFalse(enabled.TryRecordElapsed(DiagnosticMetric.JobQueueNanoseconds, -1, 1));
        Assert.IsFalse(enabled.TryRecordElapsed(DiagnosticMetric.JobQueueNanoseconds, 1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => enabled.Read(default));
        Assert.AreEqual(default, enabled.Read(DiagnosticMetric.CompletedTicks));
        Assert.AreEqual(default, enabled.Read(DiagnosticMetric.JobQueueNanoseconds));
        Assert.AreEqual(0L, enabled.DroppedMeasurements);
    }

    [TestMethod]
    public void SaturatesTotalsAndConvertedDurationsWithoutWrapping()
    {
        var metrics = new DiagnosticMetrics();
        Assert.IsTrue(metrics.TryRecord(DiagnosticMetric.RejectedBatches, long.MaxValue));
        Assert.IsFalse(metrics.Read(DiagnosticMetric.RejectedBatches).Saturated);
        Assert.IsTrue(metrics.TryRecord(DiagnosticMetric.RejectedBatches, 2));
        Assert.AreEqual(new DiagnosticMetricReading(2, long.MaxValue, 2, true), metrics.Read(DiagnosticMetric.RejectedBatches));

        Assert.IsTrue(metrics.TryRecordElapsed(DiagnosticMetric.JobQueueNanoseconds, long.MaxValue, 1));
        Assert.AreEqual(new DiagnosticMetricReading(1, long.MaxValue, long.MaxValue, true),
            metrics.Read(DiagnosticMetric.JobQueueNanoseconds));
    }

    [TestMethod]
    public void ConcurrentProducersAccountForAcceptedAndDroppedMeasurements()
    {
        const int producers = 4;
        const int measurements = 1_000;
        var metrics = new DiagnosticMetrics();
        long accepted = 0;
        Parallel.For(0, producers, _ =>
        {
            for (var i = 0; i < measurements; i++)
            {
                if (metrics.TryRecord(DiagnosticMetric.DependencyCancellations, 2))
                    Interlocked.Increment(ref accepted);
            }
        });

        var result = metrics.Read(DiagnosticMetric.DependencyCancellations);
        Assert.AreEqual(accepted, result.Samples);
        Assert.AreEqual(accepted * 2, result.Total);
        Assert.AreEqual(producers * measurements - accepted, metrics.DroppedMeasurements);
        Assert.IsFalse(result.Saturated);
    }
}
