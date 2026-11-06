namespace Groundwork.Rendering.D3D12;

internal struct GpuRetirement
{
    private bool _awaitingSignal;

    internal ulong Submitted { get; private set; }

    internal bool CanReuse(ulong completed, ulong retirement)
        => !_awaitingSignal && completed != ulong.MaxValue && completed >= retirement;

    internal bool IsIdle(ulong completed) => CanReuse(completed, Submitted);

    internal ulong BeginSubmission()
    {
        if (_awaitingSignal)
            throw new InvalidOperationException("A GPU submission could not be assigned a retirement fence.");
        if (Submitted == ulong.MaxValue - 1)
            throw new InvalidOperationException("The GPU retirement fence is exhausted.");

        // Once native submission starts, a failed Signal must prevent reuse.
        _awaitingSignal = true;
        return Submitted + 1;
    }

    internal void CompleteSignal(ulong value)
    {
        Submitted = value;
        _awaitingSignal = false;
    }
}
