# Reference analysis

Analysis was performed on 2026-07-13 at the pinned commits below. No source from GPL or unlicensed references was copied into Mountainizer.

| Reference | Examined areas | Reuse decision | License status and risk |
|---|---|---|---|
| [Replanetizer](https://github.com/RatchetModding/Replanetizer) (`be5d1e9`) | `LibReplanetizer` separation, renderer organization, level frame, selection, GLTF/OBJ exporters, camera controls | Architectural reference only. Mountainizer independently defines its scene model, renderer, and parser boundary. | GPL-3.0-or-later. Copying would impose GPL obligations incompatible with Mountainizer's MIT decision. It also encodes Ratchet & Clank/PS3 assumptions and cannot supply SSX formats. |
| [ssxdecomp/ssx3](https://github.com/ssxdecomp/ssx3) (`9cd4626`) | NTSC-U target identity, symbols/source tree, runtime-oriented investigation | Facts and naming clues only; validate every claim against data. | No repository license was present at the inspected commit, so code is all-rights-reserved by default. The matching executable SHA-1 documented there applies to the executable, not the disc image. Decompiled names and types are provisional. |
| [npiriou/ssx3](https://github.com/npiriou/ssx3) (`b74d436`) | `archive_big.cpp`, asset index, guest-memory and telemetry/debug UI patterns | Behavioral cross-check for BIG mixed endianness and diagnostics only. Mountainizer's BIG implementation was written independently and tested against synthetic and user-local files. | No top-level license was present. Documentation-only reference. Physics/rehosting and D3D11/guest-memory assumptions are outside this milestone. |
| [SSX-Library](https://github.com/GlitcherOG/SSX-Library) (`8ded7f9`) | SSX3 PS2 SSB/SDB/PSM/PHM handlers, `WorldPatch`, MDR, spline, visibility, camera trigger and texture handlers | Format map and hypotheses only. The terrain decoder uses independently expressed bounds checks and coefficient math. No runtime dependency. | No license file or grant was present. Direct copying or redistribution is not permitted without clarification. Several handlers are marked unfinished, use unchecked stream reads, and mix extraction, JSON and parsing concerns. |
| [SSX Collection Multitool](https://github.com/GlitcherOG/SSX-Collection-Multitool) (`54594b6`) | BIG archive workflow and SSX3 project window | Workflow reference only; no runtime dependency or copied implementation. | GPL-3.0. Windows Forms UI is tightly coupled to SSX-Library and includes writing/repacking paths that are explicitly out of scope. |
| [refpack-rs](https://github.com/actioninja/refpack-rs) (`ca5d751`) | Public RefPack command/header documentation and safety behavior | The documented bit layout was used as a format specification. Mountainizer contains an independent, read-only bounded C# decoder. | MPL-2.0. No crate source is vendored or translated. If code is later incorporated, MPL notices and file-level source obligations must be followed. |

## Confirmed pipeline from the supplied user-local disc

The tested ISO is volume `SSX3`, contains 181 ISO9660 entries and `SLUS_207.72`, and has SHA-256 `3c2f8eb182c9c6208a6e8172a41e61c98f420abe3f42c845f6829aeb9761ebf5`. `DATA/WORLDS/BAM.BIG` uses `BIGF` with a little-endian archive-size field and big-endian table fields. Its five entries include `bam.sdb`, `bam.ssb`, `bam.phm`, `bam.psm`, and `serial.txt`.

SSB contains `CBXS`/`CEND` outer blocks. Each payload is RefPack (`10 FB` header); decompressed groups contain 8-byte resource headers: type byte, little-endian 24-bit payload length, track byte, and little-endian 24-bit resource ID. SDB identifies 49 areas and 183 chunks. Area `A` maps to groups 2–3; group 2 contains 1,209 resources including 291 type-1 terrain patches.

## Incompatibilities and risks

- Replanetizer targets different games and PS3 data; only UI/renderer organization transfers conceptually.
- The decompilation and rehost model live runtime data, while Mountainizer reads compressed disc resources. Addresses and in-memory pointers are not on-disk offsets.
- Existing SSX tools commonly combine parsing with extraction or serialization and trust counts. Mountainizer separates raw framing, scene conversion, rendering, and export and rejects unsafe sizes.
- SDB location-to-group association is non-obvious: the tested data associates the next location's metadata-chunk count with the current location span. This is documented in code and should be revisited with another revision.
- Existing names such as “patch,” “MDR,” and resource type meanings are working terminology, not guaranteed original EA names.
