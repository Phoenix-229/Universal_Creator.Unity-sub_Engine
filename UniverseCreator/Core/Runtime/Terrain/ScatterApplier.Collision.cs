// UC-T06-08: reuse authored structural colliders; visual parts never receive generated mesh collision.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Ecology;

namespace UniverseCreator.TerrainSystem
{
    public static partial class ScatterApplier
    {
        // UC-T06-08: bounded, main-thread commit cache; no prefab references survive the commit or enter Update.
        internal sealed partial class FinalGroundContext
        {
            private readonly Dictionary<GameObject, ColliderTemplate> collisionTemplates = new Dictionary<GameObject, ColliderTemplate>();

            internal int ApplyCollisionParts(GameObject prefab, GameObject instance, ColliderPolicy policy)
            {
                if (policy == ColliderPolicy.None) return ApplyColliderParts(instance, policy);
                if (!collisionTemplates.TryGetValue(prefab, out var template))
                {
                    int remaining = 4096;
                    template = new ColliderTemplate(prefab.transform, ScatterCollisionCategory.Authored, 0, ref remaining);
                    collisionTemplates.Add(prefab, template);
                }
                // Awake may add/remove hierarchy children; preserve actual runtime routing in that uncommon case.
                if (!template.Matches(instance.transform)) return ApplyColliderParts(instance, policy);
                var scratch = new List<Collider>();
                return template.Apply(instance.transform, policy, scratch);
            }
        }

        private sealed class ColliderTemplate
        {
            private readonly ScatterCollisionCategory category;
            private readonly ColliderTemplate[] children;

            internal ColliderTemplate(Transform source, ScatterCollisionCategory inherited, int depth, ref int remaining)
            {
                if (depth > 128 || --remaining < 0)
                    throw new InvalidOperationException("Scatter collider template exceeds 4,096 transforms or 128 hierarchy levels.");
                var marker = source.GetComponent<UCScatterColliderPart>();
                category = marker != null ? marker.category : inherited;
                children = new ColliderTemplate[source.childCount];
                for (int i = 0; i < children.Length; i++) children[i] = new ColliderTemplate(source.GetChild(i), category, depth + 1, ref remaining);
            }

            internal bool Matches(Transform instance)
            {
                if (instance.childCount != children.Length) return false;
                for (int i = 0; i < children.Length; i++) if (!children[i].Matches(instance.GetChild(i))) return false;
                return true;
            }

            internal int Apply(Transform instance, ColliderPolicy policy, List<Collider> scratch)
            {
                int retained = 0;
                instance.GetComponents(scratch);
                bool excluded = (int)category < 0 || (int)category > (int)ScatterCollisionCategory.None || UCScatterColliderPart.IsNonColliding(category);
                foreach (var collider in scratch)
                {
                    if (excluded)
                    {
                        collider.enabled = false;
                        if (Application.isPlaying) UnityEngine.Object.Destroy(collider);
                        else UnityEngine.Object.DestroyImmediate(collider);
                    }
                    else { collider.isTrigger = policy == ColliderPolicy.Trigger; retained++; }
                }
                for (int i = 0; i < children.Length; i++) retained += children[i].Apply(instance.GetChild(i), policy, scratch);
                return retained;
            }
        }

        // Main-thread, once per unique tree prototype per registration; the borrowed prefab stays untouched.
        private static void ValidateTreeCollisionParts(GameObject prefab)
        {
            foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
            {
                var category = UCScatterColliderPart.Read(collider, prefab.transform);
                if ((int)category < 0 || (int)category > (int)ScatterCollisionCategory.None || UCScatterColliderPart.IsNonColliding(category))
                    throw new InvalidOperationException("Native tree prototype '" + prefab.name +
                        "' has collision on a leaf/grass/excluded or unsupported part. Remove that authored collider; retain trunk/major-branch shapes. The source prefab was not changed.");
            }
        }

        // Commit-time only. Disable before deferred removal so a rejected shape cannot affect gameplay this frame.
        internal static int ApplyColliderParts(GameObject instance, ColliderPolicy policy)
        {
            if (instance == null) return 0;
            if (policy == ColliderPolicy.None) { StripColliders(instance); return 0; }
            int retained = 0;
            foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
            {
                var category = UCScatterColliderPart.Read(collider, instance.transform);
                if ((int)category < 0 || (int)category > (int)ScatterCollisionCategory.None || UCScatterColliderPart.IsNonColliding(category))
                {
                    collider.enabled = false;
                    if (Application.isPlaying) UnityEngine.Object.Destroy(collider);
                    else UnityEngine.Object.DestroyImmediate(collider);
                    continue;
                }
                collider.isTrigger = policy == ColliderPolicy.Trigger;
                retained++;
            }
            return retained;
        }
    }
}
