using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Groundwork.Core;
using Groundwork.Voxels;
using Groundwork.Platform.Windows;

namespace Groundwork.Rendering.D3D12;

/// <summary>A single-window Direct3D 12 renderer with an exclusive render thread.</summary>
public sealed partial class D3D12Renderer : IRenderer
{
    private readonly WindowsHostModule _window;
    private readonly IRuntimeOwnership _ownership;
    private readonly bool _useWarp;
    private readonly D3D12DiagnosticsOptions _diagnosticsOptions;
    private readonly ResourceDiagnostics? _resourceDiagnostics;
    private int _gpuFaultCustody;
    private GpuDevice? _observedGpu;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _operationTimeout;
    private readonly Action<WindowsSwapChain, int, int>? _resize;
    private readonly RendererFrameAdmission _admission = new();
    private readonly Lock _lifecycle = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RenderMeshRegion? _initialGeometry;
    private D3D12DeviceSession? _deviceSession;

    /// <summary>Uses the session device for an orbital renderer after the preceding renderer has stopped.</summary>
    public static D3D12Renderer CreateWithSessionMeshes(WindowsHostModule window, IRuntimeOwnership ownership,
        RenderMeshPair pair, D3D12DeviceSession session, TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var renderer = CreateWithMeshes(window, ownership, pair, session.Diagnostics, session.UseWarp, startupTimeout, operationTimeout);
        renderer._deviceSession = session;
        return renderer;
    }

    /// <summary>Creates a fixed mesh renderer with a private stream for native optional-copy pressure evidence.</summary>
    /// <remarks>At least one mesh must contain triangles. The stream waits for the first scene Present and accepts only an armed qualification ticket.
    /// Its copies never supply textures to the scene. This option requires the debug layer, DRED and resource snapshots.</remarks>
    public static D3D12Renderer CreateWithSessionMeshesForTextureCopyQualification(WindowsHostModule window,
        IRuntimeOwnership ownership, RenderMeshPair pair, D3D12DeviceSession session,
        D3D12TextureHandoff stream, D3D12TextureView view, TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(pair);
        if (pair.First.Mesh.Indices.IsEmpty && pair.Second.Mesh.Indices.IsEmpty)
            throw new ArgumentException("A fixed texture-copy qualification requires a drawable mesh.", nameof(pair));
        if (!view.IsValid)
            throw new ArgumentException("A fixed texture-copy qualification requires a valid view.", nameof(view));
        if (session.Diagnostics is not
            { EnableDebugLayer: true, EnableDred: true, EnableResourceSnapshots: true, TextureCopyQualification: not null })
            throw new ArgumentException("A fixed texture-copy qualification requires the native qualification diagnostics.", nameof(session));

        var renderer = CreateWithSessionMeshes(window, ownership, pair, session, startupTimeout, operationTimeout);
        stream.BindOwner(view.World, renderer.Wake, view.View.RendererEpoch, fixedView: view);
        renderer._textures = stream;
        renderer._fixedTextureQualificationView = view;
        return renderer;
    }

    /// <summary>Uses the session device for a volume renderer after the preceding renderer has stopped.</summary>
    public static D3D12Renderer CreateWithSessionVolumeTerrain(WindowsHostModule window, IRuntimeOwnership ownership,
        VolumeStreamingCoordinator terrain, ulong rendererEpoch, D3D12DeviceSession session,
        VolumeTerrainColorEvaluator? colorEvaluator = null, TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(window, ownership, terrain, rendererEpoch, session.Diagnostics, session.UseWarp,
            startupTimeout, operationTimeout, null, colorEvaluator) { _deviceSession = session };
    }
    private IRuntimeAdmissionFence? _meshRuntimeFence;
    private readonly Action<GpuDevice>? _beforeMeshInstallation;
    private readonly Action? _afterMeshInstallation;
    private readonly GpuRetirementTestHooks? _retirementHooks;
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
        _resourceDiagnostics = diagnostics.EnableResourceSnapshots ? new() : null;
        _startupTimeout = ValidateTimeout(startupTimeout ?? TimeSpan.FromSeconds(5), nameof(startupTimeout));
        _operationTimeout = ValidateTimeout(operationTimeout ?? TimeSpan.FromSeconds(2), nameof(operationTimeout));
    }

    internal D3D12Renderer(WindowsHostModule window, IRuntimeOwnership ownership, TimeSpan operationTimeout,
        Action<WindowsSwapChain, int, int> resize, bool useWarp = true, D3D12DiagnosticsOptions diagnostics = default,
        RenderMeshPair? meshes = null)
        : this(diagnostics, window, ownership, useWarp, operationTimeout: operationTimeout)
    {
        _resize = resize;
        _initialGeometry = meshes is null ? null : AsRegion(meshes);
    }

    /// <summary>Creates a renderer that uploads a fixed mesh pair during startup.</summary>
    public static D3D12Renderer CreateWithMeshes(WindowsHostModule window, IRuntimeOwnership ownership,
        RenderMeshPair pair, D3D12DiagnosticsOptions diagnostics = default, bool useWarp = false,
        TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null)
    {
        var region = AsRegion(pair);
        return new D3D12Renderer(diagnostics, window, ownership, useWarp, startupTimeout, operationTimeout)
        {
            _initialGeometry = region
        };
    }

    private D3D12Renderer(WindowsHostModule window, IRuntimeOwnership ownership, RenderMeshRegion region, bool exposePair,
        D3D12DiagnosticsOptions diagnostics, bool useWarp, TimeSpan? startupTimeout, TimeSpan? operationTimeout,
        Action<GpuDevice>? beforeMeshInstallation, Action? afterMeshInstallation,
        GpuRetirementTestHooks? retirementHooks)
        : this(diagnostics, window, ownership, useWarp, startupTimeout, operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(region);
        _initialGeometry = region;
        _admission = RendererFrameAdmission.CreateWithMeshRegionReplacement();
        var port = new MeshRegionReplacementPort(this, _admission.MeshRegionReplacement!);
        if (exposePair)
            MeshReplacement = new MeshReplacementPort(port);
        else
            MeshRegionReplacement = port;
        _beforeMeshInstallation = beforeMeshInstallation;
        _afterMeshInstallation = afterMeshInstallation;
        _retirementHooks = retirementHooks;
    }

    /// <summary>Creates a renderer with an initial pair that can be replaced at runtime.</summary>
    /// <remarks>Initialization requires a runtime stop control that exposes its admission fence.</remarks>
    public static D3D12Renderer CreateWithMeshReplacement(WindowsHostModule window, IRuntimeOwnership ownership,
        RenderMeshPair pair, D3D12DiagnosticsOptions diagnostics = default, bool useWarp = false,
        TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null)
        => new(window, ownership, AsRegion(pair), exposePair: true, diagnostics, useWarp, startupTimeout,
            operationTimeout, null, null, null);

    internal static D3D12Renderer CreateWithMeshReplacement(WindowsHostModule window, IRuntimeOwnership ownership,
        RenderMeshPair pair, Action<GpuDevice> beforeMeshInstallation, D3D12DiagnosticsOptions diagnostics = default,
        bool useWarp = false, TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null,
        Action? afterMeshInstallation = null, GpuRetirementTestHooks? retirementHooks = null)
        => new(window, ownership, AsRegion(pair), exposePair: true, diagnostics, useWarp, startupTimeout, operationTimeout,
            beforeMeshInstallation, afterMeshInstallation, retirementHooks);

    /// <summary>Creates a renderer that can replace a complete bounded region at runtime.</summary>
    /// <remarks>Initialization requires a runtime stop control that exposes its admission fence.</remarks>
    public static D3D12Renderer CreateWithMeshRegionReplacement(WindowsHostModule window, IRuntimeOwnership ownership,
        RenderMeshRegion region, D3D12DiagnosticsOptions diagnostics = default, bool useWarp = false,
        TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null)
        => new(window, ownership, region, exposePair: false, diagnostics, useWarp, startupTimeout,
            operationTimeout, null, null, null);

    internal static D3D12Renderer CreateWithMeshRegionReplacement(WindowsHostModule window, IRuntimeOwnership ownership,
        RenderMeshRegion region, Action<GpuDevice> beforeMeshInstallation, D3D12DiagnosticsOptions diagnostics = default,
        bool useWarp = false, TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null,
        Action? afterMeshInstallation = null)
        => new(window, ownership, region, exposePair: false, diagnostics, useWarp, startupTimeout,
            operationTimeout, beforeMeshInstallation, afterMeshInstallation, null);

    /// <summary>The optional mesh replacement capability, available on opted-in renderers.</summary>
    public IRenderMeshReplacement? MeshReplacement { get; }

    /// <summary>The optional bounded-region capability, available on opted-in renderers.</summary>
    public IRenderMeshRegionReplacement? MeshRegionReplacement { get; }

    public RendererSnapshot Read() => _admission.Read();

    /// <summary>Waits for the specified frame to be acknowledged while the renderer is running.</summary>
    /// <returns>The current snapshot once the sequence is reached or the renderer is not running.</returns>
    public ValueTask<RendererSnapshot> WaitForPresentedAsync(ulong sequence, CancellationToken token) =>
        _admission.WaitForPresentedAsync(sequence, token);

    /// <summary>Reads the last published native diagnostics without calling the graphics driver.</summary>
    public D3D12DiagnosticsSnapshot ReadDiagnostics() => Volatile.Read(ref _diagnostics);

    /// <summary>Attempts a coherent, driver-free read of the latest optional GPU resource snapshot.</summary>
    /// <remarks>Returns false before the first publication, when disabled, or during concurrent publication.</remarks>
    public bool TryReadResourceDiagnostics(out D3D12ResourceSnapshot snapshot)
    {
        if (_resourceDiagnostics?.TryRead(out snapshot) != true)
        {
            snapshot = default;
            return false;
        }
        if (Read().IncompleteCleanup)
            snapshot.Custody |= D3D12CustodyStatus.IncompleteCleanup;
        return true;
    }

    /// <summary>Last terminal native custody evidence, independent of optional snapshots and sink delivery.</summary>
    public D3D12CustodyStatus ReadTerminalGpuCustody()
    {
        var state = Read();
        var status = (D3D12CustodyStatus)Volatile.Read(ref _gpuFaultCustody);
        if (state.FirstFault.HasValue || state.IncompleteCleanup)
            status |= Volatile.Read(ref _observedGpu)?.ReadObservedCustody() ?? D3D12CustodyStatus.None;
        if (state.IncompleteCleanup)
            status |= D3D12CustodyStatus.IncompleteCleanup;
        return status;
    }

    private void ObserveTerminalGpuCustody(GpuDevice? gpu, int? removal)
    {
        if (gpu is null)
            return;
        var custody = gpu.ObserveTerminalCustody(removal < 0);
        Interlocked.Or(ref _gpuFaultCustody, (int)custody);
    }

    private void LatchObservedGpuCustody()
    {
        if (Volatile.Read(ref _observedGpu) is { } gpu)
            Interlocked.Or(ref _gpuFaultCustody, (int)gpu.ReadObservedCustody());
    }

    public RenderSubmissionResult TrySubmit(in RenderFrame frame)
    {
        if (_sessionOrbitTextureView.IsValid && frame.Scene is { World.IsIdentity: false })
            return RenderSubmissionResult.InvalidFrame;
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
        var meshFence = context.StopControl as IRuntimeAdmissionFence;
        var requiresAdmissionFence = _admission.MeshRegionReplacement is not null || _sessionOrbitTextureView.IsValid ||
            _fixedTextureQualificationView.IsValid;
        if (requiresAdmissionFence && meshFence is null)
            throw new ArgumentException("The mesh or texture route requires a runtime admission fence.", nameof(context));

        var timestamp = context.Clock.GetTimestamp();
        lock (_lifecycle)
        {
            _admission.MarkInitialized();
            _context = context;
            _meshRuntimeFence = requiresAdmissionFence ? meshFence : null;
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
            {
                _props?.Close();
                Finish();
            }
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
                if (!RecordIncomplete(new IncompleteCleanupException(FaultSubsystem.Host), onlyIfUnsettled: true))
                    return;
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
        _textures?.Revoke();
        CloseVolumeAdmission();
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

    private void Finish(ulong parkedSkyLookup = 0)
    {
        _textures?.Close();
        lock (_lifecycle)
        {
            _admission.BeginStop();
            _admission.MarkStopped();
            _initialGeometry = null;
            _wake?.Dispose();
            _wake = null;
            _ready.TrySetResult();
            if (parkedSkyLookup != 0)
            {
                if (Volatile.Read(ref _primary) is null && Volatile.Read(ref _incomplete) is null &&
                    Read() is { FirstFault: null, FirstCleanupFault: null, IncompleteCleanup: false })
                    _deviceSession?.ConfirmParkedSkyLookup(parkedSkyLookup);
                else
                    _deviceSession?.DiscardParkedSkyLookup(parkedSkyLookup);
            }
            _settled.TrySetResult();
        }
    }

    private void OnTimeout(RendererStage stage, ulong? sequence) =>
        Fail(stage, new TimeoutException($"The D3D12 {stage} operation exceeded its progress deadline."), null, sequence);

    private void Fail(RendererStage stage, Exception error, int? removedReason, ulong? sequence)
    {
        if (Interlocked.CompareExchange(ref _primary, ExceptionDispatchInfo.Capture(error), null) is not null)
            return;
        LatchObservedGpuCustody();

        _textures?.Revoke();
        CloseVolumeAdmission();
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

    private bool RecordIncomplete(Exception error, bool onlyIfUnsettled = false)
    {
        var incomplete = error as IncompleteCleanupException ?? new IncompleteCleanupException(
            "The renderer still owns native resources after cleanup failed.", error);
        lock (_lifecycle)
        {
            // The deadline and Finish must agree whether cleanup completed.
            // In particular, a timed-out owner cannot confirm storage.
            if (onlyIfUnsettled && _settled.Task.IsCompleted)
                return false;
            if (_incomplete is not null)
                return true;
            Volatile.Write(ref _incomplete, incomplete);
            LatchObservedGpuCustody();
            _admission.RecordCleanupFault(new RenderFaultSummary((uint)RendererStage.Cleanup, error.HResult, null, null), incomplete: true);
        }
        Report(FaultClass.IncompleteCleanup, FaultOrigin.Cleanup, error);
        return true;
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

    private static RenderMeshRegion AsRegion(RenderMeshPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return new RenderMeshRegion([pair.First, pair.Second]);
    }

    private sealed class MeshReplacementPort(MeshRegionReplacementPort port) : IRenderMeshReplacement
    {
        public MeshReplacementSubmissionResult TryDeclareMeshGeneration(ulong generation) =>
            port.TryDeclareMeshGeneration(generation);

        public MeshReplacementSubmissionResult TrySubmitMeshPair(ulong generation, RenderMeshPair? pair)
        {
            var result = port.TrySubmitMeshRegion(generation, pair is null ? null : AsRegion(pair));
            return result == MeshReplacementSubmissionResult.InvalidRegion ? MeshReplacementSubmissionResult.InvalidPair : result;
        }

        public MeshReplacementSnapshot ReadMeshReplacement() => port.ReadMeshReplacement();
    }

    private sealed class MeshRegionReplacementPort(D3D12Renderer owner, RendererFrameAdmission.MeshRegionAdmission admission)
        : IRenderMeshRegionReplacement
    {
        public MeshReplacementSubmissionResult TryDeclareMeshGeneration(ulong generation) =>
            admission.TryDeclareMeshGeneration(generation);

        public MeshReplacementSubmissionResult TrySubmitMeshRegion(ulong generation, RenderMeshRegion? region)
        {
            var result = admission.TrySubmitMeshRegion(generation, region);
            if (result == MeshReplacementSubmissionResult.Accepted)
                owner.Wake();
            return result;
        }

        public MeshReplacementSnapshot ReadMeshReplacement() => admission.ReadMeshReplacement();
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(parameterName);
        return timeout;
    }
}
