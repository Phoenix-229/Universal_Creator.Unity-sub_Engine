// -----------------------------------------------------------------------------
//  Universe Creator - what season, weather and snow are doing at a world point.
//
//  This is the season counterpart to UC_Wind.hlsl, and it exists for the same
//  reason. Season, wetness and lying snow were resolved on the CPU and reached
//  the screen only as a per-instance tint, so a material could not darken when
//  it rained, could not hold snow on its upper faces, and could not go bare in
//  winter. Anything that wanted those had to restate the maths, and restated
//  maths drifts.
//
//  THE CPU OWNS EVERY CONVENTION. THIS FILE ONLY CONSUMES.
//  Season phase, the four season weights, the snow line and surface wetness are
//  all resolved by UCVegetationSystemManager and UCWeatherSystemManager and
//  published as globals. Nothing here re-derives a date from a phase or decides
//  which end of the year phase 0 is. That was deliberate: the weights arrive
//  pre-resolved precisely so the GPU cannot disagree with the CPU tint that
//  SeasonalAppearance() produces for the same instance.
//
//  Every uniform below is a GLOBAL, never per-material. A scene with no
//  vegetation manager reads zeroes, which resolve to "high summer, dry, no
//  snow" - the correct still image rather than a broken one.
//
//  WHAT IS ACTUALLY PHYSICAL HERE, and why it is not a set of art dials:
//
//    - Snow lies where it can stay. Cover comes from the climatic snow line as
//      a 200 m band, then is gated by surface orientation against snow's angle
//      of repose. That is why a real branch carries a white upper edge and bare
//      bark underneath, and why a vertical trunk stays dark in a blizzard.
//
//    - Wet surfaces darken because the water film fills the surface microcavities.
//      Light that would have scattered straight back out is instead internally
//      reflected until it is absorbed, so the effect is an exponent on albedo and
//      not a multiply: a saturated colour darkens far more than a pale one, which
//      is what wet sand and wet bark actually do.
//
//    - Water is optically smooth and its Fresnel reflectance is about 0.02-0.04,
//      so a wet surface also gains a sheen. Darker AND shinier at once is the
//      combination that reads as wet; either one alone reads as a different
//      material.
//
//    - Spring climbs a mountain and autumn does not. Budburst is driven by
//      accumulated warmth, which arrives later the higher and colder the site,
//      so it carries an altitude term. Senescence is triggered by day length,
//      and day length does not change with altitude, so it carries none. That
//      asymmetry is real, it is why an autumn treeline turns all at once while
//      its spring creeps up over weeks, and it is the reason there are two
//      numbers below instead of one.
// -----------------------------------------------------------------------------

#ifndef UC_SEASON_INCLUDED
#define UC_SEASON_INCLUDED

// -----------------------------------------------------------------------------
//  Globals. Published by UCVegetationSystemManager.PublishShaderGlobals().
// -----------------------------------------------------------------------------

// x = interpolated season phase 0..1, for anything that wants to drive its own cycle
// y = dormancy at sea level 0..1 (0 = full leaf, 1 = fully bare)
// z = extra dormancy per kilometre of altitude. Non-zero only while budburst is
//     the active transition; the CPU zeroes it through autumn so photoperiod
//     senescence does not acquire an altitude gradient it does not have.
// w = 1 when seasonal phenology is enabled, 0 when it is off
float4 _UCSeasonPhase;

// Pre-resolved weights for spring, summer, autumn, winter. Sums to 1.
// Resolved on the CPU by the same code that tints instances, so the two cannot
// disagree about where in the year the world is.
float4 _UCSeasonWeights;

// x = climatic snow line, metres
// y = transition band over which patchy cover becomes continuous, metres
// z = snow intensity 0..1, how hard it is falling
// w = cosine of snow's holding angle. Above this a face is too steep to hold.
float4 _UCSnowParams;

// x = surface wetness 0..1
// y = rain intensity 0..1
// z = humidity 0..1
// w = 1 when the weather manager is present, 0 when it is not
float4 _UCWetParams;
float4 _UCSurfaceSnow; // UC-T08-09: sampled cover and opt-in live flag, restored per camera.

// -----------------------------------------------------------------------------
//  Season
// -----------------------------------------------------------------------------

/// 1 when seasonal phenology is live. Used to skip the whole chain rather than
/// to fade it, because "no season manager" must cost nothing on a phone.
float UCSeasonEnabled()
{
    return _UCSeasonPhase.w;
}

/// Where in the year the world is, 0..1. Provided for materials that want to
/// drive something of their own; the tint path should use the weights instead.
float UCSeasonPhase()
{
    return _UCSeasonPhase.x;
}

/// The four season weights, already resolved and normalised by the CPU.
/// Reads (0,0,0,0) with no manager, which UCSeasonTint treats as full summer.
float4 UCSeasonWeights()
{
    return _UCSeasonWeights;
}

/// Blend a species' four authored seasonal colours by the world's current
/// position in the year.
///
/// The four colours stay per-material because they are a property of the plant,
/// not of the world: a larch and an oak share a September and do not share a
/// September colour. Only the weighting is global.
half3 UCSeasonTint(half3 spring, half3 summer, half3 autumn, half3 winter)
{
    float4 w = _UCSeasonWeights;

    // No manager, or a manager that has not resolved yet, means the weights are
    // zero. Falling back to summer keeps an unmanaged scene looking like a plant
    // rather than looking black.
    float total = w.x + w.y + w.z + w.w;
    if (total < 1e-4)
        return summer;

    return (spring * w.x + summer * w.y + autumn * w.z + winter * w.w) / total;
}

/// How dormant a plant at this world position is, 0 = full leaf, 1 = fully bare.
///
/// The altitude term is what makes spring arrive at a valley floor before it
/// reaches the same species 800 m above it. Nobody authors that gradient - it
/// falls out of the lapse rate, and the CPU supplies it already scaled.
float UCDormancyAt(float3 positionWS)
{
    if (_UCSeasonPhase.w < 0.5)
        return 0.0;

    float altitudeKm = positionWS.y * 0.001;
    return saturate(_UCSeasonPhase.y + altitudeKm * _UCSeasonPhase.z);
}

/// The fraction of a species' foliage still present, 1 = full canopy.
///
/// leafOffFraction is the envelope's authored "how bare it goes", and deciduous
/// is 1 for a species that drops its leaves and 0 for one that does not. An
/// evergreen returns 1 always: its winter is expressed as snow in the crown,
/// which is what UCSnowAt is for.
float UCLeafOnFraction(float3 positionWS, half leafOffFraction, half deciduous)
{
    if (deciduous < 0.5)
        return 1.0;

    float dormancy = UCDormancyAt(positionWS);
    return saturate(1.0 - dormancy * leafOffFraction);
}

// -----------------------------------------------------------------------------
//  Pigment
//
//  WHY A MULTIPLY WAS NOT ENOUGH, AND WHAT REPLACED IT
//
//  Autumn colour is a change of pigment, not a filter laid over a green leaf.
//  Chlorophyll is broken down and resorbed; the carotenoids and xanthophylls
//  that were present all summer stop being masked by it; some species then
//  synthesise anthocyanins outright. The leaf's reflectance spectrum is
//  REPLACED. Nothing is attenuating what was there before.
//
//  A multiply cannot express that, and the failure is not subtle. A green
//  albedo has almost no red in it, so multiplying it by an orange tint leaves
//  the red channel near zero and merely darkens the green - the classic
//  "dirty green autumn" that no amount of tuning the tint fixes, because the
//  operation itself is the wrong one.
//
//  Rotating hue toward the target and taking the target's saturation gets the
//  pigment change. Keeping the texture's own value is what preserves every
//  vein, blotch and baked shadow in the map, which a flat colour replace would
//  iron out.
//
//  THE WHITE-MEANS-UNCHANGED CONVENTION IS PRESERVED. The four authored season
//  colours are written as tints - summer is (1,1,1). So saturation, not the
//  strength dial alone, is what drives the repigmentation: a neutral tint is a
//  brightness and not a pigment, and passing white through here returns the
//  albedo untouched, exactly as the old multiply did.
// -----------------------------------------------------------------------------

/// RGB to HSV, branchless. Hue and saturation are undefined for pure black and
/// the epsilon below is what keeps them finite rather than NaN.
half3 UCRgbToHsv(half3 c)
{
    const half e = 1.0e-4h;

    half4 k = half4(0.0h, -1.0h / 3.0h, 2.0h / 3.0h, -1.0h);
    half4 p = lerp(half4(c.bg, k.wz), half4(c.gb, k.xy), step(c.b, c.g));
    half4 q = lerp(half4(p.xyw, c.r), half4(c.r, p.yzx), step(p.x, c.r));

    half d = q.x - min(q.w, q.y);

    return half3(abs(q.z + (q.w - q.y) / (6.0h * d + e)), d / (q.x + e), q.x);
}

/// HSV back to RGB.
half3 UCHsvToRgb(half3 c)
{
    half4 k = half4(1.0h, 2.0h / 3.0h, 1.0h / 3.0h, 3.0h);
    half3 p = abs(frac(c.xxx + k.xyz) * 6.0h - k.www);
    return c.z * lerp(k.xxx, saturate(p - k.xxx), c.y);
}

/// Repigment a foliage albedo toward a season tint.
///
/// Costs two HSV conversions, so it sits behind _SEASONTINT_ON in both shaders
/// that call it - a terrain detail material with the tint off pays nothing,
/// which is the case that has to stay cheap on a mid-range phone.
half3 UCRepigment(half3 albedo, half3 tint, half amount)
{
    if (amount <= 0.0h)
        return albedo;

    half3 t = UCRgbToHsv(tint);

    // Saturation is the strength of the pigment change. White has none, so it
    // rotates nothing and falls through to the brightness multiply below.
    half k = saturate(amount * t.y);

    if (k > 0.0h)
    {
        half3 a = UCRgbToHsv(albedo);

        // Hue is an angle: 0.95 and 0.02 are neighbours. Interpolated linearly
        // they walk the long way round, so a red leaf turning yellow would pass
        // through blue on the way. Take the short arc.
        half dh = t.x - a.x;
        dh -= round(dh);

        a.x = frac(a.x + dh * k);
        a.y = lerp(a.y, t.y, k);

        albedo = UCHsvToRgb(a);
    }

    // The tint's value still multiplies, as it always did, so an authored
    // darker autumn stays darker.
    return albedo * lerp(1.0h, t.z, amount);
}

/// How cured this point on a blade is, 0 = green, 1 = straw.
///
/// CURING RUNS FROM THE TIP DOWN, and three independent things say so. A grass
/// leaf grows from a basal meristem, so the tip is the OLDEST tissue on the
/// blade and senesces first on age alone. The tip is also the most distal point
/// on the transpiration path, so it reaches the turgor-loss point first when the
/// soil dries. And it sits in the thinnest boundary layer, so it loses the most
/// to advective drying. Age, water potential and exposure all point the same
/// way, which is why a curing sward is straw at the tips with green still in the
/// crown - and why a uniform tint over the whole blade never looked like one.
///
/// demand is how far the cure has progressed overall; heightFrac is 0 at the
/// root and 1 at the tip; gradient is how tip-weighted it is, 0 reproducing the
/// old uniform behaviour exactly so an authored material does not shift.
half UCCureAlongBlade(half demand, half heightFrac, half gradient)
{
    if (gradient <= 0.0h || demand <= 0.0h)
        return demand;

    // Width of the senescence front. Cells do not die in a straight line, so
    // the boundary is a fade over part of the blade rather than an edge.
    const half w = 0.25h;

    // The front travels from just past the tip at demand 0 to just past the
    // root at demand 1, so the ends resolve to no cure and full cure exactly.
    half front = (1.0h + w) - demand * (1.0h + 2.0h * w);

    half advancing = smoothstep(front, front + w, heightFrac);

    return lerp(demand, advancing, gradient);
}

// -----------------------------------------------------------------------------
//  Snow
// -----------------------------------------------------------------------------

/// Lying snow at an altitude, 0..1, before surface orientation is considered.
///
/// This is deliberately the same arithmetic as
/// UCVegetationSystemManager.SnowCoverFractionAt: a 200 m band centred on the
/// climatic snow line, gated by supply, with a floor above the line so a settled
/// pack does not evaporate the instant a storm stops. Kept identical on purpose -
/// if the GPU used a prettier curve the ground tint and the canopy snow would
/// disagree at exactly the altitude a player is most likely to be standing.
float UCSnowCoverAtAltitude(float altitudeMetres)
{
    if (_UCSurfaceSnow.y > 0.5) return saturate(_UCSurfaceSnow.x);
    float band = max(1.0, _UCSnowParams.y);

    float above = altitudeMetres - _UCSnowParams.x;
    float cover = saturate(0.5 + above / band);

    float floorSupply = (cover >= 1.0) ? 0.6 : 0.0;
    float supply = saturate(max(_UCSnowParams.z, floorSupply));

    return saturate(cover * supply);
}

/// How much snow this particular surface holds, 0..1.
///
/// Orientation is the whole difference between snow and a white tint. Snow rests
/// on a face only while that face is shallower than snow's angle of repose;
/// past it the pack shears off. Gating on the surface normal is what puts a
/// white edge along the top of a branch and leaves the underside bare.
///
/// canopyAccumulation is the species' authored ability to hold snow in its
/// crown - a spruce holds a great deal, a bare-twigged birch almost none.
float UCSnowAt(float3 positionWS, float3 normalWS, half canopyAccumulation)
{
    float cover = UCSnowCoverAtAltitude(positionWS.y);
    if (cover <= 0.0)
        return 0.0;

    // Repose gate. Fully held when facing straight up, gone by the holding angle.
    float upness = normalWS.y;
    float reposeCos = _UCSnowParams.w;
    float hold = saturate((upness - reposeCos) / max(1e-3, 1.0 - reposeCos));

    return saturate(cover * hold * canopyAccumulation);
}

/// Apply lying snow to a surface.
///
/// Fresh snow is a bright, almost entirely diffuse dielectric: albedo around
/// 0.85 and low smoothness, because a fresh pack is a mass of loose crystals
/// rather than a surface. It is only once it has thawed and refrozen that snow
/// becomes glossy, so wetness is what raises the smoothness here rather than
/// the snow depth doing it.
void UCApplySnow(inout half3 albedo, inout half smoothness, inout half metallic,
                 half snow, half wetness)
{
    if (snow <= 0.0)
        return;

    const half3 freshSnowAlbedo = half3(0.85, 0.87, 0.90); // faintly blue, as it reads in daylight
    const half  freshSnowSmooth = 0.18;
    const half  meltedSnowSmooth = 0.62;

    half snowSmooth = lerp(freshSnowSmooth, meltedSnowSmooth, saturate(wetness));

    albedo    = lerp(albedo, freshSnowAlbedo, snow);
    smoothness = lerp(smoothness, snowSmooth, snow);
    metallic  = lerp(metallic, 0.0, snow); // snow is never metallic
}

// -----------------------------------------------------------------------------
//  Wetness
// -----------------------------------------------------------------------------

/// How wet this surface is, 0..1.
///
/// Rain falls downward, so an up-facing surface wets fully while a vertical one
/// only ever carries what runs down it. The floor keeps a vertical trunk from
/// staying bone dry in a downpour, because in a real one it does not - it is
/// just never as dark as the ground beside it.
float UCWetnessAt(float3 normalWS)
{
    float wet = _UCWetParams.x;
    if (wet <= 0.0)
        return 0.0;

    float upness = saturate(normalWS.y);
    float exposure = lerp(0.45, 1.0, upness);

    return saturate(wet * exposure);
}

/// Apply wetness to a surface.
///
/// Two things happen at once and both are required for the result to read as
/// wet rather than as a different material:
///
///   1. The albedo darkens by an exponent, not a multiply. A water film fills
///      the surface microcavities, so light that would have scattered back out
///      is internally reflected until it is absorbed. Raising albedo to a power
///      above 1 darkens saturated colours much harder than pale ones, which is
///      exactly the asymmetry wet ground shows.
///
///   2. Smoothness rises toward water's, because the film is optically smooth,
///      and reflectance heads for water's 0.02-0.04 Fresnel. This is the sheen.
///
/// wetDarkening is the species' authored figure for how much darker it reads
/// wet - bark soaks and darkens hard, a waxy leaf barely changes.
void UCApplyWetness(inout half3 albedo, inout half smoothness, half wetness, half wetDarkening)
{
    if (wetness <= 0.0)
        return;

    half w = saturate(wetness * wetDarkening);

    // Porosity darkening. 0.85 at full wet gives roughly the doubling of optical
    // depth a saturated porous surface shows without crushing pale materials.
    albedo = pow(max(albedo, 1e-4), 1.0 + w * 0.85);

    // Optically smooth film. Never quite 1: a real film has ripples and dust.
    smoothness = lerp(smoothness, 0.92, saturate(wetness) * 0.75);
}

#endif // UC_SEASON_INCLUDED
