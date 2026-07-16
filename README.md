# Mountainizer

<img src="src/Mountainizer.App/Assets/mountainizer-icon-app.png" alt="Mountainizer icon" width="96">

Mountainizer is an open-source, read-only **SSX 3 (PlayStation 2) world inspector** for Windows. It imports a user-provided NTSC-U disc image, assembles all 17 playable courses, and renders their terrain, props, textures, splines, navigation paths, triggers, and visibility data in an interactive OpenGL viewport.

Mountainizer contains no SSX 3 assets. It never modifies or repacks the source image; you must supply your own legally obtained copy of the game.

![Mountainizer displaying the Snow Jam course](docs/mountainizer-snow-jam.jpg)

## Current features

- Imports and identifies the PS2 NTSC-U release (`SLUS_207.72`) from an ISO9660 image
- Presents all 17 courses by their in-game names, peak, discipline, and internal code
- Assembles each course from its event, shared-mountain, connector, and sky streaming areas
- Renders diffuse terrain plus decoded static props and models, using cached GPU instance batches and camera-frustum culling; decoded Type-10 lightmap atlases remain available for inspection
- Decodes all Type-1 terrain texture equations: primary source replacement, Type-10 source-alpha-scaled subtractive lightmapping `(Cd - Cs) * As`, and the optional secondary destination-alpha additive draw `(Cs - 0) * Ad + Cd`; all 30,644 patches and 623 unique RGBA32 lightmaps match the executable paths
- Decodes the Type-3 fixed transform/bounding-sphere/world-AABB/reference header, exact texture-bank subchunk selector, per-instance wind-deformation switch, and complete variable PS2 DMA/VIF tail, including model/instance `REF` rebasing, `RET` framing, SPR rewrite forms, framed VIF commands, per-instance packed RGBA5 vertex-color arrays, and runtime `MSCAL` placeholders
- Opens near the course's starting gate, facing downhill like the start of a race
- Provides Replanetizer-style free look and camera-relative fly controls, plus pan and dolly controls
- Browses the scene hierarchy, properties, source offsets, materials, and decoded textures, with linked texture previews for terrain, materials, models, and props
- Exposes Type-0's exact opaque/source-alpha primary blend choice and randomized per-model-use texture-variant selection
- Decodes variable Type-17 camera-trigger records, executable-verified ellipse/box containment transforms, volume/action classes, nested bounds and spline payloads, exact action blend timing, Switch Camera remapping, and Bounded/Spline camera-algorithm selection; Bounded actions expose typed distance, FOV, vertical/forward target offsets, pitch offset, and retail chase-camera equations, while Spline actions expose typed FOV, duration, offsets, fixed control times, sampled-speed construction, and arc-length-corrected motion equations; visualizes trigger volumes as optional debug geometry
- Decodes Type-12 version-1 collision topology, exact flat AABBs for consecutive ten-triangle batches, alignment/runtime scratch regions, and version-3 RLE octree child masks, dense node levels, radii, and symmetric positive-definite inverse matrix pairs for source-aware inspection
- Decodes Type-13 sound-trigger bindings with case-sensitive 64-bit MD5 name-prefix identities, nullable anchor-prop references, distinct shared/per-spatial trigger-info channels, the executable `WATRIG.ADL` catalog (named ambient audio, indexed bank sounds with known `MOUNTAIN`/`CROWD` and per-track Type-20 `TRACK_BANK_0/1` roles, plus crowd-instance activation), runtime-sized spatial descriptors, typed sphere/ellipsoid/cone geometry, all six executable falloff equations, and the `0xFF` end marker
- Decodes Type-15 World Painter banks with the executable's Mix/Ambience/Speech/Camera/Fog/LightGlow/ScreenTint/SkyBox/Sun/Surface/Lighting/Weather/Danger family map, exact branch/leaf spatial-index grammar, payload framing, shared blend-control word, named Fog/LightGlow/ScreenTint and most Weather controls, and corpus-correlated Mix/Ambience identifiers; Type-16 separately adds RailMan roots/six-slot sets, per-spline rail roles and surface types, dependency-resolved unary/binary rail-program inputs and generated-output descriptors, 13-family gameplay-modifier program groups, and multi-routine `LUN` bytecode with all 43 executable opcode handlers operationally classified, routine descriptors, native-call operands, direct names, and evidence-qualified subsystem correlations
- Decodes Type-21 radar/minimap course lines into 2D positions, unit lateral normals, cumulative distances, and Start/Checkpoint/Finish markers, including the executable cursor rule, cross-track projection, 10,000-unit HUD window, and retail `radarline`/`chkstart`/`chkpt` textures
- Decodes Type-20 EA `BNKl` slot tables and chained PS2 `PT` sound-info layers, including inclusive MIDI-note/velocity selection, the executable-verified root note, typed hundredths-of-a-second playback envelopes, and version-3 MicroTalk audio with loop-substream resets, per-frame PCM corrections, and PCM16/WAV output
- Decodes Type-22 avalanche streams into cumulative 30 Hz translation/rotation motion, interpolated scale, timed captured-target identities, and runtime-verified block/frame event schedules
- Decodes Type-4 static fog-sprite models into bounds and position/color/size elements, paired Type-5 world transforms and model references, including the exact `FOG`/`fog0` texture, depth fades, sorting, and PS2 source-alpha blend state; also decodes executable-verified Type-6 point/spot light parameters and equations plus the exact loader admission boundary that rejects Directional/Ambient and flag-`0x100` records, and Type-7 halo records with runtime-proven visual modes, occlusion probes, visibility state, normalized colors, final render scales, named textures, and exact PS2 source-alpha additive blending
- Decodes Type-14 AI/track navigation tables with executable-backed tagged properties, weighted path points and total lengths, path-distance event intervals, respawn flags, fixed section entries, and resolved position/direction links; empty resources remain typed navigation markers
- Decodes fixed 18-slot Type-18 NIS script-object tables, all executable command-to-slot routes, neutral missing-slot behavior, and 11 name-validated observed roles
- Hides non-visual reset planes, gameplay volumes, triggers, ride-state meshes, and collision walls by default, with a dedicated debug toggle
- Exports tessellated terrain and decoded prop instances to OBJ with an MTL library and deduplicated PNG textures
- Preserves source ranges and reports structured parsing diagnostics; the tested 17-course NTSC-U audit has zero generic fallback resources
- Includes a CLI for identification, extraction, inspection, texture dumping, and export

Mountainizer currently targets inspection and reverse engineering. It is not a level editor and cannot write game data back to an ISO.

## Requirements

- Windows 10 or Windows 11, x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source
- A legally obtained SSX 3 PS2 NTSC-U ISO for the fully tested workflow
- An OpenGL 3.3-capable graphics driver

Other disc revisions can be opened in best-effort, read-only mode, but have not been fully validated.

## Download

Download `Mountainizer.App.exe` from the [latest GitHub release](https://github.com/npiriou/mountainizer/releases/latest). It is a self-contained Windows x64 executable: no installer, ZIP archive, or separate .NET installation is required.

## Build and run

From the repository root:

```powershell
dotnet restore Mountainizer.sln
dotnet build Mountainizer.sln -c Release
dotnet run --project src/Mountainizer.App/Mountainizer.App.csproj -c Release
```

After building, the executable is located at:

```text
src\Mountainizer.App\bin\Release\net8.0-windows\Mountainizer.App.exe
```

Run the EXE from that directory and keep its companion DLLs and `runtimes` directory beside it. No ZIP or installer is required. The development build requires the .NET 8 Desktop Runtime.

## First run

1. Start `Mountainizer.App.exe`.
2. Choose **File → Import SSX 3 ISO**.
3. Select your legally obtained ISO.
4. Choose a parent directory and project name.
5. Select a course from the course menu after import completes.

Mountainizer creates a reusable local project cache. The source ISO remains read-only, and previously extracted files are reused when the project is reopened.

## Viewport controls

| Input | Action |
| --- | --- |
| Left mouse | Select the exact visible terrain or prop triangle under the cursor, or an enabled debug structure |
| Right mouse drag | Free look without moving the camera |
| Middle mouse drag | Pan with distance-aware sensitivity |
| Mouse wheel | Dolly forward or backward without changing the viewing direction |
| Ctrl + wheel | Precision dolly |
| Shift + wheel | Fast dolly |
| W/A/S/D | Move forward, left, backward, and right in camera space |
| Q/E | Move down and up in camera space |
| Arrow keys | Rotate the camera |
| Shift + movement | Move 4x faster |
| F | Frame the selected item, or return from an isolated prop to the course |
| Escape | Clear the selection |
| Double-click a prop | Isolate and frame that prop |

Choose **View → Frame scene** to frame the complete course. Visual props are enabled by default. The **View → Prop / model categories** submenu independently controls visual, collision, reset-plane, gameplay-volume, trigger, ride-state, streaming, effect, and proxy instances. The same menu can hide one selected prop, show only its type, restore hidden props, or enable all types.

## Supported courses

| Peak 1 | Peak 2 | Peak 3 |
| --- | --- | --- |
| Snow Jam — Race (`ARA1`) | Ruthless Ridge — Race (`CRA3`) | Gravitude — Race (`ERA5`) |
| R&B — Slopestyle (`ASS1`) | Intimidator — Race (`DRA4`) | Kick Doubt — Slopestyle (`ESS3`) |
| Metro-City — Race (`BRA2`) | Style Mile — Slopestyle (`DSS2`) | Much-2-Much — Big Air (`EBA3`) |
| Crow's Nest — Big Air (`ABA1`) | Launch Time — Big Air (`CBA2`) | Perpendiculous — Super Pipe (`EHP3`) |
| Disfunktion — Super Pipe (`BHP1`) | Schizophrenia — Super Pipe (`CHP2`) | The Throne — Backcountry (`EBC3`) |
| Happiness — Backcountry (`ABC1`) | Ruthless — Backcountry (`DBC2`) | |

The 49 raw SDB streaming areas also remain available for technical inspection.

## CLI

Run the CLI during development with:

```powershell
dotnet run --project src/Mountainizer.Cli -- <command>
```

Available commands:

```text
identify <iso>
list-files <iso>
extract-file <iso> <iso-file-path> <output-path>
extract <iso> <project-directory>
import <iso> <project-directory> <project-name>
list-levels <project-or-project.json>
audit <project-or-project.json> [--json <output>]
inspect <project> <course-or-area> [--json <output>]
dump-resource <project> <course-or-area> <type> <track> <resource-id> <output-file>
survey-resource <project> <type> [--header-bytes <0-4096>] [--json <output>]
dump-textures <project> <course-or-area> <output-directory>
export <project> <course-or-area> --format obj --output <directory>
```

For example, to list courses in an imported project and inspect Snow Jam:

```powershell
dotnet run --project src/Mountainizer.Cli -- list-levels "C:\Mountainizer\MyProject"
dotnet run --project src/Mountainizer.Cli -- inspect "C:\Mountainizer\MyProject" ARA1
```

See the [CLI reference](docs/cli.md) for command behavior and exit codes.

## Test

```powershell
dotnet test Mountainizer.sln -c Release
```

The normal suite uses synthetic fixtures and does not require copyrighted game data. To enable local regression tests against an imported project:

```powershell
$env:MOUNTAINIZER_TEST_PROJECT = "C:\Mountainizer\MyProject\project.json"
dotnet test src/Mountainizer.Tests/Mountainizer.Tests.csproj -c Release
```

Run the complete release gate, including the all-course local audit, with:

```powershell
.\tools\verify-release.ps1 -ProjectPath "C:\Mountainizer\MyProject\project.json"
```

Do not commit imported projects, extracted assets, or exports. Known game-data extensions are ignored, but always review changes before publishing.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Mountainizer.App` | WPF desktop application |
| `src/Mountainizer.Cli` | Command-line interface |
| `src/Mountainizer.Core` | Scene model, diagnostics, and source metadata |
| `src/Mountainizer.Formats` | BIG, RefPack, SDB, SSB, terrain, model, and texture parsing |
| `src/Mountainizer.Iso` | Read-only ISO indexing, identification, and project cache |
| `src/Mountainizer.Rendering` | OpenTK/OpenGL renderer and camera |
| `src/Mountainizer.Export` | OBJ export |
| `src/Mountainizer.Tests` | Synthetic and opt-in local regression tests |
| `docs` | Architecture, format status, usage, and research notes |

## Documentation

- [User guide](docs/user-guide.md)
- [CLI reference](docs/cli.md)
- [Architecture](docs/architecture.md)
- [Format support status](docs/format-status.md)
- [Known limitations](KNOWN_LIMITATIONS.md)
- [Reference analysis](docs/reference-analysis.md)
- [Release readiness and v1 scope](docs/release-readiness.md)
- [Contributing](CONTRIBUTING.md)

## License

Mountainizer is released under the [MIT License](LICENSE). SSX 3 and its assets are the property of their respective rights holders and are not included with this project.
