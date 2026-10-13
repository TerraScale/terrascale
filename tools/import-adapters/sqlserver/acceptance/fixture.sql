-- UNRUN. Only a later-approved, already-provisioned disposable SQL Server 2022 database.
-- sqlcmd variable FixtureDatabase must match ts_sqlserver_acceptance_<26 lowercase ULID chars>.
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
IF EXISTS (SELECT 1 FROM sys.objects WHERE is_ms_shipped = 0)
    THROW 51000, 'Fixture database must have no user objects', 1;
BEGIN TRANSACTION;
CREATE TABLE dbo.sqlserver_acceptance (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    amount decimal(38,8) NOT NULL,
    wallet money NOT NULL,
    small smallmoney NOT NULL,
    occurred datetime2(7) NOT NULL,
    [offset] datetimeoffset(7) NOT NULL,
    guid uniqueidentifier NOT NULL,
    [note.名] nvarchar(max) NULL,
    payload varbinary(max) NULL,
    active bit NULL,
    unit decimal(8,2) NOT NULL,
    qty int NOT NULL,
    calculated AS ISNULL(CONVERT(decimal(10,2), unit * qty), CONVERT(decimal(10,2), 0)) PERSISTED NOT NULL,
    padded nchar(4) NOT NULL,
    clock time(7) NOT NULL,
    [day] date NOT NULL,
    edge bigint NOT NULL
);
-- Insert separately so identity values 1..4 do not depend on a multi-row execution order.
INSERT INTO dbo.sqlserver_acceptance
    (amount, wallet, small, occurred, [offset], guid, [note.名], payload, active, unit, qty, padded, clock, [day], edge)
VALUES
    ('123456789012345678901234567890.12345678', '922337203685477.5807', '-214748.3648',
     '2024-02-29T23:59:59.1234567', '2024-02-29T23:59:59.7654321-03:00',
     '00112233-4455-6677-8899-AABBCCDDEEFF', N'Olá 😀 ' + NCHAR(9) + NCHAR(10) + NCHAR(13) + N' \N \.',
     0x00ff800102, 1, '12.34', 100, N'A   ', '12:34:56.1234567', '0001-01-01', 9223372036854775807);
INSERT INTO dbo.sqlserver_acceptance
    (amount, wallet, small, occurred, [offset], guid, [note.名], payload, active, unit, qty, padded, clock, [day], edge)
VALUES
    ('0.00000000', '-922337203685477.5808', '214748.3647',
     '9999-12-31T23:59:59.9999999', '2024-02-29T14:00:00.0000000+14:00',
     '11112233-4455-6677-8899-aabbccddeeff', N'', 0x, 0, '0.10', 3, N'B   ', '00:00:00.0000000', '9999-12-31', -9223372036854775808);
INSERT INTO dbo.sqlserver_acceptance
    (amount, wallet, small, occurred, [offset], guid, [note.名], payload, active, unit, qty, padded, clock, [day], edge)
VALUES
    ('-123456789012345678901234567890.12345678', '0.0000', '0.0000',
     '0001-01-01T00:00:00.0000000', '2024-02-29T00:00:00.0000000+00:00',
     '22112233-4455-6677-8899-aabbccddeeff', NULL, NULL, NULL, '1.50', -2, N'C   ', '23:59:59.9999999', '2024-02-29', 0);
INSERT INTO dbo.sqlserver_acceptance
    (amount, wallet, small, occurred, [offset], guid, [note.名], payload, active, unit, qty, padded, clock, [day], edge)
VALUES
    ('1.23000000', '1.0000', '1.0000',
     '2024-02-29T00:00:00.0000000', '2024-02-29T00:00:00.0000001-14:00',
     '33112233-4455-6677-8899-aabbccddeeff', REPLICATE(CONVERT(nvarchar(max), N'x'), 3998) + N'😀' + REPLICATE(CONVERT(nvarchar(max), N'x'), 45152),
     0x00, 1, '0.00', 0, N'D   ', '00:00:00.0000001', '2024-02-29', 1);
IF (SELECT COUNT_BIG(*) FROM dbo.sqlserver_acceptance) <> 4
    THROW 51000, 'Fixture count mismatch', 1;
IF (SELECT DATALENGTH([note.名]) FROM dbo.sqlserver_acceptance WHERE id = 4) <> 98304
    THROW 51000, 'Large Unicode fixture length mismatch', 1;
IF (SELECT HASHBYTES('SHA2_256', CONVERT(varbinary(max), [note.名])) FROM dbo.sqlserver_acceptance WHERE id = 1) <> 0x1a598f692797589f978722298389567c984e58b6428bbe39c3f9002e032dcceb
    THROW 51000, 'Unicode fixture content hash mismatch', 1;
IF (SELECT HASHBYTES('SHA2_256', CONVERT(varbinary(max), [note.名])) FROM dbo.sqlserver_acceptance WHERE id = 2) <> 0xe3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
    THROW 51000, 'Unicode fixture content hash mismatch', 1;
IF (SELECT HASHBYTES('SHA2_256', CONVERT(varbinary(max), [note.名])) FROM dbo.sqlserver_acceptance WHERE id = 4) <> 0x948888faefd05d16557508cd06d761fbff90c57f40f9ad531b0a55da4f06149b
    THROW 51000, 'Unicode fixture content hash mismatch', 1;
DECLARE @fixture_database nvarchar(128) = DB_NAME();
EXEC sys.sp_addextendedproperty @name=N'terrascale_acceptance_database', @value=@fixture_database,
    @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'sqlserver_acceptance';
COMMIT TRANSACTION;
