# CLI reference

Run commands with `dotnet run --project src/Mountainizer.Cli -- <command>` during development, or execute the built CLI.

```text
identify <iso>
list-files <iso>
extract-file <iso> <iso-file-path> <output-path>
extract <iso> <project-directory>
import <iso> <project-directory> <project-name>
list-levels <project-directory-or-project.json>
inspect <project> <course-or-area> [--json <output>]
dump-textures <project> <course-or-area> <output-directory>
export <project> <course-or-area> --format obj --output <directory>
```

`identify` hashes and identifies an ISO without modifying it. `list-files` lists the ISO filesystem, and `extract-file` copies one explicitly named file out for read-only investigation. `extract` and `import` create a cached Mountainizer project.

`list-levels` prints the 17 friendly playable courses first and the 49 technical SDB streaming areas afterward. A playable course code such as `ARA1` loads the complete Snow Jam assembly; a technical name such as `A_ARA1` loads only that raw area.

`inspect` reports geometry, material/reference resolution, debug structures, unknown resources, bounds, timings, warnings, and errors. `export` writes terrain and decoded prop instances to OBJ together with an MTL library and deduplicated PNG textures.

Exit codes are 0 for success, 1 for command/fatal errors, and 2 when parsing completed with structured errors.
