# User guide

## Import an ISO

Run `Mountainizer.App.exe`, choose **File → Import SSX 3 ISO**, select your legally obtained image, then choose a parent folder for the project. Mountainizer hashes and indexes the ISO, checks for `SLUS_207.72`, extracts `DATA/WORLDS/BAM.BIG` and its world members, and writes `project.json`. The source ISO is always opened read-only.

An unknown revision is allowed in best-effort read-only mode and produces a diagnostic. Matching extracted files are reused when reopening a project.

## Choose a course

The selector lists the 17 playable courses with their real names, peak, discipline, and internal code: Snow Jam, Metro-City, R&B, Crow's Nest, Disfunktion, Happiness, Ruthless Ridge, Intimidator, Style Mile, Launch Time, Schizophrenia, Ruthless, Gravitude, Kick Doubt, Much-2-Much, Perpendiculous, and The Throne.

Each course is assembled from its event data plus the shared mountain, connector, and sky areas used by the game. Technical SDB areas remain available after the playable courses for reverse-engineering work.

Terrain and decoded props are visible by default. Splines, camera triggers, and visibility curtains can be enabled individually under **View** as cyan, red, and purple debug geometry. The hierarchy exposes every parsed item and all unsupported source-aware resources.

Viewport controls:

- Left mouse: select visible terrain, props, or enabled debug structures
- Right mouse: orbit around the track surface beneath the cursor
- Middle mouse: screen-scale-aware pan
- Wheel: adaptive zoom toward the track beneath the cursor
- Ctrl + wheel: precision zoom
- Shift + wheel: fast zoom
- WASD and Q/E: fly; hold Shift to move faster
- F: frame the selected item; when a prop is isolated, return to the complete scene
- Escape: clear selection
- Double-click a prop in the hierarchy: isolate and frame that prop

Use **View → Frame scene** whenever you want to frame the complete course. The fixed red/green/blue gizmo in the viewport corner shows the world X/Y/Z axes. Use the **Assets** tab or select a texture in the hierarchy to preview decoded textures. Use **View** to toggle terrain, props/models, splines, triggers, visibility curtains, wireframe, backface culling, and the grid.

## Export

Choose **File → Export scene OBJ**. The resulting OBJ contains tessellated terrain plus every decoded prop instance with its world transform, normals, and UVs. Materials and decoded texture images are not yet emitted as MTL files.

## Privacy and copyright

Imported projects contain extracted copyrighted data and must not be shared or committed. `.gitignore` blocks known game-data extensions, but contributors remain responsible for checking changes before publishing.
