namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    // Worker-only: remove the old contribution before its state is replaced or deleted, in the same writer transaction.
    private void RemoveMerkleState(string collection, string id)
    {
        if (StoredDigest(collection, id) is var (h1, h2))
        {
            FoldMerkle(Bucket(collection, id), h1, h2);
        }
    }

    // Worker-only: the digest a document's stored state contributes to its bucket, validated, or null without a state row.
    private (long H1, long H2)? StoredDigest(string collection, string id)
    {
        var command = Command("""
            SELECT CASE WHEN typeof(bucket) = 'integer' AND bucket >= 0 AND bucket < 4096
                AND typeof(h1) = 'integer' AND typeof(h2) = 'integer' THEN 1 ELSE 0 END,
                CASE WHEN typeof(bucket) = 'integer' THEN bucket ELSE 0 END,
                CASE WHEN typeof(h1) = 'integer' THEN h1 ELSE 0 END,
                CASE WHEN typeof(h2) = 'integer' THEN h2 ELSE 0 END
            FROM _tb_state WHERE collection = $collection AND id = $id;
            """, collection);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        if (reader.GetInt64(0) != 1 || reader.GetInt64(1) != Bucket(collection, id))
        {
            throw StoredDataCorruption.Create("a stored digest has non-integer hashes or an invalid bucket");
        }

        return (reader.GetInt64(2), reader.GetInt64(3));
    }

    // A collection/prefix purge accumulates at most 4096 contributions, independent of the number of documents.
    private void RemoveMerkleStates(string collection, string? to = null)
    {
        var predicate = to is null ? "collection = $collection" : "collection >= $collection AND collection < $to";
        var command = Command($"""
            SELECT CASE WHEN typeof(bucket) = 'integer' AND bucket >= 0 AND bucket < 4096
                AND typeof(h1) = 'integer' AND typeof(h2) = 'integer' THEN 1 ELSE 0 END,
                CASE WHEN typeof(bucket) = 'integer' THEN bucket ELSE 0 END,
                CASE WHEN typeof(h1) = 'integer' THEN h1 ELSE 0 END,
                CASE WHEN typeof(h2) = 'integer' THEN h2 ELSE 0 END
            FROM _tb_state WHERE {predicate};
            """, collection);
        if (to is not null)
        {
            command.Parameters.AddWithValue("$to", to);
        }

        var hashes = new long[Buckets * 2];
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetInt64(0) != 1)
                {
                    throw StoredDataCorruption.Create("a stored digest has non-integer hashes or an invalid bucket");
                }

                var slot = (int)reader.GetInt64(1) * 2;
                hashes[slot] ^= reader.GetInt64(2);
                hashes[slot + 1] ^= reader.GetInt64(3);
            }
        }

        for (var bucket = 0; bucket < Buckets; bucket++)
        {
            if (hashes[bucket * 2] != 0 || hashes[bucket * 2 + 1] != 0)
            {
                FoldMerkle(bucket, hashes[bucket * 2], hashes[bucket * 2 + 1]);
            }
        }
    }

    private void FoldMerkle(int bucket, long h1, long h2)
    {
        var command = Command("""
            INSERT INTO _tb_merkle (bucket, h1, h2) VALUES ($bucket, $h1, $h2)
            ON CONFLICT (bucket) DO UPDATE SET
                h1 = (_tb_merkle.h1 | excluded.h1) & ~(_tb_merkle.h1 & excluded.h1),
                h2 = (_tb_merkle.h2 | excluded.h2) & ~(_tb_merkle.h2 & excluded.h2)
            WHERE typeof(_tb_merkle.h1) = 'integer' AND typeof(_tb_merkle.h2) = 'integer';
            """);
        command.Parameters.AddWithValue("$bucket", (long)bucket);
        command.Parameters.AddWithValue("$h1", h1);
        command.Parameters.AddWithValue("$h2", h2);
        if (command.ExecuteNonQuery() != 1)
        {
            throw StoredDataCorruption.Create("a stored Merkle digest has non-integer hashes");
        }
    }
}
