# CLI reference

Run commands with `dotnet run --project src/Mountainizer.Cli -- <command>` during development, or execute the built CLI.

```text
identify <iso>
list-files <iso>
extract-file <iso> <iso-file-path> <output-path>
extract <iso> <project-directory>
import <iso> <project-directory> <project-name>
list-levels <project-directory-or-project.json>
audit <project-directory-or-project.json> [--json <output>]
inspect <project> <course-or-area> [--json <output>]
dump-resource <project> <course-or-area> <type> <track> <resource-id> <output-file>
survey-resource <project> <type> [--header-bytes <0-4096>] [--json <output>]
dump-textures <project> <course-or-area> <output-directory>
export <project> <course-or-area> --format obj --output <directory>
```

`identify` hashes and identifies an ISO without modifying it. `list-files` lists the ISO filesystem, and `extract-file` copies one explicitly named file out for read-only investigation. `extract` and `import` create a cached Mountainizer project.

`list-levels` prints the 17 friendly playable courses first and the 49 technical SDB streaming areas afterward. A playable course code such as `ARA1` loads the complete Snow Jam assembly; a technical name such as `A_ARA1` loads only that raw area.

`audit` parses all 17 playable courses and emits a compact JSON acceptance report. It fails with exit code 2 if a course lacks core scene content, contains unresolved prop models or lightmaps, lacks decoded model texture previews, has invalid Type-14 property/event/link bounds, or reports a structured parser error.

Type-14 inspection includes AI/track counts, tagged-property values, event-type/runtime indices, bounded path-distance intervals, six-slot section entries, and resolved link positions, unit directions, and referenced path indices.

`inspect` reports geometry, material/reference resolution, navigation paths/events, particle/effect families, NIS script-object lookups, collision topology, debug structures, fallback resources, bounds, timings, warnings, and errors. Type-18 details include all 18 slots, occupancy, observed object role, resolved Type-3 name, executable runtime command IDs, and neutral missing-slot behavior. Type-16 table details compare six-slot set counts with same-track scene collections and profile every slot by occupancy, source/generated rail kind, spline role, surface, and set shape; slot 0 is labeled reserved/unused only as an observed corpus property. `dump-resource` copies the bounded, decompressed payload of a matching SSB resource to a file for read-only format research; if the assembled course contains multiple matches, the output path becomes a directory containing one file per match. `survey-resource` scans every SDB-referenced group once and reports the group, resource index, track/RID, payload size, and a bounded header preview for one resource type; `--header-bytes` defaults to 64 and is capped at 4,096. Neither command modifies the imported source data. `export` writes terrain and decoded prop instances to OBJ together with an MTL library and deduplicated PNG textures.

Exit codes are 0 for success, 1 for command/fatal errors, and 2 when parsing completed with structured errors.
