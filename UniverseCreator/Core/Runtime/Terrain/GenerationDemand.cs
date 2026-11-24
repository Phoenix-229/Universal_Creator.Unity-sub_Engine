using System;
using System.Diagnostics;

namespace UniverseCreator.TerrainSystem
{
    // UC-T02-08: immutable main-thread classification; scheduler workers compare plain values without touching a camera.
    internal readonly struct GenerationDemand
    {
        internal const double AgingSeconds = 2;
        internal readonly bool CollisionCritical;
        internal readonly int Band;
        internal readonly float Distance;
        internal readonly long QueuedAt;

        internal GenerationDemand(bool collisionCritical, int band, float distance, long queuedAt)
        {
            CollisionCritical = collisionCritical;
            Band = Math.Max(0, Math.Min(3, band));
            Distance = float.IsNaN(distance) || distance < 0 ? float.PositiveInfinity : distance;
            QueuedAt = queuedAt;
        }

        internal bool IsAged(long now) => now - QueuedAt >= AgingSeconds * Stopwatch.Frequency;
        internal static int Compare(GenerationDemand a, GenerationDemand b, long now)
        {
            if (a.CollisionCritical != b.CollisionCritical) return a.CollisionCritical ? -1 : 1;
            bool agedA = a.IsAged(now), agedB = b.IsAged(now);
            if (agedA != agedB) return agedA ? -1 : 1;
            // Once aged, FIFO wins over new visible work. Collision demand always retains its separate first band.
            if (agedA) return a.QueuedAt.CompareTo(b.QueuedAt);
            int order = a.Band.CompareTo(b.Band);
            if (order == 0) order = a.Distance.CompareTo(b.Distance);
            return order == 0 ? a.QueuedAt.CompareTo(b.QueuedAt) : order;
        }
    }
}
