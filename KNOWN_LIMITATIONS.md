# Known limitations

- Only the PS2 NTSC-U `SLUS_207.72` disc layout has been fully tested. Other revisions import in clearly marked best-effort read-only mode.
- ISO9660 is supported; UDF-only, multi-track, and unusual multi-extent images are not.
- The 17 playable courses are assembled from observed SDB event, shared-mountain, connector, and sky-area naming. This has been regression-tested across the supplied NTSC-U data, but the game runtime's conditional streaming rules are not completely decompiled.
- Terrain diffuse materials are rendered. Lightmaps, several material flags, and some PS2 texture formats remain incomplete.
- Splines and visibility curtains are decoded as sampled debug curves. Their gameplay roles and remaining fields are not fully named.
- Type-17 camera-trigger bounds are rendered as debug boxes. Trigger actions, priorities, and the other arrays in the trigger table remain unknown. Type-13 sound-trigger payloads remain source-aware unknown resources.
- Collision, AIP/path graphs, particles, lights, scripts, mission data, audio banks, and avalanche animation are preserved and reported but not rendered.
- The coordinate conversion boundary maps the observed SSX 3 Z-up data into Mountainizer's Y-up scene space as `(x, y, z) → (x, z, -y)`. Units, canonical winding, and handedness still need wider revision/platform validation.
- Viewport picking uses decoded object bounds and may need a second click in densely overlapping geometry. Detachable/persistent docking is not implemented.
- OBJ export includes terrain and decoded prop geometry, but does not emit MTL files or textures. glTF is not implemented.
- The development executable requires the .NET 8 Desktop Runtime. Installer signing and self-contained release packaging are future release work.
