# TerraScale sync v1 MessagePack golden vectors

These fixtures pin the first publishable `terrascale.sync.v1.msgpack` wire
contract at version 1.0. They were hand-audited against the MessagePack byte
format and are not generated during tests by the SDK codec. Each fixture pins
the exact manifest SHA-256, so a stale corpus fails instead of silently testing
an older contract.

`message-vectors.json` contains one complete envelope for every current message
tag. The delta vector includes every result-operation discriminant. Tests decode
the committed bytes into the expected public values and require the encoder to
reproduce those bytes exactly. A separate test-only generic MessagePack writer,
which imports neither the SDK codec nor its runtime, independently reproduces
the bytes from hand-audited numeric arrays.

`hostile-vectors.json` contains a fixed rejection vector for every typed hostile
decoder boundary. The whole-message limit uses a committed repeated-byte recipe
instead of adding an 8 MiB binary fixture to the repository. The recipe still
specifies every byte and its exact count.

INT-3 must consume these same fixtures in Go. Replacing a vector requires a
reviewed protocol-version decision; runtime encode/decode round trips do not
replace this evidence.
