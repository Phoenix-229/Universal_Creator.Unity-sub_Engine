// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    UCSelfTest.Weather2.cs
//  Purpose: Checks for the Koeppen cross-check itself, and for the pressure
//           lattice the synoptic weather is built on. Continues
//           UCSelfTest.Weather.cs, which reached the file-size limit.
//
//  WHY A DIAGNOSTIC GETS ITS OWN TESTS
//  -----------------------------------
//  Because a broken instrument is worse than no instrument. The whole value of
//  the climate code is that an author can trust it enough to go and change their
//  world when it disagrees with them; a classifier that mislabels a Mediterranean
//  site as a steppe would send them off to fix numbers that were right.
//
//  Every expected code below was worked out by hand from the model's own
//  constants before it was written down - the zonal profile, the Conrad amplitude
//  at that latitude, the regime's monthly weights, then the published decision
//  tree over the result. Where a site sits close to a threshold the assertion
//  checks the letters that are safely settled and leaves the marginal one alone,
//  because a test that pins a value to its third decimal fails on the day
//  somebody legitimately retunes a constant by one per cent.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Ecology;
using UniverseCreator.Nodes.Biome;
using UniverseCreator.Weather;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestKoeppenCode()
        {
            TestKoeppenTerms();
            TestKoeppenSites();
            TestKoeppenAgreementWithGraphNode();
            TestKoeppenRobustness();
        }

        /// <summary>
        /// A site at a latitude, a continentality dial and an annual rainfall, with
        /// everything else at the model's defaults. One builder for every case
        /// below, so no test can accidentally be measuring a different world than
        /// the one it names.
        /// </summary>
        private static ClimateNormals KoeppenSite(float latitudeDeg,
                                                  float continentality01,
                                                  float annualPrecipMm)
        {
            return KoeppenSiteAt(latitudeDeg, continentality01, annualPrecipMm, 0f);
        }

        /// <summary>The same site lifted to an altitude, for the altitude sweep.</summary>
        private static ClimateNormals KoeppenSiteAt(float latitudeDeg,
                                                    float continentality01,
                                                    float annualPrecipMm,
                                                    float altitudeM)
        {
            ClimateRequest r = ClimateRequest.Default(latitudeDeg, altitudeM,
                                                     annualPrecipMm,
                                                     EcoConstants.GddBaseTemperateC);

            r.distanceToCoastM =
                ClimateNormals.CoastDistanceMFromContinentality01(continentality01);

            return ClimateNormals.Build(r);
        }

        /// <summary>
        /// The seasonal terms the tree is decided by, each checked on its own before
        /// any code is asked for. A wrong code with three wrong terms behind it is
        /// almost impossible to read; a wrong code with three right terms behind it
        /// points straight at the tree.
        /// </summary>
        private static void TestKoeppenTerms()
        {
            // ---- the continentality dial and the curve it inverts ----------------
            //
            // Dividing the index out by its own ceiling makes the inverse exact and
            // the identity trivial: a dial at c lands on exactly c of the maximum
            // index. Worth asserting precisely because it means an author reading
            // "0.6" is reading a real fraction of the model's range, not a number
            // that merely rises.
            Check(ClimateNormals.CoastDistanceMFromContinentality01(0f) == 0f,
                  "Koeppen terms: a maritime dial of zero is zero distance inland");

            for (int i = 1; i <= 9; i++)
            {
                float c = i * 0.1f;
                float d = ClimateNormals.CoastDistanceMFromContinentality01(c);
                float k = ClimateNormals.ConradIndexFromCoastDistance(d);

                CheckWithin(k, AtmoConstants.ConradMaxIndex * c, 0.01f,
                            "Koeppen terms: dial " + c.ToString("0.0") +
                            " round-trips to that share of the Conrad ceiling");
            }

            float saturated = ClimateNormals.CoastDistanceMFromContinentality01(1f);
            Check(IsFinite(saturated) && saturated > 0f,
                  "Koeppen terms: a fully continental dial is a finite distance");
            Check(ClimateNormals.ConradIndexFromCoastDistance(saturated)
                      > AtmoConstants.ConradMaxIndex * 0.999f,
                  "Koeppen terms: and it has reached the Conrad ceiling");

            // ---- the aridity threshold's three seasonal offsets -----------------
            //
            // 20 * 20 degC = 400 mm of base demand, plus 280 / 140 / 0 depending on
            // when the rain arrives. The two boundary shares are checked on the side
            // the published rule puts them: 0.7 counts as summer-wet, 0.3 does not
            // count as winter-wet.
            CheckClose(KoeppenCode.AridityThresholdMm(20f, 0.8f), 680f,
                       "Koeppen terms: summer-wet aridity threshold");
            CheckClose(KoeppenCode.AridityThresholdMm(20f, 0.5f), 540f,
                       "Koeppen terms: evenly-wet aridity threshold");
            CheckClose(KoeppenCode.AridityThresholdMm(20f, 0.1f), 400f,
                       "Koeppen terms: winter-wet aridity threshold");
            CheckClose(KoeppenCode.AridityThresholdMm(20f,
                           AtmoConstants.KoeppenSummerShareHigh), 680f,
                       "Koeppen terms: the summer share boundary counts as summer");
            CheckClose(KoeppenCode.AridityThresholdMm(20f,
                           AtmoConstants.KoeppenSummerShareLow), 540f,
                       "Koeppen terms: the winter share boundary counts as even");

            // ---- the warm half, found rather than assumed ------------------------
            //
            // A deep-interior site at 48 degrees, north and south. The searched warm
            // half must land in the local summer in both, which is the whole reason
            // it is searched: a hard-coded April-September with a hemisphere flip
            // would pass this and still be wrong on a world with a different tilt.
            ClimateNormals north = KoeppenSite(48f, 0.95f, 600f);
            ClimateNormals south = KoeppenSite(-48f, 0.95f, 600f);

            Check(KoeppenCode.WarmHalfStartMonth(north) == 4,
                  "Koeppen terms: the northern warm half opens in May");
            Check(KoeppenCode.WarmHalfStartMonth(south) == 10,
                  "Koeppen terms: the southern warm half opens in November");

            CheckWarmHalfIsWarmer(north, "northern");
            CheckWarmHalfIsWarmer(south, "southern");

            // ---- a threshold this model cannot reach, stated as such -------------
            //
            // The severe-winter letter d needs a summer at or above 10 degC and a
            // winter below -38, which is an annual range wider than 48 degrees. The
            // widest range the model can produce is Conrad at its ceiling at the
            // latitude where the latitude weight peaks, and that is about 46.5. So d
            // is unreachable here, and no site below expects it.
            //
            // This is not a fault in either number: real Dfd sites sit at Conrad
            // indices above 65, so the ceiling is what excludes them. The assertion
            // is here so that raising that ceiling announces itself rather than
            // quietly making a letter reachable that nothing has ever tested.
            float widestRangeC = ClimateNormals.AnnualRangeFromConradIndex(
                                     AtmoConstants.ConradMaxIndex, 80f);

            Check(widestRangeC < AtmoConstants.KoeppenPolarWarmestMonthC
                                 - AtmoConstants.KoeppenSevereWinterC,
                  "Koeppen terms: the model's widest annual range cannot reach the " +
                  "severe-winter letter, so no site expects it");
        }

        /// <summary>
        /// That the six months the search picked really are the warmer six. Cheap,
        /// and it is the property the seasonal rain tests actually depend on - the
        /// month index above is only the answer for this particular calendar, while
        /// this holds on any world the search is pointed at.
        /// </summary>
        private static void CheckWarmHalfIsWarmer(ClimateNormals n, string label)
        {
            int start = KoeppenCode.WarmHalfStartMonth(n);
            float warm = 0f;
            float cool = 0f;

            for (int k = 0; k < AtmoConstants.MonthsPerYear; k++)
            {
                int m = (start + k) % AtmoConstants.MonthsPerYear;

                if (k < AtmoConstants.KoeppenHalfYearMonths) warm += n.monthlyMeanTempC[m];
                else                                         cool += n.monthlyMeanTempC[m];
            }

            Check(warm > cool,
                  "Koeppen terms: the " + label + " warm half is the warmer half");
        }

        /// <summary>
        /// Recognisable places, each landing on the code the real one carries.
        ///
        /// WHY SOME ASSERT TWO LETTERS AND SOME THREE
        /// The third letter of a C or D code turns on a count of months above 10 degC,
        /// and a site can sit one tenth of a degree from changing that count. Where
        /// the hand-worked margin is wide the whole code is pinned; where it is thin
        /// the letters that are settled are pinned and the marginal one is left free,
        /// with the margin named in the comment so the next reader knows it was
        /// measured rather than forgotten.
        /// </summary>
        private static void TestKoeppenSites()
        {
            // Equatorial, barely off the coast, 2500 mm. The ITCZ passes twice, so the
            // driest month still gets about 137 mm - more than double the 60 mm floor.
            KoeppenResult rainforest = KoeppenCode.Classify(KoeppenSite(2f, 0.05f, 2500f));
            Check(rainforest.group == KoeppenGroup.Tropical && rainforest.code == "Af",
                  "Koeppen site: wet equator is Af, tropical rainforest");
            Check(rainforest.driestMonthMm > AtmoConstants.KoeppenTropicalDriestMonthMm,
                  "Koeppen site: and no month of it is dry");

            // A monsoon latitude. The regime's amplitude is above 1, so three months
            // clamp to no rain at all and the dry season is unmistakable - the driest
            // month is 0 mm against a monsoon floor of 28, and it falls in the cool
            // half, which is what makes it Aw rather than As.
            KoeppenResult monsoon = KoeppenCode.Classify(KoeppenSite(15f, 0.4f, 1800f));
            Check(monsoon.group == KoeppenGroup.Tropical && monsoon.code == "Aw",
                  "Koeppen site: monsoon latitude is Aw, savanna with a dry winter");
            Check(monsoon.driestMonthMm <= 0f,
                  "Koeppen site: and its dry season is genuinely rainless");
            Check(monsoon.warmHalfPrecipShare01 > AtmoConstants.KoeppenSummerShareHigh,
                  "Koeppen site: monsoon rain arrives in the warm half");

            // Deep in a subtropical interior on 100 mm. Mean 20 degC and evenly-timed
            // rain put the dry threshold at 540 mm, so 100 mm is not merely dry, it is
            // under half of half of it.
            KoeppenResult desert = KoeppenCode.Classify(KoeppenSite(27f, 0.9f, 100f));
            Check(desert.group == KoeppenGroup.Arid && desert.code == "BWh",
                  "Koeppen site: subtropical interior on 100 mm is BWh, hot desert");
            CheckWithin(desert.aridityThresholdMm, 540f, 15f,
                        "Koeppen site: its dry threshold is 20 times the mean plus 140");

            // The Mediterranean band on its maritime side. 700 mm, but the regime puts
            // nearly all of it in winter: the driest warm month is about 3 mm against a
            // wettest cool month near 114, which is the s test passing by two orders of
            // magnitude. Warmest month lands near 20.4 degC, close enough to the 22 degC
            // a/b line that the third letter is left unpinned.
            KoeppenResult mediterranean = KoeppenCode.Classify(KoeppenSite(38f, 0.3f, 700f));
            Check(mediterranean.group == KoeppenGroup.Temperate,
                  "Koeppen site: the Mediterranean band is temperate");
            CheckCodePrefix(mediterranean, "Cs",
                            "Koeppen site: and it has a dry summer");
            Check(mediterranean.driestMonthMm < AtmoConstants.KoeppenSummerDryMonthMm,
                  "Koeppen site: its driest month clears the dry-summer bar");

            // A maritime site just poleward of that band. Same tree, opposite second
            // letter: the oceanic regime's amplitude is 0.2, so every month is wet and
            // the driest of them is near 67 mm. Warmest 12.5 degC and coldest 2.6 leave
            // about 2.5 degC of margin on both the polar and the C/D lines.
            KoeppenResult oceanic = KoeppenCode.Classify(KoeppenSite(46f, 0.1f, 1000f));
            Check(oceanic.group == KoeppenGroup.Temperate,
                  "Koeppen site: a cool maritime coast is temperate");
            CheckCodePrefix(oceanic, "Cf", "Koeppen site: and it rains all year there");

            // The same latitude band pushed inland. Conrad reaches 62, which widens the
            // year to 38 degC and takes the coldest month to about -13 - the C/D line
            // crossed by a wide margin, with a summer convective maximum that is still
            // nowhere near a 10:1 seasonal ratio, so the second letter stays f.
            KoeppenResult continental = KoeppenCode.Classify(KoeppenSite(48f, 0.95f, 600f));
            Check(continental.group == KoeppenGroup.Continental,
                  "Koeppen site: the same latitude far inland is continental");
            CheckCodePrefix(continental, "Df", "Koeppen site: and it rains all year too");

            // Boreal. Warmest month near 16.4 degC clears the 10 degC polar line, and
            // only three months manage it, which is what separates a short summer from
            // a merely cool one.
            KoeppenResult boreal = KoeppenCode.Classify(KoeppenSite(68f, 1f, 400f));
            Check(boreal.group == KoeppenGroup.Continental && boreal.code == "Dfc",
                  "Koeppen site: subarctic interior is Dfc, short cool summer");
            Check(boreal.warmMonthCount < AtmoConstants.KoeppenWarmSummerMonthCount,
                  "Koeppen site: and its growing season is under four months");

            // Tundra: warmest month about 4.4 degC, above freezing and below 10.
            KoeppenResult tundra = KoeppenCode.Classify(KoeppenSite(75f, 0.5f, 250f));
            Check(tundra.group == KoeppenGroup.Polar && tundra.code == "ET",
                  "Koeppen site: 75 degrees is ET, tundra");

            // Ice cap: no month above freezing at all, by 7.5 degC.
            KoeppenResult iceCap = KoeppenCode.Classify(KoeppenSite(85f, 0f, 150f));
            Check(iceCap.group == KoeppenGroup.Polar && iceCap.code == "EF",
                  "Koeppen site: 85 degrees is EF, ice cap");

            TestKoeppenMonsoonBranch();
        }

        /// <summary>
        /// That the first letters of a code are what they should be, without pinning
        /// the ones behind them.
        /// </summary>
        private static void CheckCodePrefix(KoeppenResult r, string prefix, string message)
        {
            bool ok = r.code != null && r.code.Length >= prefix.Length
                      && r.code.Substring(0, prefix.Length) == prefix;

            Check(ok, message + " (got " + (r.code ?? "null") + ")");
        }

        /// <summary>
        /// That all three tropical outcomes are reachable, Am included.
        ///
        /// WHY THIS IS A SCAN AND NOT A SITE
        /// Am sits between Af and Aw in a band whose width depends on the annual
        /// total: the monsoon floor falls as rainfall rises while the driest month
        /// rises, so the two lines cross over a window of a few tens of millimetres.
        /// Naming one rainfall inside that window would be a test that passes today
        /// and fails the first time an offset moves by a percent. Walking the range
        /// and asserting each outcome appears somewhere in it tests the same thing
        /// and survives a retune.
        ///
        /// The equatorial regime is used because it is the only tropical shape whose
        /// driest month lands in the window at all - the monsoon shape clamps three
        /// months to nothing, so a monsoon site is Aw at every total.
        /// </summary>
        private static void TestKoeppenMonsoonBranch()
        {
            bool sawAf = false;
            bool sawAm = false;
            bool sawAw = false;

            for (float precip = 900f; precip <= 1300f; precip += 5f)
            {
                KoeppenResult r = KoeppenCode.Classify(KoeppenSite(2f, 0.05f, precip));

                if (r.group != KoeppenGroup.Tropical) continue;

                if (r.code == "Af") sawAf = true;
                else if (r.code == "Am") sawAm = true;
                else if (r.code == "Aw" || r.code == "As") sawAw = true;
            }

            Check(sawAf, "Koeppen tropical scan: a wet enough equator reaches Af");
            Check(sawAm, "Koeppen tropical scan: the monsoon branch Am is reachable");
            Check(sawAw, "Koeppen tropical scan: a drier equator reaches savanna");
        }

        /// <summary>
        /// The diagnostic tree against the shipped per-pixel classifier.
        ///
        /// WHAT THIS CAN AND CANNOT ASSERT
        /// The two do not read the same numbers. The graph node has one annual mean
        /// and one annual total per pixel and sorts on 0, 8 and 18 degC; the tree here
        /// reads twelve months and sorts on the coldest month against 0 and 18 and the
        /// warmest against 10. An annual mean cannot see a month, so the two must
        /// diverge wherever the annual swing is wide - that is not drift, it is the
        /// difference the node's own comment admits to.
        ///
        /// So the agreement is asserted on sites where the swing is narrow enough that
        /// both are looking at the same climate, and the two divergences the wide-swing
        /// cases produce are asserted as well. Pinning a divergence is worth as much as
        /// pinning an agreement: if one of them ever moves, something changed in a tree
        /// that nothing else in the toolkit would have told us about.
        /// </summary>
        private static void TestKoeppenAgreementWithGraphNode()
        {
            // The values are shared so no translation table has to sit between them
            // and go stale. Asserted rather than trusted, because the two enums live
            // in different namespaces and nothing in the compiler ties them together.
            Check((int)KoeppenGroup.Tropical
                      == (int)KoeppenBiomeClassifierNode.KoeppenClass.Tropical &&
                  (int)KoeppenGroup.Arid
                      == (int)KoeppenBiomeClassifierNode.KoeppenClass.Arid &&
                  (int)KoeppenGroup.Temperate
                      == (int)KoeppenBiomeClassifierNode.KoeppenClass.Temperate &&
                  (int)KoeppenGroup.Continental
                      == (int)KoeppenBiomeClassifierNode.KoeppenClass.Continental &&
                  (int)KoeppenGroup.Polar
                      == (int)KoeppenBiomeClassifierNode.KoeppenClass.Polar,
                  "Koeppen cross-check: the two group enums still share their values");

            CheckNodeAgrees(2f, 0.05f, 2500f, "wet equator");
            CheckNodeAgrees(15f, 0.4f, 1800f, "monsoon latitude");
            CheckNodeAgrees(27f, 0.9f, 100f, "hot desert");
            CheckNodeAgrees(38f, 0.3f, 700f, "Mediterranean");
            CheckNodeAgrees(48f, 0.95f, 600f, "continental interior");
            CheckNodeAgrees(75f, 0.5f, 250f, "tundra");

            // The two divergences, each with the reason it exists.
            //
            // A cool maritime coast: annual mean 7.5 degC, which is under the node's
            // 8 degC line, so it reads continental. The coldest month is +2.6, which is
            // over the published 0 degC line, so the tree reads temperate. Both are
            // right about the number they were given.
            CheckNodeDiffers(46f, 0.1f, 1000f,
                             KoeppenGroup.Temperate,
                             KoeppenBiomeClassifierNode.KoeppenClass.Continental,
                             "a maritime coast whose mean is colder than its winter");

            // Subarctic: annual mean -6.2 degC, below the node's 0 degC line, so it
            // reads polar. The warmest month is +16.4, well over the published 10 degC
            // line, so the tree reads continental. A 45 degC annual swing is exactly
            // the case an annual mean cannot represent.
            CheckNodeDiffers(68f, 1f, 400f,
                             KoeppenGroup.Continental,
                             KoeppenBiomeClassifierNode.KoeppenClass.Polar,
                             "a subarctic interior with a real summer");
        }

        /// <summary>Both classifiers, over the same site, landing on the same group.</summary>
        private static void CheckNodeAgrees(float latitudeDeg, float continentality01,
                                            float annualPrecipMm, string label)
        {
            ClimateNormals n = KoeppenSite(latitudeDeg, continentality01, annualPrecipMm);
            KoeppenResult mine = KoeppenCode.Classify(n);

            int node = KoeppenBiomeClassifierNode.Classify(n.meanAnnualTempC,
                                                           n.annualPrecipMm);

            Check((int)mine.group == node,
                  "Koeppen cross-check: " + label + " reads the same group both ways" +
                  " (tree " + mine.code + ", node " +
                  ((KoeppenBiomeClassifierNode.KoeppenClass)node) + ")");
        }

        /// <summary>
        /// A site the two are expected to read differently, pinned to the pair of
        /// answers that were reasoned about rather than left to whatever they produce.
        /// </summary>
        private static void CheckNodeDiffers(float latitudeDeg, float continentality01,
                                             float annualPrecipMm,
                                             KoeppenGroup expectedTree,
                                             KoeppenBiomeClassifierNode.KoeppenClass expectedNode,
                                             string label)
        {
            ClimateNormals n = KoeppenSite(latitudeDeg, continentality01, annualPrecipMm);
            KoeppenResult mine = KoeppenCode.Classify(n);

            int node = KoeppenBiomeClassifierNode.Classify(n.meanAnnualTempC,
                                                           n.annualPrecipMm);

            Check(mine.group == expectedTree && node == (int)expectedNode,
                  "Koeppen cross-check: " + label + " is a known divergence, " +
                  expectedTree + " against " + expectedNode);
        }

        /// <summary>
        /// The done-when, and then everything the tree must never do.
        ///
        /// The done-when is that the code moves sensibly as latitude, altitude and
        /// precipitation move - so each of the three is swept on its own with the
        /// other two held still, which is also the only way to tell a dial that works
        /// from one whose effect is being supplied by its neighbour.
        /// </summary>
        private static void TestKoeppenRobustness()
        {
            TestKoeppenLatitudeSweep();
            TestKoeppenAltitudeSweep();
            TestKoeppenPrecipitationSweep();
            TestKoeppenHemisphereSymmetry();
            TestKoeppenDegenerateInputs();
        }

        /// <summary>
        /// Latitude, from the equator to the pole at one continentality and one annual
        /// total. The equator must be tropical, the pole must be polar, and polar must
        /// not un-happen on the way there - a sweep that flickers back out of E would
        /// mean the warmest month is not monotone in latitude, which would be a fault
        /// in the zonal profile rather than in the tree.
        /// </summary>
        private static void TestKoeppenLatitudeSweep()
        {
            string previous = null;
            int distinctCodes = 0;
            bool reachedPolar = false;
            bool leftPolarAgain = false;
            KoeppenGroup atEquator = KoeppenGroup.Polar;
            KoeppenGroup atPole = KoeppenGroup.Tropical;

            for (float lat = 0f; lat <= 85f; lat += 5f)
            {
                KoeppenResult r = KoeppenCode.Classify(KoeppenSite(lat, 0.5f, 800f));

                if (r.code != previous)
                {
                    distinctCodes++;
                    previous = r.code;
                }

                if (r.group == KoeppenGroup.Polar) reachedPolar = true;
                else if (reachedPolar) leftPolarAgain = true;

                if (lat == 0f) atEquator = r.group;
                atPole = r.group;
            }

            Check(atEquator == KoeppenGroup.Tropical,
                  "Koeppen latitude sweep: the wet equator is tropical");
            Check(atPole == KoeppenGroup.Polar,
                  "Koeppen latitude sweep: 85 degrees is polar");
            Check(!leftPolarAgain,
                  "Koeppen latitude sweep: polar is not left again once reached");
            Check(distinctCodes >= 4,
                  "Koeppen latitude sweep: the code moves through at least four " +
                  "climates (saw " + distinctCodes + ")");
        }

        /// <summary>
        /// Altitude at one latitude. Four kilometres of lapse rate is 26 degC, which is
        /// more than the whole latitude range of the tropics, so a tropical site must
        /// end up polar and every step must be colder than the last.
        /// </summary>
        private static void TestKoeppenAltitudeSweep()
        {
            float previousWarmest = float.MaxValue;
            bool monotone = true;
            string lowCode = null;
            KoeppenGroup highGroup = KoeppenGroup.Tropical;

            for (float altitude = 0f; altitude <= 4000f; altitude += 1000f)
            {
                ClimateNormals n = KoeppenSiteAt(20f, 0.5f, 1200f, altitude);
                KoeppenResult r = KoeppenCode.Classify(n);

                if (n.warmestMonthTempC >= previousWarmest) monotone = false;
                previousWarmest = n.warmestMonthTempC;

                if (altitude == 0f) lowCode = r.code;
                highGroup = r.group;
            }

            Check(monotone,
                  "Koeppen altitude sweep: every kilometre up is a cooler summer");
            Check(highGroup == KoeppenGroup.Polar,
                  "Koeppen altitude sweep: four kilometres up at 20 degrees is polar");
            Check(lowCode != null && lowCode != "ET" && lowCode != "EF",
                  "Koeppen altitude sweep: and the same site at sea level is not");
        }

        /// <summary>
        /// Rainfall at one site. The dry threshold here works out at about 505 mm and
        /// does not move with the total - both regimes this site can take put their
        /// rain evenly enough to pick the same seasonal offset - so the sweep must cross
        /// out of arid exactly once and stay out.
        /// </summary>
        private static void TestKoeppenPrecipitationSweep()
        {
            bool aridWhenDry = false;
            bool aridWhenWet = false;
            bool returnedToArid = false;
            bool leftArid = false;

            for (float precip = 50f; precip <= 2000f; precip += 50f)
            {
                KoeppenResult r = KoeppenCode.Classify(KoeppenSite(30f, 0.6f, precip));
                bool arid = r.group == KoeppenGroup.Arid;

                if (precip == 50f) aridWhenDry = arid;
                if (precip == 2000f) aridWhenWet = arid;

                if (!arid) leftArid = true;
                else if (leftArid) returnedToArid = true;
            }

            Check(aridWhenDry,
                  "Koeppen rainfall sweep: 50 mm at 30 degrees is a dry climate");
            Check(!aridWhenWet,
                  "Koeppen rainfall sweep: 2000 mm at the same site is not");
            Check(!returnedToArid,
                  "Koeppen rainfall sweep: it crosses out of arid once and stays out");
        }

        /// <summary>
        /// The same site north and south of the equator, landing on the same code.
        ///
        /// This is the property the searched warm half exists to provide. Every
        /// seasonal test in the tree - the aridity offset, the s and w letters, the
        /// savanna split - is phrased against the warm half rather than against named
        /// months, so a mirrored world must read identically. Anything that hard-coded
        /// a calendar would fail exactly here.
        /// </summary>
        private static void TestKoeppenHemisphereSymmetry()
        {
            CheckMirrorsMatch(2f, 0.05f, 2500f, "equatorial");
            CheckMirrorsMatch(15f, 0.4f, 1800f, "monsoon");
            CheckMirrorsMatch(38f, 0.3f, 700f, "Mediterranean");
            CheckMirrorsMatch(48f, 0.95f, 600f, "continental");
            CheckMirrorsMatch(68f, 1f, 400f, "subarctic");
        }

        /// <summary>One site and its mirror, code for code.</summary>
        private static void CheckMirrorsMatch(float latitudeDeg, float continentality01,
                                              float annualPrecipMm, string label)
        {
            KoeppenResult north =
                KoeppenCode.Classify(KoeppenSite(latitudeDeg, continentality01,
                                                 annualPrecipMm));
            KoeppenResult south =
                KoeppenCode.Classify(KoeppenSite(-latitudeDeg, continentality01,
                                                 annualPrecipMm));

            Check(north.code == south.code && north.group == south.group,
                  "Koeppen symmetry: the " + label + " site reads the same in both " +
                  "hemispheres (" + north.code + " / " + south.code + ")");
        }

        /// <summary>
        /// Every input a world can actually hold, including the ones nobody would
        /// author on purpose: both poles, no rain at all, and a fully saturated
        /// continentality dial. Nothing here may produce a NaN, an empty code or a
        /// term outside its own range, because a panel that prints "NaN" teaches an
        /// author to stop reading the panel.
        /// </summary>
        private static void TestKoeppenDegenerateInputs()
        {
            for (float lat = -90f; lat <= 90f; lat += 30f)
            {
                for (int ci = 0; ci <= 2; ci++)
                {
                    float continentality = ci * 0.5f;

                    CheckKoeppenWellFormed(lat, continentality, 0f);
                    CheckKoeppenWellFormed(lat, continentality, 250f);
                    CheckKoeppenWellFormed(lat, continentality, 2500f);
                }
            }

            // The rainless pair, pinned because they look inconsistent and are not.
            //
            // A rainless site with a mean above freezing is a desert, and this one is:
            // 20 times 6.1 degC leaves a threshold of 122 mm and no rain is under it.
            KoeppenResult coldDesert = KoeppenCode.Classify(KoeppenSite(48f, 0.95f, 0f));
            Check(coldDesert.group == KoeppenGroup.Arid && coldDesert.code == "BWk",
                  "Koeppen degenerate: a rainless site above freezing is BWk");

            // A rainless site with a mean BELOW freezing is not, because 20 times a
            // negative mean is a negative threshold and no rainfall can be under it.
            // That is the published rule behaving as written rather than a hole in it:
            // Koeppen's B class is a statement about rain against evaporative demand,
            // and where the demand is nil the class does not apply. It is why the real
            // scheme calls Antarctica EF and Verkhoyansk Dfd, neither of them desert,
            // despite both being drier than much of the Sahara.
            KoeppenResult frozenDry = KoeppenCode.Classify(KoeppenSite(68f, 1f, 0f));
            Check(frozenDry.group != KoeppenGroup.Arid,
                  "Koeppen degenerate: a rainless site below freezing is not a desert");
            Check(frozenDry.aridityThresholdMm < 0f,
                  "Koeppen degenerate: because its dry threshold is itself negative");

            // Same input twice, same answer. Cheap, and the only guard against a term
            // that quietly picked up state.
            ClimateNormals twice = KoeppenSite(38f, 0.3f, 700f);
            Check(KoeppenCode.Classify(twice).code == KoeppenCode.Classify(twice).code,
                  "Koeppen degenerate: classifying the same year twice reads the same");

            // A null year is a caller's bug, not a crash.
            KoeppenResult nothing = KoeppenCode.Classify(null);
            Check(nothing.code == null,
                  "Koeppen degenerate: a missing year classifies to nothing at all");
        }

        /// <summary>
        /// One site, with every reported term inside the range its own documentation
        /// claims for it.
        /// </summary>
        private static void CheckKoeppenWellFormed(float latitudeDeg,
                                                  float continentality01,
                                                  float annualPrecipMm)
        {
            KoeppenResult r =
                KoeppenCode.Classify(KoeppenSite(latitudeDeg, continentality01,
                                                 annualPrecipMm));

            string where = "lat " + latitudeDeg.ToString("0") +
                           ", continentality " + continentality01.ToString("0.00") +
                           ", " + annualPrecipMm.ToString("0") + " mm";

            Check(!string.IsNullOrEmpty(r.code) && r.code.Length >= 2,
                  "Koeppen well-formed: " + where + " produces a code");
            Check(r.group >= KoeppenGroup.Tropical && r.group <= KoeppenGroup.Polar,
                  "Koeppen well-formed: " + where + " produces a known group");
            Check(IsFinite(r.aridityThresholdMm),
                  "Koeppen well-formed: " + where + " has a finite dry threshold");
            Check(IsFinite(r.warmHalfPrecipShare01) &&
                  r.warmHalfPrecipShare01 >= 0f && r.warmHalfPrecipShare01 <= 1.001f,
                  "Koeppen well-formed: " + where + " has a warm-half share in range");
            Check(r.warmHalfStartMonth >= 0 &&
                  r.warmHalfStartMonth < AtmoConstants.MonthsPerYear,
                  "Koeppen well-formed: " + where + " has a real warm-half start");
            Check(r.warmMonthCount >= 0 &&
                  r.warmMonthCount <= AtmoConstants.MonthsPerYear,
                  "Koeppen well-formed: " + where + " has a countable warm season");
            Check(IsFinite(r.driestMonthMm) && IsFinite(r.wettestMonthMm) &&
                  r.driestMonthMm <= r.wettestMonthMm && r.driestMonthMm >= 0f,
                  "Koeppen well-formed: " + where + " has a driest month no wetter " +
                  "than its wettest");
        }

        // =====================================================================
        //  THE PRESSURE LATTICE
        //
        //  WHAT CAN BE ASSERTED YET AND WHAT CANNOT. The field's own promise - the
        //  same world at the same day and hour gives back the same weather - is
        //  testable today. The half of it an author actually sees, a falling
        //  barometer followed by thickening cloud, rain and a wind shift, is not:
        //  cloud and wind are not written yet. So everything the field is
        //  responsible for on its own is pinned here, and the sequence is left to
        //  the tasks that own the other half of it.
        //
        //  WHY THE SWEEPS ACCUMULATE INSTEAD OF ASSERTING PER SAMPLE. A couple of
        //  thousand samples is what it takes for "the highs and the lows balance"
        //  to mean anything at all, and two thousand Check calls for one property
        //  would bury every other line in the report.
        // =====================================================================

        private const int PressureSeed = 90210;
        private const float PressureTestLatitudeDeg = 45f;

        private static void TestWeatherCellField()
        {
            TestPressureSteering();
            TestPressureFieldStructure();
            TestPressureFieldContinuity();
            TestPressureFieldTravels();
            TestGeneralCirculation();
            TestWeatherWind();
            TestWeatherFronts();
            TestCloudCover();
            TestCloudBase();
            TestWeatherStability();
            TestWeatherLocalWind();
            TestWeatherHumidity();
            TestWeatherPrecipType();
            TestWeatherPrecipRate();
            TestWeatherState();
            TestWeatherMachine();
            TestWeatherLiveWireIntoManager();
            TestWeatherStorm();
            TestWeatherAirMass();
            TestWeatherPrecipEmitter();
            TestWetnessSimulation();
            TestWetnessCoupling();
            TestPuddling();
            TestSurfaceFriction();
            TestWaterWeatherCoupling();
            TestFogSimulation();
            TestWeatherVisibility();
            TestSnowModel();
            TestSnowSplatMutation();
            TestWeatherDeterminism();
            TestWeatherDomainSweep();
            TestWeatherPresets();
            TestWeatherQualityTiers();
        }

        /// <summary>
        /// The nine climate presets each build a full year that is finite and distinct.
        ///
        /// A preset sets only geography; the year's shape is the § 7.2 physics. This
        /// asserts the two ends the task names: no preset produces a NaN across twelve
        /// months (the Polar one runs the coldest path and is the classic offender), and
        /// no two presets collapse onto the same climate - a preset menu with duplicate
        /// entries is worse than none.
        /// </summary>
        private static void TestWeatherPresets()
        {
            System.Array kinds = System.Enum.GetValues(typeof(WeatherPresetKind));
            float[] meanTemp = new float[kinds.Length];
            float[] annualPrecip = new float[kinds.Length];

            for (int i = 0; i < kinds.Length; i++)
            {
                WeatherPresetKind kind = (WeatherPresetKind)kinds.GetValue(i);
                WeatherPreset preset = WeatherPreset.For(kind);

                Check(preset.kind == kind,
                      "WeatherPreset.For must tag the preset with the kind it built: " + kind);

                ClimateRequest request = preset.ToClimateRequest(EcoConstants.GddBaseTemperateC);
                Check(request.regimeIsAuthored,
                      "A preset request must author its regime, not leave it derived: " + kind);

                ClimateNormals n = ClimateNormals.Build(request);

                for (int m = 0; m < AtmoConstants.MonthsPerYear; m++)
                {
                    Check(IsFinite(n.monthlyMeanTempC[m]) && IsFinite(n.monthlyMinTempC[m])
                          && IsFinite(n.monthlyMaxTempC[m]),
                          "Preset " + kind + " month " + m + " has a non-finite temperature");
                    Check(IsFinite(n.monthlyPrecipMm[m]) && n.monthlyPrecipMm[m] >= 0f,
                          "Preset " + kind + " month " + m + " has a bad precipitation total");
                    Check(IsFinite(n.monthlyRadiationMj[m]) && IsFinite(n.monthlySurfaceRadiationMj[m]),
                          "Preset " + kind + " month " + m + " has a non-finite radiation");
                }

                Check(IsFinite(n.meanAnnualTempC) && IsFinite(n.annualPrecipMm)
                      && IsFinite(n.annualPetMm) && IsFinite(n.aridity)
                      && IsFinite(n.gddAnnual) && IsFinite(n.continentality),
                      "Preset " + kind + " produced a non-finite annual aggregate");

                meanTemp[i] = n.meanAnnualTempC;
                annualPrecip[i] = n.annualPrecipMm;
            }
            // Distinguishable: no two presets may land on the same climate. Comparing the
            // annual mean temperature together with the annual rainfall separates every
            // pair the table defines - two archetypes at a like temperature part on rain,
            // and two at a like rainfall part on temperature.
            for (int a = 0; a < kinds.Length; a++)
            {
                for (int b = a + 1; b < kinds.Length; b++)
                {
                    bool sameTemp = Mathf.Abs(meanTemp[a] - meanTemp[b]) < 0.5f;
                    bool samePrecip = Mathf.Abs(annualPrecip[a] - annualPrecip[b]) < 1f;
                    Check(!(sameTemp && samePrecip),
                          "Presets " + (WeatherPresetKind)kinds.GetValue(a) + " and "
                          + (WeatherPresetKind)kinds.GetValue(b)
                          + " produce an indistinguishable climate");
                }
            }
        }

        /// <summary>
        /// The five quality rows are reachable, ordered, and read one tier source.
        ///
        /// The table combines a device class with a recommended tier; this asserts the
        /// mapping honours both without a second tier enum. Emitters and opacity only
        /// climb, a mobile build never runs volumetrics, mobile tops out at the flagship
        /// row, and desktop unlocks volumetrics only at Ultra - so every row an author
        /// can override to is present and none blends into another.
        /// </summary>
        private static void TestWeatherQualityTiers()
        {
            WeatherQuality mLow = WeatherQuality.For(
                UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Low, true);
            WeatherQuality mMed = WeatherQuality.For(
                UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Medium, true);
            WeatherQuality mHigh = WeatherQuality.For(
                UniverseCreator.Optimization.AdaptiveQuality.QualityTier.High, true);
            WeatherQuality mUltra = WeatherQuality.For(
                UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Ultra, true);
            WeatherQuality dHigh = WeatherQuality.For(
                UniverseCreator.Optimization.AdaptiveQuality.QualityTier.High, false);
            WeatherQuality dUltra = WeatherQuality.For(
                UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Ultra, false);

            Check(mLow.maxEmitters == 2 && mMed.maxEmitters == 4 && mHigh.maxEmitters == 6
                  && dHigh.maxEmitters == 10 && dUltra.maxEmitters == 12,
                  "All five quality rows must be reachable by tier and device class");

            Check(mLow.maxEmitters <= mMed.maxEmitters && mMed.maxEmitters <= mHigh.maxEmitters,
                  "Mobile emitter counts must not fall as the tier rises");
            Check(mLow.precipOpacity <= mMed.precipOpacity
                  && mMed.precipOpacity <= mHigh.precipOpacity,
                  "Mobile precip opacity must not fall as the tier rises");

            Check(mUltra.maxEmitters == mHigh.maxEmitters
                  && mUltra.cloudShadows == mHigh.cloudShadows,
                  "A mobile Ultra recommendation must cap at the flagship mobile row");

            Check(!mLow.volumetric && !mMed.volumetric && !mHigh.volumetric && !mUltra.volumetric,
                  "No mobile tier may run volumetrics");
            Check(!dHigh.volumetric && dUltra.volumetric,
                  "Volumetrics unlock only at desktop Ultra");

            Check(WeatherQuality.Current(null).maxEmitters > 0,
                  "Current must return a usable bundle when no AdaptiveQuality is present");
        }

        /// <summary>
        /// The weather carries no memory: it is a closed-form function of place, clock
        /// and seed, so running the clock away and back must land on the identical sky.
        ///
        /// The first half runs the whole time-parameterised resolver stack - pressure,
        /// surface wind, front, cloud and humidity - at one instant, jumps the clock a
        /// hundred days on to give any hidden accumulator a chance to drift, then reads
        /// the same instant again and asserts every quantity is bit-identical, not
        /// merely close. The clock-moved check guards the trivial pass where nothing
        /// reads the time at all. The second half is the smoother: a machine created
        /// fresh at an instant and one that reached it through a real transition land on
        /// the same blended sky once the transition window has elapsed, so the path
        /// taken to get there is forgotten within the smoother's own tolerance.
        /// </summary>
        private static void TestWeatherDeterminism()
        {
            const float DetermLat = 45f;
            const int Sites = 8;
            const float SiteStrideX = 1.7f;
            const float SiteStrideZ = 2.3f;
            const float FreshDay = 200f;
            const float FreshHour = 14f;
            const float JumpDays = 100f;

            var field = new WeatherCellField();
            field.Bind(PressureSeed);
            float density = WindTestDensity();
            float cell = PressureCellM();
            float tilt = AtmoConstants.EarthAxialTiltDegrees;

            float whenHere = FreshDay + FreshHour / ClockState.HoursPerDay;
            float whenAway = whenHere + JumpDays;
            float itczHere = WeatherCellField.ItczLatitudeDeg((int)whenHere, tilt);
            float itczAway = WeatherCellField.ItczLatitudeDeg((int)whenAway, tilt);

            bool reproduced = true;
            bool clockMoved = false;
            bool allFinite = true;

            for (int i = 0; i < Sites; i++)
            {
                float x = i * SiteStrideX * cell;
                float z = i * SiteStrideZ * cell;

                // The closed-form quantities at the fresh instant.
                float p0 = field.PressureAnomalyHpa(x, z, DetermLat, whenHere);
                Vector2 w0 = WeatherFront.SurfaceWindWorldMPerS(
                    field, x, z, DetermLat, whenHere, density);
                WeatherFront.FrontReading f0 =
                    WeatherFront.ResolveFront(field, x, z, DetermLat, whenHere);
                float c0 = CloudCover.TotalCover01(field, x, z, DetermLat, itczHere, whenHere);
                float h0 = WeatherHumidity.Resolve01(
                    field, x, z, DetermLat, whenHere, 0.5f, 20f);

                allFinite &= IsFinite(p0) && IsFinite(w0.x) && IsFinite(w0.y)
                             && IsFinite(c0) && IsFinite(h0) && IsFinite(f0.DepthHpa);

                // A hundred days on: only run to move any accumulator that might exist.
                float pAway = field.PressureAnomalyHpa(x, z, DetermLat, whenAway);
                WeatherFront.SurfaceWindWorldMPerS(field, x, z, DetermLat, whenAway, density);
                WeatherFront.ResolveFront(field, x, z, DetermLat, whenAway);
                CloudCover.TotalCover01(field, x, z, DetermLat, itczAway, whenAway);
                WeatherHumidity.Resolve01(field, x, z, DetermLat, whenAway, 0.5f, 20f);
                if (pAway != p0) clockMoved = true;

                // Back to the fresh instant: every quantity must be exactly what it was.
                float p1 = field.PressureAnomalyHpa(x, z, DetermLat, whenHere);
                Vector2 w1 = WeatherFront.SurfaceWindWorldMPerS(
                    field, x, z, DetermLat, whenHere, density);
                WeatherFront.FrontReading f1 =
                    WeatherFront.ResolveFront(field, x, z, DetermLat, whenHere);
                float c1 = CloudCover.TotalCover01(field, x, z, DetermLat, itczHere, whenHere);
                float h1 = WeatherHumidity.Resolve01(
                    field, x, z, DetermLat, whenHere, 0.5f, 20f);

                if (p1 != p0 || w1.x != w0.x || w1.y != w0.y || c1 != c0 || h1 != h0
                    || f1.DepthHpa != f0.DepthHpa || f1.Kind != f0.Kind
                    || f1.Phase != f0.Phase
                    || f1.TendencyHpaPerDay != f0.TendencyHpaPerDay)
                    reproduced = false;
            }

            Check(allFinite,
                  "Determinism: every closed-form quantity at the fresh instant is finite");
            Check(clockMoved,
                  "Determinism: a hundred days on is a different sky, so the clock is "
                  + "genuinely read and not ignored");
            Check(reproduced,
                  "Determinism: jumping the clock forward a hundred days and back "
                  + "reproduces the weather exactly, to the bit");

            // __DETERMINISM_SMOOTHER_TAIL__

            // The smoother is the one piece that carries state, and it must forget the
            // path once its transition is spent. One machine reaches a settled rain sky
            // through a real transition out of a clear one; a second is created fresh at
            // that same instant. Stepped well past the dwell and the transition window,
            // the two read the same blended sky - a world loaded fresh matches one that
            // ran there in real time, to the smoother's tolerance.
            const float Transition = 10f;
            const float Dwell = 30f;
            const float SettleSeconds = 200f;
            const float StepSeconds = 5f;

            WeatherState clearSky = MachineSample(0f, 10f, 0.2f, 15f);
            WeatherState rainSky = MachineSample(3f, 5f, 0.85f, 9f);

            WeatherMachine ranThere = new WeatherMachine(Transition, Dwell);
            ranThere.Tick(clearSky, 0f);
            for (float t = 0f; t <= SettleSeconds; t += StepSeconds)
                ranThere.Tick(rainSky, t);
            WeatherState ranOut = ranThere.Tick(rainSky, SettleSeconds);

            WeatherMachine fresh = new WeatherMachine(Transition, Dwell);
            WeatherState freshOut = fresh.Tick(rainSky, SettleSeconds);

            Check(ranThere.CurrentKind == fresh.CurrentKind,
                  "Determinism: the settled machine and the fresh one hold the same "
                  + "weather state");
            CheckClose(ranOut.temperature, freshOut.temperature,
                       "Determinism: a fresh load matches a real-time run in temperature");
            CheckClose(ranOut.cloudCover, freshOut.cloudCover,
                       "Determinism: a fresh load matches a real-time run in cloud cover");
            CheckClose(ranOut.precipitationMmPerHour, freshOut.precipitationMmPerHour,
                       "Determinism: a fresh load matches a real-time run in the rain rate");
        }

        /// <summary>
        /// The whole published sky is swept for non-finite output. The grid is the full
        /// habitable range - latitude -90..+90 in one-degree steps by day 1..365 in
        /// one-day steps, some sixty-six thousand places - and at each the seasonal and
        /// synoptic quantities that carry the trigonometric traps are read and asserted
        /// finite: the solar declination, the day length whose acos meets tan(phi) at the
        /// poles, the noon elevation, the migrating pressure trough, the zonal sea-level
        /// pressure and the synoptic anomaly on top of it. A single running tally keeps
        /// the test cheap; only the first offending place is remembered to name it.
        ///
        /// The named point traps are then hit head-on, off the grid: Stull's wet bulb
        /// across humidity down to zero and out past its published range, the Sundqvist
        /// cloud root swept through its critical humidity to saturation, the topographic
        /// wetness index on dead-flat ground where the tangent would drive its log to
        /// infinity, and the Coriolis magnitude at the equator where the parameter itself
        /// goes to zero. Each must come back a real number.
        /// </summary>
        private static void TestWeatherDomainSweep()
        {
            var field = new WeatherCellField();
            field.Bind(PressureSeed);
            float tilt = AtmoConstants.EarthAxialTiltDegrees;
            float sampleX = 3.5f * PressureCellM();
            float sampleZ = 2.5f * PressureCellM();

            int samples = 0;
            int nonFinite = 0;
            int firstBadLat = 0;
            int firstBadDay = 0;

            for (int lat = -90; lat <= 90; lat++)
            {
                float latDeg = lat;
                for (int day = 1; day <= 365; day++)
                {
                    float dec = ClockMath.DeclinationDeg(day);
                    float dayLen = ClockMath.DayLengthHours(latDeg, dec);
                    float noonElev = ClockMath.ElevationDeg(latDeg, dec, 0f);
                    float itcz = WeatherCellField.ItczLatitudeDeg(day, tilt);
                    float mslp = WeatherCellField.MeanSeaLevelPressureHpa(latDeg, itcz);
                    float anom = field.PressureAnomalyHpa(
                        sampleX, sampleZ, latDeg, day + 0.5f);

                    samples++;
                    bool ok = IsFinite(dec) && IsFinite(dayLen) && IsFinite(noonElev)
                              && IsFinite(itcz) && IsFinite(mslp) && IsFinite(anom);
                    if (!ok && nonFinite == 0)
                    {
                        firstBadLat = lat;
                        firstBadDay = day;
                    }
                    if (!ok) nonFinite++;
                }
            }

            Check(samples == 181 * 365,
                  "Domain sweep: the grid covers -90..+90 by day 1..365 in full");
            Check(nonFinite == 0,
                  nonFinite == 0
                  ? "Domain sweep: no published field is NaN or infinite anywhere on the "
                    + "latitude-by-day grid"
                  : "Domain sweep: " + nonFinite + " non-finite samples, first at latitude "
                    + firstBadLat + " day " + firstBadDay);

            bool wetBulbFinite = true;
            float[] wbTemps = { -45f, -20f, 0f, 15f, 30f, 48f };
            float[] wbHum = { 0f, 0.0001f, 0.25f, 0.5f, 1f };
            for (int ti = 0; ti < wbTemps.Length; ti++)
                for (int hi = 0; hi < wbHum.Length; hi++)
                    wetBulbFinite &= IsFinite(AtmoMath.WetBulbC(wbTemps[ti], wbHum[hi]));
            Check(wetBulbFinite,
                  "Domain sweep: Stull's wet bulb is finite from zero humidity out past "
                  + "its published range");

            bool cloudFinite = true;
            for (float rh = 0f; rh <= 1f; rh += 0.02f)
                cloudFinite &= IsFinite(CloudCover.LayerCover01(rh));
            cloudFinite &= IsFinite(CloudCover.LayerCover01(AtmoConstants.SundqvistCriticalRh))
                           && IsFinite(CloudCover.LayerCover01(1f));
            Check(cloudFinite,
                  "Domain sweep: the Sundqvist cloud root stays finite through its critical "
                  + "humidity and up to saturation");

            float twiFlat = EcoMath.TopographicWetnessIndex(
                EcoConstants.TwiMinCatchmentArea, 0f);
            float twiBigFlat = EcoMath.TopographicWetnessIndex(1000f, 0f);
            Check(IsFinite(twiFlat) && IsFinite(twiBigFlat),
                  "Domain sweep: the topographic wetness index is finite on dead-flat "
                  + "ground where the tangent would drive its log to infinity");

            float coriolisEquator = WeatherFront.CoriolisMagnitudePerS(0f);
            Check(IsFinite(coriolisEquator) && coriolisEquator > 0f,
                  "Domain sweep: the Coriolis magnitude is finite and positive at the "
                  + "equator where the parameter itself goes to zero");
        }

        /// <summary>metres. The lattice pitch the field itself works in.</summary>
        private static float PressureCellM()
        {
            return AtmoConstants.WeatherCellSpacingKm
                   * AtmoConstants.MetresPerKilometreLattice;
        }

        /// <summary>
        /// The three circulation cells out of one sine. The zeros are the whole
        /// test: if the equator, the subtropical high, the subpolar low and the pole
        /// are not all still zeros, the term has been retuned into something that no
        /// longer reverses where the atmosphere reverses.
        /// </summary>
        private static void TestPressureSteering()
        {
            const float ZeroTol = 1e-3f;
            const float SpeedTol = 1e-2f;

            CheckWithin(WeatherCellField.SteeringCompassMPerS(0f).x, 0f, ZeroTol,
                        "Steering: the equator is a divide, not a jet");
            CheckWithin(WeatherCellField.SteeringCompassMPerS(
                            AtmoConstants.SubtropicalHighLatitudeDeg).x, 0f, ZeroTol,
                        "Steering: the subtropical high is a divide");
            CheckWithin(WeatherCellField.SteeringCompassMPerS(
                            AtmoConstants.SubpolarLowLatitudeDeg).x, 0f, ZeroTol,
                        "Steering: the subpolar low is a divide");
            CheckWithin(WeatherCellField.SteeringCompassMPerS(
                            ClockMath.PoleLatitudeDeg).x, 0f, ZeroTol,
                        "Steering: the pole is a divide");

            CheckWithin(WeatherCellField.SteeringCompassMPerS(15f).x,
                        -AtmoConstants.TradeWindSteeringMPerS, SpeedTol,
                        "Steering: the trades blow from the east at fifteen degrees");
            CheckWithin(WeatherCellField.SteeringCompassMPerS(45f).x,
                        AtmoConstants.WesterlySteeringMPerS, SpeedTol,
                        "Steering: weather arrives from the west at forty-five");
            CheckWithin(WeatherCellField.SteeringCompassMPerS(75f).x,
                        -AtmoConstants.PolarEasterlySteeringMPerS, SpeedTol,
                        "Steering: the polar cap turns easterly again");

            // Mirrored, not copied: westerly in both hemispheres, but a track tilts
            // towards whichever pole it is under.
            Vector2 north45 = WeatherCellField.SteeringCompassMPerS(45f);
            Vector2 south45 = WeatherCellField.SteeringCompassMPerS(-45f);

            CheckWithin(south45.x, north45.x, SpeedTol,
                        "Steering: the westerlies are westerly south of the equator");
            CheckWithin(south45.y, -north45.y, SpeedTol,
                        "Steering: the poleward tilt flips with the hemisphere");
            CheckWithin(north45.y,
                        north45.x * AtmoConstants.SynopticTrackTiltFraction, SpeedTol,
                        "Steering: the tilt is a fraction of the flow, not a term of "
                        + "its own");
            Check(Mathf.Abs(north45.y) < Mathf.Abs(north45.x),
                  "Steering: a track tilts, it does not turn");
            // The rotation into world XZ. Bearing zero has to be the identity or
            // every world authored before the bearing existed would have moved.
            WeatherCellField due = new WeatherCellField();
            due.Bind(PressureSeed, 0f);

            Vector2 world45 = due.SteeringWorldMPerS(45f);
            CheckWithin(world45.x, north45.x, SpeedTol,
                        "Steering: at bearing zero, east is +X");
            CheckWithin(world45.y, north45.y, SpeedTol,
                        "Steering: at bearing zero, north is +Z");

            // A quarter turn clockwise puts north along +X, so the eastward
            // component has to come out along -Z.
            WeatherCellField turned = new WeatherCellField();
            turned.Bind(PressureSeed, AtmoConstants.DegreesPerCircle * 0.25f);

            Vector2 turned45 = turned.SteeringWorldMPerS(45f);
            CheckWithin(turned45.x, north45.y, SpeedTol,
                        "Steering: a quarter turn sends the tilt down +X");
            CheckWithin(turned45.y, -north45.x, SpeedTol,
                        "Steering: a quarter turn sends the westerlies down -Z");

            // Any other bearing: the check that it is a rotation and not a shear.
            WeatherCellField oblique = new WeatherCellField();
            oblique.Bind(PressureSeed, 37f);

            CheckWithin(oblique.SteeringWorldMPerS(45f).magnitude, north45.magnitude,
                        SpeedTol,
                        "Steering: turning the compass cannot change the wind speed");
        }

        /// <summary>
        /// Determinism, the search-width invariant, and the shape of the field taken
        /// as a whole. The sweep is wide on purpose: a mean near zero only means the
        /// highs and the lows balance if enough of both have been passed over.
        /// </summary>
        private static void TestPressureFieldStructure()
        {
            const int GridSteps = 12;
            const int TimeSteps = 12;
            const float StartDay = 400f;
            const float DayStep = 1.7f;
            const float StrideX = 1.3f;
            const float StrideZ = 1.7f;
            const float CeilingFactor = 4f;

            WeatherCellField unbound = new WeatherCellField();
            Check(!unbound.IsBound, "Pressure field: a fresh field reports unbound");
            CheckClose(unbound.PressureAnomalyHpa(1000f, 2000f,
                                                  PressureTestLatitudeDeg, 100f), 0f,
                       "Pressure field: an unbound field is flat, not a throw");

            // The one invariant that cannot be seen by looking at output: a centre
            // this many cells away can still reach the sample point, one cell
            // further cannot, so a wider radius without a wider search would
            // truncate the field silently.
            Check(AtmoConstants.SynopticSearchCells
                  * AtmoConstants.WeatherCellSpacingKm
                  >= AtmoConstants.SynopticRadiusMaxKm,
                  "Pressure field: the neighbourhood search covers the widest system "
                  + "the model can draw");

            WeatherCellField a = new WeatherCellField();
            a.Bind(PressureSeed);
            Check(a.IsBound, "Pressure field: binding takes");

            WeatherCellField again = new WeatherCellField();
            again.Bind(PressureSeed);

            WeatherCellField elsewhere = new WeatherCellField();
            elsewhere.Bind(PressureSeed + 1);

            float cellM = PressureCellM();

            bool allFinite = true;
            bool reproduced = true;
            bool differs = false;
            float sum = 0f;
            float lo = float.MaxValue;
            float hi = -float.MaxValue;
            int count = 0;
            for (int ti = 0; ti < TimeSteps; ti++)
            {
                float when = StartDay + ti * DayStep;

                for (int iz = 0; iz < GridSteps; iz++)
                {
                    for (int ix = 0; ix < GridSteps; ix++)
                    {
                        float x = ix * cellM * StrideX;
                        float z = iz * cellM * StrideZ;

                        float here = a.PressureAnomalyHpa(
                            x, z, PressureTestLatitudeDeg, when);

                        if (!IsFinite(here)) allFinite = false;

                        // Bit-identical, not close: a rebind of the same seed that
                        // only nearly agrees is a field with hidden state in it.
                        if (here != again.PressureAnomalyHpa(
                                x, z, PressureTestLatitudeDeg, when))
                            reproduced = false;

                        if (here != elsewhere.PressureAnomalyHpa(
                                x, z, PressureTestLatitudeDeg, when))
                            differs = true;

                        sum += here;
                        if (here < lo) lo = here;
                        if (here > hi) hi = here;
                        count++;
                    }
                }
            }

            Check(allFinite, "Pressure field: every sample over the sweep is finite");
            Check(reproduced,
                  "Pressure field: the same seed rebuilds the same weather exactly");
            Check(differs, "Pressure field: a different seed is a different world");

            float mean = sum / count;

            Check(Mathf.Abs(mean) < AtmoConstants.SynopticAmplitudeMinHpa,
                  "Pressure field: highs and lows balance across the sweep (mean "
                  + mean.ToString("F2") + " hPa over " + count + " samples)");
            Check(hi > AtmoConstants.SynopticAmplitudeMinHpa,
                  "Pressure field: the sweep meets a real high (" + hi.ToString("F1")
                  + " hPa)");
            Check(lo < -AtmoConstants.SynopticAmplitudeMinHpa,
                  "Pressure field: the sweep meets a real low (" + lo.ToString("F1")
                  + " hPa)");

            float ceiling = AtmoConstants.SynopticAmplitudeMaxHpa * CeilingFactor;

            Check(hi < ceiling && lo > -ceiling,
                  "Pressure field: overlapping systems stack, they do not run away");
        }

        /// <summary>
        /// The claim that systems fade in and out instead of popping. A generation
        /// boundary is where a popping field would give itself away, so the step
        /// across one is measured and then compared with a step of the same length
        /// taken mid-epoch - if crossing a boundary were an event, the two would not
        /// be the same size.
        /// </summary>
        private static void TestPressureFieldContinuity()
        {
            const int Boundaries = 24;
            const int Sites = 8;
            const float ProbeDays = 1e-3f;
            const float FirstEpoch = 100f;
            const float SiteStrideX = 0.37f;
            const float SiteStrideZ = 0.91f;
            const float HandoverFactor = 4f;
            const float HandoverFloorHpa = 0.05f;
            const float NudgeM = 1000f;

            WeatherCellField field = new WeatherCellField();
            field.Bind(PressureSeed);

            float cellM = PressureCellM();

            float worstHandover = 0f;
            float worstMidEpoch = 0f;
            float worstSpatial = 0f;

            for (int b = 0; b < Boundaries; b++)
            {
                float boundaryDay = (FirstEpoch + b) * AtmoConstants.SynopticEpochDays;
                float midDay = boundaryDay + AtmoConstants.SynopticEpochDays * 0.5f;

                for (int s = 0; s < Sites; s++)
                {
                    float x = (b + s * SiteStrideX) * cellM;
                    float z = (b - s * SiteStrideZ) * cellM;

                    float step = Mathf.Abs(
                        field.PressureAnomalyHpa(x, z, PressureTestLatitudeDeg,
                                                 boundaryDay + ProbeDays)
                        - field.PressureAnomalyHpa(x, z, PressureTestLatitudeDeg,
                                                   boundaryDay - ProbeDays));

                    if (step > worstHandover) worstHandover = step;
                    float ordinary = Mathf.Abs(
                        field.PressureAnomalyHpa(x, z, PressureTestLatitudeDeg,
                                                 midDay + ProbeDays)
                        - field.PressureAnomalyHpa(x, z, PressureTestLatitudeDeg,
                                                   midDay - ProbeDays));

                    if (ordinary > worstMidEpoch) worstMidEpoch = ordinary;

                    float sideways = Mathf.Abs(
                        field.PressureAnomalyHpa(x + NudgeM, z,
                                                 PressureTestLatitudeDeg, midDay)
                        - field.PressureAnomalyHpa(x, z,
                                                   PressureTestLatitudeDeg, midDay));

                    if (sideways > worstSpatial) worstSpatial = sideways;
                }
            }
            // A pop would be a whole system switching on, which is up to the deepest
            // amplitude the model can draw. The shallowest is a generous line to
            // draw, and still nowhere near what a truncation would cost.
            Check(worstHandover < AtmoConstants.SynopticAmplitudeMinHpa,
                  "Pressure field: a generation handover cannot make a system appear "
                  + "(worst step " + worstHandover.ToString("F3") + " hPa)");

            Check(worstHandover <= worstMidEpoch * HandoverFactor + HandoverFloorHpa,
                  "Pressure field: crossing an epoch boundary is no more eventful "
                  + "than any other moment (" + worstHandover.ToString("F3")
                  + " against " + worstMidEpoch.ToString("F3") + " hPa)");

            Check(worstSpatial < AtmoConstants.SynopticAmplitudeMinHpa,
                  "Pressure field: a step sideways is a gradient, not an edge ("
                  + worstSpatial.ToString("F3") + " hPa per kilometre)");

            // The clock the field reads. A day count that is not monotone would let
            // the same world run backwards through its own weather.
            CheckClose(WeatherCellField.TimeDays(2, 5, 6f),
                       2f * AtmoConstants.DaysPerCalendarYear + 4f + 0.25f,
                       "Pressure field: a calendar reading becomes the day count it "
                       + "should");

            Check(WeatherCellField.TimeDays(1, 1, 1f)
                  > WeatherCellField.TimeDays(1, 1, 0f),
                  "Pressure field: an hour later is later");
            Check(WeatherCellField.TimeDays(1, 2, 0f)
                  > WeatherCellField.TimeDays(1, 1, ClockState.HoursPerDay - 1f),
                  "Pressure field: a day later is later");
            Check(WeatherCellField.TimeDays(2, 1, 0f)
                  > WeatherCellField.TimeDays(
                        1, (int)AtmoConstants.DaysPerCalendarYear, 0f),
                  "Pressure field: a year later is later");
        }

        /// <summary>
        /// That the systems actually travel, and that standing under them gives a
        /// barometer rather than noise. Following the steering should find much the
        /// same pattern half a day later; standing still should not, because the
        /// pattern has moved on. This is the half of the visible sequence that can be
        /// asserted before cloud and wind exist.
        /// </summary>
        private static void TestPressureFieldTravels()
        {
            const int Sites = 6;
            const int Whens = 8;
            const float FollowDays = 0.5f;
            const float StartDay = 250f;
            const float DayStep = 3.1f;
            const float SiteStride = 1.9f;
            const float TravelMargin = 1.5f;

            WeatherCellField field = new WeatherCellField();
            field.Bind(PressureSeed);

            Vector2 steer = field.SteeringWorldMPerS(PressureTestLatitudeDeg);
            float followSeconds = FollowDays * ClockState.SecondsPerDay;
            float cellM = PressureCellM();

            float followed = 0f;
            float stood = 0f;

            for (int w = 0; w < Whens; w++)
            {
                float when = StartDay + w * DayStep;

                for (int s = 0; s < Sites; s++)
                {
                    float x = s * cellM * SiteStride;
                    float z = -s * cellM * SiteStride;

                    float before = field.PressureAnomalyHpa(
                        x, z, PressureTestLatitudeDeg, when);

                    stood += Mathf.Abs(field.PressureAnomalyHpa(
                        x, z, PressureTestLatitudeDeg, when + FollowDays) - before);

                    followed += Mathf.Abs(field.PressureAnomalyHpa(
                        x + steer.x * followSeconds, z + steer.y * followSeconds,
                        PressureTestLatitudeDeg, when + FollowDays) - before);
                }
            }

            Check(followed * TravelMargin < stood,
                  "Pressure field: systems travel with the steering ("
                  + followed.ToString("F1") + " hPa of change following them against "
                  + stood.ToString("F1") + " standing still)");
            // Stand still and let a month run. What the barometer must not be is
            // either flat or jittery: too few turning points and no weather is
            // arriving, too many and the trace is noise wearing weather's clothes.
            const int TraceSamples = 301;
            const float TraceStepDays = 0.1f;
            const float SlopeFloorHpa = 1e-3f;
            const int TraceTurnMinimum = 3;
            const int TraceTurnDivisor = 3;

            float traceLo = float.MaxValue;
            float traceHi = -float.MaxValue;
            float previous = 0f;
            float previousSlope = 0f;
            int turns = 0;

            for (int i = 0; i < TraceSamples; i++)
            {
                float here = field.PressureAnomalyHpa(
                    0f, 0f, PressureTestLatitudeDeg, StartDay + i * TraceStepDays);

                if (here < traceLo) traceLo = here;
                if (here > traceHi) traceHi = here;

                if (i > 0)
                {
                    float slope = here - previous;

                    if (Mathf.Abs(slope) > SlopeFloorHpa
                        && Mathf.Abs(previousSlope) > SlopeFloorHpa
                        && slope * previousSlope < 0f)
                        turns++;

                    if (Mathf.Abs(slope) > SlopeFloorHpa) previousSlope = slope;
                }

                previous = here;
            }

            Check(traceHi - traceLo > AtmoConstants.SynopticAmplitudeMinHpa,
                  "Pressure field: a month at one place is a barometer, not a "
                  + "constant (" + (traceHi - traceLo).ToString("F1") + " hPa swing)");

            Check(turns >= TraceTurnMinimum,
                  "Pressure field: weather arrives and passes (" + turns
                  + " turning points in a month)");

            Check(turns < TraceSamples / TraceTurnDivisor,
                  "Pressure field: the barometer is smooth in time, not noisy ("
                  + turns + " turning points)");
        }

        /// <summary>
        /// The zonal mean profile and the ITCZ that shifts it. The seasonal half is
        /// the interesting one: a place in the subtropics has to cross the ridge
        /// during the year, because that crossing is the whole of its dry season, and
        /// a place in the mid-latitudes has to stay poleward of the ridge all year,
        /// because that is the whole of its year-round changeability.
        /// </summary>
        private static void TestGeneralCirculation()
        {
            const float ItczTol = 0.05f;
            const float EquinoxTol = 0.1f;
            const float FlatTol = 1e-3f;
            const float PressureTol = 1e-2f;
            const float SeasonalMarginHpa = 1f;
            const float SweepStepDeg = 0.5f;
            const float SweepStepCeilingHpa = 1f;
            const float SubtropicalSiteDeg = 25f;
            const float MidLatitudeSiteDeg = 50f;

            int midsummerDay = (int)(AtmoConstants.DaysPerCalendarYear * 0.5f
                                     - AtmoConstants.DeclinationDayOffset);
            int midwinterDay = (int)(AtmoConstants.DaysPerCalendarYear
                                     - AtmoConstants.DeclinationDayOffset);
            int equinoxDay = (int)(AtmoConstants.DaysPerCalendarYear * 0.25f
                                   - AtmoConstants.DeclinationDayOffset);

            float tilt = AtmoConstants.EarthAxialTiltDegrees;

            CheckWithin(WeatherCellField.ItczLatitudeDeg(midsummerDay, tilt),
                        AtmoConstants.ItczAmplitudeDeg, ItczTol,
                        "Circulation: the ITCZ reaches its northern limit at northern "
                        + "midsummer");
            CheckWithin(WeatherCellField.ItczLatitudeDeg(midwinterDay, tilt),
                        -AtmoConstants.ItczAmplitudeDeg, ItczTol,
                        "Circulation: and its southern limit half a year later");
            CheckWithin(WeatherCellField.ItczLatitudeDeg(equinoxDay, tilt), 0f,
                        EquinoxTol,
                        "Circulation: at the equinox the ITCZ is on the equator");

            // A world with no tilt has no season for the ITCZ to follow, which is the
            // case a fixed migration in degrees would get wrong all year round.
            bool flatWorldHolds = true;

            for (int day = 1; day <= (int)AtmoConstants.DaysPerCalendarYear; day++)
            {
                if (Mathf.Abs(WeatherCellField.ItczLatitudeDeg(day, 0f)) > FlatTol)
                    flatWorldHolds = false;

                if (Mathf.Abs(WeatherCellField.ItczLatitudeDeg(day, tilt))
                    > AtmoConstants.ItczAmplitudeDeg + ItczTol)
                    flatWorldHolds = false;
            }

            Check(flatWorldHolds,
                  "Circulation: a world with no axial tilt holds the ITCZ on the "
                  + "equator, and a tilted one never overshoots its amplitude");
            // The four nodes, with the trough on the equator.
            CheckWithin(WeatherCellField.MeanSeaLevelPressureHpa(0f, 0f),
                        AtmoConstants.SeaLevelPressureHpa
                        + AtmoConstants.ItczPressureOffsetHpa, PressureTol,
                        "Circulation: the ITCZ is a trough");
            CheckWithin(WeatherCellField.MeanSeaLevelPressureHpa(
                            AtmoConstants.SubtropicalHighLatitudeDeg, 0f),
                        AtmoConstants.SeaLevelPressureHpa
                        + AtmoConstants.SubtropicalHighPressureOffsetHpa, PressureTol,
                        "Circulation: the desert latitude is a ridge");
            CheckWithin(WeatherCellField.MeanSeaLevelPressureHpa(
                            -AtmoConstants.SubpolarLowLatitudeDeg, 0f),
                        AtmoConstants.SeaLevelPressureHpa
                        + AtmoConstants.SubpolarLowPressureOffsetHpa, PressureTol,
                        "Circulation: the storm track is a trough, in either "
                        + "hemisphere");
            CheckWithin(WeatherCellField.MeanSeaLevelPressureHpa(
                            ClockMath.PoleLatitudeDeg, 0f),
                        AtmoConstants.SeaLevelPressureHpa
                        + AtmoConstants.PolarHighPressureOffsetHpa, PressureTol,
                        "Circulation: the polar cap is a high");

            // The trough travels with the ITCZ rather than staying on the equator,
            // which is the mechanism the whole seasonal march rests on.
            CheckWithin(WeatherCellField.MeanSeaLevelPressureHpa(
                            AtmoConstants.ItczAmplitudeDeg,
                            AtmoConstants.ItczAmplitudeDeg),
                        AtmoConstants.SeaLevelPressureHpa
                        + AtmoConstants.ItczPressureOffsetHpa, PressureTol,
                        "Circulation: the whole profile shifts with the ITCZ, not "
                        + "just its middle");

            // Latitude on the far side of a migrated ITCZ runs past the pole in the
            // arithmetic; it has to land on the polar node, not off the end.
            CheckWithin(WeatherCellField.MeanSeaLevelPressureHpa(
                            -ClockMath.PoleLatitudeDeg,
                            AtmoConstants.ItczAmplitudeDeg),
                        AtmoConstants.SeaLevelPressureHpa
                        + AtmoConstants.PolarHighPressureOffsetHpa, PressureTol,
                        "Circulation: the far pole stays on the polar node");

            Check(AtmoConstants.SubtropicalHighPressureOffsetHpa
                  > AtmoConstants.ItczPressureOffsetHpa
                  && AtmoConstants.ItczPressureOffsetHpa
                     > AtmoConstants.SubpolarLowPressureOffsetHpa
                  && AtmoConstants.PolarHighPressureOffsetHpa
                     > AtmoConstants.SubpolarLowPressureOffsetHpa,
                  "Circulation: ridge, trough, storm track and cap are in the right "
                  + "order, so descent means high pressure everywhere it happens");
            // Smooth, and smooth in its slope too: the wind task differentiates this
            // profile, so a kink at a band edge would show up as a jump in the wind.
            float worstStep = 0f;
            float previousP = WeatherCellField.MeanSeaLevelPressureHpa(
                -ClockMath.PoleLatitudeDeg, AtmoConstants.ItczAmplitudeDeg);
            bool profileFinite = IsFinite(previousP);

            for (float lat = -ClockMath.PoleLatitudeDeg + SweepStepDeg;
                 lat <= ClockMath.PoleLatitudeDeg; lat += SweepStepDeg)
            {
                float p = WeatherCellField.MeanSeaLevelPressureHpa(
                    lat, AtmoConstants.ItczAmplitudeDeg);

                if (!IsFinite(p)) profileFinite = false;

                float step = Mathf.Abs(p - previousP);
                if (step > worstStep) worstStep = step;

                previousP = p;
            }

            Check(profileFinite, "Circulation: the profile is finite pole to pole");
            Check(worstStep < SweepStepCeilingHpa,
                  "Circulation: the profile has no edges (worst "
                  + worstStep.ToString("F3") + " hPa per half degree)");

            // The dry season, stated as the thing that causes it. Twenty-five degrees
            // is inside the trough's reach in summer and under the ridge in winter;
            // fifty degrees never gets under the ridge at all.
            float summerItcz = WeatherCellField.ItczLatitudeDeg(midsummerDay, tilt);
            float winterItcz = WeatherCellField.ItczLatitudeDeg(midwinterDay, tilt);

            float wetSeason = WeatherCellField.MeanSeaLevelPressureHpa(
                SubtropicalSiteDeg, summerItcz);
            float drySeason = WeatherCellField.MeanSeaLevelPressureHpa(
                SubtropicalSiteDeg, winterItcz);

            Check(wetSeason < drySeason - SeasonalMarginHpa,
                  "Circulation: a subtropical site has a wet season and a dry one out "
                  + "of dayOfYear alone (" + wetSeason.ToString("F1") + " against "
                  + drySeason.ToString("F1") + " hPa)");

            Check(Mathf.Abs(MidLatitudeSiteDeg - summerItcz)
                  > AtmoConstants.SubtropicalHighLatitudeDeg
                  && Mathf.Abs(MidLatitudeSiteDeg - winterItcz)
                     > AtmoConstants.SubtropicalHighLatitudeDeg,
                  "Circulation: a mid-latitude site stays poleward of the ridge all "
                  + "year, so its weather stays changeable");
        }

        /// <summary>
        /// The Coriolis parameter and the balance built on it, before any field is
        /// involved. The sign is the whole hemisphere reversal in the wind, so if it
        /// is wrong here it is wrong everywhere and this is the cheapest place to see
        /// it. The floor is what keeps the balanced speed finite at the equator.
        /// </summary>
        private static void TestCoriolisParameter()
        {
            const float SignTol = 1e-9f;
            const float UnitTol = 1e-4f;

            Check(WeatherFront.CoriolisParameterPerS(PressureTestLatitudeDeg) > 0f,
                  "Coriolis: the parameter is positive in the northern hemisphere");
            Check(WeatherFront.CoriolisParameterPerS(-PressureTestLatitudeDeg) < 0f,
                  "Coriolis: the parameter is negative in the southern hemisphere");
            CheckWithin(WeatherFront.CoriolisParameterPerS(0f), 0f, SignTol,
                        "Coriolis: the parameter vanishes on the equator");

            // Negating the latitude negates the parameter exactly - the reversal is
            // one sign, not two tuned halves.
            CheckWithin(WeatherFront.CoriolisParameterPerS(-PressureTestLatitudeDeg),
                        -WeatherFront.CoriolisParameterPerS(PressureTestLatitudeDeg),
                        SignTol,
                        "Coriolis: negating the latitude negates the parameter");

            // The magnitude never falls below the floor, so the divisor is never
            // zero however close to the equator the sample sits.
            float floor = WeatherFront.CoriolisFloorPerS();
            Check(floor > 0f, "Coriolis: the floor is a positive number to divide by");
            CheckWithin(WeatherFront.CoriolisMagnitudePerS(0f), floor, SignTol,
                        "Coriolis: the equatorial magnitude clamps to the floor");
            Check(WeatherFront.CoriolisMagnitudePerS(PressureTestLatitudeDeg) > floor,
                  "Coriolis: a mid-latitude magnitude sits above the floor");

            // The balance is a clean +/-1 in the mid-latitudes and zero on the
            // equator, which is what both blends the wind and turns it.
            CheckWithin(WeatherFront.RotationalBalance(PressureTestLatitudeDeg), 1f,
                        UnitTol,
                        "Coriolis: rotation fully governs the mid-latitude flow");
            CheckWithin(WeatherFront.RotationalBalance(-PressureTestLatitudeDeg), -1f,
                        UnitTol,
                        "Coriolis: the balance reverses sign across the equator");
            CheckWithin(WeatherFront.RotationalBalance(0f), 0f, UnitTol,
                        "Coriolis: nothing balances the pressure gradient on the "
                        + "equator, so the flow is pure down-gradient there");
        }

        /// <summary>kg/m3. A representative low-altitude density for the wind tests,
        /// taken from the thermodynamics file rather than assumed, so the speed the
        /// tests predict is divided by the same number the code divides by.</summary>
        private static float WindTestDensity()
        {
            const float TestTempC = 15f;
            return AtmoMath.AirDensityKgPerM3(TestTempC,
                                              AtmoConstants.SeaLevelPressureHpa);
        }

        /// <summary>
        /// The wind the field implies, against its four promises: it circles a high
        /// the right way, that sense reverses when the latitude is negated, it
        /// strengthens where the gradient tightens, and it is finite across the
        /// equator instead of dividing by zero there. The direction tests subtract
        /// the mean steering first, since it is the anomaly's wind that Buys Ballot
        /// governs, not the mean flow that carries the anomaly.
        /// </summary>
        private static void TestWeatherWind()
        {
            TestCoriolisParameter();
            TestWeatherWindBridge();

            const int Sites = 40;
            const float SiteStrideX = 1.7f;
            const float SiteStrideZ = 2.3f;
            const float WhenDays = 402.7f;
            const float MeaningfulGradHpaPerM = 1e-6f;
            const float PerpTolMPerS = 1e-2f;
            const float SpeedTol = 1e-2f;

            var field = new WeatherCellField();
            field.Bind(PressureSeed);
            float density = WindTestDensity();
            float cell = PressureCellM();
            float lat = PressureTestLatitudeDeg;

            Vector2 meanNorth = WeatherCellField.SteeringCompassMPerS(lat);
            Vector2 meanSouth = WeatherCellField.SteeringCompassMPerS(-lat);

            int senseNorthRight = 0;
            int senseSouthRight = 0;
            int senseSamples = 0;
            int senseSouthSamples = 0;
            float worstPerp = 0f;

            float gradMin = float.PositiveInfinity;
            float gradMax = 0f;
            float speedAtGradMin = 0f;
            float speedAtGradMax = 0f;
            bool everFinite = true;

            for (int i = 0; i < Sites; i++)
            {
                float worldX = i * SiteStrideX * cell;
                float worldZ = i * SiteStrideZ * cell;

                // Northern hemisphere.
                Vector2 gradN = WeatherFront.AnomalyGradientHpaPerM(
                    field, worldX, worldZ, lat, WhenDays);
                Vector2 windN = WeatherFront.BalancedWindCompassMPerS(
                    field, worldX, worldZ, lat, WhenDays, density);
                Vector2 anomN = windN - meanNorth;

                everFinite &= IsFinite(windN.x) && IsFinite(windN.y);

                float gN = gradN.magnitude;
                if (gN > MeaningfulGradHpaPerM)
                {
                    senseSamples++;

                    Vector2 gUnit = gradN / gN;
                    float along = Mathf.Abs(anomN.x * gUnit.x + anomN.y * gUnit.y);
                    if (along > worstPerp) worstPerp = along;

                    // Gradient points uphill toward the high; a positive cross with
                    // the wind is the northern circulation.
                    float crossN = gradN.x * anomN.y - gradN.y * anomN.x;
                    if (crossN > 0f) senseNorthRight++;

                    // Southern hemisphere at the mirror latitude. Its anomaly is a
                    // different field from the north's, with its own flat patches, so
                    // it is gated on its own gradient - a point that is a front in one
                    // hemisphere and calm in the other has no southern circulation to
                    // test and must not be counted as a southern failure.
                    Vector2 gradS = WeatherFront.AnomalyGradientHpaPerM(
                        field, worldX, worldZ, -lat, WhenDays);
                    if (gradS.magnitude > MeaningfulGradHpaPerM)
                    {
                        senseSouthSamples++;
                        Vector2 windS = WeatherFront.BalancedWindCompassMPerS(
                            field, worldX, worldZ, -lat, WhenDays, density);
                        Vector2 anomS = windS - meanSouth;
                        float crossS = gradS.x * anomS.y - gradS.y * anomS.x;
                        if (crossS < 0f) senseSouthRight++;
                    }

                    float speedN = anomN.magnitude;
                    if (gN < gradMin) { gradMin = gN; speedAtGradMin = speedN; }
                    if (gN > gradMax) { gradMax = gN; speedAtGradMax = speedN; }
                }
            }

            // The direction tests only mean something where there is a gradient to
            // have a direction; a couple of dozen sites over the lattice guarantees a
            // spread of them without asserting on the flat patches between systems.
            Check(senseSamples > 0,
                  "Wind: the sweep found gradients to test the circulation on");
            Check(senseSouthSamples > 0,
                  "Wind: the sweep found southern gradients to test the sense on");
            Check(senseNorthRight == senseSamples,
                  "Wind: the anomaly circulates with low pressure on its left "
                  + "everywhere in the northern hemisphere (Buys Ballot)");
            Check(senseSouthRight == senseSouthSamples,
                  "Wind: negating the latitude reverses that sense, low pressure on "
                  + "the right in the southern hemisphere");
            Check(worstPerp < PerpTolMPerS,
                  "Wind: the mid-latitude wind blows along the isobars, not across "
                  + "them (worst crossing " + worstPerp.ToString("F4") + " m/s)");

            Check(everFinite,
                  "Wind: every mid-latitude sample is finite");
            Check(gradMax > gradMin,
                  "Wind: the sweep saw a range of gradient strengths to compare");
            Check(speedAtGradMax > speedAtGradMin,
                  "Wind: the wind is stronger where the gradient is tighter ("
                  + speedAtGradMax.ToString("F2") + " against "
                  + speedAtGradMin.ToString("F2") + " m/s)");

            // The proportionality is exact below the cap: at this latitude the
            // balance is a clean +1, so the anomaly wind is the balanced speed itself
            // and it is the gradient over density times the Coriolis magnitude.
            float coriolis = WeatherFront.CoriolisMagnitudePerS(lat);
            float predictedMax = gradMax * AtmoConstants.PascalsPerHectopascal
                                 / (density * coriolis);
            predictedMax = Mathf.Min(predictedMax, AtmoConstants.BalancedWindMaxMPerS);
            CheckWithin(speedAtGradMax, predictedMax, SpeedTol,
                        "Wind: the balanced speed is |grad p| / (rho f), to the "
                        + "hectopascal-per-metre");

            // Friction slows the surface wind and turns it, and the turn reverses
            // with the hemisphere. Both are tested on the total wind, where rotation
            // is exact: the surface wind is the wind aloft turned and scaled, so its
            // length is a clean fraction of the length aloft and the signed angle
            // between them is the friction turn - positive (toward the low on its
            // left) in the north, negative in the south.
            {
                const float TurnTolDeg = 1e-2f;
                const float MagTol = 1e-3f;

                // The site that carried the strongest gradient, so there is a real
                // wind to turn rather than the bare mean flow.
                float bestX = 0f, bestZ = 0f, best = 0f;
                for (int i = 0; i < Sites; i++)
                {
                    float wx = i * SiteStrideX * cell;
                    float wz = i * SiteStrideZ * cell;
                    float g = WeatherFront.AnomalyGradientHpaPerM(
                        field, wx, wz, lat, WhenDays).magnitude;
                    if (g > best) { best = g; bestX = wx; bestZ = wz; }
                }

                Vector2 aloftN = WeatherFront.BalancedWindCompassMPerS(
                    field, bestX, bestZ, lat, WhenDays, density);
                Vector2 surfN = WeatherFront.SurfaceWindCompassMPerS(
                    field, bestX, bestZ, lat, WhenDays, density);
                Vector2 aloftS = WeatherFront.BalancedWindCompassMPerS(
                    field, bestX, bestZ, -lat, WhenDays, density);
                Vector2 surfS = WeatherFront.SurfaceWindCompassMPerS(
                    field, bestX, bestZ, -lat, WhenDays, density);

                Check(IsFinite(surfN.x) && IsFinite(surfN.y),
                      "Wind: the surface wind is finite");

                CheckWithin(surfN.magnitude,
                            aloftN.magnitude
                            * AtmoConstants.SurfaceFrictionSpeedFactor, MagTol,
                            "Wind: friction slows the surface wind to its fraction "
                            + "of the wind aloft");

                float turnN = Vector2.SignedAngle(aloftN, surfN);
                float turnS = Vector2.SignedAngle(aloftS, surfS);
                CheckWithin(turnN, AtmoConstants.SurfaceFrictionTurnDeg, TurnTolDeg,
                            "Wind: friction turns the northern surface wind toward "
                            + "the low on its left");
                CheckWithin(turnS, -AtmoConstants.SurfaceFrictionTurnDeg, TurnTolDeg,
                            "Wind: and turns it the opposite way in the south");
            }
            const float EqSampleX = 3.3f;
            const float EqSampleZ = 5.1f;
            const float EqStepDeg = 0.5f;
            const float EqSpanDeg = 20f;
            const float NearEqDeg = 0.1f;
            const float ContinuityFraction = 0.1f;
            float eqX = EqSampleX * cell;
            float eqZ = EqSampleZ * cell;

            bool equatorFinite = true;
            for (float phi = -EqSpanDeg; phi <= EqSpanDeg + EqStepDeg * 0.5f;
                 phi += EqStepDeg)
            {
                Vector2 w = WeatherFront.SurfaceWindWorldMPerS(
                    field, eqX, eqZ, phi, WhenDays, density);
                equatorFinite &= IsFinite(w.x) && IsFinite(w.y);
            }

            // Exactly on the equator the Coriolis parameter is zero; the floor is
            // what keeps this finite rather than a division by zero.
            Vector2 onEquator = WeatherFront.SurfaceWindWorldMPerS(
                field, eqX, eqZ, 0f, WhenDays, density);
            equatorFinite &= IsFinite(onEquator.x) && IsFinite(onEquator.y);

            Check(equatorFinite,
                  "Wind: the wind is finite across the equator and exactly on it, "
                  + "never a NaN where the Coriolis parameter vanishes");

            // The wind a hair north of the equator matches the wind a hair south of
            // it: the balance fades through zero, so the sense does not flip through
            // a ninety degree jump as the latitude changes sign.
            Vector2 justNorth = WeatherFront.SurfaceWindWorldMPerS(
                field, eqX, eqZ, NearEqDeg, WhenDays, density);
            Vector2 justSouth = WeatherFront.SurfaceWindWorldMPerS(
                field, eqX, eqZ, -NearEqDeg, WhenDays, density);
            float across = (justNorth - justSouth).magnitude;
            float scale = justNorth.magnitude + justSouth.magnitude + Mathf.Epsilon;
            Check(across / scale < ContinuityFraction,
                  "Wind: the flow is continuous across the equator, not reversed "
                  + "through a jump");

            // A null field has no bearing to turn a compass by, so the world wind is
            // zero rather than a guess - the flag-off path other systems rely on.
            Vector2 noField = WeatherFront.SurfaceWindWorldMPerS(
                null, eqX, eqZ, lat, WhenDays, density);
            Check(noField == Vector2.zero,
                  "Wind: an unbound field yields no wind at all");
        }

        /// <summary>
        /// The opt-in bridge that lets the weather steer the one wind manager rather
        /// than becoming a second one. It pins the two things a self-test can pin
        /// without a running scene: the bridge writes a bearing the manager's own
        /// getter reads back parallel to what was asked for, and the opt-in defaults
        /// off so a world that never asks for it behaves exactly as it did. That the
        /// grass actually leans is a play-mode fact, left for the manual pass.
        /// </summary>
        private static void TestWeatherWindBridge()
        {
            const float ParallelTol = 1e-4f;
            const float SpeedTol = 1e-4f;
            const float TestSpeed = 12.5f;
            const float SecondSpeed = 7f;

            // Inactive so OnEnable does not fire and push globals into the editor
            // while a diagnostic is the only thing looking.
            GameObject go = new GameObject("UC SelfTest Wind Bridge");
            go.SetActive(false);
            try
            {
                UCWindManager wind = go.AddComponent<UCWindManager>();

                // A spread of bearings, including the two axes the compass is
                // defined by: 0 deg is +Z, 90 deg is +X.
                Vector2[] targets =
                {
                    new Vector2(0f, 1f),    // due north
                    new Vector2(1f, 0f),    // due east
                    new Vector2(1f, 1f),    // north-east
                    new Vector2(-2f, 3f),   // an off-axis bearing, unnormalised
                };

                foreach (Vector2 t in targets)
                {
                    wind.SetWeatherWind(t, TestSpeed);
                    Vector2 got = wind.DirectionXZ;
                    Vector2 want = t.normalized;

                    // Parallel and same sense: the dot of two unit vectors is 1.
                    CheckWithin(got.x * want.x + got.y * want.y, 1f, ParallelTol,
                                "Wind bridge: the manager blows toward the vector "
                                + "the weather handed it");
                    CheckWithin(wind.speedMetresPerSecond, TestSpeed, SpeedTol,
                                "Wind bridge: and at the speed it was handed");
                }

                // A zero vector carries no bearing, so the bearing holds and only the
                // speed moves - never a snap to north.
                wind.SetWeatherWind(new Vector2(1f, 0f), TestSpeed);
                float heldDeg = wind.directionDegrees;
                wind.SetWeatherWind(Vector2.zero, SecondSpeed);
                CheckWithin(wind.directionDegrees, heldDeg, ParallelTol,
                            "Wind bridge: a directionless gust leaves the bearing "
                            + "where it was");
                CheckWithin(wind.speedMetresPerSecond, SecondSpeed, SpeedTol,
                            "Wind bridge: while still taking the new speed");

                // A negative speed is not a wind blowing backwards; it clamps.
                wind.SetWeatherWind(new Vector2(0f, 1f), -TestSpeed);
                Check(wind.speedMetresPerSecond >= 0f,
                      "Wind bridge: a negative speed clamps to a still day");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }

            // The opt-in is off until an author asks for it, so a world that never
            // touches it keeps exactly the wind it always had.
            GameObject weatherGo = new GameObject("UC SelfTest Weather Flag");
            weatherGo.SetActive(false);
            try
            {
                UCWeatherSystemManager weather =
                    weatherGo.AddComponent<UCWeatherSystemManager>();
                Check(!weather.weatherDrivesWind,
                      "Wind bridge: the weather does not drive the wind until the "
                      + "author opts in");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(weatherGo);
            }
        }

        /// <summary>
        /// A frontal passage is a sequence, not a switch. This walks one of the
        /// field's own lows over a fixed point and checks the barometer falls
        /// (approaching), bottoms out (overhead), then rises (departing) in that
        /// order, that a ridge carries no front, and that the wind-shift sense
        /// reverses across the equator - the one place the hemisphere shows.
        /// </summary>
        private static void TestWeatherFronts()
        {
            const float FrontLat = 45f;
            const int Points = 6;
            const float PointStride = 260000f;
            const int Steps = 300;
            const float WindowDays = 7f;
            const float StartDays = 120f;

            var field = new WeatherCellField();
            field.Bind(PressureSeed);
            float step = WindowDays / (Steps - 1);

            // Find the deepest passage across a few fixed points: whichever point
            // and time the barometer bottoms out lowest is a low sweeping over it.
            int bestPoint = 0;
            int bestIndex = 0;
            float bestAnom = float.PositiveInfinity;
            for (int p = 0; p < Points; p++)
            {
                float px = p * PointStride;
                for (int i = 0; i < Steps; i++)
                {
                    float t = StartDays + i * step;
                    float a = field.PressureAnomalyHpa(px, 0f, FrontLat, t);
                    if (a < bestAnom) { bestAnom = a; bestPoint = p; bestIndex = i; }
                }
            }

            Check(bestAnom <= -6f,
                  "Fronts: the field sinks a trough deep enough to carry a front "
                  + "over some point in the window");

            float bx = bestPoint * PointStride;
            float tMin = StartDays + bestIndex * step;

            // Walk out to the edges of the dip - where the barometer climbs back to
            // within the front threshold of the mean - and take the phase samples
            // halfway down each side, so each is deep and clearly still moving.
            int iLeft = bestIndex;
            while (iLeft > 0 &&
                   -field.PressureAnomalyHpa(bx, 0f, FrontLat,
                        StartDays + (iLeft - 1) * step)
                       >= AtmoConstants.FrontDepthThresholdHpa)
                iLeft--;
            int iRight = bestIndex;
            while (iRight < Steps - 1 &&
                   -field.PressureAnomalyHpa(bx, 0f, FrontLat,
                        StartDays + (iRight + 1) * step)
                       >= AtmoConstants.FrontDepthThresholdHpa)
                iRight++;

            Check(iRight - iLeft >= 2,
                  "Fronts: the passage lasts long enough to sample approach and "
                  + "clearance apart");

            float tApproach = StartDays + ((iLeft + bestIndex) / 2) * step;
            float tDepart = StartDays + ((bestIndex + iRight) / 2) * step;

            WeatherFront.FrontReading approach =
                WeatherFront.ResolveFront(field, bx, 0f, FrontLat, tApproach);
            WeatherFront.FrontReading overhead =
                WeatherFront.ResolveFront(field, bx, 0f, FrontLat, tMin);
            WeatherFront.FrontReading depart =
                WeatherFront.ResolveFront(field, bx, 0f, FrontLat, tDepart);

            Check(approach.Phase == WeatherFront.FrontPhase.Approaching,
                  "Fronts: the barometer is still falling on the way in - approaching");
            Check(depart.Phase == WeatherFront.FrontPhase.Departing,
                  "Fronts: the barometer rises behind the trough - departing");
            Check(approach.Phase != depart.Phase,
                  "Fronts: approach and clearance are different phases, not one instant");
            Check(overhead.DepthHpa >= approach.DepthHpa
                  && overhead.DepthHpa >= depart.DepthHpa,
                  "Fronts: the trough is deepest overhead, shallower on either side");
            Check(approach.Kind != WeatherFront.FrontKind.None
                  && depart.Kind != WeatherFront.FrontKind.None,
                  "Fronts: both flanks of the passage carry a front");
            Check(IsFinite(approach.TendencyHpaPerDay)
                  && IsFinite(depart.TendencyHpaPerDay)
                  && IsFinite(overhead.DepthHpa),
                  "Fronts: the readings stay finite");

            // A ridge - the barometer above the mean - is where a front is not.
            float maxAnom = float.NegativeInfinity;
            float tRidge = StartDays;
            for (int i = 0; i < Steps; i++)
            {
                float t = StartDays + i * step;
                float a = field.PressureAnomalyHpa(bx, 0f, FrontLat, t);
                if (a > maxAnom) { maxAnom = a; tRidge = t; }
            }
            WeatherFront.FrontReading ridge =
                WeatherFront.ResolveFront(field, bx, 0f, FrontLat, tRidge);
            Check(ridge.Kind == WeatherFront.FrontKind.None
                  && ridge.Phase == WeatherFront.FrontPhase.None,
                  "Fronts: a ridge carries no front - a front needs depth below the mean");

            // The wind shift is the one reading that feels the hemisphere. A cold
            // front veers north of the equator and backs south of it; a warm front
            // is its mirror - so a southern observer sees the reversed sequence.
            float coldNorth =
                WeatherFront.WindShiftSense(WeatherFront.FrontKind.Cold, FrontLat);
            float coldSouth =
                WeatherFront.WindShiftSense(WeatherFront.FrontKind.Cold, -FrontLat);
            float warmNorth =
                WeatherFront.WindShiftSense(WeatherFront.FrontKind.Warm, FrontLat);
            Check(coldNorth > 0f && coldSouth < 0f,
                  "Fronts: a cold front veers in the north and backs in the south");
            CheckClose(coldNorth, -coldSouth,
                  "Fronts: the veer reverses exactly across the equator");
            Check(coldNorth * warmNorth < 0f,
                  "Fronts: warm and cold fronts shift the wind in opposite senses");
            Check(WeatherFront.WindShiftSense(WeatherFront.FrontKind.None, FrontLat)
                      == 0f,
                  "Fronts: no front, no wind shift");

            if (approach.Kind == WeatherFront.FrontKind.Warm
                || approach.Kind == WeatherFront.FrontKind.Cold)
                CheckClose(approach.WindShiftSense,
                          WeatherFront.WindShiftSense(approach.Kind, FrontLat),
                          "Fronts: a front reports the shift its kind and hemisphere imply");

            // No field, nothing to read.
            WeatherFront.FrontReading none =
                WeatherFront.ResolveFront(null, 0f, 0f, FrontLat, tMin);
            Check(none.Kind == WeatherFront.FrontKind.None,
                  "Fronts: no field, no front");
            Check(WeatherFront.PressureTendencyHpaPerDay(null, 0f, 0f, FrontLat, tMin)
                      == 0f,
                  "Fronts: no field, no tendency");
        }

        /// <summary>
        /// Cloud from humidity, not from noise. Checks the Sundqvist curve and the
        /// independent-screen combination, that the ITCZ sits under thick cloud and the
        /// subtropical high stays clear, that cover thickens as a low deepens overhead,
        /// and that the per-deck values § 8.7 reads combine back to the total.
        /// </summary>
        private static void TestCloudCover()
        {
            float crit = AtmoConstants.SundqvistCriticalRh;

            // Sundqvist: nothing below the critical humidity, full at saturation, and
            // rising in between. The mid-point matches the closed form exactly.
            Check(CloudCover.LayerCover01(crit) == 0f
                  && CloudCover.LayerCover01(crit * 0.5f) == 0f,
                  "Cloud: no deck below the critical humidity");
            CheckClose(CloudCover.LayerCover01(1f), 1f,
                  "Cloud: a saturated deck is full cover");
            float rhMid = crit + (1f - crit) * 0.5f;
            CheckClose(CloudCover.LayerCover01(rhMid),
                  1f - Mathf.Sqrt((1f - rhMid) / (1f - crit)),
                  "Cloud: the deck cover follows the Sundqvist square root");
            Check(CloudCover.LayerCover01(rhMid) < CloudCover.LayerCover01(0.95f),
                  "Cloud: a moister deck is a cloudier deck");

            // Independent screens, never a sum: two half-covered decks read as
            // three-quarters, and any deck under a full one is already full.
            CheckClose(CloudCover.CombineTwo01(0.5f, 0.5f), 0.75f,
                  "Cloud: half over half is three-quarters, not one-and-a-half");
            CheckClose(CloudCover.CombineTwo01(0.4f, 0f), 0.4f,
                  "Cloud: a clear deck adds nothing");
            CheckClose(CloudCover.CombineTwo01(0.4f, 1f), 1f,
                  "Cloud: nothing shows through a full deck");

            var field = new WeatherCellField();
            field.Bind(PressureSeed);

            int dayOfYear = 172;
            float itcz = WeatherCellField.ItczLatitudeDeg(
                dayOfYear, AtmoConstants.EarthAxialTiltDegrees);
            float when = 300f;
            float px = 12345f;
            float pz = 6789f;

            // The circulation drives the humidity: full ascent on the ITCZ, none on
            // the subtropical ridge thirty degrees off it.
            Check(CloudCover.AscentFraction01(itcz, itcz) > 0.99f,
                  "Cloud: the ITCZ is an ascent regime");
            Check(CloudCover.AscentFraction01(
                      itcz + AtmoConstants.SubtropicalHighLatitudeDeg, itcz) < 0.01f,
                  "Cloud: the subtropical ridge is a descent regime");

            // The done-when: thick cloud on the ITCZ, a clear subtropical high.
            float itczCover = CloudCover.TotalCover01(field, px, pz, itcz, itcz, when);
            float highCover = CloudCover.TotalCover01(field, px, pz,
                itcz + AtmoConstants.SubtropicalHighLatitudeDeg, itcz, when);
            Check(itczCover > 0.7f,
                  "Cloud: the ITCZ holds cover above the wet-season target");
            Check(highCover < 0.1f,
                  "Cloud: the subtropical high holds cover below the desert target");
            Check(IsFinite(itczCover) && itczCover >= 0f && itczCover <= 1f
                  && highCover >= 0f && highCover <= 1f,
                  "Cloud: cover stays a finite fraction");

            // The per-deck values § 8.7 reads combine back to the stratiform total, and
            // the convective term the stability test will feed only ever adds cloud.
            CloudCover.CloudDecks decks =
                CloudCover.ResolveDecks(field, px, pz, itcz, itcz, when);
            CheckClose(decks.Stratiform01,
                  CloudCover.TotalCover01(field, px, pz, itcz, itcz, when),
                  "Cloud: the decks combine back to the total");
            Check(CloudCover.TotalCover01(field, px, pz,
                      itcz + AtmoConstants.SubtropicalHighLatitudeDeg,
                      itcz, when, 0.5f) >= highCover,
                  "Cloud: a convective contribution only adds cloud");

            // No field: the stratiform sky is nothing, and only a convective term the
            // caller passes in survives.
            Check(CloudCover.ResolveDecks(null, px, pz, itcz, itcz, when).Stratiform01
                      == 0f,
                  "Cloud: no field, no stratiform deck");
            CheckClose(CloudCover.TotalCover01(null, px, pz, itcz, itcz, when, 0.5f),
                  0.5f, "Cloud: no field leaves only the convective term");

            // Cover thickens as a low deepens overhead. Find one of the field's own
            // deep passages in the storm-track latitudes, then read the sky at the
            // trough against a shallower moment on its approach flank.
            const float StormLat = 45f;
            const int Steps = 300;
            const float WindowDays = 7f;
            const float StartDays = 120f;
            float dt = WindowDays / (Steps - 1);

            int bestIndex = 0;
            float bestAnom = float.PositiveInfinity;
            for (int i = 0; i < Steps; i++)
            {
                float t = StartDays + i * dt;
                float a = field.PressureAnomalyHpa(0f, 0f, StormLat, t);
                if (a < bestAnom) { bestAnom = a; bestIndex = i; }
            }

            if (bestAnom <= -6f && bestIndex >= 20)
            {
                float tTrough = StartDays + bestIndex * dt;
                float tShallow = StartDays + (bestIndex - 20) * dt;
                float trough =
                    CloudCover.TotalCover01(field, 0f, 0f, StormLat, itcz, tTrough);
                float shallow =
                    CloudCover.TotalCover01(field, 0f, 0f, StormLat, itcz, tShallow);
                Check(trough >= shallow,
                      "Cloud: the sky thickens as the low deepens overhead");
            }
        }

        /// <summary>
        /// Cloud base as an altitude, not a constant. Checks the convective base is a
        /// finite height above ground, that a humid ascent column clouds over lower than
        /// a dry subsident one (the tropical-morning vs desert-afternoon contrast), that
        /// the three layered decks stack in altitude order, and that a null field reads
        /// no base at all.
        /// </summary>
        private static void TestCloudBase()
        {
            var field = new WeatherCellField();
            field.Bind(PressureSeed);
            float when = 200f;
            float itcz = 0f;

            // A humid ascent column (the equatorial trough) clouds over far lower than a
            // dry subsident one (the subtropical ridge): the dew point sits close to the
            // temperature, so the parcel saturates after a short lift.
            CloudCover.CloudBasesM humid =
                CloudCover.ResolveBasesM(field, 0f, 0f, 0f, itcz, when);
            CloudCover.CloudBasesM dry =
                CloudCover.ResolveBasesM(field, 0f, 0f, 30f, itcz, when);

            Check(IsFinite(humid.Convective) && humid.Convective >= 0f,
                  "Cloud base: the convective base is a finite height above ground");
            Check(humid.Convective < dry.Convective,
                  "Cloud base: a humid column clouds over lower than a dry one");

            // The layered decks are drawn in altitude order, low beneath mid beneath high.
            Check(humid.Low < humid.Mid && humid.Mid < humid.High,
                  "Cloud base: the layered decks stack in altitude order");
            Check(IsFinite(humid.Low) && IsFinite(humid.Mid) && IsFinite(humid.High),
                  "Cloud base: every layered base stays finite");

            // No field, no base.
            CloudCover.CloudBasesM none = CloudCover.ResolveBasesM(null, 0f, 0f, 0f, itcz, when);
            Check(none.Convective == 0f && none.Low == 0f,
                  "Cloud base: a null field reads no cloud base");
        }

        /// <summary>
        /// Stability from the sun-driven lapse. Checks the four bands land on the
        /// adiabatic thresholds, that the surface-heating term peaks in the afternoon
        /// and inverts at night, and that the two done-when scenarios fall out of the
        /// one test: an afternoon tropical column goes absolutely unstable and
        /// convective, a winter morning lies under a stable inversion with no
        /// convection, and dry instability makes no cloud.
        /// </summary>
        private static void TestWeatherStability()
        {
            // The bands sit exactly on the adiabatic pair: below zero inverts, the
            // saturated adiabat is the floor of conditional instability, the dry
            // adiabat the floor of absolute instability.
            Check(WeatherStability.Classify(-1f)
                      == WeatherStability.StabilityState.Inversion,
                  "Stability: a negative lapse is an inversion");
            Check(WeatherStability.Classify(3f)
                      == WeatherStability.StabilityState.Stable,
                  "Stability: below the saturated adiabat is stable");
            Check(WeatherStability.Classify(AtmoConstants.SaturatedAdiabaticCPerKm)
                      == WeatherStability.StabilityState.ConditionallyUnstable,
                  "Stability: the saturated adiabat is the floor of conditional");
            Check(WeatherStability.Classify(AtmoConstants.DryAdiabaticCPerKm)
                      == WeatherStability.StabilityState.AbsolutelyUnstable,
                  "Stability: the dry adiabat is the floor of absolute instability");

            // The sun's contribution: peak when the lagged sun is overhead, a fixed
            // inversion once it is below the horizon.
            float peakHourAngleDeg = AtmoConstants.StabilityHeatingLagHours
                                     * AtmoConstants.DegreesPerHourOfHourAngle;
            CheckClose(WeatherStability.SurfaceHeatingBoostCPerKm(0f, 0f, peakHourAngleDeg),
                  AtmoConstants.StabilityMaxHeatingBoostCPerKm,
                  "Stability: the heating peaks with the lagged sun overhead");
            CheckClose(WeatherStability.SurfaceHeatingBoostCPerKm(0f, 0f, 180f),
                  -AtmoConstants.StabilityNocturnalInversionCPerKm,
                  "Stability: a night lays the full nocturnal inversion");

            var field = new WeatherCellField();
            field.Bind(PressureSeed);
            float when = 300f;

            // Done-when A: an afternoon tropical column, sun overhead on the ITCZ, goes
            // absolutely unstable and convective - the afternoon cumulonimbus.
            WeatherStability.StabilityReading tropics = WeatherStability.Resolve(
                field, 0f, 0f, 0f, 0f, peakHourAngleDeg, 0f, when);
            Check(tropics.State == WeatherStability.StabilityState.AbsolutelyUnstable,
                  "Stability: the tropical afternoon is absolutely unstable");
            Check(tropics.ConvectiveCover01 > 0.5f,
                  "Stability: the unstable moist column carries convective cloud");
            Check(IsFinite(tropics.ConvectiveCover01)
                  && tropics.ConvectiveCover01 >= 0f && tropics.ConvectiveCover01 <= 1f,
                  "Stability: the convective cover stays a finite fraction");

            // Done-when B: a winter morning at high latitude lies under a stable
            // inversion and convects nothing - the flat-grey, drizzle morning.
            WeatherStability.StabilityReading winterAM = WeatherStability.Resolve(
                field, 0f, 0f, 50f, -20f, -52.5f, 0f, when);
            Check(winterAM.State == WeatherStability.StabilityState.Inversion,
                  "Stability: the winter morning is a stable inversion");
            Check(winterAM.ConvectiveCover01 == 0f,
                  "Stability: a stable morning builds no convective cloud");

            // The diurnal swing: the same place is steeper in the afternoon than in
            // the morning - which is why storms are an afternoon event.
            Check(WeatherStability.ObservedLapseCPerKm(30f, 15f, peakHourAngleDeg)
                      > WeatherStability.ObservedLapseCPerKm(30f, 15f, -52.5f),
                  "Stability: the afternoon lapse is steeper than the morning's");

            // Convective cloud needs both instability and moisture: dry instability
            // is a dust devil, not a cloud, and stable moist air is just haze.
            Check(WeatherStability.ConvectiveCover01(
                      AtmoConstants.DryAdiabaticCPerKm, 0.9f) > 0f,
                  "Stability: unstable and moist makes convective cloud");
            Check(WeatherStability.ConvectiveCover01(
                      AtmoConstants.DryAdiabaticCPerKm, 0f) == 0f,
                  "Stability: unstable but dry makes no cloud - the desert");
            Check(WeatherStability.ConvectiveCover01(3f, 0.9f) == 0f,
                  "Stability: stable air makes no convective cloud however moist");
            Check(WeatherStability.ConvectiveCover01(AtmoConstants.DryAdiabaticCPerKm, 0.5f)
                      < WeatherStability.ConvectiveCover01(
                          AtmoConstants.DryAdiabaticCPerKm, 1f),
                  "Stability: a moister unstable column is a cloudier one");

            // The convective term feeds the cloud total: it only ever adds to the
            // stratiform decks the same column already carries.
            float stratiform = CloudCover.ResolveDecks(
                field, 0f, 0f, 0f, 0f, when).Stratiform01;
            Check(CloudCover.TotalCover01(field, 0f, 0f, 0f, 0f, when,
                      tropics.ConvectiveCover01) >= stratiform,
                  "Stability: the convective term only adds to the cloud total");
        }

        /// <summary>
        /// The local circulations. Checks the shared diurnal forcing reverses between
        /// day and night, that the sea breeze blows onshore by day and offshore (and
        /// weaker) at night and dies off inland, that the slope wind climbs by day and
        /// drains downhill after sunset, that the gap only speeds an along-valley flow,
        /// and that the combiner is the sum of the three.
        /// </summary>
        private static void TestWeatherLocalWind()
        {
            float peakHourAngleDeg = AtmoConstants.StabilityHeatingLagHours
                                     * AtmoConstants.DegreesPerHourOfHourAngle;
            float dayForcing = WeatherLocalWind.DiurnalForcingSigned(0f, 0f, peakHourAngleDeg);
            float nightForcing = WeatherLocalWind.DiurnalForcingSigned(0f, 0f, 180f);

            Check(dayForcing > 0f, "Local wind: the day forces the onshore, upslope sense");
            CheckClose(nightForcing, -AtmoConstants.NocturnalForcingFraction,
                  "Local wind: the night is the weaker reversed forcing");

            Vector2 onshore = new Vector2(1f, 0f);

            // The sea breeze: onshore and fresh at the shore by day, offshore and
            // gentler at night, and gone beyond the inland range.
            Vector2 dayShore = WeatherLocalWind.SeaBreezeWorldMPerS(onshore, 0f, 10f, dayForcing);
            Vector2 nightShore = WeatherLocalWind.SeaBreezeWorldMPerS(onshore, 0f, 10f, nightForcing);
            Check(Vector2.Dot(dayShore, onshore) > 0f,
                  "Local wind: the daytime sea breeze blows onshore");
            Check(Vector2.Dot(nightShore, onshore) < 0f,
                  "Local wind: the night reverses it to an offshore land breeze");
            Check(nightShore.magnitude < dayShore.magnitude,
                  "Local wind: the land breeze is the weaker of the two");
            Check(WeatherLocalWind.SeaBreezeWorldMPerS(
                      onshore, AtmoConstants.SeaBreezeRangeM, 10f, dayForcing)
                  == Vector2.zero,
                  "Local wind: the sea breeze dies off at the inland range");
            Vector2 halfInland = WeatherLocalWind.SeaBreezeWorldMPerS(
                onshore, AtmoConstants.SeaBreezeRangeM * 0.5f, 10f, dayForcing);
            CheckClose(halfInland.magnitude, dayShore.magnitude * 0.5f,
                  "Local wind: the sea breeze falls off linearly inland");

            Vector2 downhill = new Vector2(0f, 1f);

            // The slope wind: uphill against the fall line by day, downhill with it at
            // night - the cold-air drainage that pools fog and frost on a valley floor.
            Vector2 daySlope = WeatherLocalWind.SlopeWindWorldMPerS(downhill, 0.5f, dayForcing);
            Vector2 nightSlope = WeatherLocalWind.SlopeWindWorldMPerS(downhill, 0.5f, nightForcing);
            Check(Vector2.Dot(daySlope, downhill) < 0f,
                  "Local wind: the daytime slope wind climbs uphill");
            Check(Vector2.Dot(nightSlope, downhill) > 0f,
                  "Local wind: the night drains cold air downhill");
            Check(WeatherLocalWind.SlopeWindWorldMPerS(downhill, 0f, dayForcing)
                  == Vector2.zero,
                  "Local wind: flat ground makes no slope wind");
            Check(WeatherLocalWind.SlopeWindWorldMPerS(downhill, 0.8f, dayForcing).magnitude
                  > WeatherLocalWind.SlopeWindWorldMPerS(downhill, 0.3f, dayForcing).magnitude,
                  "Local wind: a steeper slope drives a stronger wind");

            // The gap: a venturi on the flow already moving along the valley, nothing
            // on a crosswind and nothing where the valley does not narrow.
            Vector2 axis = new Vector2(1f, 0f);
            Vector2 alongFlow = axis * 10f;
            Vector2 gap = WeatherLocalWind.GapWindWorldMPerS(axis, alongFlow, 0.5f);
            Check(Vector2.Dot(gap, axis) > 0f,
                  "Local wind: the gap speeds the along-valley flow");
            CheckClose(gap.magnitude, 5f,
                  "Local wind: half a constriction adds half the along-valley speed");
            Vector2 cross = new Vector2(0f, 7f);
            Check(WeatherLocalWind.GapWindWorldMPerS(axis, cross, 0.5f).magnitude < EcoEpsilon,
                  "Local wind: a crosswind gets no gap speed-up");
            Check(WeatherLocalWind.GapWindWorldMPerS(axis, alongFlow, 0f) == Vector2.zero,
                  "Local wind: an open valley adds nothing");

            // The combiner is exactly the three summed.
            WeatherLocalWind.LocalGeography geo = default;
            geo.onshoreDirWorld = onshore;
            geo.distanceToCoastM = AtmoConstants.SeaBreezeRangeM * 0.25f;
            geo.landSeaTempDiffC = 8f;
            geo.downhillDirWorld = downhill;
            geo.slope01 = 0.4f;
            geo.alongValleyDirWorld = axis;
            geo.valleyConstriction01 = 0.5f;

            Vector2 total = WeatherLocalWind.ResolveWorldMPerS(geo, alongFlow, 0f, 0f, peakHourAngleDeg);
            Vector2 parts =
                WeatherLocalWind.SeaBreezeWorldMPerS(geo.onshoreDirWorld,
                    geo.distanceToCoastM, geo.landSeaTempDiffC, dayForcing)
                + WeatherLocalWind.SlopeWindWorldMPerS(geo.downhillDirWorld, geo.slope01, dayForcing)
                + WeatherLocalWind.GapWindWorldMPerS(geo.alongValleyDirWorld, alongFlow,
                    geo.valleyConstriction01);
            CheckClose(total.x, parts.x, "Local wind: the combiner sums the three, x");
            CheckClose(total.y, parts.y, "Local wind: the combiner sums the three, y");
            Check(IsFinite(total.x) && IsFinite(total.y),
                  "Local wind: the combined vector stays finite");
        }

        /// <summary>
        /// The humidity field. Checks the daily cycle falls out of the temperature
        /// alone (a colder dawn is more humid than a warm afternoon at one dew point),
        /// that maritime air is moister than continental, that an approaching low
        /// raises RH ahead of itself, that air colder than its dew point saturates,
        /// and that the field stays a finite fraction with no bound system.
        /// </summary>
        private static void TestWeatherHumidity()
        {
            // Dawn maximum, afternoon minimum, with no authored curve: one air mass,
            // one dew point, read against a cold dawn and a warm afternoon.
            float dewC = WeatherHumidity.AirMassDewPointC(0.3f, 0f);
            float dawnRh = AtmoMath.RelativeHumidity01(dewC + 3f, dewC);
            float afternoonRh = AtmoMath.RelativeHumidity01(dewC + 15f, dewC);
            Check(dawnRh > afternoonRh,
                  "Humidity: the cold dawn is more humid than the warm afternoon");

            // The air mass, not the ground, carries the moisture: maritime is moist,
            // continental is dry.
            Check(WeatherHumidity.AirMassDewPointC(0f, 0f)
                      > WeatherHumidity.AirMassDewPointC(1f, 0f),
                  "Humidity: maritime air is moister than continental air");

            // Air colder than its dew point cannot hold the moisture, so it saturates
            // at 100 % rather than passing through it.
            float saturated = WeatherHumidity.Resolve01(null, 0f, 0f, 0f, 0f, 0f, -5f);
            CheckClose(saturated, 1f,
                  "Humidity: air colder than its dew point saturates at 100 %");

            // A null field advects no front moisture and still reads a finite fraction.
            Check(WeatherHumidity.FrontMoistureBoostC(null, 0f, 0f, 0f, 0f) == 0f,
                  "Humidity: a settled sky with no system advects no moisture");
            float nullRh = WeatherHumidity.Resolve01(null, 0f, 0f, 0f, 0f, 0.5f, 20f);
            Check(IsFinite(nullRh) && nullRh >= 0f && nullRh <= 1f,
                  "Humidity: the field stays a finite fraction with no bound system");
            // A maritime front raises RH ahead of itself: scan a fixed point over a
            // week for the deepest low and the strongest ridge to pass over it, then
            // read RH at each at one air temperature. The low, advecting moist air,
            // must read wetter than the ridge, which advects none.
            var field = new WeatherCellField();
            field.Bind(PressureSeed);

            float humLat = 45f;
            int steps = 300;
            float windowDays = 7f;
            float startDays = 120f;

            float lowAnom = 0f;
            float lowTime = startDays;
            float ridgeAnom = 0f;
            float ridgeTime = startDays;
            for (int i = 0; i <= steps; i++)
            {
                float t = startDays + windowDays * i / steps;
                float anom = field.PressureAnomalyHpa(0f, 0f, humLat, t);
                if (anom < lowAnom) { lowAnom = anom; lowTime = t; }
                if (anom > ridgeAnom) { ridgeAnom = anom; ridgeTime = t; }
            }

            if (lowAnom <= -4f && ridgeAnom >= 1f)
            {
                float airTempC = 22f;
                float continentality = 0.5f;
                Check(WeatherHumidity.FrontMoistureBoostC(
                          field, 0f, 0f, humLat, lowTime) > 0f,
                      "Humidity: the approaching low advects moist air");
                Check(WeatherHumidity.FrontMoistureBoostC(
                          field, 0f, 0f, humLat, ridgeTime) == 0f,
                      "Humidity: the ridge behind it advects none");
                float lowRh = WeatherHumidity.Resolve01(
                    field, 0f, 0f, humLat, lowTime, continentality, airTempC);
                float ridgeRh = WeatherHumidity.Resolve01(
                    field, 0f, 0f, humLat, ridgeTime, continentality, airTempC);
                Check(lowRh > ridgeRh,
                      "Humidity: a maritime front raises RH ahead of itself");
            }
        }

        /// <summary>
        /// Precipitation type from the wet-bulb, not the air temperature. Checks the
        /// headline case both ways - dry air at +2 degC snows, saturated air at the same
        /// temperature rains - that the sleet band blends rather than switches, that the
        /// classifier lands on its thresholds, and that frozen precipitation on warm
        /// ground melts on contact rather than accumulating.
        /// </summary>
        private static void TestWeatherPrecipType()
        {
            float airTempC = 2f;

            // The done-when, both ways at one air temperature: dry air cools the drop
            // below freezing by evaporation, so it snows at +2 degC; saturated air
            // cannot cool it, so the same +2 degC rains.
            WeatherPrecipType.PrecipReading dry =
                WeatherPrecipType.Resolve(airTempC, 0.30f, airTempC);
            WeatherPrecipType.PrecipReading humid =
                WeatherPrecipType.Resolve(airTempC, 1f, airTempC);
            Check(dry.Kind == WeatherPrecipType.PrecipKind.Snow,
                  "Precip type: dry air snows at +2 degC air temperature");
            Check(humid.Kind == WeatherPrecipType.PrecipKind.Rain,
                  "Precip type: saturated air rains at the same +2 degC");
            Check(dry.WetBulbC < airTempC,
                  "Precip type: the wet-bulb sits below the air temperature in dry air");
            Check(humid.WetBulbC > dry.WetBulbC,
                  "Precip type: saturating the air raises the wet-bulb");

            // The sleet band is a blend, not a switch: mid-band is half snow, half rain.
            float span = AtmoConstants.SleetUpperWetBulbC
                         - AtmoConstants.SnowWetBulbThresholdC;
            float midBand = AtmoConstants.SnowWetBulbThresholdC + span * 0.5f;
            CheckClose(WeatherPrecipType.RainFraction01(midBand), 0.5f,
                  "Precip type: the middle of the sleet band is half rain");
            Check(WeatherPrecipType.Classify(midBand) == WeatherPrecipType.PrecipKind.Sleet,
                  "Precip type: the band between the thresholds is sleet");
            Check(WeatherPrecipType.RainFraction01(
                      AtmoConstants.SnowWetBulbThresholdC - 1f) == 0f,
                  "Precip type: below the snow threshold is all snow");
            Check(WeatherPrecipType.RainFraction01(
                      AtmoConstants.SleetUpperWetBulbC + 1f) == 1f,
                  "Precip type: above the rain threshold is all rain");

            // Frozen precipitation on ground above freezing melts on contact - wet road,
            // no snowpack - while on cold ground it accumulates, and rain never does.
            Check(!dry.Accumulates,
                  "Precip type: snow on +2 degC ground melts on contact, no accumulation");
            WeatherPrecipType.PrecipReading coldGround =
                WeatherPrecipType.Resolve(-5f, 0.8f, -5f);
            Check(coldGround.Kind == WeatherPrecipType.PrecipKind.Snow
                  && coldGround.Accumulates,
                  "Precip type: snow on frozen ground accumulates");
            WeatherPrecipType.PrecipReading rainOnCold =
                WeatherPrecipType.Resolve(airTempC, 1f, -5f);
            Check(rainOnCold.Kind == WeatherPrecipType.PrecipKind.Rain
                  && !rainOnCold.Accumulates,
                  "Precip type: rain never accumulates as snowpack");

            // The snow line: the altitude the wet-bulb cools to the snow threshold. At
            // the threshold it sits on the sample; one lapse-rate degree warmer lifts it
            // a kilometre, one degree colder drops it a kilometre, all off one number.
            CheckClose(WeatherPrecipType.SnowLineAltitudeM(
                          AtmoConstants.SnowWetBulbThresholdC, 1500f), 1500f,
                  "Snow line: at the threshold the line sits on the sample");
            CheckClose(WeatherPrecipType.SnowLineAltitudeM(
                          AtmoConstants.SnowWetBulbThresholdC
                          + AtmoConstants.LapseRateCPerKm, 0f),
                  AtmoConstants.MetresPerKilometre,
                  "Snow line: a lapse-degree of surface warmth lifts the line a kilometre");
            CheckClose(WeatherPrecipType.SnowLineAltitudeM(
                          AtmoConstants.SnowWetBulbThresholdC
                          - AtmoConstants.LapseRateCPerKm, 1000f), 0f,
                  "Snow line: a lapse-degree of surface cold drops it a kilometre");
            Check(WeatherPrecipType.SnowLineAltitudeM(5f, 0f)
                      > WeatherPrecipType.SnowLineAltitudeM(2f, 0f),
                  "Snow line: a warmer surface pushes the line higher");
        }

        /// <summary>
        /// Precipitation intensity from the three mechanisms. Checks the frontal rate
        /// reads the kind and depth (cold harder than warm, deeper harder than shallow),
        /// the convective rate reaches its afternoon reference at full instability, and
        /// the combiner is the strongest plus a small blend - never a sum - so a
        /// windward slope reads wetter than its lee under the one storm.
        /// </summary>
        private static void TestWeatherPrecipRate()
        {
            // The frontal rate reads the kind: a cold front is harder than a warm one at
            // the same depth, and the deeper the low the harder either rains.
            float coldDeep = WeatherPrecipType.FrontalRateMmPerH(
                WeatherFront.FrontKind.Cold, AtmoConstants.FrontRainReferenceDepthHpa);
            float warmDeep = WeatherPrecipType.FrontalRateMmPerH(
                WeatherFront.FrontKind.Warm, AtmoConstants.FrontRainReferenceDepthHpa);
            CheckClose(coldDeep, AtmoConstants.ColdFrontRainMmPerH,
                  "Precip rate: a deep cold front rains at its reference intensity");
            CheckClose(warmDeep, AtmoConstants.WarmFrontRainMmPerH,
                  "Precip rate: a warm front is the gentle two millimetres an hour");
            Check(coldDeep > warmDeep,
                  "Precip rate: the cold front rains harder than the warm front");
            Check(WeatherPrecipType.FrontalRateMmPerH(WeatherFront.FrontKind.Cold,
                      AtmoConstants.FrontRainReferenceDepthHpa)
                  > WeatherPrecipType.FrontalRateMmPerH(WeatherFront.FrontKind.Cold, 3f),
                  "Precip rate: a deeper low rains harder");
            Check(WeatherPrecipType.FrontalRateMmPerH(WeatherFront.FrontKind.None, 20f) == 0f,
                  "Precip rate: no front, no frontal rain");

            // The convective rate reaches the heavy afternoon reference at full
            // instability and is nothing in a stable column.
            CheckClose(WeatherPrecipType.ConvectiveRateMmPerH(1f),
                  AtmoConstants.ConvectiveRainReferenceMmPerH,
                  "Precip rate: a fully unstable cell reaches the convective reference");
            Check(WeatherPrecipType.ConvectiveRateMmPerH(0f) == 0f,
                  "Precip rate: a stable column convects no rain");

            // The combiner is the strongest plus a small blend of the rest: three equal
            // showers make a slightly heavier one, never their sum.
            float five = 5f;
            float blended = WeatherPrecipType.CombineRateMmPerH(five, five, five);
            CheckClose(blended, five + AtmoConstants.PrecipBlendFraction * (five + five),
                  "Precip rate: three equal mechanisms blend, they do not sum");
            Check(blended > five && blended < five + five + five,
                  "Precip rate: the blend sits above the strongest and below the sum");

            // The afternoon cell delivers its 30 mm/h while a warm front delivers its 2,
            // the done-when contrast, straight out of the combiner with one mechanism each.
            CheckClose(WeatherPrecipType.CombineRateMmPerH(
                      0f, 0f, AtmoConstants.ConvectiveRainReferenceMmPerH),
                  AtmoConstants.ConvectiveRainReferenceMmPerH,
                  "Precip rate: a lone convective cell delivers its full rate");
            CheckClose(WeatherPrecipType.CombineRateMmPerH(0f, warmDeep, 0f), warmDeep,
                  "Precip rate: a lone warm front delivers its full rate");

            // Windward wetter than lee: more orographic lift on the windward side reads a
            // higher total under the same frontal and convective forcing.
            float windward = WeatherPrecipType.CombineRateMmPerH(20f, warmDeep, 0f);
            float lee = WeatherPrecipType.CombineRateMmPerH(0.5f, warmDeep, 0f);
            Check(windward > lee,
                  "Precip rate: the windward slope stays wetter than its lee");

            // Negative or absent inputs never make negative rain.
            Check(WeatherPrecipType.CombineRateMmPerH(-5f, 0f, 0f) == 0f,
                  "Precip rate: a negative input makes no rain");
        }

        private static void TestWeatherState()
        {
            // The cloud-form classifier: both mechanisms read Mixed, one reads its own
            // form, a clear column reads None.
            Check(CloudCover.ClassifyType(0f, 0f) == CloudCover.CloudType.None,
                  "Cloud type: a clear column is None");
            Check(CloudCover.ClassifyType(0.5f, 0f) == CloudCover.CloudType.Stratiform,
                  "Cloud type: sheet cover alone is Stratiform");
            Check(CloudCover.ClassifyType(0f, 0.5f) == CloudCover.CloudType.Cumuliform,
                  "Cloud type: convective cover alone is Cumuliform");
            Check(CloudCover.ClassifyType(0.5f, 0.5f) == CloudCover.CloudType.Mixed,
                  "Cloud type: sheet and heap together is Mixed");

            // A snapshot fills through the Builder and freezes: every field must survive
            // the freeze unchanged, across the scalars, the int cell index and all four
            // resolver-typed enums.
            WeatherState.Builder b = default;
            b.temperature = 12f;
            b.precipitation = 0.4f;
            b.pressureHpa = 998.5f;
            b.wetBulbC = -1.25f;
            b.precipitationType = WeatherPrecipType.PrecipKind.Snow;
            b.cloudType = CloudCover.CloudType.Mixed;
            b.frontKind = WeatherFront.FrontKind.Cold;
            b.airMassKind = AirMassKind.MaritimePolar;
            b.weatherCellX = 7;
            b.weatherCellZ = -3;
            WeatherState s = b.Build();

            CheckClose(s.temperature, 12f, "WeatherState: temperature round-trips");
            CheckClose(s.precipitation, 0.4f, "WeatherState: precipitation round-trips");
            CheckClose(s.pressureHpa, 998.5f, "WeatherState: pressure round-trips");
            CheckClose(s.wetBulbC, -1.25f, "WeatherState: wet-bulb round-trips");
            Check(s.precipitationType == WeatherPrecipType.PrecipKind.Snow,
                  "WeatherState: precipitation type round-trips");
            Check(s.cloudType == CloudCover.CloudType.Mixed,
                  "WeatherState: cloud type round-trips");
            Check(s.frontKind == WeatherFront.FrontKind.Cold,
                  "WeatherState: front kind round-trips");
            Check(s.airMassKind == AirMassKind.MaritimePolar,
                  "WeatherState: air mass kind round-trips");
            Check(s.weatherCellX == 7 && s.weatherCellZ == -3,
                  "WeatherState: cell indices round-trip");

            // The structural contract: a snapshot is data, so no field is a reference.
            bool allValueTypes = true;
            foreach (var f in typeof(WeatherState).GetFields())
                if (!f.FieldType.IsValueType) allValueTypes = false;
            Check(allValueTypes, "WeatherState: holds no reference fields");
        }

        private static WeatherState MachineSample(float precipMmPerH, float wetBulbC,
            float cloudCover, float temperatureC)
        {
            WeatherState.Builder b = default;
            b.precipitationMmPerHour = precipMmPerH;
            b.wetBulbC = wetBulbC;
            b.cloudCover = cloudCover;
            b.temperature = temperatureC;
            return b.Build();
        }

        private static void TestWeatherMachine()
        {
            WeatherState clear = MachineSample(0f, 10f, 0.2f, 15f);
            WeatherState overcast = MachineSample(0f, 5f, 0.8f, 12f);
            WeatherState rain = MachineSample(5f, 6f, 0.9f, 8f);
            WeatherState snow = MachineSample(5f, -3f, 0.9f, -1f);

            // Classification, in the fixed order: a wet sky splits Snow/Rain on the
            // wet-bulb, an otherwise cloudy sky is Overcast, everything else Clear.
            Check(WeatherMachine.Classify(clear) == WeatherKind.Clear,
                  "Machine: a dry sky under thin cloud is Clear");
            Check(WeatherMachine.Classify(overcast) == WeatherKind.Overcast,
                  "Machine: a dry sky under thick cloud is Overcast");
            Check(WeatherMachine.Classify(rain) == WeatherKind.Rain,
                  "Machine: precip with a warm wet-bulb is Rain");
            Check(WeatherMachine.Classify(snow) == WeatherKind.Snow,
                  "Machine: precip with a cold wet-bulb is Snow");

            // Transition and dwell in simulated seconds.
            WeatherMachine m = new WeatherMachine(10f, 30f);
            m.Tick(clear, 0f);
            Check(m.CurrentKind == WeatherKind.Clear, "Machine: starts in the classified state");

            // The dwell holds the state: rain arriving 10 s in cannot change it yet.
            m.Tick(rain, 10f);
            Check(m.CurrentKind == WeatherKind.Clear, "Machine: dwell holds the state before it elapses");

            // Past the dwell the state commits to Rain.
            m.Tick(rain, 40f);
            Check(m.CurrentKind == WeatherKind.Rain, "Machine: the state changes once the dwell elapses");

            // A wet-bulb crossing part-way through the transition eases temperature between
            // the two states without yet changing the state (dwell not re-elapsed).
            WeatherState mid = m.Tick(snow, 45f);
            Check(m.CurrentKind == WeatherKind.Rain, "Machine: dwell holds Rain during the crossing");
            Check(mid.temperature < rain.temperature && mid.temperature > snow.temperature,
                  "Machine: SmoothStep eases temperature between the two states");

            // Past the dwell again it commits to Snow.
            m.Tick(snow, 70f);
            Check(m.CurrentKind == WeatherKind.Snow, "Machine: Rain gives way to Snow after the dwell");

            // Clock-based and idempotent: stepping to the same simulated time twice
            // yields the identical snapshot, whatever happened between frames. (C-06)
            WeatherState first = m.Tick(snow, 80f);
            WeatherState again = m.Tick(snow, 80f);
            CheckClose(again.temperature, first.temperature,
                       "Machine: a repeated step at one simulated time is identical");
            CheckClose(again.precipitationMmPerHour, first.precipitationMmPerHour,
                       "Machine: a repeated step reproduces every field");
            Check(again.frontKind == first.frontKind && again.cloudType == first.cloudType,
                  "Machine: a repeated step reproduces the categorical fields");
        }

        // ---------------------------------------------------------------------
        //  WEATHER-WIRE1  -  the live-weather wire INTO UCWeatherSystemManager
        //
        //  Proves the opt-in wire added in UCWeatherSystemManager.Weather.cs, on the
        //  real component, not just the WeatherMachine in isolation:
        //    * OFF (default): ResolveWeather() returns the authored sky bit-for-bit,
        //      unchanged by time - the zero-regression bar.
        //    * ON: the authored air is eased on the world clock through the one owned
        //      machine - a change the author makes crosses the dwell once (no flicker)
        //      and the visible field eases across the transition window (no snap).
        //    * The classifier's thermodynamics (dew point, wet-bulb) are the pure
        //      closed-form terms of the authored temperature/humidity - no per-cell
        //      field is invented, and precipitation stays the honest zero (the live
        //      precip feed belongs to the deferred per-cell sim, not this wire).
        // ---------------------------------------------------------------------
        private static void TestWeatherLiveWireIntoManager()
        {
            // Cloud covers safely either side of the overcast threshold, derived from
            // the constant so a retune of it does not break the test.
            float thr = AtmoConstants.OvercastCloudCover01;
            float lowCover = thr * 0.4f;                 // Clear
            float highCover = Mathf.Min(1f, thr + (1f - thr) * 0.8f); // Overcast
            const float Temp = 14f;
            const float RH = 0.6f;

            GameObject go = new GameObject("UC SelfTest Weather Wire");
            try
            {
                UCWeatherSystemManager w = go.AddComponent<UCWeatherSystemManager>();
                w.temperatureCelsius = Temp;
                w.humidity = RH;

                // --- OFF (default): authored sky, bit-for-bit, time-invariant. ---
                w.enableLiveWeatherSim = false;
                w.cloudCoverage = highCover;
                WeatherState off = w.ResolveWeather();
                CheckClose(off.cloudCover, highCover,
                           "Weather wire: OFF returns the authored cloud cover unchanged");
                CheckClose(off.temperature, Temp,
                           "Weather wire: OFF returns the authored temperature unchanged");
                // Ticking while off must not arm the machine or change the result.
                w.TickLiveWeather(10f);
                w.TickLiveWeather(1000f);
                WeatherState offLater = w.ResolveWeather();
                CheckClose(offLater.cloudCover, highCover,
                           "Weather wire: OFF is time-invariant - ticking changes nothing");

                // --- ON: seed Clear, then raise cloud and watch it ease, not snap. ---
                w.enableLiveWeatherSim = true;
                w.cloudCoverage = lowCover;
                w.TickLiveWeather(0f);                 // seed
                WeatherState seeded = w.ResolveWeather();
                CheckClose(seeded.cloudCover, lowCover,
                           "Weather wire: the live sim seeds from the current authored sky");
                // The thermodynamics the classifier reads are the pure authored terms.
                CheckClose(seeded.dewPointC, AtmoMath.DewPointC(Temp, RH),
                           "Weather wire: dew point is the closed-form authored term, not invented");
                CheckClose(seeded.wetBulbC, AtmoMath.WetBulbC(Temp, RH),
                           "Weather wire: wet-bulb is the closed-form authored term, not invented");
                Check(seeded.precipitationMmPerHour == 0f,
                      "Weather wire: precipitation stays the honest zero (no fabricated storm)");

                // Author raises the cloud deck. Half-way through the 20 s transition
                // window (well before the 30 s dwell elapses) the visible cover eases
                // between the two skies and the state has NOT flipped yet - the
                // no-flicker guarantee.
                w.cloudCoverage = highCover;
                w.TickLiveWeather(10f);
                WeatherState mid = w.ResolveWeather();
                Check(mid.cloudCover > lowCover + 1e-4f && mid.cloudCover < highCover - 1e-4f,
                      "Weather wire: cloud eases between the two skies (no snap)");

                // Past the dwell the state commits to Overcast. The commit restarts the
                // transition from that instant, so the eased cover arrives over the NEXT
                // window - tick once to commit, once more past that window to settle.
                w.TickLiveWeather(100000f);   // dwell elapsed -> commit, transition restarts
                w.TickLiveWeather(100030f);   // 30 s later, past the 20 s window -> arrived
                WeatherState settled = w.ResolveWeather();
                CheckClose(settled.cloudCover, highCover,
                           "Weather wire: past the dwell the eased sky reaches the authored cover");

                // Toggling the sim off hands back the authored sky again at once.
                w.enableLiveWeatherSim = false;
                w.TickLiveWeather(100031f);
                WeatherState backToAuthored = w.ResolveWeather();
                CheckClose(backToAuthored.cloudCover, highCover,
                           "Weather wire: toggling OFF restores the authored sky immediately");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static void TestWeatherStorm()
        {
            // Activity: a stable column (no convective cover) never storms; a deep low
            // amplifies an unstable one, and full cover under a deep low saturates.
            Check(WeatherStorm.Activity01(0f, 30f) == 0f,
                  "Storm: a stable column never storms, however deep the low");
            Check(WeatherStorm.Activity01(0.5f, 20f) > WeatherStorm.Activity01(0.5f, 0f),
                  "Storm: a deep low deepens an unstable column");
            CheckClose(WeatherStorm.Activity01(1f, 20f), 1f,
                       "Storm: full convection under a deep low saturates");

            // Lightning rate: a stratus deck below the threshold never flashes; full
            // activity reaches the reference rate.
            Check(WeatherStorm.LightningRatePerMinute(0.05f) == 0f,
                  "Storm: a stratus deck below the threshold throws no lightning");
            Check(WeatherStorm.LightningRatePerMinute(1f) > 0f,
                  "Storm: a full storm throws lightning");

            // Strikes are deterministic: the same cell and minute draw the same count.
            int a1 = WeatherStorm.StrikesInMinute(42, 100, 5f);
            int a2 = WeatherStorm.StrikesInMinute(42, 100, 5f);
            Check(a1 == a2, "Storm: two machines flash together at the same cell and minute");
            Check(WeatherStorm.StrikesInMinute(42, 100, 0f) == 0,
                  "Storm: a zero rate is zero strikes");

            // The Poisson draw is unbiased: summed over many minutes it lands near the
            // mean rate, so the flashing neither starves nor floods.
            int total = 0;
            int minutes = 2000;
            for (int minute = 0; minute < minutes; minute++)
                total += WeatherStorm.StrikesInMinute(42, minute, 5f);
            float perMinute = total / (float)minutes;
            CheckWithin(perMinute, 5f, 0.5f, "Storm: the strike count averages the rate");

            // Thunder trails the flash by distance over the speed of sound.
            CheckClose(WeatherStorm.ThunderDelaySeconds(343f), 1f,
                       "Storm: thunder a speed-of-sound away trails by one second");
            CheckClose(WeatherStorm.ThunderDelaySeconds(686f), 2f,
                       "Storm: thunder twice as far trails twice as long");
            Check(WeatherStorm.ThunderDelaySeconds(0f) == 0f,
                  "Storm: a strike overhead cracks at once");
        }

        private static void TestWeatherAirMass()
        {
            // Classification by origin: a low, moist origin is maritime tropical; a low,
            // dry one continental tropical; mid latitudes are polar of the matching surface;
            // and anything poleward of the Arctic Circle is Arctic whatever its surface.
            Check(WeatherAirMass.Classify(10f, 0.2f) == AirMassKind.MaritimeTropical,
                  "AirMass: a low, moist origin forms maritime tropical air");
            Check(WeatherAirMass.Classify(10f, 0.8f) == AirMassKind.ContinentalTropical,
                  "AirMass: a low, dry origin forms continental tropical air");
            Check(WeatherAirMass.Classify(45f, 0.2f) == AirMassKind.MaritimePolar,
                  "AirMass: a mid-latitude moist origin forms maritime polar air");
            Check(WeatherAirMass.Classify(45f, 0.8f) == AirMassKind.ContinentalPolar,
                  "AirMass: a mid-latitude dry origin forms continental polar air");
            Check(WeatherAirMass.Classify(75f, 0.8f) == AirMassKind.Arctic,
                  "AirMass: a high-latitude origin forms Arctic air whatever its surface");
            Check(WeatherAirMass.Classify(-45f, 0.8f) == AirMassKind.ContinentalPolar,
                  "AirMass: the southern hemisphere classifies by absolute latitude");

            // Thermal ordering: tropical air is the warmest anomaly, polar colder, Arctic coldest.
            Check(WeatherAirMass.TemperatureAnomalyC(AirMassKind.MaritimeTropical)
                  > WeatherAirMass.TemperatureAnomalyC(AirMassKind.MaritimePolar),
                  "AirMass: tropical air is warmer than polar");
            Check(WeatherAirMass.TemperatureAnomalyC(AirMassKind.MaritimePolar)
                  > WeatherAirMass.TemperatureAnomalyC(AirMassKind.Arctic),
                  "AirMass: polar air is warmer than Arctic");

            // Moisture ordering: maritime air is moister than continental of the same class,
            // and Arctic air is the driest of all.
            Check(WeatherAirMass.DewPointAnomalyC(AirMassKind.MaritimeTropical)
                  > WeatherAirMass.DewPointAnomalyC(AirMassKind.ContinentalTropical),
                  "AirMass: maritime air is moister than continental");
            Check(WeatherAirMass.DewPointAnomalyC(AirMassKind.Arctic)
                  < WeatherAirMass.DewPointAnomalyC(AirMassKind.ContinentalPolar),
                  "AirMass: Arctic air is the driest of all");

            // The done-when: a cold front swaps maritime tropical air for continental polar,
            // and both the temperature and the dew point step down together - genuinely
            // colder, drier air, not merely rain switched on over the same warmth.
            WeatherAirMass.AirMassAnomaly warm = WeatherAirMass.Resolve(10f, 0.2f);
            WeatherAirMass.AirMassAnomaly cold = WeatherAirMass.Resolve(45f, 0.8f);
            Check(cold.TemperatureAnomalyC < warm.TemperatureAnomalyC,
                  "AirMass: a cold front steps the temperature down");
            Check(cold.DewPointAnomalyC < warm.DewPointAnomalyC,
                  "AirMass: a cold front steps the dew point down at the same time");
        }

        private static void TestWeatherPrecipEmitter()
        {
            // Fall speed from drop size (Atlas-Ulbrich): a 2 mm drop falls at ~6.5 m/s and
            // a 5 mm drop at ~9, and speed rises with size the whole way up.
            CheckWithin(PrecipitationEmitter.RainFallSpeedMPerS(2f), 6.5f, 0.3f,
                        "Precip: a 2 mm drop falls at about 6.5 m/s");
            CheckWithin(PrecipitationEmitter.RainFallSpeedMPerS(5f), 9.1f, 0.3f,
                        "Precip: a 5 mm drop falls at about 9 m/s");
            Check(PrecipitationEmitter.RainFallSpeedMPerS(5f) > PrecipitationEmitter.RainFallSpeedMPerS(2f),
                  "Precip: bigger drops fall faster");
            Check(PrecipitationEmitter.RainFallSpeedMPerS(0f) >= 0f,
                  "Precip: a vanishing drop does not fall upward");

            // Marshall-Palmer: heavier rain has a gentler slope, which is bigger drops -
            // heavy rain is not more small drops, it is larger ones.
            Check(PrecipitationEmitter.MarshallPalmerSlopePerMm(30f)
                  < PrecipitationEmitter.MarshallPalmerSlopePerMm(1f),
                  "Precip: heavier rain has a gentler drop-size slope");
            Check(PrecipitationEmitter.MedianDropDiameterMm(30f)
                  > PrecipitationEmitter.MedianDropDiameterMm(1f),
                  "Precip: heavy rain has a larger median drop than drizzle");
            Check(PrecipitationEmitter.DropNumberDensity(3f, 30f)
                  < PrecipitationEmitter.DropNumberDensity(1f, 30f),
                  "Precip: at one rate there are always fewer large drops than small");

            // A streak is fall speed across one shutter, so rain streaks and snow does not.
            float rainStreak = PrecipitationEmitter.StreakLengthM(
                PrecipitationEmitter.RainFallSpeedMPerS(3f));
            float snowStreak = PrecipitationEmitter.StreakLengthM(
                AtmoConstants.SnowTerminalVelocityMPerS);
            Check(rainStreak > snowStreak, "Precip: rain streaks far longer than snow in one shutter");

            // Snow fall speed is deterministic per flake and stays within its variance band.
            Check(PrecipitationEmitter.SnowFallSpeedMPerS(7) == PrecipitationEmitter.SnowFallSpeedMPerS(7),
                  "Precip: the same flake index always draws the same fall speed");
            float lo = AtmoConstants.SnowTerminalVelocityMPerS * (1f - AtmoConstants.SnowFallSpeedVariance01);
            float hi = AtmoConstants.SnowTerminalVelocityMPerS * (1f + AtmoConstants.SnowFallSpeedVariance01);
            bool inBand = true;
            for (int flake = 0; flake < 500; flake++)
            {
                float v = PrecipitationEmitter.SnowFallSpeedMPerS(flake);
                if (v < lo - EcoEpsilon || v > hi + EcoEpsilon) inBand = false;
            }
            Check(inBand, "Precip: every snowflake falls within its variance band");
        }

        // Dries a fully wet surface with no rain for a span of simulated seconds at a
        // fixed PET, returning the wetness left. Used to contrast July with December.
        private static float DryDownWetness(float petMmPerDay, float totalSeconds)
        {
            float wetness = 1f;
            float dt = 10f;
            for (float t = 0f; t < totalSeconds; t += dt)
                wetness = WetnessSimulation.Step(wetness, 0f, petMmPerDay, dt);
            return wetness;
        }

        private static void TestWetnessSimulation()
        {
            // Wetting tracks the rain: harder rain wets faster, and no rain never wets.
            Check(WetnessSimulation.WettingRatePerSecond(30f)
                  > WetnessSimulation.WettingRatePerSecond(1f),
                  "Wetness: a downpour wets faster than a drizzle");
            Check(WetnessSimulation.WettingRatePerSecond(0f) == 0f,
                  "Wetness: no rain never wets the ground");

            // Drying is set by PET: a high-PET afternoon dries faster than a low-PET day.
            // This is the whole point - the season lives in the PET term.
            Check(WetnessSimulation.DryingRatePerSecond(8f)
                  > WetnessSimulation.DryingRatePerSecond(0.5f),
                  "Wetness: high PET dries faster than low PET");

            // Rain wets a dry surface upward, and the wetness stays inside 0..1.
            float wetted = WetnessSimulation.Step(0f, 10f, 0f, 30f);
            Check(wetted > 0f && wetted <= 1f, "Wetness: rain wets a dry surface toward saturation");

            // The done-when: the same soaking dries in minutes under a July PET but
            // persists most of a day under a December PET, with no seasonal constant.
            float julyLeft = DryDownWetness(8f, 1300f);      // ~22 minutes, hot summer PET
            float decemberLeft = DryDownWetness(0.5f, 1300f); // same span, cold winter PET
            Check(julyLeft <= 0f, "Wetness: a July soaking dries within minutes");
            Check(decemberLeft > 0.5f, "Wetness: a December soaking is still wet after the same span");

            // Rate limiting: even an absurd rain over a long step cannot make wetness jump.
            float jump = WetnessSimulation.Step(0f, 1_000_000f, 0f, 1f);
            Check(jump <= AtmoConstants.WetnessMaxRatePerSecond + EcoEpsilon,
                  "Wetness: wetness can never jump in a single step");

            // The store is always clamped to 0..1 whatever the inputs.
            Check(WetnessSimulation.Step(1f, 0f, 1000f, 10f) >= 0f,
                  "Wetness: heavy drying never drives wetness below zero");
        }

        private static void TestWetnessCoupling()
        {
            // The coupling owns the shipped wetness global, and publishes into its x.
            Check(WetnessCoupling.WetParamsId == Shader.PropertyToID("_UCWetParams"),
                  "WetnessCoupling: writes the shipped _UCWetParams global");

            WetnessCoupling.Publish(0.7f, 0.3f, 0.5f);
            Vector4 published = Shader.GetGlobalVector(WetnessCoupling.WetParamsId);
            CheckClose(published.x, 0.7f, "WetnessCoupling: publishes the wetness into x");
            CheckClose(published.w, 1f, "WetnessCoupling: marks the world live");

            // Out-of-range inputs are clamped at the single uploader, so no caller can
            // push a wetness past 1 at the GPU.
            WetnessCoupling.Publish(2f, 2f, 2f);
            CheckClose(Shader.GetGlobalVector(WetnessCoupling.WetParamsId).x, 1f,
                       "WetnessCoupling: clamps an over-range wetness to one");

            // Clearing returns a dry, still world.
            WetnessCoupling.Clear();
            CheckClose(Shader.GetGlobalVector(WetnessCoupling.WetParamsId).x, 0f,
                       "WetnessCoupling: clears to a dry world");
        }

        private static float FillPuddle(float slopeDeg, float convexityPerM, float twi,
                                        float soilPermeability01, float rainMmPerH,
                                        float petMmPerDay, float totalSeconds)
        {
            float depth = 0f;
            float dt = 10f;
            for (float t = 0f; t < totalSeconds; t += dt)
                depth = WetnessSimulation.PuddleStep(depth, rainMmPerH, petMmPerDay,
                                                     slopeDeg, convexityPerM, twi,
                                                     soilPermeability01, dt);
            return depth;
        }

        private static void TestPuddling()
        {
            // The slope gate: a level cell holds all its water, and anything as steep
            // as a hillside holds none. This is what keeps puddles off the slopes.
            CheckClose(WetnessSimulation.PuddleRetention01(0f), 1f,
                       "Puddle: level ground holds all its standing water");
            Check(WetnessSimulation.PuddleRetention01(20f) <= EcoEpsilon,
                  "Puddle: a 20-degree slope holds no standing water");
            Check(WetnessSimulation.PuddleRetention01(0f) > WetnessSimulation.PuddleRetention01(10f),
                  "Puddle: retention falls off as the ground tilts");

            // The gather term: a ridge or open flat receives only its own rain (gain 1),
            // while a concave hollow with a high wetness index draws in far more.
            CheckClose(WetnessSimulation.PuddleCatchmentGain(2f, 0.05f), 1f,
                       "Puddle: a convex ridge gathers only the rain that lands on it");
            Check(WetnessSimulation.PuddleCatchmentGain(12f, -0.05f)
                  > WetnessSimulation.PuddleCatchmentGain(2f, 0.05f),
                  "Puddle: a high-index hollow gathers more than a ridge");

            // The done-when, in depths. On impermeable flats and in hollows a puddle
            // forms; on a 20-degree slope it never does, under the same rain.
            float flat = FillPuddle(0f, 0f, 2f, 0f, 10f, 0f, 3600f);
            float hollow = FillPuddle(0f, -0.05f, 12f, 0f, 10f, 0f, 3600f);
            float slope = FillPuddle(20f, -0.05f, 12f, 0f, 10f, 0f, 3600f);
            Check(flat > 0f, "Puddle: an impermeable flat pools under steady rain");
            Check(hollow > flat, "Puddle: a hollow pools deeper than a flat under the same rain");
            Check(slope <= EcoEpsilon, "Puddle: a 20-degree slope never pools, however long it rains");

            // Depth is bounded: past the pond threshold it is the hydrology system's water,
            // so an endless downpour cannot run the store away to infinity.
            float flooded = FillPuddle(0f, -0.05f, 12f, 0f, 30f, 0f, 86400f);
            Check(flooded <= AtmoConstants.PuddleMaxDepthMetres + EcoEpsilon,
                  "Puddle: depth is capped at the pond threshold");

            // Drying reverses it: once the rain stops, infiltration and PET draw the
            // puddle back down.
            float drained = WetnessSimulation.PuddleStep(hollow, 0f, 5f,
                                                         0f, -0.05f, 12f, 0.5f, 3600f);
            Check(drained < hollow, "Puddle: a puddle drains once the rain stops");

            // Same shape of ground, same puddle, every rebuild: the step is a pure
            // function of the deterministic topography.
            float a = WetnessSimulation.PuddleStep(0.02f, 8f, 3f, 1f, -0.03f, 9f, 0.3f, 12f);
            float b = WetnessSimulation.PuddleStep(0.02f, 8f, 3f, 1f, -0.03f, 9f, 0.3f, 12f);
            Check(a == b, "Puddle: the same ground fills to the same depth every rebuild");
        }

        private static void TestSurfaceFriction()
        {
            // A sample well above and well below freezing, so the ice band is either
            // fully off or fully on and the ordering is unambiguous.
            const float warm = 15f;
            const float frozen = -5f;
            float full = AtmoConstants.SnowFrictionFullDepthM;

            float dry = WetnessSimulation.SurfaceFriction01(0f, 0f, warm);
            float wet = WetnessSimulation.SurfaceFriction01(1f, 0f, warm);
            float snow = WetnessSimulation.SurfaceFriction01(0f, full, frozen);
            float ice = WetnessSimulation.SurfaceFriction01(1f, 0f, frozen);

            // The one ordering a traction model must not get wrong: dry grips fully,
            // then wet, then snow, then glaze ice - which is far the slipperiest.
            CheckClose(dry, 1f, "Friction: dry ground grips fully");
            Check(wet < dry, "Friction: a wet film has less grip than dry ground");
            Check(snow < wet, "Friction: snow has less grip than a wet road");
            Check(ice < snow, "Friction: glaze ice is more slippery than snow");
            CheckClose(ice, AtmoConstants.IceFriction01, "Friction: full ice reaches the ice floor");

            // Ice needs liquid water to freeze: cold but dry ground is not slippery,
            // so freezing temperature alone must not drop the grip.
            CheckClose(WetnessSimulation.SurfaceFriction01(0f, 0f, frozen), 1f,
                       "Friction: cold but dry ground still grips - no water to glaze");

            // Bounded and deterministic.
            float extreme = WetnessSimulation.SurfaceFriction01(5f, 5f, -50f);
            Check(extreme >= 0f && extreme <= 1f, "Friction: stays within 0..1 for out-of-range inputs");
            Check(WetnessSimulation.SurfaceFriction01(0.6f, 0.02f, -0.5f)
                  == WetnessSimulation.SurfaceFriction01(0.6f, 0.02f, -0.5f),
                  "Friction: the same surface resolves to the same grip every call");
        }

        private static void TestWaterWeatherCoupling()
        {
            // The gap the coupling closes: a dry sky leaves the water still, and any
            // rain at all sets the simulation dimpling.
            CheckClose(UCWaterWeatherCoupling.RainDropsPerSecond(0f, 128f), 0f,
                       "Water rain: a dry sky leaves the surface still");
            Check(UCWaterWeatherCoupling.RainDropsPerSecond(5f, 128f) > 0f,
                  "Water rain: any precipitation stipples the surface");

            // Linear in the rain: twice the downpour is twice the drops.
            float light = UCWaterWeatherCoupling.RainDropsPerSecond(5f, 128f);
            float heavy = UCWaterWeatherCoupling.RainDropsPerSecond(10f, 128f);
            CheckClose(heavy, light * 2f, "Water rain: drop rate is linear in the rain rate");

            // Scaled by window area, so a wide window and a narrow one stipple to the
            // same density rather than the same total count.
            Check(UCWaterWeatherCoupling.RainDropsPerSecond(10f, 256f)
                  > UCWaterWeatherCoupling.RainDropsPerSecond(10f, 128f),
                  "Water rain: a larger window receives more drops at the same rain");

            // Safe with no simulation in the scene, which is how the driver calls it.
            UCWaterWeatherCoupling.DriveRain(10f);
            UCWaterWeatherCoupling.Clear();
            Check(true, "Water rain: driving and clearing are safe with no active simulation");

            // Ice: open above freezing, ramping to a locked sheet as the cold deepens.
            CheckClose(UCWaterWeatherCoupling.IceCoverFraction01(2f), 0f,
                       "Water ice: a surface above freezing carries no ice");
            Check(UCWaterWeatherCoupling.IceCoverFraction01(-2f) > 0f
                  && UCWaterWeatherCoupling.IceCoverFraction01(-2f) < 1f,
                  "Water ice: a surface a little below freezing is partly iced");
            CheckClose(UCWaterWeatherCoupling.IceCoverFraction01(-40f), 1f,
                       "Water ice: a deep freeze locks the surface fully");
            Check(UCWaterWeatherCoupling.IceCoverFraction01(-3f)
                  > UCWaterWeatherCoupling.IceCoverFraction01(-1f),
                  "Water ice: a colder surface freezes further");

            // The still-only gate: a lake ices over while the river beside it, at the
            // same temperature, runs open - the W-54 done-when in one assertion.
            Check(UCWaterWeatherCoupling.FreezesWhenStill(UniverseCreator.Nodes.WaterBodyKind.Lake)
                  && UCWaterWeatherCoupling.FreezesWhenStill(UniverseCreator.Nodes.WaterBodyKind.Pool),
                  "Water ice: still lakes and pools can freeze");
            Check(!UCWaterWeatherCoupling.FreezesWhenStill(UniverseCreator.Nodes.WaterBodyKind.River)
                  && !UCWaterWeatherCoupling.FreezesWhenStill(UniverseCreator.Nodes.WaterBodyKind.Ocean),
                  "Water ice: moving rivers and the open sea never freeze in this model");
            Check(UCWaterWeatherCoupling.IceCoverFor(UniverseCreator.Nodes.WaterBodyKind.Lake, -40f) > 0f,
                  "Water ice: a lake ices over in a deep freeze");
            CheckClose(UCWaterWeatherCoupling.IceCoverFor(UniverseCreator.Nodes.WaterBodyKind.River, -40f), 0f,
                       "Water ice: the river runs open in the same freeze");
        }

        private static void TestFogSimulation()
        {
            // Radiation fog: a clear, calm, saturated night in a valley is its textbook
            // case, and each missing condition on its own must switch it off.
            float valleyNight = FogSimulation.RadiationFog01(0f, 0f, 5f, 5f, -5f, 12f);
            Check(valleyNight > 0.5f, "Fog: a clear calm saturated night fogs the valley");
            CheckClose(FogSimulation.RadiationFog01(1f, 0f, 5f, 5f, -5f, 12f), 0f,
                       "Fog: an overcast sky gives radiation fog nothing to cool to");
            CheckClose(FogSimulation.RadiationFog01(0f, 10f, 5f, 5f, -5f, 12f), 0f,
                       "Fog: wind mixes the cold layer out and radiation fog cannot settle");
            CheckClose(FogSimulation.RadiationFog01(0f, 0f, 15f, 5f, -5f, 12f), 0f,
                       "Fog: dry air well above its dew point does not fog");
            CheckClose(FogSimulation.RadiationFog01(0f, 0f, 5f, 5f, 30f, 12f), 0f,
                       "Fog: a high sun burns radiation fog off");
            Check(valleyNight > FogSimulation.RadiationFog01(0f, 0f, 5f, 5f, -5f, 0f),
                  "Fog: a hollow pools radiation fog deeper than an open flat");

            // Advection fog: warm moist air over a cold surface, and it does not care
            // about the sun - it has no sun term, so it is the same at noon as at dawn.
            CheckClose(FogSimulation.AdvectionFog01(15f, 10f, 1f), 1f,
                       "Fog: warm moist air over a cold surface makes sea fog");
            CheckClose(FogSimulation.AdvectionFog01(10f, 15f, 1f), 0f,
                       "Fog: cold air over a warm surface is not advection fog");
            CheckClose(FogSimulation.AdvectionFog01(15f, 10f, 0.5f), 0f,
                       "Fog: dry air does not fog however cold the ground");

            // Upslope fog: cloud on a windward mountainside, above the cloud base only.
            CheckClose(FogSimulation.UpslopeFog01(1200f, 1000f, 1f), 1f,
                       "Fog: a windward slope above the cloud base sits in cloud");
            CheckClose(FogSimulation.UpslopeFog01(800f, 1000f, 1f), 0f,
                       "Fog: below the cloud base there is no upslope fog");
            CheckClose(FogSimulation.UpslopeFog01(1200f, 1000f, 0f), 0f,
                       "Fog: a sheltered lee slope gets no upslope fog");

            // Steam fog: cold air over warm water, and only over water.
            CheckClose(FogSimulation.SteamFog01(12f, 5f, 1f), 1f,
                       "Fog: cold air over warm water steams");
            CheckClose(FogSimulation.SteamFog01(12f, 5f, 0f), 0f,
                       "Fog: steam fog never forms over land");
            CheckClose(FogSimulation.SteamFog01(5f, 12f, 1f), 0f,
                       "Fog: warm air over cold water does not steam");

            // The strongest mechanism wins rather than the four summing.
            CheckClose(FogSimulation.SurfaceDensity01(0.2f, 0.9f, 0.1f, 0f), 0.9f,
                       "Fog: the surface density is the strongest mechanism, not the sum");

            // The exponential profile: full at the ground, thinner with height, so a
            // ridge stands clear above a fogged valley floor.
            CheckClose(FogSimulation.DensityAtHeight(1f, 0f, AtmoConstants.FogScaleHeightM), 1f,
                       "Fog: density is undiminished at the fog's ground level");
            Check(FogSimulation.DensityAtHeight(1f, AtmoConstants.FogScaleHeightM,
                                                AtmoConstants.FogScaleHeightM) < 1f,
                  "Fog: density falls with height above the ground");
            Check(FogSimulation.DensityAtHeight(1f, 0f, AtmoConstants.FogScaleHeightM)
                  > FogSimulation.DensityAtHeight(1f, 2f * AtmoConstants.FogScaleHeightM,
                                                  AtmoConstants.FogScaleHeightM),
                  "Fog: the valley floor fogs while the ridge above it stands clear");
            Check(IsFinite(FogSimulation.DensityAtHeight(1f, 5f, AtmoConstants.FogScaleHeightM)),
                  "Fog: the height profile stays finite");
        }

        // ---------------------------------------------------------------------
        //  WEATHER VISIBILITY - fog and precipitation reduce sight together
        // ---------------------------------------------------------------------
        private static void TestWeatherVisibility()
        {
            // Fog alone: clear air with no fog, dropping to the reference floor at
            // full density, and monotonically thinning vision as it thickens.
            CheckWithin(WeatherVisibility.FogVisibilityMetres(0f),
                        AtmoConstants.VisibilityClearMetres, 1f,
                        "Visibility: no fog sees to the clear-air ceiling");
            CheckWithin(WeatherVisibility.FogVisibilityMetres(1f),
                        AtmoConstants.FogVisibilityAtFullDensityMetres, 0.5f,
                        "Visibility: the thickest fog sits at its reference floor");
            Check(WeatherVisibility.FogVisibilityMetres(0.2f)
                  > WeatherVisibility.FogVisibilityMetres(0.8f),
                  "Visibility: thicker fog sees less far");

            // Precipitation alone: none is clear; the reference rain and snow rates
            // land on their anchors; light rain barely changes anything.
            CheckWithin(WeatherVisibility.PrecipVisibilityMetres(0f, 0f),
                        AtmoConstants.VisibilityClearMetres, 1f,
                        "Visibility: no precipitation sees to the clear-air ceiling");
            CheckWithin(WeatherVisibility.PrecipVisibilityMetres(
                            AtmoConstants.PrecipVisibilityReferenceMmPerH, 0f),
                        AtmoConstants.RainVisibilityAtReferenceMetres, 1f,
                        "Visibility: heavy rain caps sight near its reference");
            CheckWithin(WeatherVisibility.PrecipVisibilityMetres(
                            AtmoConstants.PrecipVisibilityReferenceMmPerH, 1f),
                        AtmoConstants.SnowVisibilityAtReferenceMetres, 1f,
                        "Visibility: heavy snow caps sight far lower than rain");
            Check(WeatherVisibility.PrecipVisibilityMetres(1f, 0f) > 8000f,
                  "Visibility: light rain barely changes how far the eye sees");

            // Snow scatters far harder than rain, so the same rate as snow sees much
            // less far - heavy snow is the lowest-visibility condition in the world.
            Check(WeatherVisibility.PrecipVisibilityMetres(25f, 1f)
                  < WeatherVisibility.PrecipVisibilityMetres(25f, 0f),
                  "Visibility: snow sees less far than rain at the same rate");
            Check(WeatherVisibility.PrecipVisibilityMetres(10f, 1f)
                  < WeatherVisibility.PrecipVisibilityMetres(10f, 0f),
                  "Visibility: heavy snow is the lowest-visibility condition");

            // Combining: both absent stays at the ceiling; adding a second veil is
            // never better than either alone; and two light veils never multiply
            // into an artificial whiteout.
            CheckWithin(WeatherVisibility.CombineMetres(
                            AtmoConstants.VisibilityClearMetres,
                            AtmoConstants.VisibilityClearMetres),
                        AtmoConstants.VisibilityClearMetres, 1f,
                        "Visibility: two clear veils leave the clear-air ceiling");
            float combined = WeatherVisibility.CombineMetres(1000f, 2000f);
            Check(combined <= 1000f && combined <= 2000f,
                  "Visibility: two veils together see no farther than the worse one");
            Check(WeatherVisibility.CombineMetres(10000f, 10000f) > 3000f,
                  "Visibility: light haze and light rain never whiteout to metres");

            // The shipped call is the two components combined.
            CheckWithin(WeatherVisibility.VisibilityMetres(0.5f, 25f, 0f),
                        WeatherVisibility.CombineMetres(
                            WeatherVisibility.FogVisibilityMetres(0.5f),
                            WeatherVisibility.PrecipVisibilityMetres(25f, 0f)), 1f,
                        "Visibility: the world call is fog and precipitation combined");
            Check(IsFinite(WeatherVisibility.VisibilityMetres(0.7f, 12f, 0.5f)),
                  "Visibility: the combined visibility stays finite");
        }

        // ---------------------------------------------------------------------
        //  SNOW MODEL - snow water equivalent as the stored quantity
        // ---------------------------------------------------------------------
        private static void TestSnowModel()
        {
            // The stored quantity is water: 1 mm SWE is 1 kg/m2, so the load handed
            // to the canopy budget is the SWE itself with no conversion factor.
            CheckClose(SnowModel.LoadKgPerM2(10f), 10f,
                       "Snow: 1 mm SWE is 1 kg/m2, so the load is the SWE itself");
            CheckClose(SnowModel.LoadKgPerM2(-5f), 0f,
                       "Snow: a negative SWE carries no load");

            // Density firms from fresh toward settled with age, most of it early.
            CheckWithin(SnowModel.DensityKgPerM3(0f),
                        AtmoConstants.SnowFreshDensityKgPerM3, 0.5f,
                        "Snow: a fresh pack is at the fresh density");
            Check(SnowModel.DensityKgPerM3(1f) > SnowModel.DensityKgPerM3(0f),
                  "Snow: the pack firms up as it ages");
            Check(SnowModel.DensityKgPerM3(30f) < AtmoConstants.SnowSettledDensityKgPerM3
                  && SnowModel.DensityKgPerM3(30f) > AtmoConstants.SnowFreshDensityKgPerM3,
                  "Snow: an old pack has firmed toward but not past the settled density");

            // Depth follows density: fresh 10 mm SWE stands ~100 mm deep and settles
            // toward ~33 mm as the same water compacts - depth changes, water does not.
            CheckWithin(SnowModel.DepthMetres(10f, AtmoConstants.SnowFreshDensityKgPerM3),
                        0.1f, 1e-4f,
                        "Snow: 10 mm of fresh SWE stands ~100 mm deep");
            CheckWithin(SnowModel.DepthMetres(10f, AtmoConstants.SnowSettledDensityKgPerM3),
                        0.0333f, 1e-3f,
                        "Snow: the same 10 mm SWE settles toward ~33 mm of depth");
            Check(SnowModel.DepthMetresAtAge(10f, 0f)
                  > SnowModel.DepthMetresAtAge(10f, 30f),
                  "Snow: the same water stands deeper fresh than settled");
            CheckClose(SnowModel.DepthMetres(0f, AtmoConstants.SnowFreshDensityKgPerM3), 0f,
                       "Snow: no water is no depth");
            Check(IsFinite(SnowModel.DepthMetresAtAge(25f, 5f)),
                  "Snow: the depth stays finite");

            // Albedo starts brilliant and darkens with age.
            CheckWithin(SnowModel.SnowAlbedo(0f), AtmoConstants.SnowFreshAlbedo01, 0.01f,
                        "Snow: a fresh surface is at the fresh albedo");
            Check(SnowModel.SnowAlbedo(30f) < SnowModel.SnowAlbedo(0f)
                  && SnowModel.SnowAlbedo(30f) > AtmoConstants.SnowOldAlbedo01 - 0.01f,
                  "Snow: the surface darkens toward the old albedo with age");

            // Melt: pure degree-day with no sun is the degree-day factor times the
            // positive temperature.
            CheckWithin(SnowModel.MeltMmPerDay(1f, 0f, AtmoConstants.SnowFreshAlbedo01),
                        AtmoConstants.DegreeDayMeltMmPerCPerDay, 1e-3f,
                        "Snow: with no sun, melt is the degree-day term alone");

            // A low winter sun over cold snow clears nothing - both terms gate to zero
            // below freezing.
            CheckClose(SnowModel.MeltMmPerDay(-8f, 6f, AtmoConstants.SnowFreshAlbedo01), 0f,
                       "Snow: a mid-winter sunny day melts nothing");

            // Aspect: the warmer equator-facing slope melts faster than the cool one,
            // so it clears first.
            Check(SnowModel.MeltMmPerDay(6f, 25f, AtmoConstants.SnowOldAlbedo01)
                  > SnowModel.MeltMmPerDay(0f, 12f, AtmoConstants.SnowOldAlbedo01),
                  "Snow: the warm slope melts faster than the cool slope");

            // Albedo: dark old snow melts faster than fresh white snow on the same day.
            Check(SnowModel.MeltMmPerDay(3f, 22f, AtmoConstants.SnowOldAlbedo01)
                  > SnowModel.MeltMmPerDay(3f, 22f, AtmoConstants.SnowFreshAlbedo01),
                  "Snow: dirty snow melts faster than fresh under the same sun");

            // Applying melt draws the store down and never past empty.
            CheckClose(SnowModel.ApplyMelt(100f, 30f, 1f), 70f,
                       "Snow: a day of melt draws the store down by the melt");
            CheckClose(SnowModel.ApplyMelt(10f, 30f, 1f), 0f,
                       "Snow: melt cannot draw the store below empty");
            CheckClose(SnowModel.ApplyMelt(50f, 0f, 5f), 50f,
                       "Snow: no melt leaves the store untouched");
            Check(IsFinite(SnowModel.MeltMmPerDay(4f, 20f, 0.6f)),
                  "Snow: the melt rate stays finite");

            // Where snow cannot lie. Slope shedding: full below the shed angle, gone
            // above the full-shed angle, easing between.
            CheckClose(SnowModel.SlopeRetention01(30f), 1f,
                       "Snow: a gentle slope keeps all its snow");
            CheckClose(SnowModel.SlopeRetention01(65f), 0f,
                       "Snow: a cliff sheds all its snow and reads as bare rock");
            Check(SnowModel.SlopeRetention01(50f) > 0f && SnowModel.SlopeRetention01(50f) < 1f,
                  "Snow: a steep slope holds only some snow on its ledges");

            // Wind: a lee hollow piles deep, a neutral face is unchanged, a windward
            // ridge scours bare.
            CheckWithin(SnowModel.WindRedistributionFactor(0.5f), 1f, 1e-3f,
                        "Snow: neutral ground gets the open-ground snowfall");
            Check(SnowModel.WindRedistributionFactor(0f) > 1f,
                  "Snow: a lee hollow accumulates more than open ground - a cornice");
            Check(SnowModel.WindRedistributionFactor(1f) < 1f,
                  "Snow: a windward ridge scours below open ground");

            // Ground: frozen ground keeps snow, warm ground melts it on contact.
            CheckClose(SnowModel.GroundRetention01(-5f), 1f,
                       "Snow: frozen ground keeps all the snow that falls");
            CheckClose(SnowModel.GroundRetention01(3f), 0f,
                       "Snow: warm ground melts the snow on contact");

            // The combined factor, one snowfall producing every case at once.
            CheckClose(SnowModel.AccumulationFactor(10f, 0.5f, -2f, true), 0f,
                       "Snow: open water never accumulates snow");
            CheckClose(SnowModel.AccumulationFactor(65f, 0.5f, -2f, false), 0f,
                       "Snow: a cliff face stays bare");
            CheckClose(SnowModel.AccumulationFactor(10f, 0.5f, 3f, false), 0f,
                       "Snow: warm ground stays snow-free");
            Check(SnowModel.AccumulationFactor(10f, 0f, -2f, false) > 1f,
                  "Snow: a cold lee slope piles up deep");
            Check(SnowModel.AccumulationFactor(20f, 1f, -2f, false)
                  < SnowModel.AccumulationFactor(20f, 0f, -2f, false),
                  "Snow: the windward ridge is bare where the lee slope is deep");
            Check(IsFinite(SnowModel.AccumulationFactor(35f, 0.7f, -1f, false)),
                  "Snow: the accumulation factor stays finite");

            // Snow's effect back on the world. Albedo: bare soil brightens toward the
            // snow's own albedo as cover fills in, and clearing the snow restores soil.
            CheckClose(SnowModel.GroundAlbedo(AtmoConstants.SoilAlbedo01, 0f, 0f),
                       AtmoConstants.SoilAlbedo01,
                       "Snow: no cover leaves the ground at the soil albedo");
            CheckWithin(SnowModel.GroundAlbedo(AtmoConstants.SoilAlbedo01, 1f, 0f),
                        AtmoConstants.SnowFreshAlbedo01, 0.01f,
                        "Snow: full fresh cover brightens the ground to the fresh albedo");
            Check(SnowModel.GroundAlbedo(AtmoConstants.SoilAlbedo01, 1f, 0f)
                  > SnowModel.GroundAlbedo(AtmoConstants.SoilAlbedo01, 1f, 30f),
                  "Snow: an old grey pack brightens the ground less than a fresh one");
            Check(SnowModel.GroundAlbedo(AtmoConstants.SoilAlbedo01, 1f, 0f)
                  > SnowModel.GroundAlbedo(AtmoConstants.SoilAlbedo01, 0f, 0f),
                  "Snow: a snowfall raises the ground albedo above bare soil");

            // Grass: a dusting leaves the blades standing, a deep pack buries them,
            // and melting the pack back down restores them.
            CheckClose(SnowModel.GrassVisibility01(0f), 1f,
                       "Snow: bare ground shows all its grass");
            CheckClose(SnowModel.GrassVisibility01(AtmoConstants.SnowGrassBuryDepthM), 0f,
                       "Snow: a pack past the burial depth hides the grass");
            Check(SnowModel.GrassVisibility01(AtmoConstants.SnowGrassBuryDepthM * 0.5f) > 0f
                  && SnowModel.GrassVisibility01(AtmoConstants.SnowGrassBuryDepthM * 0.5f) < 1f,
                  "Snow: a shallow pack half-hides the grass");

            // Insulation: bare ground is fully coupled to the air; a deep pack pulls
            // the ground toward its own undisturbed temperature and holds it there.
            CheckClose(SnowModel.GroundInsulation01(0f), 0f,
                       "Snow: bare ground follows the air completely");
            Check(SnowModel.GroundInsulation01(0.3f) > SnowModel.GroundInsulation01(0.05f),
                  "Snow: a deeper pack decouples the ground more");
            Check(SnowModel.GroundInsulation01(1f) > 0.9f
                  && SnowModel.GroundInsulation01(1f) < 1f,
                  "Snow: a metre of pack nearly but never fully decouples the ground");
            CheckWithin(SnowModel.DecoupledGroundTempC(-15f, -1f, 0f), -15f, 1e-3f,
                        "Snow: bare ground sits at the air temperature");
            Check(SnowModel.DecoupledGroundTempC(-15f, -1f, 0.5f) > -15f,
                  "Snow: under a deep pack the frozen air no longer reaches the ground");
            Check(IsFinite(SnowModel.DecoupledGroundTempC(-20f, 0f, 0.4f)),
                  "Snow: the decoupled ground temperature stays finite");
        }

        /// <summary>
        /// Laying snow into a splat map and taking it back off. The two things that
        /// have to hold: every cell's weights still sum to one after snow is written,
        /// and a full accumulate-then-melt cycle returns the map bit-for-bit - the
        /// whole reason melt copies a cached base rather than inverting the blend.
        /// A world with fewer layers than the snow index is left untouched, not thrown.
        /// </summary>
        private static void TestSnowSplatMutation()
        {
            const int Layers = 5;
            const int SnowLayer = 4;
            const int Size = 4;
            const float SumTol = 1e-4f;

            // The layer guard: the snow index has to exist in the world's layer set.
            Check(!SnowSplatMutation.CanWriteLayer(3, SnowLayer),
                  "Splat: a three-layer world cannot hold snow at layer four");
            Check(SnowSplatMutation.CanWriteLayer(Layers, SnowLayer),
                  "Splat: a five-layer world can hold snow at layer four");
            Check(!SnowSplatMutation.CanWriteLayer(Layers, -1),
                  "Splat: a negative snow layer is never writable");

            // A hand-built base map with uneven ground weights that already sum to one.
            float[,,] baseAlpha = new float[Size, Size, Layers];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float a = 0.5f + 0.1f * ((x + y) % 3);
                float b = 0.3f;
                float c = 1f - a - b;
                baseAlpha[y, x, 0] = a;
                baseAlpha[y, x, 1] = b;
                baseAlpha[y, x, 2] = Mathf.Max(0f, c);
                baseAlpha[y, x, 3] = 0f;
                baseAlpha[y, x, 4] = 0f;
                // Renormalise the row so the base itself is a clean simplex.
                float s = 0f;
                for (int l = 0; l < Layers; l++) s += baseAlpha[y, x, l];
                for (int l = 0; l < Layers; l++) baseAlpha[y, x, l] /= s;
            }

            float[,,] alpha = (float[,,])baseAlpha.Clone();

            // Partial cover: snow takes its fraction, the ground keeps the rest, and the
            // per-cell weights still sum to one.
            float[,] cover = new float[Size, Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                cover[y, x] = 0.25f + 0.5f * ((x + 2 * y) % 3) / 2f;

            SnowSplatMutation.WriteSnow(alpha, baseAlpha, SnowLayer, cover);

            bool allSumOne = true;
            bool snowIsCover = true;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float s = 0f;
                for (int l = 0; l < Layers; l++) s += alpha[y, x, l];
                if (Mathf.Abs(s - 1f) > SumTol) allSumOne = false;
                if (Mathf.Abs(alpha[y, x, SnowLayer] - Mathf.Clamp01(cover[y, x])) > SumTol)
                    snowIsCover = false;
            }
            Check(allSumOne, "Splat: every cell's weights still sum to one after snow");
            Check(snowIsCover, "Splat: the snow layer carries exactly the cover fraction");

            // Full cover is pure snow: one on the snow layer, zero everywhere else.
            float[,] full = new float[Size, Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++) full[y, x] = 1f;
            float[,,] whiteout = (float[,,])baseAlpha.Clone();
            SnowSplatMutation.WriteSnow(whiteout, baseAlpha, SnowLayer, full);
            bool pureSnow = true;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (Mathf.Abs(whiteout[y, x, SnowLayer] - 1f) > SumTol) pureSnow = false;
                for (int l = 0; l < Layers; l++)
                    if (l != SnowLayer && Mathf.Abs(whiteout[y, x, l]) > SumTol) pureSnow = false;
            }
            Check(pureSnow, "Splat: full cover leaves nothing but snow in the cell");

            // The whole point: a full accumulate-then-restore cycle is bit-for-bit the
            // author's map, because restore copies the cached base, not an inverse.
            SnowSplatMutation.Restore(alpha, baseAlpha);
            bool bitIdentical = true;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            for (int l = 0; l < Layers; l++)
                if (alpha[y, x, l] != baseAlpha[y, x, l]) bitIdentical = false;
            Check(bitIdentical,
                  "Splat: melt restores the painted map bit-for-bit, not an approximation");

            // A too-thin world is left exactly as it was rather than throwing.
            float[,,] thin = new float[Size, Size, 3];
            float[,,] thinBase = new float[Size, Size, 3];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                thin[y, x, 0] = thinBase[y, x, 0] = 0.6f;
                thin[y, x, 1] = thinBase[y, x, 1] = 0.4f;
                thin[y, x, 2] = thinBase[y, x, 2] = 0f;
            }
            SnowSplatMutation.WriteSnow(thin, thinBase, SnowLayer, full);
            bool untouched = true;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            for (int l = 0; l < 3; l++)
                if (thin[y, x, l] != thinBase[y, x, l]) untouched = false;
            Check(untouched,
                  "Splat: a world with too few layers is left untouched, never thrown");

            // The single-cell blend agrees with the block writer on one cell.
            float[] cell = new float[Layers];
            float[] cellBase = new float[Layers] { 0.5f, 0.2f, 0.3f, 0f, 0f };
            SnowSplatMutation.BlendCell(cell, cellBase, SnowLayer, 0.4f);
            float cellSum = 0f;
            for (int l = 0; l < Layers; l++) cellSum += cell[l];
            CheckWithin(cellSum, 1f, SumTol, "Splat: a blended cell sums to one");
            CheckWithin(cell[SnowLayer], 0.4f, SumTol,
                        "Splat: the blended cell's snow weight is the cover");
        }

    }
}
