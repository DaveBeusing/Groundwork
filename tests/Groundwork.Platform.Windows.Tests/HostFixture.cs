using Groundwork.Core;

namespace Groundwork.Platform.Windows.Tests;

internal sealed class HostFixture : IAsyncDisposable
{
    private readonly RuntimeTimingLimits _timing;

    internal HostFixture(WindowsHostOptions? options = null, IRuntimeOwnership? ownership = null,
        TimeProvider? clock = null, TimeSpan? shutdown = null)
    {
        _timing = new RuntimeTimingLimits(shutdown ?? TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Clock = clock ?? TimeProvider.System;
        var quota = new ResourceQuota(1, 8, new JobQuota(2, 1, 2, 2, 2, 2, 2, 4096, 4096, 4096),
            new DiagnosticQuota(4, 8192, 12, 32768, 4, 4, 64, 1024));
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, quota));
        using var reservation = budget.TryReserveRuntime(quota)!;
        Runtime = RuntimeSafetyAuthority.Create(Clock, _timing, reservation);
        Options = options ?? new WindowsHostOptions($"Groundwork L1 {Guid.NewGuid():N}", 400, 300, 256);
        Host = new WindowsHostModule(Options, Runtime.UiWake, ownership ?? Runtime.RuntimeOwnership);
    }

    internal WindowsHostOptions Options { get; }
    internal RuntimeSafetyAuthority Runtime { get; }
    internal WindowsHostModule Host { get; }
    private TimeProvider Clock { get; }

    internal ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => Host.InitializeAsync(new EngineModuleContext(Clock, _timing, Runtime.Reporter, Runtime.StopControl),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Host.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            Runtime.SettleStop();
        }
    }
}
