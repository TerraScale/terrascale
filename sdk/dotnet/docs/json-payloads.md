# JSON request encoding and byte limits

TerraBase JSON request bodies preserve Unicode scalar values in property names and
values, including C1, DEL, U+2028/U+3000, CJK and emoji. Optional Unicode escapes
from the .NET encoder or a previously escaped GET response are compacted to UTF-8.
Quotes, backslashes and C0 controls keep the escapes required by JSON. Literal
backslash-u text remains text, and numeric tokens retain their original spelling.
Unpaired escaped surrogates are invalid. No normalization or string replacement
is introduced by compaction.

The HTTP writer still uses generated JsonTypeInfo metadata, omission rules and
converters. It compacts generated output as a stream, with a fixed 64 MiB final
serialization-buffer guard and no caller tuning option. This guard does not raise
producer admission: documents remain 1 MiB, transaction requests 4 MiB and declared
schemas 64 KiB. Transactions retain per-document and captured-change limits too;
passing HTTP request admission does not prove a transaction can commit.

Create, Put, both Patch forms, declared schema and nested transaction documents
use this same HTTP writer. Media types, bearer authority, roles, preconditions,
errors, cancellation and single-dispatch behavior remain unchanged. These bytes
are API JSON, never HTML: applications must still encode values for their output
context. The dashboard continues through the SDK.

Owning producer `TerraScale/terrascale@1d823d8534fc5035859a47e263691d0c7cb7b744`; unchanged admission sources:

| Source | SHA256 |
| --- | --- |
| `src/TerraBase/Features/Documents/RequestJson.cs` | `2f3d372224a665fa3a57e2d356f92fd0e871e7d2642db000dce69aa893fa07ac` |
| `src/TerraBase/Features/Documents/DocumentRoute.cs` | `6add2f7cc95edd8ddb54be992ffc2999ec22e05d91d1837c1f3643e791d2b6e8` |
| `src/TerraBase/Engine/CollectionSchemaValidation.cs` | `ab0e77b346bfe8f3486a7471393b5dac3feef4f902ac264e58e68b70bfa3c18b` |

HTTP-fixture admission and loopback HTTP results are local evidence. Integrated
consumer and owned production round trips remain separate qualification gates.
NuGet publication remains held; use immutable source or a local pack.
