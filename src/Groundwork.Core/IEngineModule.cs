namespace Groundwork.Core;

public interface IEngineModule : IAsyncDisposable
{
    ValueTask InitializeAsync(EngineModuleContext context, CancellationToken cancellationToken);

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

public sealed record EngineModuleContext(
    TimeProvider Clock,
    RuntimeTimingLimits Timing,
    IFaultReporter FaultReporter,
    IRuntimeStopControl StopControl);

public enum HostStopReason
{
    WindowClose = 1,
    ExternalCancellation,
    StartupFault,
    RuntimeFault
}
