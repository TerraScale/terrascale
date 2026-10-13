-- MongoDB offline adapter; import contract v1 revision 3
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
CREATE TABLE public.mongodb_documents (
    id text NOT NULL PRIMARY KEY,
    document jsonb NOT NULL,
    provenance jsonb NOT NULL
);
COPY public.mongodb_documents (id, document, provenance) FROM stdin;
00000000002XDG9J5RSYDKXNZG	{"_id":{"$oid":"0123456789abcdef01234567"},"long":{"$numberLong":"9223372036854775807"},"decimal":{"$numberDecimal":"1234567890123456789012345678901234"},"money":{"$numberDecimal":"123.4500"},"date":{"$date":{"$numberLong":"1699162200123"}},"binary":{"$binary":{"base64":"AP9cCQo=","subType":"80"}},"timestamp":{"$timestamp":{"t":4294967295,"i":0}},"nested":{"items":[null,true,{"$numberInt":"-2147483648"},{"x.y":"caf\\u00e9 \\ud83d\\ude42"}]},"nullable":null}	{"adapter":"mongodb-ejson-v1","sourceFormat":"mongoexport-canonical-ejson-v2-jsonl","sourceSha256":"a908269bed392ccf8cbfc4552e4cc23d6675ff3d7d2eedb84f148a4b6aae81d0","database":"synthetic","collection":"records","sourceRecord":1}
0000000000JKTPPZNSRZJ1J8KW	{"_id":{"$numberLong":"-9223372036854775808"},"tiny":{"$numberDecimal":"1E-6176"},"huge":{"$numberDecimal":"9.999999999999999999999999999999999E+6144"},"beforeEpoch":{"$date":{"$numberLong":"-1"}},"extremeDate":{"$date":{"$numberLong":"9223372036854775807"}},"negativeZero":{"$numberDouble":"-0.0"},"subnormal":{"$numberDouble":"5E-324"},"nan":{"$numberDouble":"NaN"},"infinity":{"$numberDecimal":"Infinity"},"emptyBinary":{"$binary":{"base64":"","subType":"00"}}}	{"adapter":"mongodb-ejson-v1","sourceFormat":"mongoexport-canonical-ejson-v2-jsonl","sourceSha256":"a908269bed392ccf8cbfc4552e4cc23d6675ff3d7d2eedb84f148a4b6aae81d0","database":"synthetic","collection":"records","sourceRecord":2}
000000000016Q6JQY6W5C9875T	{"_id":"literal:\\\\N\\t\\n\\r\\\\.","text":"COPY public.x FROM PROGRAM 'false';\\n\\\\! false\\n\\\\.\\t\\\\N","array":[[],{},["a",null]],"notNull":"null","uuidBytes":{"$binary":{"base64":"AAECAwQFBgcICQoLDA0ODw==","subType":"04"}}}	{"adapter":"mongodb-ejson-v1","sourceFormat":"mongoexport-canonical-ejson-v2-jsonl","sourceSha256":"a908269bed392ccf8cbfc4552e4cc23d6675ff3d7d2eedb84f148a4b6aae81d0","database":"synthetic","collection":"records","sourceRecord":3}
\.
