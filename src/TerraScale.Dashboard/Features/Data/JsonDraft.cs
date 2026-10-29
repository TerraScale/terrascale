using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TerraScale.Dashboard.Features.Data
{
    internal static class JsonDraft
    {
        // Format is for display only; both callers retain Razor HTML encoding.
        internal static string Format(JsonElement value, int maxBytes)
        {
            var pretty = JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true }).ReplaceLineEndings("\r\n");
            return Encoding.UTF8.GetByteCount(pretty) <= maxBytes ? pretty : Compact(value);
        }

        // Pasted valid JSON can contain literal C1 controls; keep error drafts safe for HTML5 too.
        internal static string EscapeC1(string draft)
        {
            var text = new StringBuilder(draft.Length);
            foreach (var character in draft)
            {
                if (character is >= '\u0080' and <= '\u009f')
                {
                    text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    text.Append(character);
                }
            }

            return text.ToString();
        }

        // Decoding string tokens avoids serializer blocklists and already-expanded GET wire text.
        internal static string Compact(JsonElement value, bool htmlSafe = true)
        {
            return value.ValueKind switch
            {
                JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().Select(property => Quote(property.Name, htmlSafe) + ":" + Compact(property.Value, htmlSafe))) + "}",
                JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(item => Compact(item, htmlSafe))) + "]",
                JsonValueKind.String => Quote(value.GetString()!, htmlSafe),
                _ => value.GetRawText()
            };
        }

        private static string Quote(string value, bool htmlSafe)
        {
            var text = new StringBuilder("\"");
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\b': text.Append("\\b"); break;
                    case '\f': text.Append("\\f"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        // C1 numeric HTML references can be remapped by the HTML5 parser.
                        if (character < ' ' || (htmlSafe && character is >= '\u0080' and <= '\u009f'))
                        {
                            text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            text.Append(character);
                        }

                        break;
                }
            }
            return text.Append('"').ToString();
        }
    }
}
