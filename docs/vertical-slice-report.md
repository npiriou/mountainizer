# Milestone report

## Successfully parsed

The supplied NTSC-U disc is indexed as ISO9660 volume `SSX3`; `SLUS_207.72` and `DATA/WORLDS/BAM.BIG` are detected and the complete ISO SHA-256 is recorded. The BIG archive's mixed-endian table is indexed and SDB/SSB/PHM/PSM members are extracted to a reusable read-only project cache.

SDB exposes 49 streaming areas. Mountainizer maps those areas into all 17 playable courses and loads each course's event, shared-mountain, connector, and sky groups in one bounded SSB pass. Terrain, materials, MDR models, static instances, textures, splines, visibility curtains, and the spatial portion of camera-trigger tables are parsed into source-aware Core records. Unknown resource families retain their type, track/RID, physical compressed group range, logical decompressed offset, byte preview, and diagnostics.

The local regression audit opens all 17 course assemblies. Every course contains terrain, props, models, and splines, every audited prop resolves to decoded MDR geometry, and no structured parser errors occur.

## Rendered and exported

Terrain difference coefficients are converted to bicubic control points, tessellated, normal-generated, UV-mapped, and textured. MDR submeshes render at decoded instance transforms with resolved material textures, including trees, rails, ramps, rocks, signs, icicles, groomers, and other obstacles. Repeated model/material ranges use GPU instancing; the renderer caches the visible instance set and frustum-culls it when the camera changes. Splines, camera-trigger bounds, and visibility curtains are optional colored debug overlays.

The desktop workbench provides course selection with real names, technical-area access, name/category hierarchy search, source/property inspection, texture previews, per-category and per-instance visibility controls, triangle-accurate terrain/prop selection, camera navigation, terrain highlighting, prop isolation, structured diagnostics, and diagnostic JSON export. OBJ export includes terrain, decoded prop instances, an MTL library, and referenced diffuse textures as PNG files.

## References

Replanetizer informed editor/renderer separation. `ssxdecomp/ssx3` established the target executable and runtime investigation points. The physics rehost cross-checked BIG behavior. SSX-Library and Collection Multitool supplied format hypotheses that were validated against the user-local game data. Public refpack-rs documentation supplied the compression command layout. Licensing notes and reference commits are recorded in `reference-analysis.md`.

## Remaining risks

Collision, AIP graphs, particle/light systems, sound triggers, scripts, missions, and other SSB types remain preserved but unsupported. Camera-trigger actions and the non-spatial trigger arrays are unknown. Lightmaps and some texture/material variants remain incomplete. Coordinate units/handedness need validation beyond NTSC-U PS2. glTF, installer signing, and editing/serialization are future milestones.
