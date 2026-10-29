# MCP schema Unicode admission

Declared-schema MCP writes measure the same compact UTF-8 JSON as dashboard schema writes, with a 64 KiB limit. Optional Unicode escapes and whitespace do not reduce the available schema budget. Object, boolean, authorization and producer validation rules remain unchanged.

The HTTP integration journey submits both literal and escaped C1 descriptions that compact to 64,018 bytes, checks producer request size and schema readback, and rejects a C1 description exceeding 64 KiB after compaction. The producer retains authoritative syntax, depth, value-count and schema validation.
