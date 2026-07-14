# Format status

Status as of 2026-07-14. “Tested” means the user-local NTSC-U image supplied for development; no copyrighted sample data is committed.

| Format / section | Current support | Rendering / use | Important unknowns | Confidence | Tested result |
|---|---|---|---|---|---|
| ISO9660 | PVD, recursive directories, bounded extraction, revision discovery | Read-only project import | Multi-extent/interleaved records, UDF | High | NTSC-U ISO; `SLUS_207.72` found |
| BIGF / `BAM.BIG` | Mixed-endian index, listing and extraction | Supplies SDB/SSB/PHM/PSM | Other BIG variants | High | 5 entries; 113,078,400 bytes |
| SDB | Header, 49 locations, group spans | 17-course catalog and multi-area course assembly | Remaining metadata and runtime conditional-streaming rules | Medium | All 17 playable assemblies resolved |
| SSB / RefPack | `CBXS`/`CEND`, bounded RefPack, resource headers | Shared parser for GUI and CLI | Alternate outer/header variants | High | All groups needed by every playable course |
| PHM / PSM | Resource tables and string resolution | Names MDR models, props, and splines | Remaining arrays/flags | Medium | 89,676 names resolved in tested world |
| Type 0 materials | 20-byte material records and texture RID mapping | Textures MDR submeshes | Remaining short fields | Medium | All model submeshes resolved in audited courses |
| Type 1 terrain | World patch fields, bicubic coefficient conversion, diffuse UV corners, lightmap atlas rectangle, 8×8 tessellation | Diffuse terrain; lightmap coordinates retained for inspection | Flags, trailing semantics, original lightmap blend | Medium | 970–3,569 patches per assembled course |
| Type 2 MDR | Hierarchy, transforms, quantized vertices/UVs/normals, normal and combined packet chains, material parts | Instanced textured props/obstacles | Packet variants outside audited data | Medium | Geometry decoded across all 17 courses |
| Type 3 instances | Complete 160-byte record framing, transforms, bounds, object/model references, preserved trailing words | Every audited prop resolves to MDR geometry; named non-visual instances receive a category and reason | Semantic meaning of remaining fields and dynamic behavior | Medium | 1,104–4,635 resolved instances per course |
| Type 4 particle programs | Variable-size program framing, self/reference IDs and header metadata | Inspector hierarchy; referenced by Type-5 emitters | Instruction/parameter tables and runtime simulation | Low | Known record variants enriched and preserved across all 17 courses |
| Type 5 particle instances | 144-byte transform, position, bounds, and particle-model references | Inspector properties; experimental marker disabled by default | Type-4 particle program/model semantics and runtime simulation | Low | Framing and finite positions validated across all 17 courses |
| Type 6 lights | 112-byte framing, kind/range/color fields, spatial anchor triplet | Inspector properties; experimental marker disabled by default | Exact light equation and remaining fields | Low | Record size and spatial coordinates validated across all 17 courses |
| Type 7 halos | 80-byte framing, color, spatial anchor triplet and derived radius | Inspector properties; experimental marker disabled by default | Texture/blend behavior and remaining fields | Low | Record size and finite positions validated across all 17 courses |
| Type 8 splines | Header, segment records, difference-coefficient conversion, sampled cubic curves | Cyan debug lines | Roles, coefficients, remaining fields | Low | 51–330 splines per course; zero parse errors |
| Type 9 SSH | 4-bit, 8-bit, and direct RGBA pixels; PS2 palettes/unswizzle; shared-bank consolidation | Terrain/model textures and asset preview | Additional pixel formats | Medium | 17-course visual/reference audit |
| Type 10 SSH/lightmaps | Direct RGBA atlas decoding, independent RID namespace, patch RID lookup | Asset preview only; experimental viewport application disabled | Original channel/blend equation and additional pixel variants | Low | Decoded and associated across all 17 courses without claiming visual parity |
| Type 11 visibility curtains | Known 184-byte record, bounds and sampled cubic curve | Purple debug lines | Curtain surface semantics and flags | Low | 1–28 curtains per course |
| Type 12 collision | Framed and preserved | Not rendered | Full topology/material mapping | Low | Present across audited courses |
| Type 13 sound triggers | Framed and preserved | Not rendered | Header, variable arrays, spatial/action records | Low | Present across audited courses |
| Type 14 AIP | Framed and preserved | Not rendered | Path graph structures | Low | Present across audited courses |
| Type 17 camera triggers | 28-byte header and first array of 72-byte spatial volume records | Red debug boxes | Actions, priorities, remaining arrays | Low | 101 volumes in the ABC1 assembly |
| Type 18 NIS tables | Object-reference table framing and valid-reference extraction | Inspector hierarchy | Reference roles and playback sequencing | Low | Enriched across all 17 courses |
| Type 22 avalanche | Empty per-track markers recognized | Inspector hierarchy | Non-empty animation payload in avalanche courses | Unknown | Large payload remains preserved without interpretation |
| Other SSB types | Source range, track/RID, size, 128-byte preview, diagnostics | Unknown Sections hierarchy | Particle programs, collision, scripts, missions, audio, avalanche animation | Unknown | Preserved without crashing |

The local regression suite parses all 17 playable course assemblies and asserts that each has terrain, props, models, and splines with zero structured parser errors. It also verifies complete Type-3 framing, prop classification, model-instance resolution, and linked texture previews.
