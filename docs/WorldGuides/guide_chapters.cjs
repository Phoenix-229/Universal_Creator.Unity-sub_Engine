// UC-T08-15.11: one visible-UI procedure per world, with no guide-only engine code.
'use strict';
const fs = require('node:fs');
const path = require('node:path');

/**
 * Writes only the setup, application and save steps needed by the selected world.
 * @param {object} context Recipe, inspected schema and print helpers; root is the workspace path.
 * @returns {object} Opening, per-graph before/after and closing chapters; missing UI declarations throw.
 */
function chapters(context) {
  const {world, schema, catalogue, page, p, ol, table, escape:e, root, uiChecks} = context;
  const a=world.alpine, height=a?1800:900, sea=a?0.28:0.35;
  const name=a?'RiftHighlands':'GreenCoast';
  const project='Assets/'+name, graphFolder=project+'/Graphs';
  const inspector='Assets/UniverseCreator/Core/Editor/Inspectors/';
  const files=new Map(), before={}, after={};
  const source=relative=> {
    if(!files.has(relative)) files.set(relative,fs.readFileSync(path.join(root,relative),'utf8'));
    return files.get(relative);
  };
  const note=text=>'<div class="note">'+text+'</div>';
  const heading=(number,title,body)=>page(number+'. '+title,body);
  function ui(relative,rows,prefix='') {
    const text=source(relative);
    return table(['Inspector foldout → subgroup','Visible field','Enter / select','Meaning / units'],rows.map(([location,label,value,why,token])=> {
      if(!text.includes(token||'"'+label+'"')) throw new Error('Missing visible UI declaration: '+relative+' / '+label);
      if(!text.includes('"'+location.split(' → ')[0]+'"')) throw new Error('Missing Inspector group: '+location);
      uiChecks.push({source:relative,location:prefix+location,label,value});
      return [e(prefix+location),e(label),e(value),e(why)];
    }));
  }
  function fields(type,relative,rows) {
    const manager=schema.managers.find(m=>m.type.endsWith('.'+type));
    return ui(relative,rows.map(([location,key,value,why])=> {
      const field=manager?.fields.find(f=>f.name===key);
      if(!field) throw new Error('Missing inspected field: '+type+'.'+key);
      return [location,field.label,value,why,'"'+key+'"'];
    }));
  }
  const worldUI=inspector+'UniverseCreatorInspector.cs';
  const lightUI=inspector+'UCSystemManagerInspectors.cs';
  const weatherUI=inspector+'UCWeatherSystemManagerInspector.cs';
  const bodyUI=inspector+'UCWaterBodyInspector.cs';
  const waterUI=inspector+'UCWaterSystemManagerInspector.cs';
  const chunkUI=inspector+'ChunkManagerInspector.cs';

  let start=page(world.version+' · '+name,
    '<div class="eyebrow">Universe Creator · specific world build</div>'+p(e(world.objective))+
    p('Unity 6000.5.2f1 · URP 17.5.0 · revised 23 November 2026')+
    p(a?'Build an ocean-ringed continent with tectonic highlands, glacial valleys, inland lakes, tributaries, lower coastal plains, rocky headlands, sheltered beaches, cool forests and high-altitude snow.':
      'Build an ocean-ringed continent with eroded inland uplands, connected tributaries, floodplain lakes, sandy bays, low coastal plains, rocky headlands, seasonal forest and open grassland.')+
    p('Start with one empty scene. Follow the four complete graphs and Inspector tables in order. The result is a natural-world scene ready for your game’s roads, buildings and gameplay systems.'), 'cover');
  start+=heading('1','Follow this build order',ol([
    'Create '+name+' and set its parent Inspector and Chunk Manager. Save the scene before generating.',
    'Build one Lighting Graph and accept its baseline climate. Configure the shared game clock, sun and weather.',
    'Build the main Node Graph. Generate terrain, water masks, branching trenches, biomes and terrain surface weights.',
    'Assign the eight terrain layers. Build and apply the Texture Graph to this world’s terrain.',
    'Assign tree, shrub, grass and rock prototypes on VegetationSystem. Build and apply its Vegetation Graph.',
    'Check terrain, shores, confluences, materials and plant placement. Save, reopen and compare before adding artificial features.'
  ])+p('The terrain graph determines heights and biome coverage. Textures dress that accepted coverage; the vegetation graph reads the same accepted height and biome fields. Texture application comes first for an inspectable ground surface. Plants do not need to be scattered to calculate those texture weights.')+
    note('T01, L01, X01 and V01 identify the actual nodes in the connection and settings tables. Keep each node’s Unity title. Each full flowchart draws every node once; two boxes with the same title and different references are two nodes of that type. Zoom the larger map pages to read the exact port labels.'));
  start+=heading('2','Create and save the world',ol([
    'Use File > New Scene, then File > Save As. In the Project window create <b>'+e(project)+'/Scenes</b> and <b>'+e(graphFolder)+'</b> with Create > Folder. Save the scene as '+name+'.unity.',
    'Choose <b>GameObject > 3D Object > Universe Creator > Create New World (Empty Graph)</b>. Rename the created world root '+name+'. Leave Position and Rotation at zero and Scale at one.',
    'Select '+name+' in the Hierarchy. In its Universe Creator Object Inspector expand <b>Generation</b> and turn <b>Auto Regenerate</b> off while wiring.',
    'Expand the root’s <b>Graph</b> foldout. Use its assigned empty main graph; save/rename that graph as '+name+'_Terrain in '+e(graphFolder)+'. Open <b>Window > Universe Creator > Node Graph Editor</b>.',
    'Keep the created ChunkManager, WaterSystem, WeatherSystem, LightingSystem and VegetationSystem children. Each Inspector below is reached by selecting its named child.',
    'In each companion graph window select this world in <b>World</b>, choose <b>New Empty</b>, then <b>Save</b> into '+e(graphFolder)+'. Name the assets '+name+'_Lighting, '+name+'_Texture and '+name+'_Vegetation. For Texture Graph select an owned terrain in its <b>Terrain</b> target when terrain exists.'
  ])+p('The root graph is the terrain/water graph. LightingSystem and VegetationSystem expose their own shared graph bindings. The root’s UC World Graph Bindings component also exposes those same references and the Texture Graph slot.'));
  start+=heading('3','Enter the parent world dimensions',p('Hierarchy → '+name+' → Universe Creator Object. Expand the listed foldout; enter each value before generation.')+
    ui(worldUI,[
      ['World Settings','World Preset','None','Prevents a preset from overriding this recipe.'],
      ['World Settings','Seed',a?'64117':'55117','Different master seed for the two worlds.'],
      ['World Settings','World Size (km)','6','A 6000 × 6000 m finite world.'],
      ['World Settings','Max Height (m)',String(height),'Normalized height 1 maps to this many metres.'],
      ['World Settings','Resolution','513','Samples per 1000 m chunk; spacing is about 1.953 m.'],
      ['World Settings','World Offset','X 0, Y 0','Sampling offset, not the root transform.'],
      ['World Settings','Origin Clamp (m)','100000','Precision warning distance; keep this root at the origin.']
    ])+p('This recipe’s sea elevation is '+sea+' × '+height+' = '+(sea*height)+' m. Use the same normalized sea value in WaterSystem and every specified sea-reading node.'));
  start+=heading('4','Set chunks and the terrain target',ui(worldUI,[
    ['Chunk Settings (25+1 Virtual Grid)','Chunk Size (m)','1000','Produces six chunks per side for this 6 km world.'],
    ['Chunk Settings (25+1 Virtual Grid)','Stream At Runtime','Off','Start with the complete finite world visible; enable streaming after scene checks.'],
    ['Chunk Settings (25+1 Virtual Grid)','Resident Window','5','Used when runtime streaming is enabled.'],
    ['Chunk Settings (25+1 Virtual Grid)','High-LOD Ring','1','Centre and adjacent chunks use the high tier.'],
    ['Chunk Settings (25+1 Virtual Grid)','Buffer Ring Width','1','Keeps an extra approach band.'],
    ['Chunk Settings (25+1 Virtual Grid)','Recentre Threshold','0.55','Fraction of chunk width before the grid recentres.'],
    ['Terrain Rendering','Acuity Budget (px)','1','Terrain silhouette error budget in pixels.'],
    ['Terrain Rendering','Pixel Error Floor','1','Lower bound for native terrain LOD error.'],
    ['Terrain Rendering','Density Hysteresis','0.5','Avoids repeated LOD updates for a stationary view.'],
    ['Terrain Rendering','Draw Instanced','On','Use the supported instanced terrain path.'],
    ['Terrain Rendering','Generate Collider','On','Required for the game’s grounded player and terrain raycasts.']
  ])+p('Chunks Per Side and Basemap Distance are readouts. Leave <b>Terrain Rendering → Material</b> empty for the first shape build; the Texture Graph will create its UC terrain material. Set <b>Cast Shadows</b> on for the desktop inspection scene.')+
    p('Resident Window 5 means a 5 × 5 interior: 25 cells. Buffer Ring Width 1 adds one surrounding ring, giving a 7 × 7 buffered window before the finite world boundary limits it. It does not mean one extra chunk. This recipe has six cells per side, so the complete world fits that buffered window. With Stream At Runtime off, keep the complete world available for inspection.')+
    p('Near and far terrain keep a common height sample grid. LOD adjusts drawing density from the assigned focus camera’s lens and render target; changing LOD does not regenerate saved heights, sculpting or river beds. With Generate Collider on, High and Low terrain have collision by default; Buffer does not.'));
  start+=heading('5','Set build budgets and save behaviour',ui(worldUI,[
    ['Performance','GPU Instancing','On','Enables eligible scatter rendering.'],
    ['Performance','Draw Distance (m)','3000','Initial natural-world draw distance.'],
    ['Performance','LOD Bias','1','Neutral content LOD bias.'],
    ['Performance','Max Concurrent Chunks','2','Bounds simultaneous generation work.'],
    ['Performance','Preview Quality','1','Full authored preview detail.'],
    ['Performance','Chunk Commit Budget (ms)','4','Paces Unity object work between Editor ticks.'],
    ['Performance','Generated Payload Budget (MiB)','256','Author this before the world’s generated-payload owner is initialized.'],
    ['Generation','Auto Regenerate','Off','Apply changes deliberately with Regenerate.'],
    ['Generation','Wireframe Preview','Off','Use shaded terrain to judge the world.'],
    ['Generation','Capture Manual Edits On Regenerate','On','Captures Unity terrain sculpting before a deliberate rebuild.']
  ])+ui(chunkUI,[['Focus and Materials','LOD Focus','Your Main Camera transform','Select the ChunkManager child; this is a Transform slot.']])+
    p('Under ChunkManager → Focus and Materials, the read-only View Camera, Camera Position (world m) and Camera Near / Far (m) show the resolved camera and its actual lens. Camera parenting is optional. Assign the same camera Transform to LightingSystem → Lighting Graph → Environment Sample Target so its sky and weather belong to this world.')+
    p('The payload budget is a ceiling, not measured memory use. If you change it after initialization, save and reload the world so the owner can adopt the new budget. Do not lower terrain resolution to conceal a failed generation.'));

  before.LightingClimateWeather=heading('6','Open this world’s Lighting Graph',ol([
    'Select '+name+'/LightingSystem. Expand <b>Lighting Graph</b>, assign '+name+'_Lighting and click <b>Open Lighting Graph</b>. This is the same binding shown on the root.',
    'Set the window’s <b>World</b> to '+name+' and <b>Graph</b> to '+name+'_Lighting. Its read-only <b>Main Node Graph</b> must identify '+name+'_Terrain.',
    'Use <b>Pointer</b> to select nodes and wire ports. In empty graph space open the add-node menu and search the exact titles in the following diagrams. Use <b>Hand</b> to drag-pan the canvas, then return to Pointer.',
    'Create all six nodes in this single asset. The two Weather Region nodes describe opposite halves of this world and chain through Previous Regions. Weather Output and Lighting Output are separate terminals in this same Lighting Graph.',
    'Enter the node tables and connect each numbered arrow. Leave every port labelled “leave empty” disconnected, then Save.'
  ])+note('This graph uses climate, weather and lighting nodes. Terrain generators belong in the main graph; scatter nodes belong in Vegetation Graph. There is no separate Sun or River Course Solver node to create.'));
  before.LightingClimateWeather+=heading('7','Set this world’s clock',p('Hierarchy → '+name+'/WeatherSystem → Climate and Season (live) → World clock.')+
    fields('UCWeatherSystemManager',weatherUI,[
      ['Climate and Season  (live) → World clock','pauseTime','Off','Allows gameplay time to advance.'],
      ['Climate and Season  (live) → World clock','timeScale','120','120 game seconds per real second.'],
      ['Climate and Season  (live) → World clock','startDayOfYear',a?'172':'100','Day in the model year.'],
      ['Climate and Season  (live) → World clock','startHourOfDay','10','Start at 10:00 in this virtual world.']
    ])+ui(weatherUI,[
      ['Climate and Season  (live) → World clock','Game day (real minutes)','12','24 game hours take 12 real minutes; sets Time Scale to 1440 / 12.'],
      ['Climate and Season  (live) → World clock','Calendar year','1','The game’s model calendar, independent of your computer’s current date.']
    ])+ol([
      'Keep Unity out of Play mode. Select WeatherSystem, open <b>Climate and Season (live) → World clock</b> and click <b>Start Scene Day/Night Preview</b>. Preview temporarily enables the Scene view’s lighting and skybox and connects that view to this world’s lighting. Observe morning, noon, sunset and night at the game rate.',
      'Click <b>Stop Scene Day/Night Preview (restore time)</b> when finished. Stopping, saving, entering Play or closing this Inspector restores the preview’s starting time and the Scene lighting/skybox settings.',
      'In Play mode the same weather clock advances gameplay. Pause Time stops it; Time Scale changes its rate. Scene view remains available while playing, but Game view shows the game camera’s image. The preview button is disabled during Play; use the running game clock then. Do not use the operating-system clock to control this game day.'
    ]));
  before.LightingClimateWeather+=heading('8','Set the sun’s geographic site',fields('UCWeatherSystemManager',weatherUI,[
    ['Climate and Season  (live) → Site','latitudeDeg',a?'48':'8','Degrees north; an authored site for this fictional world.'],
    ['Climate and Season  (live) → Site','latitudeSpanDeg','0','One geographic sky site across this 6 km recipe.'],
    ['Climate and Season  (live) → Site','longitudeSpanDeg','0','No artificial longitude gradient across 6 km.'],
    ['Climate and Season  (live) → Site','longitudeDeg',a?'15':'45','Degrees east.'],
    ['Climate and Season  (live) → Site','clockReferenceMeridianDeg',a?'15':'45','Retained reference for this clock; set once for the new scene.'],
    ['Climate and Season  (live) → Site','standardMeridianDeg',a?'15':'45','Civil display meridian; changing it does not move the sun’s geographic site.'],
    ['Climate and Season  (live) → Site','axialTiltDeg','23.44','Degrees.'],
    ['Climate and Season  (live) → Site','northBearingDeg','0','World +Z is geographic north.'],
    ['Wind  (live)','windEnabled','On','Allows the shared wind manager to drive compatible content.']
  ])+fields('UCWeatherSystemManager',weatherUI,[
    ['Runtime Environment','enableRegionalWeather','On','Samples the two authored Weather Regions.'],
    ['Runtime Environment','enableLiveWeatherSim','Off','Keep their authored weather stable for initial inspection.'],
    ['Runtime Environment','weatherDrivesWind','On','Use each sampled region’s wind.'],
    ['Runtime Environment','simulateSurfaceResponse','On','Paces local wetness and snow response.']
  ])+p('If Wind (live) shows <b>Create Wind Manager</b>, click it. Do not set the grey Planned (not implemented) fields for this recipe. Assign the Main Camera transform to <b>LightingSystem → Lighting Graph → Environment Sample Target</b> after you place the camera.'));
  before.LightingClimateWeather+=heading('9','Set the lighting trims',fields('UCLightingSystemManager',lightUI,[
    ['Time and Sun','lightingEnabled','On','Updates the existing sun and atmosphere owner.'],
    ['Time and Sun','moonEnabled','On','Keeps a physically related night light.'],
    ['Tier and Performance','followAdaptiveQuality','Off','Allows a fixed inspection tier.'],
    ['Tier and Performance','lightingTierOverride','High','Initial desktop inspection tier.']
  ])+ui(lightUI,[
    ['Sky and Atmosphere','Turbidity',a?'2.5':'3','Atmospheric haze trim.','"settings.turbidity"'],
    ['Sky and Atmosphere','Sun Disc Size Deg','0.53','Angular diameter in degrees; independent of terrain extent and light-object scale.','"settings.sunDiscSizeDeg"'],
    ['Sky and Atmosphere','Moon Disc Size Deg','0.52','Angular diameter in degrees; the clock supplies its illuminated phase.','"settings.moonDiscSizeDeg"'],
    ['Sky and Atmosphere','Enable Stars','On','Shows catalog stars at night; game time, moonlight and clouds control their visibility.','"settings.enableStars"'],
    ['Sky and Atmosphere','Star Brightness','1','Saved radiance multiplier, 0–4; does not make stars appear in daylight.','"settings.starBrightness"'],
    ['Clouds','Cloud Softens Shadows','On','Uses cloud-cover softness.','"settings.cloudSoftensShadows"'],
    ['Exposure and Tonemapping','Enable Module Tonemapping','On','Enables the UC tonemapping Volume for this initial world inspection.','"settings.enableModuleTonemapping"'],
    ['Exposure and Tonemapping','Exposure Mode','Physical','Uses the physical environment exposure.','"settings.exposureMode"'],
    ['Exposure and Tonemapping','Night Floor Lux','0.002','Small night exposure-meter floor in lux; it does not recolour terrain.','"settings.nightFloorLux"'],
    ['Irradiance Grid','Enable Irradiance GI','On','Enables the existing bounded probe owner.','"settings.enableIrradianceGI"'],
    ['Irradiance Grid','Probe Spacing','32','Metres between probes.','"settings.probeSpacing"'],
    ['Irradiance Grid','Probes Per Frame','8','Paces dirty probe work.','"settings.probesPerFrame"'],
    ['Irradiance Grid','Bounce Strength','0.6','Indirect-light trim.','"settings.bounceStrength"']
  ])+p('Use the created directional light in <b>Time and Sun → Sun</b>. Shadows and enabled screen-space effects are tier readouts; do not look for editable cascade fields here. Manual Exposure Ev appears only when Exposure Mode is Manual. After desktop checks, turn Follow Adaptive Quality on for the host game’s quality owner.')+
    p('The UC lighting renderer supplies its generated sky material for the preview and this world’s admitted game camera. Keep the camera assigned to Lighting Graph → Environment Sample Target as instructed below. The Sun and Moon directional-light icons are editor handles: their position and transform scale do not set sky-disc size or limit light to a small world area. Look toward the clock’s celestial direction to see each sky disc; do not add another sun sphere or clock.')+
    p('Game time advances continuously through midnight and year rollover. Date, latitude and season change the celestial paths; morning/noon/evening buttons seek immediately for inspection. Sky brightness and ambient light blend over 0.25 real seconds between cached samples during normal progression. Near the horizon the sun and moon become warmer and dimmer through atmospheric extinction, and the horizon hides their lower portions. Their angular sizes stay the same across a larger world; for a deliberate larger presentation, increase the saved disc-size fields rather than scaling the light objects.')+
    p('The moon’s illuminated face changes continuously from new to full and back over approximately 29.53 game days. A new moon is unlit; an above-horizon moon may also be visible in daylight. Preview and Play use the same clock and sky outputs. Clouds weaken direct light, fog reduces visibility with distance, and surface wetness changes reflections. This is an Earth-like astronomical approximation.')+
    p('Keep Enable Stars on and Star Brightness at 1 for the first night inspection. Stars fade through twilight and disappear by daylight. Moonlight lowers faint-star contrast, atmospheric haze reduces low-horizon visibility, and the existing clouds cover the star field. The refined catalog raster makes the points smaller without changing their positions or relative magnitudes. Positions come from the Bright Star Catalog and rotate with the game date, time and latitude; Polaris is above the northern horizon only at northern latitudes. This bounded J2000 sky map omits proper motion, precession and the Milky Way. Compare a clear moonless night with a clear near-full-moon night in both Scene preview and Play.')+
    p('Inspect dry brown ground at solar noon with Physical exposure and Enable Module Tonemapping on. Terrain keeps its authored surface colour: time changes illumination, not the texture. Dull dry soil should not show a sharp sun reflection; water and wet/smooth surfaces can. If the host game supplies its own tonemapping, disable the UC module toggle and configure the host’s post-processing before judging brightness; avoid stacking two tonemapping owners.')+
    p('If clock angles move but the view stays bright at night, check <b>Time and Sun → Sun</b> and <b>Time and Sun → Lighting Enabled</b> first. A second always-on directional light can illuminate the terrain throughout the night; inspect any other scene lights rather than adding another sun. Judge the actual sky, terrain shadows and night lighting together.'));
  after.LightingClimateWeather=heading('10','Accept climate before generating ground',ol([
    'Save '+name+'_Lighting. On LightingSystem → Lighting Graph click <b>Run Baseline Climate</b>. Wait for a successful status and a nonzero Accepted Baseline Climate revision.',
    'Click <b>Run / Apply Lighting and Weather</b> after the baseline is accepted. This updates the dynamic weather and lighting state.',
    'The main graph’s Climate Baseline Input depends on that accepted baseline. Accepted climate is saved with the scene and restored after reopening; pending graph edits do not overwrite it. Older saves without acceptance prepare the terrain-independent early Lighting stage before a required terrain bake. If the binding or climate is invalid, generation reports failure and retains accepted ground; keep Allow Missing Baseline off.',
    'After changing baseline temperature, moisture or seasonal curves later, click <b>Run Baseline Climate</b> to accept those edits, rebuild terrain and then reapply vegetation. Changing live weather or the game hour refreshes appearance without rebaking plant positions.'
  ]));

  before.TerrainWater=heading('11','Build the main terrain and water graph',p(a?
    'Tectonic Plate Seed and Ridged Multifractal create the inland relief. Continental Shape Mask multiplies this relief to retain an outer ocean on all four sides. Glacial, rainfall and thermal erosion form the valleys and catchments. The procedural network and authored River Course Input merge into one bank and water chain.':
    'Continental Shape Mask multiplied by Hybrid Multifractal creates the outer ocean and inland hills. Rainfall and thermal erosion shape catchments before Lake Fill, Lake Basin and River Network carve water relief.')+p('In both worlds, Slope selects only gentle shores for the Coastal Erosion candidate. The following Blend copies that candidate under its mask, leaving steep coastal faces unchanged. This yields varied coast geometry before any lake or river carve.')+ol([
    'Select the world root and open Window > Universe Creator > Node Graph Editor. Confirm '+name+'_Terrain is the root’s main graph.',
    'Create each node in the following diagrams, enter its fields and connect the numbered arrows exactly. A reused upstream output is one output with several wires, not another node.',
    'Place Height Output and Biome Splat Output at the right side. The height chain ends at Height Output; biome/surface weights end at Biome Splat Output.',
    'Leave unknown optional ports empty as listed. Do not add a second height clamp, erosion pass or sea-freeboard correction after Height Output.'
  ]));
  before.TerrainWater+=heading('12','Set WaterSystem and one sea authority',p('Hierarchy → '+name+'/WaterSystem. Configure these before the first terrain generation.')+
    ui(waterUI,[
      ['Source','Source','Both','Allows the procedural spawner and your authored water bodies.','"source"'],
      ['Source','Water Layer','Water','Choose the existing Unity Water layer for water surfaces.'],
      ['Source','Drive World Water Layer','On','Publishes that layer to the world owner.','"driveWorldWaterLayer"'],
      ['Sea Level','Node Sea Level',String(sea),'Normalized sea height; '+(sea*height)+' m in this world.','"nodeSeaLevel"'],
      ['Defaults For New Bodies','New Body Bed Source','Terrain','New bodies read the world’s terrain.','"newBodyBedSource"'],
      ['Defaults For New Bodies','New Body Build Trigger','Manual','Build after editing a manual shoreline/course.','"newBodyBuildTrigger"']
    ])+p('After the graph is fully wired, click <b>Sea Level → Push To Graph</b>. It updates the existing sea-reading nodes; Save the main graph afterward. Use <b>Create Water → River/Lake</b> only for deliberate manual bodies. The Hydrology Spawner owns procedural lakes and the ocean.')+
    p('Shared Water Description edits a shared descriptor. Set <b>Shared Material Override</b> only if you have an existing compatible water material; the later Texture Graph also supplies a water descriptor. Leave interactive ripple simulation off for the first shape build.'));
  after.TerrainWater=heading('13','Enter the biome surface rules',p('In the main graph select <b>Biome Splat Output</b>. Expand <b>Rules</b>, then click <b>+ Add Rule</b> once per row below. Expand each <b>Rule</b> row and enter its values. Use <b>Up</b> / <b>Down</b> to reorder and <b>Remove</b> to delete a row; the list scrolls when it grows. Each row is a BiomeSplatRule, not a new node.')+
    table(['Biome','Ground Layer','Slope Layer','Slope Start / Slope End (°)','High Layer','High Start / High End','Weight'],
      ['TropicalRainforest','TropicalSeasonalForest','Savanna','SubtropicalDesert','TemperateRainforest','TemperateDeciduousForest','TemperateGrassland','BorealForest','Tundra','PolarDesert'].map(b=>[
        e(b.replace(/([a-z])([A-Z])/g,'$1 $2')),String(/Forest/.test(b)?7:/Desert/.test(b)?3:6),'1',a?'30 / 55':'35 / 60',a?'4':'0',a?'0.68 / 0.78':'0.9 / 1','1'
      ]))+p('Each Rules row has separate <b>Slope Start (°)</b>, <b>Slope End (°)</b>, <b>High Start (0–1)</b> and <b>High End (0–1)</b> fields; enter both numbers in each table pair and commit numeric edits with Enter. High Start / High End are normalized heights: '+(a?'1224–1404 m':'810–900 m')+' at this world height. Layer numbers are zero-based array indices. Assign all eight slots in step 17. For these inline rules, leave <b>Palette</b> unassigned: a populated palette supplies the active rules instead. The last duplicate biome wins; Weight 0 disables a rule, and an empty list uses Default Layer.')+
    p('Surface priority is exposed rock/cliff on steep slopes, snow where enabled above Snow Line, beach sand near sea level, then the biome floor. Wetness is water membership. This prevents plants from having to paint the terrain first.'));
  after.TerrainWater+=heading('14','Generate and inspect the trenches',ol([
    'Save the main graph. Select '+name+' and click <b>Regenerate</b> in the root Inspector’s bottom action bar. Wait for generation and the paced water settlement to finish.',
    'Use the Scene view to inspect the coast, closed basins and river beds. River Network must cut headwaters and tributaries that join downstream reaches; separate catchments may have separate outlets.',
    'Inspect all four outer edges from above: the surrounding Ocean body must cover their below-sea terrain. Move inland to inspect plains, uplands, valleys and closed lakes; the ocean is the boundary, not the whole world.',
    'Check sandy bays beside low shores and rocky/cliff headlands. Slope-gated wave erosion must leave the steep faces intact. Beach sand fades out by '+(a?'22':'24')+'°; the immediate water-edge strip uses mud/silt, so a coast is not one continuous sand ring.',
    'Expand WaterSystem → Bodies to select the generated lakes. A requested target of three is not a guarantee of three suitable closed basins. Do not rerun repeatedly simply to force the target count.',
    'Lake outlet tracing samples already accepted bodies. After the first lakes settle, run one deliberate second Regenerate to integrate their outlet courses; existing lake identities prevent duplicate footprints.',
    'If generation fails, read the Console’s first exception before tuning density. Check the climate binding, connected Height inputs and sea values. A failed River Network cannot be repaired by adding another Water Bank Seat.',
    'Compare river sources, junctions, mouth elevation and bed continuity. Sea Freeboard must preserve lake interiors and river relief; avoid adding a hard floor that creates raised rings.'
  ])+note('At about 1.953 m per terrain sample, a sub-metre trench cannot carry full geometric detail. Narrow streams still need supported water rendering. Inspect the actual terrain and water result; a source or self-test check does not prove its final appearance.'));
  after.TerrainWater+=heading('15','Edit a lake without losing it',p('Select a lake through WaterSystem → Bodies. The generated formation is already chosen from terrain-compatible seeded families. Preserve its selected formation for the procedural build. For a deliberate manual example use Create Water → Lake, place the outline in a suitable valley, then enter:')+
    fields('UCWaterBody',bodyUI,[
      ['Dig  (carves the terrain)','digTerrain','On','Without this no lake trench is dug.'],
      ['Dig  (carves the terrain)','bedProfile','Lake Basin','Non-ocean bed profile.'],
      ['Dig  (carves the terrain)','digDepthMetres',a?'18':'6','Metres below the lake surface.'],
      ['Dig  (carves the terrain)','shoreRunMetres',a?'60':'120','Metres of shore transition.'],
      ['Dig  (carves the terrain) → Bed ruggedness','bedRuggednessMetres',a?'0.8':'0.25','Maximum bed detail scale in metres.'],
      ['Dig  (carves the terrain) → Basin shape','lakeFormation',a?'Tectonic':'River Floodplain','Choose this only for the new manual example.'],
      ['Dig  (carves the terrain) → Basin shape','lakeSedimentation',a?'0.2':'0.65','0 is less sediment smoothing; 1 is more.']
    ])+ol([
      'For a polygon lake, use its existing outline nodes in Scene view; move a point, Shift-click a segment to insert a point, or select a point and press Delete. Keep a closed shoreline.',
      'Press the body’s <b>Build</b> after editing. Inspect the basin, shore slope and water level together. A manual lake needs a plausible closed basin or natural barrier.',
      'For an edited generated lake click <b>Save Placement (protect from Regenerate)</b> under Dig → Procedural. This preserves its footprint and edits for later rebuilds.',
      'The existing Lake Formation dropdown also contains Glacial Erosion, Glacial Deposition, Volcanic, Karst, Landslide Dammed, Coastal Barrier, Wind Erosion, Meteorite Impact and Biological Damming. They are formation-specific profiles, not eleven additional graph nodes.'
    ]));
  after.TerrainWater+=heading('16','Preserve a manually authored river',ol([
    'Select WaterSystem and click <b>Create Water → River</b>. Keep Source at Both. The new river is a UCWaterBody with a course of child nodes.',
    'In Scene view use the river’s existing Ctrl-click terrain placement tool to extend its course. Move the course nodes from a source toward a lake or ocean mouth, preserving a downstream bed trend.',
    'On the river Inspector enable <b>Dig (carves the terrain) → Dig Terrain</b> and use <b>Bed Profile → River Channel</b>. Edit its course node width, depth and flow through the existing course controls; press <b>Build</b>.',
    a?'The graph already includes River Course Input. Its Height receives River Network’s Height, and its captured authored River Mask joins the network mask before River Banks. Keep Carve Trench enabled.':
      'Green Coast’s base recipe uses the procedural network. An authored river body is still re-cut by the existing native carve owner when rebuilt; it does not need a made-up River Course Solver node.',
    'Select ChunkManager and click <b>Capture Manual Edits</b> after sculpting the terrain. This stores owned terrain changes; it is separate from Save Placement on a procedural water body.',
    'Save the scene, water bodies and graph. Regenerate once and compare the manual course and junction. The same saved terrain cells must be restored when reopening Unity.'
  ])+p('If the main graph is later extended with River Course Input, chain its Height after River Network and union its mask with the procedural River Mask before banks/fill. Do not connect two wires to a single Height or River Mask input.'));

  before.TextureMaterialShader=heading('17','Assign the ground’s eight layer slots',p('Select '+name+' → Terrain Layers. Set <b>Layer Count = 8</b>. Create eight TerrainLayer assets in the Project window using <b>Create > Rendering > Terrain Layer</b>, then assign them to the following exact root slots.')+
    table(['Root field','Layer name','Existing colour texture / purpose'],[
      ['Channel 0','Rock','Surfaces/Rock051/Rock051_2K-PNG_Color.png'],
      ['Channel 1','Cliff Rock','Surfaces/Rock063/Rock063_2K-PNG_Color.png'],
      ['Channel 2','Mud/Silt','Surfaces/Ground094B/Ground094B_1K-PNG_Color.png'],
      ['Channel 3','Sand','Assign your licensed seamless sand colour texture.'],
      ['Channel 4','Snow','Surfaces/Snow015/Snow015_2K-PNG_Color.png'],
      ['Channel 5','Reserved Road','Reserve this slot; use the rock layer as a temporary placeholder.'],
      ['Channel 6','Grass','Surfaces/Grass001/Grass001_2K-PNG_Color.png'],
      ['Channel 7','Forest Floor','Surfaces/Ground108/Ground108_2K-PNG_Color.png']
    ].map(row=>row.map(e)))+p('Paths above are relative to <b>Assets/UniverseCreator/Content</b>. The current Surfaces folder has no confirmed seamless sand set; SandFootsteps_01_T_A is a footprint/decal asset, so it is not prescribed as a beach tile. Import your chosen licensed sand colour and normal maps before finishing this world.')+
    p('Each TerrainLayer stores the ground colour, matching normal and tiling. Use Normal Map import type for *_NormalGL.png; colour uses sRGB, while roughness, AO, masks and displacement are data. Do not connect a normal texture to an Albedo field.'));
  before.TextureMaterialShader+=heading('18','Wire this world’s Texture Graph',ol([
    'On the root’s UC World Graph Bindings assign '+name+'_Texture. Open <b>Window > Universe Creator > Texture Graph</b>; choose this asset and an owned generated <b>Terrain</b>.',
    'Build all nodes in the following connected diagrams. Four Albedo Layer → Normal Map Layer pairs feed Texture Splat’s Layer R/G/B/A inputs, which correspond to terrain channels 0/1/2/3.',
    'Assign each Albedo Layer’s <b>Albedo</b> texture from its matching slot in step 17. On each Normal Map Layer assign the matching <b>Normal Map</b>; use its Roughness texture only if provided and keep Roughness Is Smoothness off.',
    'Cliff Surface begins a separate surface modifier chain; its empty Surface input is supported. Assign its rock colour/normal textures. This chain and Texture Splat meet at Terrain Blend Final, then Shader Output.',
    'Water Surface → Water Foam → Caustics supplies Water Surface Data to Shader Output. It describes water rendering; water bodies and trenches still come from WaterSystem and the main graph.',
    'Save, then use <b>Run / Apply</b> on this target terrain. Repeat for the world’s other owned terrain cells as needed. Do not target an unrelated terrain.'
  ])+p('The cards show only fields used by this recipe. Vector2 values are X,Y. Material output goes to Assets/UniverseCreator/Generated; identify this world’s distinct material name in Shader Output.'));
  after.TextureMaterialShader=heading('19','Check materials and surface placement',ol([
    'After Run / Apply succeeds, select the root and inspect <b>Terrain Rendering → Material</b>. Use the generated '+(a?'UC_AlpineTerrain':'UC_ContinentalTerrain')+' UC terrain material on owned cells.',
    'Inspect rock slopes, cliff projection, mud at banks, beaches, grassland and forest floor. Texture Graph preserves native layers 4–7 beyond its four authored channels; confirm their additional URP terrain pass in Scene view.',
    a?'Inspect Snow Cover above 1296 m, with a 180 m falloff. Snow is an authored surface effect and the native layer-4 terrain coverage; it does not add a second height displacement.':
      'Keep Snow Cover coverage at zero in this warm coastal recipe. Layer 4 stays reserved for a deliberate later cold-region or season workflow.',
    'Select a water body and inspect <b>Material</b> after applying. If you choose a Shared Material Override, use an existing compatible UC water material and click WaterSystem’s <b>Apply Shared Description</b>.',
    'Move the Environment Sample Target between western and eastern regions to inspect local wetness, cloud and visibility. A baked wetness trim is not a substitute for the live weather owner.',
    'Correct missing texture assignments before placing vegetation. A solid fallback colour cannot supply the surface detail expected from matching PBR maps.'
  ]));

  before.Vegetation=heading('20','Assign trees and shrubs on VegetationSystem',p('Select '+name+'/VegetationSystem. Expand <b>Prototype library</b>, then the subgroup in the first column. These arrays are owned by the world and hosted here; do not look for them under LightingSystem.')+
    ui(worldUI,[
      ['Trees','Tree Count','1','Creates tree prototype 0.'],
      ['Trees → Tree 0','Prefab',a?'Your licensed conifer prefab':'DatePalm_02_Prefab','Tree prototype 0; see the content note below.'],
      ['Trees → Tree 0','Wind Bend','0.4','Native tree wind trim for a compatible prefab.'],
      ['Trees → Tree 0','Width Scale','0.8–1.2','Random width range.'],
      ['Trees → Tree 0','Height Scale','0.8–1.25','Random height range.'],
      ['Trees → Tree 0','Max Slope',a?'32':'28','Degrees; matches Tree Scatter.'],
      ['Shrubs & Bushes','Shrub Count','1','Creates shrub prototype 0.'],
      ['Shrubs & Bushes → Shrub 0','Prefab',a?'Cherry_shrub_01':'BushGroup_Prefab','Existing prefab in Content/Shrubs/SampleNatural.'],
      ['Shrubs & Bushes → Shrub 0','Weight','1','Selection weight for this single prototype.'],
      ['Shrubs & Bushes → Shrub 0 → Walk-through','Collider Policy','None','Initial scenery: no trigger or solid collider.'],
      ['Shrubs & Bushes → Shrub 0 → Placement','Embed Depth','0.08','Metres of ground seating.'],
      ['Shrubs & Bushes → Shrub 0 → Placement','Normal Align','0.25','0 stays upright; 1 follows the ground normal.']
    ],'Prototype library → ')+p(a?'The current Trees/SampleNatural library contains palms and Cherry_tree_01, with no confirmed conifer prefab. Add your licensed pine/spruce/fir prefab and use it in tree prototype 0 for the prescribed boreal stand; do not scatter tropical palms into this cold-world recipe.':
      'DatePalm_02_Prefab is in Content/Trees/SampleNatural. Use it as an existing warm-world prototype; replace it with a licensed native seasonal-forest species where the game’s art direction needs one.')+
    p('Collider Policy defaults to Auto, which gives shrubs triggers. The table deliberately selects None for this scenery build; Trigger Radius, Trigger Height and Collider Layer then hide because they have no consumer. Prototype entry titles include the assigned prefab name after their index.'));
  before.Vegetation+=heading('21','Assign grass detail and rock prototypes',ui(worldUI,[
    ['Grass & Detail','Detail Count','1','Creates detail layer 0.'],
    ['Grass & Detail → Detail 0','Mesh Prototype','SmallGrass_01_Prefab','Content/Grass/SampleNatural; actual category is Grass.'],
    ['Grass & Detail → Detail 0','Billboard','None','A mesh prototype takes precedence; do not assign both.'],
    ['Grass & Detail → Detail 0','Density /m²',a?'0.3':'0.45','Library detail painting density.'],
    ['Grass & Detail → Detail 0','Width (m)','0.35–0.7','Detail instance width range.'],
    ['Grass & Detail → Detail 0','Height (m)',a?'0.2–0.5':'0.3–0.75','Detail instance height range.'],
    ['Grass & Detail → Detail 0 → Placement','Align To Ground','On','Seats eligible details on the ground.'],
    ['Grass & Detail → Detail 0 → Placement','GPU Instancing','On','For the mesh detail prototype.'],
    ['Grass & Detail → Detail 0 → Placement','Max Slope','35','Degrees.'],
    ['Rocks & Props','Object Count','2','Creates the two rock/object prototypes used by Rock Scatter.'],
    ['Rocks & Props → Object 0 / Object 1','Prefab','Rock_01_Prefab; Rock_02_Prefab','Assign Rock_01 to Object 0 and Rock_02 to Object 1.'],
    ['Rocks & Props → Object 0 / Object 1','Weight','1 for each','Equal prototype selection weights.'],
    ['Rocks & Props → Object 0 / Object 1','Embed Depth (m)','0.2','Seats rock bases into the terrain.'],
    ['Rocks & Props → Object 0 / Object 1','Normal Align','0.6','Aligns partly to the ground normal.'],
    ['Rocks & Props → Object 0 / Object 1','Instancing','When Dense','Enum choice WhenDense; batches instances after the library threshold.'],
    ['Rocks & Props → Object 0 / Object 1','Collider Policy','Auto','Stone requests solid collision within the library budgets; dense instanced rocks have no collider.']
  ],'Prototype library → ')+p('Leave Healthy Colour and Dry Colour at neutral fresh defaults for the first material check. Wind Response and Motion Model work only with supporting shaders. In Play mode check rock collision before using these procedural rocks as gameplay obstacles; collider budgets and instancing can retire their colliders.'));
  before.Vegetation+=heading('22','Set the vegetation manager’s placement controls',fields('UCVegetationSystemManager',lightUI,[
    ['Global','vegetationEnabled','On','Enables the world’s existing placement owner.'],
    ['Global','globalDensityScale','1','Neutral multiplier.'],
    ['Ecology','useEcology','Off','Use the authored biome masks and scatter rules for this first build.'],
    ['Grass and Detail','detailResolution','512','Native detail grid per chunk.'],
    ['Grass and Detail','detailResolutionPerPatch','16','Native detail patch size.'],
    ['Grass and Detail','detailUsesWorldWind','On','Compatible detail content reads the shared wind.'],
    ['Grass and Detail','detailMemoryCeilingMB','128','Detail allocation ceiling in MiB.'],
    ['Placement Rules','avoidWater','On','Reject submerged placements.'],
    ['Placement Rules','avoidCutRegions','On','Preserve authored cut exclusions.'],
    ['Placement Rules','clearanceMetres','1','General ground clearance in metres.'],
    ['Biome and Masking','useGraphMasks','On','Uses the connected accepted biome membership masks.'],
    ['Biome and Masking','maskThreshold','0.5','Membership acceptance threshold.'],
    ['Hierarchy','parentUnderVegetationSystem','On','Keeps generated placement under its existing owner.']
  ])+p('Altitudes in Placement Rules are metres above sea level. For this build use Min Altitude Metres = 0 and Max Altitude Metres = '+(a?'720':'550')+'. Graph density is candidates per km²; Grass & Detail library density is per m² and manager tree density is per hectare. Do not copy one unit into another field.'));
  before.Vegetation+=heading('23','Build this world’s Vegetation Graph',ol([
    'Select VegetationSystem and assign '+name+'_Vegetation in <b>Vegetation Graph</b>. Click <b>Open Vegetation Graph</b>. Confirm the window’s World and Main Node Graph belong to '+name+'.',
    'Create the six nodes in the following diagrams. Both World Graph Input nodes refer to the same accepted main graph; set each node’s <b>Main Graph</b> field to '+name+'_Terrain.',
    'The first reader selects '+(a?'Boreal Forest':'Tropical Seasonal Forest')+' for tree and shrub masks. The other selects '+(a?'Temperate Grassland':'Savanna')+' for grass. Connect Accepted Biome Membership to each scatter’s Mask; the encoded Biome inlet stays empty.',
    'Set tree prototype index 0, shrub prototype index 0 and grass detail layer index 0 as printed. Rock Scatter reads the Rocks & Props library and retains its height/slope and water rejection.',
    'Save the graph and click <b>Run / Apply Vegetation</b> on the manager. Wait for the accepted status before editing another region.'
  ]));
  after.Vegetation=heading('24','Check that plants occupy the right ground',ol([
    'Inspect a forest patch, open grassland, a steep cliff, a shoreline and a river junction. Trees and shrubs must follow their forest mask; grass follows its separate open-biome mask.',
    'If a stand is empty, first check World Graph Input → Main Graph, the selected biome and accepted membership in the main graph. A zero mask legitimately creates no plants. Increasing density does not create a missing biome.',
    'Check that all prototype 0 slots contain the assigned assets and that Grass Detail uses detail layer 0. Correct missing assets before changing placement rules.',
    'Inspect scale, ground seating, wind, LOD transitions and plant shadows in Scene and Play. Rocks may occupy non-forest ground; slope and water exclusions still apply.',
    'To change a baked biome, rebuild the terrain graph, reapply textures where needed, then Run / Apply Vegetation. Live rain, fog and clock changes update appearance through their existing managers.'
  ]));
  const finish=heading('25','Save, reopen and enter the game',ol([
    'Stop Scene Day/Night Preview. Save all four graph assets. Select ChunkManager and click <b>Capture Manual Edits</b> for deliberate terrain sculpting; save protected generated water placement on each edited body.',
    'Save the scene with File > Save. Reopen it and inspect the existing terrain-cell identities, authored terrain layers, manager bindings and manual river. Valid saved ground must restore without duplicate terrain cells. Play entry restores accepted saved ground and generates missing graph-bound cells; it does not automatically regenerate all saved sculpting and river beds. Use the root’s Regenerate action deliberately when you intend to rebuild.',
    'In Scene view, frame an inland position above the actual ground. Select your Main Camera in Hierarchy, then use <b>GameObject → Align With View</b> and confirm its clearance above the terrain. A character and camera parenting are optional. Assign this same camera’s Transform to <b>ChunkManager → Focus and Materials → LOD Focus</b> and <b>LightingSystem → Lighting Graph → Environment Sample Target</b>. A camera outside the world hierarchy needs that explicit environment target binding.',
    'Select Main Camera and check <b>Camera → Environment → Background Type</b> is Skybox and <b>Culling Mask</b> includes the terrain layer. Under <b>Projection → Clipping Planes</b>, retain a useful Near distance and set Far beyond the landscape you intend to see. These are host-camera choices, not an infinite visibility setting. Compare ChunkManager’s camera position and clip readouts with the actual ground; a camera at Y = 1 cannot view ground above Y = 1184 from below. Keep the authored Field of View unless your game calls for a different lens.',
    'Enter Play mode and select Game view to see that camera’s output. Inspect the 12-minute day, shadows, exposure, local weather, terrain collision and river mouths. Scene view can also remain open during Play; it is the editor’s navigation view, not the game camera. Use Scene Day/Night Preview only with Play stopped.',
    'If you later enable Stream At Runtime, validate near-ground traversal and accepted collision before connecting the host game’s teleport/travel UI. The host game must wait for destination readiness; a camera move alone is not proof that a terrain collider is ready.',
    'Once this world passes the visual and save/reopen checks, add roads, buildings and gameplay through their own systems. Preserve the natural terrain, masks, water and vegetation owners.'
  ])+p('If Game view is grey, inspect this camera before regenerating: it must be enabled, above the ground, pointed toward it, and include the terrain GameObject’s layer in its Culling Mask. Check its near/far clipping planes against the ground distance. UC preserves authored terrain layers and camera clipping/culling settings; it does not widen them automatically. A Scene view that sees terrain does not prove the game camera sees it.')+
    p('Universe Creator is the PCG subengine inside your game. This guide configures its natural-world scene; rendering quality, scene loading, gameplay and the phone budget must be measured in the assembled game.')+
    note('Recipe wiring/fields are checked against this source version. The Scene/Play and camera/sky repair passed lighting 1,454, streaming/readiness 177, the full suite 19,690, scene reload 18 and domain reload 9 checks, all with zero failures. These checks do not certify the pictured scenery, missing art assets, GPU behaviour or real-device performance. Inspect this particular scene before using it as a finished game world.'));
  return {start,before,after,finish};
}

module.exports=chapters;
