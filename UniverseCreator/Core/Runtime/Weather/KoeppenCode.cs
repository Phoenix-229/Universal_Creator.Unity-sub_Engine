// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    KoeppenCode.cs
//  Purpose: Turns a year of climate normals into a Koeppen-Geiger code. A
//           diagnostic, and only ever a diagnostic.
//
//  WHY THIS IS NOT AN INPUT TO ANYTHING
//  ------------------------------------
//  Nothing in the vegetation or weather layers branches on the letters this
//  produces. That is deliberate and it is worth stating twice, because a climate
//  code is exactly the kind of value that looks convenient to switch on. The
//  moment something does, the code stops being a check: a world that reports the
//  wrong class would then also BEHAVE like the wrong class, and the disagreement
//  that was supposed to raise the alarm disappears into the output.
//
//  What it is for: an author places a spruce forest, the panel says Af, and the
//  numbers behind that panel are wrong. Latitude, altitude and rainfall are three
//  dials and this is one string, and it is the cheapest instrument in the toolkit
//  for catching a world whose climate does not mean what its author thought.
//
//  WHY NOT REUSE THE GRAPH'S CLASSIFIER
//  ------------------------------------
//  The shipped Koeppen node classifies a whole terrain per pixel and has only an
//  annual mean and an annual total to work with. Its own comment admits the cost:
//  the published aridity rule needs a seasonal term and it does not have one, so
//  it approximates. It is right to approximate - it has no month-by-month series
//  available per pixel and cannot afford one.
//
//  The normals ARE that series. So this is not a second copy of that classifier;
//  it is the full tree the node cannot run, over data the node does not have, at
//  one site rather than a million. The thresholds both use are named once, in
//  AtmoConstants, so the two cannot drift apart on the values they share.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Weather
{
    /// <summary>
    /// The five top-level Koeppen groups. The values match the shipped graph
    /// classifier's own enum on purpose, so the two can be compared directly
    /// without a translation table sitting between them and going stale.
    /// </summary>
    public enum KoeppenGroup
    {
        Tropical = 0,
        Arid = 1,
        Temperate = 2,
        Continental = 3,
        Polar = 4
    }

    /// <summary>
    /// A classification and the terms it was decided by. The terms are carried
    /// because a code on its own cannot be argued with: "BSk" tells an author
    /// nothing about how close the site was to not being dry at all, and the
    /// threshold it missed by is the number that tells them which dial to move.
    /// </summary>
    public struct KoeppenResult
    {
        /// <summary>Top-level group.</summary>
        public KoeppenGroup group;

        /// <summary>The letters, two or three of them: Af, BWh, Csa, Dfb, ET.</summary>
        public string code;

        /// <summary>The code in words, for a panel that has room for it.</summary>
        public string name;

        /// <summary>mm/yr. Rainfall below which this site would be classed dry.</summary>
        public float aridityThresholdMm;

        /// <summary>0-1 share of the annual rain falling in the warm half of the year.</summary>
        public float warmHalfPrecipShare01;

        /// <summary>Index of the first month of the warm half.</summary>
        public int warmHalfStartMonth;

        /// <summary>mm. The wettest and driest single months.</summary>
        public float driestMonthMm;

        /// <summary>mm. The wettest single month.</summary>
        public float wettestMonthMm;

        /// <summary>Count of months at or above the polar warmth threshold.</summary>
        public int warmMonthCount;
    }

    /// <summary>
    /// The Koeppen-Geiger decision tree over a year of climate normals. Pure
    /// static, allocates one string, reads nothing but its argument.
    /// </summary>
    public static class KoeppenCode
    {
        /// <summary>
        /// Classifies a set of normals.
        ///
        /// ORDER OF THE TESTS IS PART OF THE DEFINITION. Polar is settled first,
        /// on temperature alone: the driest place on the planet is the Antarctic
        /// plateau, and testing dryness before warmth would report it as a cold
        /// desert rather than as perpetual frost. Dry comes next and outranks the
        /// three temperate groups, because a dry climate is dry whatever its
        /// winter does. Only then do the coldest-month thresholds sort what is
        /// left into tropical, temperate and continental.
        /// </summary>
        public static KoeppenResult Classify(ClimateNormals n)
        {
            KoeppenResult r = new KoeppenResult();
            if (n == null) return r;

            int warmStart = WarmHalfStartMonth(n);
            r.warmHalfStartMonth = warmStart;

            float warmHalfPrecip = 0f;
            for (int k = 0; k < AtmoConstants.KoeppenHalfYearMonths; k++)
                warmHalfPrecip += n.monthlyPrecipMm[WrapMonth(warmStart + k)];

            float annual = n.annualPrecipMm > 0f ? n.annualPrecipMm : 0f;
            r.warmHalfPrecipShare01 = annual > 0f ? warmHalfPrecip / annual : 0f;

            r.driestMonthMm = float.MaxValue;
            r.wettestMonthMm = float.MinValue;
            r.warmMonthCount = 0;

            for (int m = 0; m < AtmoConstants.MonthsPerYear; m++)
            {
                float p = n.monthlyPrecipMm[m];
                if (p < r.driestMonthMm) r.driestMonthMm = p;
                if (p > r.wettestMonthMm) r.wettestMonthMm = p;

                if (n.monthlyMeanTempC[m] >= AtmoConstants.KoeppenPolarWarmestMonthC)
                    r.warmMonthCount++;
            }

            r.aridityThresholdMm = AridityThresholdMm(n.meanAnnualTempC,
                                                      r.warmHalfPrecipShare01);

            if (n.warmestMonthTempC < AtmoConstants.KoeppenPolarWarmestMonthC)
                return FillPolar(r, n);

            if (annual < r.aridityThresholdMm)
                return FillArid(r, n, annual);

            if (n.coldestMonthTempC >= AtmoConstants.KoeppenTropicalColdestMonthC)
                return FillTropical(r, n, annual, warmStart);

            return FillTemperateOrContinental(r, n, warmStart);
        }

        // =================================================================
        //  THE SEASONAL TERMS
        // =================================================================

        /// <summary>
        /// Index of the first month of the six-month warm half.
        ///
        /// Found by searching for the warmest run rather than assuming April to
        /// September and flipping it below the equator. The published rule uses
        /// that convention because it is written for Earth's calendar; a world
        /// whose axial tilt, year length or hemisphere differs would break it, and
        /// the warmest six consecutive months are what the convention is trying to
        /// name in the first place.
        /// </summary>
        public static int WarmHalfStartMonth(ClimateNormals n)
        {
            int best = 0;
            float bestSum = float.MinValue;

            for (int start = 0; start < AtmoConstants.MonthsPerYear; start++)
            {
                float sum = 0f;
                for (int k = 0; k < AtmoConstants.KoeppenHalfYearMonths; k++)
                    sum += n.monthlyMeanTempC[WrapMonth(start + k)];

                if (sum > bestSum)
                {
                    bestSum = sum;
                    best = start;
                }
            }

            return best;
        }

        /// <summary>
        /// mm/yr. The rainfall a site needs to clear to not be classed dry.
        ///
        /// The seasonal offset is the whole reason this rule is not just "20 times
        /// the temperature": rain that arrives in high summer is largely lost to
        /// evaporation, so a summer-wet climate needs far more of it to support the
        /// same vegetation as a winter-wet one. That term is what the graph node's
        /// per-pixel version has to leave out, and it is worth up to 280 mm.
        /// </summary>
        public static float AridityThresholdMm(float meanAnnualTempC,
                                               float warmHalfPrecipShare01)
        {
            float offset;
            if (warmHalfPrecipShare01 >= AtmoConstants.KoeppenSummerShareHigh)
                offset = AtmoConstants.KoeppenAridityOffsetSummerMm;
            else if (warmHalfPrecipShare01 < AtmoConstants.KoeppenSummerShareLow)
                offset = AtmoConstants.KoeppenAridityOffsetWinterMm;
            else
                offset = AtmoConstants.KoeppenAridityOffsetEvenMm;

            return AtmoConstants.KoeppenAridityTempFactor * meanAnnualTempC + offset;
        }

        /// <summary>Wraps a month index into the year, for negatives as well as overshoots.</summary>
        private static int WrapMonth(int month)
        {
            int m = month % AtmoConstants.MonthsPerYear;
            return m < 0 ? m + AtmoConstants.MonthsPerYear : m;
        }

        // =================================================================
        //  THE FIVE GROUPS
        // =================================================================

        private static KoeppenResult FillPolar(KoeppenResult r, ClimateNormals n)
        {
            r.group = KoeppenGroup.Polar;

            // The split is on whether anything thaws at all. Tundra has a growing
            // season measured in weeks; an ice cap has none, and the difference is
            // the whole of what can live there.
            bool iceCap = n.warmestMonthTempC < AtmoConstants.KoeppenFrostWarmestMonthC;

            r.code = iceCap ? "EF" : "ET";
            r.name = iceCap ? "Ice cap, no month above freezing"
                            : "Tundra, brief cool summer";
            return r;
        }

        private static KoeppenResult FillArid(KoeppenResult r, ClimateNormals n,
                                              float annual)
        {
            r.group = KoeppenGroup.Arid;

            bool desert = annual < r.aridityThresholdMm * AtmoConstants.KoeppenDesertFraction;
            bool hot = n.meanAnnualTempC >= AtmoConstants.KoeppenHotAnnualMeanC;

            r.code = (desert ? "BW" : "BS") + (hot ? "h" : "k");
            r.name = (desert ? "Desert" : "Steppe") + (hot ? ", hot" : ", cold");
            return r;
        }

        private static KoeppenResult FillTropical(KoeppenResult r, ClimateNormals n,
                                                  float annual, int warmStart)
        {
            r.group = KoeppenGroup.Tropical;

            if (r.driestMonthMm >= AtmoConstants.KoeppenTropicalDriestMonthMm)
            {
                r.code = "Af";
                r.name = "Tropical rainforest, no dry month";
                return r;
            }

            // The monsoon line. A brief dry month is survivable if the rest of the
            // year is wet enough to carry the forest through it, and how wet that
            // has to be scales with the total.
            float monsoonFloor = AtmoConstants.KoeppenMonsoonInterceptMm
                                 - annual / AtmoConstants.KoeppenMonsoonDivisor;

            if (r.driestMonthMm >= monsoonFloor)
            {
                r.code = "Am";
                r.name = "Tropical monsoon, short dry season";
                return r;
            }

            bool drySummer = DriestMonthIsInWarmHalf(n, warmStart);
            r.code = drySummer ? "As" : "Aw";
            r.name = drySummer ? "Tropical savanna, dry summer"
                               : "Tropical savanna, dry winter";
            return r;
        }

        private static KoeppenResult FillTemperateOrContinental(KoeppenResult r,
                                                               ClimateNormals n,
                                                               int warmStart)
        {
            bool temperate = n.coldestMonthTempC
                             >= AtmoConstants.KoeppenTemperateColdestMonthC;

            r.group = temperate ? KoeppenGroup.Temperate : KoeppenGroup.Continental;

            string first = temperate ? "C" : "D";
            string second = SeasonalityLetter(n, warmStart);
            string third = SummerWarmthLetter(n, r.warmMonthCount);

            r.code = first + second + third;
            r.name = (temperate ? "Temperate" : "Continental")
                     + ", " + SeasonalityWord(second)
                     + ", " + SummerWarmthWord(third);
            return r;
        }

        /// <summary>
        /// f, s or w: rain all year, a dry summer or a dry winter.
        ///
        /// Both tests are relative, not absolute, and that is the point. A dry
        /// summer is not "little rain in summer" - it is little rain in summer
        /// while the winter is wet, which is what a subtropical high sitting
        /// offshore for four months does and what nothing else does. A flat 700 mm
        /// year and a Mediterranean 700 mm year differ here and nowhere else in the
        /// annual figures, and they grow completely different plants.
        /// </summary>
        private static string SeasonalityLetter(ClimateNormals n, int warmStart)
        {
            float driestWarm = float.MaxValue;
            float wettestWarm = float.MinValue;
            float driestCool = float.MaxValue;
            float wettestCool = float.MinValue;

            for (int k = 0; k < AtmoConstants.MonthsPerYear; k++)
            {
                float p = n.monthlyPrecipMm[WrapMonth(warmStart + k)];

                if (k < AtmoConstants.KoeppenHalfYearMonths)
                {
                    if (p < driestWarm) driestWarm = p;
                    if (p > wettestWarm) wettestWarm = p;
                }
                else
                {
                    if (p < driestCool) driestCool = p;
                    if (p > wettestCool) wettestCool = p;
                }
            }

            if (driestWarm < AtmoConstants.KoeppenSummerDryMonthMm &&
                wettestCool > driestWarm * AtmoConstants.KoeppenDrySummerRatio)
                return "s";

            if (wettestWarm > driestCool * AtmoConstants.KoeppenDryWinterRatio)
                return "w";

            return "f";
        }

        /// <summary>
        /// a, b, c or d: how warm the summer is and, for the coldest continental
        /// interiors, how brutal the winter is with it. The count of months above
        /// the polar threshold is what separates a cool summer from a short one.
        /// </summary>
        private static string SummerWarmthLetter(ClimateNormals n, int warmMonthCount)
        {
            if (n.warmestMonthTempC >= AtmoConstants.KoeppenHotSummerMonthC) return "a";
            if (warmMonthCount >= AtmoConstants.KoeppenWarmSummerMonthCount) return "b";

            // Tested after b, because d is a subtype of the cool-summer case rather
            // than an alternative to it: a Siberian winter and a short summer go
            // together, and it is the winter that distinguishes Dfd from Dfc.
            if (n.coldestMonthTempC < AtmoConstants.KoeppenSevereWinterC) return "d";

            return "c";
        }

        /// <summary>The second letter in words.</summary>
        private static string SeasonalityWord(string letter)
        {
            if (letter == "s") return "dry summer";
            if (letter == "w") return "dry winter";
            return "rain all year";
        }

        /// <summary>The third letter in words.</summary>
        private static string SummerWarmthWord(string letter)
        {
            if (letter == "a") return "hot summer";
            if (letter == "b") return "warm summer";
            if (letter == "d") return "cool summer, severe winter";
            return "cool short summer";
        }

        /// <summary>
        /// Whether the driest month falls in the warm half, which is what separates
        /// a tropical savanna's As from its Aw.
        /// </summary>
        private static bool DriestMonthIsInWarmHalf(ClimateNormals n, int warmStart)
        {
            int driest = 0;
            float least = float.MaxValue;

            for (int m = 0; m < AtmoConstants.MonthsPerYear; m++)
            {
                if (n.monthlyPrecipMm[m] < least)
                {
                    least = n.monthlyPrecipMm[m];
                    driest = m;
                }
            }

            int offset = WrapMonth(driest - warmStart);
            return offset < AtmoConstants.KoeppenHalfYearMonths;
        }
    }
}
