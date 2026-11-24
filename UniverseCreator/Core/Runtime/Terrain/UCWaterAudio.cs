// UC-T04-09A: optional licensed contact audio; existing character/hull owners remain authoritative.
using System;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Optional owner-supplied licensed clips for one water kind; missing clips produce silence.</summary>
    [Serializable]
    public sealed class WaterAudioClips
    {
        /// <summary>Water kind matched by this profile; first matching profile wins.</summary>
        public UCWaterVolume.BodyKind kind = UCWaterVolume.BodyKind.River;
        /// <summary>Spatial loop for ordinary wet contact; null disables this loop.</summary>
        public AudioClip ambience;
        /// <summary>Optional stronger-current loop, selected above the component's rapid-speed threshold in metres per second.</summary>
        public AudioClip rapids;
        /// <summary>Optional swimming/diving loop; falls back to ambience when absent.</summary>
        public AudioClip swimming;
        /// <summary>Optional moving-hull loop; falls back to ambience when absent.</summary>
        public AudioClip vehicleWake;
        /// <summary>Entry clip for a gentle contact, or null for silence.</summary>
        public AudioClip entry;
        /// <summary>Impact entry clip; falls back to entry when absent.</summary>
        public AudioClip splash;
        /// <summary>Exit clip, or null for silence.</summary>
        public AudioClip exit;
    }

    /// <summary>Plays bounded spatial audio from accepted character or buoyancy contacts without querying or moving water.</summary>
    /// <remarks>Attach beside UCWaterCharacter or UCBuoyancy. Uses two owned sources per audible actor and admits at most 16 actors process-wide.
    /// Assign licensed clips in the inspector; no clips are bundled. Playback is main-thread Play mode only. Disable/unload unsubscribes and stops both voices.</remarks>
    [DisallowMultipleComponent, AddComponentMenu("Universe Creator/Water Contact Audio")]
    public sealed class UCWaterAudio : MonoBehaviour
    {
        /// <summary>Per-kind clip choices; first matching entry wins. Empty entries fall back to fallbackClips.</summary>
        [Tooltip("Optional licensed clips by water kind. First matching profile wins; no sound files are downloaded or generated.")]
        public WaterAudioClips[] bodyClips = Array.Empty<WaterAudioClips>();
        /// <summary>Fallback clips used when no per-kind profile matches; null means silence.</summary>
        [Tooltip("Optional fallback licensed clips for lake, river, ocean, pool or swamp contacts.")]
        public WaterAudioClips fallbackClips;
        /// <summary>Master volume in 0..1; nonfinite inputs fall back to 0.5.</summary>
        [Range(0, 1)] public float volume = 0.5f;
        /// <summary>Spatial attenuation range in metres, 1..100; nonfinite inputs fall back to 30.</summary>
        [Tooltip("Maximum audible distance in metres. Sources use logarithmic distance attenuation.")]
        [Range(1, 100)] public float maxDistanceMetres = 30;
        /// <summary>Current speed selecting an optional rapids loop in metres per second, 0.1..20.</summary>
        [Tooltip("Current speed in metres per second at which the optional rapids loop replaces ambience.")]
        [Range(0.1f, 20)] public float rapidsSpeedMetresPerSecond = 1.5f;

        private UCWaterCharacter character;
        private UCBuoyancy buoyancy;
        private AudioSource loopSource, transitionSource;
        private GameObject sourceObject;
        private bool admitted;
        private float configuredDistance = -1;
        private float nextTransition;
        private static int audibleActors;
        internal const int MaxAudibleActors = 16;
        internal static int AudibleActors => audibleActors;

        private void OnEnable()
        {
            character = GetComponent<UCWaterCharacter>(); buoyancy = GetComponent<UCBuoyancy>();
            // One subscriber per actor. If a character exists it owns movement; hull contacts remain for rigidbody actors.
            if (character != null) character.ContactChanged += ContactChanged;
            else if (buoyancy != null) buoyancy.ContactChanged += ContactChanged;
            nextTransition = 0;
        }

        private void OnDisable()
        {
            if (character != null) character.ContactChanged -= ContactChanged;
            else if (buoyancy != null) buoyancy.ContactChanged -= ContactChanged;
            Retire();
        }

        private void OnDestroy()
        {
            Retire();
            if (sourceObject != null)
            {
                if (Application.isPlaying) Destroy(sourceObject); else DestroyImmediate(sourceObject);
            }
        }

        private void LateUpdate()
        {
            var contact = character != null && character.isActiveAndEnabled ? character.Contact
                : character == null && buoyancy != null && buoyancy.isActiveAndEnabled ? buoyancy.Contact : default;
            RefreshContact(contact, Time.time);
        }

        private WaterAudioClips Clips(UCWaterVolume.BodyKind kind)
        {
            if (bodyClips != null)
                for (int i = 0; i < Mathf.Min(bodyClips.Length, 32); i++)
                    if (bodyClips[i] != null && bodyClips[i].kind == kind) return bodyClips[i];
            return fallbackClips;
        }

        internal AudioClip LoopFor(in WaterContactState state)
        {
            if (!state.Wet) return null;
            var clips = Clips(state.Kind);
            if (clips == null) return null;
            if (state.Swimming && clips.swimming != null) return clips.swimming;
            if (buoyancy != null && state.RelativeSpeed >= 0.15f && clips.vehicleWake != null) return clips.vehicleWake;
            float threshold = FiniteClamp(rapidsSpeedMetresPerSecond, 0.1f, 20, 1.5f);
            return state.Current.magnitude >= threshold && clips.rapids != null ? clips.rapids : clips.ambience;
        }

        // Same path used by LateUpdate and deterministic headless routing/retirement fixtures; Play alone starts voices.
        internal void RefreshContact(in WaterContactState state, float time)
        {
            var clip = LoopFor(state);
            if (!state.Wet || !RiverChannelSnapshot.Finite(time))
            {
                if (loopSource != null) { loopSource.Stop(); loopSource.clip = null; }
                if (transitionSource == null || !transitionSource.isPlaying) Retire();
                return;
            }
            if (clip == null)
            {
                if (loopSource != null) { loopSource.Stop(); loopSource.clip = null; }
                if (transitionSource == null || !transitionSource.isPlaying) Retire();
                return;
            }
            if (!Admit()) return;
            ConfigureIfChanged();
            sourceObject.transform.position = state.Position;
            loopSource.volume = FiniteClamp(volume, 0, 1, 0.5f) * Mathf.Clamp01(0.25f + state.RelativeSpeed * 0.15f + state.Current.magnitude * 0.1f);
            if (loopSource.clip != clip) { loopSource.Stop(); loopSource.clip = clip; }
            if (Application.isPlaying && !loopSource.isPlaying) loopSource.Play();
        }

        private void ContactChanged(WaterContactEvent change)
        {
            if (character != null ? !character.isActiveAndEnabled : buoyancy == null || !buoyancy.isActiveAndEnabled)
            { Retire(); return; } // UC-T04-09A: lifecycle exits stop voices rather than starting audio on a retired movement owner.
            ReceiveContact(change, Time.time);
        }

        internal void ReceiveContact(in WaterContactEvent change, float time)
        {
            var clips = Clips(change.State.Kind);
            AudioClip clip = null;
            if (clips != null)
            {
                if ((change.Kind & WaterContactEventKind.Exit) != 0) clip = clips.exit;
                else if ((change.Kind & WaterContactEventKind.Splash) != 0) clip = clips.splash != null ? clips.splash : clips.entry;
                else if ((change.Kind & WaterContactEventKind.Enter) != 0) clip = clips.entry;
            }
            RefreshContact(change.State, time);
            if (clip == null || !RiverChannelSnapshot.Finite(time) || time < nextTransition || !Admit()) return;
            nextTransition = time + 0.125f; // Combined splash/entry selects one clip, never overlapping PlayOneShot voices.
            ConfigureIfChanged(); sourceObject.transform.position = change.State.Position;
            transitionSource.Stop(); transitionSource.clip = clip;
            transitionSource.volume = FiniteClamp(volume, 0, 1, 0.5f);
            if (Application.isPlaying) transitionSource.Play();
        }

        private bool Admit()
        {
            if (admitted) return true;
            if (audibleActors >= MaxAudibleActors) return false;
            if (sourceObject == null)
            {
                sourceObject = new GameObject("UC Water Contact Voices") { hideFlags = HideFlags.DontSave };
                sourceObject.transform.SetParent(transform, false);
                loopSource = sourceObject.AddComponent<AudioSource>(); loopSource.loop = true;
                transitionSource = sourceObject.AddComponent<AudioSource>();
                Configure(loopSource); Configure(transitionSource);
                configuredDistance = FiniteClamp(maxDistanceMetres, 1, 100, 30);
            }
            audibleActors++; admitted = true;
            return true;
        }

        private void Configure(AudioSource source)
        {
            source.playOnAwake = false; source.spatialBlend = 1; source.dopplerLevel = 0;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.maxDistance = FiniteClamp(maxDistanceMetres, 1, 100, 30);
            source.minDistance = Mathf.Min(2, source.maxDistance * 0.1f);
        }

        private void ConfigureIfChanged()
        {
            float distance = FiniteClamp(maxDistanceMetres, 1, 100, 30);
            if (configuredDistance == distance) return; // UC-T04-09A: stable controls do not repeat native source configuration each frame.
            Configure(loopSource); Configure(transitionSource); configuredDistance = distance;
        }

        private void Retire()
        {
            if (loopSource != null) { loopSource.Stop(); loopSource.clip = null; }
            if (transitionSource != null) { transitionSource.Stop(); transitionSource.clip = null; }
            if (admitted) { audibleActors--; admitted = false; }
        }

        private static float FiniteClamp(float value, float min, float max, float fallback) =>
            RiverChannelSnapshot.Finite(value) ? Mathf.Clamp(value, min, max) : fallback;
    }
}
