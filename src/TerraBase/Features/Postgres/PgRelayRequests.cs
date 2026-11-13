using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Sql;
using TerraBase.Tenancy;

namespace TerraBase.Features.Postgres;

internal sealed class PgRelayRequests(IMemoryBudget memory, TenantContext tenant) : IDisposable
{
    [Flags]
    private enum Effect { Read = 0, Write = 1, Begin = 2, End = 4, Commit = 8, Unknown = Write | Begin | End | Commit }

    private sealed class Entry(IMemoryReservation reservation)
    {
        public IMemoryReservation Reservation { get; } = reservation;
        public Effect Effect { get; set; }
        public long Version { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _statements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _portals = new(StringComparer.Ordinal);
    private int _transactions = 1;

    public (bool Commits, Action<bool>? Completed) Sent(char kind, byte[] body, (bool Transaction, bool Pending) state)
    {
        lock (_gate)
        {
            if (!state.Pending)
            {
                _transactions = state.Transaction ? 2 : 1;
            }

            var message = new PgReader(body);
            switch (kind)
            {
                case 'Q':
                    if (PostgresStatements.Parse(message.CString()) is not PgStatement[] statements)
                    {
                        return (false, null);
                    }

                    var commits = false;
                    foreach (var statement in statements)
                    {
                        commits |= Execute(Classify(statement));
                    }

                    return (commits, null);
                case 'P':
                    var name = message.CString();
                    var parsed = PostgresStatements.Parse(message.CString());
                    if (parsed is not PgStatement[] { Length: <= 1 } prepared)
                    {
                        return (false, null);
                    }

                    return (false, Store(_statements, name, prepared is [var single] ? Classify(single) : Effect.Read));
                case 'B':
                    var portal = message.CString();
                    var source = message.CString();
                    var effect = _statements.TryGetValue(source, out var bound) ? bound.Effect : Effect.Unknown;
                    return (false, Store(_portals, portal, effect));
                case 'E':
                    return (Execute(_portals.TryGetValue(message.CString(), out var executing) ? executing.Effect : Effect.Unknown), null);
                case 'C':
                    var entries = message.Byte() == 'S' ? _statements : _portals;
                    var closing = message.CString();
                    if (!entries.TryGetValue(closing, out var entry))
                    {
                        return (false, null);
                    }

                    var version = entry.Version;
                    return (false, success =>
                    {
                        lock (_gate)
                        {
                            if (success && entries.TryGetValue(closing, out var current) && current == entry && current.Version == version)
                            {
                                entries.Remove(closing);
                                entry.Reservation.Dispose();
                            }
                        }
                    }
                    );
                default:
                    return (false, null);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _statements.Values.Concat(_portals.Values))
            {
                entry.Reservation.Dispose();
            }

            _statements.Clear();
            _portals.Clear();
        }
    }

    private Action<bool> Store(Dictionary<string, Entry> entries, string name, Effect effect)
    {
        var existed = entries.TryGetValue(name, out var entry);
        if (entry is null)
        {
            if (entries.Count >= 256)
            {
                throw new InvalidDataException("too many relay statements or portals");
            }

            entry = new Entry(memory.TryReserve(128 + 2L * name.Length, tenant) ?? throw new QosMemoryException());
            entries.Add(name, entry);
        }

        var previous = entry.Effect;
        entry.Effect |= effect;
        var version = ++entry.Version;
        return success =>
        {
            lock (_gate)
            {
                if (entries.TryGetValue(name, out var current) && current == entry && current.Version == version)
                {
                    if (!success && !existed)
                    {
                        entries.Remove(name);
                        entry.Reservation.Dispose();
                    }
                    else
                    {
                        entry.Effect = success ? effect : previous;
                    }
                }
            }
        };
    }

    private bool Execute(Effect effect)
    {
        var commits = (effect & Effect.Commit) != 0 || (_transactions & 1) != 0 && (effect & Effect.Write) != 0;
        if ((effect & Effect.Begin) != 0)
        {
            _transactions |= 2;
        }

        if ((effect & Effect.End) != 0)
        {
            _transactions |= 1;
        }

        return commits;
    }

    private static Effect Classify(PgStatement statement) => statement.Kind switch
    {
        PgKind.Begin => Effect.Begin,
        PgKind.Commit => Effect.Commit | Effect.End,
        PgKind.Rollback => Effect.End,
        PgKind.Sql => SqlScript.Classify(statement.Sql) is SqlStatementPlan { Kind: SqlStatementKind.Write } ? Effect.Write : Effect.Read,
        _ => Effect.Read,
    };
}
