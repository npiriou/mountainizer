# Contributing

Keep changes small, bounded, and traceable. Parser changes should include a synthetic test, diagnostics for malformed input, source byte ranges, and an update to `docs/format-status.md`. Do not commit game files, extracted assets, hashes that identify private user paths, or generated exports.

Use C# nullable reference types, explicit-width on-disk values, explicit endianness, checked arithmetic at trust boundaries, and no global parser state. Treat hypotheses as hypotheses. GPL or unlicensed reference code must not be copied into this MIT repository.

Before submitting a change, run `dotnet build Mountainizer.sln -c Release` and `dotnet test Mountainizer.sln -c Release`.
