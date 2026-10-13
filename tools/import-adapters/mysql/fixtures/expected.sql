SET client_encoding = 'UTF8';
CREATE TABLE public."Line ""Items""" (
    "order" text NOT NULL,
    "part:id" text NOT NULL,
    "a`b" bigint NOT NULL
);
COPY public."Line ""Items""" ("order", "part:id", "a`b") FROM stdin;
18446744073709551615	x:%	1
\.
CREATE TABLE public."orders" (
    "id" text NOT NULL,
    "amount" numeric NOT NULL,
    "label" text NOT NULL,
    "note" text,
    "raw" bytea NOT NULL,
    "optional_raw" bytea,
    "day" date NOT NULL,
    "local_time" timestamp NOT NULL,
    "instant" timestamptz NOT NULL,
    "state" text NOT NULL,
    "tags" text NOT NULL,
    "duration" text NOT NULL
);
COPY public."orders" ("id", "amount", "label", "note", "raw", "optional_raw", "day", "local_time", "instant", "state", "tags", "duration") FROM stdin;
18446744073709551615	12345678901234567890123456789012345.123456789012345678901234567890	Olá 😀\tline\nquote' slash\\	\\N	\\x000a5cff	\\x	2024-02-29	2024-02-29 12:34:56.123456	2024-02-29 12:34:56.123456+00:00	paid	red,blue	-838:59:59.123456
0	-0.000000000000000000000000000000		\N	\\xffffffff	\N	1000-01-01	1000-01-01 00:00:00.000000	1970-01-01 00:00:01.000000+00:00	a'b		00:00:00.000000
\.
ALTER TABLE ONLY public."Line ""Items""" ADD PRIMARY KEY ("order", "part:id");
ALTER TABLE ONLY public."Line ""Items""" ADD CONSTRAINT "item_unique" UNIQUE ("part:id");
ALTER TABLE ONLY public."Line ""Items""" ADD CONSTRAINT "item_order" FOREIGN KEY ("order") REFERENCES public."orders" ("id");
ALTER TABLE ONLY public."orders" ADD PRIMARY KEY ("id");
