using System.Diagnostics.CodeAnalysis;

namespace Groundwork.Rendering;

public sealed partial class RendererFrameAdmission
{
    /// <summary>The geometry mailbox for an opted-in provider, or null otherwise.</summary>
    public MeshPairAdmission? MeshReplacement { get; private set; }

    /// <summary>The region geometry mailbox for an opted-in provider, or null otherwise.</summary>
    public MeshRegionAdmission? MeshRegionReplacement { get; private set; }

    /// <summary>
    /// Creates admission for a provider that installs an initial pair as generation 1 before calling MarkRunning.
    /// The provider owns initial preparation and signals its render wake on successful mesh submission.
    /// </summary>
    public static RendererFrameAdmission CreateWithMeshReplacement()
    {
        var admission = new RendererFrameAdmission();
        admission.MeshReplacement = new MeshPairAdmission(admission);
        return admission;
    }

    /// <summary>
    /// Creates admission for a provider that installs an initial region as generation 1 before calling MarkRunning.
    /// The provider owns initial preparation and signals its render wake on successful mesh submission.
    /// </summary>
    public static RendererFrameAdmission CreateWithMeshRegionReplacement()
    {
        var admission = new RendererFrameAdmission();
        admission.MeshRegionReplacement = new MeshRegionAdmission(admission);
        return admission;
    }

    /// <summary>
    /// Shares frame admission's lifecycle and lock. The single render owner owns preparation and native resources.
    /// </summary>
    public sealed class MeshPairAdmission : IRenderMeshReplacement
    {
        private readonly MeshAdmission<RenderMeshPair, Candidate> _geometry;

        internal MeshPairAdmission(RendererFrameAdmission frames) =>
            _geometry = new(frames, static (generation, pair) => new Candidate(generation, pair),
                MeshReplacementSubmissionResult.InvalidPair);

        /// <summary>An immutable pair taken by the single owner for preparation.</summary>
        public sealed class Candidate
        {
            internal Candidate(ulong generation, RenderMeshPair pair)
            {
                Generation = generation;
                Pair = pair;
            }

            public ulong Generation { get; }
            public RenderMeshPair Pair { get; }
        }

        public MeshReplacementSnapshot ReadMeshReplacement() => _geometry.Read();

        public MeshReplacementSubmissionResult TryDeclareMeshGeneration(ulong generation) =>
            _geometry.TryDeclare(generation);

        public MeshReplacementSubmissionResult TrySubmitMeshPair(ulong generation, RenderMeshPair? pair) =>
            _geometry.TrySubmit(generation, pair);

        /// <summary>Takes one eligible pair for preparation when the current frame has a drawable scene.</summary>
        public bool TryTake([NotNullWhen(true)] out Candidate? candidate) => _geometry.TryTake(out candidate);

        /// <summary>
        /// Swaps owner-held geometry handles and publishes the generation at the same admission point.
        /// On success, prepared receives the former active handle. On denial both handles stay with the owner.
        /// </summary>
        /// <remarks>
        /// Before calling, the provider proves retirement of prior work. canCommit must check its final
        /// Core, target and deadline authority without native calls, waiting or reentering admission.
        /// A true result must establish that installation won at the provider's final authority check.
        /// </remarks>
        public bool TryInstall<T>(Candidate candidate, ref T active, ref T prepared, Func<bool> canCommit) =>
            _geometry.TryInstall(candidate, ref active, ref prepared, canCommit);

        /// <summary>
        /// Releases the owner slot after private preparation has settled. A still-current candidate can
        /// return to the mailbox after a transient denial without consuming another submission.
        /// </summary>
        public void ReleaseCandidate(Candidate candidate, bool retry = false) =>
            _geometry.ReleaseCandidate(candidate, retry);

        /// <summary>Records a successful normal present of the current frame with the installed pair.</summary>
        public void RecordPresented(ulong sequence, ulong generation) => _geometry.RecordPresented(sequence, generation);

        internal void MarkRunning() => _geometry.MarkRunning();
        internal void MarkStopped() => _geometry.MarkStopped();
        internal void Close() => _geometry.Close();
    }

    /// <summary>
    /// Shares frame admission's lifecycle and lock. The single render owner owns preparation and native resources.
    /// </summary>
    public sealed class MeshRegionAdmission : IRenderMeshRegionReplacement
    {
        private readonly MeshAdmission<RenderMeshRegion, Candidate> _geometry;

        internal MeshRegionAdmission(RendererFrameAdmission frames) =>
            _geometry = new(frames, static (generation, region) => new Candidate(generation, region),
                MeshReplacementSubmissionResult.InvalidRegion);

        /// <summary>An immutable region taken by the single owner for preparation.</summary>
        public sealed class Candidate
        {
            internal Candidate(ulong generation, RenderMeshRegion region)
            {
                Generation = generation;
                Region = region;
            }

            public ulong Generation { get; }
            public RenderMeshRegion Region { get; }
        }

        public MeshReplacementSnapshot ReadMeshReplacement() => _geometry.Read();

        public MeshReplacementSubmissionResult TryDeclareMeshGeneration(ulong generation) =>
            _geometry.TryDeclare(generation);

        public MeshReplacementSubmissionResult TrySubmitMeshRegion(ulong generation, RenderMeshRegion? region) =>
            _geometry.TrySubmit(generation, region);

        /// <summary>Takes one eligible region for preparation when the current frame has a drawable scene.</summary>
        public bool TryTake([NotNullWhen(true)] out Candidate? candidate) => _geometry.TryTake(out candidate);

        /// <summary>
        /// Swaps owner-held geometry handles and publishes the generation at the same admission point.
        /// On success, prepared receives the former active handle. On denial both handles stay with the owner.
        /// </summary>
        /// <remarks>
        /// Before calling, the provider proves retirement of prior work. canCommit must check its final
        /// Core, target and deadline authority without native calls, waiting or reentering admission.
        /// A true result must establish that installation won at the provider's final authority check.
        /// </remarks>
        public bool TryInstall<T>(Candidate candidate, ref T active, ref T prepared, Func<bool> canCommit) =>
            _geometry.TryInstall(candidate, ref active, ref prepared, canCommit);

        /// <summary>
        /// Releases the owner slot after private preparation has settled. A still-current candidate can
        /// return to the mailbox after a transient denial without consuming another submission.
        /// </summary>
        public void ReleaseCandidate(Candidate candidate, bool retry = false) =>
            _geometry.ReleaseCandidate(candidate, retry);

        /// <summary>Records a successful normal present of the current frame with the installed region.</summary>
        public void RecordPresented(ulong sequence, ulong generation) => _geometry.RecordPresented(sequence, generation);

        internal void MarkRunning() => _geometry.MarkRunning();
        internal void MarkStopped() => _geometry.MarkStopped();
        internal void Close() => _geometry.Close();
    }

    private sealed class MeshAdmission<TGeometry, TCandidate>(
        RendererFrameAdmission frames, Func<ulong, TGeometry, TCandidate> createCandidate,
        MeshReplacementSubmissionResult invalidGeometry)
        where TGeometry : class
        where TCandidate : class
    {
        private readonly record struct Slot(ulong Generation, TCandidate Candidate);

        private MeshReplacementSnapshot _status = new(1, 0, 0);
        private ulong _acceptedGeneration = 1;
        private Slot? _pending;
        private Slot? _preparing;

        internal MeshReplacementSnapshot Read()
        {
            lock (frames._gate)
            {
                return _status;
            }
        }

        internal MeshReplacementSubmissionResult TryDeclare(ulong generation)
        {
            lock (frames._gate)
            {
                if (GetLifecycleRejection() is { } rejection)
                    return rejection;
                if (generation == 0)
                    return MeshReplacementSubmissionResult.InvalidGeneration;
                if (generation < _status.DesiredGeneration)
                    return MeshReplacementSubmissionResult.StaleGeneration;
                if (generation == _status.DesiredGeneration)
                    return MeshReplacementSubmissionResult.DuplicateGeneration;

                _status = _status with { DesiredGeneration = generation };
                _pending = null;
                return MeshReplacementSubmissionResult.Accepted;
            }
        }

        internal MeshReplacementSubmissionResult TrySubmit(ulong generation, TGeometry? geometry)
        {
            lock (frames._gate)
            {
                if (GetLifecycleRejection() is { } rejection)
                    return rejection;
                if (generation == 0)
                    return MeshReplacementSubmissionResult.InvalidGeneration;
                if (geometry is null)
                    return invalidGeometry;
                if (generation < _status.DesiredGeneration)
                    return MeshReplacementSubmissionResult.StaleGeneration;
                if (generation > _status.DesiredGeneration)
                    return MeshReplacementSubmissionResult.UndeclaredGeneration;
                if (generation == _acceptedGeneration)
                    return MeshReplacementSubmissionResult.DuplicateGeneration;

                _pending = new Slot(generation, createCandidate(generation, geometry));
                _acceptedGeneration = generation;
                return MeshReplacementSubmissionResult.Accepted;
            }
        }

        internal bool TryTake([NotNullWhen(true)] out TCandidate? candidate)
        {
            lock (frames._gate)
            {
                if (_preparing is null && _pending is { } pending &&
                    pending.Generation == _status.DesiredGeneration && CanInstall())
                {
                    _preparing = pending;
                    _pending = null;
                    candidate = pending.Candidate;
                    return true;
                }

                candidate = null;
                return false;
            }
        }

        internal bool TryInstall<T>(TCandidate candidate, ref T active, ref T prepared, Func<bool> canCommit)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(canCommit);
            lock (frames._gate)
            {
                if (_preparing is not { } preparing || !ReferenceEquals(preparing.Candidate, candidate) ||
                    preparing.Generation != _status.DesiredGeneration || !CanInstall() || !canCommit())
                    return false;

                (active, prepared) = (prepared, active);
                _status = _status with { InstalledGeneration = preparing.Generation };
                _preparing = null;
                return true;
            }
        }

        internal void ReleaseCandidate(TCandidate candidate, bool retry)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            lock (frames._gate)
            {
                if (_preparing is not { } preparing || !ReferenceEquals(preparing.Candidate, candidate))
                    return;

                _preparing = null;
                if (retry && frames._status.Lifecycle == RendererLifecycle.Running &&
                    preparing.Generation == _status.DesiredGeneration && _pending is null)
                    _pending = preparing;
            }
        }

        internal void RecordPresented(ulong sequence, ulong generation)
        {
            lock (frames._gate)
            {
                if (!frames._takenFrameHasScene || generation == 0 || generation != _status.InstalledGeneration)
                    throw new InvalidOperationException("Only a mesh scene with the installed generation may be recorded.");

                frames.RecordPresentedCore(sequence);
                _status = _status with { LastPresentedGeneration = generation };
            }
        }

        // The enclosing frame admission holds its gate for these lifecycle transitions.
        internal void MarkRunning() => _status = _status with { InstalledGeneration = 1 };
        internal void MarkStopped() => _status = _status with { InstalledGeneration = 0 };

        internal void Close()
        {
            _pending = null;
            _preparing = null;
        }

        private bool CanInstall() => frames._status.Lifecycle == RendererLifecycle.Running &&
            frames._status.Surface == RenderSurfaceState.Drawable && frames._takenFrameHasScene;

        private MeshReplacementSubmissionResult? GetLifecycleRejection() => frames._status.Lifecycle switch
        {
            RendererLifecycle.Running => null,
            RendererLifecycle.Stopping or RendererLifecycle.Stopped => MeshReplacementSubmissionResult.Closed,
            _ => MeshReplacementSubmissionResult.NotRunning
        };
    }
}
