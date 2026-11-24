# Bright Star Catalog sky resource

`catalog.txt` is the unchanged original Bright Star Catalogue, 5th Revised Ed. (Hoffleit & Warren, 1991), revised preliminary version, mirrored by frostoven/BSC5P-JSON. It contains 9,110 records, including nonstellar/invalid records that the runtime parser skips.

Sources:

- Catalog description: https://heasarc.gsfc.nasa.gov/W3Browse/star-catalog/bsc5p.html
- Original fixed-width data and byte layout: https://github.com/frostoven/BSC5P-JSON/tree/primary/original_bsc5p
- Original catalog: https://raw.githubusercontent.com/frostoven/BSC5P-JSON/primary/original_bsc5p/catalog

SHA-256: `69797549CC1605AAD7FF94E9325E29A1661F2A253917FAAA056D9BF20B809AFD` (1,704,879 bytes).

The converter repository marks its converter code CC0; that does not establish a separate CC0 license for the astronomical catalog. Catalog credit and original provenance are retained here; UC claims no ownership of the source data.

UC reads J2000 right ascension (one-based columns 76–83), declination (84–90) and visual magnitude (103–107). It rasterizes relative flux `10^(-0.4*magnitude)` into a bounded 1024×512 linear RGBAHalf equatorial texture, with bilinear position distribution and latitude-bin solid-angle correction. The integrated brightness uses the existing 0.001 lux starlight anchor. This is a sky rendering approximation, not exact individual-star photometry.

The existing game clock/location rotates the field. Stars fade through twilight, are absent in daylight, dim under the existing moonlit exposure and sit behind the existing cloud composite. There are no per-star GameObjects or runtime downloads. One native texture is retained per lighting owner (4 MiB nominal pixel storage); the temporary 8 MiB colour buffer and native CPU copy are not retained after upload.

Limits: bright-catalog coverage, J2000 without proper motion/precession, approximately 0.35° RA raster spacing, neutral star colour, no Milky Way map/light pollution/physical scintillation, and the existing approximate game solar calendar. Polaris is below the horizon at southern latitudes. These limits must not be described as an exact all-star ephemeris.
