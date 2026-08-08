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

        public bool StopRequested { get { return stopRequested; } }

        public void RequestStop()
        {
            stopRequested = true;
        }

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
            if (stopRequested)
                throw new OperationCanceledException("Generation stopped by user.");
        }
    }
}
