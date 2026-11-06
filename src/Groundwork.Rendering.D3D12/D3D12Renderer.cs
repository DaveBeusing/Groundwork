using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Groundwork.Core;
using Groundwork.Platform.Windows;

namespace Groundwork.Rendering.D3D12;

/// <summary>A single-window Direct3D 12 renderer with an exclusive render thread.</summary>
public sealed partial class D3D12Renderer : IRenderer
{
    private readonly WindowsHostModule _window;
    private readonly IRuntimeOwnership _ownership;
    private readonly bool _useWarp;
    private readonly D3D12DiagnosticsOptions _diagnosticsOptions;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _operationTimeout;
    private readonly Action<WindowsSwapChain, int, int>? _resize;
    private readonly RendererFrameAdmission _admission = new();
    private readonly Lock _lifecycle = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private D3D12DiagnosticsSnapshot _diagnostics = new(null, false, false, null, null);
    private EngineModuleContext? _context;
    private AutoResetEvent? _wake;
    private ExceptionDispatchInfo? _primary;
    private IncompleteCleanupException? _incomplete;
    private RenderCustody? _custody;
    private bool _threadStarted;
    private bool _startupSucceeded;
    private long _faultTimestamp;

    public D3D12Renderer(WindowsHostModule window, IRuntimeOwnership ownership, bool useWarp = false,
        TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null)
        : this(default, window, ownership, useWarp, startupTimeout, operationTimeout)
    {
    }

    public D3D12Renderer(D3D12DiagnosticsOptions diagnostics, WindowsHostModule window, IRuntimeOwnership ownership,
        bool useWarp = false, TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(ownership);
        (_window, _ownership, _useWarp) = (window, ownership, useWarp);
        _diagnosticsOptions = diagnostics;
        _startupTimeout = ValidateTimeout(startupTimeout ?? TimeSpan.FromSeconds(5), nameof(startupTimeout));
        _operationTimeout = ValidateTimeout(operationTimeout ?? TimeSpan.FromSeconds(2), nameof(operationTimeout));
    }

    internal D3D12Renderer(WindowsHostModule window, IRuntimeOwnership ownership, TimeSpan operationTimeout,
        Action<WindowsSwapChain, int, int> resize, bool useWarp = true, D3D12DiagnosticsOptions diagnostics = default)
        : this(diagnostics, window, ownership, useWarp, operationTimeout: operationTimeout)
    {
        _resize = resize;
    }

    public RendererSnapshot Read() => _admission.Read();

    /// <summary>Reads the last published native diagnostics without calling the graphics driver.</summary>
    public D3D12DiagnosticsSnapshot ReadDiagnostics() => Volatile.Read(ref _diagnostics);

    public RenderSubmissionResult TrySubmit(in RenderFrame frame)
    {
        var result = _admission.TrySubmit(frame);
        if (result is RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced)
            Wake();
        return result;
    }

    public ValueTask InitializeAsync(EngineModuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Clock);
        ArgumentNullException.ThrowIfNull(context.FaultReporter);
        ArgumentNullException.ThrowIfNull(context.StopControl);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The Direct3D 12 provider requires Windows 11 x64.");
        var timestamp = context.Clock.GetTimestamp();
        lock (_lifecycle)
        {
            _admission.MarkInitialized();
            _context = context;
            _faultTimestamp = timestamp;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycle)
        {
            if (_threadStarted || Read().Lifecycle != RendererLifecycle.Initialized)
                throw new InvalidOperationException("The renderer can start only once after initialization.");

            _wake = new AutoResetEvent(false);
            _threadStarted = true;
        }

        var watchdog = new RendererWatchdog(OnTimeout);
        try
        {
            watchdog.Begin(RendererStage.Startup, null, _startupTimeout);
            var thread = new Thread(() => RunOwner(watchdog)) { IsBackground = true, Name = "Groundwork D3D12 renderer" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        catch (Exception error)
        {
            Fail(RendererStage.Startup, error, null, null);
            watchdog.Dispose();
            Finish();
        }

        try
        {
            await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CloseAdmission();
            _context!.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            throw;
        }

        Volatile.Read(ref _primary)?.Throw();
        if (!Volatile.Read(ref _startupSucceeded))
            throw new OperationCanceledException("The renderer stopped before startup completed.");
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        CloseAdmission();
        EngineModuleContext? context;
        lock (_lifecycle)
        {
            if (!_threadStarted)
                Finish();
            context = _context;
        }

        if (context is null)
            return;

        var deadline = context.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        while (!_settled.Task.IsCompleted)
        {
            var remaining = deadline.GetRemainingForSettlement(context.Clock);
            if (remaining == TimeSpan.Zero)
            {
                if (_settled.Task.IsCompleted)
                    return;

                RecordIncomplete(new IncompleteCleanupException(FaultSubsystem.Host));
                throw Volatile.Read(ref _incomplete)!;
            }

            try
            {
                await _settled.Task.WaitAsync(TimeSpan.FromMilliseconds(
                    Math.Clamp(remaining.TotalMilliseconds, 1, int.MaxValue)), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The runtime deadline is the authority for all shutdown callers.
            }
        }
    }

    public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);

    private void CloseAdmission()
    {
        lock (_lifecycle)
        {
            _admission.BeginStop();
            _wake?.Set();
        }
    }

    private void Wake()
    {
        lock (_lifecycle)
            _wake?.Set();
    }

    private void Finish()
    {
        lock (_lifecycle)
        {
            _admission.BeginStop();
            _admission.MarkStopped();
            _wake?.Dispose();
            _wake = null;
            _ready.TrySetResult();
            _settled.TrySetResult();
        }
    }

    private void OnTimeout(RendererStage stage, ulong? sequence) =>
        Fail(stage, new TimeoutException($"The D3D12 {stage} operation exceeded its progress deadline."), null, sequence);

    private void Fail(RendererStage stage, Exception error, int? removedReason, ulong? sequence)
    {
        if (Interlocked.CompareExchange(ref _primary, ExceptionDispatchInfo.Capture(error), null) is not null)
            return;

        lock (_lifecycle)
        {
            _admission.RecordTerminalFault(new RenderFaultSummary((uint)stage, error.HResult, removedReason, sequence));
            _wake?.Set();
        }

        _ready.TrySetResult();
        try
        {
            _context!.StopControl.RequestStop(Volatile.Read(ref _startupSucceeded) ? HostStopReason.RuntimeFault : HostStopReason.StartupFault);
        }
        catch (Exception stopError)
        {
            _admission.RecordCleanupFault(new RenderFaultSummary((uint)RendererStage.Cleanup,
                stopError.HResult, null, sequence), incomplete: false);
        }
        finally
        {
            Report(FaultClass.UnknownCritical, FaultOrigin.Primary, error);
        }
    }

    private void RecordIncomplete(Exception error)
    {
        var incomplete = error as IncompleteCleanupException ?? new IncompleteCleanupException(
            "The renderer still owns native resources after cleanup failed.", error);
        if (Interlocked.CompareExchange(ref _incomplete, incomplete, null) is not null)
            return;

        _admission.RecordCleanupFault(new RenderFaultSummary((uint)RendererStage.Cleanup, error.HResult, null, null), incomplete: true);
        Report(FaultClass.IncompleteCleanup, FaultOrigin.Cleanup, error);
    }

    private void Report(FaultClass classification, FaultOrigin origin, Exception error)
    {
        try
        {
            var timestamp = ReferenceEquals(_context!.Clock, TimeProvider.System)
                ? Stopwatch.GetTimestamp() : _faultTimestamp;
            _context.FaultReporter.TryReport(new FaultObservation(classification, FaultSeverity.Error, FaultSubsystem.Host,
                origin, unchecked((uint)error.HResult), timestamp, default, default, default));
        }
        catch (Exception reportingError)
        {
            _admission.RecordCleanupFault(new RenderFaultSummary((uint)RendererStage.Cleanup,
                reportingError.HResult, null, null), incomplete: false);
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(parameterName);
        return timeout;
    }
}
