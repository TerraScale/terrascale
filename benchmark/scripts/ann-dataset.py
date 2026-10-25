"""Writes corpus.f32 (100000 x 768) and queries.f32 (1000 x 768) for cmd/ann-bench into DIR.

Source: Qdrant's dbpedia-entities OpenAI text-embedding-3-large datasets on Hugging Face. text-embedding-3 vectors are
Matryoshka embeddings, so the first 768 values, renormalized, are the model's 768-dimension embedding. Queries are
entities of the 1M set that are not in the 100K corpus. Run with pyarrow and numpy: python3 -I ann-dataset.py DIR
"""
import sys

import numpy as np
import pyarrow.parquet as pq

d = sys.argv[1]
corpus_files = [f'{d}/dbpedia-entities-openai3-text-embedding-3-large-1024-100K-train-0000{i}-of-00002.parquet' for i in (0, 1)]
query_file = f'{d}/dbpedia-entities-openai3-text-embedding-3-large-1536-1M-train-00025-of-00026.parquet'
column = 'text-embedding-3-large-1024-embedding'
tables = [pq.read_table(f, columns=['_id', column]) for f in corpus_files]
seen = set(sum((t.column('_id').to_pylist() for t in tables), []))
corpus = np.concatenate([np.array(t.column(column).to_pylist(), dtype=np.float32)[:, :768] for t in tables])
corpus /= np.linalg.norm(corpus, axis=1, keepdims=True)
q = pq.read_table(query_file, columns=['_id', 'text-embedding-3-large-1536-embedding'])
keep = [i for i, x in enumerate(q.column('_id').to_pylist()) if x not in seen][:1000]
queries = np.array([q.column(1)[i].as_py()[:768] for i in keep], dtype=np.float32)
queries /= np.linalg.norm(queries, axis=1, keepdims=True)
corpus.astype('<f4').tofile(f'{d}/corpus.f32')
queries.astype('<f4').tofile(f'{d}/queries.f32')
print(corpus.shape, queries.shape)
