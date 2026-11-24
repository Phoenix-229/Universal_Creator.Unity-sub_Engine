// UC-T06-08: explicit artist-authored collision parts; no filename, mesh shape or leaf-triangle guesses.
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Physical part represented by colliders below a prefab marker; unmarked hierarchies preserve authored collision.</summary>
    public enum ScatterCollisionCategory
    {
        /// <summary>Keep authored colliders under the species' existing None/Trigger/Solid policy.</summary>
        Authored = 0,
        /// <summary>Structural tree trunk collision.</summary>
        Trunk = 1,
        /// <summary>Structural major-branch collision.</summary>
        MajorBranch = 2,
        /// <summary>Solid rock/deposit collision.</summary>
        Rock = 3,
        /// <summary>Leaf cards are visual only and carry no scatter collider.</summary>
        LeafCard = 4,
        /// <summary>Grass is visual only and carries no scatter collider.</summary>
        Grass = 5,
        /// <summary>Explicitly exclude collision from this subtree.</summary>
        None = 6
    }

    /// <summary>Marks a prefab subtree's collision category; a closer child marker overrides a parent marker.</summary>
    [DisallowMultipleComponent, AddComponentMenu("Universe Creator/Scatter Collider Part")]
    public sealed class UCScatterColliderPart : MonoBehaviour
    {
        /// <summary>Serialized physical category for colliders on this transform and unmarked descendants.</summary>
        [Tooltip("Identify trunk/major branches/rocks, or noncolliding leaf cards/grass. Child markers override parent markers.")]
        public ScatterCollisionCategory category;

        internal static ScatterCollisionCategory Read(Collider collider, Transform root)
        {
            for (Transform current = collider.transform; current != null; current = current.parent)
            {
                var marker = current.GetComponent<UCScatterColliderPart>();
                if (marker != null) return marker.category;
                if (current == root) break;
            }
            return ScatterCollisionCategory.Authored;
        }

        internal static bool IsNonColliding(ScatterCollisionCategory category) => category == ScatterCollisionCategory.LeafCard ||
            category == ScatterCollisionCategory.Grass || category == ScatterCollisionCategory.None;
    }
}
