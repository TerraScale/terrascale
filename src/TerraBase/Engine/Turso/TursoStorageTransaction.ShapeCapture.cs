using System.Buffers.Text;
using System.Text;
using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    private sealed class ShapeCaptureTransaction
    {
        private readonly Dictionary<(string Table, string Id), string> _documents = [];

        public void Add(string table, byte[] image)
        {
            if (table != "_tb_state")
            {
                if (SqliteRecord.Decode(image) is [string id, string rowDocument])
                {
                    _documents[(table, id)] = rowDocument;
                }
                return;
            }

            if (SqliteRecord.Decode(image) is not [string collection, string key, string encoded, long, long, long live, long, long])
            {
                throw new InvalidDataException("a captured _tb_state row does not have the state table's columns");
            }
            var body = live == 0 ? null
                : _documents.TryGetValue((collection, key), out var stateDocument) ? stateDocument
                : throw new InvalidDataException("a captured live document state has no document row before it in its transaction");
            byte[] stored;
            try
            {
                stored = Base64Url.DecodeFromChars(encoded);
            }
            catch (FormatException error)
            {
                throw new InvalidDataException("a captured state is not base64url", error);
            }
            _ = DocumentStateEncoding.DecodeStored(stored, body is null ? [] : Encoding.UTF8.GetBytes(body));
        }
    }
}
