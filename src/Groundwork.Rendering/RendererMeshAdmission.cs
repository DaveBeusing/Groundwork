using System.Diagnostics.CodeAnalysis;

namespace Groundwork.Rendering;

public sealed partial class RendererFrameAdmission
{
    /// <summary>The geometry mailbox for an opted-in provider, or null for fixed geometry.</summary>
    public MeshPairAdmission? MeshReplacement { get; private set; }

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
    /// Shares frame admission's lifecycle and lock. The single render owner owns preparation and native resources.
    /// </summary>
    public sealed class MeshPairAdmission : IRenderMeshReplacement
    {
        private readonly RendererFrameAdmission _frames;
        private MeshReplacementSnapshot _status = new(1, 0, 0);
        private ulong _acceptedGeneration = 1;
        private Candidate? _pending;
        private Candidate? _preparing;

        internal MeshPairAdmission(RendererFrameAdmission frames) => _frames = frames;

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

        public MeshReplacementSnapshot ReadMeshReplacement()
        {
            lock (_frames._gate)
            {
                return _status;
            }
        }

        public MeshReplacementSubmissionResult TryDeclareMeshGeneration(ulong generation)
        {
            lock (_frames._gate)
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

        public MeshReplacementSubmissionResult TrySubmitMeshPair(ulong generation, RenderMeshPair? pair)
        {
            lock (_frames._gate)
            {
                if (GetLifecycleRejection() is { } rejection)
                    return rejection;
                if (generation == 0)
                    return MeshReplacementSubmissionResult.InvalidGeneration;
                if (pair is null)
                    return MeshReplacementSubmissionResult.InvalidPair;
                if (generation < _status.DesiredGeneration)
                    return MeshReplacementSubmissionResult.StaleGeneration;
                if (generation > _status.DesiredGeneration)
                    return MeshReplacementSubmissionResult.UndeclaredGeneration;
                if (generation == _acceptedGeneration)
                    return MeshReplacementSubmissionResult.DuplicateGeneration;

                _pending = new Candidate(generation, pair);
                _acceptedGeneration = generation;
                return MeshReplacementSubmissionResult.Accepted;
            }
        }

        /// <summary>Takes one eligible pair for preparation when the current frame has a drawable scene.</summary>
        public bool TryTake([NotNullWhen(true)] out Candidate? candidate)
        {
            lock (_frames._gate)
            {
                if (_preparing is null && _pending is { } pending &&
                    pending.Generation == _status.DesiredGeneration && CanInstall())
                {
                    _preparing = pending;
                    _pending = null;
                    candidate = pending;
                    return true;
                }

                candidate = null;
                return false;
            }
        }

        /// <summary>
        /// Swaps two owner-held geometry handles and publishes the generation at the same admission point.
        /// On success, prepared receives the former active handle. On denial both handles stay with the owner.
        /// </summary>
        /// <remarks>
        /// Before calling, the provider proves retirement of prior work. canCommit must check its final
        /// Core, target and deadline authority without native calls, waiting or reentering admission.
        /// A true result must establish that installation won at the provider's final authority check.
        /// </remarks>
        public bool TryInstall<T>(Candidate candidate, ref T active, ref T prepared, Func<bool> canCommit)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(canCommit);
            lock (_frames._gate)
            {
                if (!ReferenceEquals(_preparing, candidate) ||
                    candidate.Generation != _status.DesiredGeneration || !CanInstall() || !canCommit())
                    return false;

                (active, prepared) = (prepared, active);
                _status = _status with { InstalledGeneration = candidate.Generation };
                _preparing = null;
                return true;
            }
        }

        /// <summary>
        /// Releases the owner slot after private preparation has settled. A still-current candidate can
        /// return to the mailbox after a transient denial without consuming another submission.
        /// </summary>
        public void ReleaseCandidate(Candidate candidate, bool retry = false)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            lock (_frames._gate)
            {
                if (!ReferenceEquals(_preparing, candidate))
                    return;

                _preparing = null;
                if (retry && _frames._status.Lifecycle == RendererLifecycle.Running &&
                    candidate.Generation == _status.DesiredGeneration && _pending is null)
                    _pending = candidate;
            }
        }

        /// <summary>Records a successful normal present of the current frame with the installed pair.</summary>
        public void RecordPresented(ulong sequence, ulong generation)
        {
            lock (_frames._gate)
            {
                if (!_frames._takenFrameHasScene || generation == 0 || generation != _status.InstalledGeneration)
                    throw new InvalidOperationException("Only a mesh scene with the installed generation may be recorded.");

                _frames.RecordPresentedCore(sequence);
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

        private bool CanInstall() => _frames._status.Lifecycle == RendererLifecycle.Running &&
            _frames._status.Surface == RenderSurfaceState.Drawable && _frames._takenFrameHasScene;

        private MeshReplacementSubmissionResult? GetLifecycleRejection() => _frames._status.Lifecycle switch
        {
            RendererLifecycle.Running => null,
            RendererLifecycle.Stopping or RendererLifecycle.Stopped => MeshReplacementSubmissionResult.Closed,
            _ => MeshReplacementSubmissionResult.NotRunning
        };
    }
}
