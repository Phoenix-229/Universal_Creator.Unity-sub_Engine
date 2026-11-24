// UC-T06-06: prototype/world IDs are captured before workers run, including when ecology is disabled.
using System;
using System.Collections.Generic;
using UniverseCreator.Nodes;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    internal sealed class ScatterIdentitySnapshot
    {
        internal readonly string World;
        private readonly Dictionary<string, string[]> prototypes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        internal readonly ulong Hash;

        private ScatterIdentitySnapshot(string world, Dictionary<string, string[]> captured)
        {
            World = world; prototypes = captured;
            ulong hash = 1469598103934665603UL;
            void Fold(string value) { unchecked { if (value != null) foreach (char c in value) { hash ^= c; hash *= 1099511628211UL; } hash ^= 0xff; hash *= 1099511628211UL; } }
            Fold(world);
            foreach (string channel in new[] { "trees", "shrubs", "details", "objects" })
            { Fold(channel); foreach (string prototype in prototypes[channel]) Fold(prototype); }
            Hash = hash != 0 ? hash : 1;
        }

        internal string Prototype(string channel, int slot)
        {
            // UC-T06-06: match ScatterApplier routing: rocks/debris/underwater/custom channels all consume the object roster.
            string roster = channel == "trees" || channel == "shrubs" ? channel :
                channel == "grass" || (channel != null && channel.StartsWith("detail_", StringComparison.Ordinal)) ? "details" : "objects";
            return prototypes.TryGetValue(roster, out var ids) && slot >= 0 && slot < ids.Length ? ids[slot] : null;
        }

        internal static ScatterIdentitySnapshot Capture(UniverseCreatorObject world)
        {
            if (world == null) return null;
            string worldId = world.ReadVegetationIdentity();
            var captured = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string Identity(string existing)
            {
                if (string.IsNullOrEmpty(existing)) throw new InvalidOperationException("Initialize legacy scatter prototype identities explicitly before generation; snapshot capture does not change authoring.");
                if (!seen.Add(existing)) throw new InvalidOperationException("Duplicate persistent scatter prototype identity. Resolve the duplicated roster entry before generation; saved removals were retained.");
                return existing;
            }
            var lib = world.scatterLibrary;
            captured["trees"] = new string[lib?.trees?.Count ?? 0];
            captured["shrubs"] = new string[lib?.shrubs?.Count ?? 0];
            captured["details"] = new string[lib?.details?.Count ?? 0];
            captured["objects"] = new string[lib?.objects?.Count ?? 0];
            for (int i = 0; i < captured["trees"].Length; i++) if (lib.trees[i] != null)
                captured["trees"][i] = Identity(lib.trees[i].persistentId);
            for (int i = 0; i < captured["shrubs"].Length; i++) if (lib.shrubs[i] != null)
                captured["shrubs"][i] = Identity(lib.shrubs[i].persistentId);
            for (int i = 0; i < captured["details"].Length; i++) if (lib.details[i] != null)
                captured["details"][i] = Identity(lib.details[i].persistentId);
            for (int i = 0; i < captured["objects"].Length; i++) if (lib.objects[i] != null)
                captured["objects"][i] = Identity(lib.objects[i].persistentId);
            return new ScatterIdentitySnapshot(worldId, captured);
        }

        internal void Stamp(string channel, string producerId, List<ScatterInstance> instances)
        {
            if (string.IsNullOrEmpty(producerId)) throw new InvalidOperationException("Scatter identity requires a serialized producer ID.");
            string layer = channel.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + channel + producerId; // Length prefix avoids ambiguous channel/producer delimiter combinations.
            var seen = new HashSet<VegetationInstanceKey>();
            for (int i = 0; i < instances.Count; i++)
            {
                ScatterInstance instance = instances[i];
                string prototype = Prototype(channel, instance.prototypeIndex);
                if (string.IsNullOrEmpty(prototype)) continue; // Missing prototypes remain an application diagnostic, not a fabricated identity.
                instance.identity = VegetationInstanceKey.Create(World, layer, prototype, instance.position.x, instance.position.z);
                if (!seen.Add(instance.identity)) throw new InvalidOperationException("Duplicate full scatter instance identity in one producer. Placement was not published; persistent edits were retained.");
                instances[i] = instance;
            }
        }
    }
}
