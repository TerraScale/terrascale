using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using TerraBase.Testing;

namespace TerraBase.Fast.Postgres;

public sealed class PostgresTemporalTests
{
    [Test]
    public async Task Native_temporal_parameters_and_binary_results_preserve_microseconds_and_nulls()
    {
        await using var api = await ManagementApi.StartAsync();
        await using var connection = await ConnectAsync(api);
        await using var create = new NpgsqlCommand("CREATE TABLE moments (id BIGINT, d DATE, t TIMESTAMP, z TIMESTAMPTZ)", connection);
        await create.ExecuteNonQueryAsync();
        await using var insert = new NpgsqlCommand("INSERT INTO moments VALUES ($1, $2, $3, $4)", connection);
        foreach (var type in new[] { NpgsqlDbType.Integer, NpgsqlDbType.Date, NpgsqlDbType.Timestamp, NpgsqlDbType.TimestampTz })
        {
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type });
        }

        await insert.PrepareAsync();
        DateTime[] clocks = [new(1, 1, 2), new DateTime(1999, 12, 31, 23, 59, 59).AddTicks(9999990),
            new(2000, 1, 1), new DateTime(2024, 2, 29, 12, 34, 56).AddTicks(1234560), new(9999, 12, 30)];
        for (var i = 0; i <= clocks.Length; i++)
        {
            insert.Parameters[0].Value = i;
            insert.Parameters[1].Value = i < clocks.Length ? DateOnly.FromDateTime(clocks[i]) : DBNull.Value;
            insert.Parameters[2].Value = i < clocks.Length ? clocks[i] : DBNull.Value;
            insert.Parameters[3].Value = i < clocks.Length ? DateTime.SpecifyKind(clocks[i], DateTimeKind.Utc) : DBNull.Value;
            await insert.ExecuteNonQueryAsync();
        }

        await using var query = new NpgsqlCommand("SELECT id, d, t, z FROM moments ORDER BY id", connection);
        await using var reader = await query.ExecuteReaderAsync();
        var row = 0;
        foreach (var clock in clocks)
        {
            await Assert.That(await reader.ReadAsync()).IsTrue();
            await Assert.That(reader.GetInt64(0)).IsEqualTo((long)row++);
            await Assert.That(reader.GetFieldValue<DateOnly>(1)).IsEqualTo(DateOnly.FromDateTime(clock));
            await Assert.That(reader.GetDateTime(2).Ticks).IsEqualTo(clock.Ticks);
            await Assert.That(reader.GetDateTime(2).Kind).IsEqualTo(DateTimeKind.Unspecified);
            await Assert.That(reader.GetDateTime(3).Ticks).IsEqualTo(clock.Ticks);
            await Assert.That(reader.GetDateTime(3).Kind).IsEqualTo(DateTimeKind.Utc);
        }

        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(reader.IsDBNull(1) && reader.IsDBNull(2) && reader.IsDBNull(3)).IsTrue();
        await Assert.That(await reader.ReadAsync()).IsFalse();
    }

    [Test]
    [Arguments("0001-01-01", "0001-01-01 00:00:00.000000", "0001-01-01 01:00:00+01", "0001-01-01 00:00:00.000000+00")]
    [Arguments("9999-12-31", "9999-12-31 23:59:59.999999", "9999-12-31T22:59:59.999999-01:00", "9999-12-31 23:59:59.999999+00")]
    [Arguments("2000-02-29", "1999-12-31 23:59:59.999999", "2000-01-01 02:59:59.999999+03:00", "1999-12-31 23:59:59.999999+00")]
    [Arguments("0001-01-01", "0001-01-01 00:00:00.000000+01:00", "2000-01-01 02:00:00+02", "2000-01-01 00:00:00.000000+00")]
    public async Task Text_results_validate_calendar_dates_and_normalize_offsets(string date, string timestamp, string zoned, string utc)
    {
        await using var api = await ManagementApi.StartAsync();
        await using var connection = await ConnectAsync(api);
        await using var create = new NpgsqlCommand("CREATE TABLE moments (d DATE, t TIMESTAMP, z TIMESTAMPTZ)", connection);
        await create.ExecuteNonQueryAsync();
        await using var insert = new NpgsqlCommand("INSERT INTO moments VALUES ($1, $2, $3)", connection);
        foreach (var value in new[] { date, timestamp, zoned })
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await insert.ExecuteNonQueryAsync();
        await using var query = new NpgsqlCommand("SELECT d, t, z FROM moments", connection) { AllResultTypesAreUnknown = true };
        await using var reader = await query.ExecuteReaderAsync();
        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo(date);
        await Assert.That(reader.GetString(1)).IsEqualTo(timestamp[..26]);
        await Assert.That(reader.GetString(2)).IsEqualTo(utc);
        await reader.DisposeAsync();
        query.AllResultTypesAreUnknown = false;
        await using var binary = await query.ExecuteReaderAsync();
        await Assert.That(await binary.ReadAsync()).IsTrue();
        await Assert.That(binary.GetFieldValue<DateOnly>(0).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(date);
        await Assert.That(binary.GetDateTime(1).ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(timestamp[..26]);
        await Assert.That(binary.GetDateTime(2).ToString("yyyy-MM-dd HH:mm:ss.ffffff'+00'", System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(utc);
    }

    [Test]
    public async Task Typed_text_and_invalid_binary_binds_validate_values_and_recover()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var key = await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, "documents:read", "documents:write");
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", api.Server.PostgresPort);
        await using var stream = tcp.GetStream();
        await SendAsync(null, [0, 3, 0, 0, .. CString("user"), .. CString("wire"), .. CString("database"), .. CString(database.Name), 0]);
        await ReadAsync();
        await SendAsync('p', CString(key));
        await DrainAsync();
        foreach (var oid in new[] { 1082, 1114, 1184 })
        {
            var valid = oid == 1082 ? "2000-02-29" : "2000-02-29 12:34:56.123456+03:00";
            var expected = oid == 1082 ? valid : oid == 1114 ? "2000-02-29 12:34:56.123456" : "2000-02-29 09:34:56.123456+00";
            var width = oid == 1082 ? 4 : 8;
            (int Format, byte[] Value, string? Expected)[] cases =
            [
                (0, Encoding.UTF8.GetBytes(valid), expected),
                (0, Encoding.UTF8.GetBytes(oid == 1082 ? "2023-02-29" : "2023-02-29 12:34:56"), null),
                (1, new byte[width - 1], null),
                (1, new byte[width + 1], null),
                (1, oid == 1082 ? Int(int.MaxValue - 1) : BitConverter.GetBytes(IPAddress.HostToNetworkOrder(long.MaxValue - 1)), null),
                (1, Enumerable.Repeat((byte)255, width).ToArray(), oid == 1082 ? "1999-12-31" : "1999-12-31 23:59:59.999999" + (oid == 1184 ? "+00" : "")),
            ];
            foreach (var (format, parameter, expectedText) in cases)
            {
                await SendAsync('P', [0, .. CString("SELECT $1"), 0, 1, .. Int(oid)]);
                await SendAsync('B', [0, 0, 0, 1, 0, (byte)format, 0, 1, .. Int(parameter.Length), .. parameter, 0, 0]);
                await SendAsync('E', [0, 0, 0, 0, 0]);
                await SendAsync('S', []);
                var messages = await DrainAsync();
                if (expectedText is { } text)
                {
                    await Assert.That(Encoding.UTF8.GetString(messages.Single(m => m.Type == 'D').Body[6..])).IsEqualTo(text);
                }
                else
                {
                    await Assert.That(Encoding.UTF8.GetString(messages.Single(m => m.Type == 'E').Body)).Contains("C22P02\0");
                }

                await SendAsync('Q', CString("SELECT 1"));
                await Assert.That(Encoding.UTF8.GetString((await DrainAsync()).Single(m => m.Type == 'D').Body[6..])).IsEqualTo("1");
            }
        }

        await SendAsync('Q', CString("CREATE TABLE wire_moments (n BIGINT, d DATE); INSERT INTO wire_moments VALUES (42, '2000-01-01')"));
        await DrainAsync();
        await SendAsync('P', [0, .. CString("SELECT n, d FROM wire_moments"), 0, 0]);
        // Portal/statement names, no parameters, two result formats: binary BIGINT and text DATE.
        await SendAsync('B', [0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 0]);
        await SendAsync('D', [(byte)'P', 0]);
        await SendAsync('E', [0, 0, 0, 0, 0]);
        await SendAsync('S', []);
        var mixed = (await DrainAsync()).Single(m => m.Type == 'D').Body;
        byte[] expectedMixed = [0, 2, .. Int(8), .. BitConverter.GetBytes(IPAddress.HostToNetworkOrder(42L)), .. Int(10), .. Encoding.UTF8.GetBytes("2000-01-01")];
        await Assert.That(mixed.SequenceEqual(expectedMixed)).IsTrue();

        static byte[] CString(string value)
        {
            return [.. Encoding.UTF8.GetBytes(value), 0];
        }
        static byte[] Int(int value)
        {
            return BitConverter.GetBytes(IPAddress.HostToNetworkOrder(value));
        }
        async Task SendAsync(char? type, byte[] body)
        {
            await stream.WriteAsync((byte[])[.. type is { } t ? new[] { (byte)t } : [], .. Int(body.Length + 4), .. body]);
        }
        async Task<(char Type, byte[] Body)> ReadAsync()
        {
            var header = new byte[5];
            await stream.ReadExactlyAsync(header);
            var body = new byte[IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 1)) - 4];
            await stream.ReadExactlyAsync(body);
            return ((char)header[0], body);
        }

        async Task<List<(char Type, byte[] Body)>> DrainAsync()
        {
            var messages = new List<(char Type, byte[] Body)>();
            do
            {
                messages.Add(await ReadAsync());
            }
            while (messages[^1].Type != 'Z');
            return messages;
        }
    }

    private static async Task<NpgsqlConnection> ConnectAsync(ManagementApi api)
    {
        var database = await api.CreateManagedDatabaseAsync();
        var key = await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, "documents:read", "documents:write");
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = api.Server.PostgresPort,
            Username = "wire",
            Password = key,
            Database = database.Name,
            SslMode = SslMode.Disable,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
