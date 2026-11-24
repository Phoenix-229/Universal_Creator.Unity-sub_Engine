// UC-T04-09A: bounded contact events shared by the existing movement/drag owners; playback is a separate consumer.
using System;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Changes in accepted water contact; several changes may occur in one sample.</summary>
    [Flags]
    public enum WaterContactEventKind
    {
        /// <summary>No event this sample.</summary>
        None = 0,
        /// <summary>Entered an accepted local water column.</summary>
        Enter = 1,
        /// <summary>Left water or the contact owner was disabled.</summary>
        Exit = 2,
        /// <summary>Entry exceeded the impact-speed threshold.</summary>
        Splash = 4,
        /// <summary>Moving relative to local water; emitted at most eight times per second.</summary>
        Wake = 8,
        /// <summary>Crossed between volumes/body kinds while remaining wet.</summary>
        BodyChanged = 16
    }

    /// <summary>Accepted local water state for particles, ambience and game-specific swimming/vehicle audio.</summary>
    public readonly struct WaterContactState
    {
        /// <summary>True while inside the water column, false on exit/retirement.</summary>
        public readonly bool Wet;
        /// <summary>True when the existing character owner reports swimming or diving.</summary>
        public readonly bool Swimming;
        /// <summary>Actual accepted body kind; retained in the exit event.</summary>
        public readonly UCWaterVolume.BodyKind Kind;
        /// <summary>Contact position in world metres.</summary>
        public readonly Vector3 Position;
        /// <summary>Submergence and full local column depth in metres.</summary>
        public readonly float Depth, WaterDepth;
        /// <summary>Current in world metres per second.</summary>
        public readonly Vector3 Current;
        /// <summary>Speed relative to the local water in metres per second.</summary>
        public readonly float RelativeSpeed;
        internal WaterContactState(bool wet, bool swimming, UCWaterVolume.BodyKind kind, Vector3 position, float depth,
            float waterDepth, Vector3 current, float speed)
        { Wet = wet; Swimming = swimming; Kind = kind; Position = position; Depth = depth; WaterDepth = waterDepth; Current = current; RelativeSpeed = speed; }
    }

    /// <summary>One bounded contact notification; runtime movement owners invoke it synchronously on the main thread.</summary>
    public readonly struct WaterContactEvent
    {
        /// <summary>Enter/exit/splash/wake/body-change flags in this notification.</summary>
        public readonly WaterContactEventKind Kind;
        /// <summary>Local state after the transition, including the former body kind on exit.</summary>
        public readonly WaterContactState State;
        internal WaterContactEvent(WaterContactEventKind kind, WaterContactState state) { Kind = kind; State = state; }
    }

    internal sealed class WaterContactTracker
    {
        internal WaterContactState Current { get; private set; }
        private UCWaterVolume previousVolume;
        private float nextWake;

        internal WaterContactEvent Step(in WaterSample sample, Vector3 position, Vector3 velocity, float time, bool swimming, float splashSpeed)
        {
            if (!RiverChannelSnapshot.Finite(time) || !Finite(position) || !Finite(velocity)) return Reset();
            bool wet = sample.volume != null && sample.submerged && sample.depth > 0 && sample.waterDepth > 0 &&
                RiverChannelSnapshot.Finite(sample.depth) && RiverChannelSnapshot.Finite(sample.waterDepth) && Finite(sample.velocity);
            if (!wet) return Reset();
            float speed = (velocity - sample.velocity).magnitude;
            if (!RiverChannelSnapshot.Finite(speed)) return Reset(); // UC-T04-09A: finite coordinates can still overflow relative-speed arithmetic.
            var flags = WaterContactEventKind.None;
            if (!Current.Wet)
            {
                flags |= WaterContactEventKind.Enter;
                if (Mathf.Abs(velocity.y - sample.velocity.y) >= Mathf.Max(0.1f, splashSpeed)) flags |= WaterContactEventKind.Splash;
                nextWake = time + 0.125f;
            }
            else if (previousVolume != sample.volume || Current.Kind != sample.volume.kind) flags |= WaterContactEventKind.BodyChanged;
            if (speed >= 0.15f && time >= nextWake) { flags |= WaterContactEventKind.Wake; nextWake = time + 0.125f; }
            previousVolume = sample.volume;
            Current = new WaterContactState(true, swimming, sample.volume.kind, position, sample.depth, sample.waterDepth, sample.velocity, speed);
            return new WaterContactEvent(flags, Current);
        }

        internal WaterContactEvent Reset()
        {
            var flags = Current.Wet ? WaterContactEventKind.Exit : WaterContactEventKind.None;
            Current = new WaterContactState(false, false, Current.Kind, Current.Position, 0, 0, Vector3.zero, 0);
            previousVolume = null; nextWake = 0;
            return new WaterContactEvent(flags, Current);
        }

        private static bool Finite(Vector3 p) => RiverChannelSnapshot.Finite(p.x) && RiverChannelSnapshot.Finite(p.y) && RiverChannelSnapshot.Finite(p.z);
    }
}
