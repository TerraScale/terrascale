-- UNRUN. Fixed trusted fixture SQL only; exports are never executed by convert.py.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 5000;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'$(FixtureDatabase)' OR LEN(DB_NAME()) <> 50
   OR LEFT(DB_NAME(), 24) COLLATE Latin1_General_100_BIN2 <> N'ts_sqlserver_acceptance_'
   OR RIGHT(DB_NAME(), 26) COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9a-hjkmnp-tv-z]%'
   OR SUBSTRING(DB_NAME(), 25, 1) COLLATE Latin1_General_100_BIN2 NOT LIKE N'[0-7]'
    THROW 51000, 'Disposable fixture database guard failed', 1;
IF CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) <> 16
    THROW 51000, 'This acceptance fixture requires existing SQL Server 2022', 1;
IF (SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID()) <> 1
    THROW 51000, 'Snapshot isolation must already be available; do not change database options here', 1;
SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.sqlserver_acceptance', N'U') IS NULL
        THROW 51000, 'Owned fixture must be a base table', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.sqlserver_acceptance) <> 4
        THROW 51000, 'Snapshot must contain exactly four fixture rows', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
        WHERE class = 1 AND major_id = OBJECT_ID(N'dbo.sqlserver_acceptance') AND minor_id = 0
          AND name = N'terrascale_acceptance_database' AND CONVERT(nvarchar(128), value) = DB_NAME())
        THROW 51000, 'Owned fixture marker mismatch', 1;
    IF (SELECT COUNT(*) FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.sqlserver_acceptance') AND is_primary_key = 1) <> 1
        THROW 51000, 'Fixture primary key mismatch', 1;
    IF (SELECT COUNT(*) FROM sys.index_columns ic
        JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE i.object_id = OBJECT_ID(N'dbo.sqlserver_acceptance') AND i.is_primary_key = 1
          AND ic.key_ordinal > 0) <> 1
        THROW 51000, 'Fixture primary key width mismatch', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.index_columns ic
        JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'dbo.sqlserver_acceptance') AND i.is_primary_key = 1
          AND ic.key_ordinal = 1 AND c.name = N'id')
        THROW 51000, 'Fixture primary key column mismatch', 1;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.sqlserver_acceptance')) <> 17
        THROW 51000, 'Fixture schema mismatch', 1;
    IF EXISTS (
        SELECT c.name, TYPE_NAME(c.system_type_id), c.is_nullable, c.is_identity, c.is_computed,
               CASE WHEN TYPE_NAME(c.system_type_id) IN ('nvarchar', 'nchar', 'varbinary') THEN c.max_length ELSE 0 END,
               CASE WHEN TYPE_NAME(c.system_type_id) = 'decimal' THEN c.precision ELSE 0 END,
               CASE WHEN TYPE_NAME(c.system_type_id) IN ('decimal', 'datetime2', 'datetimeoffset', 'time') THEN c.scale ELSE 0 END
        FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.sqlserver_acceptance')
        EXCEPT
        SELECT * FROM (VALUES
            (N'id',N'bigint',0,1,0,0,0,0), (N'amount',N'decimal',0,0,0,0,38,8),
            (N'wallet',N'money',0,0,0,0,0,0), (N'small',N'smallmoney',0,0,0,0,0,0),
            (N'occurred',N'datetime2',0,0,0,0,0,7), (N'offset',N'datetimeoffset',0,0,0,0,0,7),
            (N'guid',N'uniqueidentifier',0,0,0,0,0,0), (N'note.名',N'nvarchar',1,0,0,-1,0,0),
            (N'payload',N'varbinary',1,0,0,-1,0,0), (N'active',N'bit',1,0,0,0,0,0),
            (N'unit',N'decimal',0,0,0,0,8,2), (N'qty',N'int',0,0,0,0,0,0),
            (N'calculated',N'decimal',0,0,1,0,10,2), (N'padded',N'nchar',0,0,0,8,0,0),
            (N'clock',N'time',0,0,0,0,0,7), (N'day',N'date',0,0,0,0,0,0), (N'edge',N'bigint',0,0,0,0,0,0)
        ) expected(name, type_name, nullable, identity_column, computed, max_length, precision_value, scale_value)
    ) THROW 51000, 'Fixture type manifest mismatch', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.computed_columns
                   WHERE object_id = OBJECT_ID(N'dbo.sqlserver_acceptance') AND name = N'calculated' AND is_persisted = 1)
        THROW 51000, 'Computed value must be persisted fixture arithmetic', 1;
    -- Format temporal fractions explicitly: default string styles can omit trailing zeros.
    -- CONCAT begins with nvarchar(max), so the 49 KiB Unicode record is not cut to 4000 characters.
    SELECT CONCAT(CONVERT(nvarchar(max), N'['),
        N'"', CONVERT(nvarchar(20), id), N'","', CONVERT(nvarchar(50), amount),
        N'","', CONVERT(nvarchar(50), wallet, 2), N'","', CONVERT(nvarchar(50), small, 2),
        N'","', CONVERT(nvarchar(19), occurred, 126), N'.',
            RIGHT(N'0000000' + CONVERT(nvarchar(7), DATEPART(NANOSECOND, occurred) / 100), 7),
        N'","', CONVERT(nvarchar(19), [offset], 126), N'.',
            RIGHT(N'0000000' + CONVERT(nvarchar(7), DATEPART(NANOSECOND, [offset]) / 100), 7),
            CASE WHEN DATEPART(TZOFFSET, [offset]) < 0 THEN N'-' ELSE N'+' END,
            RIGHT(N'00' + CONVERT(nvarchar(2), ABS(DATEPART(TZOFFSET, [offset])) / 60), 2), N':',
            RIGHT(N'00' + CONVERT(nvarchar(2), ABS(DATEPART(TZOFFSET, [offset])) % 60), 2),
        N'","', LOWER(CONVERT(nvarchar(36), guid)), N'",',
            CASE WHEN [note.名] IS NULL THEN N'null' ELSE N'"' + STRING_ESCAPE([note.名], 'json') + N'"' END, N',',
            CASE WHEN payload IS NULL THEN N'null' ELSE N'"' + CONVERT(nvarchar(max), payload, 1) + N'"' END, N',',
            CASE WHEN active IS NULL THEN N'null' WHEN active = 1 THEN N'true' ELSE N'false' END,
        N',"', CONVERT(nvarchar(50), calculated), N'","', STRING_ESCAPE(padded, 'json'),
        N'","', CONVERT(nvarchar(8), clock, 108), N'.',
            RIGHT(N'0000000' + CONVERT(nvarchar(7), DATEPART(NANOSECOND, clock) / 100), 7),
        N'","', CONVERT(nvarchar(10), [day], 23),
        N'","', CONVERT(nvarchar(50), unit), N'","', CONVERT(nvarchar(11), qty),
        N'","', CONVERT(nvarchar(20), edge), N'"]')
    FROM dbo.sqlserver_acceptance ORDER BY id;
    ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
