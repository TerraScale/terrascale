--
-- PostgreSQL database dump
--

\restrict mEMluNi34ddZ5okwHbvAqvSjv3wclfOhjhkBEMCpqjvtsVJ8Vbx5DOiYVdIdfSo

-- Dumped from database version 17.11
-- Dumped by pg_dump version 17.11

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: sales; Type: SCHEMA; Schema: -; Owner: -
--

CREATE SCHEMA sales;


--
-- Name: mood; Type: TYPE; Schema: public; Owner: -
--

CREATE TYPE public.mood AS ENUM (
    'sad',
    'ok',
    'happy'
);


--
-- Name: touch(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.touch() RETURNS trigger
    LANGUAGE plpgsql
    AS $$ BEGIN NEW.seen_at := now(); RETURN NEW; END; $$;


SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: users; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.users (
    id uuid NOT NULL,
    org_id bigint NOT NULL,
    email character varying(200) NOT NULL,
    "Display Name" text,
    active boolean DEFAULT true NOT NULL,
    balance numeric(14,4),
    score double precision,
    ratio real,
    small smallint,
    mood public.mood,
    born date,
    seen_at timestamp without time zone,
    created_at timestamp with time zone NOT NULL,
    profile jsonb,
    settings json,
    avatar bytea,
    wake time without time zone,
    span interval,
    ip inet,
    flags bit varying(8),
    CONSTRAINT users_score_check CHECK ((score >= (0)::double precision))
);


--
-- Name: TABLE users; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.users IS 'synthetic fixture users; it''s fine';


--
-- Name: active_users; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.active_users AS
 SELECT id,
    email
   FROM public.users
  WHERE active;


--
-- Name: events; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.events (
    at timestamp with time zone NOT NULL,
    kind text NOT NULL,
    payload jsonb
);


--
-- Name: measurements; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.measurements (
    id integer NOT NULL,
    taken date NOT NULL,
    value numeric
)
PARTITION BY RANGE (taken);


--
-- Name: measurements_2025; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.measurements_2025 (
    id integer NOT NULL,
    taken date NOT NULL,
    value numeric
);


--
-- Name: measurements_2026; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.measurements_2026 (
    id integer NOT NULL,
    taken date NOT NULL,
    value numeric
);


--
-- Name: orgs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.orgs (
    id bigint NOT NULL,
    name text NOT NULL
);


--
-- Name: tagged; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.tagged (
    id integer NOT NULL,
    tags text[]
);


--
-- Name: Order Items; Type: TABLE; Schema: sales; Owner: -
--

CREATE TABLE sales."Order Items" (
    order_id integer NOT NULL,
    line integer NOT NULL,
    sku text NOT NULL,
    "unit.price" numeric NOT NULL
);


--
-- Name: measurements_2025; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public.measurements ATTACH PARTITION public.measurements_2025 FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');


--
-- Name: measurements_2026; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public.measurements ATTACH PARTITION public.measurements_2026 FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');


--
-- Data for Name: events; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.events (at, kind, payload) FROM stdin;
2026-10-11 21:00:00-03	login	{"ok": true}
2026-10-11 21:00:01-03	logout	\N
\.


--
-- Data for Name: measurements_2025; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.measurements_2025 (id, taken, value) FROM stdin;
1	2025-06-01	1.5
\.


--
-- Data for Name: measurements_2026; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.measurements_2026 (id, taken, value) FROM stdin;
2	2026-06-01	2.5
3	2026-07-01	\N
\.


--
-- Data for Name: orgs; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.orgs (id, name) FROM stdin;
1	Acme
2	Tab\tand\nnewline
9007199254740993	Big id
\.


--
-- Data for Name: tagged; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.tagged (id, tags) FROM stdin;
1	{a,b}
\.


--
-- Data for Name: users; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.users (id, org_id, email, "Display Name", active, balance, score, ratio, small, mood, born, seen_at, created_at, profile, settings, avatar, wake, span, ip, flags) FROM stdin;
5f0c8e9a-6a43-4c3e-9a7d-2d1f0c5e8b11	1	ada@example.com	Ada Lovelace	t	1234567890.1234	0.1	1.5	-32768	happy	1815-12-10	2026-10-12 08:30:00.123456	2026-10-12 05:30:00.123456-03	{"a": [1, 2, {"b": null}], "ü": "ß"}	{"z":1, "a":2}	\\x00ff10	23:59:59.999999	1 year 2 mons 3 days 04:05:06	192.168.0.1/24	101
0b8e7f6d-1c2a-4b3e-8f9a-0a1b2c3d4e5f	2	grace@example.com	\N	f	\N	1e+300	\N	\N	\N	\N	\N	2000-01-01 00:59:59-02	\N	\N	\N	\N	\N	\N	\N
a1b2c3d4-e5f6-4711-8899-aabbccddeeff	1	back\\slash@example.com	Quote " and \\ backslash	t	0.0001	12345.678	-0.25	7	sad	2000-02-29	2000-02-29 00:00:00	2000-02-29 03:30:00-03	[]	null	\\x	00:00:00	-1 days	::1	
\.


--
-- Data for Name: Order Items; Type: TABLE DATA; Schema: sales; Owner: -
--

COPY sales."Order Items" (order_id, line, sku, "unit.price") FROM stdin;
1	1	SKU-1	9.99
1	2	SKU:2%	0.000000000000000000000000001
2	1	SKU-3	123456789012345678901234567890.5
\.


--
-- Name: measurements measurements_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.measurements
    ADD CONSTRAINT measurements_pkey PRIMARY KEY (id, taken);


--
-- Name: measurements_2025 measurements_2025_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.measurements_2025
    ADD CONSTRAINT measurements_2025_pkey PRIMARY KEY (id, taken);


--
-- Name: measurements_2026 measurements_2026_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.measurements_2026
    ADD CONSTRAINT measurements_2026_pkey PRIMARY KEY (id, taken);


--
-- Name: orgs orgs_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.orgs
    ADD CONSTRAINT orgs_pkey PRIMARY KEY (id);


--
-- Name: tagged tagged_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.tagged
    ADD CONSTRAINT tagged_pkey PRIMARY KEY (id);


--
-- Name: users users_email_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_email_key UNIQUE (email);


--
-- Name: users users_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_pkey PRIMARY KEY (id);


--
-- Name: Order Items Order Items_pkey; Type: CONSTRAINT; Schema: sales; Owner: -
--

ALTER TABLE ONLY sales."Order Items"
    ADD CONSTRAINT "Order Items_pkey" PRIMARY KEY (order_id, line);


--
-- Name: users_org_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX users_org_idx ON public.users USING btree (org_id);


--
-- Name: measurements_2025_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.measurements_pkey ATTACH PARTITION public.measurements_2025_pkey;


--
-- Name: measurements_2026_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.measurements_pkey ATTACH PARTITION public.measurements_2026_pkey;


--
-- Name: users users_touch; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER users_touch BEFORE UPDATE ON public.users FOR EACH ROW EXECUTE FUNCTION public.touch();


--
-- Name: users users_org_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_org_id_fkey FOREIGN KEY (org_id) REFERENCES public.orgs(id);


--
-- PostgreSQL database dump complete
--

\unrestrict mEMluNi34ddZ5okwHbvAqvSjv3wclfOhjhkBEMCpqjvtsVJ8Vbx5DOiYVdIdfSo

