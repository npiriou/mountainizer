# Coding conventions

- Nullable reference types and implicit usings remain enabled.
- On-disk integers use explicit widths and endianness-named reader methods.
- Validate before slicing, seeking, allocating or converting to `int`; cap untrusted collections.
- Raw framing stays separate from `Mountainizer.Core` scene conversion.
- Do not catch and discard parser exceptions. Add a structured diagnostic with file, section and offset.
- Preserve unknown resources and original ordering. Show unknown numeric values in hexadecimal when practical.
- Store physical source ranges and distinguish decompressed logical offsets.
- Parser state is per operation and cancellation-aware where work may be long.
- Rendering/export must never read source-format structures directly.
- Do not add writing/repacking paths in the read-only milestone.
