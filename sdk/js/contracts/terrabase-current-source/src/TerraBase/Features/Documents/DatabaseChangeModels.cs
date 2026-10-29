using System.Text.Json;

namespace TerraBase.Features.Documents;

/// <summary>
/// The data of an SSE <c>change</c> event of <c>GET …/databases/{database}/changes</c>; the event's <c>id</c> is the cursor
/// after it.
/// </summary>
/// <param name="Collection">The document's collection.</param>
/// <param name="Id">The document's id.</param>
/// <param name="Op"><c>upsert</c> or <c>delete</c>.</param>
/// <param name="Version">The operation's stamp, a decimal integer.</param>
/// <param name="Origin">The node that made the operation, 16 hexadecimal digits.</param>
/// <param name="Document">The document's current JSON on the serving machine; null when it does not exist there now.</param>
public sealed record DatabaseChangeEvent(string Collection, string Id, string Op, string Version, string Origin, JsonElement? Document);

/// <summary>The data of an SSE <c>cursor</c> event: the stream's position, sent when it starts and every 15 s.</summary>
/// <param name="Cursor">The cursor; also the event's <c>id</c>.</param>
public sealed record DatabaseChangeCursor(string Cursor);
