// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    EcoScatterContextBuilder.cs
//  Purpose: Flattens the prototype library into the plain arrays a worker thread
//           can read, and hands back the context a scatter node selects from.
//
//  WHY THE FLATTENING LIVES HERE AND NOT IN ECOLOGY. Ecology knows about
//  envelopes and nothing about prototypes, prefabs or terrain data. Putting this
//  step in the Terrain layer keeps that direction of dependency intact: Terrain
//  may read Ecology, Ecology may not read Terrain. The same reason the water
//  probes are flattened on this side of the boundary rather than the other.
//
//  MAIN THREAD ONLY, ONCE PER BAKE. Reading a List<T> off a serialised
//  MonoBehaviour field is not safe from a worker, so the lists are copied into
//  arrays first and only the arrays cross over.
//
//  THE TWO WEIGHTS ARE MULTIPLIED HERE, which is the only place in the codebase
//  that knows there are two. The prototype slot's weight is the author's dial in
//  the library inspector and survives a preset being re-applied; the envelope's
//  weight is part of the species description and is reset to one by any preset.
//  Their product is what relative abundance means, and SpeciesSelector receives
//  it as a single number.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    public static class EcoScatterContextBuilder
    {
        /// <summary>
        /// Resolves a world's ecology, or returns null when there is none to resolve.
        ///
        /// Main thread only, and that is the whole reason this method exists as a
        /// separate step: ResolveSettings reads the lighting and weather managers
        /// live, so it cannot be called from the worker that will consume the result.
        ///
        /// Null is the ordinary answer, not a failure - useEcology ships off, and a
        /// world that has not switched it on gets exactly the placement Modules 1
        /// and 2 gave it. That is the opt-in contract the vegetation manager's own
        /// header states, honoured here rather than restated.
        /// </summary>
        public static Ecology.EcoScatterContext BuildFor(UniverseCreatorObject world)
        {
            if (world == null) return null;

            // The mirror of how the vegetation manager finds the lighting manager,
            // inactive children included, so the lookup behaves the same in both
            // directions. Whether ecology runs is useEcology's decision alone.
            UCVegetationSystemManager veg =
                world.GetComponentInChildren<UCVegetationSystemManager>(true);

            if (veg == null || !veg.useEcology) return null;

            return Build(world.scatterLibrary, veg.ResolveSettings());
        }

        /// <summary>
        /// Builds the bake-level context: the settings and the four species tables,
        /// which are the same for every chunk. A chunked bake calls ForChunk on the
        /// result once per chunk so each gets its own sampler; a single whole-world
        /// tile can use this one directly. The sampler binds itself on first use,
        /// once the height field it needs exists.
        /// </summary>
        public static Ecology.EcoScatterContext Build(
            ScatterPrototypeLibrary lib, in Ecology.EcoSampleSettings settings)
        {
            Ecology.EcoScatterContext ctx = new Ecology.EcoScatterContext();
            ctx.settings = settings;
            ctx.sampler = new Ecology.EnvironmentSampler();

            if (lib == null) return ctx;

            Fill(ctx.trees, lib.trees, TreeEco, TreeWeight);
            Fill(ctx.shrubs, lib.shrubs, ShrubEco, ShrubWeight);
            Fill(ctx.details, lib.details, DetailEco, DetailWeight);
            Fill(ctx.objects, lib.objects, ObjectEco, ObjectWeight);

            return ctx;
        }

        // One loop for four unrelated def types. They share no base class - each is a
        // separate serialised shape with its own prefab and render fields - so the two
        // things this needs from them are read through accessors rather than by
        // inventing a common base that would change four serialised layouts.
        private static void Fill<T>(Ecology.SpeciesTable table, List<T> defs,
                                    System.Func<T, Ecology.EcoEnvelope> readEco,
                                    System.Func<T, float> readWeight)
        {
            int count = defs != null ? defs.Count : 0;

            table.count = count;

            if (count <= 0)
            {
                table.envelopes = null;
                table.weights = null;
                return;
            }

            table.envelopes = new Ecology.EcoEnvelope[count];
            table.weights = new float[count];

            for (int i = 0; i < count; i++)
            {
                T def = defs[i];

                if (def == null)
                {
                    // An empty slot still occupies its index, because the index is what
                    // the terrain's prototype array is keyed by. Weight zero keeps it
                    // from ever being chosen without shifting everything after it.
                    table.weights[i] = 0f;
                    continue;
                }

                Ecology.EcoEnvelope eco = readEco(def);

                table.envelopes[i] = eco;
                table.weights[i] = Mathf.Max(0f, readWeight(def)) *
                                   (eco != null ? Mathf.Max(0f, eco.weight) : 1f);
            }
        }

        // Static accessors rather than inline lambdas: a lambda that captures nothing is
        // cached by the compiler, and naming them keeps the Fill calls above readable.
        private static Ecology.EcoEnvelope TreeEco(TreePrototypeDef d) { return d.eco; }
        private static float TreeWeight(TreePrototypeDef d) { return d.weight; }

        private static Ecology.EcoEnvelope ShrubEco(ShrubPrototypeDef d) { return d.eco; }
        private static float ShrubWeight(ShrubPrototypeDef d) { return d.weight; }

        private static Ecology.EcoEnvelope DetailEco(DetailPrototypeDef d) { return d.eco; }
        private static float DetailWeight(DetailPrototypeDef d) { return d.weight; }

        private static Ecology.EcoEnvelope ObjectEco(ObjectPrototypeDef d) { return d.eco; }
        private static float ObjectWeight(ObjectPrototypeDef d) { return d.weight; }
    }
}
