# User guide

## Import an ISO

Run `Mountainizer.App.exe`, choose **File → Import SSX 3 ISO**, select your legally obtained image, then choose a parent folder for the project. Mountainizer hashes and indexes the ISO, checks for `SLUS_207.72`, extracts `DATA/WORLDS/BAM.BIG` and its world members, and writes `project.json`. The source ISO is always opened read-only.

An unknown revision is allowed in best-effort read-only mode and produces a diagnostic. Matching extracted files are reused when reopening a project.

## Choose a course

The selector lists the 17 playable courses with their real names, peak, discipline, and internal code: Snow Jam, R&B, Metro-City, Crow's Nest, Disfunktion, Happiness, Ruthless Ridge, Intimidator, Style Mile, Launch Time, Schizophrenia, Ruthless, Gravitude, Kick Doubt, Much-2-Much, Perpendiculous, and The Throne.

Each course is assembled from its event data plus the shared mountain, connector, and sky areas used by the game. Technical SDB areas remain available after the playable courses for reverse-engineering work.

Terrain and decoded visual props are visible by default. Named non-visual reset planes, gameplay volumes, trigger meshes, ride-state meshes, streaming/effect markers, proxies, and collision walls are classified with a visible reason in the property inspector. Use **View → Prop / model categories** to toggle each category independently. The same menu provides **Hide selected prop**, **Show only selected prop type**, **Show all prop types**, and **Unhide all props**. The hierarchy category selector filters prop entries by the same classification while the text box filters by name. Splines and decoded AIP navigation paths share the cyan debug-line toggle; camera triggers and visibility curtains can be enabled separately as red and purple geometry. Decoded collision topology, radar/minimap routes, Type-4 particle models, Type-5 emitters, Type-6 lights, Type-7 halos, Type-15/16 structured tables, Type-17 camera-trigger tables/actions, Type-18 NIS script-object lookups, `BNKl` banks, and avalanche block streams are listed in dedicated hierarchy categories for inspection but are not rendered as their original runtime effects.

The supported v1 viewport is diffuse-only. Decoded Type-10 lightmap atlases can be inspected in **Assets**; their retail equation is now known to be source-alpha-scaled subtraction, `(Cd - Cs) * As`, but that PS2 GS path is not yet simulated in the viewport.

Viewport controls:

- Left mouse: select the exact visible terrain or prop triangle under the cursor, or an enabled debug structure
- Right mouse: free look without moving the camera
- Middle mouse: screen-scale-aware pan
- Wheel: dolly without changing the viewing direction
- Ctrl + wheel: precision dolly
- Shift + wheel: fast dolly
- WASD and Q/E: move in camera space; hold Shift for a 4x speed boost
- Arrow keys: rotate the camera
- F: frame the selected item; when a prop is isolated, return to the complete scene
- Escape: clear selection
- Double-click a prop in the hierarchy: isolate and frame that prop

Use **View → Frame scene** whenever you want to frame the complete course. The fixed red/green/blue gizmo in the viewport corner shows the world X/Y/Z axes. The **Assets** preview follows selected textures, terrain patches, materials, models, and prop instances and shows the first linked decoded texture. Use **View** to toggle terrain, individual prop/model categories, splines, triggers, visibility curtains, wireframe, backface culling, and the grid.

## Export

Choose **File → Export scene OBJ**. The resulting OBJ contains tessellated terrain plus every decoded prop instance with its world transform, normals, and UVs. Mountainizer also writes a companion MTL library and a sibling texture folder containing each referenced decoded diffuse texture as a deduplicated RGBA PNG. Keep those companion files beside the OBJ when importing it into Blender or another tool.

OBJ normals use the inverse-transpose transform required for non-uniformly scaled instances. Lightmaps and unresolved PS2 material flags are intentionally not included in the v1 export contract.

## Privacy and copyright

Imported projects contain extracted copyrighted data and must not be shared or committed. `.gitignore` blocks known game-data extensions, but contributors remain responsible for checking changes before publishing.
