# Architecture

Mountainizer is a one-way, read-only pipeline:

```text
user ISO -> ISO9660 index -> project extraction cache -> BIG index
         -> SDB course/area catalog + SSB bounded decompression/resource framing
         -> stable Core scene model -> OpenGL renderer / OBJ exporter / diagnostics UI
```

There is deliberately no serialization edge back toward the ISO.

Playable courses are catalog entries rather than single SDB locations. The format layer resolves each course to its event, shared-mountain, connector, and sky areas, scans their SSB groups in one pass, and consolidates streamed textures before handing one `MountainizerScene` to downstream consumers. Raw SDB areas remain independently loadable for technical inspection.

## Projects

- `Mountainizer.Core`: stable scene records for visible assets, particle/effect records, navigation paths and markers, radar routes, collision topology, structured/reference research tables, bounded bank/avalanche streams, source byte ranges, confidence and structured diagnostics. Parsed objects do not expose parser-owned streams.
- `Mountainizer.Formats`: span reader, BIG, RefPack, SDB, SSB framing, terrain conversion and mesh generation. Every count/offset is bounded before allocation or slicing.
- `Mountainizer.Iso`: ISO9660 read-only index/extraction, SHA-256 identification, versioned `project.json`, deterministic cache layout.
- `Mountainizer.Rendering`: OpenTK 4.9.4 OpenGL 3.3 renderer, camera, grid, textured terrain, cached GPU instance/material batches, per-instance frustum culling, category/instance visibility controls, two-stage bounds-plus-triangle picking, wireframe and hierarchy-driven selection highlight. It consumes `Core` only.
- `Mountainizer.Export`: independent OBJ export from `Core` meshes.
- `Mountainizer.Cli`: automation surface using the same libraries as the GUI.
- `Mountainizer.App`: Windows WPF inspection shell using OpenTK.GLWpfControl 4.3.6. Parsing/import run outside the UI thread and report progress.
- `Mountainizer.Tests`: committed synthetic fixtures plus opt-in local regressions against a user-imported NTSC-U project.

OpenTK was selected because it is maintained, MIT-licensed, provides direct OpenGL 3.3 access, and keeps rendering narrow. WPF is used for the first Windows milestone because its native file dialogs, tree/data views, accessibility and async UI integration avoid building an entire editor shell. The model and most parsers target plain `net8.0`; Linux can use a future UI host without changing them.

## Trust boundaries

ISO, BIG, RefPack and SSB data are untrusted. Readers validate integer ranges before allocation; cap directory depth, table counts, block size and decompressed group size; use checked conversions; and return diagnostics for independently skippable resources. A corrupt outer stream may prevent later group recovery and is reported as such.

Every resource retains its source file and compressed group range. Because SSB resource bytes are compressed, `LogicalOffset` records the decompressed group offset while `SourceOffset`/`SourceLength` identify the physical compressed group. Pretending a decompressed byte has a unique physical offset would be misleading.

## Coordinate boundary

All format-to-render conversion passes through `Ssx3Coordinates.ToMountainizer`. The tested terrain, prop transforms, and named start structures establish SSX 3 world data as Z-up. Mountainizer maps `(x, y, z)` to its Y-up scene space as `(x, z, -y)`. Units, canonical winding, and handedness still need wider revision/platform validation. No renderer-local coordinate fixes are allowed.

## UI and threading

The fixed workspace has resizable hierarchy, viewport, inspector, texture-preview, diagnostics and log panes. Terrain selection is initiated in the hierarchy and highlighted by a second line pass; decoded props can be isolated and framed. Import hashing and course parsing use background tasks; scene upload occurs on the OpenGL context's render thread. Static model geometry is uploaded once, repeated transforms are submitted as GPU instances, and the visible transform buffer is rebuilt only when the camera or visibility settings change.

## Project layout

```text
<project>/
  project.json
  source/
  extracted/DATA/WORLDS/{BAM.BIG,bam.sdb,bam.ssb,bam.phm,bam.psm}
  cache/
  logs/import-diagnostics.json
```

The source ISO remains outside the project and is opened read-only. Paths inside `extractedRoot` are relative; the ISO path is necessarily local metadata.
