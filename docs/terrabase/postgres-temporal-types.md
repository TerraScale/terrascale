# PostgreSQL temporal values

Native `date` (OID 1082), `timestamp` (1114), and `timestamptz` (1184) support text and binary binds/results. Binary
values use the 2000-01-01 epoch: signed big-endian int32 days or int64 microseconds, exactly 4 or 8 bytes respectively.
NULL stays NULL. The finite Gregorian range is **0001-01-01 through 9999-12-31** for dates and **0001-01-01
00:00:00.000000 through 9999-12-31 23:59:59.999999** for timestamps (UTC for `timestamptz`). Infinity, BC, year zero,
invalid dates, and out-of-range values return `22P02`. This is a .NET subset, not PostgreSQL's full temporal range. Text
dates use `yyyy-MM-dd`; timestamps use `yyyy-MM-dd HH:mm:ss` (or `T`), with optional 1–6 digit fractional seconds. Valid
offsets are `Z`, `±HH`, or `±HH:mm`, at most ±14:00. `timestamp` validates and ignores the offset, preserving its clock.
`timestamptz` normalizes to UTC; omitted offsets mean UTC. UTC conversion must fit the finite range. Output has six
fractional digits, plus `+00` for `timestamptz`. Extra precision is refused. Session `TimeZone` and `DateStyle` do not
alter formats. Npgsql maps .NET MinValue/MaxValue to infinity by default; use finite ISO text for exact endpoints.
Validation applies to typed temporal binds and result columns; text-bound SQL writes can store invalid text, which fails
when read as temporal.
