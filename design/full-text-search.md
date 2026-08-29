# Full-text search design

## Goal

Full-text search adds an Elasticsearch-like inverted index over values already stored in ordinary LSM tables. The source table remains authoritative. The search index is a hidden normal `LsmStore`, so postings use the existing WAL, memtable, bounded SSTables, Bloom filters, sparse indexes, and compaction implementation.

## Data flow

```text
source table row -> analyzer -> posting records -> search LsmStore -> SSTables
query -> analyzer -> posting range reads -> boolean/phrase filtering -> BM25 ranking -> source row fetch
```

A search index is created with `CREATE SEARCH INDEX name ON table (value.title, value.body)`. Each field is a JSON path. The index catalog is stored in `data/search-indexes/catalog.json`; index records are stored below `data/search-indexes/{name}/`.

## Posting model

One posting is stored per field, term, and document key. Its key is ordered as `field`, `term`, and an encoded document key. Its value contains term frequency, token positions, and the document field length. This keeps updates independent and lets SSTable range reads retrieve one term's postings without loading the whole index.

Updates write new postings and tombstone old postings. Deletes tombstone all postings generated from the old value. The source table is checked before a hit is returned, protecting results from stale postings after a crash or partial update.

## Analysis and queries

The standard analyzer lowercases Unicode text and emits contiguous Unicode letters and numbers. Unicode tokenization is intentional: document values may contain accented Latin text, Cyrillic, Greek, Arabic, CJK text, or user-entered international data. An ASCII-only tokenizer would silently discard or merge much of that content. Lowercasing makes Database and database equivalent while preserving the original source value for results. The analyzer deliberately has no stop-word removal or stemming in version one, so index creation and query analysis are deterministic and language-neutral. The tradeoff is that language-specific morphology and token boundaries are not yet optimized.

Search returns ranked results because a multi-document query needs a useful order rather than arbitrary key order. Ranking favors documents that contain more query terms, repeat a term, and use a term in a short field, while reducing the influence of terms that appear in many documents. This makes a search response useful when the caller only reads the first page.

Version one uses BM25-style scoring, a standard lexical-retrieval algorithm. For a term t in document d, the score is approximately:

idf(t) * (frequency * (k1 + 1))
       -----------------------------------------------
       frequency + k1 * (1 - b + b * fieldLength / averageFieldLength)

idf (inverse document frequency) gives rarer terms more weight; term frequency rewards repeated matches but saturates; and the length-normalization factor prevents long documents from winning simply because they contain more words. k1 controls frequency saturation and b controls length normalization. The implementation currently uses k1 = 1.2, b = 0.75, and a fixed average-length approximation, so scores are useful for ordering but are not a compatibility promise with Elasticsearch.

The first query surface supports term queries, quoted phrases, AND and OR, field-qualified terms such as title:database, BM25-style scoring, and bounded pagination. SQL uses `SEARCH index MATCH 'query'`; REST uses `POST /search/{index}`.

## Consistency and recovery

Index maintenance is synchronous after the source write is durable. A source write can therefore report failure if index maintenance fails. Search remains defensive and validates candidate source rows. Index definitions can be rebuilt from a complete source-table scan with the `POST /search/{index}/rebuild` endpoint.

There is no separate search-index WAL: the internal LsmStore WAL is the recovery boundary. A rebuild is the repair mechanism for an index whose postings are missing or stale. Search indexes are removed when their source table is dropped.

## Non-goals

Fuzzy matching, wildcards, stemming, aggregations, autocomplete, distributed shard routing, and a cost-based planner are intentionally outside version one. The posting layout leaves room for these later features without changing the source-table format.

The same storage pattern is also available for exact-value secondary indexes. `CREATE INDEX name ON table (value.path) USING FASTWRITE` selects an LSM/SSTable-backed index; plain `CREATE INDEX` continues to select the existing B+ tree implementation.