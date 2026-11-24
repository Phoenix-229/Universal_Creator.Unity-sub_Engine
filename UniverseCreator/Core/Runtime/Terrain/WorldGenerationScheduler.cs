using System;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>UC-T02-01: identifies a world's runtime lifetime and one monotonically advancing generation revision.</summary>
    /// <remarks>Default is unowned. A new component/domain lifetime gets a new world ID, so old work cannot target a replacement.</remarks>
    public readonly struct GenerationRunId : IEquatable<GenerationRunId>
    {
        /// <summary>Unique runtime world identity, independent of a shared graph asset.</summary>
        public Guid WorldId { get; }
        /// <summary>Positive world revision; zero denotes an unowned product.</summary>
        public long Revision { get; }
        internal GenerationRunId(Guid worldId, long revision) { WorldId = worldId; Revision = revision; }
        /// <summary>True for an assigned world/revision pair.</summary>
        public bool IsAssigned => WorldId != Guid.Empty && Revision > 0;
        /// <inheritdoc />
        public bool Equals(GenerationRunId other) => WorldId == other.WorldId && Revision == other.Revision;
        /// <inheritdoc />
        public override bool Equals(object obj) => obj is GenerationRunId other && Equals(other);
        /// <inheritdoc />
        public override int GetHashCode() => unchecked(WorldId.GetHashCode() * 397 ^ Revision.GetHashCode());
        /// <inheritdoc />
        public override string ToString() => WorldId.ToString("N") + ":" + Revision;
    }

    /// <summary>UC-T02-01: shared publication authority for a world's terrain, materials, vegetation and environment.</summary>
    /// <remarks>Begin/cancel on the main thread. Workers may query identity and publish managed results.
    /// Publication callbacks run under the authority lock; keep them short and never wait for another thread in one.</remarks>
    public sealed partial class WorldGenerationScheduler
    {
        private readonly object sync = new object();
        private readonly Guid worldId = Guid.NewGuid();
        private long revision = 1;
        private StopToken stop = new StopToken();

        /// <summary>UC-T02-09: this world's optional bounded generation diagnostics; disabled by default.</summary>
        public UCGenerationMetrics Metrics { get; } = new UCGenerationMetrics();

        /// <summary>Current identity, including a cancelled revision; reading it does not start new work.</summary>
        public GenerationRunId CurrentRun { get { lock (sync) return new GenerationRunId(worldId, revision); } }

        /// <summary>Starts a revision and cooperatively cancels every domain of its predecessor.</summary>
        /// <returns>The new identity to capture into workers and all deferred publications.</returns>
        /// <exception cref="OverflowException">The revision counter is exhausted; existing identity is not reused.</exception>
        public GenerationRunId BeginRun()
        {
            GenerationRunId result;
            lock (sync)
            {
                long next = checked(revision + 1);
                stop.RequestStop();
                revision = next;
                stop = new StopToken();
                result = new GenerationRunId(worldId, revision);
            }
            PumpWorkers(); // UC-T02-02: retire queued predecessors without giving back running predecessors' slots.
            return result;
        }

        /// <summary>Captures the current usable revision, starting one only if the previous revision was cancelled.</summary>
        /// <returns>Identity for synchronous material work or streaming that joins the world's existing revision.</returns>
        public GenerationRunId CaptureRun()
        {
            lock (sync) return stop.StopRequested ? BeginRun() : new GenerationRunId(worldId, revision);
        }

        /// <summary>Tests whether a product may publish into this world.</summary>
        /// <param name="run">Captured identity; default, foreign and cancelled identities are rejected.</param>
        /// <returns>True only for the current uncancelled revision.</returns>
        public bool IsCurrent(GenerationRunId run)
        {
            lock (sync) return run.WorldId == worldId && run.Revision == revision && !stop.StopRequested;
        }

        /// <summary>Creates a local cancellation token linked to the captured world revision.</summary>
        /// <param name="run">Revision to join; stale identities receive an already-cancelled token.</param>
        /// <returns>Independent local stop request; cancelling the world also cancels this token.</returns>
        public StopToken CreateStopToken(GenerationRunId run)
        {
            lock (sync)
            {
                var local = new StopToken(stop);
                if (!IsCurrent(run)) local.RequestStop();
                return local;
            }
        }

        /// <summary>Cancels only the specified revision without cancelling a newer replacement.</summary>
        /// <param name="run">Captured identity; a stale/foreign/default identity has no effect.</param>
        public void Cancel(GenerationRunId run)
        {
            lock (sync) if (run.WorldId == worldId && run.Revision == revision) stop.RequestStop();
            PumpWorkers(); // UC-T02-02: cancellation retires queued work; live workers release only in their final exit path.
        }

        /// <summary>Atomically rejects stale work or publishes one managed result for the current revision.</summary>
        /// <param name="run">Identity captured before computation.</param>
        /// <param name="publish">Short callback; Unity work is allowed only when called on the main thread.</param>
        /// <returns>False without invoking the callback if the revision is obsolete.</returns>
        /// <exception cref="ArgumentNullException">Callback is null.</exception>
        /// <remarks>Callback exceptions propagate; the scheduler never hides failed publication.</remarks>
        public bool TryPublish(GenerationRunId run, Action publish)
        {
            if (publish == null) throw new ArgumentNullException(nameof(publish));
            lock (sync)
            {
                if (!IsCurrent(run)) return false;
                publish();
                return true;
            }
        }
    }
}
