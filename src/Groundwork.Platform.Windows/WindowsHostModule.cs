using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Groundwork.Core;

namespace Groundwork.Platform.Windows;

/// <summary>Owns one Win32 window and its dedicated STA message thread.</summary>
public sealed partial class WindowsHostModule : IEngineModule, IInputSource, IHostWindowStateSource
{
    private static readonly InputSnapshot EmptyInput = new(0, [], [], null, false);
    private static readonly TimeSpan LongestWait = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly WindowsHostOptions _options;
    private readonly RuntimeWakeSignal _wake;
    private readonly IRuntimeOwnership _ownership;
    private readonly Lock _lifecycle = new();
    private readonly Lock _faults = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RuntimeFailure?> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<OperationalFailure> _cleanup = [];
    private EngineModuleContext? _context;
    private WindowsInputBuffer? _input;
    private OperationalFailure? _primary;
    private WindowState _windowState = new(new(HostWindowPhase.Starting, 0, 0, false, 0));
    private Stage _stage;
    private int _stopRequested;
    private IncompleteCleanupException? _teardownTimeout;
    private long _lastClockTimestamp;

    public WindowsHostModule(WindowsHostOptions options, RuntimeWakeSignal uiWake, IRuntimeOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(uiWake);
        ArgumentNullException.ThrowIfNull(ownership);
        (_options, _wake, _ownership) = (options, uiWake, ownership);
    }

    /// <summary>Contains the first operational failure and separate cleanup failures after the host stops.</summary>
    public RuntimeFailure? Failure => _finished.Task.IsCompletedSuccessfully ? _finished.Task.Result : null;

    public InputSnapshot Capture() => Volatile.Read(ref _input)?.Capture() ?? EmptyInput;

    public HostWindowSnapshot Read() => Volatile.Read(ref _windowState).Snapshot;

    public ValueTask InitializeAsync(EngineModuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Clock);
        ArgumentNullException.ThrowIfNull(context.FaultReporter);
        ArgumentNullException.ThrowIfNull(context.StopControl);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Win32 host requires Windows.");
        }

        ValidateOptions(_options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(context.Timing.Shutdown, TimeSpan.Zero, nameof(context));
        var clockFrequency = context.Clock.TimestampFrequency;
        if (clockFrequency <= 0)
        {
            throw new ArgumentException("The host requires a positive clock frequency.", nameof(context));
        }

        var timestamp = context.Clock.GetTimestamp();
        lock (_lifecycle)
        {
            if (_stage != Stage.Created || StopRequested)
            {
                throw new InvalidOperationException("The Windows host cannot be initialized again.");
            }

            _input = new WindowsInputBuffer(_options.TransitionCapacity);
            _lastClockTimestamp = timestamp;
            Volatile.Write(ref _context, context);
            _stage = Stage.Initialized;
        }

        return ValueTask.CompletedTask;
    }

    private static void ValidateOptions(WindowsHostOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Title) || options.Title.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The window title must be nonempty and contain no null characters.", nameof(options));
        }

        if (options.WindowWidth <= 0 || options.WindowHeight <= 0 || options.TransitionCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Window dimensions and transition capacity must be positive.");
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycle)
        {
            if (_stage != Stage.Initialized || StopRequested)
            {
                throw new InvalidOperationException("The Windows host must be initialized and may start only once.");
            }

            _stage = Stage.Starting;
        }

        using var cancellation = cancellationToken.UnsafeRegister(
            static state => ((WindowsHostModule)state!).RequestStop(HostStopReason.ExternalCancellation), this);
        IRuntimeWorkLease? lease = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StopRequested)
            {
                throw new OperationCanceledException("The Windows host received stop before startup.");
            }

            lease = _ownership.TryTrack(this)
                ?? throw new InvalidOperationException("The runtime declined Windows host ownership.");
            var thread = new Thread(RunUiThread) { IsBackground = true, Name = "Groundwork Win32 host" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start(lease);
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException)
            {
                FailUi(error);
            }
            else
            {
                _ready.TrySetCanceled(cancellationToken);
            }

            ReleaseOwnership(lease);
            Finish();
        }

        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (ReferenceEquals(s_uiHost, this))
        {
            throw new InvalidOperationException("The window thread cannot wait for its own shutdown.");
        }

        var deadline = RequestStop(HostStopReason.ExternalCancellation);
        bool closeWithoutThread;
        lock (_lifecycle)
        {
            closeWithoutThread = _stage is Stage.Created or Stage.Initialized;
            if (closeWithoutThread)
            {
                _stage = Stage.ClosingWithoutThread;
            }
        }

        if (closeWithoutThread)
        {
            _ready.TrySetCanceled(CancellationToken.None);
            Finish();
        }

        if (!_finished.Task.IsCompleted)
        {
            if (deadline is { } activeDeadline)
            {
                await AwaitTeardownAsync(activeDeadline, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Another caller is finishing an uninitialized host with no native owner.
                await _finished.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var failure = await _finished.Task.ConfigureAwait(false);
        if (failure is null)
        {
            return;
        }

        if (_ready.Task.IsCompletedSuccessfully)
        {
            failure.Primary?.Exception.Throw();
        }

        if (!failure.Cleanup.IsEmpty)
        {
            failure.Cleanup[0].Exception.Throw();
        }
    }

    public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);

    private EngineModuleContext Context => _context!;
    private WindowsInputBuffer Input => _input!;
    private bool StopRequested => Volatile.Read(ref _stopRequested) != 0;

    private void MarkStop()
    {
        Interlocked.Exchange(ref _stopRequested, 1);
    }

    private RuntimeDeadline? RequestStop(HostStopReason reason)
    {
        MarkStop();
        var context = Volatile.Read(ref _context);
        if (context is null)
        {
            return null;
        }

        try
        {
            return context.StopControl.RequestStop(reason);
        }
        finally
        {
            _wake.Signal();
        }
    }

    private async Task AwaitTeardownAsync(RuntimeDeadline deadline, CancellationToken cancellationToken)
    {
        while (!_finished.Task.IsCompleted)
        {
            var remaining = deadline.GetRemainingForSettlement(Context.Clock);
            if (remaining == TimeSpan.Zero)
            {
                break;
            }

            var interval = TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 1, LongestWait.TotalMilliseconds));
            try
            {
                await _finished.Task.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                // Recheck Core's deadline after the system timer wakes.
            }
        }

        IncompleteCleanupException incomplete;
        lock (_faults)
        {
            if (_finished.Task.IsCompleted)
            {
                return;
            }

            if (_teardownTimeout is not null)
            {
                throw _teardownTimeout;
            }

            // The owner remains registered until the UI thread actually tears down.
            incomplete = new IncompleteCleanupException(FaultSubsystem.Platform);
            _teardownTimeout = incomplete;
            AddFailure(incomplete, FaultClass.IncompleteCleanup, FaultOrigin.Cleanup);
        }

        ReportFailure(incomplete, FaultClass.IncompleteCleanup, FaultOrigin.Cleanup);
        throw incomplete;
    }

    private long SampleTimestamp()
    {
        var timestamp = Context.Clock.GetTimestamp();
        if (timestamp < Volatile.Read(ref _lastClockTimestamp))
        {
            throw new InvalidOperationException("The Windows input clock moved backwards.");
        }

        Volatile.Write(ref _lastClockTimestamp, timestamp);
        return timestamp;
    }

    private void ReleaseOwnership(IRuntimeWorkLease? lease)
    {
        try
        {
            lease?.Dispose();
        }
        catch (Exception error)
        {
            RecordFailure(error, FaultClass.IncompleteCleanup, FaultOrigin.Cleanup);
        }
    }

    private void Finish(bool nativeReleased = true)
    {
        try
        {
            var snapshot = Read();
            Publish(nativeReleased ? HostWindowPhase.Closed : HostWindowPhase.Closing,
                nativeReleased ? 0 : snapshot.ClientWidth, nativeReleased ? 0 : snapshot.ClientHeight, false);
        }
        catch (OverflowException error)
        {
            RecordFailure(error, FaultClass.UnknownCritical, FaultOrigin.Cleanup);
        }

        lock (_lifecycle)
        {
            _stage = Stage.Closed;
        }

        lock (_faults)
        {
            _finished.TrySetResult(_primary is null && _cleanup.Count == 0 ? null :
                new RuntimeFailure(_primary, ImmutableArray.CreateRange(_cleanup)));
        }
    }

    private void RecordFailure(Exception error, FaultClass classification, FaultOrigin origin)
    {
        lock (_faults)
        {
            AddFailure(error, classification, origin);
        }

        ReportFailure(error, classification, origin);
    }

    private void ReportFailure(Exception error, FaultClass classification, FaultOrigin origin)
    {
        var context = Volatile.Read(ref _context);
        if (context is null)
        {
            return;
        }

        try
        {
            var fault = new FaultObservation(classification, FaultSeverity.Error, FaultSubsystem.Platform,
                origin, unchecked((uint)error.HResult), Volatile.Read(ref _lastClockTimestamp), default, [], []);
            context.FaultReporter.TryReport(fault);
        }
        catch (Exception reportingError)
        {
            lock (_faults)
            {
                AddFailure(reportingError, FaultClass.UnknownCritical, FaultOrigin.Cleanup);
            }
        }
    }

    private void AddFailure(Exception error, FaultClass classification, FaultOrigin origin)
    {
        var failure = new OperationalFailure(classification, FaultSubsystem.Platform, default, ExceptionDispatchInfo.Capture(error));
        if (origin == FaultOrigin.Primary && _primary is null)
        {
            _primary = failure;
        }
        else if (_cleanup.Count < 8)
        {
            _cleanup.Add(failure);
        }
    }

    private void Publish(HostWindowPhase phase, int width, int height, bool focused)
    {
        var current = Read();
        if (current.Phase == phase && current.ClientWidth == width && current.ClientHeight == height && current.Focused == focused)
        {
            return;
        }

        Volatile.Write(ref _windowState,
            new WindowState(new HostWindowSnapshot(phase, width, height, focused, checked(current.Revision + 1))));
    }

    private enum Stage { Created, Initialized, Starting, ClosingWithoutThread, Closed }
    private sealed record WindowState(HostWindowSnapshot Snapshot);
}
