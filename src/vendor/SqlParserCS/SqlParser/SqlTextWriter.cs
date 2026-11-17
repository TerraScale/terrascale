using System.Runtime.CompilerServices;
using System.Text;
using SqlParser.Ast;

namespace SqlParser;

public class SqlTextWriter(StringBuilder sb) : StringWriter(sb)
{
    public void WriteCommaSpaced()
    {
        Write(Constants.SpacedComma);
    }
    /// <summary>
    /// Writes a given IEnumerable IWriteSql list with a specified
    /// delimiter between each item in the list
    /// </summary>
    /// <typeparam name="T">IWriteSql Type</typeparam>
    /// <param name="enumerable">IWriteSql enumerable list</param>
    /// <param name="delimiter">String delimiter between each object</param>
    public void WriteDelimited<T>(IEnumerable<T>? enumerable, string delimiter = ",") where T : IWriteSql
    {
        if (enumerable == null) { return; }

        var fragments = enumerable as T[] ?? enumerable.ToArray();
        for (var i = 0; i < fragments.Length; i++)
        {
            if (i > 0)
            {
                Write(delimiter);
            }

            fragments[i].ToSql(this);
        }
    }
    /// <summary>
    /// Writes a given IEnumerable IWriteSql list with a specified
    /// delimiter between each item in the list
    /// </summary>
    /// <typeparam name="T">IWriteSql Type</typeparam>
    /// <param name="enumerable">IWriteSql enumerable list</param>
    /// <param name="delimiter">Character delimiter between each object</param>
    public void WriteDelimited<T>(IEnumerable<T>? enumerable, char delimiter) where T : IWriteSql
    {
        WriteDelimited(enumerable, delimiter.ToString());
    }
    /// <summary>
    /// Writes a given IEnumerable IWriteSql list with no
    /// spaces between each item in the list
    /// </summary>
    /// <typeparam name="T">IWriteSql Type</typeparam>
    /// <param name="enumerable">IWriteSql enumerable list</param>
    public void WriteList<T>(IEnumerable<T>? enumerable) where T : IWriteSql
    {
        if (enumerable == null) { return; }
      
        foreach (var item in enumerable)
        {
            item.ToSql(this);
        }
    }
    /// <summary>
    /// Writes an identifier constraint
    /// <example>
    ///  <c>
    ///     'CONSTRAINT abc'
    ///  </c>
    /// </example>
    /// </summary>
    /// <param name="ident"></param>
    public void WriteConstraint(Ident? ident)
    {
        if (ident != null)
        {
            this.WriteSql($"CONSTRAINT {ident} ");
        }
    }
}

public static class SqlWritingExtensions
{
    public static void WriteSql(this SqlTextWriter writer, [InterpolatedStringHandlerArgument("writer")] ref SqlInterpolatedStringHandler handler)
    {
    }

    /// <summary>
    /// Writes a IWriteSql object instance and all child objects
    /// to a single SQL string
    /// </summary>
    /// <param name="sql"></param>
    /// <returns></returns>
    public static string ToSql(this IWriteSql sql)
    {
        var builder = StringBuilderPool.Get();

        using (var writer = new SqlTextWriter(builder))
        {
            sql.ToSql(writer);
        }

        return StringBuilderPool.Return(builder);
    }
    /// <summary>
    /// Write enumerable IWriteSql instances all child objects
    /// to a single SQL string
    /// </summary>
    /// <typeparam name="T">IWriteSql type</typeparam>
    /// <param name="list">List to write enumerated</param>
    /// <param name="delimiter">Delimiter between each IWriteSql object instance; default is comma separated.</param>
    /// <returns></returns>
    public static string ToSqlDelimited<T>(this IEnumerable<T>? list, string delimiter = Constants.SpacedComma) where T : IWriteSql
    {
        if (list == null)
        {
            return string.Empty;
        }

        var builder = StringBuilderPool.Get();

        using (var writer = new SqlTextWriter(builder))
        {
            writer.WriteDelimited(list, delimiter);
        }

        return StringBuilderPool.Return(builder);
    }

    public static string ToSqlDelimited<T>(this IEnumerable<T>? list, char delimiter) where T : IWriteSql
    {
        if (list == null)
        {
            return string.Empty;
        }

        var builder = StringBuilderPool.Get();

        using (var writer = new SqlTextWriter(builder))
        {
            writer.WriteDelimited(list, delimiter.ToString());
        }

        return StringBuilderPool.Return(builder);
    }
}