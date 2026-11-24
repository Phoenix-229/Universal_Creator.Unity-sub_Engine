// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.cs
//  Purpose: Fills an EcoEnvelope from a real-world organism, so an author starts
//           from a spruce rather than from fifty-five zeroes.
//
//  Every preset carries measured values for the organism it names - treeline
//  temperature, annual rainfall, mature height, diameter at breast height, shade
//  tolerance from the silvics literature. None of them are invented to look
//  plausible, because a number invented to look plausible is exactly what puts a
//  pine in a swamp.
//
//  FOUR RULES, and they are what keep presets honest:
//
//    1. A preset writes the biology and nothing else. It never touches a prefab
//       reference, a collider setting, an instancing policy, a LOD bias or a
//       per-chunk budget. Those are the author's decisions about the machine, not
//       facts about the plant, and a preset that overwrote them would throw away
//       work the author had done.
//    2. Applying one is a single undo step, recorded by the caller on the world
//       object before the call.
//    3. The envelope remembers which preset it came from and a checksum of the
//       values that were written, so the inspector can say "(modified)" the
//       moment an author diverges from the archetype. Drifting is allowed;
//       drifting without being told is not.
//    4. No preset is a biome. A preset is one organism. Assembling organisms into
//       an assemblage is a separate job, and conflating the two is how a system
//       ends up unable to put a willow beside a pine.
//
//  The presets themselves live in one file per landform group, as partial methods
//  on this class - fifty-five organisms in one file would be the two-thousand-line
//  script nobody can debug.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// Which landform group a preset belongs to. Used only to organise the
    /// inspector's menu; it carries no ecology of its own.
    /// </summary>
    public enum SpeciesPresetGroup
    {
        None = 0,
        BorealMontaneConifer = 1,
        TemperateBroadleaf = 2,
        MediterraneanDry = 3,
        Tropical = 4,
        Arid = 5,
        CoastalSaline = 6,
        Wetland = 7,
        Aquatic = 8,
        AlpineTundra = 9,
        GrassAndGround = 10,
        NonLivingScatter = 11
    }

    /// <summary>
    /// One real organism, or one real deposit.
    ///
    /// Numbered in blocks of one hundred per group, deliberately. A saved world
    /// stores the integer, so adding a sixth conifer must never shift what the
    /// existing broadleaf values mean (C-15 rule 2). The gaps are the room to
    /// grow.
    /// </summary>
    public enum SpeciesPreset
    {
        None = 0,

        // ---- Boreal / montane conifer -----------------------------------
        Spruce = 100,
        Fir = 101,
        Pine = 102,
        Larch = 103,
        KrummholzDwarfPine = 104,

        // ---- Temperate broadleaf ----------------------------------------
        Oak = 200,
        Beech = 201,
        Birch = 202,
        Maple = 203,
        Ash = 204,
        Alder = 205,
        Willow = 206,

        // ---- Mediterranean / dry ----------------------------------------
        Olive = 300,
        CorkOak = 301,
        Juniper = 302,
        MaquisScrub = 303,
        RosemaryShrub = 304,

        // ---- Tropical ---------------------------------------------------
        RainforestEmergent = 400,
        RainforestCanopy = 401,
        UnderstoryPalm = 402,
        CoconutPalm = 403,
        BananaHerb = 404,

        // ---- Arid -------------------------------------------------------
        SaguaroColumnarCactus = 500,
        BarrelCactus = 501,
        CreosoteShrub = 502,
        Acacia = 503,
        SpinifexTussock = 504,

        // ---- Coastal / saline -------------------------------------------
        Mangrove = 600,
        SaltmarshGrass = 601,
        MarramDuneGrass = 602,
        SeaBuckthorn = 603,

        // ---- Wetland ----------------------------------------------------
        Reed = 700,
        Sedge = 701,
        Cattail = 702,
        SphagnumMoss = 703,
        WaterLily = 704,

        // ---- Aquatic ----------------------------------------------------
        Kelp = 800,
        Seagrass = 801,
        FreshwaterWeed = 802,

        // ---- Alpine / tundra --------------------------------------------
        CushionPlant = 900,
        AlpineSedge = 901,
        DwarfWillow = 902,
        CrustoseLichen = 903,

        // ---- Grass and ground -------------------------------------------
        MeadowFescue = 1000,
        Bunchgrass = 1001,
        PrairieTallgrass = 1002,
        BrackenFern = 1003,
        Heather = 1004,
        MossMat = 1005,

        // ---- Non-living scatter -----------------------------------------
        AngularScree = 1100,
        RoundedCobble = 1101,
        GlacialErratic = 1102,
        DeadfallLog = 1103,
        Driftwood = 1104,
        CoralHead = 1105
    }

    /// <summary>
    /// The preset table. Static, in source, and greppable - not a folder of
    /// assets, so a diff shows when an organism's numbers changed and no absolute
    /// path is needed to find them.
    /// </summary>
    public static partial class SpeciesPresets
    {
        /// <summary>
        /// Overwrites the biological fields of <paramref name="eco"/> with the
        /// named organism's real values and stamps the envelope with where they
        /// came from.
        ///
        /// Returns false when the preset is unknown or the envelope is null, and
        /// changes nothing in that case, so a caller can report the failure rather
        /// than silently applying half an organism.
        /// </summary>
        public static bool Apply(EcoEnvelope eco, SpeciesPreset preset)
        {
            if (eco == null) return false;
            if (preset == SpeciesPreset.None) return false;

            bool applied =
                ApplyConifer(eco, preset)
                || ApplyBroadleaf(eco, preset)
                || ApplyMediterranean(eco, preset)
                || ApplyTropical(eco, preset)
                || ApplyArid(eco, preset)
                || ApplyCoastal(eco, preset)
                || ApplyWetland(eco, preset)
                || ApplyAquatic(eco, preset)
                || ApplyAlpine(eco, preset)
                || ApplyGround(eco, preset)
                || ApplyScatter(eco, preset);

            if (!applied) return false;

            // Stamped last, so the checksum covers the values that were just
            // written and nothing the author changes afterwards.
            eco.presetName = DisplayName(preset);
            eco.presetChecksum = ChecksumOf(eco);

            return true;
        }

        /// <summary>
        /// True once any biological field has diverged from the preset that was
        /// applied. An envelope that never had a preset applied is not modified -
        /// there is nothing for it to have diverged from.
        /// </summary>
        public static bool IsModified(EcoEnvelope eco)
        {
            if (eco == null) return false;
            if (string.IsNullOrEmpty(eco.presetName)) return false;

            return ChecksumOf(eco) != eco.presetChecksum;
        }

        /// <summary>Which group a preset is filed under, from its numeric block.</summary>
        public static SpeciesPresetGroup GroupOf(SpeciesPreset preset)
        {
            int block = (int)preset / 100;

            switch (block)
            {
                case 1: return SpeciesPresetGroup.BorealMontaneConifer;
                case 2: return SpeciesPresetGroup.TemperateBroadleaf;
                case 3: return SpeciesPresetGroup.MediterraneanDry;
                case 4: return SpeciesPresetGroup.Tropical;
                case 5: return SpeciesPresetGroup.Arid;
                case 6: return SpeciesPresetGroup.CoastalSaline;
                case 7: return SpeciesPresetGroup.Wetland;
                case 8: return SpeciesPresetGroup.Aquatic;
                case 9: return SpeciesPresetGroup.AlpineTundra;
                case 10: return SpeciesPresetGroup.GrassAndGround;
                case 11: return SpeciesPresetGroup.NonLivingScatter;
                default: return SpeciesPresetGroup.None;
            }
        }

        /// <summary>
        /// The organism's name as an author reads it, with the spaces the enum
        /// member cannot carry.
        /// </summary>
        public static string DisplayName(SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.KrummholzDwarfPine: return "Krummholz Dwarf Pine";
                case SpeciesPreset.CorkOak: return "Cork Oak";
                case SpeciesPreset.MaquisScrub: return "Maquis Scrub";
                case SpeciesPreset.RosemaryShrub: return "Rosemary Shrub";
                case SpeciesPreset.RainforestEmergent: return "Rainforest Emergent";
                case SpeciesPreset.RainforestCanopy: return "Rainforest Canopy";
                case SpeciesPreset.UnderstoryPalm: return "Understory Palm";
                case SpeciesPreset.CoconutPalm: return "Coconut Palm";
                case SpeciesPreset.BananaHerb: return "Banana Herb";
                case SpeciesPreset.SaguaroColumnarCactus: return "Saguaro Columnar Cactus";
                case SpeciesPreset.BarrelCactus: return "Barrel Cactus";
                case SpeciesPreset.CreosoteShrub: return "Creosote Shrub";
                case SpeciesPreset.SpinifexTussock: return "Spinifex Tussock";
                case SpeciesPreset.SaltmarshGrass: return "Saltmarsh Grass";
                case SpeciesPreset.MarramDuneGrass: return "Marram Dune Grass";
                case SpeciesPreset.SeaBuckthorn: return "Sea Buckthorn";
                case SpeciesPreset.SphagnumMoss: return "Sphagnum Moss";
                case SpeciesPreset.WaterLily: return "Water Lily";
                case SpeciesPreset.FreshwaterWeed: return "Freshwater Weed";
                case SpeciesPreset.CushionPlant: return "Cushion Plant";
                case SpeciesPreset.AlpineSedge: return "Alpine Sedge";
                case SpeciesPreset.DwarfWillow: return "Dwarf Willow";
                case SpeciesPreset.CrustoseLichen: return "Crustose Lichen";
                case SpeciesPreset.MeadowFescue: return "Meadow Fescue";
                case SpeciesPreset.PrairieTallgrass: return "Prairie Tallgrass";
                case SpeciesPreset.BrackenFern: return "Bracken Fern";
                case SpeciesPreset.MossMat: return "Moss Mat";
                case SpeciesPreset.AngularScree: return "Angular Scree";
                case SpeciesPreset.RoundedCobble: return "Rounded Cobble";
                case SpeciesPreset.GlacialErratic: return "Glacial Erratic";
                case SpeciesPreset.DeadfallLog: return "Deadfall Log";
                case SpeciesPreset.CoralHead: return "Coral Head";
                default: return preset.ToString();
            }
        }

        // Every preset in the group files ends by calling this, so no preset can
        // forget to name itself and no two presets can disagree about how the
        // shared fields are stamped.
        private static void Stamp(EcoEnvelope eco, string displayName, LifeForm form)
        {
            eco.displayName = displayName;
            eco.lifeForm = form;
        }
    }
}
