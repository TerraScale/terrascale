-- sqlserver-jsonl-v1; offline COPY artifact; see provenance.json
CREATE TABLE "dbo"."orders" (
    "id" bigint NOT NULL,
    "amount" numeric(38,8) NOT NULL,
    "wallet" numeric(19,4) NOT NULL,
    "small" numeric(10,4) NOT NULL,
    "occurred" text NOT NULL,
    "offset" text NOT NULL,
    "guid" uuid NOT NULL,
    "note" text,
    "payload" bytea,
    "active" boolean,
    "calculated" numeric(10,2) NOT NULL,
    "padded" text NOT NULL,
    "clock" text NOT NULL,
    "day" date NOT NULL,
    PRIMARY KEY ("id")
);
COPY "dbo"."orders" ("id", "amount", "wallet", "small", "occurred", "offset", "guid", "note", "payload", "active", "calculated", "padded", "clock", "day") FROM stdin;
9223372036854775807	123456789012345678901234567890.12345678	922337203685477.5807	-214748.3648	2024-02-29T23:59:59.1234567	2024-02-29T23:59:59.7654321-03:00	00112233-4455-6677-8899-aabbccddeeff	Olá 😀 \t\n\r \\N \\.	\\x00ff800102	t	1234.50	A   	12:34:56.1234567	0001-01-01
-9223372036854775808	-0.00000000	-922337203685477.5808	214748.3647	2024-02-29T23:59:59.1234567	2024-02-29T23:59:59.7654321-03:00	00112233-4455-6677-8899-aabbccddeeff		\\x	f	1234.50	A   	12:34:56.1234567	0001-01-01
0	123456789012345678901234567890.12345678	922337203685477.5807	-214748.3648	2024-02-29T23:59:59.1234567	2024-02-29T23:59:59.7654321-03:00	00112233-4455-6677-8899-aabbccddeeff	\N	\N	\N	1234.50	A   	12:34:56.1234567	0001-01-01
\.
