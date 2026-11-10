namespace Groundwork.Voxels;

public enum VolumeWorkClass { Baseline, Movement, FirstEdit, Recovery, Speculative }

/// <summary>Independent dimensions of one atomic reservation, including retained and retiring resources.</summary>
public readonly record struct VolumeResources(long CpuBytes, long NativeBytes, long UploadBytes, long RetirementBytes,
    int DensityJobs, int MeshJobs, int QueueSlots, int LeaseSlots, int RetirementSlots)
{
    public bool IsNonnegative => CpuBytes >= 0 && NativeBytes >= 0 && UploadBytes >= 0 && RetirementBytes >= 0 &&
        DensityJobs >= 0 && MeshJobs >= 0 && QueueSlots >= 0 && LeaseSlots >= 0 && RetirementSlots >= 0;

    public bool Fits(VolumeResources limit) => IsNonnegative && CpuBytes <= limit.CpuBytes &&
        NativeBytes <= limit.NativeBytes && UploadBytes <= limit.UploadBytes && RetirementBytes <= limit.RetirementBytes &&
        DensityJobs <= limit.DensityJobs && MeshJobs <= limit.MeshJobs && QueueSlots <= limit.QueueSlots &&
        LeaseSlots <= limit.LeaseSlots && RetirementSlots <= limit.RetirementSlots;

    public static VolumeResources operator +(VolumeResources a, VolumeResources b) => checked(new(
        a.CpuBytes + b.CpuBytes, a.NativeBytes + b.NativeBytes, a.UploadBytes + b.UploadBytes,
        a.RetirementBytes + b.RetirementBytes, a.DensityJobs + b.DensityJobs, a.MeshJobs + b.MeshJobs,
        a.QueueSlots + b.QueueSlots, a.LeaseSlots + b.LeaseSlots, a.RetirementSlots + b.RetirementSlots));

    public static VolumeResources operator -(VolumeResources a, VolumeResources b) => checked(new(
        a.CpuBytes - b.CpuBytes, a.NativeBytes - b.NativeBytes, a.UploadBytes - b.UploadBytes,
        a.RetirementBytes - b.RetirementBytes, a.DensityJobs - b.DensityJobs, a.MeshJobs - b.MeshJobs,
        a.QueueSlots - b.QueueSlots, a.LeaseSlots - b.LeaseSlots, a.RetirementSlots - b.RetirementSlots));

    public VolumeResources Retained => this with { DensityJobs = 0, MeshJobs = 0, QueueSlots = 0 };
    public static VolumeResources Peak(VolumeResources a, VolumeResources b) => new(
        Math.Max(a.CpuBytes, b.CpuBytes), Math.Max(a.NativeBytes, b.NativeBytes), Math.Max(a.UploadBytes, b.UploadBytes),
        Math.Max(a.RetirementBytes, b.RetirementBytes), Math.Max(a.DensityJobs, b.DensityJobs), Math.Max(a.MeshJobs, b.MeshJobs),
        Math.Max(a.QueueSlots, b.QueueSlots), Math.Max(a.LeaseSlots, b.LeaseSlots), Math.Max(a.RetirementSlots, b.RetirementSlots));
}

/// <summary>Disjoint envelopes. An idle protected envelope remains unavailable to speculation.</summary>
public sealed class VolumeStreamingLimits
{
    private readonly VolumeResources[] _envelopes;
    public VolumeStreamingLimits(VolumeResources total, VolumeResources baseline, VolumeResources movement,
        VolumeResources firstEdit, VolumeResources recovery, VolumeResources speculative, int candidates, int readers, int journalRecords)
    {
        _envelopes = [baseline, movement, firstEdit, recovery, speculative];
        var sum = default(VolumeResources);
        foreach (var envelope in _envelopes)
        {
            if (!envelope.IsNonnegative)
                throw new ArgumentOutOfRangeException(nameof(baseline));
            sum += envelope;
        }
        if (!sum.Fits(total))
            throw new ArgumentException("Disjoint envelopes must fit the total in every dimension.", nameof(total));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(candidates);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(candidates, 256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readers);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(readers, 256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(journalRecords);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(journalRecords, 65536);
        if (candidates < sum.RetirementSlots)
            throw new ArgumentException("Candidate storage must cover every envelope retirement slot.", nameof(candidates));
        Total = total;
        Candidates = candidates;
        Readers = readers;
        JournalRecords = journalRecords;
    }

    public VolumeResources Total { get; }
    public int Candidates { get; }
    public int Readers { get; }
    public int JournalRecords { get; }
    public VolumeResources Envelope(VolumeWorkClass kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return _envelopes[(int)kind];
    }
}
