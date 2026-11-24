// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    StopToken.cs
//  Purpose: Cooperative cancellation token passed to every Generate() call so
//           long-running nodes (erosion, scatter) can bail out mid-work when
//           the user stops generation or changes parameters.
// -----------------------------------------------------------------------------

using System;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// Lightweight cancellation token. The async generation loop sets stopRequested
    /// to true when the user aborts, and every Generator checks it at convenient
    /// boundaries (per-iteration, per-chunk, etc.).
    /// </summary>
    public class StopToken
    {
        private volatile bool stopRequested;
        // UC-T02-01: a local chunk stop must also observe cancellation of its owning world revision.
        private readonly StopToken parent;
        private readonly StopToken otherParent; // UC-T02-03: a shared solve can observe both cache lifetime and scheduling revision.

        /// <summary>Creates an independent cooperative cancellation token.</summary>
        public StopToken() { }

        /// <summary>Creates a local token that also observes an owning run's cancellation.</summary>
        /// <param name="parent">Owning token; null creates an independent token. Existing parent chains are immutable.</param>
        public StopToken(StopToken parent) { this.parent = parent; }

        internal StopToken(StopToken parent, StopToken otherParent) { this.parent = parent; this.otherParent = otherParent; }

        /// <summary>UC-T02-01: true when this token or its owning run requested cancellation.</summary>
        public bool StopRequested { get { return stopRequested || (parent != null && parent.StopRequested) || (otherParent != null && otherParent.StopRequested); } }

        public void RequestStop()
        {
            stopRequested = true;
        }

        /// <summary>Clears only the local stop flag; UC-T02-01 parent cancellation cannot be reset by a child.</summary>
        public void Reset()
        {
            stopRequested = false;
        }

        /// <summary>
        /// Throws OperationCanceledException if stop was requested. Nodes that want
        /// immediate bail-out can call this instead of checking the flag manually.
        /// </summary>
        public void ThrowIfStopRequested()
        {
            if (StopRequested)
                throw new OperationCanceledException("Generation stopped by user.");
        }
    }
}
