using System.Collections.Frozen;
using System.Globalization;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;
using TerraScale.Contracts.Consistency;

namespace TerraBase.Postgres.Sessions;

internal sealed record PgSettingChange(string Name, string Value, bool Reported);

internal readonly union PgSettingResult(PgSettingChange, PgError);

internal sealed class PgSettings
{
    public const string Bookmark = "terrascale.bookmark";
    public const string Consistency = "terrascale.consistency";

    private static readonly (string Name, string Value, bool Reported, bool ReadOnly)[] Defaults =
    [
        ("server_version", PgRewriteContext.ServerVersion, true, true),
        ("server_version_num", PgRewriteContext.ServerVersionNumber, false, true),
        ("server_encoding", "UTF8", true, true),
        ("client_encoding", "UTF8", true, false),
        ("DateStyle", "ISO, MDY", true, false),
        ("IntervalStyle", "postgres", true, false),
        ("TimeZone", "UTC", true, false),
        ("integer_datetimes", "on", true, true),
        ("standard_conforming_strings", "on", true, true),
        ("is_superuser", "off", true, true),
        ("in_hot_standby", "off", true, true),
        ("application_name", string.Empty, true, false),
        ("session_authorization", string.Empty, true, true),
        ("default_transaction_read_only", "off", true, false),
        ("default_transaction_isolation", "read committed", false, false),
        ("transaction_isolation", "read committed", false, true),
        ("transaction_read_only", "off", false, true),
        ("search_path", "\"$user\", public", false, false),
        ("extra_float_digits", "1", false, false),
        ("statement_timeout", "0", false, false),
        ("lock_timeout", "0", false, false),
        ("idle_in_transaction_session_timeout", "0", false, false),
        ("client_min_messages", "notice", false, false),
        ("bytea_output", "hex", false, false),
        ("max_identifier_length", "63", false, true),
        ("max_connections", "4096", false, true),
        ("block_size", "4096", false, true),
        ("lc_collate", "C", false, true),
        ("lc_ctype", "C", false, true),
        ("lc_messages", "C", false, false),
        ("lc_monetary", "C", false, false),
        ("lc_numeric", "C", false, false),
        ("lc_time", "C", false, false),
        ("default_text_search_config", "pg_catalog.simple", false, false),
        ("synchronous_commit", "on", false, false),
        ("row_security", "on", false, false),
        ("jit", "off", false, false),
        (Consistency, "fast", false, false),
        (Bookmark, string.Empty, true, false),
    ];

    private static readonly FrozenSet<string> ReportedNames = Defaults.Where(static entry => entry.Reported).Select(static entry => entry.Name).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ReadOnlyNames = Defaults.Where(static entry => entry.ReadOnly).Select(static entry => entry.Name).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> DefaultValues = Defaults.ToFrozenDictionary(static entry => entry.Name, static entry => entry.Value, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> CanonicalNames = Defaults.ToFrozenDictionary(static entry => entry.Name, static entry => entry.Name, StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sessionValues = new(StringComparer.OrdinalIgnoreCase);
    private ConsistencyMode _defaultMode;
    private SqlAccess _defaultAccess;
    private TimeSpan _statementTimeout;

    public PgSettings(string user, string applicationName)
    {
        foreach (var (name, value, _, _) in Defaults)
        {
            _values[name] = value;
        }

        _values["application_name"] = applicationName;
        _values["session_authorization"] = user;
        RefreshDerived();
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    public IEnumerable<KeyValuePair<string, string>> Reported => _values.Where(static pair => ReportedNames.Contains(pair.Key) && pair.Key != Bookmark);

    public ConsistencyMode DefaultMode => _defaultMode;

    public SqlAccess DefaultAccess => _defaultAccess;

    public TimeSpan StatementTimeout => _statementTimeout;

    public bool TryGet(string name, out string value)
    {
        if (name.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            value = string.Empty;
            return false;
        }

        if (_values.TryGetValue(name, out var found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static string CanonicalName(string name) => CanonicalNames.TryGetValue(name, out var canonical) ? canonical : name;

    public PgSettingResult Set(string name, string? value, bool local)
    {
        var canonical = CanonicalName(name);
        if (ReadOnlyNames.Contains(canonical))
        {
            return new PgError("55P02", $"parameter \"{canonical}\" cannot be changed");
        }

        var defaultValue = DefaultValues.TryGetValue(canonical, out var fallback) ? fallback : string.Empty;
        var applied = value ?? (_sessionValues.TryGetValue(canonical, out var session) && local ? session : defaultValue);
        if (Validate(canonical, applied) is { } error)
        {
            return error;
        }

        applied = Normalize(canonical, applied);
        if (local && !_sessionValues.ContainsKey(canonical))
        {
            _sessionValues[canonical] = _values.TryGetValue(canonical, out var previous) ? previous : defaultValue;
        }
        else if (!local)
        {
            _sessionValues.Remove(canonical);
        }

        _values[canonical] = applied;
        RefreshDerived();
        return new PgSettingChange(canonical, applied, ReportedNames.Contains(canonical));
    }

    public IReadOnlyList<PgSettingChange> EndTransaction()
    {
        if (_sessionValues.Count == 0)
        {
            return [];
        }

        List<PgSettingChange> changes = [];
        foreach (var (name, value) in _sessionValues)
        {
            _values[name] = value;
            changes.Add(new PgSettingChange(name, value, ReportedNames.Contains(name)));
        }

        _sessionValues.Clear();
        RefreshDerived();
        return changes;
    }

    public IReadOnlyList<PgSettingChange> ResetAll()
    {
        List<PgSettingChange> changes = [];
        foreach (var (name, value, reported, readOnly) in Defaults)
        {
            if (readOnly || name is "application_name" or Bookmark || _values[name] == value)
            {
                continue;
            }

            _values[name] = value;
            changes.Add(new PgSettingChange(name, value, reported));
        }

        _sessionValues.Clear();
        RefreshDerived();
        return changes;
    }

    public void SetInternal(string name, string value) => _values[name] = value;

    private void RefreshDerived()
    {
        _defaultMode = _values["default_transaction_isolation"].Equals("serializable", StringComparison.OrdinalIgnoreCase)
            || _values[Consistency].Equals("strong", StringComparison.OrdinalIgnoreCase)
                ? ConsistencyMode.Strong
                : ConsistencyMode.Fast;
        _defaultAccess = _values["default_transaction_read_only"] is "on" or "true" ? SqlAccess.Read : SqlAccess.Write;
        _statementTimeout = ParseDuration(_values["statement_timeout"]) is { } timeout && timeout > TimeSpan.Zero && timeout < PostgresLimits.StatementTimeout
            ? timeout
            : PostgresLimits.StatementTimeout;
    }

    private static PgError? Validate(string name, string value) => name switch
    {
        "client_encoding" when !value.Replace("-", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal).Equals("UTF8", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("UNICODE", StringComparison.OrdinalIgnoreCase) => new PgError("22023", "only the UTF8 client encoding is supported"),
        "TimeZone" when !IsUtc(value) => new PgError("22023", "only UTC and its aliases are supported time zones"),
        "DateStyle" when !value.Contains("ISO", StringComparison.OrdinalIgnoreCase) => new PgError("22023", "DateStyle must be ISO"),
        "default_transaction_isolation" when value.ToLowerInvariant() is not ("serializable" or "repeatable read" or "read committed" or "read uncommitted") =>
            new PgError("22023", $"invalid value for parameter \"default_transaction_isolation\": \"{value}\""),
        Consistency when value.ToLowerInvariant() is not ("fast" or "strong") => new PgError("22023", "terrascale.consistency is fast or strong"),
        Bookmark when !CausalBookmark.TryDecode(value, out _) => new PgError("22023", "invalid terrascale.bookmark value"),
        "statement_timeout" or "lock_timeout" or "idle_in_transaction_session_timeout" when ParseDuration(value) is null =>
            new PgError("22023", $"invalid value for parameter \"{name}\": \"{value}\""),
        _ => null,
    };

    private static string Normalize(string name, string value) => name switch
    {
        "client_encoding" => "UTF8",
        "TimeZone" => "UTC",
        "DateStyle" => value.Contains("DMY", StringComparison.OrdinalIgnoreCase) ? "ISO, DMY" : value.Contains("YMD", StringComparison.OrdinalIgnoreCase) ? "ISO, YMD" : "ISO, MDY",
        "default_transaction_isolation" or Consistency => value.ToLowerInvariant(),
        _ => value,
    };

    private static bool IsUtc(string value) => value.Trim().Trim('\'').ToLowerInvariant() is
        "utc" or "etc/utc" or "etc/uct" or "uct" or "gmt" or "etc/gmt" or "z" or "zulu" or "universal" or "etc/universal" or "greenwich" or "+00" or "+00:00" or "0";

    private static TimeSpan? ParseDuration(string value)
    {
        var text = value.Trim().Trim('\'').ToLowerInvariant();
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        if (digits == 0 || !long.TryParse(text.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        long? unit = text[digits..].Trim() switch
        {
            "" or "ms" => TimeSpan.TicksPerMillisecond,
            "s" or "sec" or "seconds" => TimeSpan.TicksPerSecond,
            "min" or "minutes" => TimeSpan.TicksPerMinute,
            "h" or "hours" => TimeSpan.TicksPerHour,
            "us" => TimeSpan.TicksPerMicrosecond,
            _ => null,
        };
        return unit is { } ticks && amount <= TimeSpan.MaxValue.Ticks / ticks ? TimeSpan.FromTicks(amount * ticks) : null;
    }
}
