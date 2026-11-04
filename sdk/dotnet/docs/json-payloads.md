# JSON request encoding

TerraBase JSON request bodies preserve Unicode scalar values in property names and
values, including C1, DEL, U+2028/U+3000, CJK and emoji. Optional Unicode escapes
from the .NET encoder are compacted to UTF-8. Quotes, backslashes and C0 controls
keep the escapes required by JSON. Literal backslash-u text remains text, and
numeric tokens retain their original spelling. Unpaired escaped surrogates are
invalid. No normalization or string replacement is introduced by compaction.

The HTTP writer uses generated JsonTypeInfo metadata, omission rules and
converters. It compacts generated output as a stream, with a fixed 64 MiB final
serialization-buffer guard and no caller tuning option. This guard does not raise
producer admission limits. Media types, bearer authority, errors, cancellation and
single-dispatch behavior remain unchanged. These bytes are API JSON, never HTML:
applications must still encode values for their output context.
