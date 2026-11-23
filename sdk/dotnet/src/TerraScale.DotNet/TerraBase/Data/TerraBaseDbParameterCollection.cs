using System.Collections;
using System.Data.Common;
using System.Globalization;

namespace TerraScale.DotNet.TerraBase;

public sealed class TerraBaseDbParameterCollection : DbParameterCollection
{
    private readonly List<TerraBaseDbParameter> parameters = [];

    public override int Count => parameters.Count;

    public override object SyncRoot { get; } = new();

    public TerraBaseDbParameter Add(TerraBaseDbParameter value)
    {
        parameters.Add(value);
        return value;
    }

    public override int Add(object value)
    {
        parameters.Add(AsParameter(value));
        return parameters.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values) Add(value);
    }

    public override void Clear() => parameters.Clear();

    public override bool Contains(object value) => value is TerraBaseDbParameter parameter && parameters.Contains(parameter);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index)
    {
        for (var i = 0; i < parameters.Count; i++) array.SetValue(parameters[i], index + i);
    }

    public override IEnumerator GetEnumerator() => parameters.GetEnumerator();

    protected override DbParameter GetParameter(int index) => parameters[index];

    protected override DbParameter GetParameter(string parameterName) =>
        parameters[IndexOfChecked(parameterName)];

    public override int IndexOf(object value) => value is TerraBaseDbParameter parameter ? parameters.IndexOf(parameter) : -1;

    public override int IndexOf(string parameterName)
    {
        var name = TerraBaseDbParameters.TrimPrefix(parameterName);
        for (var i = 0; i < parameters.Count; i++)
        {
            if (string.Equals(parameters[i].ParameterName, name, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    public override void Insert(int index, object value) => parameters.Insert(index, AsParameter(value));

    public override void Remove(object value)
    {
        var index = IndexOf(value);
        if (index >= 0) parameters.RemoveAt(index);
    }

    public override void RemoveAt(int index) => parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName) => parameters.RemoveAt(IndexOfChecked(parameterName));

    protected override void SetParameter(int index, DbParameter value) => parameters[index] = AsParameter(value);

    protected override void SetParameter(string parameterName, DbParameter value) =>
        parameters[IndexOfChecked(parameterName)] = AsParameter(value);

    private int IndexOfChecked(string parameterName)
    {
        var index = IndexOf(parameterName);
        return index >= 0 ? index : throw new ArgumentException($"There is no parameter '{parameterName}'.", nameof(parameterName));
    }

    private static TerraBaseDbParameter AsParameter(object value) =>
        value as TerraBaseDbParameter ?? throw new ArgumentException("The value is not a TerraBaseDbParameter.", nameof(value));

    internal (IReadOnlyList<TerraBaseSqlValue>? Args, IReadOnlyDictionary<string, TerraBaseSqlValue>? Named) ToArguments()
    {
        if (parameters.Count == 0) return (null, null);
        List<TerraBaseSqlValue>? positional = null;
        Dictionary<string, TerraBaseSqlValue>? named = null;
        foreach (var parameter in parameters)
        {
            if (parameter.ParameterName.Length == 0)
            {
                positional ??= [];
                positional.Add(parameter.ToSqlValue());
            }
            else
            {
                named ??= new(StringComparer.Ordinal);
                named.Add(parameter.ParameterName, parameter.ToSqlValue());
            }
        }

        if (positional is not null && named is not null)
        {
            throw new ArgumentException("Use either positional (?) or named (@name, :name, $name) parameters, not both.");
        }

        return positional is not null ? (positional, null) : (null, named);
    }
}

internal static class TerraBaseDbParameters
{
    public static string TrimPrefix(string name) => name.Length > 0 && name[0] is '@' or ':' or '$' ? name[1..] : name;

    public static TerraBaseSqlValue ToSqlValue(object? value) => value switch
    {
        null or DBNull => TerraBaseSqlValue.Null,
        bool flag => flag,
        byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong large => unchecked((long)large),
        char character => character.ToString(),
        float single => TerraBaseSqlValue.FromDouble(single),
        double real => TerraBaseSqlValue.FromDouble(real),
        decimal exact => exact.ToString("0.0###########################", CultureInfo.InvariantCulture),
        string text => text,
        byte[] blob => TerraBaseSqlValue.FromBytes(blob),
        DateTime moment => moment.ToString(@"yyyy\-MM\-dd HH\:mm\:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString(@"yyyy\-MM\-dd HH\:mm\:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString(@"yyyy\-MM\-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString(time.Ticks % TimeSpan.TicksPerSecond == 0 ? @"HH\:mm\:ss" : @"HH\:mm\:ss\.fffffff", CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        Guid id => id.ToString().ToUpperInvariant(),
        Enum member => Convert.ToInt64(member, CultureInfo.InvariantCulture),
        TerraBaseSqlValue sql => sql,
        _ => throw new NotSupportedException($"'{value.GetType()}' is not a supported SQL argument type."),
    };
}
