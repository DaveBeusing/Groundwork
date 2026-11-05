using System.Runtime.CompilerServices;

namespace Groundwork.Core;

public readonly record struct RuntimeTimingLimits(
    TimeSpan Shutdown, TimeSpan BorrowedDrain, TimeSpan SupervisorResponse);

public readonly record struct DiagnosticQuota(
    int NormalRecords, long NormalBytes,
    int EmergencyRecords, long EmergencyBytes,
    int RequiredEvidenceRecords, int MaxTargetsPerRecord,
    int MaxTextCharsPerRecord, int MaxPayloadBytesPerRecord);

public readonly record struct JobQuota(
    int Jobs, int Workers, int DependencyEdges, int RetentionHandles,
    int BorrowedLeases, int DeferredBatches, int ReceiptRecords,
    long DescriptorBytes, long SnapshotBytes, long ResultBytes);

public readonly record struct ResourceQuota(
    int Worlds, int OutstandingOperations, JobQuota Jobs, DiagnosticQuota Diagnostics);

public readonly record struct ProcessResourceLimits(int Runtimes, ResourceQuota Resources);

public sealed class ProcessResourceBudget
{
    private readonly Lock _gate = new();
    private readonly ProcessResourceLimits _limits;
    private readonly ResourceQuotaUsage _usage;
    private int _runtimes;

    public ProcessResourceBudget(ProcessResourceLimits limits)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.Runtimes);
        ResourceQuotaUsage.Validate(limits.Resources);
        _limits = limits;
        _usage = new(limits.Resources);
    }

    public ProcessRuntimeReservation? TryReserveRuntime(ResourceQuota quota)
    {
        ResourceQuotaUsage.Validate(quota);
        lock (_gate)
        {
            if (_runtimes == _limits.Runtimes || !_usage.TryCharge(quota))
            {
                return null;
            }

            try
            {
                var reservation = new ProcessRuntimeReservation(this, quota);
                _runtimes++;
                return reservation;
            }
            catch
            {
                _usage.Release(quota);
                throw;
            }
        }
    }

    internal void Release(ResourceQuota quota)
    {
        lock (_gate)
        {
            _usage.Release(quota);
            _runtimes--;
        }
    }
}

public sealed class ProcessRuntimeReservation : IDisposable
{
    private readonly ProcessResourceBudget _budget;
    private int _state;

    internal ProcessRuntimeReservation(ProcessResourceBudget budget, ResourceQuota quota)
        => (_budget, Quota) = (budget, quota);

    public ResourceQuota Quota { get; }

    internal bool TryAdopt() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    internal void ReleaseAdopted()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) == 1)
        {
            _budget.Release(Quota);
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
        {
            _budget.Release(Quota);
        }
    }
}

internal sealed class ResourceQuotaUsage(ResourceQuota quota)
{
    private const int QuantityCount = 17;
    private readonly long[] _used = new long[QuantityCount];

    internal static void Validate(ResourceQuota quota)
    {
        Span<long> quantities = stackalloc long[QuantityCount];
        CopyQuantities(quota, quantities, includeOperations: true);
        foreach (var quantity in quantities)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity, nameof(quota));
        }

        var diagnostic = quota.Diagnostics;
        if (quota.Jobs.Workers > quota.Jobs.Jobs ||
            diagnostic.MaxTargetsPerRecord <= 0 ||
            diagnostic.MaxTextCharsPerRecord <= 0 ||
            diagnostic.MaxPayloadBytesPerRecord < FaultLedger.MinimumSummaryPayload ||
            diagnostic.MaxPayloadBytesPerRecord > diagnostic.NormalBytes ||
            (long)diagnostic.RequiredEvidenceRecords + FaultLedger.EmergencySummaryCount > diagnostic.EmergencyRecords ||
            RequiredEvidenceBytes(diagnostic.RequiredEvidenceRecords) +
                (long)FaultLedger.EmergencySummaryCount * diagnostic.MaxPayloadBytesPerRecord > diagnostic.EmergencyBytes)
        {
            throw new ArgumentException("The quota cannot contain its configured workers and diagnostic reservations.", nameof(quota));
        }
    }

    internal static long RequiredEvidenceBytes(int records)
        => (long)Unsafe.SizeOf<RequiredFaultEvidence>() * records;

    internal bool TryCharge(ResourceQuota child, bool includeOperations = true)
    {
        var limits = quota.Diagnostics;
        var shape = child.Diagnostics;
        if (shape.MaxTargetsPerRecord > limits.MaxTargetsPerRecord ||
            shape.MaxTextCharsPerRecord > limits.MaxTextCharsPerRecord ||
            shape.MaxPayloadBytesPerRecord > limits.MaxPayloadBytesPerRecord ||
            child.OutstandingOperations > quota.OutstandingOperations)
        {
            return false;
        }

        Span<long> capacities = stackalloc long[QuantityCount];
        Span<long> amounts = stackalloc long[QuantityCount];
        CopyQuantities(quota, capacities, includeOperations);
        CopyQuantities(child, amounts, includeOperations);
        for (var i = 0; i < _used.Length; i++)
        {
            if (amounts[i] > capacities[i] - _used[i])
            {
                return false;
            }
        }

        for (var i = 0; i < _used.Length; i++)
        {
            _used[i] += amounts[i];
        }

        return true;
    }

    internal void Release(ResourceQuota child, bool includeOperations = true)
    {
        Span<long> amounts = stackalloc long[QuantityCount];
        CopyQuantities(child, amounts, includeOperations);
        for (var i = 0; i < _used.Length; i++)
        {
            _used[i] -= amounts[i];
        }
    }

    private static void CopyQuantities(ResourceQuota quota, Span<long> target, bool includeOperations)
    {
        var jobs = quota.Jobs;
        var diagnostics = quota.Diagnostics;
        ReadOnlySpan<long> quantities =
        [
            quota.Worlds, includeOperations ? quota.OutstandingOperations : 0,
            jobs.Jobs, jobs.Workers, jobs.DependencyEdges, jobs.RetentionHandles,
            jobs.BorrowedLeases, jobs.DeferredBatches, jobs.ReceiptRecords,
            jobs.DescriptorBytes, jobs.SnapshotBytes, jobs.ResultBytes,
            diagnostics.NormalRecords, diagnostics.NormalBytes, diagnostics.EmergencyRecords,
            diagnostics.EmergencyBytes, diagnostics.RequiredEvidenceRecords
        ];
        quantities.CopyTo(target);
    }
}
