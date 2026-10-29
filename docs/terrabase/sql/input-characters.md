# SQL input characters

Decoded SQL text containing U+FFFF is rejected with `SQLITE_ERROR` and
`U+FFFF is not supported in SQL text`, including strings, quoted names and comments.
Validation precedes tokenization for HTTP SQL and Hrana execute/describe/sequence;
a rejected HTTP batch or sequence applies none of its statements.
