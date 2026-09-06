`causal-json-v1.dict` is a 4,096-byte Zstandard dictionary (dictionary ID
44001), trained with zstd 1.5.7 on 1,024 synthetic canonical JSON documents.
The corpus contains sorted-key orders, events, profiles, and inventory records;
sequential document identifiers; bounded prices and quantities; gru/iad/fra
origins and causal contexts; sample addresses and public schema metadata.
No customer documents, credentials, or downloaded dictionaries are inputs.

Training parameters: `zstd --train samples/*.json --maxdict=4096 --dictID=44001`.
The dictionary bytes are versioned in the repository. Every compressed group
carries their full BLAKE3 hash and its decoded length. Receivers reject another
dictionary version and refuse decoded groups beyond 64 MiB before decompressing.
