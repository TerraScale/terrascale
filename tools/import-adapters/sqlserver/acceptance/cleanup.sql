-- UNRUN, DESTRUCTIVE: request explicit owner approval for this exact disposable table first.
-- Keep evidence/captures; never drop a database or alter a source snapshot.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 5000;
IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'$(FixtureDatabase)' OR LEN(DB_NAME()) <> 50
   OR LEFT(DB_NAME(), 24) COLLATE Latin1_General_100_BIN2 <> N'ts_sqlserver_acceptance_'
   OR RIGHT(DB_NAME(), 26) COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9a-hjkmnp-tv-z]%'
   OR SUBSTRING(DB_NAME(), 25, 1) COLLATE Latin1_General_100_BIN2 NOT LIKE N'[0-7]'
    THROW 51000, 'Disposable fixture database guard failed', 1;
IF OBJECT_ID(N'dbo.sqlserver_acceptance', N'U') IS NULL
    THROW 51000, 'Owned fixture table is absent; do not widen cleanup', 1;
IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
    WHERE class = 1 AND major_id = OBJECT_ID(N'dbo.sqlserver_acceptance') AND minor_id = 0
      AND name = N'terrascale_acceptance_database' AND CONVERT(nvarchar(128), value) = DB_NAME())
    THROW 51000, 'Owned fixture marker mismatch', 1;
BEGIN TRANSACTION;
IF (SELECT COUNT_BIG(*) FROM dbo.sqlserver_acceptance WITH (TABLOCKX, HOLDLOCK)) <> 4
    THROW 51000, 'Fixture changed; inspect before cleanup', 1;
DROP TABLE dbo.sqlserver_acceptance;
COMMIT TRANSACTION;
