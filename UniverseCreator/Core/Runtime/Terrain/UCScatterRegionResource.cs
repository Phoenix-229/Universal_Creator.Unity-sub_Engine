// UC-T06-12: scene references survive domain reload; process tokens remain owned by the re-adopted chunk.
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Records the world, native region, accepted revision and full identity of one generated scene resource.</summary>
    [DisallowMultipleComponent, AddComponentMenu("")]
    internal sealed class UCScatterRegionResource : MonoBehaviour
    {
        [SerializeField, HideInInspector] private UniverseCreatorObject world;
        [SerializeField, HideInInspector] private Terrain region;
        [SerializeField, HideInInspector] private string worldId;
        [SerializeField, HideInInspector] private long revision;
        [SerializeField, HideInInspector] private VegetationInstanceKey identity;
        [SerializeField, HideInInspector] private bool parked, activeBeforeParking;

        internal UniverseCreatorObject World => world;
        internal Terrain Region => region;
        internal VegetationInstanceKey Identity => identity;
        internal string RuntimeWorldId => worldId;
        internal long AcceptedRevision => revision;

        internal void Bind(UniverseCreatorObject owner, Terrain nativeRegion, GenerationRunId run, VegetationInstanceKey key)
        { world = owner; region = nativeRegion; worldId = run.WorldId.ToString("N"); revision = run.Revision; identity = key; parked = false; }

        internal void SetLive(bool live)
        {
            if (!live)
            {
                if (!parked) { activeBeforeParking = gameObject.activeSelf; parked = true; }
                gameObject.SetActive(false);
            }
            else if (parked) { parked = false; gameObject.SetActive(activeBeforeParking); }
        }
    }
}
