using System.Text;
using TerraBase.Engine;

namespace TerraBase.Features.Import;

/// <summary>Display names, <c>exclude</c> entries, collection names and document ids (docs/terrabase/import.md section 6).</summary>
internal static class ImportNames
{
    /// <summary>The collection that holds the import's own record in its destination.</summary>
    public const string Collection = "terrascale-import";

    /// <summary>An identifier as the contract writes it: bare when it is <c>[a-z0-9_]</c> not starting with a digit, else double-quoted.</summary>
    public static string Quote(string identifier) =>
        identifier.Length > 0 && !char.IsAsciiDigit(identifier[0]) && identifier.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            ? identifier
            : "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary><c>schema.table</c>.</summary>
    public static string Table(string schema, string table) => Quote(schema) + "." + Quote(table);

    /// <summary>Parses a dotted, optionally quoted name into its identifiers, or null when malformed.</summary>
    public static List<string>? Parse(string text)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var part = new StringBuilder();
            if (text[i] == '"')
            {
                i++;
                while (true)
                {
                    if (i >= text.Length)
                    {
                        return null;
                    }

                    if (text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        part.Append('"');
                        i += 2;
                    }
                    else if (text[i] == '"')
                    {
                        i++;
                        break;
                    }
                    else
                    {
                        part.Append(text[i++]);
                    }
                }
            }
            else
            {
                while (i < text.Length && text[i] != '.')
                {
                    if (text[i] == '"')
                    {
                        return null;
                    }

                    part.Append(text[i++]);
                }
            }

            if (part.Length == 0)
            {
                return null;
            }

            parts.Add(part.ToString());
            if (i < text.Length)
            {
                if (text[i] != '.' || i == text.Length - 1)
                {
                    return null;
                }

                i++;
            }
        }

        return parts;
    }

    /// <summary>
    /// The collection for a table: <c>public</c>/<c>main</c> drop their schema, others give <c>schema_table</c>; lowercased,
    /// other characters to <c>_</c>, <c>t_</c> unless it starts with a letter, at most 63 characters.
    /// </summary>
    public static string CollectionFor(string schema, string table)
    {
        var raw = schema is "public" or "main" ? table : schema + "_" + table;
        var name = new StringBuilder(raw.Length + 2);
        foreach (var c in raw.ToLowerInvariant())
        {
            name.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? c : '_');
        }

        if (name.Length == 0 || name[0] is not (>= 'a' and <= 'z'))
        {
            name.Insert(0, "t_");
        }

        return name.Length > 63 ? name.ToString(0, 63) : name.ToString();
    }

    /// <summary>Whether a collection name is reserved by the engine or by the import itself.</summary>
    public static bool Reserved(string collection) => CollectionName.IsReserved(collection) || collection == Collection;

    /// <summary>The Documents API rule for a caller-supplied key.</summary>
    public static bool ValidId(string id) => id.Length is > 0 and <= 256 && !id.Any(char.IsControl);

    /// <summary>A composite key component: <c>%</c> and <c>:</c> escaped, so joining with <c>:</c> stays unambiguous.</summary>
    public static string Component(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal).Replace(":", "%3A", StringComparison.Ordinal);
}
