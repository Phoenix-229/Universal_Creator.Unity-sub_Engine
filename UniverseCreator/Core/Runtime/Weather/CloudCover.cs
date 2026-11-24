// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    CloudCover.cs
//  Purpose: How much of the sky is cloud, from relative humidity rather than a
//           noise octave. Sundqvist per deck, the decks combined as independent
//           screens, the column humidity set by the general circulation and
//           thickened by the passing systems.
//
//  WHY HUMIDITY AND NOT NOISE
//  --------------------------
//  Cloud is where the air is near saturation, so it has to follow the same
//  circulation that decides everything else: cloudy in the ascent of the ITCZ
//  and the storm track, clear in the subsidence of the subtropical ridge. A
//  noise field would put a desert under overcast and an ocean front under a
//  clear sky. This reads the humidity the pressure field already implies.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. The column temperature comes from ClimateNormals in this same
//  folder, never from the ecology layer, which reads this one.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Fractional cloud cover from relative humidity. Every method is a pure
    /// function of its arguments; nothing here holds state.
    /// </summary>
    public static class CloudCover
    {
        /// <summary>
        /// 0-1 cover of one deck from its relative humidity (Sundqvist). Nothing
        /// below the critical value, because a cell averages over air that is not
        /// uniformly humid; then it climbs as a square root toward full at
        /// saturation, which is steeper near the threshold than a straight line.
        /// </summary>
        public static float LayerCover01(float relativeHumidity01)
        {
            float rh = Mathf.Clamp01(relativeHumidity01);
            float crit = AtmoConstants.SundqvistCriticalRh;
            if (rh <= crit) return 0f;

            float saturationDeficit = (1f - rh) / (1f - crit);
            return Mathf.Clamp01(1f - Mathf.Sqrt(saturationDeficit));
        }

        /// <summary>
        /// 1 - (1-a)(1-b). Two decks combined as if each were an independent screen
        /// over the sky, which is the only combination that cannot exceed full cover:
        /// two half-covered decks read as three-quarters, never as one-and-a-half.
        /// </summary>
        public static float CombineTwo01(float a, float b)
        {
            return 1f - (1f - Mathf.Clamp01(a)) * (1f - Mathf.Clamp01(b));
        }

        /// <summary>
        /// 1 - (1-low)(1-mid)(1-high): the three decks stacked as independent screens.
        /// The one place three layer covers become the single sky-occlusion fraction the
        /// sun is dimmed by, so the light a sheet blocks cannot disagree with the sheet a
        /// renderer draws. Order-independent and never exceeds full cover - thin cirrus
        /// over a clear low sky reads as exactly the cirrus, never the sum.
        /// </summary>
        public static float CombinedCover01(float low, float mid, float high)
        {
            return CombineTwo01(CombineTwo01(low, mid), high);
        }

        /// <summary>
        /// 0-1 how strongly the mean circulation is rising here: 1 in the troughs the
        /// ITCZ and the storm track sit in, 0 on the subtropical ridge. Read straight
        /// off the mean pressure offset the field already owns, so the cloudy
        /// latitudes and the low-pressure latitudes cannot drift apart.
        /// </summary>
        public static float AscentFraction01(float latitudeDeg, float itczLatitudeDeg)
        {
            float offset =
                WeatherCellField.MeanSeaLevelPressureHpa(latitudeDeg, itczLatitudeDeg)
                - AtmoConstants.SeaLevelPressureHpa;

            return 1f - Mathf.SmoothStep(AtmoConstants.ItczPressureOffsetHpa,
                                         AtmoConstants.SubtropicalHighPressureOffsetHpa,
                                         offset);
        }

        /// <summary>
        /// 0-1 relative humidity of the column at the surface: the ascent regime sets
        /// the base, and the synoptic anomaly shifts it - a low deepening overhead
        /// raises it so cover thickens as the front approaches, a ridge lowers it.
        /// </summary>
        public static float ColumnHumidity01(WeatherCellField field,
            float worldX, float worldZ, float latitudeDeg,
            float itczLatitudeDeg, float timeDays)
        {
            float wetness = AscentFraction01(latitudeDeg, itczLatitudeDeg);
            float baseRh = Mathf.Lerp(AtmoConstants.CloudDryColumnHumidity01,
                                      AtmoConstants.CloudWetColumnHumidity01, wetness);

            float anomaly = field != null
                ? field.PressureAnomalyHpa(worldX, worldZ, latitudeDeg, timeDays)
                : 0f;

            return Mathf.Clamp01(
                baseRh - anomaly * AtmoConstants.CloudAnomalyHumidityPerHpa);
        }

        /// <summary>
        /// m. Scale height of water vapour in this column: deep where the air is
        /// rising, shallow where it is sinking. This is what keeps the high deck off
        /// a desert - subsidence caps the moisture near the ground, so the very cold
        /// air aloft has nothing to saturate however cold it gets.
        /// </summary>
        private static float MoistureScaleHeightM(float latitudeDeg,
                                                  float itczLatitudeDeg)
        {
            float wetness = AscentFraction01(latitudeDeg, itczLatitudeDeg);

            return Mathf.Lerp(AtmoConstants.CloudMoistureScaleHeightDryM,
                              AtmoConstants.CloudMoistureScaleHeightWetM, wetness);
        }

        /// <summary>
        /// 0-1 relative humidity at one deck's altitude. The surface vapour thins with
        /// height by the column's scale height, while the air cools by the lapse rate
        /// and so holds less - which is why a rising column can be dry at the ground
        /// and saturated where its cloud actually forms.
        /// </summary>
        public static float LayerHumidity01(float surfaceHumidity01, float surfaceTempC,
                                            float layerAltitudeM,
                                            float moistureScaleHeightM)
        {
            float surfaceVapourKPa =
                AtmoMath.VapourPressureKPa(surfaceTempC, surfaceHumidity01);
            float layerVapourKPa = surfaceVapourKPa
                * Mathf.Exp(-layerAltitudeM / moistureScaleHeightM);

            float layerTempC = AtmoMath.LapseAdjustedTempC(surfaceTempC, layerAltitudeM);
            float saturationKPa = AtmoMath.SaturationVapourPressureKPa(layerTempC);
            if (saturationKPa <= 0f) return 0f;

            return Mathf.Clamp01(layerVapourKPa / saturationKPa);
        }

        /// <summary>
        /// The three deck covers and their combined stratiform total. What § 8.7
        /// receives, so a shader can shade the low overcast apart from the high cirrus
        /// rather than reading one grey scalar for the whole sky.
        /// </summary>
        public struct CloudDecks
        {
            public float Low;
            public float Mid;
            public float High;

            /// <summary>The three decks as independent screens - never their sum.</summary>
            public float Stratiform01
            {
                get { return CombinedCover01(Low, Mid, High); }
            }
        }

        /// <summary>The dominant cloud form: layered sheets, convective heaps, both, or a clear sky.</summary>
        public enum CloudType { None, Stratiform, Cumuliform, Mixed }

        /// <summary>
        /// The cloud form from the two mechanisms present: stratiform sheet cover from
        /// layered ascent and cumuliform cover from convection. Both present reads Mixed,
        /// neither reads None. What a sky renderer draws instead of one flat overcast.
        /// </summary>
        public static CloudType ClassifyType(float stratiform01, float convectiveCover01)
        {
            bool hasSheet = Mathf.Clamp01(stratiform01) > 0f;
            bool hasHeap = Mathf.Clamp01(convectiveCover01) > 0f;
            if (hasSheet && hasHeap) return CloudType.Mixed;
            if (hasHeap) return CloudType.Cumuliform;
            if (hasSheet) return CloudType.Stratiform;
            return CloudType.None;
        }

        /// <summary>
        /// Resolves the low, mid and high deck covers at a point: the column humidity
        /// evaluated at each deck's own altitude and run through Sundqvist. This is the
        /// per-layer answer; <see cref="TotalCover01"/> is the one number on top of it.
        /// </summary>
        public static CloudDecks ResolveDecks(WeatherCellField field,
            float worldX, float worldZ, float latitudeDeg,
            float itczLatitudeDeg, float timeDays)
        {
            CloudDecks decks = default;
            if (field == null) return decks;

            float columnRh = ColumnHumidity01(field, worldX, worldZ, latitudeDeg,
                                              itczLatitudeDeg, timeDays);
            float surfaceTempC = ClimateNormals.ZonalMeanSeaLevelTempC(latitudeDeg);
            float scaleHeight = MoistureScaleHeightM(latitudeDeg, itczLatitudeDeg);

            decks.Low = LayerCover01(LayerHumidity01(columnRh, surfaceTempC,
                AtmoConstants.CloudLowLayerAltitudeM, scaleHeight));
            decks.Mid = LayerCover01(LayerHumidity01(columnRh, surfaceTempC,
                AtmoConstants.CloudMidLayerAltitudeM, scaleHeight));
            decks.High = LayerCover01(LayerHumidity01(columnRh, surfaceTempC,
                AtmoConstants.CloudHighLayerAltitudeM, scaleHeight));

            return decks;
        }

        /// <summary>
        /// 0-1 total sky cover: the low, mid and high decks combined as independent
        /// screens along with the convective cover the stability test contributes. The
        /// convective term defaults to none, so a caller without a stability result
        /// still gets the stratiform sky rather than nothing.
        /// </summary>
        public static float TotalCover01(WeatherCellField field,
            float worldX, float worldZ, float latitudeDeg,
            float itczLatitudeDeg, float timeDays, float convectiveCover01 = 0f)
        {
            if (field == null) return Mathf.Clamp01(convectiveCover01);

            float stratiform = ResolveDecks(field, worldX, worldZ, latitudeDeg,
                                            itczLatitudeDeg, timeDays).Stratiform01;

            return CombineTwo01(stratiform, convectiveCover01);
        }

        /// <summary>
        /// m above ground. Convective cloud base: the lifting condensation level of the
        /// surface parcel, so a humid morning clouds over low and a dry afternoon high.
        /// This is the altitude § 8.7 draws the cumulus sheet at, not a constant.
        /// </summary>
        public static float ConvectiveCloudBaseM(WeatherCellField field,
            float worldX, float worldZ, float latitudeDeg,
            float itczLatitudeDeg, float timeDays)
        {
            float surfaceRh = ColumnHumidity01(field, worldX, worldZ, latitudeDeg,
                                               itczLatitudeDeg, timeDays);
            float surfaceTempC = ClimateNormals.ZonalMeanSeaLevelTempC(latitudeDeg);
            float surfaceDewC = AtmoMath.DewPointC(surfaceTempC, surfaceRh);

            return AtmoMath.LiftingCondensationLevelM(surfaceTempC, surfaceDewC);
        }

        /// <summary>
        /// m above ground. Cloud base of one layered deck: its own parcel's LCL added to
        /// the deck altitude, so a near-saturated layer sits right at its altitude and a
        /// dry one lifts above it before clouding over.
        /// </summary>
        private static float LayeredCloudBaseM(float columnRh, float surfaceTempC,
            float layerAltitudeM, float moistureScaleHeightM)
        {
            float layerRh = LayerHumidity01(columnRh, surfaceTempC,
                                            layerAltitudeM, moistureScaleHeightM);
            float layerTempC = AtmoMath.LapseAdjustedTempC(surfaceTempC, layerAltitudeM);
            float layerDewC = AtmoMath.DewPointC(layerTempC, layerRh);

            return layerAltitudeM
                   + AtmoMath.LiftingCondensationLevelM(layerTempC, layerDewC);
        }
        /// <summary>The convective base and the three layered deck bases, all m above
        /// ground - what § 8.7 draws each sheet at.</summary>
        public struct CloudBasesM
        {
            public float Convective;
            public float Low;
            public float Mid;
            public float High;
        }

        /// <summary>
        /// Resolves every cloud base at a point: the surface parcel for the convective
        /// sheet, each deck's own parcel for the layered sheets.
        /// </summary>
        public static CloudBasesM ResolveBasesM(WeatherCellField field,
            float worldX, float worldZ, float latitudeDeg,
            float itczLatitudeDeg, float timeDays)
        {
            CloudBasesM bases = default;
            if (field == null) return bases;

            float columnRh = ColumnHumidity01(field, worldX, worldZ, latitudeDeg,
                                              itczLatitudeDeg, timeDays);
            float surfaceTempC = ClimateNormals.ZonalMeanSeaLevelTempC(latitudeDeg);
            float scaleHeight = MoistureScaleHeightM(latitudeDeg, itczLatitudeDeg);

            bases.Convective = ConvectiveCloudBaseM(field, worldX, worldZ, latitudeDeg,
                                                    itczLatitudeDeg, timeDays);
            bases.Low = LayeredCloudBaseM(columnRh, surfaceTempC,
                AtmoConstants.CloudLowLayerAltitudeM, scaleHeight);
            bases.Mid = LayeredCloudBaseM(columnRh, surfaceTempC,
                AtmoConstants.CloudMidLayerAltitudeM, scaleHeight);
            bases.High = LayeredCloudBaseM(columnRh, surfaceTempC,
                AtmoConstants.CloudHighLayerAltitudeM, scaleHeight);

            return bases;
        }
    }
}
