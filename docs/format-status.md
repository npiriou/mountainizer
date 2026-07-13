# Format status

Status as of 2026-07-13. “Tested” means the user-local NTSC-U image supplied for development; no copyrighted sample data is committed.

| Format / section | Current support | Rendering / use | Important unknowns | Confidence | Tested result |
|---|---|---|---|---|---|
| ISO9660 | PVD, recursive directories, bounded extraction, revision discovery | Read-only project import | Multi-extent/interleaved records, UDF | High | NTSC-U ISO; `SLUS_207.72` found |
| BIGF / `BAM.BIG` | Mixed-endian index, listing and extraction | Supplies SDB/SSB/PHM/PSM | Other BIG variants | High | 5 entries; 113,078,400 bytes |
| SDB | Header, 49 locations, group spans | 17-course catalog and multi-area course assembly | Remaining metadata and runtime conditional-streaming rules | Medium | All 17 playable assemblies resolved |
| SSB / RefPack | `CBXS`/`CEND`, bounded RefPack, resource headers | Shared parser for GUI and CLI | Alternate outer/header variants | High | All groups needed by every playable course |
| PHM / PSM | Resource tables and string resolution | Names MDR models, props, and splines | Remaining arrays/flags | Medium | 89,676 names resolved in tested world |
| Type 0 materials | 20-byte material records and texture RID mapping | Textures MDR submeshes | Remaining short fields | Medium | All model submeshes resolved in audited courses |
| Type 1 terrain | World patch fields, bicubic coefficient conversion, UV corners, 8×8 tessellation | Textured terrain | Lightmaps, flags, trailing semantics | Medium | 970–3,569 patches per assembled course |
| Type 2 MDR | Hierarchy, transforms, quantized vertices/UVs/normals, normal and combined packet chains, material parts | Instanced textured props/obstacles | Packet variants outside audited data | Medium | Geometry decoded across all 17 courses |
| Type 3 instances | 160-byte transforms, bounds, object/model references | Every audited prop resolves to MDR geometry | Dynamic behavior and flags | Medium | 1,104–4,635 resolved instances per course |
| Type 8 splines | Header, segment records, difference-coefficient conversion, sampled cubic curves | Cyan debug lines | Roles, coefficients, remaining fields | Low | 51–330 splines per course; zero parse errors |
| Type 9 SSH | 4-bit, 8-bit, and direct RGBA pixels; PS2 palettes/unswizzle; shared-bank consolidation | Terrain/model textures and asset preview | Additional pixel formats | Medium | 17-course visual/reference audit |
| Type 10 SSH/lightmaps | Resource framing only | Reported as unknown | Lightmap application and variants | Low | Preserved with source metadata |
| Type 11 visibility curtains | Known 184-byte record, bounds and sampled cubic curve | Purple debug lines | Curtain surface semantics and flags | Low | 1–28 curtains per course |
| Type 12 collision | Framed and preserved | Not rendered | Full topology/material mapping | Low | Present across audited courses |
| Type 13 sound triggers | Framed and preserved | Not rendered | Header, variable arrays, spatial/action records | Low | Present across audited courses |
| Type 14 AIP | Framed and preserved | Not rendered | Path graph structures | Low | Present across audited courses |
| Type 17 camera triggers | 28-byte header and first array of 72-byte spatial volume records | Red debug boxes | Actions, priorities, remaining arrays | Low | 101 volumes in the ABC1 assembly |
| Other SSB types | Source range, track/RID, size, preview bytes, diagnostics | Unknown Sections hierarchy | Particles, lights, scripts, missions, audio, avalanche data | Unknown | Preserved without crashing |

The local regression suite parses all 17 playable course assemblies and asserts that each has terrain, props, models, and splines with zero structured parser errors.
