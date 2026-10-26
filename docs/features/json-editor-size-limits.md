# Document and schema editor sizes

Document and patch submissions are normalized to compact UTF-8 JSON before the dashboard's 1 MiB check. JSON whitespace and optional Unicode escapes do not consume that compact budget; required JSON escapes still do. The producer additionally enforces 4,096 values, 16 nested containers, valid Unicode, unique member names and its number contract. A patch must also leave a valid bounded resulting document.

Declared schemas use the producer's 64 KiB compact JSON budget, not the former editor-only 48 KiB reservation. The producer also enforces its supported Draft 2020-12 vocabulary, 4,093 values and schema depth of 15. Validation applies to future local writes; it does not repair historical documents.

Display drafts keep JSON escapes for C1 controls to avoid HTML5 character remapping, and Razor still HTML-encodes them. Write normalization is separate from display formatting. Bounded document forms allow 6 MiB plus 16 KiB including browse context; schema forms allow 192 KiB of presentation plus 4 KiB overhead. Oversized requests remain rejected; parsed drafts survive validation and producer errors.

Writes still use the existing authorized SDK with CSRF protection and document version preconditions. SDK 0.0.12 can re-expand blocklisted Unicode on the wire (TS-1327); a locally accepted compact draft can therefore still receive the producer's 413. The presentation fix does not bypass that SDK dependency or change the data API's byte admission contract.
