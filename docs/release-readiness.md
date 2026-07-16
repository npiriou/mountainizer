# Release readiness and v1 scope

## Decision

Mountainizer v1 is a read-only, diffuse-rendered inspector for the visible static world in the PS2 NTSC-U release. Its supported contract is ISO import, 17-course assembly, terrain and decoded static-prop rendering, source-aware inspection, debug visualization of decoded splines/navigation paths/triggers/visibility data, diffuse OBJ export, structured diagnostics, and CLI automation.

The following are explicitly outside the v1 completion boundary:

- editing, serialization, repacking, or modifying an ISO;
- unresolved non-bit-3 material state portions and unsupported texture variants; Type-0 opaque/source-alpha primary selection, Type-1 primary replacement, Type-10 subtractive lightmapping, and the optional destination-alpha additive secondary draw are decoded exactly, but the PS2-specific multipass paths are not all simulated in the supported viewport;
- gameplay simulation or full semantic decoding of collision, AIP, particle, light, audio, script, mission, and avalanche systems; Type-18 NIS object lookup is structurally/runtime decoded but its command names and higher-level sequences remain partial;
- glTF export, installer packaging, code signing, and non-NTSC-U platform/revision guarantees.

Collision, AIP, sound triggers, structured Type-15/16 tables, Type-20 banks, Type-21 radar routes, and Type-22 avalanche streams remain research-oriented rather than gameplay-complete. AIP tagged properties, path-distance intervals, respawn flags, section entries, and resolved links are now runtime-validated, but exact event behavior and several metadata/link roles remain future work alongside packed collision meanings, remaining rail/native semantics, interactive audio mixing/playback, and avalanche rendering/physics simulation. Type-21 cursor selection, lateral projection, 1P windowing, and core line/marker textures are runtime-verified; Type-22 transform interpolation and block-event cadence are runtime-verified; Type-20 MicroTalk itself decodes to PCM16/WAV.

## Automated release gates

`tools/verify-release.ps1` is the canonical gate. It requires:

- solution restore and a Release build with warnings treated as errors;
- all committed non-copyrighted tests passing with no skipped tests;
- a working CLI help entry point;
- a self-contained Windows x64 publish containing one `Mountainizer.App.exe` and no companion runtime files.

GitHub Actions runs this legal, reproducible subset on every push and pull request. A release candidate must additionally run:

```powershell
.\tools\verify-release.ps1 -ProjectPath "C:\Mountainizer\MyProject\project.json"
```

That enables the copyrighted-data tests and writes `artifacts/release-course-audit.json` through the CLI `audit` command.
It also launches the freshly published executable and verifies that all 17 courses reach the Ready state through the real desktop selector. Camera acceptance checks require scene geometry to remain in the central viewport for every course.

## NTSC-U acceptance criteria

For each of the 17 playable courses, the current parser must report:

- terrain, decoded props, decoded models, and splines;
- zero structured parser errors;
- zero prop instances with unresolved model geometry;
- zero decoded models without a diffuse texture preview;
- zero referenced terrain lightmaps without a decoded atlas.

The accepted courses are `ARA1`, `ASS1`, `BRA2`, `ABA1`, `BHP1`, `ABC1`, `CRA3`, `DRA4`, `DSS2`, `CBA2`, `CHP2`, `DBC2`, `ERA5`, `ESS3`, `EBA3`, `EHP3`, and `EBC3`.

## Current evidence

On 2026-07-16, the supplied `SLUS_207.72` image passed the complete canonical release gate at 90 tests with zero failures or skips. The run included the warnings-as-errors solution build, real all-course regressions, a fresh 17-course CLI audit with zero errors, zero audit issues, and zero generic fallback resources, the self-contained single-file Windows publish, and successful desktop selector loads for all 17 playable courses. Coverage includes exact SDB chunk/subchunk descriptor framing, Type-3 fixed-header/texture-bank/DMA-VIF command and per-vertex color validation, Type-12 version-1 triangle-batch and version-3 RLE/octree validation, all 33,026 anchored Type-13 name hashes, bounded Type-14 event/link references, executable-routed Type-18 slots, and the free-look camera's fixed-step dolly and high-speed camera-relative movement controls.

The diffuse-only boundary is intentional: Type-10 atlases, patch UV rectangles, and the exact `(Cd - Cs) * As` PS2 subtractive equation are decoded and resolvable, but that GS path is not yet simulated in the supported viewport. The older experimental multiplicative blend created false colored patch boundaries. Claiming visual parity would therefore be inaccurate.
