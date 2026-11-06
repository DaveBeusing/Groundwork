using System.Diagnostics.CodeAnalysis;

namespace Groundwork.Core;

public interface IRuntimeStopControl
{
    RuntimeDeadline RequestStop(HostStopReason reason);
}

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The runtime authority owns the wake handle through terminal custody.")]
public sealed class RuntimeWakeSignal
{
    private readonly AutoResetEvent _signal = new(false);
    private int _closed;

    internal RuntimeWakeSignal() { }

    public WaitHandle Handle => _signal;

    public void Signal()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _closed) != 0)
        {
            // A concurrent late producer cannot signal a released runtime.
        }
    }

    internal void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _signal.Dispose();
        }
    }
}

public interface IRuntimeWorkLease : IDisposable { }

public interface IRuntimeOwnership
{
    IRuntimeWorkLease? TryTrack(object retainedOwner);
}

public enum ShutdownSettlement : byte { Pending = 1, Released, Custody, InvalidScope }

public sealed class RuntimeSafetyAuthority
{
    private static readonly Lock CustodianGate = new();
    private static readonly HashSet<RuntimeSafetyAuthority> Custodian = [];
    private static ulong _lastWorldId;

    private readonly ProcessRuntimeReservation _reservation;
    private readonly ResourceQuotaUsage _worldUsage;
    private readonly FaultLedger _ledger;
    private readonly OwnershipScope _ownership;
    private readonly HashSet<WorldId> _pendingWorlds = [];
    private readonly Dictionary<WorldId, WorldControl> _worlds = [];
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StopRequest? _stopRequest;
    private RuntimeDeadline? _deadline;
    private long _fallbackTimestamp = long.MaxValue;
    private long _fallbackWatchdogOrigin = long.MaxValue;
    private int _contendedEvidenceLoss;
    private int _outstandingOperations;
    private int _runtimeCustody;
    private bool _settling;
    private bool _released;

    private RuntimeSafetyAuthority(
        DeadlineClock clock, long shutdown, long borrowedDrain, long supervisorResponse,
        ProcessRuntimeReservation reservation, int runtimeDiagnosticRecords, long runtimeDiagnosticBytes)
    {
        Clock = clock;
        ShutdownDuration = shutdown;
        BorrowedDrainDuration = borrowedDrain;
        SupervisorDuration = supervisorResponse;
        _reservation = reservation;
        var diagnosticQuota = reservation.Quota.Diagnostics;
        _worldUsage = new ResourceQuotaUsage(reservation.Quota with
        {
            Diagnostics = diagnosticQuota with
            {
                NormalRecords = diagnosticQuota.NormalRecords - runtimeDiagnosticRecords,
                NormalBytes = diagnosticQuota.NormalBytes - runtimeDiagnosticBytes,
                EmergencyRecords = diagnosticQuota.EmergencyRecords - FaultLedger.EmergencySummaryCount,
                EmergencyBytes = diagnosticQuota.EmergencyBytes -
                    (long)FaultLedger.EmergencySummaryCount * diagnosticQuota.MaxPayloadBytesPerRecord
            }
        });
        _ownership = new OwnershipScope(this, null, reservation.Quota.OutstandingOperations);
        _ledger = new FaultLedger(this, null, diagnosticQuota with
        {
            NormalRecords = runtimeDiagnosticRecords,
            NormalBytes = runtimeDiagnosticBytes,
        }, StopFromFault);
        Reporter = new FaultReporterPort(_ledger);
        State = new SafetyStatePort(_ledger);
        Diagnostics = new DiagnosticReaderPort(_ledger);
        StopControl = new StopPort(this);
        UiWake = new RuntimeWakeSignal();
        try
        {
            SimulationWake = new RuntimeWakeSignal();
        }
        catch
        {
            UiWake.Close();
            throw;
        }
    }

    public static RuntimeSafetyAuthority Create(
        TimeProvider clock, RuntimeTimingLimits timing, ProcessRuntimeReservation reservation)
        => Create(clock, timing, reservation, 0);

    /// <summary>Sets aside part of the runtime's normal diagnostic quota for host events.</summary>
    public static RuntimeSafetyAuthority Create(
        TimeProvider clock, RuntimeTimingLimits timing, ProcessRuntimeReservation reservation, int runtimeDiagnosticRecords)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentOutOfRangeException.ThrowIfNegative(runtimeDiagnosticRecords);
        var quota = reservation.Quota.Diagnostics;
        var runtimeDiagnosticBytes = (long)runtimeDiagnosticRecords * quota.MaxPayloadBytesPerRecord;
        if (runtimeDiagnosticRecords > quota.NormalRecords || runtimeDiagnosticBytes > quota.NormalBytes)
            throw new ArgumentOutOfRangeException(nameof(runtimeDiagnosticRecords));
        var domain = new DeadlineClock(clock);
        var shutdown = domain.GetDuration(timing.Shutdown);
        var borrowedDrain = domain.GetDuration(timing.BorrowedDrain);
        var supervisor = domain.GetDuration(timing.SupervisorResponse);
        if (!reservation.TryAdopt())
        {
            throw new InvalidOperationException("The process reservation is already adopted or released.");
        }

        try
        {
            return new RuntimeSafetyAuthority(domain, shutdown, borrowedDrain, supervisor, reservation,
                runtimeDiagnosticRecords, runtimeDiagnosticBytes);
        }
        catch
        {
            reservation.ReleaseAdopted();
            throw;
        }
    }

    public IRuntimeStopControl StopControl { get; }
    public IFaultReporter Reporter { get; }
    public IRuntimeSafetyState State { get; }
    public IDiagnosticReader Diagnostics { get; }
    public RuntimeWakeSignal UiWake { get; }
    public RuntimeWakeSignal SimulationWake { get; }
    public IRuntimeOwnership RuntimeOwnership => _ownership;

    internal object Gate { get; } = new();
    internal DeadlineClock Clock { get; }
    internal long ShutdownDuration { get; }
    internal long BorrowedDrainDuration { get; }
    internal long SupervisorDuration { get; }
    internal Task StopRequested => _stopRequested.Task;
    internal bool HasContendedEvidenceLoss => Volatile.Read(ref _contendedEvidenceLoss) != 0;
    internal bool IsPublicationFenced => Volatile.Read(ref _stopRequest) is not null || HasContendedEvidenceLoss || !Clock.IsValid;

    internal SafetyCondition InheritedConditions
    {
        get
        {
            var conditions = IsPublicationFenced ? SafetyCondition.TerminalStop : SafetyCondition.None;
            if (HasContendedEvidenceLoss)
            {
                conditions |= SafetyCondition.RequiredEvidenceLost;
            }

            return Volatile.Read(ref _runtimeCustody) != 0
                ? conditions | SafetyCondition.IncompleteCleanup
                : conditions;
        }
    }

    // The fences are monotonic. If all checks pass, the first interlocked
    // read is the publication linearization point. Ledger changes stay under Gate.
    internal bool TryAuthorizePublication()
        => Interlocked.CompareExchange(ref _stopRequest, null, null) is null && Clock.IsValid &&
           Interlocked.CompareExchange(ref _contendedEvidenceLoss, 0, 0) == 0;

    internal void FenceOnContendedEvidenceLoss()
    {
        // If the gate is contended, use the last trusted clock sample. This can
        // shorten draining, but never allows a contended fault to extend cleanup.
        Interlocked.CompareExchange(ref _fallbackTimestamp, Clock.LastTimestamp, long.MaxValue);
        Interlocked.CompareExchange(ref _fallbackWatchdogOrigin, TimeProvider.System.GetTimestamp(), long.MaxValue);
        Interlocked.Exchange(ref _contendedEvidenceLoss, 1);
        SignalStop();
    }

    private void SignalStop()
    {
        UiWake.Signal();
        SimulationWake.Signal();
        _stopRequested.TrySetResult();
    }

    public bool TryAllocateWorldId(out WorldId world)
    {
        lock (Gate)
        {
            world = default;
            if (IsPublicationFenced || _worlds.Count + _pendingWorlds.Count >= _reservation.Quota.Worlds)
            {
                return false;
            }

            while (true)
            {
                var last = Volatile.Read(ref _lastWorldId);
                if (last == ulong.MaxValue)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _lastWorldId, last + 1, last) == last)
                {
                    world = new WorldId(last + 1);
                    _pendingWorlds.Add(world);
                    return true;
                }
            }
        }
    }

    public bool TryAbandonWorldId(WorldId world)
    {
        lock (Gate)
        {
            return _pendingWorlds.Remove(world);
        }
    }

    public WorldSafetyPorts OpenWorld(
        WorldId world, ResourceQuota quota, IBarrierSupervisorPolicy policy,
        IWorldStateRoot initialRoot, IJobScopePolicy scopePolicy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(initialRoot);
        ArgumentNullException.ThrowIfNull(scopePolicy);
        ResourceQuotaUsage.Validate(quota);
        if (initialRoot.World != world || quota.Worlds != 1)
        {
            throw new ArgumentException("A world requires its own initial root and one world slot.", nameof(initialRoot));
        }

        lock (Gate)
        {
            if (IsPublicationFenced || !_pendingWorlds.Contains(world) || !_worldUsage.TryCharge(quota, includeOperations: false))
            {
                throw new InvalidOperationException("The world is not pending in this authority or its quota is unavailable.");
            }

            try
            {
                var control = new WorldControl(this, world, quota, policy, initialRoot, scopePolicy);
                var ports = new WorldSafetyPorts(control);
                _worlds.Add(world, control);
                _pendingWorlds.Remove(world);
                return ports;
            }
            catch
            {
                _worldUsage.Release(quota, includeOperations: false);
                throw;
            }
        }
    }

    public ShutdownSettlement SettleWorld(WorldId world)
    {
        lock (Gate)
        {
            if (!_worlds.TryGetValue(world, out var control))
            {
                return ShutdownSettlement.InvalidScope;
            }

            if (IsPublicationFenced && !_deadline.HasValue)
            {
                StopCore();
            }

            if (control.Ledger.HasContendedEvidenceLoss && !control.Deadline.HasValue)
            {
                control.StopWorld();
            }

            var result = SettleWorldCore(control);
            TryReleaseStoppedRuntime();
            return result;
        }
    }

    public ShutdownSettlement SettleStop()
    {
        if (Volatile.Read(ref _released))
        {
            return ShutdownSettlement.Released;
        }

        if (!IsPublicationFenced)
        {
            return ShutdownSettlement.InvalidScope;
        }

        var deadline = BeginStop();
        if (!Monitor.TryEnter(Gate))
        {
            return deadline.IsExpiredForSettlement()
                ? EnterRuntimeCustody(reconcileLater: true)
                : ShutdownSettlement.Pending;
        }

        try
        {
            return SettleStopCore();
        }
        finally
        {
            Monitor.Exit(Gate);
        }
    }

    private ShutdownSettlement SettleStopCore()
    {
        if (_released)
        {
            return ShutdownSettlement.Released;
        }

        _settling = true;
        var deadline = StopCore();
        foreach (var world in _worlds.Values)
        {
            SettleWorldCore(world);
        }

        if (TryReleaseStoppedRuntime())
        {
            return ShutdownSettlement.Released;
        }

        return deadline.IsExpiredForSettlement()
            ? EnterRuntimeCustody(reconcileLater: false)
            : ShutdownSettlement.Pending;
    }

    private ShutdownSettlement EnterRuntimeCustody(bool reconcileLater)
    {
        bool entered;
        lock (CustodianGate)
        {
            if (Volatile.Read(ref _released))
            {
                return ShutdownSettlement.Released;
            }

            entered = _runtimeCustody == 0;
            if (entered)
            {
                Volatile.Write(ref _runtimeCustody, 1);
                Custodian.Add(this);
            }
        }

        if (entered && reconcileLater)
        {
            _ = ReconcileCustodyAsync();
        }

        return ShutdownSettlement.Custody;
    }

    private async Task ReconcileCustodyAsync()
    {
        // Custody is already rooted. Reconcile without occupying a worker
        // while a producer still holds the gate.
        while (!Volatile.Read(ref _released))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
            if (!Monitor.TryEnter(Gate))
            {
                continue;
            }

            try
            {
                SettleStopCore();
            }
            finally
            {
                Monitor.Exit(Gate);
            }

            return;
        }
    }

    internal bool TryTrackOperation()
    {
        if (IsPublicationFenced || _outstandingOperations >= _reservation.Quota.OutstandingOperations)
        {
            return false;
        }

        _outstandingOperations++;
        return true;
    }

    internal void ReleaseOperation(WorldControl? world)
    {
        _outstandingOperations--;
        if (!_settling && Volatile.Read(ref _runtimeCustody) != 0)
        {
            SettleStopCore();
            return;
        }

        if (world is { Settling: true, IsStopping: false, OwnerCount: 0 } && world.Ledger.TryCloseDiagnostics())
        {
            ReleaseWorld(world);
        }

        TryReleaseStoppedRuntime();
    }

    private ShutdownSettlement SettleWorldCore(WorldControl world)
    {
        if (world.Deadline is not { } deadline)
        {
            return ShutdownSettlement.InvalidScope;
        }

        world.Settling = true;
        if (world.OwnerCount == 0 && world.Ledger.TryCloseDiagnostics())
        {
            ReleaseWorld(world);
            return ShutdownSettlement.Released;
        }

        if (!deadline.IsExpiredForSettlement())
        {
            return ShutdownSettlement.Pending;
        }

        world.InCustody = true;
        world.Ledger.MarkIncomplete();
        UpdateCustodian();
        return ShutdownSettlement.Custody;
    }

    private void ReleaseWorld(WorldControl world)
    {
        if (_worlds.Remove(world.World))
        {
            world.Release();
            _worldUsage.Release(world.Quota, includeOperations: false);
            UpdateCustodian();
        }
    }

    internal void ReconcileDiagnosticSettlement(WorldId? world)
    {
        lock (Gate)
        {
            if (_released)
                return;
            if (_settling)
                SettleStopCore();
            else if (world is { } id && _worlds.TryGetValue(id, out var control) && control.Settling)
                SettleWorldCore(control);
        }
    }

    private bool TryReleaseStoppedRuntime()
    {
        if (!_released && _settling && _worlds.Count == 0 && _outstandingOperations == 0 && _ledger.TryCloseDiagnostics())
        {
            _ledger.ReleaseStorage();
            UiWake.Close();
            SimulationWake.Close();
            _reservation.ReleaseAdopted();
            lock (CustodianGate)
            {
                Volatile.Write(ref _released, true);
                Custodian.Remove(this);
            }
        }

        return _released;
    }

    private void UpdateCustodian()
    {
        lock (CustodianGate)
        {
            if (!_released && (_runtimeCustody != 0 || _worlds.Values.Any(world => world.InCustody)))
            {
                Custodian.Add(this);
            }
            else
            {
                Custodian.Remove(this);
            }
        }
    }

    private void StopFromFault() => StopCore();

    private RuntimeDeadline BeginStop()
    {
        if (Volatile.Read(ref _stopRequest) is { } existing)
        {
            return existing.Deadline;
        }

        // The system clock is trusted. A supplied clock can run user code or
        // already be blocked, so its last trusted sample bounds shutdown.
        var now = ReferenceEquals(Clock.Provider, TimeProvider.System)
            ? TimeProvider.System.GetTimestamp()
            : Clock.LastTimestamp;
        var origin = Math.Min(now, Volatile.Read(ref _fallbackTimestamp));
        var watchdogOrigin = Volatile.Read(ref _fallbackWatchdogOrigin);
        var request = new StopRequest(Clock.At(
            origin, ShutdownDuration, Clock.IsValid, watchdogOrigin == long.MaxValue ? null : watchdogOrigin));
        if (Interlocked.CompareExchange(ref _stopRequest, request, null) is { } previous)
        {
            return previous.Deadline;
        }

        SignalStop();
        return request.Deadline;
    }

    private RuntimeDeadline StopCore()
    {
        if (_deadline is { } existing)
        {
            return existing;
        }

        var deadline = BeginStop();
        _deadline = deadline;
        _ledger.MarkStopped();
        _pendingWorlds.Clear();
        foreach (var world in _worlds.Values)
        {
            world.StopForRuntime(deadline);
        }

        return deadline;
    }

    private sealed class StopPort(RuntimeSafetyAuthority authority) : IRuntimeStopControl
    {
        public RuntimeDeadline RequestStop(HostStopReason reason)
        {
            if (!Enum.IsDefined(reason))
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            return authority.BeginStop();
        }
    }

    private sealed record StopRequest(RuntimeDeadline Deadline);
}

internal sealed class OwnershipScope(RuntimeSafetyAuthority runtime, WorldControl? world, int limit) : IRuntimeOwnership
{
    private readonly HashSet<WorkLease> _leases = [];

    internal int Count => _leases.Count;

    public IRuntimeWorkLease? TryTrack(object retainedOwner)
    {
        ArgumentNullException.ThrowIfNull(retainedOwner);
        lock (runtime.Gate)
        {
            if (world is { IsTerminal: true } || _leases.Count >= limit || !runtime.TryTrackOperation())
            {
                return null;
            }

            try
            {
                var lease = new WorkLease(this, retainedOwner);
                _leases.Add(lease);
                return lease;
            }
            catch
            {
                runtime.ReleaseOperation(world);
                throw;
            }
        }
    }

    private void Release(WorkLease lease)
    {
        lock (runtime.Gate)
        {
            if (_leases.Remove(lease))
            {
                lease.ClearOwner();
                runtime.ReleaseOperation(world);
            }
        }
    }

    private sealed class WorkLease(OwnershipScope scope, object retainedOwner) : IRuntimeWorkLease
    {
        private object? _retainedOwner = retainedOwner;

        internal void ClearOwner()
        {
            GC.KeepAlive(_retainedOwner);
            _retainedOwner = null;
        }

        public void Dispose() => scope.Release(this);
    }
}
