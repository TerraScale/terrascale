--
-- PostgreSQL database dump
--

\restrict OGdk03hugjByhZwkyGipVwQQbee9EEN814IpKhoCpe1C0BBq2FJrISYCCH93OJf

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

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: readings; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.readings (
    id integer NOT NULL,
    at timestamp with time zone,
    day date,
    value double precision
);


--
-- Data for Name: readings; Type: TABLE DATA; Schema: public; Owner: -
--

COPY public.readings (id, at, day, value) FROM stdin;
1	2025-12-31 21:00:00-03	2026-01-01	1
2	infinity	2026-01-02	2
3	2026-01-02 21:00:00-03	0044-03-15 BC	3
4	2026-01-03 21:00:00-03	2026-01-04	NaN
5	2026-01-04 21:00:00-03	2026-01-05	1e+308
\.


--
-- Name: readings readings_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.readings
    ADD CONSTRAINT readings_pkey PRIMARY KEY (id);


--
-- PostgreSQL database dump complete
--

\unrestrict OGdk03hugjByhZwkyGipVwQQbee9EEN814IpKhoCpe1C0BBq2FJrISYCCH93OJf

