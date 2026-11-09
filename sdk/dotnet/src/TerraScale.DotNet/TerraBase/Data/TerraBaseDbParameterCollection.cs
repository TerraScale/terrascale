using System.Collections;
using System.Data.Common;
using System.Globalization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>The parameters of one <see cref="TerraBaseDbCommand"/>: unnamed ones are positional <c>?</c> arguments, named ones map to <c>@name</c>, <c>:name</c> or <c>$name</c>.</summary>
public sealed class TerraBaseDbParameterCollection : DbParameterCollection
{
    private readonly List<TerraBaseDbParameter> parameters = [];

    /// <summary>The number of parameters.</summary>
    public override int Count => parameters.Count;

    /// <summary>An object shared by nothing; the collection is not synchronized.</summary>
    public override object SyncRoot { get; } = new();

    /// <summary>Adds one parameter.</summary>
    public TerraBaseDbParameter Add(TerraBaseDbParameter value)
    {
        parameters.Add(value);
        return value;
    }

    /// <summary>Adds one parameter and returns its index.</summary>
    public override int Add(object value)
    {
        parameters.Add(AsParameter(value));
        return parameters.Count - 1;
    }

    /// <summary>Adds every parameter.</summary>
    public override void AddRange(Array values)
    {
        foreach (var value in values) Add(value);
    }

    /// <summary>Removes every parameter.</summary>
    public override void Clear() => parameters.Clear();

    /// <summary>Whether a parameter is present.</summary>
    public override bool Contains(object value) => value is TerraBaseDbParameter parameter && parameters.Contains(parameter);

    /// <summary>Whether a parameter with this name is present.</summary>
    public override bool Contains(string value) => IndexOf(value) >= 0;

    /// <summary>Copies the parameters into an array.</summary>
    public override void CopyTo(Array array, int index)
    {
        for (var i = 0; i < parameters.Count; i++) array.SetValue(parameters[i], index + i);
    }

    /// <summary>Enumerates the parameters in add order.</summary>
    public override IEnumerator GetEnumerator() => parameters.GetEnumerator();

    /// <summary>The parameter at <paramref name="index"/>.</summary>
    protected override DbParameter GetParameter(int index) => parameters[index];

    /// <summary>The first parameter with this name, with or without its prefix.</summary>
    protected override DbParameter GetParameter(string parameterName) =>
        parameters[IndexOfChecked(parameterName)];

    /// <summary>The index of a parameter, or -1.</summary>
    public override int IndexOf(object value) => value is TerraBaseDbParameter parameter ? parameters.IndexOf(parameter) : -1;

    /// <summary>The index of the first parameter with this name, with or without its prefix, or -1.</summary>
    public override int IndexOf(string parameterName)
    {
        var name = TerraBaseDbParameters.TrimPrefix(parameterName);
        for (var i = 0; i < parameters.Count; i++)
        {
            if (string.Equals(parameters[i].ParameterName, name, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    /// <summary>Inserts a parameter at <paramref name="index"/>.</summary>
    public override void Insert(int index, object value) => parameters.Insert(index, AsParameter(value));

    /// <summary>Removes a parameter.</summary>
    public override void Remove(object value)
    {
        var index = IndexOf(value);
        if (index >= 0) parameters.RemoveAt(index);
    }

    /// <summary>Removes the parameter at <paramref name="index"/>.</summary>
    public override void RemoveAt(int index) => parameters.RemoveAt(index);

    /// <summary>Removes the first parameter with this name, with or without its prefix.</summary>
    public override void RemoveAt(string parameterName) => parameters.RemoveAt(IndexOfChecked(parameterName));

    /// <summary>Replaces the parameter at <paramref name="index"/>.</summary>
    protected override void SetParameter(int index, DbParameter value) => parameters[index] = AsParameter(value);

    /// <summary>Replaces the first parameter with this name, with or without its prefix.</summary>
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
        null => TerraBaseSqlValue.Null,
        bool flag => flag,
        byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(value),
        float or double or decimal => TerraBaseSqlValue.FromDouble(Convert.ToDouble(value)),
        string text => text,
        byte[] blob => TerraBaseSqlValue.FromBytes(blob),
        DateTime moment => TerraBaseSqlValue.FromString(moment.ToString("O", CultureInfo.InvariantCulture)),
        TerraBaseSqlValue sql => sql,
        _ => throw new NotSupportedException($"'{value.GetType()}' is not a supported SQL argument type."),
    };
}
